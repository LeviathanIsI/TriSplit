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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApplicationLogger.LogShutdown();
        _host?.StopAsync();
        _host?.Dispose();
        base.OnExit(e);
    }
}

