using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Desktop.Models;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileStore _profileStore;
    private readonly ISampleLoader _sampleLoader;
    private readonly IDialogService _dialogService;
    private readonly IAppSession _appSession;

    [ObservableProperty]
    private ObservableCollection<Profile> _profiles = new();

    [ObservableProperty]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<MappingRowViewModel> _mappingRows = new();

    [ObservableProperty]
    private SampleData? _currentSampleData;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ObservableCollection<string> _sourceHeaders = new();

    [ObservableProperty]
    private ObservableCollection<string> _hubSpotProperties = new();

    public ProfilesViewModel(
        IProfileStore profileStore,
        ISampleLoader sampleLoader,
        IDialogService dialogService,
        IAppSession appSession)
    {
        _profileStore = profileStore;
        _sampleLoader = sampleLoader;
        _dialogService = dialogService;
        _appSession = appSession;

        InitializeHubSpotProperties();
    }

    private void InitializeHubSpotProperties()
    {
        HubSpotProperties = new ObservableCollection<string>
        {
            "firstname",
            "lastname",
            "email",
            "phone",
            "company",
            "lifecyclestage",
            "leadstatus",
            "address",
            "city",
            "state",
            "zip",
            "country",
            "website",
            "jobtitle",
            "notes"
        };
    }

    [RelayCommand]
    private async Task LoadProfilesAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading profiles...";

            var profiles = await _profileStore.GetAllProfilesAsync();
            Profiles = new ObservableCollection<Profile>(profiles);

            StatusMessage = $"Loaded {Profiles.Count} profiles";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading profiles: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadSampleDataAsync()
    {
        var filePath = await _dialogService.ShowOpenFileDialogAsync(
            "Select Data File",
            "CSV Files|*.csv|Excel Files|*.xlsx;*.xls|All Files|*.*");

        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Loading {Path.GetFileName(filePath)}...";

            CurrentSampleData = await _sampleLoader.LoadSampleWithLimitAsync(filePath, 100);
            _appSession.LoadedFilePath = filePath;
            _appSession.CurrentSampleData = CurrentSampleData;

            SourceHeaders = new ObservableCollection<string>(CurrentSampleData.Headers);
            BuildMappingRows();

            StatusMessage = $"Loaded {CurrentSampleData.TotalRows} rows from {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading file: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void BuildMappingRows()
    {
        MappingRows.Clear();

        if (CurrentSampleData == null)
            return;

        foreach (var header in CurrentSampleData.Headers)
        {
            var mappingRow = new MappingRowViewModel
            {
                SourceColumn = header,
                HubSpotProperty = GuessHubSpotProperty(header),
                AssociationType = "Contact"
            };

            // Add sample values
            var sampleValues = CurrentSampleData.Rows
                .Take(3)
                .Select(row => row.GetValueOrDefault(header, string.Empty)?.ToString() ?? string.Empty)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

            mappingRow.SampleValues = string.Join(", ", sampleValues);
            MappingRows.Add(mappingRow);
        }
    }

    private string GuessHubSpotProperty(string sourceColumn)
    {
        var column = sourceColumn.ToLowerInvariant().Replace("_", "").Replace(" ", "");

        return column switch
        {
            "firstname" or "fname" or "first" => "firstname",
            "lastname" or "lname" or "last" => "lastname",
            "email" or "emailaddress" => "email",
            "phone" or "phonenumber" or "mobile" => "phone",
            "company" or "companyname" or "org" => "company",
            "address" or "street" or "streetaddress" => "address",
            "city" or "town" => "city",
            "state" or "province" => "state",
            "zip" or "zipcode" or "postalcode" => "zip",
            _ => string.Empty
        };
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        var name = await _dialogService.ShowInputDialogAsync("New Profile", "Enter profile name:");

        if (string.IsNullOrEmpty(name))
            return;

        try
        {
            var profile = new Profile
            {
                Name = name,
                ContactMappings = MappingRows
                    .Where(m => m.AssociationType == "Contact" && !string.IsNullOrEmpty(m.HubSpotProperty))
                    .Select(m => new FieldMapping
                    {
                        SourceColumn = m.SourceColumn,
                        HubSpotProperty = m.HubSpotProperty,
                        AssociationType = m.AssociationType
                    })
                    .ToList(),
                PropertyMappings = MappingRows
                    .Where(m => m.AssociationType == "Property" && !string.IsNullOrEmpty(m.HubSpotProperty))
                    .Select(m => new FieldMapping
                    {
                        SourceColumn = m.SourceColumn,
                        HubSpotProperty = m.HubSpotProperty,
                        AssociationType = m.AssociationType
                    })
                    .ToList()
            };

            await _profileStore.SaveProfileAsync(profile);
            await LoadProfilesAsync();

            SelectedProfile = profile;
            StatusMessage = $"Created profile: {name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile == null)
            return;

        try
        {
            SelectedProfile.ContactMappings = MappingRows
                .Where(m => m.AssociationType == "Contact" && !string.IsNullOrEmpty(m.HubSpotProperty))
                .Select(m => new FieldMapping
                {
                    SourceColumn = m.SourceColumn,
                    HubSpotProperty = m.HubSpotProperty,
                    AssociationType = m.AssociationType
                })
                .ToList();

            SelectedProfile.PropertyMappings = MappingRows
                .Where(m => m.AssociationType == "Property" && !string.IsNullOrEmpty(m.HubSpotProperty))
                .Select(m => new FieldMapping
                {
                    SourceColumn = m.SourceColumn,
                    HubSpotProperty = m.HubSpotProperty,
                    AssociationType = m.AssociationType
                })
                .ToList();

            await _profileStore.SaveProfileAsync(SelectedProfile);
            StatusMessage = $"Saved profile: {SelectedProfile.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error saving profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task MergeRecordsAsync()
    {
        await Task.CompletedTask;
        StatusMessage = "Merge functionality not yet implemented";
    }

    [RelayCommand]
    private async Task AssociateRecordsAsync()
    {
        await Task.CompletedTask;
        StatusMessage = "Associate functionality not yet implemented";
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value != null)
        {
            _appSession.SelectedProfile = value;
            LoadProfileMappings(value);
        }
    }

    private void LoadProfileMappings(Profile profile)
    {
        if (CurrentSampleData == null)
            return;

        MappingRows.Clear();

        var allMappings = profile.ContactMappings
            .Concat(profile.PropertyMappings)
            .Concat(profile.PhoneMappings)
            .ToList();

        foreach (var header in CurrentSampleData.Headers)
        {
            var existingMapping = allMappings.FirstOrDefault(m => m.SourceColumn == header);

            var mappingRow = new MappingRowViewModel
            {
                SourceColumn = header,
                HubSpotProperty = existingMapping?.HubSpotProperty ?? GuessHubSpotProperty(header),
                AssociationType = existingMapping?.AssociationType ?? "Contact"
            };

            // Add sample values
            var sampleValues = CurrentSampleData.Rows
                .Take(3)
                .Select(row => row.GetValueOrDefault(header, string.Empty)?.ToString() ?? string.Empty)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

            mappingRow.SampleValues = string.Join(", ", sampleValues);
            MappingRows.Add(mappingRow);
        }
    }
}