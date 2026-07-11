using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;
using DevMX.App.ViewModels;

namespace DevMX.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance mutex: prevent multiple app instances from running concurrently
        bool createdNew;
        _mutex = new Mutex(true, "DevMX.App.SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DevMX is already running.", "DevMX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Apply saved theme before UI renders
        var settings = DevMxSettings.Load();
        ThemeManager.Apply(settings.Theme);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

