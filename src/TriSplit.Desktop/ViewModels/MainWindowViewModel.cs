using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
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
    private string _activeProfileName = "No profile selected";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private string _processingStatus = string.Empty;

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

        // Subscribe to app session changes
        _appSession.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IAppSession.SelectedProfile))
            {
                ActiveProfileName = _appSession.SelectedProfile?.Name ?? "No profile selected";
            }
        };
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        StatusMessage = value switch
        {
            0 => "Configure data mappings and save profiles",
            1 => "Test your profile with sample data",
            2 => "Process full files with selected profile",
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
}