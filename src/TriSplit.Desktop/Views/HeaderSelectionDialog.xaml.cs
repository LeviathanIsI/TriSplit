using System.Windows;
using TriSplit.Desktop.ViewModels;

namespace TriSplit.Desktop.Views;

public partial class HeaderSelectionDialog : Window
{
    public enum HeaderDialogResult
    {
        Cancel,
        AddMappings,
        UpdateMetadataOnly
    }

    public HeaderSelectionViewModel ViewModel { get; }
    public HeaderDialogResult Result { get; private set; }

    public HeaderSelectionDialog(HeaderSelectionViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        
        // Set owner references for selection change notifications
        foreach (var header in viewModel.Headers)
        {
            header.Owner = viewModel;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectAll();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectNone();
    }

    private void AddMappings_Click(object sender, RoutedEventArgs e)
    {
        Result = HeaderDialogResult.AddMappings;
        DialogResult = true;
    }

    private void UpdateMetadata_Click(object sender, RoutedEventArgs e)
    {
        Result = HeaderDialogResult.UpdateMetadataOnly;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = HeaderDialogResult.Cancel;
        DialogResult = false;
    }
}
