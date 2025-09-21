using System.IO;

namespace TriSplit.Desktop.Services;

public interface IApplicationBootstrapper
{
    void Initialize();
    string GetAppDataPath();
    string GetProfilesPath();
    string GetProfileMetadataPath();
    string GetTemplatesPath();
    string GetTempPath();
    string GetExportsPath();
    string GetLogsPath();
}

public class ApplicationBootstrapper : IApplicationBootstrapper
{
    private readonly string _appDataPath;

    public ApplicationBootstrapper()
    {
        _appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TriSplit");
    }

    public void Initialize()
    {
        try
        {
            var directories = new[]
            {
                _appDataPath,
                GetProfilesPath(),
                GetProfileMetadataPath(),
                GetTemplatesPath(),
                GetTempPath(),
                GetExportsPath(),
                GetLogsPath()
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }
        catch
        {
            // Continue even if directories cannot be created
        }
    }

    public string GetAppDataPath() => _appDataPath;
    public string GetProfilesPath() => Path.Combine(_appDataPath, "Profiles");
    public string GetProfileMetadataPath() => Path.Combine(GetProfilesPath(), "ProfileMetadata");
    public string GetTemplatesPath() => Path.Combine(_appDataPath, "Templates");
    public string GetTempPath() => Path.Combine(_appDataPath, "Temp");
    public string GetExportsPath() => Path.Combine(_appDataPath, "Exports");
    public string GetLogsPath() => Path.Combine(_appDataPath, "Logs");
}

public static class ApplicationPaths
{
    private static string? _appDataPath;

    public static string AppDataPath
    {
        get
        {
            _appDataPath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TriSplit");
            return _appDataPath;
        }
    }

    public static string ProfilesPath => Path.Combine(AppDataPath, "Profiles");
    public static string ProfileMetadataPath => Path.Combine(ProfilesPath, "ProfileMetadata");
    public static string TemplatesPath => Path.Combine(AppDataPath, "Templates");
    public static string TempPath => Path.Combine(AppDataPath, "Temp");
    public static string ExportsPath => Path.Combine(AppDataPath, "Exports");
    public static string LogsPath => Path.Combine(AppDataPath, "Logs");

    public static string GetExportPath(string? subfolder = null)
    {
        var basePath = ExportsPath;
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            basePath = Path.Combine(basePath, subfolder);
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(basePath, timestamp);
    }

    public static string GetTempWorkspace(string sessionId)
    {
        return Path.Combine(TempPath, sessionId);
    }
}
