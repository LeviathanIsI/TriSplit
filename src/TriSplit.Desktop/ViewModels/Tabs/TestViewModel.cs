using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Desktop.Models;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.ViewModels.Tabs;

public partial class TestViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly ISampleLoader _sampleLoader;
    private readonly IAppSession _appSession;
    private const int PREVIEW_ROW_LIMIT = 20;
    private const int PREVIEW_COL_LIMIT = 100;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _transformCts;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _transformGate = new(1, 1);
    private int _loadCount;
    private int _transformCount;

    [ObservableProperty]
    private string _selectedFileName = string.Empty;

    [ObservableProperty]
    private bool _isFileSelected;

    [ObservableProperty]
    private bool _isFileLoaded;

    [ObservableProperty]
    private bool _isDataEmpty = true;

    [ObservableProperty]
    private int _rowCount;

    [ObservableProperty]
    private int _columnCount;

    [ObservableProperty]
    private int _previewRowCount;

    [ObservableProperty]
    private ObservableCollection<ColumnMappingStatus> _columnMappings = new();

    [ObservableProperty]
    private int _estimatedContacts;

    [ObservableProperty]
    private int _estimatedProperties;

    [ObservableProperty]
    private int _estimatedPhoneNumbers;

    [ObservableProperty]
    private string _processingTimeEstimate = "--";

    [ObservableProperty]
    private int _mappedColumnsCount;

    [ObservableProperty]
    private int _unmappedColumnsCount;

    [ObservableProperty]
    private string _activeProfileName = "No data profile selected";

    [ObservableProperty]
    private bool _showOriginalData = true;

    [ObservableProperty]
    private bool _showTransformedData;

    [ObservableProperty]
    private DataTable? _previewData;

    [ObservableProperty]
    private string _testStatus = "Ready to test";

    private string? _currentFilePath;
    private DataTable? _originalData;
    private DataTable? _transformedData;

    public TestViewModel(
        IDialogService dialogService,
        ISampleLoader sampleLoader,
        IAppSession appSession)
    {
        _dialogService = dialogService;
        _sampleLoader = sampleLoader;
        _appSession = appSession;

        // Subscribe to profile changes
        _appSession.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IAppSession.SelectedProfile))
            {
                UpdateActiveProfile();
                if (IsFileLoaded)
                {
                    UpdateColumnMappingStatus();
                    UpdateStatistics();
                }
            }
            else if (e.PropertyName == nameof(IAppSession.LoadedFilePath))
            {
                if (!string.IsNullOrEmpty(_appSession.LoadedFilePath))
                {
                    _currentFilePath = _appSession.LoadedFilePath;
                    SelectedFileName = Path.GetFileName(_currentFilePath);
                    IsFileSelected = true;
                    _ = UploadAndTestAsync();
                }
            }
        };

        UpdateActiveProfile();
    }

    private void UpdateActiveProfile()
    {
        ActiveProfileName = _appSession.SelectedProfile?.Name ?? "No data profile selected";
        if (IsFileLoaded && _appSession.SelectedProfile != null)
        {
            _ = ApplyTransformationAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseFilesAsync()
    {
        var filePath = await _dialogService.ShowOpenFileDialogAsync(
            "Select Test File",
            "CSV Files|*.csv|Excel Files|*.xlsx;*.xls|All Files|*.*");

        if (!string.IsNullOrEmpty(filePath))
        {
            _currentFilePath = filePath;
            SelectedFileName = Path.GetFileName(filePath);
            IsFileSelected = true;
            TestStatus = "File selected, ready to upload";
        }
    }

    [RelayCommand]
    private async Task UploadAndTestAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath))
            return;

        var opId = System.Threading.Interlocked.Increment(ref _loadCount);

        // Cancel any existing load operation
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        // Try to acquire the load gate
        if (!await _loadGate.WaitAsync(0, ct))
        {
            TestStatus = "Load already in progress...";
            return;
        }

        try
        {
            TestStatus = "Loading file...";

            // Load only preview data for display - OFF UI THREAD
            var sampleData = await Task.Run(async () =>
                await _sampleLoader.LoadSampleWithLimitAsync(_currentFilePath, PREVIEW_ROW_LIMIT), ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            // Build DataTable OFF UI THREAD
            var dataTable = await Task.Run(() =>
            {
                var dt = new DataTable();

                // Add columns (limit to prevent UI freeze with wide sheets)
                var mappedHeaders = new List<(string Original, string Display)>();
                var usedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var limitedHeaders = sampleData.Headers.Take(PREVIEW_COL_LIMIT).ToList();
                for (var index = 0; index < limitedHeaders.Count; index++)
                {
                    var original = limitedHeaders[index];
                    var display = string.IsNullOrWhiteSpace(original)
                        ? $"Column {index + 1}"
                        : original.Trim();

                    if (string.IsNullOrWhiteSpace(display))
                    {
                        display = $"Column {index + 1}";
                    }

                    var baseName = display;
                    var suffix = 1;
                    while (!usedColumnNames.Add(display))
                    {
                        display = $"{baseName}_{suffix++}";
                    }

                    dt.Columns.Add(display);
                    mappedHeaders.Add((original, display));
                }

                // Add rows
                foreach (var row in sampleData.Rows)
                {
                    var dataRow = dt.NewRow();
                    foreach (var (Original, Display) in mappedHeaders)
                    {
                        if (!string.IsNullOrEmpty(Original) && row.TryGetValue(Original, out var value))
                        {
                            dataRow[Display] = value?.ToString() ?? string.Empty;
                        }
                        else
                        {
                            dataRow[Display] = string.Empty;
                        }
                    }
                    dt.Rows.Add(dataRow);
                }

                return dt;
            }, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            // Update UI on UI thread
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _originalData = dataTable;
                PreviewData = _originalData;
                RowCount = sampleData.TotalRows;
                PreviewRowCount = Math.Min(PREVIEW_ROW_LIMIT, RowCount);
                ColumnCount = sampleData.Headers.Count;
                IsFileLoaded = true;
                IsDataEmpty = false;

                // Update shared session
                _appSession.LoadedFilePath = _currentFilePath;

                TestStatus = $"Loaded {RowCount:N0} rows, {ColumnCount} columns (showing first {PreviewRowCount})";

                // Update mapping status and statistics
                UpdateColumnMappingStatus();
                UpdateStatistics();
            }, System.Windows.Threading.DispatcherPriority.Background);

            // Apply transformation if profile is selected
            if (_appSession.SelectedProfile != null)
            {
                await ApplyTransformationAsync();
            }
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TestStatus = $"Error: {ex.Message}";
            });
            await _dialogService.ShowMessageAsync("Error", $"Failed to load file: {ex.Message}");
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task ApplyTransformationAsync()
    {
        var opId = System.Threading.Interlocked.Increment(ref _transformCount);

        // Cancel any existing transform
        _transformCts?.Cancel();
        _transformCts = new CancellationTokenSource();
        var ct = _transformCts.Token;

        // Try to acquire the transform gate
        if (!await _transformGate.WaitAsync(0, ct))
        {
            TestStatus = "Transformation already in progress...";
            return;
        }

        try
        {
            var original = _originalData;
            var profile = _appSession.SelectedProfile;
            if (original == null || profile == null)
            {
                return;
            }

            TestStatus = "Applying transformation...";

            var transformed = await Task.Run(() =>
            {
                // Create transformed DataTable off-thread
                var dt = new DataTable();

                // Map columns based on profile
                var allMappings = profile.ContactMappings
                    .Concat(profile.PropertyMappings)
                    .Concat(profile.PhoneMappings)
                    .Take(PREVIEW_COL_LIMIT) // Limit columns for preview
                    .ToList();

                // Add HubSpot columns
                var addedColumns = new HashSet<string>();
                foreach (var mapping in allMappings)
                {
                    if (!string.IsNullOrWhiteSpace(mapping.HubSpotProperty) && !addedColumns.Contains(mapping.HubSpotProperty))
                    {
                        dt.Columns.Add(mapping.HubSpotProperty);
                        addedColumns.Add(mapping.HubSpotProperty);
                    }
                }

                // Add association type column
                dt.Columns.Add("Association Type");

                // Transform rows
                foreach (DataRow originalRow in original.Rows)
                {
                    if (ct.IsCancellationRequested) return null;

                    var transformedRow = dt.NewRow();

                    foreach (var mapping in allMappings)
                    {
                        if (original.Columns.Contains(mapping.SourceColumn) &&
                            dt.Columns.Contains(mapping.HubSpotProperty))
                        {
                            transformedRow[mapping.HubSpotProperty] = originalRow[mapping.SourceColumn];
                        }
                    }

                    // Set association type based on first matching mapping
                    var firstMapping = allMappings.FirstOrDefault();
                    if (firstMapping != null)
                    {
                        transformedRow["Association Type"] = firstMapping.AssociationType;
                    }

                    dt.Rows.Add(transformedRow);
                }

                return dt;
            }, ct);

            if (!ct.IsCancellationRequested && transformed != null)
            {
                // Update UI on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _transformedData = transformed;
                    TestStatus = $"Transformation complete - {profile.ContactMappings.Count + profile.PropertyMappings.Count + profile.PhoneMappings.Count} mappings applied";

                    if (ShowTransformedData)
                    {
                        PreviewData = _transformedData;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TestStatus = $"Transformation error: {ex.Message}";
            });
        }
        finally
        {
            _transformGate.Release();
        }
    }

    partial void OnShowOriginalDataChanged(bool value)
    {
        if (value && _originalData != null)
        {
            PreviewData = _originalData;
            ShowTransformedData = false;
        }
    }

    partial void OnShowTransformedDataChanged(bool value)
    {
        if (value)
        {
            ShowOriginalData = false;
            if (_transformedData != null)
            {
                PreviewData = _transformedData;
            }
            else if (_appSession.SelectedProfile != null)
            {
                _ = ApplyTransformationAsync();
            }
            else
            {
                TestStatus = "Please select a data profile to view transformed data";
                ShowOriginalData = true;
            }
        }
    }

    [RelayCommand]
    private async Task ExportPreviewAsync()
    {
        if (PreviewData == null)
            return;

        var fileName = ShowTransformedData ? "transformed_preview.csv" : "original_preview.csv";
        var filePath = await _dialogService.ShowSaveFileDialogAsync(
            "Export Preview",
            "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            fileName);

        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            // Export DataTable to CSV
            using var writer = new StreamWriter(filePath);

            // Write headers
            var headers = PreviewData.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
            await writer.WriteLineAsync(string.Join(",", headers));

            // Write rows
            foreach (DataRow row in PreviewData.Rows)
            {
                var values = row.ItemArray.Select(v => $"\"{v?.ToString() ?? string.Empty}\"");
                await writer.WriteLineAsync(string.Join(",", values));
            }

            TestStatus = $"Preview exported to {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            TestStatus = $"Export error: {ex.Message}";
            await _dialogService.ShowMessageAsync("Error", $"Failed to export: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RunFullProcessingAsync()
    {
        if (_currentFilePath == null || _appSession.SelectedProfile == null)
        {
            await _dialogService.ShowMessageAsync("Error", "Please select a file and data profile first");
            return;
        }

        var result = await _dialogService.ShowConfirmationDialogAsync(
            "Run Full Processing",
            $"This will process the entire file '{Path.GetFileName(_currentFilePath)}' using data profile '{_appSession.SelectedProfile.Name}'.\n\nContinue?");

        if (result)
        {
            TestStatus = "Switching to Processing tab...";
            // This would trigger navigation to the Processing tab
            // Implementation depends on how navigation is handled in the main window
        }
    }

    private void UpdateColumnMappingStatus()
    {
        if (_originalData == null || _appSession.SelectedProfile == null)
        {
            ColumnMappings.Clear();
            return;
        }

        ColumnMappings.Clear();
        var profile = _appSession.SelectedProfile;
        var allMappings = profile.ContactMappings
            .Concat(profile.PropertyMappings)
            .Concat(profile.PhoneMappings)
            .ToList();

        var mappedColumns = new HashSet<string>(allMappings.Select(m => m.SourceColumn));
        var criticalColumns = new HashSet<string> { "First Name", "Last Name", "Email", "Address", "Phone 1 Number" };

        foreach (DataColumn column in _originalData.Columns)
        {
            var status = new ColumnMappingStatus
            {
                ColumnName = column.ColumnName,
                IsMapped = mappedColumns.Contains(column.ColumnName),
                IsCritical = criticalColumns.Contains(column.ColumnName)
            };

            if (status.IsMapped)
            {
                var mapping = allMappings.First(m => m.SourceColumn == column.ColumnName);
                status.MappedTo = mapping.HubSpotProperty;
                status.AssociationType = mapping.AssociationType;
            }

            ColumnMappings.Add(status);
        }

        MappedColumnsCount = ColumnMappings.Count(c => c.IsMapped);
        UnmappedColumnsCount = ColumnMappings.Count(c => !c.IsMapped);
    }

    private void UpdateStatistics()
    {
        if (!IsFileLoaded || _appSession.SelectedProfile == null)
        {
            EstimatedContacts = 0;
            EstimatedProperties = 0;
            EstimatedPhoneNumbers = 0;
            ProcessingTimeEstimate = "--";
            return;
        }

        var profile = _appSession.SelectedProfile;

        // Estimate contacts (unique by association type)
        EstimatedContacts = RowCount * profile.ContactMappings.Select(m => m.AssociationType).Distinct().Count();

        // Estimate properties
        EstimatedProperties = RowCount * profile.PropertyMappings.Count;

        // Estimate phone numbers
        EstimatedPhoneNumbers = RowCount * profile.PhoneMappings.Count;

        // Estimate processing time (rough: 100 rows/second)
        var totalOperations = EstimatedContacts + EstimatedProperties + EstimatedPhoneNumbers;
        var estimatedSeconds = totalOperations / 100.0;

        if (estimatedSeconds < 60)
            ProcessingTimeEstimate = $"{estimatedSeconds:F0} seconds";
        else if (estimatedSeconds < 3600)
            ProcessingTimeEstimate = $"{estimatedSeconds / 60:F1} minutes";
        else
            ProcessingTimeEstimate = $"{estimatedSeconds / 3600:F1} hours";
    }
}

public partial class ColumnMappingStatus : ObservableObject
{
    [ObservableProperty]
    private string _columnName = string.Empty;

    [ObservableProperty]
    private bool _isMapped;

    [ObservableProperty]
    private bool _isCritical;

    [ObservableProperty]
    private string? _mappedTo;

    [ObservableProperty]
    private string? _associationType;
}