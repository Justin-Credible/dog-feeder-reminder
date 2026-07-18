using Meadow;
using System;
using System.IO;
using System.Text;

namespace dog_feeder_reminder;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public static class Logger
{
    const string LogFileName = "dog-feeder.log";
    const string PreviousLogFileName = "dog-feeder.log.1";
    const long MinimumMaxFileSizeBytes = 4 * 1024;
    const long DefaultMaxFileSizeBytes = 256 * 1024;

    static readonly object FileGate = new object();

    static bool fileLoggingEnabled;
    static LogLevel minFileLogLevel = LogLevel.Info;
    static long maxFileSizeBytes = DefaultMaxFileSizeBytes;
    static string logFilePath;
    static string previousLogFilePath;
    static Func<DateTime?> deviceTimeProvider;

    public static bool IsFileLoggingEnabled => fileLoggingEnabled;

    /// <summary>
    /// Enables (or disables) rolling disk logging as early as possible during startup.
    /// Safe to call multiple times; the most recent call wins.
    /// </summary>
    public static void ConfigureFileLogging(bool enabled, LogLevel minLevel, long maxFileSizeBytesValue, string dataDirectory)
    {
        lock (FileGate)
        {
            minFileLogLevel = minLevel;
            maxFileSizeBytes = maxFileSizeBytesValue >= MinimumMaxFileSizeBytes ? maxFileSizeBytesValue : DefaultMaxFileSizeBytes;

            if (!enabled || string.IsNullOrWhiteSpace(dataDirectory))
            {
                fileLoggingEnabled = false;
                return;
            }

            try
            {
                Directory.CreateDirectory(dataDirectory);
                logFilePath = Path.Combine(dataDirectory, LogFileName);
                previousLogFilePath = Path.Combine(dataDirectory, PreviousLogFileName);
                fileLoggingEnabled = true;
            }
            catch (Exception ex)
            {
                fileLoggingEnabled = false;
                Resolver.Log.Error($"ERROR [Logger] Failed to initialize disk logging: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Registers a callback that returns the current device time, or null if it is not yet valid.
    /// Until this is set (or while it returns null), disk log entries use a zeroed timestamp.
    /// </summary>
    public static void SetDeviceTimeProvider(Func<DateTime?> provider)
    {
        deviceTimeProvider = provider;
    }

    public static void Debug(string tag, string message) => Write(LogLevel.Debug, tag, message);

    public static void Info(string tag, string message) => Write(LogLevel.Info, tag, message);

    public static void Warn(string tag, string message) => Write(LogLevel.Warn, tag, message);

    public static void Error(string tag, string message) => Write(LogLevel.Error, tag, message);

    /// <summary>
    /// Returns the current disk log content (most recent file only), or an empty string if
    /// disk logging is disabled or no log has been written yet.
    /// </summary>
    public static string ReadLogFileText()
    {
        lock (FileGate)
        {
            if (!fileLoggingEnabled || string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
            {
                return string.Empty;
            }

            try
            {
                using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                return $"Failed to read log file: {ex.Message}";
            }
        }
    }

    static void Write(LogLevel level, string tag, string message)
    {
        var line = $"{LevelLabel(level)} [{tag}] {message}";

        switch (level)
        {
            case LogLevel.Debug:
                Resolver.Log.Debug(line);
                break;
            case LogLevel.Info:
                Resolver.Log.Info(line);
                break;
            case LogLevel.Warn:
                Resolver.Log.Warn(line);
                break;
            default:
                Resolver.Log.Error(line);
                break;
        }

        AppendToFile(level, line);
    }

    static string LevelLabel(LogLevel level)
    {
        switch (level)
        {
            case LogLevel.Debug:
                return "DEBUG";
            case LogLevel.Info:
                return "INFO";
            case LogLevel.Warn:
                return "WARN";
            default:
                return "ERROR";
        }
    }

    static void AppendToFile(LogLevel level, string line)
    {
        if (!fileLoggingEnabled || level < minFileLogLevel)
        {
            return;
        }

        var timestamp = FormatTimestamp();

        lock (FileGate)
        {
            if (!fileLoggingEnabled || string.IsNullOrWhiteSpace(logFilePath))
            {
                return;
            }

            try
            {
                RollFileIfNeededLocked();
                File.AppendAllText(logFilePath, $"{timestamp} {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Avoid throwing from logging; disk issues shouldn't crash the app.
            }
        }
    }

    static void RollFileIfNeededLocked()
    {
        if (!File.Exists(logFilePath))
        {
            return;
        }

        if (new FileInfo(logFilePath).Length < maxFileSizeBytes)
        {
            return;
        }

        try
        {
            if (File.Exists(previousLogFilePath))
            {
                File.Delete(previousLogFilePath);
            }

            File.Move(logFilePath, previousLogFilePath);
        }
        catch
        {
            // Best effort; if rolling fails, truncate so logging can continue.
            try
            {
                File.Delete(logFilePath);
            }
            catch
            {
                // Nothing more we can do.
            }
        }
    }

    static string FormatTimestamp()
    {
        var time = deviceTimeProvider?.Invoke();
        return time.HasValue
            ? time.Value.ToString("yyyy-MM-dd HH:mm:ss:fff")
            : "0000-00-00 00:00:00:000";
    }
}
