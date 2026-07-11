using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevMX.App.ViewModels;

/// <summary>
/// ViewModel for the settings pane. Wraps DevMxSettings with undo support
/// and an Apply command that persists + triggers reconnect.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Action _onApply;
    private readonly Action<string>? _onThemeChanged;
    private bool _initialized;

    // Original values loaded at construction — used for merge-on-apply dirtiness tracking.
    private string _originalEndpoint;
    private string _originalModel;
    private string _originalProvider;
    private string _originalWorkDir;
    private string _originalTheme;
    private string _originalToolProfile;
    private string _originalPollThrottleSeconds;

    [ObservableProperty]
    private string endpoint;

    [ObservableProperty]
    private string model;

    [ObservableProperty]
    private string provider;

    [ObservableProperty]
    private string workDir;

    [ObservableProperty]
    private string theme;

    [ObservableProperty]
    private string toolProfile;

    [ObservableProperty]
    private string pollThrottleSeconds;

    // ===== Testable path-based constructor =====

    /// <summary>Settings file path (for testability; uses default when null).</summary>
    private string? _settingsPath;

    public string ServerExe => DevMxSettings.Load(_settingsPath ?? DevMxSettings.DefaultSettingsPath).ServerExe;
    public string ApiKeySource
    {
        get
        {
            var settings = DevMxSettings.Load(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
            return settings.Provider == "anthropic"
                ? "ANTHROPIC_API_KEY (environment variable)"
                : "OPENAI_COMPAT_API_KEY (environment variable)";
        }
    }

    /// <summary>Available theme options for the ComboBox.</summary>
    public IEnumerable<string> ThemeOptions => new[] { "dark", "light" };

    /// <summary>Available tool profile options.</summary>
    public IEnumerable<string> ToolProfileOptions => new[] { "auto", "full", "restricted" };

    /// <summary>Creates a SettingsViewModel backed by the given settings instance.</summary>
    /// <param name="settings">The DevMxSettings to edit.</param>
    /// <param name="onApply">Callback fired when Apply &amp; Reconnect is clicked.</param>
    /// <param name="onThemeChanged">Callback fired immediately when theme changes.</param>
    public SettingsViewModel(DevMxSettings settings, Action onApply, Action<string>? onThemeChanged = null)
        : this(settings, onApply, onThemeChanged, null)
    {
    }

    /// <summary>Creates a SettingsViewModel with an injectable settings path (for testing).</summary>
    public SettingsViewModel(DevMxSettings settings, Action onApply, Action<string>? onThemeChanged, string? settingsPath)
    {
        _onApply = onApply;
        _onThemeChanged = onThemeChanged;
        _settingsPath = settingsPath;

        // Record original values for dirtiness tracking
        _originalEndpoint = settings.Endpoint;
        _originalModel = settings.Model;
        _originalProvider = settings.Provider;
        _originalWorkDir = settings.WorkDir;
        _originalTheme = settings.Theme;
        _originalToolProfile = settings.ToolProfile;
        _originalPollThrottleSeconds = settings.PollThrottleSeconds.ToString();

        // Set VM fields (triggers OnPropertyChanged but persist handlers no-op until _initialized)
        Endpoint = settings.Endpoint;
        Model = settings.Model;
        Provider = settings.Provider;
        WorkDir = settings.WorkDir;
        Theme = settings.Theme;
        ToolProfile = settings.ToolProfile;
        PollThrottleSeconds = settings.PollThrottleSeconds.ToString();

        _initialized = true;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_initialized)
            return;
        if (e.PropertyName == nameof(Theme))
        {
            _onThemeChanged?.Invoke(Theme);
            // Persist theme immediately
            PersistTheme();
        }
    }

    [RelayCommand]
    private void SetTheme(string themeName)
    {
        Theme = themeName;
        // Update original so it doesn't count as dirty on Apply
        _originalTheme = Theme;
    }

    [RelayCommand]
    private void SetToolProfile(string profileName)
    {
        ToolProfile = profileName;
        // Persist immediately
        PersistToolProfile();
        // Update original so it doesn't count as dirty on Apply
        _originalToolProfile = ToolProfile;
    }

    private void PersistTheme()
    {
        var settings = DevMxSettings.Load(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
        settings.Theme = Theme;
        settings.Save(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
    }

    private void PersistToolProfile()
    {
        var settings = DevMxSettings.Load(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
        settings.ToolProfile = ToolProfile;
        settings.Save(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        // Reload fresh settings from disk
        var fresh = DevMxSettings.Load(_settingsPath ?? DevMxSettings.DefaultSettingsPath);

        // Merge: write VM value only if it differs from the original (user edited it this session)
        // Otherwise keep the fresh-disk value.
        fresh.Endpoint = (Endpoint != _originalEndpoint) ? Endpoint : fresh.Endpoint;
        fresh.Model = (Model != _originalModel) ? Model : fresh.Model;
        fresh.Provider = (Provider != _originalProvider) ? Provider : fresh.Provider;
        fresh.WorkDir = (WorkDir != _originalWorkDir) ? WorkDir : fresh.WorkDir;
        fresh.Theme = (Theme != _originalTheme) ? Theme : fresh.Theme;
        fresh.ToolProfile = (ToolProfile != _originalToolProfile) ? ToolProfile : fresh.ToolProfile;

        // Validate PollThrottleSeconds
        if (int.TryParse(PollThrottleSeconds, out int throttleVal))
        {
            // Only apply if user changed it from original
            fresh.PollThrottleSeconds = (PollThrottleSeconds != _originalPollThrottleSeconds)
                ? throttleVal
                : fresh.PollThrottleSeconds;
        }
        // else: keep fresh disk value

        // Save the merged settings
        fresh.Save(_settingsPath ?? DevMxSettings.DefaultSettingsPath);

        // Refresh VM fields + originals from the merged result
        Endpoint = fresh.Endpoint;
        Model = fresh.Model;
        Provider = fresh.Provider;
        WorkDir = fresh.WorkDir;
        Theme = fresh.Theme;
        ToolProfile = fresh.ToolProfile;
        PollThrottleSeconds = fresh.PollThrottleSeconds.ToString();

        // Update originals to match the merged result (so subsequent Apply is idempotent)
        _originalEndpoint = fresh.Endpoint;
        _originalModel = fresh.Model;
        _originalProvider = fresh.Provider;
        _originalWorkDir = fresh.WorkDir;
        _originalTheme = fresh.Theme;
        _originalToolProfile = fresh.ToolProfile;
        _originalPollThrottleSeconds = fresh.PollThrottleSeconds.ToString();

        _onApply();
    }

    private bool CanApply() => true;
}
