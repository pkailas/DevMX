using System.Configuration;
using System.Data;
using System.Windows;
using DevMX.App.ViewModels;

namespace DevMX.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply saved theme before UI renders
        var settings = DevMxSettings.Load();
        ThemeManager.Apply(settings.Theme);
    }
}

