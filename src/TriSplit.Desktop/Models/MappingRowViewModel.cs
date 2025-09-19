using CommunityToolkit.Mvvm.ComponentModel;

namespace TriSplit.Desktop.Models;

public partial class MappingRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourceColumn = string.Empty;

    [ObservableProperty]
    private string _hubSpotProperty = string.Empty;

    [ObservableProperty]
    private string _associationType = string.Empty;

    [ObservableProperty]
    private string _objectType = string.Empty;

    [ObservableProperty]
    private string _sampleValues = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}