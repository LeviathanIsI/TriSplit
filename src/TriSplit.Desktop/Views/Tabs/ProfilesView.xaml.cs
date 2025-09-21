using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TriSplit.Desktop.ViewModels.Tabs;

namespace TriSplit.Desktop.Views.Tabs;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private void ProfilesView_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void ProfilesView_OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel)
            return;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var path = files.FirstOrDefault();
            if (!string.IsNullOrEmpty(path))
            {
                await viewModel.LoadHeaderSuggestionsCommand.ExecuteAsync(path);
            }
        }
    }
}
