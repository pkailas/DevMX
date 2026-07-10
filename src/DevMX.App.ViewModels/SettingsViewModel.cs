using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevMX.App.ViewModels;

/// <summary>
/// ViewModel for the settings pane. Wraps DevMxSettings with undo support
/// and an Apply command that persists + triggers reconnect.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DevMxSettings _settings;
    private readonly Action _onApply;

    [ObservableProperty]
    private string endpoint;

    [ObservableProperty]
    private string model;

    [ObservableProperty]
    private string provider;

    [ObservableProperty]
    private string workDir;

    public string ServerExe => _settings.ServerExe;
    public string ApiKeySource => _settings.Provider == "anthropic"
        ? "ANTHROPIC_API_KEY (environment variable)"
        : "OPENAI_COMPAT_API_KEY (environment variable)";

    /// <summary>Creates a SettingsViewModel backed by the given settings instance.</summary>
    /// <param name="settings">The DevMxSettings to edit.</param>
    /// <param name="onApply">Callback fired when Apply &amp; Reconnect is clicked.</param>
    public SettingsViewModel(DevMxSettings settings, Action onApply)
    {
        _settings = settings;
        _onApply = onApply;
        Endpoint = settings.Endpoint;
        Model = settings.Model;
        Provider = settings.Provider;
        WorkDir = settings.WorkDir;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        // Persist changes to the backing settings object
        _settings.Endpoint = Endpoint;
        _settings.Model = Model;
        _settings.Provider = Provider;
        _settings.WorkDir = WorkDir;
        _settings.Save();
        _onApply();
    }

    private bool CanApply() => true;
}
