using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
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

        ApplyIntroText();
        UpdateVisibilityStates();
    }

    private void ApplyIntroText()
    {
        var accentBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.Gold;

        IntroText.Inlines.Clear();
        IntroText.Inlines.Add(new Run("The headers in "));
        IntroText.Inlines.Add(new Run($"'{_profileName}'")
        {
            Foreground = accentBrush,
            FontWeight = FontWeights.SemiBold
        });
        IntroText.Inlines.Add(new Run(" changed. Update the profile or treat this as a new source?"));
    }

    private void UpdateVisibilityStates()
    {
        if (MissingHeaders.Count == 0)
        {
            MissingContainer.Visibility = Visibility.Collapsed;
            MissingItems.Visibility = Visibility.Collapsed;
            NoMissingText.Visibility = Visibility.Visible;
        }
        else
        {
            MissingContainer.Visibility = Visibility.Visible;
            MissingItems.Visibility = Visibility.Visible;
            NoMissingText.Visibility = Visibility.Collapsed;
        }

        if (AdditionalHeaders.Count == 0)
        {
            AdditionalContainer.Visibility = Visibility.Collapsed;
            AdditionalItems.Visibility = Visibility.Collapsed;
            NoAdditionalText.Visibility = Visibility.Visible;
        }
        else
        {
            AdditionalContainer.Visibility = Visibility.Visible;
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