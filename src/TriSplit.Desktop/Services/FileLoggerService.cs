using System.IO;

namespace TriSplit.Desktop.Services;

public interface IFileLoggerService
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
    void LogDebug(string message);
}

public class FileLoggerService : IFileLoggerService, IDisposable
{
    private readonly string _logPath;
    private readonly StreamWriter? _logWriter;
    private readonly object _lockObject = new();

    public FileLoggerService()
    {
        var logsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TriSplit",
            "Logs"
        );

        Directory.CreateDirectory(logsPath);

        var logFileName = $"trisplit_{DateTime.Now:yyyyMMdd}.log";
        _logPath = Path.Combine(logsPath, logFileName);

        try
        {
            _logWriter = new StreamWriter(_logPath, append: true)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // If we can't write to the log file, continue without logging
        }
    }

    public void LogInfo(string message)
    {
        WriteLog("INFO", message);
    }

    public void LogWarning(string message)
    {
        WriteLog("WARN", message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message} | Exception: {ex}" : message;
        WriteLog("ERROR", fullMessage);
    }

    public void LogDebug(string message)
    {
#if DEBUG
        WriteLog("DEBUG", message);
#endif
    }

    private void WriteLog(string level, string message)
    {
        if (_logWriter == null) return;

        lock (_lockObject)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level}] {message}";
                _logWriter.WriteLine(logEntry);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            try
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}

public static class ApplicationLogger
{
    private static FileLoggerService? _instance;
    private static readonly object _lock = new();

    public static IFileLoggerService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new FileLoggerService();
                }
            }
            return _instance;
        }
    }

    public static void LogStartup()
    {
        Instance.LogInfo("===========================================");
        Instance.LogInfo("TriSplit Application Started");
        Instance.LogInfo($"Version: 1.0.0");
        Instance.LogInfo($"Machine: {Environment.MachineName}");
        Instance.LogInfo($"User: {Environment.UserName}");
        Instance.LogInfo($"OS: {Environment.OSVersion}");
        Instance.LogInfo($".NET: {Environment.Version}");
        Instance.LogInfo("===========================================");
    }

    public static void LogShutdown()
    {
        Instance.LogInfo("TriSplit Application Shutdown");
        Instance.LogInfo("===========================================");
    }
}