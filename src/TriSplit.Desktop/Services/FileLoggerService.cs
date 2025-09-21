using System.IO;
using System.Linq;

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
    private const int MaxLogFiles = 10;
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    private readonly object _lockObject = new();
    private readonly string _logsDirectory;
    private StreamWriter? _logWriter;
    private string _currentLogPath = string.Empty;

    public FileLoggerService()
    {
        _logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TriSplit",
            "Logs");

        Directory.CreateDirectory(_logsDirectory);

        try
        {
            RotateLog();
        }
        catch
        {
            _logWriter = null;
        }
    }

    public string? CurrentLogPath => _currentLogPath;

    public void LogInfo(string message) => WriteLog("INFO", message);

    public void LogWarning(string message) => WriteLog("WARN", message);

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
        lock (_lockObject)
        {
            EnsureWriter();
            if (_logWriter == null)
            {
                return;
            }

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

    private void EnsureWriter()
    {
        if (_logWriter == null)
        {
            RotateLog();
            return;
        }

        try
        {
            if (_logWriter.BaseStream.Length >= MaxLogFileSizeBytes)
            {
                RotateLog();
            }
        }
        catch
        {
            RotateLog();
        }
    }

    private void RotateLog()
    {
        try
        {
            _logWriter?.Flush();
            _logWriter?.Dispose();
        }
        catch
        {
            // Ignore disposal issues when rotating
        }

        _currentLogPath = Path.Combine(_logsDirectory, $"trisplit_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _logWriter = new StreamWriter(new FileStream(_currentLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };

        CleanupOldLogs();
    }

    private void CleanupOldLogs()
    {
        try
        {
            var existingLogs = Directory.GetFiles(_logsDirectory, "trisplit_*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            for (var index = MaxLogFiles; index < existingLogs.Count; index++)
            {
                try
                {
                    File.Delete(existingLogs[index]);
                }
                catch
                {
                    // Ignore errors when pruning logs
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
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
                _logWriter = null;
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
        try
        {
            Instance.LogInfo("TriSplit Application Shutdown");
            Instance.LogInfo("===========================================");
        }
        finally
        {
            Dispose();
        }
    }

    public static void Dispose()
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}
