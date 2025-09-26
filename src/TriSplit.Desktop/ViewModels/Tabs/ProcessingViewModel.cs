using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private string _detectedSourceDisplay = "Upload a file to detect the data source";
    [ObservableProperty]
    private bool _showSourceActions;
    [ObservableProperty]
    private bool _isSourceConfirmed;
    [ObservableProperty]
    private bool _isOverrideMode;
    [ObservableProperty]
    private Profile? _overrideProfile;
    [ObservableProperty]
    private bool _requiresProfileSetup;
    [ObservableProperty]
    private bool _outputCsv = true;
    [ObservableProperty]
    private bool _outputExcel;
    [ObservableProperty]
    private bool _outputJson;
    [ObservableProperty]
    private DateTime? _tagDataDate = DateTime.Today;
    [ObservableProperty]
    private string _tagDraft = string.Empty;
    [ObservableProperty]
    private string _acceptedTag = string.Empty;
    [ObservableProperty]
    private bool _tagAccepted;
    [ObservableProperty]
    private string _tagStatus = "No tag generated";
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
    private bool _isUpdatingTagDraft;
    private static readonly int[] _progressMilestones = new[] { 25, 50, 75 };
    private int _nextProgressMilestoneIndex;
    public ProcessingViewModel(
        IDialogService dialogService,
        ISampleLoader sampleLoader,
        IAppSession appSession,
        IProfileStore profileStore,
        IProfileDetectionService profileDetectionService)
    {
        _dialogService = dialogService;
        _sampleLoader = sampleLoader;
        _appSession = appSession;
        _profileStore = profileStore;
        _profileDetectionService = profileDetectionService;

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
        OutputCsv = _appSession.OutputCsv;
        OutputExcel = _appSession.OutputExcel;
        OutputJson = _appSession.OutputJson;
        if (!string.IsNullOrWhiteSpace(_appSession.LoadedFilePath))
        {
            InputFilePath = _appSession.LoadedFilePath;
            _ = DetectProfileForFileAsync(_appSession.LoadedFilePath);
        RefreshSourceActionState();
        CancelOverrideCommand.NotifyCanExecuteChanged();
        }
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
            else if (_appSession.LastProfileId.HasValue)
            {
                var persistedProfile = AvailableProfiles.FirstOrDefault(p => p.Id == _appSession.LastProfileId.Value);
                if (persistedProfile != null)
                {
                    _updatingFromSession = true;
                    SelectedProfile = persistedProfile;
                    _updatingFromSession = false;
                    _appSession.SelectedProfile = persistedProfile;
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
        DetectedSourceDisplay = "Identifying Data Source";
        IsOverrideMode = false;
        IsSourceConfirmed = false;
        ShowSourceActions = false;
        var headers = (await _sampleLoader.GetColumnHeadersAsync(filePath)).ToList();
        if (headers.Count == 0)
        {
            DetectedSourceDisplay = "Upload a file to detect the data source";
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
                    DetectedSourceDisplay = detectionResult.Profile.Name;
                    AddLogEntry(detectionResult.StatusMessage, LogLevel.Info);
                    ProcessingStatus = detectionResult.StatusMessage;
                    StatusColor = Brushes.LightGray;
                    RequiresProfileSetup = false;
                    RefreshSourceActionState();
        CancelOverrideCommand.NotifyCanExecuteChanged();
                }
                else
                {
                    DetectedSourceDisplay = "Data source detected";
                }
                return true;
                        case ProfileDetectionOutcome.NewSource:
                _appSession.SelectedProfile = null;
                SelectedProfile = null;
                DetectedSourceDisplay = "Data source not recognized";
                AddLogEntry(detectionResult.StatusMessage, LogLevel.Warning);
                ProcessingStatus = "New source detected. Choose how to proceed.";
                StatusColor = Brushes.Orange;
                if (!_appSession.TryEnterNewSourcePrompt(filePath))
                {
                    return false;
                }
                try
                {
                    var decision = await _dialogService.ShowNewSourceDecisionAsync(Path.GetFileName(filePath));
                    switch (decision)
                    {
                        case NewSourceDecision.UpdateExisting:
                            if (AvailableProfiles.Count == 0)
                            {
                                RequiresProfileSetup = true;
                                _appSession.NotifyNewSourceRequested(filePath, headers);
                                _appSession.RequestNavigation(AppTab.Profiles);
                                InputFilePath = filePath;
                                ProcessingStatus = "Create and save a new profile before processing.";
                                AddLogEntry("No saved profiles exist yet. Redirecting to create a new profile.", LogLevel.Info);
                            }
                            else
                            {
                                RequiresProfileSetup = false;
                                _appSession.LoadedFilePath = filePath;
                                InputFilePath = filePath;
                                BeginOverride();
                                ProcessingStatus = "Select a saved profile to update this source.";
                                AddLogEntry("Select an existing data profile to map this source.", LogLevel.Info);
                            }
                            RefreshSourceActionState();
                            return false;
                        case NewSourceDecision.CreateNew:
                            RequiresProfileSetup = true;
                            _appSession.NotifyNewSourceRequested(filePath, headers);
                            _appSession.RequestNavigation(AppTab.Profiles);
                            _appSession.LoadedFilePath = filePath;
                            InputFilePath = filePath;
                            ProcessingStatus = "Create and save a new profile before processing.";
                            AddLogEntry("Navigated to Profiles tab to capture a new data source.", LogLevel.Info);
                            RefreshSourceActionState();
                            return false;
                        default:
                            ProcessingStatus = "Profile detection cancelled.";
                            StatusColor = Brushes.Orange;
                            RefreshSourceActionState();
                            return false;
                    }
                }
                finally
                {
                    _appSession.CompleteNewSourcePrompt();
                }case ProfileDetectionOutcome.Cancelled:
                _appSession.SelectedProfile = null;
                SelectedProfile = null;
                DetectedSourceDisplay = "Data source detection cancelled";
                AddLogEntry(detectionResult.StatusMessage, LogLevel.Warning);
                ProcessingStatus = detectionResult.StatusMessage;
                StatusColor = Brushes.Orange;
                RequiresProfileSetup = false;
                RefreshSourceActionState();
                return false;
            default:
                DetectedSourceDisplay = "Unable to detect data source";
                return false;
        }
    }
    [RelayCommand(CanExecute = nameof(CanAcceptDetectedSource))]
    private void AcceptDetectedSource()
    {
        if (SelectedProfile == null)
        {
            return;
        }
        IsSourceConfirmed = true;
        IsOverrideMode = false;
        RequiresProfileSetup = false;
        ProcessingStatus = $"Data source accepted: {SelectedProfile.Name}";
        StatusColor = Brushes.LightGray;
        AddLogEntry($"Accepted data source: {SelectedProfile.Name}", LogLevel.Success);
    }
    private bool CanAcceptDetectedSource() => SelectedProfile != null && !IsSourceConfirmed && !IsOverrideMode;
    [RelayCommand(CanExecute = nameof(CanBeginOverride))]
    private void BeginOverride()
    {
        if (AvailableProfiles.Count == 0)
        {
            return;
        }
        IsOverrideMode = true;
        IsSourceConfirmed = false;
        OverrideProfile = SelectedProfile ?? AvailableProfiles.FirstOrDefault();
        ProcessingStatus = "Override data source and apply to continue.";
        StatusColor = Brushes.Orange;
    }
    private bool CanBeginOverride() => AvailableProfiles.Count > 0;
    [RelayCommand(CanExecute = nameof(CanApplyOverride))]
    private void ApplyOverride()
    {
        var profile = OverrideProfile;
        if (profile == null)
        {
            return;
        }
        SelectedProfile = profile;
        DetectedSourceDisplay = profile.Name;
        IsSourceConfirmed = true;
        RequiresProfileSetup = false;
        ProcessingStatus = $"Data source overridden to {profile.Name}";
        StatusColor = Brushes.LightGray;
        AddLogEntry($"Data source overridden to {profile.Name}", LogLevel.Info);
        IsOverrideMode = false;
    }
    private bool CanApplyOverride() => OverrideProfile != null;
    [RelayCommand(CanExecute = nameof(CanCancelOverride))]
    private void CancelOverride()
    {
        IsOverrideMode = false;
        ProcessingStatus = "Review detected data source.";
        OverrideProfile = null;
        StatusColor = Brushes.LightGray;
    }
    private bool CanCancelOverride() => IsOverrideMode;
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
        if (SelectedProfile is { } profile)
        {
            var incompleteMappings = (profile.Mappings ?? Enumerable.Empty<ProfileMapping>())
                .Where(m => string.IsNullOrWhiteSpace(m.SourceField) || string.IsNullOrWhiteSpace(m.HubSpotHeader))
                .ToList();
            if (incompleteMappings.Count > 0)
            {
                var preview = string.Join(", ", incompleteMappings
                    .Select(m => string.IsNullOrWhiteSpace(m.SourceField) ? "(source column not set)" : m.SourceField.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5));
                var message = new StringBuilder()
                    .AppendLine("Finish setting your data profile before processing:")
                    .AppendLine(string.IsNullOrWhiteSpace(preview)
                        ? "- Specify both Source Field and HubSpot Header for each mapping"
                        : $"- Specify HubSpot Header for {incompleteMappings.Count} mapping(s) (e.g. {preview})")
                    .ToString();
                await _dialogService.ShowMessageAsync("Profile Incomplete", message);
                return;
            }
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
            _nextProgressMilestoneIndex = 0;
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
                var entryLevel = p.Severity switch
                {
                    ProcessingProgressSeverity.Warning => LogLevel.Warning,
                    ProcessingProgressSeverity.Error => LogLevel.Error,
                    _ => LogLevel.Info
                };
                if (entryLevel != LogLevel.Info)
                {
                    AddLogEntry(p.Message, entryLevel);
                    return;
                }
                while (_nextProgressMilestoneIndex < _progressMilestones.Length &&
                       p.PercentComplete >= _progressMilestones[_nextProgressMilestoneIndex])
                {
                    var milestone = _progressMilestones[_nextProgressMilestoneIndex];
                    AddLogEntry($"Processing {milestone}% complete", LogLevel.Info);
                    _nextProgressMilestoneIndex++;
                }
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
            var processor = new UnifiedProcessor(SelectedProfile, inputReader, progress);
            var options = new ProcessingOptions
            {
                OutputCsv = OutputCsv,
                OutputExcel = OutputExcel,
                OutputJson = OutputJson,
                Tag = string.IsNullOrWhiteSpace(AcceptedTag) ? null : AcceptedTag
            };
            var selectedOutputs = new List<string>();
            if (options.OutputCsv) selectedOutputs.Add("CSV");
            if (options.OutputExcel) selectedOutputs.Add("Excel");
            if (options.OutputJson) selectedOutputs.Add("JSON");
            WriteRunLog($"Outputs: {string.Join(", ", selectedOutputs)}");
            WriteRunLog($"Tag: {options.Tag ?? "(none)"}");
            AddLogEntry($"Tag applied: {options.Tag ?? "(none)"}", LogLevel.Info);
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
    [RelayCommand(CanExecute = nameof(CanGenerateTag))]
    private void GenerateTag()
    {
        if (!TagDataDate.HasValue || SelectedProfile == null)
        {
            return;
        }
        
        var formattedDate = TagDataDate.Value.ToString("yy.MM.dd", CultureInfo.InvariantCulture);
        var details = new List<string>();
        
        // Add data source from Property or Contact groups
        var dataSource = GetDataSourceFromProfile(SelectedProfile);
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            details.Add(dataSource);
        }
        
        var suffix = string.Join(" ", details.Where(part => !string.IsNullOrWhiteSpace(part)));
        var tag = string.IsNullOrWhiteSpace(suffix)
            ? formattedDate
            : $"{formattedDate} - {suffix}";
            
        _isUpdatingTagDraft = true;
        TagDraft = tag.Trim();
        _isUpdatingTagDraft = false;
        TagAccepted = false;
        TagStatus = "Tag generated. Click Accept to apply.";
        AcceptTagCommand.NotifyCanExecuteChanged();
    }
    private bool CanGenerateTag() => TagDataDate.HasValue && SelectedProfile != null;

    private string GetDataSourceFromProfile(Profile profile)
    {
        // Check Property groups first (usually Group 1)
        if (profile.Groups.PropertyGroups.Count > 0)
        {
            foreach (var propertyGroup in profile.Groups.PropertyGroups.Values)
            {
                if (!string.IsNullOrWhiteSpace(propertyGroup.DataSource))
                {
                    return propertyGroup.DataSource.Trim();
                }
            }
        }
        
        // Check Contact groups as fallback
        if (profile.Groups.ContactGroups.Count > 0)
        {
            foreach (var contactGroup in profile.Groups.ContactGroups.Values)
            {
                if (!string.IsNullOrWhiteSpace(contactGroup.DataSource))
                {
                    return contactGroup.DataSource.Trim();
                }
            }
        }
        
        return string.Empty;
    }
    [RelayCommand(CanExecute = nameof(CanAcceptTag))]
    private void AcceptTag()
    {
        var normalized = NormalizeTagText(TagDraft);
        _isUpdatingTagDraft = true;
        TagDraft = normalized;
        _isUpdatingTagDraft = false;
        AcceptedTag = normalized;
        TagAccepted = !string.IsNullOrWhiteSpace(normalized);
        TagStatus = TagAccepted
            ? $"Tag accepted: {AcceptedTag}"
            : "Tag cleared.";
        var logMessage = TagAccepted
            ? $"Tag accepted: {AcceptedTag}"
            : "Tag cleared.";
        AddLogEntry(logMessage, TagAccepted ? LogLevel.Info : LogLevel.Warning);
        AcceptTagCommand.NotifyCanExecuteChanged();
    }
    private bool CanAcceptTag() => !string.IsNullOrWhiteSpace(TagDraft);
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
    private static string NormalizeTagText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).Trim();
    }
    private void ResetTagState()
    {
        _isUpdatingTagDraft = true;
        TagDraft = string.Empty;
        _isUpdatingTagDraft = false;
        AcceptedTag = string.Empty;
        TagAccepted = false;
        TagStatus = "No tag generated";
        AcceptTagCommand.NotifyCanExecuteChanged();
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
                            !RequiresProfileSetup &&
                            IsSourceConfirmed &&
                            TagAccepted &&
                            (OutputCsv || OutputExcel || OutputJson);
    }
    partial void OnOutputCsvChanged(bool value)
    {
        _appSession.OutputCsv = value;
        UpdateCanStartProcessing();
    }
    partial void OnOutputExcelChanged(bool value)
    {
        _appSession.OutputExcel = value;
        UpdateCanStartProcessing();
    }
    partial void OnOutputJsonChanged(bool value)
    {
        _appSession.OutputJson = value;
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
        ResetTagState();
        GenerateTagCommand.NotifyCanExecuteChanged();
        if (value != null && InputFilePath != "No file selected")
        {
            DetectedSourceDisplay = value.Name;
            RequiresProfileSetup = false;
        }
        else if (value == null && InputFilePath == "No file selected")
        {
            DetectedSourceDisplay = "Upload a file to detect the data source";
        }
        if (value == null)
        {
            IsSourceConfirmed = false;
            IsOverrideMode = false;
        }
        RefreshSourceActionState();
        CancelOverrideCommand.NotifyCanExecuteChanged();
        if (!_updatingFromSession && value != null)
        {
            _appSession.SelectedProfile = value;
            AddLogEntry($"Selected data profile: {value.Name}", LogLevel.Info);
        }
        UpdateCanStartProcessing();
        AcceptTagCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsProcessingChanged(bool value)
    {
        UpdateCanStartProcessing();
        OpenRunLogCommand.NotifyCanExecuteChanged();
        CopyDiagnosticsCommand.NotifyCanExecuteChanged();
    }
    partial void OnInputFilePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "No file selected")
        {
            IsSourceConfirmed = false;
            IsOverrideMode = false;
            RequiresProfileSetup = false;
        }
        RefreshSourceActionState();
        CancelOverrideCommand.NotifyCanExecuteChanged();
    }
    partial void OnTagDataDateChanged(DateTime? value)
    {
        TagAccepted = false;
        GenerateTagCommand.NotifyCanExecuteChanged();
        if (!_isUpdatingTagDraft && !string.IsNullOrWhiteSpace(TagDraft))
        {
            TagStatus = "Tag date changed. Generate tags to update.";
        }
    }
    partial void OnTagDraftChanged(string value)
    {
        AcceptTagCommand.NotifyCanExecuteChanged();
        if (_isUpdatingTagDraft)
        {
            return;
        }
        TagAccepted = false;
        TagStatus = string.IsNullOrWhiteSpace(value)
            ? "No tag generated"
            : "Tag edited. Click Accept to apply.";
    }
    partial void OnOverrideProfileChanged(Profile? value)
    {
        ApplyOverrideCommand.NotifyCanExecuteChanged();
    }
    partial void OnRequiresProfileSetupChanged(bool value)
    {
        UpdateCanStartProcessing();
    }
    partial void OnTagAcceptedChanged(bool value)
    {
        UpdateCanStartProcessing();
    }
    partial void OnIsSourceConfirmedChanged(bool value)
    {
        UpdateCanStartProcessing();
        AcceptDetectedSourceCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsOverrideModeChanged(bool value)
    {
        if (!value)
        {
            OverrideProfile = null;
        }
        RefreshSourceActionState();
        CancelOverrideCommand.NotifyCanExecuteChanged();
    }
    partial void OnShowSourceActionsChanged(bool value)
    {
        BeginOverrideCommand.NotifyCanExecuteChanged();
    }
    private void RefreshSourceActionState()
    {
        var hasFile = !string.IsNullOrWhiteSpace(InputFilePath) && InputFilePath != "No file selected";
        ShowSourceActions = hasFile && !RequiresProfileSetup && (SelectedProfile != null || AvailableProfiles.Count > 0);
        AcceptDetectedSourceCommand.NotifyCanExecuteChanged();
        BeginOverrideCommand.NotifyCanExecuteChanged();
        ApplyOverrideCommand.NotifyCanExecuteChanged();
        CancelOverrideCommand.NotifyCanExecuteChanged();
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

