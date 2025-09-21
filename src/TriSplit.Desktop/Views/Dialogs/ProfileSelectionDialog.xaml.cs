using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TriSplit.Core.Interfaces;

namespace TriSplit.Desktop.Views.Dialogs;

public partial class ProfileSelectionDialog : Window
{
    public ProfileMatchCandidate? Result { get; private set; }

    private readonly List<ProfileMatchOption> _options;

    public ProfileSelectionDialog(IEnumerable<ProfileMatchCandidate> candidates)
    {
        InitializeComponent();

        _options = candidates
            .Select(c => new ProfileMatchOption(c))
            .ToList();

        MatchesList.ItemsSource = _options;
        MatchesList.SelectionChanged += OnSelectionChanged;

        if (_options.Count > 0)
        {
            MatchesList.SelectedIndex = 0;
        }

        UpdateButtonState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtonState();
    }

    private void UpdateButtonState()
    {
        SelectButton.IsEnabled = MatchesList.SelectedItem != null;
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        if (MatchesList.SelectedItem is ProfileMatchOption option)
        {
            Result = option.Candidate;
            DialogResult = true;
        }
    }

    private sealed class ProfileMatchOption
    {
        public ProfileMatchOption(ProfileMatchCandidate candidate)
        {
            Candidate = candidate;
            Title = $"{candidate.Profile.Name} ({candidate.Score:P0})";

            var missingPreview = candidate.MissingHeaders.Count == 0
                ? "Missing: none"
                : $"Missing: {FormatList(candidate.MissingHeaders)}";

            var extraPreview = candidate.AdditionalHeaders.Count == 0
                ? "Extra: none"
                : $"Extra: {FormatList(candidate.AdditionalHeaders)}";

            Details = $"{missingPreview} | {extraPreview}";
        }

        public string Title { get; }
        public string Details { get; }
        public ProfileMatchCandidate Candidate { get; }

        private static string FormatList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            if (values.Count <= 3)
            {
                return string.Join(", ", values);
            }

            var preview = string.Join(", ", values.Take(3));
            var remaining = values.Count - 3;
            return $"{preview} (+{remaining} more)";
        }
    }
}
