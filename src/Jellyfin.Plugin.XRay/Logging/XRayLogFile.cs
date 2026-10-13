using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Logging;

/// <summary>
/// Writes the plugin's log messages to a dedicated daily file (<c>xray_yyyyMMdd.log</c>) next to Jellyfin's own
/// logs, so they also show up under Dashboard → Logs. Jellyfin's Serilog factory ignores extra logger
/// providers, so the plugin's loggers are wrapped with <see cref="Wrap{T}"/> instead.
/// </summary>
public sealed class XRayLogFile
{
    /// <summary>
    /// Number of daily files kept.
    /// </summary>
    public const int KeepDays = 7;

    private readonly object _lock = new();
    private readonly string _directory;
    private DateTime _currentDate;

    /// <summary>
    /// Initializes a new instance of the <see cref="XRayLogFile"/> class.
    /// </summary>
    /// <param name="directory">Directory the log files are written to.</param>
    public XRayLogFile(string directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// Gets the path of the log file for a day.
    /// </summary>
    /// <param name="date">Day.</param>
    /// <returns>Full path.</returns>
    public string GetPath(DateTime date)
        => Path.Combine(_directory, "xray_" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    /// <summary>
    /// Wraps a logger so its messages also go to the X-Ray log file.
    /// </summary>
    /// <typeparam name="T">Logger category.</typeparam>
    /// <param name="inner">Jellyfin logger.</param>
    /// <returns>Logger writing to both.</returns>
    public ILogger Wrap<T>(ILogger<T> inner) => new TeeLogger(inner, this, typeof(T).Name);

    /// <summary>
    /// Appends a line to the current day's file. IO errors are ignored: logging must never break analysis.
    /// </summary>
    /// <param name="time">Local time of the message.</param>
    /// <param name="level">Level.</param>
    /// <param name="category">Category (class name).</param>
    /// <param name="message">Formatted message.</param>
    /// <param name="exception">Exception, if any.</param>
    public void Write(DateTime time, LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(category).Append(": ").Append(message)
            .AppendLine();
        if (exception is not null)
        {
            line.Append(exception).AppendLine();
        }

        lock (_lock)
        {
            try
            {
                if (_currentDate != time.Date)
                {
                    _currentDate = time.Date;
                    Directory.CreateDirectory(_directory);
                    DeleteOldFiles(time.Date);
                }

                File.AppendAllText(GetPath(time), line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "VRB",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "FTL",
        _ => "???",
    };

    private void DeleteOldFiles(DateTime today)
    {
        var oldest = GetPath(today.AddDays(-(KeepDays - 1)));
        foreach (var file in Directory.EnumerateFiles(_directory, "xray_????????.log"))
        {
            if (string.CompareOrdinal(file, oldest) < 0)
            {
                File.Delete(file);
            }
        }
    }

    private sealed class TeeLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly XRayLogFile _file;
        private readonly string _category;

        public TeeLogger(ILogger inner, XRayLogFile file, string category)
        {
            _inner = inner;
            _file = file;
            _category = category;
        }

        private static bool FileEnabled => Plugin.Instance?.Configuration.WriteLogFile ?? true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel)
            => _inner.IsEnabled(logLevel) || (logLevel >= LogLevel.Debug && logLevel != LogLevel.None && FileEnabled);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (_inner.IsEnabled(logLevel))
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
            }

            if (logLevel >= LogLevel.Debug && logLevel != LogLevel.None && FileEnabled)
            {
                _file.Write(DateTime.Now, logLevel, _category, formatter(state, exception), exception);
            }
        }
    }
}
