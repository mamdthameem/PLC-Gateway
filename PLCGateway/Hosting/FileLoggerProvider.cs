using System.Text;

namespace PlcApi.Hosting;

// Plain daily log files for an installed gateway: logs\gateway-20261004.log, one per day, kept for
// RetainDays. A Windows service has no console, so without this the only record of a problem at a
// client site would be whatever someone happened to see. Deliberately small — no buffering, no
// background thread — because the gateway logs little once it is running normally.
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainDays = 30;

    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateTime _day;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string text)
    {
        lock (_gate)
        {
            try
            {
                DateTime today = DateTime.Now.Date;
                if (_writer is null || today != _day)
                {
                    _writer?.Dispose();
                    _day = today;
                    string path = Path.Combine(_directory, $"gateway-{today:yyyyMMdd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(false)) { AutoFlush = true };
                    PruneOldFiles();
                }
                _writer.Write(text);
            }
            catch
            {
                // Logging must never take the gateway down (a full disk, a locked file). Drop the line.
            }
        }
    }

    private void PruneOldFiles()
    {
        DateTime cutoff = DateTime.Now.Date.AddDays(-RetainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "gateway-*.log"))
        {
            try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); } catch { /* next time */ }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Level filtering is done by the logging framework from the "Logging" settings section.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ').Append(Level(logLevel)).Append(' ')
                .Append(_category).Append(": ")
                .AppendLine(formatter(state, exception));
            if (exception is not null) sb.AppendLine(exception.ToString());

            _provider.Write(sb.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace       => "TRACE",
            LogLevel.Debug       => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "CRIT ",
            _                    => "     ",
        };
    }
}
