using Microsoft.Extensions.Configuration.Json;

namespace PlcApi.Hosting;

// Where an INSTALLED gateway keeps everything that is not program files.
//
//   C:\ProgramData\ShotSense\appsettings.Production.json   this install's settings and secrets
//   C:\ProgramData\ShotSense\logs\                          gateway-YYYYMMDD.log, install logs
//   C:\ProgramData\ShotSense\site\                          the encrypted copy of the client's tag file (SiteProfile)
//   C:\ProgramData\ShotSense\pgdata\                        the bundled PostgreSQL's data
//
// ProgramData, not the install folder, so an upgrade (which replaces program files) and an uninstall
// (which removes them) never touch settings, logs or the database.
//
// SHOTSENSE_DATA_DIR overrides the folder. It exists for testing an installer layout on a dev PC
// without writing to the real ProgramData; an installed gateway never sets it.
public static class GatewayPaths
{
    public const string ServiceName = "ShotSenseGateway";

    public static string DataDir =>
        Environment.GetEnvironmentVariable("SHOTSENSE_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShotSense");

    public static string SettingsFile => Path.Combine(DataDir, "appsettings.Production.json");
    public static string LogsDir      => Path.Combine(DataDir, "logs");
    public static string SiteDir      => Path.Combine(DataDir, "site");

    /// <summary>
    /// Layers the install's settings file over appsettings.json / appsettings.{env}.json, and UNDER
    /// environment variables and the command line, so a one-off `--urls` still wins. Optional: a dev
    /// PC has no such file and keeps using the project's own appsettings.Production.json.
    /// </summary>
    public static void AddInstallSettings(ConfigurationManager configuration)
    {
        var source = new JsonConfigurationSource { Path = SettingsFile, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();   // absolute path → provider rooted at the nearest existing folder

        int lastJson = -1;
        for (int i = 0; i < configuration.Sources.Count; i++)
            if (configuration.Sources[i] is JsonConfigurationSource) lastJson = i;

        configuration.Sources.Insert(lastJson + 1, source);
    }
}
