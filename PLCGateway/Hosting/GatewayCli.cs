using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using PlcApi.Auth;
using PlcApi.Services;

namespace PlcApi.Hosting;

// One-shot commands. Each runs, prints what it did, and exits WITHOUT starting the gateway — no PLC
// scan, no web server. Used by the installer and for support at a client site:
//
//   PLCGateway.exe --init-database     create the gateway's database role + database, then migrate
//                                      (installer; needs SHOTSENSE_PG_SUPERUSER_PASSWORD)
//   PLCGateway.exe --migrate-only      bring the schema up to date and exit
//   PLCGateway.exe --create-admin      add the first dashboard login (admin role)
//   PLCGateway.exe --set-password      set a new password for an existing login
//   PLCGateway.exe --hash-password     print the hash for the emergency local admin's password
//                                      (Auth:LocalAdmin:PasswordHash); touches no database
//
// Usernames and passwords come from environment variables when the installer runs them
// (SHOTSENSE_ADMIN_USERNAME / SHOTSENSE_ADMIN_PASSWORD), so they never appear on a command line or
// in a file; otherwise the command asks, hiding the password as it is typed. `--username <name>`
// skips the username question.
//
// Exit codes: 0 done, 1 failed, 2 wrong usage, 3 --create-admin found the login already exists.
public static class GatewayCli
{
    private static readonly string[] Commands = { "--init-database", "--migrate-only", "--create-admin", "--set-password", "--hash-password" };

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._@-]{3,64}$");

    /// <returns>The exit code when <paramref name="args"/> held a command; null to start the gateway.</returns>
    public static async Task<int?> TryRunAsync(string[] args, IConfiguration config)
    {
        string? command = args.FirstOrDefault(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase))?.ToLowerInvariant();
        if (command is null) return null;

        try
        {
            return command switch
            {
                "--init-database" => await InitDatabaseAsync(config),
                "--migrate-only"  => await MigrateOnlyAsync(config),
                "--create-admin"  => await CreateAdminAsync(args, config),
                "--set-password"  => await SetPasswordAsync(args, config),
                "--hash-password" => HashPassword(config),
                _                 => 2,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> InitDatabaseAsync(IConfiguration config)
    {
        string? password = Environment.GetEnvironmentVariable("SHOTSENSE_PG_SUPERUSER_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Set SHOTSENSE_PG_SUPERUSER_PASSWORD to the PostgreSQL superuser's password first.");
            return 2;
        }
        string superuser = Environment.GetEnvironmentVariable("SHOTSENSE_PG_SUPERUSER") is { Length: > 0 } s ? s : "postgres";

        await DatabaseSetup.InitializeAsync(DatabaseSetup.ConnectionString(config), superuser, password, Console.WriteLine);
        return 0;
    }

    private static async Task<int> MigrateOnlyAsync(IConfiguration config)
    {
        await DatabaseSetup.MigrateAsync(DatabaseSetup.ConnectionString(config));
        Console.WriteLine("Database schema is up to date.");
        return 0;
    }

    private static async Task<int> CreateAdminAsync(string[] args, IConfiguration config)
    {
        var users = new UserService(config, NullLogger<UserService>.Instance);

        string username = AskUsername(args);
        if (username.Length == 0) return 2;
        string? password = AskNewPassword(username);
        if (password is null) return 2;

        bool created = await users.InsertUserAsync(username, $"{username}@plc.local", "Administrator",
            PasswordHasher.Hash(password), "admin", null);
        if (!created)
        {
            // Its own exit code so the installer can tell "already there" (a re-run after a failed
            // install) from a real failure, and set the password instead.
            Console.Error.WriteLine($"A login called '{username}' already exists. Nothing was changed. Use --set-password to change its password.");
            return 3;
        }

        Console.WriteLine($"Created dashboard login '{username}' (admin).");
        return 0;
    }

    private static async Task<int> SetPasswordAsync(string[] args, IConfiguration config)
    {
        var users = new UserService(config, NullLogger<UserService>.Instance);

        string username = AskUsername(args);
        if (username.Length == 0) return 2;
        string? password = AskNewPassword(username);
        if (password is null) return 2;

        if (!await users.UpdatePasswordHashAsync(username, PasswordHasher.Hash(password)))
        {
            Console.Error.WriteLine($"No login called '{username}' was found. Nothing was changed.");
            return 1;
        }

        Console.WriteLine($"Password changed for '{username}'. Use the new password at the next sign-in.");
        return 0;
    }

    // The emergency local admin's password lives only as a hash in the settings file, so nothing on
    // the PC can show it. This prints the hash and where it goes; the password itself is never shown
    // or stored.
    private static int HashPassword(IConfiguration config)
    {
        string username = GatewayAuth.LocalAdminUsername(config);
        string? password = AskNewPassword(username);
        if (password is null) return 2;

        string hash = PasswordHasher.Hash(password);
        Console.WriteLine();
        Console.WriteLine("Put this in appsettings.Production.json (on an installed gateway:");
        Console.WriteLine(@"C:\ProgramData\ShotSense\appsettings.Production.json), then restart the gateway:");
        Console.WriteLine();
        Console.WriteLine($$"""
          "Auth": {
            "LocalAdmin": {
              "Username": "{{username}}",
              "PasswordHash": "{{hash}}"
            }
          }
        """);
        Console.WriteLine();
        Console.WriteLine($"Sign in with '{username}' from the plant network: login page → \"Local admin login\".");
        return 0;
    }

    private static string AskUsername(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--username", StringComparison.OrdinalIgnoreCase));
        string? username = i >= 0 && i + 1 < args.Length ? args[i + 1]
            : Environment.GetEnvironmentVariable("SHOTSENSE_ADMIN_USERNAME");

        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Write("Username: ");
            username = Console.ReadLine();
        }

        username = username?.Trim() ?? "";
        if (!UsernamePattern.IsMatch(username))
        {
            Console.Error.WriteLine("A username is 3 to 64 characters: letters, digits and . _ @ - only. Nothing was changed.");
            return "";
        }
        return username;
    }

    /// <returns>The new password, or null after printing why it was refused.</returns>
    private static string? AskNewPassword(string username)
    {
        string? password = Environment.GetEnvironmentVariable("SHOTSENSE_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            password = ReadHidden($"New password for '{username}' (at least {PasswordHasher.MinLength} characters): ");
            string again = ReadHidden("Type it again: ");
            if (!string.Equals(password, again, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("The two passwords are different. Nothing was changed.");
                return null;
            }
        }

        if (password.Length < PasswordHasher.MinLength)
        {
            Console.Error.WriteLine($"Too short: use at least {PasswordHasher.MinLength} characters. Nothing was changed.");
            return null;
        }
        return password;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            string line = Console.ReadLine() ?? "";
            Console.WriteLine();
            return line;
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
