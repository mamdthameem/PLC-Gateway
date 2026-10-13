using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;

namespace PlcApi.Hosting;

// A client's private PLC settings — its tag list and recording rules — come from the cloud, not from the
// appsettings.json shipped in the installer (decided 2026-10-13: the tags differ per client, and the
// client must not be able to read them off the setup file or the program folder).
//
//   Office:  upload the client's tag file in the cloud (client dialog → "Tag settings file").
//   Gateway, at every start: GET SiteProfile:Url (the cloud's siteProfile function) with X-License-Key.
//     downloaded → used, and saved ENCRYPTED in C:\ProgramData\ShotSense\site\site-profile.bin
//     no answer  → the saved copy is used (the internet can be down; recording must not stop)
//     neither    → the gateway stops with a clear log line; as a service it tries again in a minute
//   Later:   the hourly licence check reports the cloud's current version (siteProfileVersion); a new
//            one is downloaded, saved, and the gateway restarts itself to apply it (LicenseCheckService).
//
// The profile is applied as one more settings layer holding ONLY the sections in Sections — anything else
// in it is dropped, so a profile can never change keys, the database or the licence address. It is never
// written anywhere in plain text, never served by any API and never logged (only its version and tag count).
// The shipped appsettings.json carries none of these sections (installer\build.ps1 strips them).
//
// The saved copy is AES-256-GCM encrypted with a key derived from License:Key. Honest limit: an
// administrator of that PC can read License:Key as well, and the gateway has to hold the tags in memory
// to read the PLC. So this keeps the tags from ordinary users and from copies of the file, not from
// someone with full control of the machine.
//
// No SiteProfile:Url (a development PC): the tags in appsettings.json are used, as before.
public static class SiteProfile
{
    /// <summary>The only settings a site profile may carry.</summary>
    public static readonly string[] Sections = { "Tags", "ScanIntervalMs", "CovRules", "DataCollection", "MaintenanceThresholds" };

    public static string CacheFile => Path.Combine(GatewayPaths.SiteDir, "site-profile.bin");

    private static readonly byte[] Magic = "SSP1"u8.ToArray();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record Profile(string Version, byte[] Json, int TagCount, DateTime SavedUtc);

    /// <summary>
    /// At startup, before any service reads its settings: download the profile (or fall back to the saved
    /// copy) and layer it into <paramref name="config"/>. Null means there is nothing to read the PLC with
    /// yet, and the gateway must stop.
    /// </summary>
    public static async Task<SiteProfileState?> LoadAsync(ConfigurationManager config, ILogger log)
    {
        string? url = config["SiteProfile:Url"]?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            log.LogInformation("Site profile: none configured (SiteProfile:Url), so the tags in appsettings.json are used.");
            return new SiteProfileState(null);
        }

        string? key = SecretConfig.Get(config, "License:Key", SecretConfig.MinLength);
        if (key is null)
        {
            log.LogCritical("Site profile: License:Key is not set, so the tag file can be neither fetched nor opened. The gateway is stopping.");
            return null;
        }

        Profile? profile = null;
        try
        {
            profile = await DownloadAsync(url, key, CancellationToken.None);
            log.LogInformation("Site profile: version {version} from the cloud, {tags} tags.", profile.Version, profile.TagCount);
            try { SaveCache(profile, key); }
            catch (Exception ex) { log.LogWarning(ex, "Site profile: could not save the encrypted copy. The next start without internet will have no tags."); }
        }
        catch (SiteProfileException ex)
        {
            log.LogWarning("Site profile: {reason}", ex.Message);
            profile = ReadCache(key, log);
            if (profile is not null)
                log.LogWarning("Site profile: using the saved copy, version {version} from {saved:u}, {tags} tags.",
                    profile.Version, profile.SavedUtc, profile.TagCount);
        }

        if (profile is null)
        {
            log.LogCritical("Site profile: no tag file. None could be downloaded and there is no saved copy. Upload the " +
                            "client's tag file in the cloud and check this PC's internet connection. The gateway is stopping; " +
                            "as a service it tries again in a minute.");
            return null;
        }

        int baseTags = config.GetSection("Tags").GetChildren().Count();
        if (baseTags > 0)
            log.LogWarning("Site profile: appsettings.json still lists {count} tags. Settings files merge lists by position, so " +
                           "remove them there: the shipped appsettings.json must carry no tags.", baseTags);

        Apply(config, profile.Json);
        return new SiteProfileState(profile.Version);
    }

    /// <summary>GET the profile: <c>{ "version": "…", "profile": { …sections… } }</c>.</summary>
    public static async Task<Profile> DownloadAsync(string url, string licenseKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-License-Key", licenseKey);

        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SiteProfileException($"the cloud could not be reached ({ex.Message}).");
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new SiteProfileException("no tag file has been uploaded for this client in the cloud yet.");
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new SiteProfileException("the cloud refused this gateway's licence key.");
            if (!resp.IsSuccessStatusCode)
                throw new SiteProfileException($"the cloud answered HTTP {(int)resp.StatusCode}.");
            return Parse(await resp.Content.ReadAsStringAsync(ct), DateTime.UtcNow);
        }
    }

    /// <summary>Keeps only the allowed sections; a profile without tags is refused.</summary>
    private static Profile Parse(string body, DateTime savedUtc)
    {
        JsonObject root;
        try { root = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException(); }
        catch (Exception) { throw new SiteProfileException("the cloud's answer was not a tag file."); }

        string version = root["version"]?.GetValue<string>() ?? "";
        if (version.Length == 0 || root["profile"] is not JsonObject profile)
            throw new SiteProfileException("the cloud's answer was not a tag file.");

        var kept = new JsonObject();
        foreach (string section in Sections)
            if (profile[section] is { } node) kept[section] = node.DeepClone();

        int tagCount = (kept["Tags"] as JsonArray)?.Count ?? 0;
        if (tagCount == 0)
            throw new SiteProfileException("the tag file in the cloud has no Tags.");

        return new Profile(version, Encoding.UTF8.GetBytes(kept.ToJsonString()), tagCount, savedUtc);
    }

    /// <summary>The profile as one more settings layer: over the settings files, under env vars and the command line.</summary>
    private static void Apply(ConfigurationManager config, byte[] json)
    {
        int lastJson = -1;
        for (int i = 0; i < config.Sources.Count; i++)
            if (config.Sources[i] is JsonConfigurationSource or JsonStreamConfigurationSource) lastJson = i;

        config.Sources.Insert(lastJson + 1, new JsonStreamConfigurationSource { Stream = new MemoryStream(json) });
    }

    // ── The encrypted copy ───────────────────────────────────────────────────────────────────────
    // File: "SSP1" | nonce (12) | tag (16) | AES-256-GCM( {"version","savedUtc","profile"} ).

    private static byte[] CacheKey(string licenseKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256,
        Encoding.UTF8.GetBytes(licenseKey), 32, salt: "ShotSense"u8.ToArray(), info: "site profile cache v1"u8.ToArray());

    public static void SaveCache(Profile profile, string licenseKey)
    {
        var body = new JsonObject
        {
            ["version"]  = profile.Version,
            ["savedUtc"] = profile.SavedUtc.ToString("O"),
            ["profile"]  = JsonNode.Parse(profile.Json),
        };
        byte[] plain  = Encoding.UTF8.GetBytes(body.ToJsonString());
        byte[] nonce  = RandomNumberGenerator.GetBytes(12);
        byte[] tag    = new byte[16];
        byte[] cipher = new byte[plain.Length];
        using (var aes = new AesGcm(CacheKey(licenseKey), tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag);

        Directory.CreateDirectory(GatewayPaths.SiteDir);
        string temp = CacheFile + ".tmp";
        using (var file = File.Create(temp))
        {
            file.Write(Magic);
            file.Write(nonce);
            file.Write(tag);
            file.Write(cipher);
        }
        File.Move(temp, CacheFile, overwrite: true);
    }

    public static Profile? ReadCache(string licenseKey, ILogger log)
    {
        if (!File.Exists(CacheFile)) return null;
        try
        {
            byte[] all = File.ReadAllBytes(CacheFile);
            if (all.Length < Magic.Length + 28 || !all.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new CryptographicException("not a saved tag file");

            var nonce  = all.AsSpan(4, 12);
            var tag    = all.AsSpan(16, 16);
            var cipher = all.AsSpan(32);
            byte[] plain = new byte[cipher.Length];
            using (var aes = new AesGcm(CacheKey(licenseKey), 16))
                aes.Decrypt(nonce, cipher, tag, plain);

            var body = JsonNode.Parse(plain)!.AsObject();
            DateTime saved = DateTime.Parse(body["savedUtc"]!.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            return Parse(new JsonObject { ["version"] = body["version"]!.DeepClone(), ["profile"] = body["profile"]!.DeepClone() }
                .ToJsonString(), saved);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException
                                         or SiteProfileException or FormatException or InvalidOperationException)
        {
            // Most likely the licence key changed since it was saved; the next download replaces it.
            log.LogWarning("Site profile: the saved copy could not be opened ({reason}).", ex.Message);
            return null;
        }
    }
}

/// <summary>Which tag file this run uses. Version is null when the tags come from appsettings.json (a dev PC).</summary>
public sealed class SiteProfileState(string? version)
{
    public string? Version { get; } = version;

    /// <summary>Set when a newer tag file has been saved: the gateway exits with code 1 to be restarted on it.</summary>
    public bool RestartRequested { get; set; }
}

/// <summary>Why a tag file could not be had, in words for the log.</summary>
public sealed class SiteProfileException(string message) : Exception(message);
