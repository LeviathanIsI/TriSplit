
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
    private readonly Dictionary<(ProfileObjectType Type, int Index), GroupDefaults> _groupDefaults = new();

    internal const int MaxGroupsPerType = 10;
    private bool _isLoadingProfile;

    public ProfilesViewModel(
        IProfileStore profileStore,
        IDialogService dialogService,
        IAppSession appSession,
        ISampleLoader sampleLoader,
        IProfileDetectionService profileDetectionService)
    {
        _profileStore = profileStore;
        _dialogService = dialogService;
        _appSession = appSession;
        _sampleLoader = sampleLoader;
        _profileDetectionService = profileDetectionService;

        FieldMappings.CollectionChanged += OnFieldMappingsChanged;

        PropertyGroups = new GroupDefaultsCollectionViewModel(this, ProfileObjectType.Property);
        ContactGroups = new GroupDefaultsCollectionViewModel(this, ProfileObjectType.Contact);
        PhoneGroups = new GroupDefaultsCollectionViewModel(this, ProfileObjectType.Phone);
        GroupCollections = new[]
        {
            PropertyGroups,
            ContactGroups,
            PhoneGroups
        };

        EnsureDefaultGroups();
        RebuildGroupCollections();

        _ = LoadProfilesAsync();
    }

    public ObservableCollection<ProfileListItemViewModel> SavedProfiles { get; } = new();

    public ObservableCollection<MappingRowViewModel> FieldMappings { get; } = new();

    public GroupDefaultsCollectionViewModel PropertyGroups { get; }

    public GroupDefaultsCollectionViewModel ContactGroups { get; }

    public GroupDefaultsCollectionViewModel PhoneGroups { get; }

    public IReadOnlyList<GroupDefaultsCollectionViewModel> GroupCollections { get; }

    public ObservableCollection<string> HubSpotHeaders { get; } = new();

    public IReadOnlyList<ProfileObjectType> ObjectTypes { get; } = Enum.GetValues<ProfileObjectType>();

    public IReadOnlyList<MissingHeaderBehavior> MissingHeaderBehaviorOptions { get; } = Enum.GetValues<MissingHeaderBehavior>();

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
            await _dialogService.ShowMessageAsync("Suggest Headers", "Select a CSV file first.");
            return;
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
        return new GroupDefaults
        {
            AssociationLabel = defaults.AssociationLabel,
            DataSource = defaults.DataSource,
            Tags = new List<string>(defaults.Tags)
        };
    }

    internal GroupDefaults GetDefaults(ProfileObjectType type, int index)
    {
        if (!_groupDefaults.TryGetValue((type, index), out var defaults))
        {
            defaults = new GroupDefaults();
            _groupDefaults[(type, index)] = defaults;
        }

        return defaults;
    }

    internal void UpdateDefaults(ProfileObjectType type, int index, string association, string dataSource, string tags)
    {
        var defaults = GetDefaults(type, index);
        defaults.AssociationLabel = association?.Trim() ?? string.Empty;
        defaults.DataSource = dataSource?.Trim() ?? string.Empty;
        defaults.Tags = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        EnsureDirty();
    }

    internal void EnsureDirty()
    {
        if (_isLoadingProfile)
        {
            return;
        }

        IsDirty = true;
    }

    internal void NotifyMappingChanged(MappingRowViewModel mapping)
    {
        if (_isLoadingProfile)
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

    internal void NotifyGroupDefinitionsChanged(ProfileObjectType type)
    {
        foreach (var mapping in FieldMappings.Where(m => m.ObjectType == type))
        {
            mapping.RefreshGroupIndices();
        }
    }

    private void LoadGroupDefaults(ProfileGroupConfiguration? configuration)
    {
        if (configuration == null)
        {
            return;
        }

        foreach (var kvp in configuration.PropertyGroups)
        {
            _groupDefaults[(ProfileObjectType.Property, kvp.Key)] = CloneDefaults(kvp.Value);
        }

        foreach (var kvp in configuration.ContactGroups)
        {
            _groupDefaults[(ProfileObjectType.Contact, kvp.Key)] = CloneDefaults(kvp.Value);
        }

        foreach (var kvp in configuration.PhoneGroups)
        {
            _groupDefaults[(ProfileObjectType.Phone, kvp.Key)] = CloneDefaults(kvp.Value);
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
        foreach (var mapping in FieldMappings)
        {
            mapping.SetDuplicate(false);
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
                mapping.SetDuplicate(true);
            }
        }

        HasDuplicateMappings = duplicateGroups.Count > 0;
        DuplicateWarning = HasDuplicateMappings ? "Duplicate HubSpot headers found in the same group." : string.Empty;
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

        if (_isLoadingProfile)
        {
            return;
        }

        EnsureDirty();
        UpdateProfileStatus();
        UpdateDuplicateFlags();
    }

    private void OnMappingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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

            var existing = new HashSet<string>(FieldMappings.Select(m => m.SourceField), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var header in headers)
            {
                if (existing.Contains(header))
                {
                    continue;
                }

                AddMapping(header);
                added++;
            }

            if (added > 0)
            {
                ProfileStatus = $"Added {added} header suggestion(s).";
            }
            else
            {
                ProfileStatus = "All headers already mapped.";
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
        RemoveGroupCommand = new RelayCommand<GroupDefaultsEditorViewModel>(RemoveGroup, CanRemoveGroup);
    }

    public ProfileObjectType ObjectType { get; }

    public string Header => $"{ObjectType} Groups";

    public ObservableCollection<GroupDefaultsEditorViewModel> Groups { get; }

    public IRelayCommand AddGroupCommand { get; }

    public IRelayCommand<GroupDefaultsEditorViewModel> RemoveGroupCommand { get; }

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
        UpdateCommands();
        _owner.NotifyGroupDefinitionsChanged(ObjectType);
        _owner.EnsureDirty();
    }

    private bool CanAddGroup() => Groups.Count < ProfilesViewModel.MaxGroupsPerType;

    private void RemoveGroup(GroupDefaultsEditorViewModel? editor)
    {
        if (editor == null || Groups.Count <= 1 || !Groups.Contains(editor))
        {
            return;
        }

        Groups.Remove(editor);
        _owner.RemoveGroupDefaults(ObjectType, editor.GroupIndex);
        UpdateCommands();
        _owner.NotifyGroupDefinitionsChanged(ObjectType);
    }

    private bool CanRemoveGroup(GroupDefaultsEditorViewModel? editor) => editor != null && Groups.Count > 1;

    private void UpdateCommands()
    {
        (AddGroupCommand as RelayCommand)?.NotifyCanExecuteChanged();
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

        _suppress = true;
        Reload();
        _suppress = false;
    }

    public ProfileObjectType ObjectType { get; }

    public int GroupIndex { get; }

    public string Title => $"Group {GroupIndex}";

    [ObservableProperty]
    private string _associationLabel = string.Empty;

    [ObservableProperty]
    private string _dataSource = string.Empty;

    [ObservableProperty]
    private string _tags = string.Empty;

    partial void OnAssociationLabelChanged(string value) => Persist();

    partial void OnDataSourceChanged(string value) => Persist();

    partial void OnTagsChanged(string value) => Persist();

    internal void Reload()
    {
        var defaults = _owner.EnsureGroupDefaults(ObjectType, GroupIndex);

        _suppress = true;
        AssociationLabel = defaults.AssociationLabel;
        DataSource = defaults.DataSource;
        Tags = defaults.Tags.Count == 0 ? string.Empty : string.Join(", ", defaults.Tags);
        _suppress = false;
    }

    private void Persist()
    {
        if (_suppress)
        {
            return;
        }

        _owner.UpdateDefaults(ObjectType, GroupIndex, AssociationLabel, DataSource, Tags);
    }
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
