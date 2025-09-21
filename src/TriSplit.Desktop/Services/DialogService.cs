using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
            var result = MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return result == MessageBoxResult.Yes;
        });
    }

    public Task ShowMessageAsync(string title, string message)
    {
        return InvokeOnDispatcherAsync(() =>
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
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
            var window = new Window
            {
                Title = title,
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Owner = GetActiveWindow()
            };

            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(10)
            };

            var label = new System.Windows.Controls.Label
            {
                Content = prompt
            };

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = defaultValue,
                Margin = new Thickness(0, 5, 0, 10)
            };

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 75,
                Margin = new Thickness(0, 0, 5, 0),
                IsDefault = true
            };

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 75,
                IsCancel = true
            };

            string? result = null;

            okButton.Click += (s, e) =>
            {
                result = textBox.Text;
                window.DialogResult = true;
            };

            cancelButton.Click += (s, e) =>
            {
                window.DialogResult = false;
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            panel.Children.Add(label);
            panel.Children.Add(textBox);
            panel.Children.Add(buttonPanel);

            window.Content = panel;

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
}

