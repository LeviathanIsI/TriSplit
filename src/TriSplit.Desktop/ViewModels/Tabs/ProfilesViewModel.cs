
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.ViewModels.Tabs;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileStore _profileStore;
    private readonly IDialogService _dialogService;
    private readonly IAppSession _appSession;
    private readonly ISampleLoader _sampleLoader;
    private readonly IProfileDetectionService _profileDetectionService;
    private readonly IProfileMetadataRepository _profileMetadataRepository;
    private readonly Dictionary<(ProfileObjectType Type, int Index), GroupDefaults> _groupDefaults = new();

    internal const int MaxGroupsPerType = 10;
    internal const int MaxAssociationsPerGroup = 10;
    private bool _isLoadingProfile;
    private bool _isInitializing;
    private bool _isUpdatingDuplicateFlags;
    private static readonly string[] DefaultHubSpotHeaders = new[]
    {
        "Address",
        "APN",
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


    public ProfilesViewModel(
        IProfileStore profileStore,
        IDialogService dialogService,
        IAppSession appSession,
        ISampleLoader sampleLoader,
        IProfileDetectionService profileDetectionService,
        IProfileMetadataRepository profileMetadataRepository)
    {
        _isInitializing = true;
        try
        {
            _profileStore = profileStore;
            _dialogService = dialogService;
            _appSession = appSession;
            _sampleLoader = sampleLoader;
            _profileDetectionService = profileDetectionService;
            _profileMetadataRepository = profileMetadataRepository;

            FieldMappings.CollectionChanged += OnFieldMappingsChanged;
            
            // Initialize the sorted view
            FieldMappingsView = CollectionViewSource.GetDefaultView(FieldMappings);
            ApplySort();

            PropertyGroups = new GroupDefaultsCollectionViewModel(this, ProfileObjectType.Property);
            ContactGroups = new GroupDefaultsCollectionViewModel(this, ProfileObjectType.Contact);
            PhoneGroups = new GroupDefaultsCollectionViewModel(this, ProfileObjectType.Phone);
            GroupCollections = new[]
            {
                ContactGroups,
                PropertyGroups,
                PhoneGroups
            };

            EnsureDefaultGroups();
            EnsureDefaultHubSpotHeaders();
            RebuildGroupCollections();
        }
        finally
        {
            _isInitializing = false;
        }

        _ = LoadProfilesAsync();
    }

    public ObservableCollection<ProfileListItemViewModel> SavedProfiles { get; } = new();

    public ObservableCollection<MappingRowViewModel> FieldMappings { get; } = new();

    public ICollectionView FieldMappingsView { get; private set; }

    public enum SortColumn
    {
        SourceField,
        Group,
        HubSpotHeader
    }

    private SortColumn _currentSortColumn = SortColumn.SourceField;
    public SortColumn CurrentSortColumn 
    { 
        get => _currentSortColumn; 
        set 
        { 
            if (_currentSortColumn != value)
            {
                _currentSortColumn = value;
                OnPropertyChanged();
            }
        } 
    }

    private ListSortDirection _currentSortDirection = ListSortDirection.Ascending;
    public ListSortDirection CurrentSortDirection 
    { 
        get => _currentSortDirection; 
        set 
        { 
            if (_currentSortDirection != value)
            {
                _currentSortDirection = value;
                OnPropertyChanged();
            }
        } 
    }

    public GroupDefaultsCollectionViewModel PropertyGroups { get; }

    public GroupDefaultsCollectionViewModel ContactGroups { get; }

    public GroupDefaultsCollectionViewModel PhoneGroups { get; }

    public IReadOnlyList<GroupDefaultsCollectionViewModel> GroupCollections { get; }

    public ObservableCollection<string> HubSpotHeaders { get; } = new();

    public IReadOnlyList<ProfileObjectType> ObjectTypes { get; } = Enum.GetValues<ProfileObjectType>();

    public IReadOnlyList<MissingHeaderBehavior> MissingHeaderBehaviorOptions { get; } = Enum.GetValues<MissingHeaderBehavior>();

    public IReadOnlyList<string> AssociationLabelOptions { get; } = new[]
    {
        "Owner",
        "Executor",
        "Mailing Address",
        "Relative",
        "Associate"
    };

    public int MappingCount => FieldMappings.Count;

    [ObservableProperty]
    private ProfileListItemViewModel? _selectedProfile;

    [ObservableProperty]
    private string _profileName = "New Data Profile";

    [ObservableProperty]
    private bool _createSecondaryContactsFile;

    [ObservableProperty]
    private bool _owner1GetsMailing;

    [ObservableProperty]
    private bool _owner2GetsMailing;

    [ObservableProperty]
    private MissingHeaderBehavior _selectedMissingHeaderBehavior = MissingHeaderBehavior.Error;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _profileStatus = "Ready";

    [ObservableProperty]
    private string _mappingSummary = "0 mappings configured";

    [ObservableProperty]
    private bool _hasDuplicateMappings;

    [ObservableProperty]
    private string _duplicateWarning = string.Empty;

    [ObservableProperty]
    private int _configuredMappingCount;

    [ObservableProperty]
    private bool _isGroupDefaultsExpanded = true;

    [ObservableProperty]
    private GridLength _groupPanelRowHeight = new GridLength(1, GridUnitType.Star);

    partial void OnSelectedProfileChanged(ProfileListItemViewModel? value)
    {
        if (_isLoadingProfile)
        {
            return;
        }

        if (value != null)
        {
            _ = LoadProfileAsync(value.Profile);
        }
    }

    partial void OnProfileNameChanged(string value) => EnsureDirty();

    partial void OnCreateSecondaryContactsFileChanged(bool value) => EnsureDirty();

    partial void OnOwner1GetsMailingChanged(bool value) => EnsureDirty();

    partial void OnOwner2GetsMailingChanged(bool value) => EnsureDirty();

    partial void OnSelectedMissingHeaderBehaviorChanged(MissingHeaderBehavior value) => EnsureDirty();

    partial void OnIsGroupDefaultsExpandedChanged(bool value)
    {
        GroupPanelRowHeight = value ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    [RelayCommand]
    private void AddMapping(string? sourceHeader = null)
    {
        var mapping = new MappingRowViewModel(this)
        {
            SourceField = sourceHeader ?? string.Empty,
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 1
        };

        FieldMappings.Add(mapping);
        EnsureDirty();
        UpdateProfileStatus();
        UpdateDuplicateFlags();
    }

    [RelayCommand]
    private void RemoveMapping(MappingRowViewModel? mapping)
    {
        if (mapping == null)
        {
            return;
        }

        FieldMappings.Remove(mapping);
        EnsureDirty();
        UpdateProfileStatus();
        UpdateDuplicateFlags();
    }

    [RelayCommand(CanExecute = nameof(CanSortBySourceField))]
    private void SortBySourceField()
    {
        if (CurrentSortColumn == SortColumn.SourceField)
        {
            // Toggle direction if already sorted by this column
            CurrentSortDirection = CurrentSortDirection == ListSortDirection.Ascending 
                ? ListSortDirection.Descending 
                : ListSortDirection.Ascending;
        }
        else
        {
            CurrentSortColumn = SortColumn.SourceField;
            CurrentSortDirection = ListSortDirection.Ascending;
        }
        ApplySort();
    }
    private bool CanSortBySourceField() => FieldMappings.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSortByGroup))]
    private void SortByGroup()
    {
        if (CurrentSortColumn == SortColumn.Group)
        {
            CurrentSortDirection = CurrentSortDirection == ListSortDirection.Ascending 
                ? ListSortDirection.Descending 
                : ListSortDirection.Ascending;
        }
        else
        {
            CurrentSortColumn = SortColumn.Group;
            CurrentSortDirection = ListSortDirection.Ascending;
        }
        ApplySort();
    }
    private bool CanSortByGroup() => FieldMappings.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSortByHubSpotHeader))]
    private void SortByHubSpotHeader()
    {
        if (CurrentSortColumn == SortColumn.HubSpotHeader)
        {
            CurrentSortDirection = CurrentSortDirection == ListSortDirection.Ascending 
                ? ListSortDirection.Descending 
                : ListSortDirection.Ascending;
        }
        else
        {
            CurrentSortColumn = SortColumn.HubSpotHeader;
            CurrentSortDirection = ListSortDirection.Ascending;
        }
        ApplySort();
    }
    private bool CanSortByHubSpotHeader() => FieldMappings.Count > 0;

    private void ApplySort()
    {
        if (FieldMappingsView == null) return;
        
        FieldMappingsView.SortDescriptions.Clear();
        
        switch (CurrentSortColumn)
        {
            case SortColumn.SourceField:
                FieldMappingsView.SortDescriptions.Add(new SortDescription(nameof(MappingRowViewModel.SourceField), CurrentSortDirection));
                break;
            case SortColumn.Group:
                FieldMappingsView.SortDescriptions.Add(new SortDescription(nameof(MappingRowViewModel.ObjectType), CurrentSortDirection));
                FieldMappingsView.SortDescriptions.Add(new SortDescription(nameof(MappingRowViewModel.GroupIndex), CurrentSortDirection));
                break;
            case SortColumn.HubSpotHeader:
                FieldMappingsView.SortDescriptions.Add(new SortDescription(nameof(MappingRowViewModel.HubSpotHeader), CurrentSortDirection));
                break;
        }
        
        // Property change notifications are now handled automatically by the properties
        
        // Refresh can execute states
        SortBySourceFieldCommand.NotifyCanExecuteChanged();
        SortByGroupCommand.NotifyCanExecuteChanged();
        SortByHubSpotHeaderCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearMappings()
    {
        if (FieldMappings.Count == 0)
        {
            return;
        }

        FieldMappings.Clear();
        EnsureDirty();
        UpdateProfileStatus();
        UpdateDuplicateFlags();
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        if (IsDirty)
        {
            var confirm = await _dialogService.ShowConfirmationDialogAsync(
                "Discard Changes",
                "Discard unsaved changes and start a new profile?",
                "Discard",
                "Cancel");

            if (!confirm)
            {
                return;
            }
        }

        _isLoadingProfile = true;
        try
        {
            ProfileName = "New Data Profile";
            CreateSecondaryContactsFile = false;
            Owner1GetsMailing = false;
            Owner2GetsMailing = false;
            SelectedMissingHeaderBehavior = MissingHeaderBehavior.Error;
            FieldMappings.Clear();
            HubSpotHeaders.Clear();
            EnsureDefaultHubSpotHeaders();
            _groupDefaults.Clear();
            EnsureDefaultGroups();
            RebuildGroupCollections();
            SelectedProfile = null;
            _appSession.SelectedProfile = null;
            ProfileStatus = "Ready";
            MappingSummary = "0 mappings configured";
            HasDuplicateMappings = false;
            DuplicateWarning = string.Empty;
            ConfiguredMappingCount = 0;
            IsDirty = false;
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile == null)
        {
            return;
        }

        var confirm = await _dialogService.ShowConfirmationDialogAsync(
            "Delete Data Profile",
            $"Delete '{SelectedProfile.Name}'?",
            "Delete",
            "Cancel");

        if (!confirm)
        {
            return;
        }

        await _profileStore.DeleteProfileAsync(SelectedProfile.Profile.Id);
        _profileDetectionService.InvalidateCache();

        SavedProfiles.Remove(SelectedProfile);
        SelectedProfile = null;
        await NewProfileAsync();
        await LoadProfilesAsync();
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (!TryBuildProfile(SelectedProfile?.Profile.Id, SelectedProfile?.Profile.CreatedAt ?? DateTime.UtcNow, SelectedProfile?.Profile.Metadata, out var profile, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                await _dialogService.ShowMessageAsync("Save Profile", error);
            }
            return;
        }

        var saved = await _profileStore.SaveProfileAsync(profile);
        await AfterProfileSavedAsync(saved);
    }

    [RelayCommand]
    private async Task SaveProfileAsAsync()
    {
        var newName = await _dialogService.ShowInputDialogAsync(
            "Save Profile As",
            "Enter a name for the new profile:",
            ProfileName);

        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var trimmed = newName.Trim();
        if (!TryBuildProfile(null, DateTime.UtcNow, SelectedProfile?.Profile.Metadata, out var profile, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                await _dialogService.ShowMessageAsync("Save Profile", error);
            }
            return;
        }

        profile.Name = trimmed;
        ProfileName = trimmed;

        var saved = await _profileStore.SaveProfileAsync(profile);
        await AfterProfileSavedAsync(saved);
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (!TryBuildProfile(SelectedProfile?.Profile.Id, SelectedProfile?.Profile.CreatedAt ?? DateTime.UtcNow, SelectedProfile?.Profile.Metadata, out var profile, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                await _dialogService.ShowMessageAsync("Export Profile", error);
            }
            return;
        }

        var path = await _dialogService.ShowSaveFileDialogAsync(
            "Export Data Profile",
            "JSON Files|*.json",
            SanitizeFileName(profile.Name) + ".json");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var json = JsonConvert.SerializeObject(profile, Formatting.Indented);
        await File.WriteAllTextAsync(path, json);
        ProfileStatus = $"Exported to {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        var path = await _dialogService.ShowOpenFileDialogAsync(
            "Load Data Profile",
            "JSON Files|*.json");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var profile = JsonConvert.DeserializeObject<Profile>(json);
            if (profile == null)
            {
                await _dialogService.ShowMessageAsync("Load Profile", "Failed to parse the selected profile file.");
                return;
            }

            await LoadProfileAsync(profile);
            var existing = SavedProfiles.FirstOrDefault(p => p.Profile.Id == profile.Id);
            if (existing == null)
            {
                SavedProfiles.Add(new ProfileListItemViewModel(profile));
            }
            else
            {
                existing.Update(profile);
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Load Profile", $"Failed to load profile: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadHeaderSuggestionsAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            filePath = _appSession.LoadedFilePath;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            var pickedPath = await _dialogService.ShowOpenFileDialogAsync(
                "Suggest Headers",
                "CSV or Excel|*.csv;*.xlsx;*.xls");

            if (string.IsNullOrWhiteSpace(pickedPath))
            {
                await _dialogService.ShowMessageAsync("Suggest Headers", "Select a CSV file first.");
                return;
            }

            filePath = pickedPath;
            _appSession.LoadedFilePath = filePath;
        }

        await LoadHeaderSuggestionsInternalAsync(filePath);
    }

    [RelayCommand]
    private async Task ImportHeadersAsync()
    {
        var path = await _dialogService.ShowOpenFileDialogAsync(
            "Import Headers",
            "CSV or Excel|*.csv;*.xlsx;*.xls");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await LoadHeaderSuggestionsInternalAsync(path);
    }

    private async Task LoadProfilesAsync()
    {
        var profiles = await _profileStore.GetAllProfilesAsync();
        var selectedId = SelectedProfile?.Profile.Id ?? _appSession.SelectedProfile?.Id;

        SavedProfiles.Clear();
        foreach (var profile in profiles)
        {
            SavedProfiles.Add(new ProfileListItemViewModel(profile));
        }

        if (selectedId.HasValue)
        {
            var match = SavedProfiles.FirstOrDefault(p => p.Profile.Id == selectedId.Value);
            if (match != null)
            {
                SelectedProfile = match;
                await LoadProfileAsync(match.Profile);
                return;
            }
        }

        if (SavedProfiles.Count > 0)
        {
            SelectedProfile = SavedProfiles[0];
            await LoadProfileAsync(SavedProfiles[0].Profile);
        }
    }

    private async Task AfterProfileSavedAsync(Profile saved)
    {
        var existing = SavedProfiles.FirstOrDefault(p => p.Profile.Id == saved.Id);
        if (existing == null)
        {
            var item = new ProfileListItemViewModel(saved);
            SavedProfiles.Add(item);
            SelectedProfile = item;
        }
        else
        {
            existing.Update(saved);
            SelectedProfile = existing;
        }

        _appSession.SelectedProfile = saved;
        _profileDetectionService.InvalidateCache();

        _isLoadingProfile = true;
        try
        {
            await LoadProfileAsync(saved);
            ProfileStatus = $"Saved {ConfiguredMappingCount} mapping(s).";
            IsDirty = false;
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    private Task LoadProfileAsync(Profile profile)
    {
        _isLoadingProfile = true;
        try
        {
            ProfileName = profile.Name;
            CreateSecondaryContactsFile = profile.CreateSecondaryContactsFile;
            Owner1GetsMailing = profile.OwnerMailing?.Owner1GetsMailing ?? false;
            Owner2GetsMailing = profile.OwnerMailing?.Owner2GetsMailing ?? false;
            SelectedMissingHeaderBehavior = profile.MissingHeaderBehavior;

            _groupDefaults.Clear();
            LoadGroupDefaults(profile.Groups);
            EnsureDefaultGroups();
            RebuildGroupCollections();

            FieldMappings.Clear();
            HubSpotHeaders.Clear();
            EnsureDefaultHubSpotHeaders();

            foreach (var mapping in profile.Mappings)
            {
                if (!HubSpotHeaders.Contains(mapping.HubSpotHeader))
                {
                    HubSpotHeaders.Add(mapping.HubSpotHeader);
                }

                var row = new MappingRowViewModel(this)
                {
                    SourceField = mapping.SourceField,
                    HubSpotHeader = mapping.HubSpotHeader,
                    ObjectType = mapping.ObjectType,
                    GroupIndex = mapping.GroupIndex,
                    AssociationOverride = mapping.AssociationLabelOverride ?? string.Empty,
                    DataSourceOverride = mapping.DataSourceOverride ?? string.Empty,
                    TagsOverride = mapping.TagsOverride.Count == 0 ? string.Empty : string.Join(", ", mapping.TagsOverride),
                    Transform = mapping.Transform?.Raw ?? string.Empty
                };

                FieldMappings.Add(row);
            }

            UpdateProfileStatus();
            UpdateDuplicateFlags();
            IsDirty = false;
            ProfileStatus = $"Loaded {ConfiguredMappingCount} mapping(s).";
        }
        finally
        {
            _isLoadingProfile = false;
        }
    
        return Task.CompletedTask;
    }

    private bool TryBuildProfile(Guid? existingId, DateTime createdAt, Dictionary<string, string>? metadata, out Profile profile, out string? error)
    {
        profile = new Profile();
        error = null;

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            error = "Profile name is required.";
            return false;
        }

        if (HasDuplicateMappings)
        {
            error = "Resolve duplicate HubSpot headers before saving.";
            return false;
        }

        var mappings = new List<ProfileMapping>();
        foreach (var mappingRow in FieldMappings)
        {
            if (!mappingRow.TryCreateMapping(out var mapping, out var mappingError))
            {
                error = mappingError ?? "One or more mappings are incomplete.";
                return false;
            }

            mappings.Add(mapping);
        }

        var now = DateTime.UtcNow;
        profile.Id = existingId ?? Guid.NewGuid();
        profile.CreatedAt = existingId.HasValue ? createdAt : now;
        profile.UpdatedAt = now;
        profile.Name = ProfileName.Trim();
        profile.CreateSecondaryContactsFile = CreateSecondaryContactsFile;
        profile.OwnerMailing = new OwnerMailingConfiguration
        {
            Owner1GetsMailing = Owner1GetsMailing,
            Owner2GetsMailing = Owner2GetsMailing
        };
        profile.MissingHeaderBehavior = SelectedMissingHeaderBehavior;
        profile.Mappings = mappings;
        profile.Groups = BuildGroupConfiguration();
        profile.Metadata = metadata != null
            ? new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return true;
    }

    private ProfileGroupConfiguration BuildGroupConfiguration()
    {
        var config = new ProfileGroupConfiguration();

        foreach (var kvp in _groupDefaults)
        {
            var defaults = CloneDefaults(kvp.Value);
            switch (kvp.Key.Type)
            {
                case ProfileObjectType.Contact:
                    config.ContactGroups[kvp.Key.Index] = defaults;
                    break;
                case ProfileObjectType.Phone:
                    config.PhoneGroups[kvp.Key.Index] = defaults;
                    break;
                default:
                    config.PropertyGroups[kvp.Key.Index] = defaults;
                    break;
            }
        }

        return config;
    }

    private static GroupDefaults CloneDefaults(GroupDefaults defaults)
    {
        return defaults.Clone();
    }

    private void NormalizeGroupDefaults(GroupDefaults defaults, ProfileObjectType type, int index)
    {
        defaults.Type = type;
        defaults.Index = index;
        defaults.Tags ??= new List<string>();
        defaults.Associations ??= new List<GroupAssociation>();

        foreach (var association in defaults.Associations)
        {
            association.Labels ??= new List<string>();
        }
    }

    internal GroupAssociation CreateDefaultAssociation(ProfileObjectType sourceType, int sourceIndex, string? preferredLabel = null)
    {
        var (targetType, targetIndex) = GetDefaultAssociationTarget(sourceType, sourceIndex);
        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredLabel))
        {
            labels.Add(preferredLabel.Trim());
        }

        return new GroupAssociation
        {
            TargetType = targetType,
            TargetIndex = targetIndex,
            Labels = labels
        };
    }

    private (ProfileObjectType TargetType, int TargetIndex) GetDefaultAssociationTarget(ProfileObjectType sourceType, int sourceIndex)
    {
        ProfileObjectType targetType;
        IReadOnlyList<int> indices;

        switch (sourceType)
        {
            case ProfileObjectType.Contact:
                targetType = ProfileObjectType.Property;
                indices = GetAvailableGroupIndices(ProfileObjectType.Property);
                break;
            case ProfileObjectType.Phone:
                targetType = ProfileObjectType.Contact;
                indices = GetAvailableGroupIndices(ProfileObjectType.Contact);
                break;
            default:
                targetType = ProfileObjectType.Contact;
                indices = GetAvailableGroupIndices(ProfileObjectType.Contact);
                break;
        }

        var targetIndex = indices.Contains(sourceIndex) ? sourceIndex : (indices.Count > 0 ? indices[0] : 1);
        if (targetIndex <= 0)
        {
            targetIndex = 1;
        }

        return (targetType, targetIndex);
    }

    internal GroupDefaults GetDefaults(ProfileObjectType type, int index)
    {
        if (!_groupDefaults.TryGetValue((type, index), out var defaults))
        {
            defaults = new GroupDefaults();
            _groupDefaults[(type, index)] = defaults;
        }

        NormalizeGroupDefaults(defaults, type, index);
        return defaults;
    }

    internal void UpdateGroupMetadata(ProfileObjectType type, int index, string dataSource, string dataType, string tags)
    {
        var defaults = GetDefaults(type, index);
        defaults.DataSource = dataSource?.Trim() ?? string.Empty;
        defaults.DataType = dataType?.Trim() ?? string.Empty;
        defaults.Tags = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        EnsureDirty();
    }

    internal void UpdateGroupAssociations(ProfileObjectType type, int index, IReadOnlyList<GroupAssociation> associations)
    {
        var defaults = GetDefaults(type, index);
        defaults.Associations = associations
            .Select(a => new GroupAssociation
            {
                TargetType = a.TargetType,
                TargetIndex = a.TargetIndex,
                Labels = a.Labels?.Where(l => !string.IsNullOrWhiteSpace(l))
                                      .Select(l => l.Trim())
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToList() ?? new List<string>()
            })
            .ToList();

        NormalizeGroupDefaults(defaults, type, index);
        EnsureDirty();
    }

    internal void EnsureDirty()
    {
        if (_isLoadingProfile || _isInitializing)
        {
            return;
        }

        IsDirty = true;
    }

    internal void NotifyMappingChanged(MappingRowViewModel mapping)
    {
        if (_isLoadingProfile || _isInitializing || _isUpdatingDuplicateFlags)
        {
            return;
        }

        EnsureDirty();
        UpdateProfileStatus();
        UpdateDuplicateFlags();
    }

    internal void SynchronizeHubSpotHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return;
        }

        if (!HubSpotHeaders.Contains(header))
        {
            HubSpotHeaders.Add(header);
        }
    }

    private void EnsureDefaultHubSpotHeaders()
    {
        foreach (var header in DefaultHubSpotHeaders)
        {
            if (!HubSpotHeaders.Contains(header))
            {
                HubSpotHeaders.Add(header);
            }
        }
    }

    private void EnsureDefaultGroups()
    {
        foreach (var type in ObjectTypes)
        {
            GetDefaults(type, 1);
        }
    }

    private void RebuildGroupCollections()
    {
        PropertyGroups.RebuildFromOwner();
        ContactGroups.RebuildFromOwner();
        PhoneGroups.RebuildFromOwner();
    }

    internal IReadOnlyList<AssociationTargetOption> GetAssociationTargetOptions(ProfileObjectType sourceType)
    {
        var options = new List<AssociationTargetOption>();

        void AddOptions(ProfileObjectType targetType)
        {
            foreach (var index in GetAvailableGroupIndices(targetType))
            {
                options.Add(new AssociationTargetOption(targetType, index));
            }
        }

        AddOptions(ProfileObjectType.Contact);
        AddOptions(ProfileObjectType.Property);
        AddOptions(ProfileObjectType.Phone);

        return options;
    }

    internal IReadOnlyList<int> GetAvailableGroupIndices(ProfileObjectType type)
    {
        var indices = _groupDefaults.Keys
            .Where(k => k.Type == type)
            .Select(k => k.Index)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (indices.Count == 0)
        {
            indices.Add(1);
        }

        return indices;
    }

    internal GroupDefaults EnsureGroupDefaults(ProfileObjectType type, int index) => GetDefaults(type, index);

    internal void RemoveGroupDefaults(ProfileObjectType type, int index)
    {
        if (_groupDefaults.Remove((type, index)))
        {
            EnsureDirty();
        }

        foreach (var kvp in _groupDefaults)
        {
            var associations = kvp.Value.Associations;
            if (associations == null || associations.Count == 0)
            {
                continue;
            }

            var removed = associations.RemoveAll(a => a.TargetType == type && a.TargetIndex == index);
            if (removed > 0)
            {
                NormalizeGroupDefaults(kvp.Value, kvp.Value.Type, kvp.Value.Index);
                EnsureDirty();
            }
        }

        var remaining = GetAvailableGroupIndices(type);
        var fallback = remaining.FirstOrDefault();
        if (fallback <= 0)
        {
            fallback = 1;
        }

        EnsureGroupDefaults(type, fallback);

        foreach (var mapping in FieldMappings.Where(m => m.ObjectType == type && m.GroupIndex == index))
        {
            mapping.GroupIndex = fallback;
        }
    }

    internal int CloneGroup(ProfileObjectType type, int sourceIndex)
    {
        var nextIndex = Enumerable.Range(1, MaxGroupsPerType)
            .FirstOrDefault(i => !_groupDefaults.ContainsKey((type, i)));

        if (nextIndex == 0)
        {
            ProfileStatus = $"Reached {MaxGroupsPerType} {type} groups.";
            return 0;
        }

        var sourceDefaults = GetDefaults(type, sourceIndex);
        var clone = CloneDefaults(sourceDefaults);
        _groupDefaults[(type, nextIndex)] = clone;
        NormalizeGroupDefaults(clone, type, nextIndex);
        EnsureDirty();
        return nextIndex;
    }

    internal void NotifyGroupDefinitionsChanged(ProfileObjectType type)
    {
        if (_isInitializing || _isLoadingProfile)
        {
            return;
        }

        foreach (var mapping in FieldMappings.Where(m => m.ObjectType == type))
        {
            mapping.RefreshGroupIndices();
        }

        PropertyGroups.RefreshAssociationTargets();
        ContactGroups.RefreshAssociationTargets();
        PhoneGroups.RefreshAssociationTargets();
    }

    private void LoadGroupDefaults(ProfileGroupConfiguration? configuration)
    {
        if (configuration == null)
        {
            return;
        }

        foreach (var kvp in configuration.PropertyGroups)
        {
            var propertyClone = CloneDefaults(kvp.Value);
            NormalizeGroupDefaults(propertyClone, ProfileObjectType.Property, kvp.Key);
            _groupDefaults[(ProfileObjectType.Property, kvp.Key)] = propertyClone;
        }

        foreach (var kvp in configuration.ContactGroups)
        {
            var contactClone = CloneDefaults(kvp.Value);
            NormalizeGroupDefaults(contactClone, ProfileObjectType.Contact, kvp.Key);
            _groupDefaults[(ProfileObjectType.Contact, kvp.Key)] = contactClone;
        }

        foreach (var kvp in configuration.PhoneGroups)
        {
            var phoneClone = CloneDefaults(kvp.Value);
            NormalizeGroupDefaults(phoneClone, ProfileObjectType.Phone, kvp.Key);
            _groupDefaults[(ProfileObjectType.Phone, kvp.Key)] = phoneClone;
        }
    }

    private void UpdateProfileStatus()
    {
        var total = FieldMappings.Count;
        var configured = FieldMappings.Count(m => m.IsConfigured);
        ConfiguredMappingCount = configured;
        MappingSummary = total == 0
            ? "0 mappings configured"
            : $"{configured} of {total} mapping(s) configured";
    }

    private void UpdateDuplicateFlags()
    {
        if (_isUpdatingDuplicateFlags)
        {
            return; // Prevent recursive calls
        }

        _isUpdatingDuplicateFlags = true;
        try
        {
            // Set duplicate flags on all mappings
            foreach (var mapping in FieldMappings)
            {
                mapping.SetDuplicateInternal(false);
            }

            var duplicateGroups = FieldMappings
                .Where(m => m.IsConfigured)
                .GroupBy(m => (m.ObjectType, m.GroupIndex, Header: m.HubSpotHeader.Trim().ToUpperInvariant()))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicateGroups)
            {
                foreach (var mapping in group)
                {
                    mapping.SetDuplicateInternal(true);
                }
            }

            HasDuplicateMappings = duplicateGroups.Count > 0;
            DuplicateWarning = HasDuplicateMappings ? "Duplicate HubSpot headers found in the same group." : string.Empty;

            // Notify the UI of duplicate flag changes without triggering the change chain
            foreach (var mapping in FieldMappings)
            {
                mapping.NotifyDuplicateChanged();
            }
        }
        finally
        {
            _isUpdatingDuplicateFlags = false;
        }
    }

    private void OnFieldMappingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (MappingRowViewModel mapping in e.NewItems)
            {
                mapping.PropertyChanged += OnMappingPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (MappingRowViewModel mapping in e.OldItems)
            {
                mapping.PropertyChanged -= OnMappingPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(MappingCount));

        if (_isLoadingProfile || _isInitializing)
        {
            return;
        }

        EnsureDirty();
        UpdateProfileStatus();
        UpdateDuplicateFlags();
    }

    private void OnMappingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingDuplicateFlags)
        {
            return; // Prevent recursive calls during duplicate flag updates
        }

        if (sender is MappingRowViewModel mapping)
        {
            NotifyMappingChanged(mapping);
        }
    }

    private async Task LoadHeaderSuggestionsInternalAsync(string filePath)
    {
        try
        {
            var sample = await _sampleLoader.LoadSampleWithLimitAsync(filePath, 1);
            var headers = sample.Headers ?? new List<string>();
            if (headers.Count == 0)
            {
                await _dialogService.ShowMessageAsync("Suggest Headers", "No headers found in the selected file.");
                return;
            }

            // Show header selection dialog
            var headerSelectionVM = new HeaderSelectionViewModel();
            headerSelectionVM.SetHeaders(headers);
            
            var dialog = new Views.HeaderSelectionDialog(headerSelectionVM)
            {
                Owner = Application.Current.MainWindow
            };

            var dialogResult = dialog.ShowDialog();
            if (dialogResult != true)
            {
                return;
            }

            var selectedHeaders = headerSelectionVM.GetSelectedHeaders();
            if (selectedHeaders.Count == 0)
            {
                ProfileStatus = "No headers selected.";
                return;
            }

            // Save metadata for the current profile if we have one
            if (SelectedProfile?.Profile != null)
            {
                await _profileMetadataRepository.SaveMetadataAsync(SelectedProfile.Profile, headers);
            }

            // Handle the different dialog results
            if (dialog.Result == Views.HeaderSelectionDialog.HeaderDialogResult.UpdateMetadataOnly)
            {
                ProfileStatus = $"Updated metadata with {headers.Count} header(s).";
                _appSession.LoadedFilePath = filePath;
                return;
            }

            // Add selected mappings to the profile
            var existing = new HashSet<string>(FieldMappings.Select(m => m.SourceField), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            
            // Batch add mappings to prevent cascading property change notifications
            var isLoadingPreviousValue = _isLoadingProfile;
            _isLoadingProfile = true;
            try
            {
                foreach (var header in selectedHeaders)
                {
                    if (existing.Contains(header))
                    {
                        continue;
                    }

                    var mapping = new MappingRowViewModel(this)
                    {
                        SourceField = header,
                        ObjectType = ProfileObjectType.Property,
                        GroupIndex = 1
                    };

                    FieldMappings.Add(mapping);
                    added++;
                }
            }
            finally
            {
                _isLoadingProfile = isLoadingPreviousValue;
            }

            // Trigger updates once after all mappings are added
            if (added > 0)
            {
                EnsureDirty();
                UpdateProfileStatus();
                UpdateDuplicateFlags();
                ProfileStatus = $"Added {added} mapping(s) and updated metadata.";
            }
            else
            {
                ProfileStatus = "All selected headers already mapped. Metadata updated.";
            }

            _appSession.LoadedFilePath = filePath;
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Suggest Headers", $"Failed to load headers: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join(string.Empty, name.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}

public partial class MappingRowViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public MappingRowViewModel(ProfilesViewModel owner)
    {
        _owner = owner;
    }

    [ObservableProperty]
    private string _sourceField = string.Empty;

    [ObservableProperty]
    private string _hubSpotHeader = string.Empty;

    [ObservableProperty]
    private ProfileObjectType _objectType = ProfileObjectType.Property;

    [ObservableProperty]
    private int _groupIndex = 1;

    public IReadOnlyList<int> AvailableGroupIndices => _owner.GetAvailableGroupIndices(ObjectType);

    [ObservableProperty]
    private string _associationOverride = string.Empty;

    [ObservableProperty]
    private string _dataSourceOverride = string.Empty;

    [ObservableProperty]
    private string _tagsOverride = string.Empty;

    [ObservableProperty]
    private string _transform = string.Empty;

    [ObservableProperty]
    private bool _isDuplicate;

    partial void OnSourceFieldChanged(string value) => _owner.NotifyMappingChanged(this);

    partial void OnHubSpotHeaderChanged(string value)
    {
        Debug.WriteLine($"HubSpotHeader changed to '{value}'");
        _owner.SynchronizeHubSpotHeader(value);
        _owner.NotifyMappingChanged(this);
    }

    partial void OnObjectTypeChanged(ProfileObjectType value)
    {
        var available = _owner.GetAvailableGroupIndices(value);
        if (!available.Contains(GroupIndex))
        {
            GroupIndex = available.First();
        }

        OnPropertyChanged(nameof(AvailableGroupIndices));
        _owner.NotifyMappingChanged(this);
    }

    partial void OnGroupIndexChanged(int value) => _owner.NotifyMappingChanged(this);

    partial void OnAssociationOverrideChanged(string value) => _owner.NotifyMappingChanged(this);

    partial void OnDataSourceOverrideChanged(string value) => _owner.NotifyMappingChanged(this);

    partial void OnTagsOverrideChanged(string value) => _owner.NotifyMappingChanged(this);

    partial void OnTransformChanged(string value) => _owner.NotifyMappingChanged(this);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SourceField) &&
        !string.IsNullOrWhiteSpace(HubSpotHeader) &&
        GroupIndex > 0;

    internal void RefreshGroupIndices()
    {
        var available = _owner.GetAvailableGroupIndices(ObjectType);
        if (!available.Contains(GroupIndex))
        {
            GroupIndex = available.First();
        }

        OnPropertyChanged(nameof(AvailableGroupIndices));
    }

    internal void SetDuplicate(bool value) => IsDuplicate = value;

    internal void SetDuplicateInternal(bool value)
    {
        // Set the backing field directly to avoid triggering PropertyChanged notifications
        #pragma warning disable MVVMTK0034 // Intentionally accessing backing field to avoid recursion
        _isDuplicate = value;
        #pragma warning restore MVVMTK0034
    }

    internal void NotifyDuplicateChanged()
    {
        OnPropertyChanged(nameof(IsDuplicate));
    }

    public bool TryCreateMapping(out ProfileMapping mapping, out string? error)
    {
        mapping = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(SourceField))
        {
            error = "Each mapping requires a source field.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(HubSpotHeader))
        {
            error = $"Mapping '{SourceField}' requires a HubSpot header.";
            return false;
        }

        if (GroupIndex <= 0 || GroupIndex > ProfilesViewModel.MaxGroupsPerType)
        {
            error = $"Mapping '{SourceField}' has an invalid group index.";
            return false;
        }

        if (!TransformParser.TryParse(Transform, out var definition, out var transformError))
        {
            error = transformError;
            return false;
        }

        mapping = new ProfileMapping
        {
            SourceField = SourceField.Trim(),
            ObjectType = ObjectType,
            GroupIndex = GroupIndex,
            HubSpotHeader = HubSpotHeader.Trim(),
            Transform = definition,
            AssociationLabelOverride = string.IsNullOrWhiteSpace(AssociationOverride) ? null : AssociationOverride.Trim(),
            DataSourceOverride = string.IsNullOrWhiteSpace(DataSourceOverride) ? null : DataSourceOverride.Trim(),
            TagsOverride = string.IsNullOrWhiteSpace(TagsOverride)
                ? new List<string>()
                : TagsOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        return true;
    }
}

public partial class ProfileListItemViewModel : ObservableObject
{
    public ProfileListItemViewModel(Profile profile)
    {
        Profile = profile;
        _name = profile.Name;
    }

    public Profile Profile { get; private set; }

    [ObservableProperty]
    private string _name;

    public void Update(Profile profile)
    {
        Profile = profile;
        Name = profile.Name;
    }
}

public class GroupDefaultsCollectionViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public GroupDefaultsCollectionViewModel(ProfilesViewModel owner, ProfileObjectType objectType)
    {
        _owner = owner;
        ObjectType = objectType;
        Groups = new ObservableCollection<GroupDefaultsEditorViewModel>();
        AddGroupCommand = new RelayCommand(AddGroup, CanAddGroup);
        CloneGroupCommand = new RelayCommand<GroupDefaultsEditorViewModel>(CloneGroup, CanCloneGroup);
        RemoveGroupCommand = new RelayCommand<GroupDefaultsEditorViewModel>(RemoveGroup, CanRemoveGroup);
    }

    public ProfileObjectType ObjectType { get; }

    public string Header => $"{ObjectType.ToString().ToUpperInvariant()} GROUPS";

    public ObservableCollection<GroupDefaultsEditorViewModel> Groups { get; }

    public IRelayCommand AddGroupCommand { get; }

    public IRelayCommand<GroupDefaultsEditorViewModel> CloneGroupCommand { get; }

    public IRelayCommand<GroupDefaultsEditorViewModel> RemoveGroupCommand { get; }

    internal void RefreshAssociationTargets()
    {
        foreach (var group in Groups)
        {
            group.RefreshAssociationTargets();
        }
    }

    internal void RebuildFromOwner()
    {
        var indices = _owner.GetAvailableGroupIndices(ObjectType);
        Groups.Clear();

        foreach (var index in indices)
        {
            Groups.Add(new GroupDefaultsEditorViewModel(_owner, ObjectType, index));
        }

        if (Groups.Count == 0)
        {
            Groups.Add(new GroupDefaultsEditorViewModel(_owner, ObjectType, 1));
        }

        RefreshAssociationTargets();
        UpdateCommands();
        _owner.NotifyGroupDefinitionsChanged(ObjectType);
    }

    private void AddGroup()
    {
        var existing = Groups.Select(g => g.GroupIndex).ToHashSet();
        var nextIndex = Enumerable.Range(1, ProfilesViewModel.MaxGroupsPerType)
            .FirstOrDefault(i => !existing.Contains(i));

        if (nextIndex == 0)
        {
            return;
        }

        _owner.EnsureGroupDefaults(ObjectType, nextIndex);
        Groups.Add(new GroupDefaultsEditorViewModel(_owner, ObjectType, nextIndex));
        RefreshAssociationTargets();
        UpdateCommands();
        _owner.NotifyGroupDefinitionsChanged(ObjectType);
        _owner.EnsureDirty();
    }

    private void CloneGroup(GroupDefaultsEditorViewModel? editor)
    {
        if (editor == null)
        {
            return;
        }

        var newIndex = _owner.CloneGroup(ObjectType, editor.GroupIndex);
        if (newIndex == 0)
        {
            UpdateCommands();
            return;
        }

        RebuildFromOwner();
    }

    private bool CanAddGroup() => Groups.Count < ProfilesViewModel.MaxGroupsPerType;

    private bool CanCloneGroup(GroupDefaultsEditorViewModel? editor) => editor != null && Groups.Count < ProfilesViewModel.MaxGroupsPerType;

    private void RemoveGroup(GroupDefaultsEditorViewModel? editor)
    {
        if (editor == null || Groups.Count <= 1 || !Groups.Contains(editor))
        {
            return;
        }

        Groups.Remove(editor);
        _owner.RemoveGroupDefaults(ObjectType, editor.GroupIndex);
        RefreshAssociationTargets();
        UpdateCommands();
        _owner.NotifyGroupDefinitionsChanged(ObjectType);
    }

    private bool CanRemoveGroup(GroupDefaultsEditorViewModel? editor) => editor != null && Groups.Count > 1;

    private void UpdateCommands()
    {
        (AddGroupCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CloneGroupCommand as RelayCommand<GroupDefaultsEditorViewModel>)?.NotifyCanExecuteChanged();
        (RemoveGroupCommand as RelayCommand<GroupDefaultsEditorViewModel>)?.NotifyCanExecuteChanged();
    }
}


public partial class GroupDefaultsEditorViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private bool _suppress;

    public GroupDefaultsEditorViewModel(ProfilesViewModel owner, ProfileObjectType objectType, int groupIndex)
    {
        _owner = owner;
        ObjectType = objectType;
        GroupIndex = groupIndex;

        Associations = new ObservableCollection<GroupAssociationEditorViewModel>();
        Associations.CollectionChanged += OnAssociationsCollectionChanged;

        AddAssociationCommand = new RelayCommand(AddAssociation, CanAddAssociation);
        RemoveAssociationCommand = new RelayCommand<GroupAssociationEditorViewModel>(RemoveAssociation, CanRemoveAssociation);

        _suppress = true;
        Reload();
        _suppress = false;
    }

    public ProfileObjectType ObjectType { get; }

    public int GroupIndex { get; }

    public string Title => $"Group {GroupIndex}";

    public ObservableCollection<GroupAssociationEditorViewModel> Associations { get; }

    public IRelayCommand AddAssociationCommand { get; }

    public IRelayCommand<GroupAssociationEditorViewModel> RemoveAssociationCommand { get; }

    [ObservableProperty]
    private string _dataSource = string.Empty;

    [ObservableProperty]
    private string _dataType = string.Empty;

    [ObservableProperty]
    private string _tags = string.Empty;

    partial void OnDataSourceChanged(string value) => PersistMetadata();

    partial void OnDataTypeChanged(string value) => PersistMetadata();

    partial void OnTagsChanged(string value) => PersistMetadata();

    internal void Reload()
    {
        var defaults = _owner.EnsureGroupDefaults(ObjectType, GroupIndex);

        foreach (var association in Associations)
        {
            association.Changed -= OnAssociationChanged;
        }

        _suppress = true;

        Associations.Clear();
        DataSource = defaults.DataSource;
        DataType = defaults.DataType;
        Tags = defaults.Tags.Count == 0 ? string.Empty : string.Join(", ", defaults.Tags);

        foreach (var model in defaults.Associations)
        {
            Associations.Add(CreateAssociationEditor(model));
        }

        _suppress = false;

        PersistAssociations();
        UpdateAssociationCommands();
    }

    internal void RefreshAssociationTargets()
    {
        foreach (var association in Associations)
        {
            association.RefreshTargetOptions();
        }
    }

    private GroupAssociationEditorViewModel CreateAssociationEditor(GroupAssociation model)
    {
        var vm = new GroupAssociationEditorViewModel(_owner, this, model);
        vm.Changed += OnAssociationChanged;
        return vm;
    }

    private void PersistMetadata()
    {
        if (_suppress)
        {
            return;
        }

        _owner.UpdateGroupMetadata(ObjectType, GroupIndex, DataSource, DataType, Tags);
        _owner.EnsureDirty();
    }

    internal void PersistAssociations()
    {
        if (_suppress)
        {
            return;
        }

        var models = Associations.Select(a => a.ToModel()).ToList();
        _owner.UpdateGroupAssociations(ObjectType, GroupIndex, models);
        _owner.EnsureDirty();
    }

    private void OnAssociationChanged(object? sender, EventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        PersistAssociations();
    }

    private void OnAssociationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (GroupAssociationEditorViewModel vm in e.NewItems)
            {
                vm.Changed += OnAssociationChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (GroupAssociationEditorViewModel vm in e.OldItems)
            {
                vm.Changed -= OnAssociationChanged;
            }
        }

        if (_suppress)
        {
            return;
        }

        if (Associations.Count == 0)
        {
            PersistAssociations();
            UpdateAssociationCommands();
            return;
        }

        PersistAssociations();
        UpdateAssociationCommands();
    }

    private void AddAssociation()
    {
        Associations.Add(CreateAssociationEditor(_owner.CreateDefaultAssociation(ObjectType, GroupIndex)));
        PersistAssociations();
        UpdateAssociationCommands();
    }

    private bool CanAddAssociation() => Associations.Count < ProfilesViewModel.MaxAssociationsPerGroup;

    private void RemoveAssociation(GroupAssociationEditorViewModel? association)
    {
        if (association == null)
        {
            return;
        }

        association.Changed -= OnAssociationChanged;
        Associations.Remove(association);
        PersistAssociations();
        UpdateAssociationCommands();
    }

    private bool CanRemoveAssociation(GroupAssociationEditorViewModel? association) => association != null;

    private void UpdateAssociationCommands()
    {
        (AddAssociationCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveAssociationCommand as RelayCommand<GroupAssociationEditorViewModel>)?.NotifyCanExecuteChanged();
    }
}

public partial class GroupAssociationEditorViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private readonly GroupDefaultsEditorViewModel _parent;
    private bool _suppress;

    public GroupAssociationEditorViewModel(ProfilesViewModel owner, GroupDefaultsEditorViewModel parent, GroupAssociation model)
    {
        _owner = owner;
        _parent = parent;

        TargetOptions = new ObservableCollection<AssociationTargetOption>();
        LabelOptions = new ObservableCollection<SelectableLabelViewModel>();

        _suppress = true;
        RefreshTargetOptions();
        LoadFromModel(model);
        _suppress = false;
    }

    public event EventHandler? Changed;

    public ObservableCollection<AssociationTargetOption> TargetOptions { get; }

    [ObservableProperty]
    private AssociationTargetOption? _selectedTarget;

    public ObservableCollection<SelectableLabelViewModel> LabelOptions { get; }

    public GroupAssociation ToModel()
    {
        var target = SelectedTarget ?? TargetOptions.FirstOrDefault();
        return new GroupAssociation
        {
            TargetType = target?.Type ?? ProfileObjectType.Contact,
            TargetIndex = target?.Index ?? 1,
            Labels = LabelOptions.Where(option => option.IsSelected).Select(option => option.Label).ToList()
        };
    }

    internal void RefreshTargetOptions()
    {
        var current = SelectedTarget;
        var previousSuppress = _suppress;
        _suppress = true;

        TargetOptions.Clear();
        foreach (var option in _owner.GetAssociationTargetOptions(_parent.ObjectType))
        {
            TargetOptions.Add(option);
        }

        SelectedTarget = current != null
            ? TargetOptions.FirstOrDefault(o => o.Type == current.Type && o.Index == current.Index) ?? TargetOptions.FirstOrDefault()
            : TargetOptions.FirstOrDefault();

        _suppress = previousSuppress;
    }

    private void LoadFromModel(GroupAssociation model)
    {
        var previousSuppress = _suppress;
        _suppress = true;

        if (model != null)
        {
            SelectedTarget = TargetOptions.FirstOrDefault(o => o.Type == model.TargetType && o.Index == model.TargetIndex)
                ?? TargetOptions.FirstOrDefault();
        }

        foreach (var option in LabelOptions)
        {
            option.PropertyChanged -= OnLabelOptionChanged;
        }

        LabelOptions.Clear();
        var configured = model?.Labels ?? new List<string>();
        var options = _owner.AssociationLabelOptions
            .Concat(configured)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var label in options)
        {
            var isSelected = configured.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase));
            var option = new SelectableLabelViewModel(label, isSelected);
            option.PropertyChanged += OnLabelOptionChanged;
            LabelOptions.Add(option);
        }

        if (LabelOptions.Count == 0)
        {
            var defaultLabel = _owner.AssociationLabelOptions.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(defaultLabel))
            {
                var option = new SelectableLabelViewModel(defaultLabel, true);
                option.PropertyChanged += OnLabelOptionChanged;
                LabelOptions.Add(option);
            }
        }

        _suppress = previousSuppress;
    }

    partial void OnSelectedTargetChanged(AssociationTargetOption? value)
    {
        RaiseChanged();
    }

    private void OnLabelOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableLabelViewModel.IsSelected))
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (_suppress)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public partial class SelectableLabelViewModel : ObservableObject
{
    public SelectableLabelViewModel(string label, bool isSelected)
    {
        Label = label;
        _isSelected = isSelected;
    }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class AssociationTargetOption
{
    public AssociationTargetOption(ProfileObjectType type, int index)
    {
        Type = type;
        Index = index;
        DisplayName = $"{type} Group {index}";
    }

    public ProfileObjectType Type { get; }

    public int Index { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

internal static class TransformParser
{
    private static readonly Dictionary<string, TransformVerb> VerbLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TRIM"] = TransformVerb.Trim,
        ["UPPER"] = TransformVerb.Upper,
        ["LOWER"] = TransformVerb.Lower,
        ["ZIP5"] = TransformVerb.Zip5,
        ["PHONE10"] = TransformVerb.Phone10,
        ["LEFT"] = TransformVerb.Left,
        ["RIGHT"] = TransformVerb.Right,
        ["REPLACE"] = TransformVerb.Replace,
        ["CONCAT"] = TransformVerb.Concat
    };

    public static bool TryParse(string? input, out TransformDefinition? definition, out string? error)
    {
        definition = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var trimmed = input.Trim();
        var verbText = trimmed;
        var arguments = new List<string>();

        var openIndex = trimmed.IndexOf('(');
        if (openIndex >= 0)
        {
            if (!trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                error = "Transform syntax must end with ')'.";
                return false;
            }

            verbText = trimmed.Substring(0, openIndex);
            var inner = trimmed.Substring(openIndex + 1, trimmed.Length - openIndex - 2);
            if (!string.IsNullOrWhiteSpace(inner))
            {
                arguments = inner.Split(',', StringSplitOptions.TrimEntries).ToList();
            }
        }

        if (!VerbLookup.TryGetValue(verbText.Trim(), out var verb))
        {
            error = $"Unknown transform '{trimmed}'.";
            return false;
        }

        switch (verb)
        {
            case TransformVerb.Left:
            case TransformVerb.Right:
                if (arguments.Count != 1 || !int.TryParse(arguments[0], out _))
                {
                    error = $"{verb} requires a single numeric argument.";
                    return false;
                }
                break;
            case TransformVerb.Replace:
                if (arguments.Count != 2)
                {
                    error = "REPLACE requires exactly two arguments.";
                    return false;
                }
                break;
            case TransformVerb.Concat:
                if (arguments.Count == 0)
                {
                    error = "CONCAT requires one or more arguments.";
                    return false;
                }
                break;
            default:
                if (arguments.Count > 0)
                {
                    error = $"{verb} does not take arguments.";
                    return false;
                }
                break;
        }

        definition = new TransformDefinition
        {
            Raw = trimmed,
            Verb = verb,
            Arguments = arguments
        };

        return true;
    }
}
