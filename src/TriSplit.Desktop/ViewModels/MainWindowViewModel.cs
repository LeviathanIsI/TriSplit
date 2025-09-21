using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using TriSplit.Desktop.Services;
using TriSplit.Desktop.ViewModels.Tabs;

namespace TriSplit.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IAppSession _appSession;

    [ObservableProperty]
    private string _title = "TriSplit - Data Mapping & HubSpot Integration";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _activeProfileName = "No data profile selected";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private string _processingStatus = string.Empty;

    [ObservableProperty]
    private string _profilesTabHeader = "DATA PROFILES";

    public ViewModels.Tabs.ProfilesViewModel ProfilesViewModel { get; }
    public TestViewModel TestViewModel { get; }
    public ProcessingViewModel ProcessingViewModel { get; }

    public MainWindowViewModel(
        ViewModels.Tabs.ProfilesViewModel profilesViewModel,
        TestViewModel testViewModel,
        ProcessingViewModel processingViewModel,
        IAppSession appSession)
    {
        ProfilesViewModel = profilesViewModel;
        TestViewModel = testViewModel;
        ProcessingViewModel = processingViewModel;
        _appSession = appSession;
        _appSession.NavigationRequested += OnNavigationRequested;

        // Subscribe to app session changes
        _appSession.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IAppSession.SelectedProfile))
            {
                ActiveProfileName = _appSession.SelectedProfile?.Name ?? "No data profile selected";
            }
        };

        ProfilesViewModel.PropertyChanged += ProfilesViewModelOnPropertyChanged;
        UpdateProfilesTabHeader();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        StatusMessage = value switch
        {
            0 => "Configure data mappings and save data profiles",
            1 => "Test your data profile with sample data",
            2 => "Process full files with selected data profile",
            _ => "Ready"
        };
    }

    [RelayCommand]
    private void NavigateToTab(int tabIndex)
    {
        if (tabIndex >= 0 && tabIndex <= 2)
        {
            SelectedTabIndex = tabIndex;
        }
    }

    private void OnNavigationRequested(AppTab tab)
    {
        SelectedTabIndex = tab switch
        {
            AppTab.Profiles => 0,
            AppTab.Test => 1,
            AppTab.Processing => 2,
            _ => SelectedTabIndex
        };
    }

    private void ProfilesViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilesViewModel.IsDirty))
        {
            UpdateProfilesTabHeader();
        }
    }

    private void UpdateProfilesTabHeader()
    {
        ProfilesTabHeader = ProfilesViewModel.IsDirty ? "DATA PROFILES*" : "DATA PROFILES";
    }
}
