using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public Task<ProfileMatchCandidate?> ShowProfileSelectionDialogAsync(IReadOnlyList<ProfileMatchCandidate> candidates) => Task.FromResult<ProfileMatchCandidate?>(null);

    public Task<PartialMatchDecision> ShowPartialMatchDialogAsync(ProfileMatchCandidate candidate) => Task.FromResult(PartialMatchDecision.Cancel);

    public Task<string?> ShowInputDialogAsync(string title, string prompt, string defaultValue = "") => Task.FromResult<string?>(null);

    public Task<NewSourceDecision> ShowNewSourceDecisionAsync(string sourceFileName) => Task.FromResult(NewSourceDecision.Cancel);
}

class DummyAppSession : IAppSession
{
    public event EventHandler? SessionUpdated;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public event Action<AppTab>? NavigationRequested;
    public event EventHandler<NewSourceRequestedEventArgs>? NewSourceRequested;

    private Profile? _selectedProfile;
    private Guid? _lastProfileId;

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!EqualityComparer<Profile?>.Default.Equals(_selectedProfile, value))
            {
                _selectedProfile = value;
                _lastProfileId = value?.Id;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedProfile)));
                SessionUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public SampleData? CurrentSampleData { get; set; }
    public string? LoadedFilePath { get; set; }
    public bool OutputCsv { get; set; } = true;
    public bool OutputExcel { get; set; }
    public bool OutputJson { get; set; }
    public Guid? LastProfileId => _lastProfileId;

    public void RequestNavigation(AppTab tab) => NavigationRequested?.Invoke(tab);

    public void NotifyNewSourceRequested(string filePath, IReadOnlyList<string> headers) => NewSourceRequested?.Invoke(this, new NewSourceRequestedEventArgs(filePath, headers));

    public bool TryEnterNewSourcePrompt(string filePath) => true;

    public void CompleteNewSourcePrompt()
    {
    }
}

class DummySampleLoader : ISampleLoader
{
    public Task<SampleData> LoadSampleAsync(string filePath) => Task.FromResult(new SampleData { SourceFile = filePath });

    public Task<IEnumerable<string>> GetColumnHeadersAsync(string filePath) => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

    public Task<SampleData> LoadSampleWithLimitAsync(string filePath, int limit = 100) => Task.FromResult(new SampleData { SourceFile = filePath });
}

class DummyProfileMetadataRepository : IProfileMetadataRepository
{
    public Task<ProfileMetadata?> GetMetadataAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult<ProfileMetadata?>(null);

    public Task SaveMetadataAsync(Profile profile, IEnumerable<string> headers, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ProfileMetadata>> GetAllMetadataAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProfileMetadata>>(Array.Empty<ProfileMetadata>());

    public Task DeleteMetadataAsync(Profile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public string GetMetadataFilePath(Profile profile) => Path.Combine(Path.GetTempPath(), $"{profile.Id}.json");
}

class DummyProfileDetectionService : IProfileDetectionService
{
    public Task<ProfileDetectionResult> DetectProfileAsync(IReadOnlyList<string> headers, string? sourceFilePath, CancellationToken cancellationToken = default)
        => Task.FromResult(ProfileDetectionResult.NewSource("Dummy detection result"));

    public void InvalidateCache()
    {
    }
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
        var sampleLoader = new DummySampleLoader();
        var metadataRepository = new DummyProfileMetadataRepository();
        var detectionService = new DummyProfileDetectionService();

        var viewModel = new ProfilesViewModel(profileStore, dialogService, appSession, sampleLoader, metadataRepository, detectionService);

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
