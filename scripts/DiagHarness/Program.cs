using System;
using System.Collections.Generic;
using System.Linq;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Desktop.Services;
using TriSplit.Desktop.ViewModels.Tabs;

namespace DiagHarness;

class DummyProfileStore : IProfileStore
{
    public List<Profile> Profiles { get; } = new();
    public Task<IEnumerable<Profile>> GetAllProfilesAsync() => Task.FromResult<IEnumerable<Profile>>(Profiles);
    public Task<Profile?> GetProfileAsync(Guid id) => Task.FromResult<Profile?>(Profiles.FirstOrDefault(p => p.Id == id));
    public Task<Profile> SaveProfileAsync(Profile profile)
    {
        Profiles.RemoveAll(p => p.Id == profile.Id);
        Profiles.Add(profile);
        return Task.FromResult(profile);
    }
    public Task DeleteProfileAsync(Guid id)
    {
        Profiles.RemoveAll(p => p.Id == id);
        return Task.CompletedTask;
    }
    public Task<Profile?> GetProfileByNameAsync(string name) => Task.FromResult<Profile?>(Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
}

class DummyDialogService : IDialogService
{
    public Task<string?> ShowOpenFileDialogAsync(string title, string filter) => Task.FromResult<string?>(null);
    public Task<string?> ShowSaveFileDialogAsync(string title, string filter, string defaultFileName = "") => Task.FromResult<string?>(null);
    public Task<bool> ShowConfirmationDialogAsync(string title, string message) => Task.FromResult(true);
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    public Task<string?> ShowInputDialogAsync(string title, string prompt, string defaultValue = "") => Task.FromResult<string?>(null);
}

class DummyAppSession : IAppSession
{
    public event EventHandler? SessionUpdated;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private Profile? _selectedProfile;
    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!EqualityComparer<Profile?>.Default.Equals(_selectedProfile, value))
            {
                _selectedProfile = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedProfile)));
                SessionUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public SampleData? CurrentSampleData { get; set; }
    public string? LoadedFilePath { get; set; }
}

internal class Program
{
    private static void AddMapping(ProfilesViewModel viewModel, string source, string association, string hubSpot)
    {
        viewModel.FieldMappings.Add(new FieldMappingViewModel
        {
            SourceField = source,
            AssociationLabel = association,
            HubSpotHeader = hubSpot
        });
    }

    private static void Main()
    {
        var profileStore = new DummyProfileStore();
        var dialogService = new DummyDialogService();
        var appSession = new DummyAppSession();

        var viewModel = new ProfilesViewModel(profileStore, dialogService, appSession);

        viewModel.FieldMappings.Clear();

        AddMapping(viewModel, "Address", "Owner", "Address");
        AddMapping(viewModel, "City", "Owner", "City");
        AddMapping(viewModel, "State", "Owner", "State");
        AddMapping(viewModel, "Zip", "Owner", "Postal Code");
        AddMapping(viewModel, "Mailing Address", "Mailing Address", "Address");
        AddMapping(viewModel, "Mailing City", "Mailing Address", "City");
        AddMapping(viewModel, "Mailing State", "Mailing Address", "State");
        AddMapping(viewModel, "Mailing Zip", "Mailing Address", "Postal Code");

        viewModel.FieldMappings.Add(new FieldMappingViewModel());

        viewModel.ProfileName = "TestProfile";

        Console.WriteLine("Saving profile...");
        viewModel.SaveProfileCommand.Execute(null);

        var saved = profileStore.Profiles.Single();
        Console.WriteLine($"Property mappings saved: {saved.PropertyMappings.Count}");
        foreach (var mapping in saved.PropertyMappings)
        {
            Console.WriteLine($"  {mapping.SourceColumn} -> {mapping.HubSpotProperty} [{mapping.AssociationType}]");
        }
        Console.WriteLine($"Contact mappings saved: {saved.ContactMappings.Count}");
        foreach (var mapping in saved.ContactMappings)
        {
            Console.WriteLine($"  {mapping.SourceColumn} -> {mapping.HubSpotProperty} [{mapping.AssociationType}]");
        }
    }
}
