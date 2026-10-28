using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlcApi.Auth;
using PlcApi.Services;

namespace PlcApi.Hosting;

// One-shot commands. Each runs, prints what it did, and exits WITHOUT starting the gateway — no PLC
// scan, no web server. Used by the installer and for support at a client site:
//
//   PLCGateway.exe --init-database     create the gateway's database role + database, then migrate
//                                      (installer; needs SHOTSENSE_PG_SUPERUSER_PASSWORD)
//   PLCGateway.exe --migrate-only      bring the schema up to date and exit
//   PLCGateway.exe --set-local-admin   set the emergency local admin's password: written, as a hash only,
//                                      into C:\ProgramData\ShotSense\appsettings.Production.json (run as
//                                      administrator); on a dev PC with no such file, printed instead
//
// The shared-login commands (--create-admin, --set-password) went with the shared login (2026-10-26):
// everyday logins are per-person email accounts, managed on the cloud's Users page.
//
// The password comes from SHOTSENSE_ADMIN_PASSWORD when the installer runs it, so it never appears on a
// command line or in a file; otherwise the command asks, hiding the password as it is typed.
//
// Exit codes: 0 done, 1 failed, 2 wrong usage.
public static class GatewayCli
{
    private static readonly string[] Commands = { "--init-database", "--migrate-only", "--set-local-admin" };

    /// <returns>The exit code when <paramref name="args"/> held a command; null to start the gateway.</returns>
    public static async Task<int?> TryRunAsync(string[] args, IConfiguration config)
    {
        string? command = args.FirstOrDefault(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase))?.ToLowerInvariant();
        if (command is null) return null;

        try
        {
            return command switch
            {
                "--init-database"   => await InitDatabaseAsync(config),
                "--migrate-only"    => await MigrateOnlyAsync(config),
                "--set-local-admin" => SetLocalAdmin(config),
                _                   => 2,
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

    // The emergency local admin's password lives only as a hash in the settings file, so nothing on the
    // PC can show it. On an installed gateway the hash is written straight into the install's settings
    // file; on a dev PC (no such file) it is printed for appsettings.Production.json. The password itself
    // is never shown or stored.
    private static int SetLocalAdmin(IConfiguration config)
    {
        string username = GatewayAuth.LocalAdminUsername(config);
        string? password = AskNewPassword(username);
        if (password is null) return 2;
        string hash = PasswordHasher.Hash(password);

        string file = GatewayPaths.SettingsFile;
        if (!File.Exists(file))
        {
            Console.WriteLine();
            Console.WriteLine($"No installed settings file ({file}). Put this in appsettings.Production.json, then restart:");
            Console.WriteLine();
            Console.WriteLine($$"""
              "Auth": {
                "LocalAdmin": {
                  "Username": "{{username}}",
                  "PasswordHash": "{{hash}}"
                }
              }
            """);
            return 0;
        }

        // Rewritten in place, so the file keeps its permissions (administrators and the service only).
        var settings = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
            ?? throw new InvalidOperationException($"{file} is not a JSON object.");
        if (settings["Auth"] is not JsonObject auth)
            settings["Auth"] = auth = new JsonObject();
        auth["LocalAdmin"] = new JsonObject { ["Username"] = username, ["PasswordHash"] = hash };
        File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep '+' in the hash readable
            }),
            new UTF8Encoding(false));

        Console.WriteLine($"Local admin '{username}' set in {file}.");
        Console.WriteLine("Restart the ShotSense Gateway service for it to take effect. It works from the plant network only:");
        Console.WriteLine("login page → \"Local admin login\".");
        return 0;
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
