using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
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

    public ObservableCollection<string> AssociationLabels { get; }
    public ObservableCollection<string> HubSpotHeaders { get; }

    [ObservableProperty]
    private string _profileName = "New Data Profile";

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

    public ProfilesViewModel(
        IProfileStore profileStore,
        IDialogService dialogService,
        IAppSession appSession)
    {
        _profileStore = profileStore;
        _dialogService = dialogService;
        _appSession = appSession;

        // Initialize Association Labels
        AssociationLabels = new ObservableCollection<string>
        {
            "Owner",
            "Executor",
            "Mailing Address"
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

        InitializeMappings();
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

    private void UpdateMappingCount()
    {
        var count = FieldMappings.Count(m => !string.IsNullOrWhiteSpace(m.SourceField));
        MappingCount = $"{count} mapping{(count == 1 ? "" : "s")} configured";
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
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            await _dialogService.ShowMessageAsync("Error", "Please enter a data profile name");
            return;
        }

        try
        {
            // Check if we're updating an existing profile
            Profile profile;
            bool isUpdate = false;

            if (SelectedProfile != null && SelectedProfile.Profile.Id != Guid.Empty)
            {
                // Check if we're updating the currently selected profile (match by UUID)
                var existingProfile = SavedProfiles.FirstOrDefault(p => p.Id == SelectedProfile.Id);
                if (existingProfile != null)
                {
                    // We're updating the selected profile
                    profile = existingProfile.Profile;
                    profile.Name = ProfileName;
                    profile.ContactMappings.Clear();
                    profile.PropertyMappings.Clear();
                    profile.PhoneMappings.Clear();
                    isUpdate = true;
                }
                else
                {
                    // Selected profile not found, create a new one
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
            else
            {
                // No profile selected or empty ID, check if a profile with this name already exists
                var existingProfile = SavedProfiles.FirstOrDefault(p => p.Name == ProfileName);
                if (existingProfile != null)
                {
                    var result = await _dialogService.ShowConfirmationDialogAsync(
                        "Overwrite Data Profile",
                        $"A data profile named '{ProfileName}' already exists. Do you want to overwrite it?");

                    if (!result)
                        return;

                    profile = existingProfile.Profile;
                    profile.Name = ProfileName;
                    profile.ContactMappings.Clear();
                    profile.PropertyMappings.Clear();
                    profile.PhoneMappings.Clear();
                    isUpdate = true;
                }
                else
                {
                    // Creating a new profile
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

            // Save all mappings that have at least a source field
            foreach (var mapping in FieldMappings.Where(m => !string.IsNullOrWhiteSpace(m.SourceField)))
            {
                var fieldMapping = new FieldMapping
                {
                    SourceColumn = mapping.SourceField ?? string.Empty,
                    HubSpotProperty = mapping.HubSpotHeader ?? string.Empty,
                    AssociationType = mapping.AssociationLabel ?? string.Empty
                };

                // Categorize based on AssociationLabel
                switch (mapping.AssociationLabel)
                {
                    case "Owner":
                    case "Executor":
                        profile.ContactMappings.Add(fieldMapping);
                        break;
                    case "Mailing Address":
                        profile.PropertyMappings.Add(fieldMapping);
                        break;
                    default:
                        // If no association label or unknown, default to ContactMappings
                        profile.ContactMappings.Add(fieldMapping);
                        break;
                }
            }

            // Log for debugging
            var totalMappings = profile.ContactMappings.Count + profile.PropertyMappings.Count + profile.PhoneMappings.Count;

            await _profileStore.SaveProfileAsync(profile);
            await LoadProfilesAsync();

            // Select the saved profile in the list
            SelectedProfile = SavedProfiles.FirstOrDefault(p => p.Id == profile.Id);

            ProfileStatus = isUpdate
                ? $"Data profile '{ProfileName}' updated with {totalMappings} mappings"
                : $"Data profile '{ProfileName}' saved with {totalMappings} mappings";
            _appSession.SelectedProfile = profile;
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error saving data profile: {ex.Message}";
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
                    HubSpotHeader = mapping.HubSpotProperty
                });
            }

            // Load Property mappings
            foreach (var mapping in profile.PropertyMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = mapping.AssociationType,
                    HubSpotHeader = mapping.HubSpotProperty
                });
            }

            // Note: Phone mappings are not used with the current association labels
            // but we keep them for backward compatibility if they exist

            // Add a few empty rows for new mappings
            for (int i = 0; i < 3; i++)
            {
                FieldMappings.Add(new FieldMappingViewModel());
            }

            UpdateMappingCount();
            ProfileStatus = $"Data profile '{profile.Name}' loaded";
            _appSession.SelectedProfile = profile;
        }
        catch (Exception ex)
        {
            ProfileStatus = $"Error loading data profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddMapping()
    {
        FieldMappings.Add(new FieldMappingViewModel());
        UpdateMappingCount();
    }

    [RelayCommand]
    private void RemoveMapping(FieldMappingViewModel mapping)
    {
        if (mapping != null)
        {
            FieldMappings.Remove(mapping);
            UpdateMappingCount();
        }
    }

    [RelayCommand]
    private void ClearMappings()
    {
        FieldMappings.Clear();
        InitializeMappings();
    }

    [RelayCommand]
    private void NewProfile()
    {
        ProfileName = "New Data Profile";
        SelectedProfile = null;
        ClearMappings();
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
                // Determine the correct AssociationLabel
                string associationLabel = mapping.AssociationType;
                if (string.IsNullOrEmpty(associationLabel) ||
                    (associationLabel != "Owner" && associationLabel != "Executor"))
                {
                    associationLabel = "Owner"; // Default for contacts
                }

                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = associationLabel,
                    HubSpotHeader = mapping.HubSpotProperty
                });
                loadedMappings++;
            }

            // Load Property mappings
            foreach (var mapping in profile.PropertyMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = "Mailing Address",
                    HubSpotHeader = mapping.HubSpotProperty
                });
                loadedMappings++;
            }

            // Load Phone mappings (if any exist for backward compatibility)
            foreach (var mapping in profile.PhoneMappings)
            {
                FieldMappings.Add(new FieldMappingViewModel
                {
                    SourceField = mapping.SourceColumn,
                    AssociationLabel = "Owner", // Default to Owner since Phone isn't an option anymore
                    HubSpotHeader = mapping.HubSpotProperty
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

    partial void OnFieldMappingsChanged(ObservableCollection<FieldMappingViewModel>? oldValue, ObservableCollection<FieldMappingViewModel> newValue)
    {
        if (oldValue != null)
        {
            foreach (var mapping in oldValue)
            {
                mapping.PropertyChanged -= OnMappingPropertyChanged;
            }
        }

        if (newValue != null)
        {
            foreach (var mapping in newValue)
            {
                mapping.PropertyChanged += OnMappingPropertyChanged;
            }
        }
    }

    private void OnMappingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
    private string? _associationLabel = null;

    [ObservableProperty]
    private string? _hubSpotHeader = null;
}