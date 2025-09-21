using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Linq;
using System.Windows.Media;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Core.Processors;
using TriSplit.Core.Services;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.ViewModels.Tabs;

public partial class ProcessingViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly ISampleLoader _sampleLoader;
    private readonly CsvInputReader _csvReader;
    private readonly ExcelInputReader _excelReader;
    private readonly IAppSession _appSession;
    private readonly IProfileStore _profileStore;
    private readonly IProfileDetectionService _profileDetectionService;
    private readonly IExcelExporter _excelExporter;

    private CancellationTokenSource? _cancellationTokenSource;
    private bool _updatingFromSession = false;
    private string? _currentRunLogPath;
    private StreamWriter? _runLogWriter;

    [ObservableProperty]
    private string _inputFilePath = "No file selected";

    [ObservableProperty]
    private ObservableCollection<Profile> _availableProfiles = new();

    [ObservableProperty]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private bool _outputCsv = true;

    [ObservableProperty]
    private bool _outputExcel;

    [ObservableProperty]
    private bool _outputJson;

    [ObservableProperty]
    private bool _removeDuplicates = true;

    [ObservableProperty]
    private bool _validateEmails = true;

    [ObservableProperty]
    private bool _normalizePhones = true;

    [ObservableProperty]
    private bool _splitBatches = true;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logEntries = new();

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _processingProgress;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _processingStatus = "Ready";

    [ObservableProperty]
    private Brush _statusColor = Brushes.LightGray;

    [ObservableProperty]
    private bool _canStartProcessing;

    [ObservableProperty]
    private bool _hasOutput;

    [ObservableProperty]
    private string? _lastRunLogPath;

    [ObservableProperty]
    private bool _hasRunLog;

    [ObservableProperty]
    private string? _lastSummaryReportPath;

    private string? _outputDirectory;
    private string? _lastProfilePath;

    public ProcessingViewModel(
        IDialogService dialogService,
        ISampleLoader sampleLoader,
        IAppSession appSession,
        IProfileStore profileStore,
        IProfileDetectionService profileDetectionService,
        IExcelExporter excelExporter)
    {
        _dialogService = dialogService;
        _sampleLoader = sampleLoader;
        _appSession = appSession;
        _profileStore = profileStore;
        _profileDetectionService = profileDetectionService;
        _excelExporter = excelExporter;
        _csvReader = new CsvInputReader();
        _excelReader = new ExcelInputReader();

        _ = LoadProfilesAsync();

        // Subscribe to session changes
        _appSession.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(IAppSession.SelectedProfile))
            {
                // Reload profiles to ensure we have the latest, then select the profile
                await LoadProfilesAsync();
            }
            else if (e.PropertyName == nameof(IAppSession.LoadedFilePath))
            {
                if (!string.IsNullOrEmpty(_appSession.LoadedFilePath))
                {
                    var path = _appSession.LoadedFilePath;
                    if (!string.IsNullOrEmpty(path) && await DetectProfileForFileAsync(path))
                    {
                        InputFilePath = path;
                    }
                    else
                    {
                        InputFilePath = "No file selected";
                    }
                    UpdateCanStartProcessing();
                }
            }
        };
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _profileStore.GetAllProfilesAsync();
            AvailableProfiles = new ObservableCollection<Profile>(profiles);

            if (_appSession.SelectedProfile != null)
            {
                // Find the profile in the list that matches the session's selected profile
                var matchingProfile = AvailableProfiles.FirstOrDefault(p => p.Id == _appSession.SelectedProfile.Id);
                if (matchingProfile != null)
                {
                    // Set flag to prevent circular updates
                    _updatingFromSession = true;
                    SelectedProfile = matchingProfile;
                    _updatingFromSession = false;
                }
            }
        }
        catch (Exception ex)
        {
            AddLogEntry($"Error loading data profiles: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task<bool> DetectProfileForFileAsync(string filePath)
    {
        var headers = (await _sampleLoader.GetColumnHeadersAsync(filePath)).ToList();
        if (headers.Count == 0)
        {
            AddLogEntry($"No headers were detected in {Path.GetFileName(filePath)}.", LogLevel.Warning);
            await _dialogService.ShowMessageAsync("No Headers Found", $"No headers were detected in {Path.GetFileName(filePath)}.");
            return false;
        }

        var detectionResult = await _profileDetectionService.DetectProfileAsync(headers, filePath);
        switch (detectionResult.Outcome)
        {
            case ProfileDetectionOutcome.Matched:
                if (detectionResult.Profile != null)
                {
                    _appSession.SelectedProfile = detectionResult.Profile;
                    AddLogEntry(detectionResult.StatusMessage, LogLevel.Info);
                    ProcessingStatus = detectionResult.StatusMessage;
                    StatusColor = Brushes.LightGray;
                }
                return true;
            case ProfileDetectionOutcome.NewSource:
                _appSession.SelectedProfile = null;
                SelectedProfile = null;
                AddLogEntry(detectionResult.StatusMessage, LogLevel.Warning);
                ProcessingStatus = detectionResult.StatusMessage;
                StatusColor = Brushes.Orange;
                await _dialogService.ShowMessageAsync("Unrecognized Source", detectionResult.StatusMessage + " Please configure a profile in the Profiles tab before processing.");
                return false;
            case ProfileDetectionOutcome.Cancelled:
                _appSession.SelectedProfile = null;
                SelectedProfile = null;
                AddLogEntry(detectionResult.StatusMessage, LogLevel.Warning);
                ProcessingStatus = detectionResult.StatusMessage;
                StatusColor = Brushes.Orange;
                return false;
            default:
                return false;
        }
    }

    [RelayCommand]
    private async Task SelectFileAsync()
    {
        var filePath = await _dialogService.ShowOpenFileDialogAsync(
            "Select Input File",
            "CSV Files|*.csv|Excel Files|*.xlsx;*.xls|All Files|*.*");

        if (!string.IsNullOrEmpty(filePath))
        {
            if (!await DetectProfileForFileAsync(filePath))
            {
                InputFilePath = "No file selected";
                UpdateCanStartProcessing();
                return;
            }

            InputFilePath = filePath;
            _appSession.LoadedFilePath = filePath;
            UpdateCanStartProcessing();
            AddLogEntry($"Selected file: {Path.GetFileName(filePath)}", LogLevel.Info);
        }
    }

    [RelayCommand]
    private async Task StartProcessingAsync()
    {
        if (string.IsNullOrEmpty(InputFilePath) || InputFilePath == "No file selected" || SelectedProfile == null)
        {
            await _dialogService.ShowMessageAsync("Error", "Please select a file and data profile first");
            return;
        }

        if (!OutputCsv && !OutputExcel && !OutputJson)
        {
            await _dialogService.ShowMessageAsync("Export Required", "Select at least one output format before processing.");
            return;
        }

        LastSummaryReportPath = null;
        _lastProfilePath = SelectedProfile?.FilePath;
        CopyDiagnosticsCommand.NotifyCanExecuteChanged();

        try
        {
            IsProcessing = true;
            ProcessingProgress = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            BeginRunLog();
            WriteRunLog($"Input file: {InputFilePath}");
            if (SelectedProfile != null)
            {
                WriteRunLog($"Profile: {SelectedProfile.Name}");
            }

            AddLogEntry("Starting processing...", LogLevel.Info);
            ProcessingStatus = "Processing...";
            StatusColor = Brushes.Orange;

            // Create output directory in the Exports folder
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = Path.GetFileNameWithoutExtension(InputFilePath);
            _outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TriSplit",
                "Exports",
                timestamp,
                fileName
            );
            Directory.CreateDirectory(_outputDirectory);

            AddLogEntry($"Output directory: {_outputDirectory}", LogLevel.Info);

            // Create processor with progress reporting
            var progress = new Progress<ProcessingProgress>(p =>
            {
                ProgressText = p.Message;
                ProcessingProgress = p.PercentComplete;
                AddLogEntry(p.Message, p.PercentComplete < 0 ? LogLevel.Error : LogLevel.Info);
            });

            // Determine input reader type based on file extension
            IInputReader inputReader;
            var extension = Path.GetExtension(InputFilePath).ToLower();
            if (extension == ".csv")
            {
                inputReader = _csvReader;
            }
            else if (extension == ".xlsx" || extension == ".xls")
            {
                inputReader = _excelReader;
            }
            else
            {
                throw new NotSupportedException($"File type {extension} is not supported");
            }

            if (SelectedProfile is null)
            {
                throw new InvalidOperationException("A data profile must be selected before processing.");
            }

            // Create processor driven entirely by the selected data profile
            var processor = new UnifiedProcessor(SelectedProfile, inputReader, _excelExporter, progress);

            var options = new ProcessingOptions
            {
                OutputCsv = OutputCsv,
                OutputExcel = OutputExcel,
                OutputJson = OutputJson
            };

            var selectedOutputs = new List<string>();
            if (options.OutputCsv) selectedOutputs.Add("CSV");
            if (options.OutputExcel) selectedOutputs.Add("Excel");
            if (options.OutputJson) selectedOutputs.Add("JSON");
            WriteRunLog($"Outputs: {string.Join(", ", selectedOutputs)}");

            // Process the file and generate export files based on selected formats
            var result = await processor.ProcessAsync(InputFilePath, _outputDirectory ?? string.Empty, options, token);

            if (token.IsCancellationRequested) return;

            // Show processing results
            if (result.Success)
            {
                ProcessingProgress = 100;
                ProgressText = "Processing complete!";
                ProcessingStatus = "Completed successfully";
                StatusColor = Brushes.LimeGreen;
                HasOutput = result.CsvFiles.Count > 0 || result.ExcelFiles.Count > 0 || result.JsonFiles.Count > 0;
                LastSummaryReportPath = result.SummaryReportPath;
                CopyDiagnosticsCommand.NotifyCanExecuteChanged();

                AddLogEntry($"Processing complete!", LogLevel.Success);
                AddLogEntry($"  Total records processed: {result.TotalRecordsProcessed}", LogLevel.Success);
                AddLogEntry($"  Contacts created: {result.ContactsCreated}", LogLevel.Success);
                AddLogEntry($"  Properties created: {result.PropertiesCreated}", LogLevel.Success);
                AddLogEntry($"  Phone numbers created: {result.PhonesCreated}", LogLevel.Success);
                AddLogEntry($"", LogLevel.Info);

                if (result.CsvFiles.Count > 0)
                {
                    AddLogEntry("CSV exports:", LogLevel.Info);
                    foreach (var file in result.CsvFiles)
                    {
                        AddLogEntry($"  - {Path.GetFileName(file)}", LogLevel.Info);
                    }
                }

                if (result.ExcelFiles.Count > 0)
                {
                    AddLogEntry("Excel exports:", LogLevel.Info);
                    foreach (var file in result.ExcelFiles)
                    {
                        AddLogEntry($"  - {Path.GetFileName(file)}", LogLevel.Info);
                    }
                }

                if (result.JsonFiles.Count > 0)
                {
                    AddLogEntry("JSON exports:", LogLevel.Info);
                    foreach (var file in result.JsonFiles)
                    {
                        AddLogEntry($"  - {Path.GetFileName(file)}", LogLevel.Info);
                    }
                }

                if (!string.IsNullOrWhiteSpace(result.SummaryReportPath))
                {
                    AddLogEntry($"Summary: {Path.GetFileName(result.SummaryReportPath)}", LogLevel.Info);
                }

                if (!string.IsNullOrWhiteSpace(_currentRunLogPath))
                {
                    AddLogEntry($"Run log saved to {Path.GetFileName(_currentRunLogPath)}", LogLevel.Info);
                }
            }
            else
            {
                ProcessingProgress = 100;
                ProgressText = "Processing failed";
                ProcessingStatus = $"Failed: {result.ErrorMessage}";
                StatusColor = Brushes.Red;
                HasOutput = false;
                LastSummaryReportPath = null;
                CopyDiagnosticsCommand.NotifyCanExecuteChanged();

                AddLogEntry($"Processing failed: {result.ErrorMessage}", LogLevel.Error);
                if (!string.IsNullOrWhiteSpace(_currentRunLogPath))
                {
                    AddLogEntry($"Run log saved to {Path.GetFileName(_currentRunLogPath)}", LogLevel.Info);
                }
            }

            if (result.Success)
            {
                AddLogEntry($"Processing completed successfully in {_outputDirectory}", LogLevel.Success);
            }
        }
        catch (OperationCanceledException)
        {
            ProcessingStatus = "Cancelled";
            StatusColor = Brushes.Orange;
            LastSummaryReportPath = null;
            CopyDiagnosticsCommand.NotifyCanExecuteChanged();
            AddLogEntry("Processing cancelled by user", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            ProcessingStatus = "Failed";
            StatusColor = Brushes.Red;
            LastSummaryReportPath = null;
            CopyDiagnosticsCommand.NotifyCanExecuteChanged();
            AddLogEntry($"Processing failed: {ex.Message}", LogLevel.Error);
            await _dialogService.ShowMessageAsync("Error", $"Processing failed: {ex.Message}");
        }
        finally
        {
            EndRunLog();
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void CancelProcessing()
    {
        _cancellationTokenSource?.Cancel();
        AddLogEntry("Cancelling processing...", LogLevel.Warning);
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
    }

    [RelayCommand]
    private async Task OpenOutputFolderAsync()
    {
        if (!string.IsNullOrEmpty(_outputDirectory) && Directory.Exists(_outputDirectory))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _outputDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Error", $"Could not open folder: {ex.Message}");
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenRunLog))]
    private void OpenRunLog()
    {
        if (string.IsNullOrWhiteSpace(LastRunLogPath) || !File.Exists(LastRunLogPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LastRunLogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _ = _dialogService.ShowMessageAsync("Error", $"Could not open run log: {ex.Message}");
        }
    }

    private bool CanOpenRunLog()
    {
        return !IsProcessing && HasRunLog && !string.IsNullOrWhiteSpace(LastRunLogPath) && File.Exists(LastRunLogPath);
    }

    [RelayCommand(CanExecute = nameof(CanCopyDiagnostics))]
    private async Task CopyDiagnosticsAsync()
    {
        var artifacts = new List<(string Path, string Label)>
        {
            (LastRunLogPath ?? string.Empty, "run-log")
        };

        if (!string.IsNullOrWhiteSpace(_lastProfilePath))
        {
            artifacts.Add((_lastProfilePath!, "profile"));
        }

        if (!string.IsNullOrWhiteSpace(LastSummaryReportPath))
        {
            artifacts.Add((LastSummaryReportPath!, "summary"));
        }

        var existingArtifacts = artifacts
            .Where(a => !string.IsNullOrWhiteSpace(a.Path) && File.Exists(a.Path))
            .ToList();

        if (existingArtifacts.Count == 0)
        {
            await _dialogService.ShowMessageAsync("Diagnostics", "No diagnostic artifacts are available yet. Run processing first.");
            return;
        }

        var diagnosticsDirectory = ApplicationPaths.TempPath;
        Directory.CreateDirectory(diagnosticsDirectory);
        var zipPath = Path.Combine(diagnosticsDirectory, $"TriSplit_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (path, label) in existingArtifacts)
                {
                    var entryName = $"{label}_{Path.GetFileName(path)}";
                    archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                }
            }

            try
            {
                Clipboard.SetText(zipPath);
            }
            catch
            {
                // Ignore clipboard errors
            }

            AddLogEntry($"Diagnostics bundle created at {zipPath}", LogLevel.Info);
            await _dialogService.ShowMessageAsync("Diagnostics Ready", $"Saved to:\n{zipPath}\n\nPath copied to clipboard.");
        }
        catch (Exception ex)
        {
            AddLogEntry($"Failed to collect diagnostics: {ex.Message}", LogLevel.Error);
            await _dialogService.ShowMessageAsync("Diagnostics Failed", $"Could not create diagnostics bundle: {ex.Message}");
        }
    }

    private bool CanCopyDiagnostics()
    {
        return !IsProcessing && HasRunLog && !string.IsNullOrWhiteSpace(LastRunLogPath) && File.Exists(LastRunLogPath);
    }

    private void BeginRunLog()
    {
        EndRunLog();
        var logsDirectory = ApplicationPaths.LogsPath;
        Directory.CreateDirectory(logsDirectory);
        _currentRunLogPath = Path.Combine(logsDirectory, $"processing-run_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _runLogWriter = new StreamWriter(new FileStream(_currentRunLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        WriteRunLog("=== Processing run started ===");
    }

    private void EndRunLog()
    {
        if (_runLogWriter != null)
        {
            WriteRunLog("=== Processing run finished ===");
            _runLogWriter.Dispose();
            _runLogWriter = null;
        }

        if (!string.IsNullOrWhiteSpace(_currentRunLogPath))
        {
            LastRunLogPath = _currentRunLogPath;
        }

        _currentRunLogPath = null;
    }

    private void WriteRunLog(string message)
    {
        if (_runLogWriter == null)
        {
            return;
        }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            _runLogWriter.WriteLine(line);
        }
        catch
        {
            // Ignore logging errors
        }
    }

    private void UpdateCanStartProcessing()
    {
        CanStartProcessing = !IsProcessing &&
                            !string.IsNullOrEmpty(InputFilePath) &&
                            InputFilePath != "No file selected" &&
                            SelectedProfile != null &&
                            (OutputCsv || OutputExcel || OutputJson);
    }

    partial void OnOutputCsvChanged(bool value)
    {
        UpdateCanStartProcessing();
    }

    partial void OnOutputExcelChanged(bool value)
    {
        UpdateCanStartProcessing();
    }

    partial void OnOutputJsonChanged(bool value)
    {
        UpdateCanStartProcessing();
    }

    partial void OnLastSummaryReportPathChanged(string? value)
    {
        CopyDiagnosticsCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastRunLogPathChanged(string? value)
    {
        HasRunLog = !string.IsNullOrWhiteSpace(value) && File.Exists(value);
        OpenRunLogCommand.NotifyCanExecuteChanged();
        CopyDiagnosticsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        UpdateCanStartProcessing();

        // Only update session and log if this change didn't come from the session
        if (!_updatingFromSession && value != null)
        {
            _appSession.SelectedProfile = value;
            AddLogEntry($"Selected data profile: {value.Name}", LogLevel.Info);
        }
    }

    partial void OnIsProcessingChanged(bool value)
    {
        UpdateCanStartProcessing();
        OpenRunLogCommand.NotifyCanExecuteChanged();
        CopyDiagnosticsCommand.NotifyCanExecuteChanged();
    }

    private void AddLogEntry(string message, LogLevel level)
    {
        WriteRunLog($"[{level}] {message}");

        var color = level switch
        {
            LogLevel.Success => Brushes.LimeGreen,
            LogLevel.Warning => Brushes.Orange,
            LogLevel.Error => Brushes.Red,
            _ => Brushes.LightGray
        };

        LogEntries.Add(new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message,
            MessageColor = color
        });
    }
}

public partial class LogEntry : ObservableObject
{
    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private Brush _messageColor = Brushes.LightGray;
}

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}




