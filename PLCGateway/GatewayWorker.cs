using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PLCGateway.Models;

public class GatewayWorker : BackgroundService
{
    private readonly ILogger<GatewayWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly DatabaseService _dbService;
    private readonly PlcService _plcService;
    private readonly CovDetectionService _covService;
    private readonly PlcConnectionState _connState;

    private int _scanMs = 1000;
    private int _stringMaxLen = TagParser.DefaultStringMaxLen;
    private int _heartbeatSeconds = 60;
    private readonly int _disconnectAfterFailedScans;

    // Resolved, pre-parsed tag metadata (addresses parsed once at startup, not every scan).
    private readonly List<ResolvedTag> _readTags = new();
    private readonly List<PlcAddress> _writeTags = new();
    // Per data block: the byte region to read in one request.
    private readonly Dictionary<int, (int Start, int Length)> _regions = new();

    // In-memory previous-value cache: last stored value + last Tier 2 store time per address.
    // Seeded from Tier 1 at startup; replaces the per-tag SELECT that ran every scan.
    private readonly Dictionary<string, (string? Value, DateTime? LastTier2At)> _cache = new();
    private readonly Dictionary<string, object> _lastWrittenValues = new();

    // Blast state — tracked from the Blast ON/OFF tag each scan; Current_imp_N readings are
    // stored every scan while blast is active so energy math has dense amp samples.
    private bool _blastIsOn = false;
    public const string BLAST_TAG_NAME = "Blast ON/OFF";
    public const string MACHINE_STATUS_TAG_NAME = "Machine status";
    private static readonly HashSet<string> CurrentImpTags =
        new(Enumerable.Range(1, 10).Select(i => $"Current_imp_{i}"));

    private sealed class ResolvedTag
    {
        public string Address = string.Empty;
        public string Name = string.Empty;
        public string DataType = string.Empty;
        public int Db;
        public int ByteOffset;
        public int Bit;
        public int TypeLen;
        public string CovMode = "relative";
        public double? CovDeadband;
        public bool IsHeartbeat;
        public bool IsCurrentImp;
    }

    public GatewayWorker(
        ILogger<GatewayWorker> logger,
        IConfiguration configuration,
        DatabaseService dbService,
        PlcService plcService,
        CovDetectionService covService,
        PlcConnectionState connState)
    {
        _logger = logger;
        _configuration = configuration;
        _dbService = dbService;
        _plcService = plcService;
        _covService = covService;
        _connState = connState;

        _scanMs = _configuration.GetValue<int?>("ScanIntervalMs") ?? 1000;
        _stringMaxLen = _configuration.GetValue<int?>("DataCollection:StringMaxLength") ?? TagParser.DefaultStringMaxLen;
        _heartbeatSeconds = _configuration.GetValue<int?>("DataCollection:PeriodicHeartbeatSeconds") ?? 60;
        _disconnectAfterFailedScans = _configuration.GetValue<int?>("PLC:DisconnectAfterFailedScans") ?? 2;

        BuildTagPlan();
    }

    // Parses every configured tag once: resolves address → (db, byte, bit), type length,
    // per-tag COV rule and heartbeat/current-imp flags, then computes the contiguous byte
    // region to read per data block.
    private void BuildTagPlan()
    {
        var tagsSection = _configuration.GetSection("Tags");
        if (!tagsSection.Exists())
        {
            _logger.LogWarning("No Tags found in configuration (appsettings.json).");
            return;
        }

        var tags = tagsSection.Get<List<PlcAddress>>() ?? new List<PlcAddress>();

        var absByName = _configuration.GetSection("CovRules:AbsoluteByName").Get<Dictionary<string, double>>()
                        ?? new Dictionary<string, double>();
        var absByPrefix = _configuration.GetSection("CovRules:AbsoluteByPrefix").Get<Dictionary<string, double>>()
                          ?? new Dictionary<string, double>();
        var heartbeatTags = new HashSet<string>(
            _configuration.GetSection("DataCollection:HeartbeatTags").Get<string[]>() ?? Array.Empty<string>());

        foreach (var tag in tags)
        {
            var mode = tag.Mode?.ToLowerInvariant() ?? "";
            if (mode == "write" || mode == "readwrite")
                _writeTags.Add(tag);
            if (mode != "read" && mode != "readwrite")
                continue;

            int db, byteOffset, bit;
            try
            {
                (db, byteOffset, bit) = TagParser.ParseAddress(tag.Address);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping tag {name} with unparseable address {addr}.", tag.Name, tag.Address);
                continue;
            }

            string dataType = tag.DataType ?? "UNKNOWN";
            int typeLen = TagParser.TypeLengthBytes(dataType, _stringMaxLen);

            (string covMode, double? deadband) = ResolveCovRule(tag.Name, absByName, absByPrefix);

            _readTags.Add(new ResolvedTag
            {
                Address      = tag.Address,
                Name         = tag.Name,
                DataType     = dataType,
                Db           = db,
                ByteOffset   = byteOffset,
                Bit          = bit,
                TypeLen      = typeLen,
                CovMode      = covMode,
                CovDeadband  = deadband,
                IsHeartbeat  = heartbeatTags.Contains(tag.Name),
                IsCurrentImp = CurrentImpTags.Contains(tag.Name)
            });
        }

        foreach (var group in _readTags.GroupBy(t => t.Db))
        {
            int start = group.Min(t => t.ByteOffset);
            int end   = group.Max(t => t.ByteOffset + t.TypeLen);
            _regions[group.Key] = (start, end - start);
        }

        _logger.LogInformation(
            "Tag plan built: {read} read tags, {write} write tags, {regions} DB region(s).",
            _readTags.Count, _writeTags.Count, _regions.Count);
    }

    private static (string Mode, double? Deadband) ResolveCovRule(
        string name,
        Dictionary<string, double> absByName,
        Dictionary<string, double> absByPrefix)
    {
        if (absByName.TryGetValue(name, out double d))
            return ("absolute", d);
        foreach (var kv in absByPrefix)
            if (name.StartsWith(kv.Key, StringComparison.Ordinal))
                return ("absolute", kv.Value);
        return ("relative", null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GatewayWorker starting with batched two-tier architecture.");

        // Seed the previous-value cache from Tier 1 so COV/heartbeat decisions survive restarts.
        try
        {
            foreach (var (address, value, lastStored) in await _dbService.GetCacheSeedAsync())
                _cache[address] = (value, lastStored);
            _logger.LogInformation("Seeded scan cache with {n} tags.", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed scan cache; starting empty.");
        }

        try
        {
            _plcService.Connect();
            _logger.LogInformation("Connected to PLC.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial PLC connection failed; worker will retry in loop.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool passOk = false;

            try
            {
                try { _plcService.Connect(); }
                catch (Exception ex) { _logger.LogWarning(ex, "PLC connect attempt failed in loop."); }

                if (_plcService.IsConnected)
                {
                    passOk = await RunScanPassAsync();
                    WriteReservedTags();
                }
                else
                {
                    _logger.LogWarning("PLC not connected this cycle.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in worker loop.");
                passOk = false;
            }

            await UpdateConnectionStateAsync(passOk);

            await Task.Delay(_scanMs, stoppingToken);
        }

        _logger.LogInformation("GatewayWorker stopping.");
    }

    // Reads all DB regions, parses every tag, and writes one batched Tier 1 + Tier 2 pass.
    // Returns true when the region reads succeeded (a connected-but-dead link returns false,
    // which feeds the disconnect detection).
    private async Task<bool> RunScanPassAsync()
    {
        var regionData = new Dictionary<int, byte[]>();
        foreach (var (db, region) in _regions)
        {
            try
            {
                regionData[db] = _plcService.ReadRegion(db, region.Start, region.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Region read failed for DB{db} ({start}+{len}).", db, region.Start, region.Length);
                return false;
            }
        }

        var now = DateTime.Now;
        var tier1 = new List<Tier1Write>(_readTags.Count);
        var tier2 = new List<Tier2Write>();

        foreach (var rt in _readTags)
        {
            if (!regionData.TryGetValue(rt.Db, out var bytes)) continue;
            int regionStart = _regions[rt.Db].Start;

            string value;
            try
            {
                value = TagParser.Parse(bytes, regionStart, rt.ByteOffset, rt.Bit, rt.DataType, _stringMaxLen);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Parse failed for {name} ({addr}).", rt.Name, rt.Address);
                continue;
            }

            _cache.TryGetValue(rt.Address, out var prev);
            string? previousValue = prev.Value;

            if (rt.Name == BLAST_TAG_NAME)
                _blastIsOn = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

            tier1.Add(new Tier1Write
            {
                Address = rt.Address,
                ParameterName = rt.Name,
                Value = value,
                DataType = rt.DataType
            });

            string? reason = _covService.ShouldStoreInHistorical(
                value, rt.DataType, previousValue, rt.CovMode, rt.CovDeadband);

            if (reason == null && rt.IsCurrentImp && _blastIsOn)
                reason = "BLAST_ON";

            if (reason == null && rt.IsHeartbeat &&
                (prev.LastTier2At == null || (now - prev.LastTier2At.Value).TotalSeconds >= _heartbeatSeconds))
                reason = "HEARTBEAT";

            DateTime? lastT2 = prev.LastTier2At;
            if (reason != null)
            {
                tier2.Add(new Tier2Write
                {
                    Address = rt.Address,
                    ParameterName = rt.Name,
                    Value = value,
                    DataType = rt.DataType,
                    StorageReason = reason,
                    PreviousValue = previousValue
                });
                lastT2 = now;
            }

            _cache[rt.Address] = (value, lastT2);
        }

        await _dbService.WriteScanBatchAsync(tier1, tier2);
        return true;
    }

    // RESERVED — not exercised by the current tag set (all tags are read-only). Kept for the
    // spares REPLACED write-back flow: write config-provided values back to the PLC when a
    // write/readwrite tag is configured. Do not delete.
    private void WriteReservedTags()
    {
        foreach (var tag in _writeTags)
        {
            object? writeValue = tag.WriteValue;
            if (writeValue == null) continue;

            bool isNew = !_lastWrittenValues.TryGetValue(tag.Address, out var last) || !Equals(last, writeValue);
            if (!isNew) continue;

            try
            {
                _plcService.Write(tag.Address, writeValue);
                _lastWrittenValues[tag.Address] = writeValue;
                _logger.LogInformation("Write {name} ({addr}) = {val}", tag.Name, tag.Address, writeValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write to PLC for tag {name} ({addr})", tag.Name, tag.Address);
            }
        }
    }

    // Maps a scan pass outcome onto the shared connection state and the gateway_status row.
    // On the disconnect transition, forces machine/blast OFF backdated to the last good scan
    // so the unobserved gap contributes zero to every duration and accumulator.
    private async Task UpdateConnectionStateAsync(bool passOk)
    {
        try
        {
            var scanTime = DateTime.Now;

            if (passOk)
            {
                bool reconnected = _connState.RecordScanSuccess(scanTime);
                await _dbService.UpdateGatewayScanHeartbeatAsync(scanTime);
                if (reconnected)
                {
                    await _dbService.SetGatewayConnectedAsync(true, scanTime);
                    _logger.LogInformation("PLC connected — live data resumed.");
                }
            }
            else
            {
                bool disconnected = _connState.RecordScanFailure(_disconnectAfterFailedScans);
                if (disconnected)
                {
                    var disconnectAt = _connState.LastSuccessfulScanAt ?? scanTime;
                    _blastIsOn = false;
                    await _dbService.RecordPlcDisconnectAsync(
                        disconnectAt, MACHINE_STATUS_TAG_NAME, BLAST_TAG_NAME);
                    _logger.LogWarning(
                        "PLC disconnected — machine status forced OFF, Tier 1 marked stale (backdated to {t}).",
                        disconnectAt);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update PLC connection state.");
        }
    }
}
