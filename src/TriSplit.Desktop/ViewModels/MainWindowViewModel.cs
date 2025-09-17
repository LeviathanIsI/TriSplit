using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace TriSplit.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "TriSplit - Data Mapping & HubSpot Integration";

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private int _selectedTabIndex;

    public ObservableCollection<TabItemViewModel> Tabs { get; }

    private readonly ProfilesViewModel _profilesViewModel;

    public MainWindowViewModel(ProfilesViewModel profilesViewModel)
    {
        _profilesViewModel = profilesViewModel;

        Tabs = new ObservableCollection<TabItemViewModel>
        {
            new TabItemViewModel { Header = "Profiles", Content = _profilesViewModel }
        };

        CurrentViewModel = _profilesViewModel;
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value >= 0 && value < Tabs.Count)
        {
            CurrentViewModel = Tabs[value].Content;
        }
    }
}

public class TabItemViewModel
{
    public string Header { get; set; } = string.Empty;
    public ViewModelBase? Content { get; set; }
}