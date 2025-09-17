using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;
using TriSplit.Core.Extensions;
using TriSplit.Desktop.Services;
using TriSplit.Desktop.ViewModels;
using TriSplit.Desktop.Views;

namespace TriSplit.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Register Core services
                services.AddTriSplitCore();

                // Register Desktop services
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<IAppSession, AppSession>();

                // Register ViewModels as singletons for state preservation
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<ProfilesViewModel>();

                // Register Views
                services.AddTransient<MainWindow>();
                services.AddTransient<ProfilesView>();
            })
            .Build();

        _host.Start();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync();
        _host?.Dispose();
        base.OnExit(e);
    }
}

