using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Text;
using System.Globalization;
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
    private readonly ISampleLoader _sampleLoader;
    private readonly IProfileMetadataRepository _profileMetadataRepository;
    private readonly IProfileDetectionService _profileDetectionService;
    private readonly List<(string Property, string Normalized)> _normalizedHubSpotHeaders = new();
    private string? _currentHeaderSourcePath;
    private List<string> _pendingHeaderSignature = new();
    private bool _suppressNewSourcePrompt;
    private bool _isSessionSyncInProgress;

    private static readonly Dictionary<string, string> SynonymMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ownerphone"] = "Phone Number",
        ["ownerphone1"] = "Phone Number",
        ["phone1"] = "Phone Number",
        ["phone"] = "Phone Number",
        ["primaryphone"] = "Phone Number",
        ["mobilephone"] = "Phone Number",
        ["workphone"] = "Phone Number",
        ["ownerfirstname"] = "First Name",
        ["ownerlastname"] = "Last Name",
        ["firstname"] = "First Name",
        ["lastname"] = "Last Name",
        ["contactfirstname"] = "First Name",
        ["contactlastname"] = "Last Name",
        ["emailaddress"] = "Email",
        ["owneremail"] = "Email",
        ["postalcode"] = "Postal Code",
        ["zipcode"] = "Postal Code",
        ["zip"] = "Postal Code",
        ["mailingzip"] = "Postal Code",
        ["mailingpostalcode"] = "Postal Code",
        ["mailingstreet"] = "Address",
        ["mailingaddress"] = "Address",
        ["mailingcity"] = "City",
        ["mailingstate"] = "State",
        ["mailingapn"] = "APN",
        ["apnnumber"] = "APN"
    };

    private readonly HashSet<FieldMappingViewModel> _trackedMappings = new();
    private bool _isUpdatingMappingState;
    private bool _isLoadingProfile;
    private readonly TimeSpan _autosaveDelay = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _autosaveTimer;
    private bool _isAutosaving;
    private int _remainingAutosaveTime;
    private Guid? _loadedProfileId;



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
    private string _contactPropertyDataSource = string.Empty;

    partial void OnContactPropertyDataSourceChanged(string value)
    {
        if (_isLoadingProfile)
            return;

        MarkDirty();
    }

    [ObservableProperty]
    private string _phoneDataSource = string.Empty;

    partial void OnPhoneDataSourceChanged(string value)
    {
        if (_isLoadingProfile)
            return;

        MarkDirty();
    }

    [ObservableProperty]
    private string _dataType = string.Empty;

    partial void OnDataTypeChanged(string value)
    {
        if (_isLoadingProfile)
            return;

        MarkDirty();
    }

    [ObservableProperty]
    private string _tagNote = string.Empty;

    partial void OnTagNoteChanged(string value)
    {
        if (_isLoadingProfile)
            return;

        MarkDirty();
    }

    [ObservableProperty]
    private string _defaultAssociationLabel = string.Empty;

    partial void OnDefaultAssociationLabelChanged(string value)
    {
        if (_isLoadingProfile)
            return;

        MarkDirty();
    }

    [ObservableProperty]
    private ObservableCollection<ProfileViewModel> _savedProfiles = new();

    [ObservableProperty]
    private ProfileViewModel? _selectedProfile;

    partial void OnSelectedProfileChanged(ProfileViewModel? value)
    {
        if (_isLoadingProfile)
            return;

        SaveProfileCommand.NotifyCanExecuteChanged();
            SaveProfileAsCommand.NotifyCanExecuteChanged();

        if (value != null && (!_loadedProfileId.HasValue || _loadedProfileId.Value != value.Profile.Id))
        {
            ProfileStatus = $"Selected data profile '{value.Name}'. Click Load to edit.";
        }
    }

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

    [ObservableProperty]
    private bool _hasAutosaveStatus;

    [ObservableProperty]
    private string _headerSuggestionFileName = "Drop a CSV or Excel file to suggest mappings";

    [ObservableProperty]
    private string _suggestionSummary = string.Empty;

    public ObservableCollection<MappingSuggestionViewModel> MappingSuggestions { get; } = new();

    [ObservableProperty]
    private bool _hasMappingSuggestions;

    partial void OnAutosaveStatusChanged(string value)
    {
        HasAutosaveStatus = !string.IsNullOrWhiteSpace(value);
    }




    public ProfilesViewModel(
        IProfileStore profileStore,
        IDialogService dialogService,
        IAppSession appSession,
        ISampleLoader sampleLoader,
        IProfileMetadataRepository profileMetadataRepository,
        IProfileDetectionService profileDetectionService)
    {
        _profileStore = profileStore;
        _dialogService = dialogService;
        _appSession = appSession;
        _sampleLoader = sampleLoader;
        _profileMetadataRepository = profileMetadataRepository;
        _appSession.NewSourceRequested += OnNewSourceRequested;
        _appSession.PropertyChanged += OnSessionPropertyChanged;
        _profileDetectionService = profileDetectionService;

        FieldMappings = new ObservableCollection<FieldMappingViewModel>();

        MappingSuggestions.CollectionChanged += (s, e) => UpdateSuggestionState();

        // Initialize Association Labels
        AssociationLabels = new ObservableCollection<string>
        {
            string.Empty,
            "Owner",
            "Executor"
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

        foreach (var header in HubSpotHeaders)
        {
            var normalized = NormalizeForMatching(header);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _normalizedHubSpotHeaders.Add((header, normalized));
            }
        }

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
    private void SetPendingHeaderSignature(IEnumerable<string>? headers)
    {
        _pendingHeaderSignature = headers?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToList() ?? new List<string>();
    }


    private void UpdateAutosaveStatus(bool clearIfClean = false)
    {
        if (!IsDirty)
        {
            if (clearIfClean)
            {
                AutosaveStatus = string.Empty;
            }
            return;
        }

        if (SelectedProfile == null)
        {
            AutosaveStatus = "*Unsaved Changes*";
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

            if (_appSession.SelectedProfile != null)
            {
                var matching = SavedProfiles.FirstOrDefault(p => p.Id == _appSession.SelectedProfile.Id);
                if (matching != null)
                {
                    SelectedProfile = matching;
                    return;
                }
            }

            if (_appSession.LastProfileId.HasValue)
            {
                var persisted = SavedProfiles.FirstOrDefault(p => p.Id == _appSession.LastProfileId.Value);
                if (persisted != null)
                {
                    SelectedProfile = persisted;
                    _appSession.SelectedProfile = persisted.Profile;
                }
            }
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error loading data profiles: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        await SaveProfileInternalAsync(isAutoSave: false);
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfileAs))]
    private async Task SaveProfileAsAsync()
    {
        var proposedName = string.IsNullOrWhiteSpace(ProfileName) ? "New Data Profile" : ProfileName.Trim();
        var input = await _dialogService.ShowInputDialogAsync("Save Data Profile As", "Enter a name for the new data profile:", proposedName);
        if (string.IsNullOrWhiteSpace(input))
        {
            ProfileStatus = "Save As cancelled";
            return;
        }

        var trimmed = input.Trim();
        if (!string.Equals(ProfileName, trimmed, StringComparison.Ordinal))
        {
            ProfileName = trimmed;
        }

        SelectedProfile = null;
        _loadedProfileId = null;

        await SaveProfileInternalAsync(isAutoSave: false, forceNewProfile: true);
    }

    private bool CanSaveProfile()
    {
        if (_isLoadingProfile)
            return false;

        if (SelectedProfile == null)
            return true;

        return _loadedProfileId.HasValue && _loadedProfileId.Value == SelectedProfile.Profile.Id;
    }

    private bool CanSaveProfileAs()
    {
        return !_isLoadingProfile;
    }

    private async Task SaveProfileInternalAsync(bool isAutoSave, bool forceNewProfile = false)
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

            var selectedProfileId = SelectedProfile?.Profile.Id;
            if (!forceNewProfile && selectedProfileId.HasValue && (!_loadedProfileId.HasValue || _loadedProfileId.Value != selectedProfileId.Value))
            {
                if (isAutoSave)
                {
                    return;
                }

                await _dialogService.ShowMessageAsync("Load Profile First", "Select and load the profile you want to modify before saving.");
                return;
            }

            if (!forceNewProfile && SelectedProfile != null && SelectedProfile.Profile.Id != Guid.Empty)
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

            profile.ContactPropertyDataSource = ContactPropertyDataSource?.Trim() ?? string.Empty;
            profile.PhoneDataSource = PhoneDataSource?.Trim() ?? string.Empty;
            profile.DataType = DataType?.Trim() ?? string.Empty;
            profile.TagNote = TagNote?.Trim() ?? string.Empty;
            profile.DefaultAssociationLabel = DefaultAssociationLabel?.Trim() ?? string.Empty;

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

            if (_pendingHeaderSignature.Count > 0)
            {
                await _profileMetadataRepository.SaveMetadataAsync(profile, _pendingHeaderSignature);
                profile.SourceHeaders = _pendingHeaderSignature.ToList();
            }

            await _profileStore.SaveProfileAsync(profile);
            _loadedProfileId = profile.Id;
            SaveProfileCommand.NotifyCanExecuteChanged();
            SaveProfileAsCommand.NotifyCanExecuteChanged();

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
            ClearSuggestions();
            var profile = SelectedProfile.Profile;
            ProfileName = profile.Name;
            ContactPropertyDataSource = profile.ContactPropertyDataSource ?? string.Empty;
            PhoneDataSource = profile.PhoneDataSource ?? string.Empty;
            DataType = profile.DataType ?? string.Empty;
            TagNote = profile.TagNote ?? string.Empty;
            DefaultAssociationLabel = profile.DefaultAssociationLabel ?? string.Empty;

            await LoadMetadataForProfileAsync(profile);

            FieldMappings.Clear();

            var loadedMappings = 0;

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

            for (int i = 0; i < 3; i++)
            {
                FieldMappings.Add(new FieldMappingViewModel());
            }

            UpdateMappingCount();
            ProfileStatus = $"Data profile '{profile.Name}' loaded with {loadedMappings} mappings";
            _appSession.SelectedProfile = profile;
            _loadedProfileId = profile.Id;
            SaveProfileCommand.NotifyCanExecuteChanged();
            SaveProfileAsCommand.NotifyCanExecuteChanged();
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
            SaveProfileCommand.NotifyCanExecuteChanged();
            SaveProfileAsCommand.NotifyCanExecuteChanged();
        }
    }


    private void PopulateSuggestions(IEnumerable<string> headers)
    {
        MappingSuggestions.Clear();

        var list = headers?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList() ?? new List<string>();

        var suggestionCount = 0;

        foreach (var header in list)
        {
            var suggestion = BuildSuggestion(header);
            if (suggestion != null)
            {
                suggestion.IsAccepted = true;
                MappingSuggestions.Add(suggestion);
                suggestionCount++;
            }
            else
            {
                var entry = new MappingSuggestionViewModel(header, null, 0.0, isAccepted: false);
                MappingSuggestions.Add(entry);
            }
        }

        if (list.Count > 0)
        {
            var suggestionMessage = suggestionCount > 0
                ? $"Found {suggestionCount} suggested mapping{(suggestionCount == 1 ? string.Empty : "s")} from {HeaderSuggestionFileName}."
                : $"No confident suggestions found in {HeaderSuggestionFileName}.";

            var totalMessage = $" Showing all {list.Count} column{(list.Count == 1 ? string.Empty : "s")} from the source file.";
            SuggestionSummary = suggestionMessage + totalMessage;
        }
        else
        {
            SuggestionSummary = $"No columns discovered in {HeaderSuggestionFileName}.";
        }

        UpdateSuggestionState();
    }

    private MappingSuggestionViewModel? BuildSuggestion(string header)
    {
        var normalizedHeader = NormalizeForMatching(header);
        if (string.IsNullOrWhiteSpace(normalizedHeader))
            return null;

        if (SynonymMap.TryGetValue(normalizedHeader, out var synonymProperty))
        {
            return new MappingSuggestionViewModel(header, synonymProperty, 1.0);
        }

        string? bestProperty = null;
        double bestScore = 0;

        foreach (var (property, normalizedProperty) in _normalizedHubSpotHeaders)
        {
            if (string.IsNullOrWhiteSpace(normalizedProperty))
                continue;

            var score = CalculateSimilarity(normalizedHeader, normalizedProperty);
            if (score > bestScore)
            {
                bestScore = score;
                bestProperty = property;
            }
        }

        if (bestProperty != null && bestScore >= 0.65)
        {
            return new MappingSuggestionViewModel(header, bestProperty, bestScore);
        }

        return null;
    }

    [RelayCommand]
    private void ApplySuggestions()
    {
        if (MappingSuggestions.Count == 0)
        {
            ProfileStatus = "No suggestions available to apply.";
            return;
        }

        var accepted = MappingSuggestions.Where(s => s.IsAccepted).ToList();
        if (accepted.Count == 0)
        {
            ProfileStatus = "Select at least one column before applying.";
            return;
        }

        var applied = 0;
        foreach (var suggestion in accepted)
        {
            ApplySuggestionToMappings(suggestion);
            applied++;
        }

        if (applied == 0)
        {
            ProfileStatus = "No suggestions were applied.";
            return;
        }

        UpdateMappingCount();
        MarkDirty();
        AutosaveStatus = "*Unsaved Changes*";

        MappingSuggestions.Clear();
        SuggestionSummary = string.Empty;
        UpdateSuggestionState();

        ProfileStatus = $"Applied {applied} suggestion(s).";
    }

    [RelayCommand]
    private void DismissSuggestions()
    {
        if (MappingSuggestions.Count == 0 && string.IsNullOrWhiteSpace(SuggestionSummary))
        {
            return;
        }

        MappingSuggestions.Clear();
        SuggestionSummary = string.Empty;
        UpdateSuggestionState();
        ProfileStatus = "Suggestions dismissed.";
    }

    private void ApplySuggestionToMappings(MappingSuggestionViewModel suggestion)
    {
        var existing = FieldMappings.FirstOrDefault(m =>
            string.Equals(m.SourceField, suggestion.SourceHeader, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            existing = FieldMappings.FirstOrDefault(m => string.IsNullOrWhiteSpace(m.SourceField));
            if (existing == null)
            {
                existing = new FieldMappingViewModel();
                FieldMappings.Add(existing);
            }

            existing.SourceField = suggestion.SourceHeader;
        }

        if (!string.IsNullOrWhiteSpace(suggestion.SuggestedProperty))
        {
            existing.HubSpotHeader = suggestion.SuggestedProperty;
        }
    }

    private void ClearSuggestions()
    {
        MappingSuggestions.Clear();
        SuggestionSummary = string.Empty;
        HeaderSuggestionFileName = "Drop a CSV or Excel file to suggest mappings";
        _currentHeaderSourcePath = null;
        UpdateSuggestionState();
    }

    private void UpdateSuggestionState()
    {
        HasMappingSuggestions = MappingSuggestions.Count > 0 || !string.IsNullOrWhiteSpace(SuggestionSummary);
    }

    private static string NormalizeForMatching(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0;

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var distance = LevenshteinDistance(a, b);
        var maxLength = Math.Max(a.Length, b.Length);
        return maxLength == 0 ? 1.0 : 1.0 - (double)distance / maxLength;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
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
        ContactPropertyDataSource = string.Empty;
        PhoneDataSource = string.Empty;
        DataType = string.Empty;
        TagNote = string.Empty;
        DefaultAssociationLabel = string.Empty;
        FieldMappings.Clear();
        InitializeMappings();
        _isLoadingProfile = false;
        IsDirty = false;
        _autosaveTimer?.Stop();
        AutosaveStatus = string.Empty;
        ClearSuggestions();
        SetPendingHeaderSignature(null);
        ProfileStatus = "Creating new data profile";
        _loadedProfileId = null;
        SaveProfileCommand.NotifyCanExecuteChanged();
            SaveProfileAsCommand.NotifyCanExecuteChanged();
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
                await _profileMetadataRepository.DeleteMetadataAsync(profile.Profile);
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
    private async Task LoadHeaderSuggestionsAsync(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            filePath = await _dialogService.ShowOpenFileDialogAsync(
                "Select CSV/Excel File",
                "CSV Files|*.csv|Excel Files|*.xlsx;*.xls|All Files|*.*");
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var headers = (await _sampleLoader.GetColumnHeadersAsync(filePath)).ToList();
            if (headers.Count == 0)
            {
                await _dialogService.ShowMessageAsync("No Headers Found", $"No headers were detected in {Path.GetFileName(filePath)}.");
                return;
            }

            _currentHeaderSourcePath = filePath;
            _appSession.LoadedFilePath = filePath;
            HeaderSuggestionFileName = Path.GetFileName(filePath);
            SetPendingHeaderSignature(headers);

            var detectionResult = await _profileDetectionService.DetectProfileAsync(headers, filePath);
            await HandleDetectionResultAsync(detectionResult, headers, filePath);
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error reading headers: {ex.Message}";
            await _dialogService.ShowMessageAsync("Header Load Error", $"We couldn't read the headers: {ex.Message}");
        }
    }

    private async Task LoadMetadataForProfileAsync(Profile profile)
    {
        if (profile == null)
        {
            return;
        }

        var metadata = await _profileMetadataRepository.GetMetadataAsync(profile);
        if (metadata != null)
        {
            if (!string.IsNullOrWhiteSpace(metadata.FilePath))
            {
                profile.MetadataFileName = Path.GetFileName(metadata.FilePath);
            }

            profile.SourceHeaders = metadata.Headers;
            SetPendingHeaderSignature(metadata.Headers);
        }
        else
        {
            SetPendingHeaderSignature(null);
        }
    }

    private async void OnNewSourceRequested(object? sender, NewSourceRequestedEventArgs e)
    {
        if (e == null || string.IsNullOrWhiteSpace(e.FilePath))
        {
            return;
        }

        if (!File.Exists(e.FilePath))
        {
            await _dialogService.ShowMessageAsync("File Not Found", $"We couldn\"t find {Path.GetFileName(e.FilePath)}. Select the file again from Processing.");
            return;
        }

        try
        {
            _suppressNewSourcePrompt = true;
            NewProfile();
            ProfileName = GenerateProfileNameFromFile(e.FilePath);
            await LoadHeaderSuggestionsAsync(e.FilePath);
            ProfileStatus = $"Review mappings for {Path.GetFileName(e.FilePath)} and save this profile before processing.";
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Error", $"Unable to prepare the new profile: {ex.Message}");
        }
    }

    private static string GenerateProfileNameFromFile(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrWhiteSpace(name) ? "New Data Profile" : name;
    }


    private async Task HandleDetectionResultAsync(ProfileDetectionResult detectionResult, IReadOnlyList<string> headers, string filePath)
    {
        switch (detectionResult.Outcome)
        {
            case ProfileDetectionOutcome.Matched:
                if (detectionResult.Profile != null)
                {
                    await ApplyMatchedProfileAsync(detectionResult.Profile, headers, filePath, detectionResult.ShouldUpdateMetadata, detectionResult.StatusMessage);
                }
                else
                {
                    ProfileStatus = detectionResult.StatusMessage;
                }
                break;
                        case ProfileDetectionOutcome.NewSource:
            {
                PopulateSuggestions(headers);
                ProfileStatus = detectionResult.StatusMessage;
                UpdateSuggestionState();

                if (_suppressNewSourcePrompt)
                {
                    _suppressNewSourcePrompt = false;
                    break;
                }

                if (!_appSession.TryEnterNewSourcePrompt(filePath))
                {
                    break;
                }

                try
                {
                    var decision = await _dialogService.ShowNewSourceDecisionAsync(Path.GetFileName(filePath));
                    switch (decision)
                    {
                        case NewSourceDecision.CreateNew:
                            NewProfile();
                            ProfileName = GenerateProfileNameFromFile(filePath);
                            PopulateSuggestions(headers);
                            SetPendingHeaderSignature(headers);
                            ProfileStatus = $"Mapping headers from {Path.GetFileName(filePath)}. Save this profile before processing.";
                            break;
                        case NewSourceDecision.UpdateExisting:
                            SetPendingHeaderSignature(headers);
                            ProfileStatus = "Select a saved profile on the left to update with these headers.";
                            break;
                        default:
                            ClearSuggestions();
                            SetPendingHeaderSignature(null);
                            _appSession.LoadedFilePath = null;
                            ProfileStatus = "New source cancelled.";
                            break;
                    }
                }
                finally
                {
                    _appSession.CompleteNewSourcePrompt();
                }

                break;
            }case ProfileDetectionOutcome.Cancelled:
                ProfileStatus = detectionResult.StatusMessage;
                ClearSuggestions();
                SetPendingHeaderSignature(null);
                _appSession.LoadedFilePath = null;
                break;
        }
    }

    private async Task ApplyMatchedProfileAsync(Profile matchedProfile, IReadOnlyList<string> headers, string sourcePath, bool markDirty, string statusMessage)
    {
        var targetProfile = SavedProfiles.FirstOrDefault(p => p.Id == matchedProfile.Id);
        if (targetProfile == null)
        {
            await LoadProfilesAsync();
            targetProfile = SavedProfiles.FirstOrDefault(p => p.Id == matchedProfile.Id);
        }

        if (targetProfile == null)
        {
            await _dialogService.ShowMessageAsync("Profile Not Found", $"The matched profile '{matchedProfile.Name}' is no longer available.");
            return;
        }

        SelectedProfile = targetProfile;
        await LoadProfileAsync();

        _currentHeaderSourcePath = sourcePath;
        _appSession.LoadedFilePath = sourcePath;
        SetPendingHeaderSignature(headers);
        AutosaveStatus = string.Empty;

        ProfileStatus = statusMessage;

        if (markDirty)
        {
            MarkDirty();
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

    private async void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAppSession.SelectedProfile))
        {
            await SyncSelectedProfileFromSessionAsync();
        }
        else if (e.PropertyName == nameof(IAppSession.LoadedFilePath))
        {
            await SyncLoadedFileFromSessionAsync();
        }
    }

    private async Task SyncSelectedProfileFromSessionAsync()
    {
        if (_isSessionSyncInProgress)
        {
            return;
        }

        var sessionProfile = _appSession.SelectedProfile;
        if (sessionProfile == null)
        {
            return;
        }

        if (!SavedProfiles.Any())
        {
            await LoadProfilesAsync();
        }

        var matching = SavedProfiles.FirstOrDefault(p => p.Id == sessionProfile.Id);
        if (matching == null)
        {
            return;
        }

        if (SelectedProfile?.Id == matching.Id && PathsEqual(_appSession.LoadedFilePath, _currentHeaderSourcePath))
        {
            return;
        }

        try
        {
            _isSessionSyncInProgress = true;
            SelectedProfile = matching;
            await LoadProfileAsync();
        }
        finally
        {
            _isSessionSyncInProgress = false;
        }
    }

    private async Task SyncLoadedFileFromSessionAsync()
    {
        if (_isSessionSyncInProgress)
        {
            return;
        }

        var filePath = _appSession.LoadedFilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        if (_appSession.SelectedProfile == null)
        {
            return;
        }

        if (PathsEqual(filePath, _currentHeaderSourcePath))
        {
            return;
        }

        try
        {
            _isSessionSyncInProgress = true;
            await LoadHeaderSuggestionsAsync(filePath);
        }
        finally
        {
            _isSessionSyncInProgress = false;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
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

public partial class MappingSuggestionViewModel : ObservableObject
{
    public MappingSuggestionViewModel(string sourceHeader, string? suggestedProperty, double confidence, bool isAccepted = true)
    {
        SourceHeader = sourceHeader;
        SuggestedProperty = suggestedProperty;
        Confidence = confidence;
        IsAccepted = isAccepted;
    }

    public string SourceHeader { get; }
    public string? SuggestedProperty { get; }
    public double Confidence { get; }

    public bool HasSuggestedProperty => !string.IsNullOrWhiteSpace(SuggestedProperty);

    public string ConfidenceDisplay => HasSuggestedProperty
        ? (Confidence >= 1 ? "100%" : string.Format(CultureInfo.CurrentCulture, "{0:P0}", Confidence))
        : "--";

    [ObservableProperty]
    private bool _isAccepted = true;
}
