using System.Windows;

namespace TriSplit.Desktop.Views.Dialogs;

public partial class MessageDialog : Window
{
    public bool PrimaryInvoked { get; private set; }

    public MessageDialog(string title, string message, string primaryButtonText, string? secondaryButtonText = null)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryButtonText;

        if (!string.IsNullOrWhiteSpace(secondaryButtonText))
        {
            SecondaryButton.Content = secondaryButtonText;
            SecondaryButton.Visibility = Visibility.Visible;
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        PrimaryInvoked = true;
        DialogResult = true;
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        PrimaryInvoked = false;
        DialogResult = false;
    }
}