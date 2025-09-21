using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TriSplit.Core.Interfaces;
using TriSplit.Desktop.Views.Dialogs;

namespace TriSplit.Desktop.Services;

public class DialogService : IDialogService
{
    public Task<string?> ShowOpenFileDialogAsync(string title, string filter)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter
            };

            var result = dialog.ShowDialog();
            return result == true ? dialog.FileName : null;
        });
    }

    public Task<string?> ShowSaveFileDialogAsync(string title, string filter, string defaultFileName = "")
    {
        return InvokeOnDispatcherAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = defaultFileName
            };

            var result = dialog.ShowDialog();
            return result == true ? dialog.FileName : null;
        });
    }

    public Task<bool> ShowConfirmationDialogAsync(string title, string message)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            var dialog = new MessageDialog(title, message, "Yes", "No")
            {
                Owner = GetActiveWindow()
            };

            var result = dialog.ShowDialog();
            return result == true && dialog.PrimaryInvoked;
        });
    }

    public Task ShowMessageAsync(string title, string message)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            var dialog = new MessageDialog(title, message, "OK")
            {
                Owner = GetActiveWindow()
            };

            dialog.ShowDialog();
        });
    }

    public Task<ProfileMatchCandidate?> ShowProfileSelectionDialogAsync(IReadOnlyList<ProfileMatchCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return Task.FromResult<ProfileMatchCandidate?>(null);
        }

        return InvokeOnDispatcherAsync(() =>
        {
            var dialog = new ProfileSelectionDialog(candidates)
            {
                Owner = GetActiveWindow()
            };

            var result = dialog.ShowDialog();
            return result == true ? dialog.Result : null;
        });
    }

    public Task<PartialMatchDecision> ShowPartialMatchDialogAsync(ProfileMatchCandidate candidate)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            var dialog = new PartialMatchDialog(candidate)
            {
                Owner = GetActiveWindow()
            };

            var result = dialog.ShowDialog();
            return result == true ? dialog.Decision : PartialMatchDecision.Cancel;
        });
    }

    public Task<string?> ShowInputDialogAsync(string title, string prompt, string defaultValue = "")
    {
        return InvokeOnDispatcherAsync(() =>
        {
            var owner = GetActiveWindow();
            var surfaceBrush = GetBrush("SurfaceBrush", Brushes.WhiteSmoke);
            var backgroundBrush = GetBrush("BackgroundBrush", Brushes.White);
            var textBrush = GetBrush("TextBrush", Brushes.Black);
            var borderBrush = GetBrush("BorderBrush", Brushes.Gray);
            var primaryButtonStyle = GetStyle("PrimaryButton");
            var secondaryButtonStyle = GetStyle("SecondaryButton");

            var window = new Window
            {
                Title = title,
                Owner = owner,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Background = surfaceBrush,
                Foreground = textBrush,
                MinWidth = 420
            };

            var border = new Border
            {
                Background = surfaceBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24)
            };

            var panel = new StackPanel();

            var promptText = new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var textBox = new TextBox
            {
                Text = defaultValue,
                Background = backgroundBrush,
                Foreground = textBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                MinWidth = 320,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button
            {
                Content = "OK",
                Width = 110,
                IsDefault = true,
                Style = primaryButtonStyle,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 110,
                IsCancel = true,
                Style = secondaryButtonStyle
            };

            string? result = null;

            okButton.Click += (_, _) =>
            {
                result = textBox.Text;
                window.DialogResult = true;
            };

            cancelButton.Click += (_, _) =>
            {
                window.DialogResult = false;
            };

            window.Loaded += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            panel.Children.Add(promptText);
            panel.Children.Add(textBox);
            panel.Children.Add(buttonPanel);

            border.Child = panel;
            window.Content = border;

            return window.ShowDialog() == true ? result : null;
        });
    }

    private static Window? GetActiveWindow()
    {
        var app = Application.Current;
        if (app == null)
        {
            return null;
        }

        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return active ?? app.MainWindow;
    }

    private static Task InvokeOnDispatcherAsync(Action callback)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            callback();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(callback).Task;
    }

    private static Task<TResult> InvokeOnDispatcherAsync<TResult>(Func<TResult> callback)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return Task.FromResult(callback());
        }

        return dispatcher.InvokeAsync(callback).Task;
    }

    private static Brush GetBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private static Style? GetStyle(string resourceKey)
    {
        return Application.Current?.TryFindResource(resourceKey) as Style;
    }
}