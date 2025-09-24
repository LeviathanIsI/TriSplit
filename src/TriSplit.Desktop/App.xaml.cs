using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Windows;
using TriSplit.Core.Extensions;
using TriSplit.Desktop.Services;
using TriSplit.Desktop.ViewModels;
using TriSplit.Desktop.ViewModels.Tabs;
using TriSplit.Desktop.Views;
using TriSplit.Desktop.Views.Tabs;

namespace TriSplit.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private SplashScreenWindow? _splashWindow;
    private MainWindow? _mainWindow;
    private bool _splashAnimationCompleted;
    private bool _mainWindowReady;


    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _splashWindow = new SplashScreenWindow();
        _splashWindow.AnimationCompleted += OnSplashAnimationCompleted;
        _splashWindow.Show();

        // Register encoding provider once at startup (for Excel reading)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Register Core services
                services.AddTriSplitCore();

                // Register Desktop services
                services.AddSingleton<IApplicationBootstrapper, ApplicationBootstrapper>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<IAppSession, AppSession>();
                services.AddSingleton<IProfileDetectionService, ProfileDetectionService>();

                // Register ViewModels as singletons for state preservation
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<ViewModels.Tabs.ProfilesViewModel>();
                services.AddSingleton<TestViewModel>();
                services.AddSingleton<ProcessingViewModel>();

                // Register Views
                services.AddTransient<MainWindow>();
                services.AddTransient<Views.Tabs.ProfilesView>();
                services.AddTransient<TestView>();
                services.AddTransient<ProcessingView>();
            })
            .Build();

        _host.Start();

        try
        {
            // Initialize the application structure (create folders)
            var bootstrapper = _host.Services.GetRequiredService<IApplicationBootstrapper>();
            bootstrapper.Initialize();

            // Log application startup
            ApplicationLogger.LogStartup();
        }
        catch (Exception ex)
        {
            // If bootstrapping fails, show error and continue
            MessageBox.Show(
                $"Warning: Could not initialize application folders.\n\n{ex.Message}",
                "TriSplit Initialization",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _mainWindow = _host.Services.GetRequiredService<MainWindow>();
        _mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
        MainWindow = _mainWindow;
        _mainWindowReady = true;
        TryShowMainWindow();
    }

    private void OnSplashAnimationCompleted(object? sender, EventArgs e)
    {
        _splashAnimationCompleted = true;
        TryShowMainWindow();
    }

    private void TryShowMainWindow()
    {
        if (!_splashAnimationCompleted || !_mainWindowReady || _mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _splashWindow?.Close();
        _splashWindow = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            var session = _host.Services.GetService<IAppSession>();
            if (session != null)
            {
                session.LoadedFilePath = null;
            }
        }

        ApplicationLogger.LogShutdown();
        ApplicationLogger.Dispose();
        _splashWindow?.Close();
        _splashWindow = null;
        _host?.StopAsync();
        _host?.Dispose();
        base.OnExit(e);
    }
}
