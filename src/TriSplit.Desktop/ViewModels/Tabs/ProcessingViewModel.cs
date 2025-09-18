using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

    private CancellationTokenSource? _cancellationTokenSource;
    private bool _updatingFromSession = false;

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

    private string? _outputDirectory;

    public ProcessingViewModel(
        IDialogService dialogService,
        ISampleLoader sampleLoader,
        IAppSession appSession,
        IProfileStore profileStore)
    {
        _dialogService = dialogService;
        _sampleLoader = sampleLoader;
        _appSession = appSession;
        _profileStore = profileStore;
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
                    InputFilePath = _appSession.LoadedFilePath;
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
            AddLogEntry($"Error loading profiles: {ex.Message}", LogLevel.Error);
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
            await _dialogService.ShowMessageAsync("Error", "Please select a file and profile first");
            return;
        }

        try
        {
            IsProcessing = true;
            ProcessingProgress = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

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

            // Create processor using factory
            var processor = ProcessorFactory.CreateProcessor(
                SelectedProfile,
                inputReader,
                progress,
                "tlo" // Force TLO processor for now
            );

            // Process the file and generate three output files with Import ID linking
            var result = await processor.ProcessAsync(InputFilePath, _outputDirectory, token);

            if (token.IsCancellationRequested) return;

            // Show processing results
            if (result.Success)
            {
                ProcessingProgress = 100;
                ProgressText = "Processing complete!";
                ProcessingStatus = "Completed successfully";
                StatusColor = Brushes.LimeGreen;
                HasOutput = true;

                AddLogEntry($"Processing complete!", LogLevel.Success);
                AddLogEntry($"  Total records processed: {result.TotalRecordsProcessed}", LogLevel.Success);
                AddLogEntry($"  Contacts created: {result.ContactsCreated}", LogLevel.Success);
                AddLogEntry($"  Properties created: {result.PropertiesCreated}", LogLevel.Success);
                AddLogEntry($"  Phone numbers created: {result.PhonesCreated}", LogLevel.Success);
                AddLogEntry($"", LogLevel.Info);
                AddLogEntry($"Output files (Import ID linked):", LogLevel.Info);
                AddLogEntry($"  1. {Path.GetFileName(result.ContactsFile)}", LogLevel.Info);
                AddLogEntry($"  2. {Path.GetFileName(result.PhonesFile)}", LogLevel.Info);
                AddLogEntry($"  3. {Path.GetFileName(result.PropertiesFile)}", LogLevel.Info);
            }
            else
            {
                ProcessingProgress = 100;
                ProgressText = "Processing failed";
                ProcessingStatus = $"Failed: {result.ErrorMessage}";
                StatusColor = Brushes.Red;
                HasOutput = false;

                AddLogEntry($"Processing failed: {result.ErrorMessage}", LogLevel.Error);
            }

            AddLogEntry($"Processing completed successfully in {_outputDirectory}", LogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            ProcessingStatus = "Cancelled";
            StatusColor = Brushes.Orange;
            AddLogEntry("Processing cancelled by user", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            ProcessingStatus = "Failed";
            StatusColor = Brushes.Red;
            AddLogEntry($"Processing failed: {ex.Message}", LogLevel.Error);
            await _dialogService.ShowMessageAsync("Error", $"Processing failed: {ex.Message}");
        }
        finally
        {
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

    private void UpdateCanStartProcessing()
    {
        CanStartProcessing = !IsProcessing &&
                            !string.IsNullOrEmpty(InputFilePath) &&
                            InputFilePath != "No file selected" &&
                            SelectedProfile != null;
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        UpdateCanStartProcessing();

        // Only update session and log if this change didn't come from the session
        if (!_updatingFromSession && value != null)
        {
            _appSession.SelectedProfile = value;
            AddLogEntry($"Selected profile: {value.Name}", LogLevel.Info);
        }
    }

    partial void OnIsProcessingChanged(bool value)
    {
        UpdateCanStartProcessing();
    }

    private void AddLogEntry(string message, LogLevel level)
    {
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