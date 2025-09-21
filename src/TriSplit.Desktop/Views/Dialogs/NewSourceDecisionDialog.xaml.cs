using System.Windows;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.Views.Dialogs;

public partial class NewSourceDecisionDialog : Window
{
    public NewSourceDecision Result { get; private set; } = NewSourceDecision.Cancel;

    public NewSourceDecisionDialog(string sourceFileName)
    {
        InitializeComponent();
        MessageText.Text = string.IsNullOrWhiteSpace(sourceFileName)
            ? "We detected headers that do not match any saved data profile."
            : $"We detected headers from '{sourceFileName}' that do not match any saved data profile.";
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        Result = NewSourceDecision.UpdateExisting;
        DialogResult = true;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        Result = NewSourceDecision.CreateNew;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = NewSourceDecision.Cancel;
        DialogResult = false;
    }
}
