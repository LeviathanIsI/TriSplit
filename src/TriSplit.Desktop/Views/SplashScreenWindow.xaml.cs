using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace TriSplit.Desktop.Views;

public partial class SplashScreenWindow : Window
{
    private bool _storyboardStarted;

    public event EventHandler? AnimationCompleted;

    public SplashScreenWindow()
    {
        InitializeComponent();
    }

    private void OnSplashLoaded(object sender, RoutedEventArgs e)
    {
        BeginAnimationSequence();
    }

    public void BeginAnimationSequence()
    {
        if (_storyboardStarted)
        {
            return;
        }

        if (Resources["SplashStoryboard"] is Storyboard storyboard)
        {
            storyboard.Completed += OnStoryboardCompleted;
            storyboard.Begin(this);
            _storyboardStarted = true;
        }
    }

    private void OnStoryboardCompleted(object? sender, EventArgs e)
    {
        AnimationCompleted?.Invoke(this, EventArgs.Empty);
    }
}
