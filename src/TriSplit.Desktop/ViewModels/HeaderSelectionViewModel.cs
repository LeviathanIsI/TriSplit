using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TriSplit.Desktop.ViewModels;

public partial class HeaderSelectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Select Headers to Import";
    
    public ObservableCollection<HeaderItemViewModel> Headers { get; } = new();

    public void SetHeaders(IEnumerable<string> headers)
    {
        Headers.Clear();
        foreach (var header in headers)
        {
            Headers.Add(new HeaderItemViewModel { Header = header, IsSelected = true });
        }
    }

    public void SelectAll()
    {
        foreach (var header in Headers)
        {
            header.IsSelected = true;
        }
    }

    public void SelectNone()
    {
        foreach (var header in Headers)
        {
            header.IsSelected = false;
        }
    }

    public IReadOnlyList<string> GetSelectedHeaders()
    {
        return Headers.Where(h => h.IsSelected).Select(h => h.Header).ToList();
    }

    public int SelectedCount => Headers.Count(h => h.IsSelected);
    public int TotalCount => Headers.Count;

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
    }
}

public partial class HeaderItemViewModel : ObservableObject
{
    [ObservableProperty] 
    private string _header = string.Empty;

    [ObservableProperty]
    private bool _isSelected = true;

    public HeaderSelectionViewModel? Owner { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        Owner?.NotifySelectionChanged();
    }
}
