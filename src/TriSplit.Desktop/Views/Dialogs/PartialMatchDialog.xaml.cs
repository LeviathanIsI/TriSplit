using System.Collections.Generic;
using System.Windows;
using TriSplit.Core.Interfaces;
using TriSplit.Desktop.Services;

namespace TriSplit.Desktop.Views.Dialogs;

public partial class PartialMatchDialog : Window
{
    private readonly string _profileName;
    public IReadOnlyList<string> MissingHeaders { get; }
    public IReadOnlyList<string> AdditionalHeaders { get; }
    public PartialMatchDecision Decision { get; private set; } = PartialMatchDecision.Cancel;

    public PartialMatchDialog(ProfileMatchCandidate candidate)
    {
        MissingHeaders = candidate.MissingHeaders;
        AdditionalHeaders = candidate.AdditionalHeaders;
        _profileName = candidate.Profile.Name;

        InitializeComponent();
        DataContext = this;

        IntroText.Text = $"The headers in '{_profileName}' changed. Update the profile or treat this as a new source?";

        if (MissingHeaders.Count == 0)
        {
            MissingItems.Visibility = Visibility.Collapsed;
            NoMissingText.Visibility = Visibility.Visible;
        }
        else
        {
            MissingItems.Visibility = Visibility.Visible;
            NoMissingText.Visibility = Visibility.Collapsed;
        }

        if (AdditionalHeaders.Count == 0)
        {
            AdditionalItems.Visibility = Visibility.Collapsed;
            NoAdditionalText.Visibility = Visibility.Visible;
        }
        else
        {
            AdditionalItems.Visibility = Visibility.Visible;
            NoAdditionalText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        Decision = PartialMatchDecision.UpdateExisting;
        DialogResult = true;
    }

    private void OnCreateNewClick(object sender, RoutedEventArgs e)
    {
        Decision = PartialMatchDecision.CreateNew;
        DialogResult = true;
    }
}
