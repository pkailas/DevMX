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
    private string _originalVisionEndpoint;
    private string _originalVisionModel;
    private string _originalProvider;
    private string _originalWorkDir;
    private string _originalTheme;
    private string _originalToolProfile;
    private string _originalPollThrottleSeconds;
    private string _originalCompactThresholdTokens;
    private int _originalFontSize;

    [ObservableProperty]
    private string endpoint;

    [ObservableProperty]
    private string model;

    [ObservableProperty]
    private string visionEndpoint;

    [ObservableProperty]
    private string visionModel;

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

    [ObservableProperty]
    private string compactThresholdTokens;

    [ObservableProperty]
    private int fontSize;

    /// <summary>Predefined font size levels (like Edge zoom levels). Base = 13 (100%).</summary>
    private static readonly int[] FontSizeLevels = { 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24 };

    /// <summary>Default/base font size (100% zoom).</summary>
    private const int DefaultFontSize = 13;

    /// <summary>Computed zoom percentage text (e.g. "100%", "115%").</summary>
    public string ZoomDisplayText => $"{Math.Round((FontSize / (double)DefaultFontSize) * 100)}%";

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
        _originalVisionEndpoint = settings.VisionEndpoint;
        _originalVisionModel = settings.VisionModel;
        _originalProvider = settings.Provider;
        _originalWorkDir = settings.WorkDir;
        _originalTheme = settings.Theme;
        _originalToolProfile = settings.ToolProfile;
        _originalPollThrottleSeconds = settings.PollThrottleSeconds.ToString();
        _originalCompactThresholdTokens = settings.CompactThresholdTokens.ToString();
        _originalFontSize = settings.FontSize;

        // Set VM fields (triggers OnPropertyChanged but persist handlers no-op until _initialized)
        Endpoint = settings.Endpoint;
        Model = settings.Model;
        VisionEndpoint = settings.VisionEndpoint;
        VisionModel = settings.VisionModel;
        Provider = settings.Provider;
        WorkDir = settings.WorkDir;
        Theme = settings.Theme;
        ToolProfile = settings.ToolProfile;
        PollThrottleSeconds = settings.PollThrottleSeconds.ToString();
        CompactThresholdTokens = settings.CompactThresholdTokens.ToString();
        FontSize = settings.FontSize;

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
        if (e.PropertyName == nameof(FontSize))
        {
            // Persist font size immediately
            PersistFontSize();
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

    private void PersistFontSize()
    {
        var settings = DevMxSettings.Load(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
        settings.FontSize = FontSize;
        settings.Save(_settingsPath ?? DevMxSettings.DefaultSettingsPath);
    }

    [RelayCommand]
    private void IncreaseFontSize()
    {
        int index = Array.IndexOf(FontSizeLevels, FontSize);
        if (index >= 0 && index < FontSizeLevels.Length - 1)
        {
            FontSize = FontSizeLevels[index + 1];
            _originalFontSize = FontSize;
        }
    }

    [RelayCommand]
    private void DecreaseFontSize()
    {
        int index = Array.IndexOf(FontSizeLevels, FontSize);
        if (index > 0)
        {
            FontSize = FontSizeLevels[index - 1];
            _originalFontSize = FontSize;
        }
    }

    [RelayCommand]
    private void ResetFontSize()
    {
        FontSize = DefaultFontSize;
        _originalFontSize = FontSize;
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
        fresh.VisionEndpoint = (VisionEndpoint != _originalVisionEndpoint) ? VisionEndpoint : fresh.VisionEndpoint;
        fresh.VisionModel = (VisionModel != _originalVisionModel) ? VisionModel : fresh.VisionModel;
        fresh.Provider = (Provider != _originalProvider) ? Provider : fresh.Provider;
        fresh.WorkDir = (WorkDir != _originalWorkDir) ? WorkDir : fresh.WorkDir;
        fresh.Theme = (Theme != _originalTheme) ? Theme : fresh.Theme;
        fresh.ToolProfile = (ToolProfile != _originalToolProfile) ? ToolProfile : fresh.ToolProfile;
        fresh.FontSize = (FontSize != _originalFontSize) ? FontSize : fresh.FontSize;

        // Validate PollThrottleSeconds
        if (int.TryParse(PollThrottleSeconds, out int throttleVal))
        {
            // Only apply if user changed it from original
            fresh.PollThrottleSeconds = (PollThrottleSeconds != _originalPollThrottleSeconds)
                ? throttleVal
                : fresh.PollThrottleSeconds;
        }
        // else: keep fresh disk value

        // Validate CompactThresholdTokens (0 = off)
        if (int.TryParse(CompactThresholdTokens, out int compactVal) && compactVal >= 0)
        {
            fresh.CompactThresholdTokens = (CompactThresholdTokens != _originalCompactThresholdTokens)
                ? compactVal
                : fresh.CompactThresholdTokens;
        }

        // Save the merged settings
        fresh.Save(_settingsPath ?? DevMxSettings.DefaultSettingsPath);

        // Refresh VM fields + originals from the merged result
        Endpoint = fresh.Endpoint;
        Model = fresh.Model;
        VisionEndpoint = fresh.VisionEndpoint;
        VisionModel = fresh.VisionModel;
        Provider = fresh.Provider;
        WorkDir = fresh.WorkDir;
        Theme = fresh.Theme;
        ToolProfile = fresh.ToolProfile;
        PollThrottleSeconds = fresh.PollThrottleSeconds.ToString();
        CompactThresholdTokens = fresh.CompactThresholdTokens.ToString();
        FontSize = fresh.FontSize;

        // Update originals to match the merged result (so subsequent Apply is idempotent)
        _originalEndpoint = fresh.Endpoint;
        _originalModel = fresh.Model;
        _originalVisionEndpoint = fresh.VisionEndpoint;
        _originalVisionModel = fresh.VisionModel;
        _originalProvider = fresh.Provider;
        _originalWorkDir = fresh.WorkDir;
        _originalTheme = fresh.Theme;
        _originalToolProfile = fresh.ToolProfile;
        _originalPollThrottleSeconds = fresh.PollThrottleSeconds.ToString();
        _originalCompactThresholdTokens = fresh.CompactThresholdTokens.ToString();
        _originalFontSize = fresh.FontSize;

        _onApply();
    }

    private bool CanApply() => true;
}
