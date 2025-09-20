using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Desktop.Models;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.ViewModels.Tabs;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileStore _profileStore;
    private readonly IDialogService _dialogService;
    private readonly IAppSession _appSession;

    private readonly HashSet<FieldMappingViewModel> _trackedMappings = new();
    private bool _isUpdatingMappingState;
    private bool _isLoadingProfile;
    private readonly TimeSpan _autosaveDelay = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _autosaveTimer;
    private bool _isAutosaving;
    private int _remainingAutosaveTime;



    private int _blockSelectionStartIndex = -1;
    private int _blockSelectionEndIndex = -1;
    private readonly List<MappingBlockSnapshot> _blockClipboard = new();
#if DEBUG
    private static readonly object BlockLogSync = new();
    private static readonly string BlockLogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TriSplit", "Debug");
    private static readonly string BlockLogPath = Path.Combine(BlockLogDirectory, "block_selection.log");
#endif

    [ObservableProperty]
    private bool _hasBlockSelection;

    [ObservableProperty]
    private int _blockSelectionCount;

    [ObservableProperty]
    private string _blockSelectionSummary = string.Empty;

    [ObservableProperty]
    private bool _hasBlockClipboard;

    [ObservableProperty]
    private bool _canPasteBlock;
    public ObservableCollection<string> AssociationLabels { get; }
    public ObservableCollection<string> HubSpotHeaders { get; }
    public ObservableCollection<string> ObjectTypes { get; }

    [ObservableProperty]
    private string _profileName = "New Data Profile";

    partial void OnProfileNameChanged(string value)
    {
        if (_isLoadingProfile)
            return;

        MarkDirty();
    }

    [ObservableProperty]
    private ObservableCollection<ProfileViewModel> _savedProfiles = new();

    [ObservableProperty]
    private ProfileViewModel? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<FieldMappingViewModel> _fieldMappings = new();

    [ObservableProperty]
    private string _profileStatus = "No data profile loaded";

    [ObservableProperty]
    private string _mappingCount = "0 mappings configured";

    [ObservableProperty]
    private bool _hasDuplicateMappings;

    [ObservableProperty]
    private string _duplicateWarning = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _autosaveStatus = string.Empty;




    public ProfilesViewModel(
        IProfileStore profileStore,
        IDialogService dialogService,
        IAppSession appSession)
    {
        _profileStore = profileStore;
        _dialogService = dialogService;
        _appSession = appSession;

        FieldMappings = new ObservableCollection<FieldMappingViewModel>();

        // Initialize Association Labels
        AssociationLabels = new ObservableCollection<string>
        {
            string.Empty,
            "Owner",
            "Executor",
            "Mailing Address",
            "Relative",
            "Associate",
        };

        // Initialize HubSpot Headers (alphabetically sorted)
        HubSpotHeaders = new ObservableCollection<string>
        {
            "Address",
            "APN",
            "Association Label",
            "Attorney Address",
            "Attorney City",
            "Attorney Company",
            "Attorney First Name",
            "Attorney Last Name",
            "Attorney Phone",
            "Attorney Postal Code",
            "Attorney State",
            "Bankruptcy Filing Date",
            "Bathrooms",
            "Bedrooms",
            "City",
            "Company Name",
            "Contact Data",
            "County",
            "Date of Death",
            "Deceased First Name",
            "Deceased Last Name",
            "Email",
            "Estimated Equity",
            "Estimated Loan to Value",
            "Estimated Remaining Balance of Open Loans",
            "Estimated Value",
            "First Name",
            "Foreclosure Factor",
            "Last Name",
            "Last Sale Date",
            "Last Sale Price",
            "Lien Amount",
            "Lot Size",
            "MLS Amount",
            "MLS Date",
            "MLS Status",
            "Open Mortgages",
            "Owner Deceased",
            "Owner Occupied",
            "Parcel Number",
            "Phone Number",
            "Phone Type",
            "Postal Code",
            "Probate Date",
            "Property Condition",
            "Property Type",
            "Square Footage",
            "State",
            "Taxes Actionable",
            "Taxes Delinquent Amount",
            "Taxes Delinquent Date",
            "Year Built"
        };

        ObjectTypes = new ObservableCollection<string>
        {
            string.Empty,
            MappingObjectTypes.Contact,
            MappingObjectTypes.PhoneNumber,
            MappingObjectTypes.Property
        };

        _isLoadingProfile = true;
        InitializeMappings();
        _isLoadingProfile = false;
        _ = LoadProfilesAsync();
    }

    private void InitializeMappings()
    {
        // Start with a few empty mapping rows
        for (int i = 0; i < 3; i++)
        {
            FieldMappings.Add(new FieldMappingViewModel());
        }

        UpdateMappingCount();
    }

    private void MarkDirty()
    {
        if (_isUpdatingMappingState || _isLoadingProfile)
            return;

        if (!IsDirty)
        {
            IsDirty = true;
        }

        UpdateAutosaveStatus();
        ScheduleAutosave();
    }

    private void ScheduleAutosave()
    {
        if (_isLoadingProfile || _isAutosaving)
            return;

        _autosaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        _autosaveTimer.Stop();
        _autosaveTimer.Tick -= OnAutosaveTimerTick;
        _autosaveTimer.Tick += OnAutosaveTimerTick;
        _remainingAutosaveTime = (int)_autosaveDelay.TotalSeconds;
        UpdateAutosaveStatus();
        _autosaveTimer.Start();
    }

    private async void OnAutosaveTimerTick(object? sender, EventArgs e)
    {
        if (!IsDirty || SelectedProfile == null || _isAutosaving)
        {
            _autosaveTimer?.Stop();
            UpdateAutosaveStatus(clearIfClean: true);
            return;
        }

        _remainingAutosaveTime--;

        if (_remainingAutosaveTime > 0)
        {
            UpdateAutosaveStatus();
            return;
        }

        _autosaveTimer?.Stop();

        _isAutosaving = true;
        try
        {
            await SaveProfileInternalAsync(isAutoSave: true);
        }
        finally
        {
            _isAutosaving = false;
        }
    }

private void UpdateAutosaveStatus(bool clearIfClean = false)
{
    if (!IsDirty)
    {
        AutosaveStatus = clearIfClean ? string.Empty : AutosaveStatus;
        return;
    }

    if (_remainingAutosaveTime > 0)
    {
        AutosaveStatus = $"*Unsaved Changes* Auto-Saving in {_remainingAutosaveTime}...";
    }
    else if (_isAutosaving)
    {
        AutosaveStatus = "*Auto-Saving...*";
    }
}

    private void UpdateMappingCount()
    {
        if (_isUpdatingMappingState)
            return;

        _isUpdatingMappingState = true;
        try
        {
            var populatedMappings = FieldMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.SourceField))
            .Select(m => new { Mapping = m, Key = m.SourceField!.Trim() })
            .ToList();

        var count = populatedMappings.Count;
        MappingCount = $"{count} mapping{(count == 1 ? string.Empty : "s")} configured";

        foreach (var mapping in FieldMappings)
        {
            mapping.IsDuplicate = false;
        }

        var duplicateGroups = populatedMappings
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        var duplicates = duplicateGroups
            .SelectMany(g => g.Select(x => x.Mapping))
            .ToHashSet();

        foreach (var mapping in duplicates)
        {
            mapping.IsDuplicate = true;
        }

        HasDuplicateMappings = duplicateGroups.Count > 0;
        DuplicateWarning = HasDuplicateMappings
            ? $"Duplicate source columns: {string.Join(", ", duplicateGroups.Select(g => g.Key).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}"
            : string.Empty;
    }
    finally
    {
        _isUpdatingMappingState = false;
    }
}

    public void BeginBlockSelection(int anchorIndex)
    {
        if (anchorIndex < 0 || anchorIndex >= FieldMappings.Count)
            return;

        ClearBlockSelectionMarkers();
        LogBlockAction($"BeginBlockSelection: anchor={anchorIndex}");

        _blockSelectionStartIndex = anchorIndex;
        _blockSelectionEndIndex = anchorIndex;
        ApplyBlockSelectionRange(anchorIndex, anchorIndex);
    }

    public void BeginBlockSelectionForPaste(int anchorIndex)
    {
        if (anchorIndex < 0 || anchorIndex >= FieldMappings.Count)
            return;

        if (_blockClipboard.Count <= 0)
        {
            BeginBlockSelection(anchorIndex);
            return;
        }

        ClearBlockSelectionMarkers();

        var endIndex = Math.Min(anchorIndex + _blockClipboard.Count - 1, FieldMappings.Count - 1);
        LogBlockAction($"BeginBlockSelectionForPaste: anchor={anchorIndex}, end={endIndex}, clipboard={_blockClipboard.Count}");

        _blockSelectionStartIndex = anchorIndex;
        _blockSelectionEndIndex = endIndex;
        ApplyBlockSelectionRange(anchorIndex, endIndex);
    }

    public void UpdateBlockSelection(int anchorIndex, int currentIndex)
    {
        if (_blockSelectionStartIndex == -1)
        {
            BeginBlockSelection(anchorIndex);
            return;
        }

        var clampedIndex = Math.Clamp(currentIndex, 0, FieldMappings.Count - 1);
        _blockSelectionEndIndex = clampedIndex;

        var start = Math.Min(_blockSelectionStartIndex, clampedIndex);
        var end = Math.Max(_blockSelectionStartIndex, clampedIndex);
        ApplyBlockSelectionRange(start, end);
        LogBlockAction($"UpdateBlockSelection: anchor={anchorIndex}, current={currentIndex}, range={start}-{end}");
    }

    public void CompleteBlockSelection()
    {
        LogBlockAction("CompleteBlockSelection");
        UpdatePasteAvailability();
    }

    private void ApplyBlockSelectionRange(int startIndex, int endIndex)
    {
        if (FieldMappings.Count == 0)
        {
            ClearBlockSelectionMarkers();
            LogBlockAction("ApplyBlockSelectionRange aborted: no mappings");
            return;
        }

        startIndex = Math.Clamp(startIndex, 0, FieldMappings.Count - 1);
        endIndex = Math.Clamp(endIndex, 0, FieldMappings.Count - 1);

        foreach (var mapping in FieldMappings)
        {
            mapping.IsBlockSelected = false;
        }

        for (int i = startIndex; i <= endIndex; i++)
        {
            FieldMappings[i].IsBlockSelected = true;
        }

        _blockSelectionStartIndex = startIndex;
        _blockSelectionEndIndex = endIndex;
        BlockSelectionCount = (endIndex - startIndex) + 1;
        HasBlockSelection = BlockSelectionCount > 0;

        BlockSelectionSummary = HasBlockSelection
            ? $"{BlockSelectionCount} row{(BlockSelectionCount == 1 ? string.Empty : "s")} selected"
            : string.Empty;

        LogBlockAction($"ApplyBlockSelectionRange: {startIndex}-{endIndex}, count={BlockSelectionCount}");
        UpdatePasteAvailability();
    }

    [RelayCommand]
    private void ClearBlockSelection()
    {
        ClearBlockSelectionMarkers();
        LogBlockAction("ClearBlockSelection command");
    }

    private void ClearBlockSelectionMarkers()
    {
        foreach (var mapping in FieldMappings)
        {
            mapping.IsBlockSelected = false;
        }

        _blockSelectionStartIndex = -1;
        _blockSelectionEndIndex = -1;
        BlockSelectionCount = 0;
        HasBlockSelection = false;
        BlockSelectionSummary = string.Empty;
        LogBlockAction("ClearBlockSelectionMarkers: state reset");
        UpdatePasteAvailability();
    }

    [RelayCommand]
    private void CopyBlockSelection()
    {
        var range = GetCurrentSelectionRange();
        if (range == null)
        {
            ProfileStatus = "Select rows before copying";
            LogBlockAction("CopyBlockSelection aborted: no selection");
            return;
        }

        _blockClipboard.Clear();

        for (int i = range.Value.Start; i <= range.Value.End; i++)
        {
            var mapping = FieldMappings[i];
            _blockClipboard.Add(new MappingBlockSnapshot(
                mapping.SourceField ?? string.Empty,
                mapping.AssociationLabel ?? string.Empty,
                mapping.ObjectType ?? string.Empty,
                mapping.HubSpotHeader ?? string.Empty));
        }

        HasBlockClipboard = _blockClipboard.Count > 0;
        LogBlockAction($"CopyBlockSelection: range={range.Value.Start}-{range.Value.End}, rows={_blockClipboard.Count}");
        UpdatePasteAvailability();

        if (HasBlockClipboard)
        {
            ProfileStatus = $"Copied {_blockClipboard.Count} row{(_blockClipboard.Count == 1 ? string.Empty : "s")}";
        }
    }

    [RelayCommand]
    private void PasteBlockSelection()
    {
        if (_blockClipboard.Count == 0)
        {
            ProfileStatus = "Copy a block before pasting";
            LogBlockAction("PasteBlockSelection aborted: clipboard empty");
            return;
        }

        var range = GetCurrentSelectionRange();
        if (range == null)
        {
            ProfileStatus = "Select target rows before pasting";
            LogBlockAction("PasteBlockSelection aborted: no selection");
            return;
        }

        var start = range.Value.Start;
        LogBlockAction($"PasteBlockSelection requested: start={start}, clipboardRows={_blockClipboard.Count}");

        for (int i = 0; i < _blockClipboard.Count; i++)
        {
            var snapshot = _blockClipboard[i];
            var clone = new FieldMappingViewModel
            {
                SourceField = snapshot.SourceField,
                AssociationLabel = snapshot.AssociationLabel,
                ObjectType = snapshot.ObjectType,
                HubSpotHeader = snapshot.HubSpotHeader
            };

            FieldMappings.Insert(start + i, clone);
        }

        var end = start + _blockClipboard.Count - 1;
        ApplyBlockSelectionRange(start, end);

        UpdateMappingCount();
        LogBlockAction($"PasteBlockSelection applied by insertion: range={start}-{end}, rows={_blockClipboard.Count}");
        ProfileStatus = $"Pasted {_blockClipboard.Count} row{(_blockClipboard.Count == 1 ? string.Empty : "s")}";
    }

    private (int Start, int End)? GetCurrentSelectionRange()
    {
        if (!HasBlockSelection || _blockSelectionStartIndex < 0 || _blockSelectionEndIndex < 0)
            return null;

        return (Math.Min(_blockSelectionStartIndex, _blockSelectionEndIndex), Math.Max(_blockSelectionStartIndex, _blockSelectionEndIndex));
    }

    private void UpdatePasteAvailability()
    {
        CanPasteBlock = HasBlockSelection && _blockClipboard.Count > 0;
        LogBlockAction($"UpdatePasteAvailability: CanPasteBlock={CanPasteBlock}, HasSelection={HasBlockSelection}, ClipboardCount={_blockClipboard.Count}, SelectionCount={BlockSelectionCount}");
    }

#if DEBUG
    private void LogBlockAction(string message)
    {
        try
        {
            lock (BlockLogSync)
            {
                Directory.CreateDirectory(BlockLogDirectory);
                File.AppendAllText(BlockLogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ignored
        }
    }
#else
    private void LogBlockAction(string message)
    {
    }
#endif

    private sealed record MappingBlockSnapshot(string SourceField, string AssociationLabel, string ObjectType, string HubSpotHeader);

    private void OnFieldMappingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var mapping in _trackedMappings.ToList())
            {
                mapping.PropertyChanged -= OnMappingPropertyChanged;
            }

            _trackedMappings.Clear();
            UpdateMappingCount();
            return;
        }

        if (e.OldItems != null)
        {
            foreach (FieldMappingViewModel mapping in e.OldItems.Cast<FieldMappingViewModel>())
            {
                UntrackMapping(mapping);
            }
        }

        if (e.NewItems != null)
        {
            foreach (FieldMappingViewModel mapping in e.NewItems.Cast<FieldMappingViewModel>())
            {
                TrackMapping(mapping);
            }
        }

        UpdateMappingCount();
        if (!_isUpdatingMappingState && !_isLoadingProfile)
        {
            MarkDirty();
        }
    }

    private void TrackMapping(FieldMappingViewModel mapping)
    {
        if (mapping == null)
            return;

        if (_trackedMappings.Add(mapping))
        {
            mapping.PropertyChanged += OnMappingPropertyChanged;
        }
    }

    private void UntrackMapping(FieldMappingViewModel mapping)
    {
        if (mapping == null)
            return;

        if (_trackedMappings.Remove(mapping))
        {
            mapping.PropertyChanged -= OnMappingPropertyChanged;
        }
    }

    [RelayCommand]
    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _profileStore.GetAllProfilesAsync();
            SavedProfiles.Clear();

            foreach (var profile in profiles)
            {
                SavedProfiles.Add(new ProfileViewModel
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Profile = profile
                });
            }

            ProfileStatus = SavedProfiles.Any() ? $"{SavedProfiles.Count} data profiles available" : "No data profiles saved";
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error loading data profiles: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        await SaveProfileInternalAsync(isAutoSave: false);
    }

    private async Task SaveProfileInternalAsync(bool isAutoSave)
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            if (!isAutoSave)
            {
                await _dialogService.ShowMessageAsync("Error", "Please enter a data profile name");
            }
            return;
        }

        try
        {
            Profile? profile = null;
            bool isUpdate = false;

            if (SelectedProfile != null && SelectedProfile.Profile.Id != Guid.Empty)
            {
                profile = SelectedProfile.Profile;
                isUpdate = true;
            }
            else if (!isAutoSave)
            {
                var existingByName = SavedProfiles.FirstOrDefault(p => p.Name.Equals(ProfileName, StringComparison.OrdinalIgnoreCase));
                if (existingByName != null)
                {
                    var overwrite = await _dialogService.ShowConfirmationDialogAsync(
                        "Overwrite Data Profile",
                        $"A data profile named '{ProfileName}' already exists. Do you want to overwrite it?");

                    if (!overwrite)
                        return;

                    profile = existingByName.Profile;
                    SelectedProfile = existingByName;
                    isUpdate = true;
                }
                else
                {
                    profile = new Profile
                    {
                        Id = Guid.NewGuid(),
                        Name = ProfileName,
                        ContactMappings = new List<FieldMapping>(),
                        PropertyMappings = new List<FieldMapping>(),
                        PhoneMappings = new List<FieldMapping>()
                    };
                }
            }

            if (profile == null)
            {
                // Autosave only supports existing profiles
                return;
            }

            profile.Name = ProfileName;
            profile.ContactMappings ??= new List<FieldMapping>();
            profile.PropertyMappings ??= new List<FieldMapping>();
            profile.PhoneMappings ??= new List<FieldMapping>();

            profile.ContactMappings.Clear();
            profile.PropertyMappings.Clear();
            profile.PhoneMappings.Clear();

            foreach (var mapping in FieldMappings.Where(m => !string.IsNullOrWhiteSpace(m.SourceField)))
            {
                var association = mapping.AssociationLabel?.Trim();
                var objectTypeValue = mapping.ObjectType?.Trim();
                var normalizedObjectType = string.IsNullOrWhiteSpace(objectTypeValue)
                    ? string.Empty
                    : MappingObjectTypes.Normalize(objectTypeValue);

                var fieldMapping = new FieldMapping
                {
                    SourceColumn = mapping.SourceField ?? string.Empty,
                    HubSpotProperty = mapping.HubSpotHeader ?? string.Empty,
                    AssociationType = association ?? string.Empty,
                    ObjectType = normalizedObjectType
                };

                var targetBucket = normalizedObjectType;
                if (string.IsNullOrEmpty(targetBucket))
                {
                    if (!string.IsNullOrWhiteSpace(association) && association.Equals("Mailing Address", StringComparison.OrdinalIgnoreCase))
                    {
                        targetBucket = MappingObjectTypes.Property;
                    }
                    else
                    {
                        targetBucket = MappingObjectTypes.Contact;
                    }
                }

                switch (targetBucket)
                {
                    case MappingObjectTypes.Property:
                        profile.PropertyMappings.Add(fieldMapping);
                        break;
                    case MappingObjectTypes.PhoneNumber:
                        profile.PhoneMappings.Add(fieldMapping);
                        break;
                    default:
                        profile.ContactMappings.Add(fieldMapping);
                        break;
                }
            }

            var totalMappings = profile.ContactMappings.Count + profile.PropertyMappings.Count + profile.PhoneMappings.Count;

            await _profileStore.SaveProfileAsync(profile);

            if (!isAutoSave)
            {
                await LoadProfilesAsync();
                SelectedProfile = SavedProfiles.FirstOrDefault(p => p.Id == profile.Id);
                ProfileStatus = isUpdate
                    ? $"Data profile '{ProfileName}' updated with {totalMappings} mappings"
                    : $"Data profile '{ProfileName}' saved with {totalMappings} mappings";
                AutosaveStatus = "*Changes Saved*";
            }
            else
            {
                ProfileStatus = $"Autosaved '{profile.Name}' at {DateTime.Now:t}";
                AutosaveStatus = "*Auto-Saved*";
            }

            _appSession.SelectedProfile = profile;
            _autosaveTimer?.Stop();
            IsDirty = false;
        }
        catch (Exception ex)
        {
            if (!isAutoSave)
            {
                ProfileStatus = $"Error saving data profile: {ex.Message}";
            }
            else
            {
                AutosaveStatus = $"Autosave failed: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        if (SelectedProfile == null)
        {
            await _dialogService.ShowMessageAsync("Error", "Please select a data profile to load");
            return;
        }

        try
        {
            _isLoadingProfile = true;
            var profile = SelectedProfile.Profile;
            ProfileName = profile.Name;

            FieldMappings.Clear();

            // Load Contact mappings
            foreach (var mapping in profile.ContactMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty,
                    ObjectType = string.IsNullOrWhiteSpace(mapping.ObjectType)
                        ? string.Empty
                        : MappingObjectTypes.Normalize(mapping.ObjectType)
                });
            }

            // Load Property mappings
            foreach (var mapping in profile.PropertyMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty,
                    ObjectType = string.IsNullOrWhiteSpace(mapping.ObjectType)
                        ? string.Empty
                        : MappingObjectTypes.Normalize(mapping.ObjectType)
                });
            }


            // Load Phone mappings (if any exist for backward compatibility)
            foreach (var mapping in profile.PhoneMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty,
                    ObjectType = string.IsNullOrWhiteSpace(mapping.ObjectType)
                        ? string.Empty
                        : MappingObjectTypes.Normalize(mapping.ObjectType)
                });
            }

            // Add a few empty rows for new mappings
            for (int i = 0; i < 3; i++)
            {
                FieldMappings.Add(new FieldMappingViewModel());
            }

            UpdateMappingCount();
            ProfileStatus = $"Data profile '{profile.Name}' loaded";
            _appSession.SelectedProfile = profile;
            IsDirty = false;
            _autosaveTimer?.Stop();
            AutosaveStatus = string.Empty;
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error loading data profile: {ex.Message}";
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    [RelayCommand]
    private void AddMapping()
    {
        FieldMappings.Add(new FieldMappingViewModel());
        UpdateMappingCount();
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveMapping(FieldMappingViewModel mapping)
    {
        if (mapping != null)
        {
            FieldMappings.Remove(mapping);
            UpdateMappingCount();
            MarkDirty();
        }
    }

    [RelayCommand]
    private void ClearMappings()
    {
        _isLoadingProfile = true;
        FieldMappings.Clear();
        InitializeMappings();
        _isLoadingProfile = false;
        MarkDirty();
        AutosaveStatus = "*Unsaved Changes*";
    }

    [RelayCommand]
    private void NewProfile()
    {
        _isLoadingProfile = true;
        ProfileName = "New Data Profile";
        SelectedProfile = null;
        FieldMappings.Clear();
        InitializeMappings();
        _isLoadingProfile = false;
        IsDirty = false;
        _autosaveTimer?.Stop();
        AutosaveStatus = string.Empty;
        ProfileStatus = "Creating new data profile";
    }

    [RelayCommand]
    private async Task DeleteSelectedProfileAsync(ProfileViewModel? profileToDelete)
    {
        // Use the passed profile if available, otherwise use the selected profile
        var profile = profileToDelete ?? SelectedProfile;

        if (profile == null)
        {
            await _dialogService.ShowMessageAsync("Error", "Please select a data profile to delete");
            return;
        }

        var profileName = profile.Name;
        var result = await _dialogService.ShowConfirmationDialogAsync(
            "Delete Data Profile",
            $"Are you sure you want to delete data profile '{profileName}'?");

        if (result)
        {
            try
            {
                await _profileStore.DeleteProfileAsync(profile.Id);
                await LoadProfilesAsync();
                ProfileStatus = $"Data profile '{profileName}' deleted";

                // Clear selection if we deleted the selected profile
                if (SelectedProfile?.Id == profile.Id)
                {
                    SelectedProfile = null;
                }
            }
            catch (Exception ex)
            {
                ProfileStatus = $"Error deleting data profile: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task ImportHeadersAsync()
    {
        var filePath = await _dialogService.ShowOpenFileDialogAsync(
            "Select CSV/Excel File",
            "CSV Files|*.csv|Excel Files|*.xlsx;*.xls|All Files|*.*");

        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            // This would integrate with ISampleLoader to read headers
            ProfileStatus = "Import headers functionality to be implemented";
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error importing headers: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile == null)
        {
            await _dialogService.ShowMessageAsync("Error", "Please select a data profile to export");
            return;
        }

        var filePath = await _dialogService.ShowSaveFileDialogAsync(
            "Export Data Profile",
            $"{SelectedProfile.Name}.json",
            "JSON Files|*.json|All Files|*.*");

        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            // Export profile to JSON
            ProfileStatus = "Export functionality to be implemented";
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error exporting data profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedProfile == null)
        {
            await _dialogService.ShowMessageAsync("Error", "Please select a data profile to apply");
            return;
        }

        try
        {
            // Load the profile data into the current editing session
            var profile = SelectedProfile.Profile;

            // Set the profile name in the text box
            ProfileName = profile.Name;

            // Clear existing mappings
            FieldMappings.Clear();

            // Load all mappings from the selected profile
            int loadedMappings = 0;

            // Load Contact mappings
            foreach (var mapping in profile.ContactMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty,
                    ObjectType = string.IsNullOrWhiteSpace(mapping.ObjectType)
                        ? string.Empty
                        : MappingObjectTypes.Normalize(mapping.ObjectType)
                });
                loadedMappings++;
            }

            // Load Property mappings
            foreach (var mapping in profile.PropertyMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty,
                    ObjectType = string.IsNullOrWhiteSpace(mapping.ObjectType)
                        ? string.Empty
                        : MappingObjectTypes.Normalize(mapping.ObjectType)
                });
                loadedMappings++;
            }

            // Load Phone mappings (if any exist for backward compatibility)
            foreach (var mapping in profile.PhoneMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty,
                    ObjectType = string.IsNullOrWhiteSpace(mapping.ObjectType)
                        ? string.Empty
                        : MappingObjectTypes.Normalize(mapping.ObjectType)
                });
                loadedMappings++;
            }

            // Add a few empty rows for new mappings
            for (int i = 0; i < 3; i++)
            {
                FieldMappings.Add(new FieldMappingViewModel());
            }

            UpdateMappingCount();
            ProfileStatus = $"Data profile '{profile.Name}' loaded with {loadedMappings} mappings";
            _appSession.SelectedProfile = profile;
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error applying data profile: {ex.Message}";
            await _dialogService.ShowMessageAsync("Error", $"Failed to apply data profile: {ex.Message}");
        }
    }


    private void OnMappingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingMappingState || _isLoadingProfile)
            return;

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(FieldMappingViewModel.SourceField))
        {
            UpdateMappingCount();
        }

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(FieldMappingViewModel.SourceField) ||
            e.PropertyName == nameof(FieldMappingViewModel.AssociationLabel) ||
            e.PropertyName == nameof(FieldMappingViewModel.ObjectType) ||
            e.PropertyName == nameof(FieldMappingViewModel.HubSpotHeader))
        {
            MarkDirty();
        }
    }

    partial void OnFieldMappingsChanged(ObservableCollection<FieldMappingViewModel>? oldValue, ObservableCollection<FieldMappingViewModel> newValue)
    {
        if (oldValue != null)
        {
            oldValue.CollectionChanged -= OnFieldMappingsCollectionChanged;

            foreach (var mapping in oldValue)
            {
                UntrackMapping(mapping);
            }
        }

        if (newValue != null)
        {
            newValue.CollectionChanged += OnFieldMappingsCollectionChanged;

            foreach (var mapping in newValue)
            {
                TrackMapping(mapping);
            }
        }

        UpdateMappingCount();
    }
}


public partial class ProfileViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;

    public Profile Profile { get; set; } = new();
}

public partial class FieldMappingViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourceField = string.Empty;

    [ObservableProperty]
    private string _associationLabel = string.Empty;

    [ObservableProperty]
    private string _objectType = string.Empty;

    [ObservableProperty]
    private string? _hubSpotHeader = null;

    [ObservableProperty]
    private bool _isDuplicate;

    [ObservableProperty]
    private bool _isBlockSelected;
}

