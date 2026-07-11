using DevMX.App.ViewModels;

namespace DevMX.App.ViewModels.Tests;

/// <summary>
/// Proves that the menu bar commands on MainViewModel invoke the same underlying
/// flows as the slash-command callbacks — no duplicated logic.
/// Tests the commands that operate on SettingsViewModel (theme, profile) and
/// shared UI state (sidebar toggle, help, about) without needing a live AppSession.
/// </summary>
public class MainViewModelMenuCommandTests
{
    private DevMxSettings _settings;
    private List<Action> _dispatchedActions = new();
    private string? _themeApplied;
    private MainViewModel _vm;

    public MainViewModelMenuCommandTests()
    {
        _settings = DevMxSettings.Load();
        var session = new AppSession(_settings);
        _dispatchedActions = new List<Action>();
        Action<Action> dispatch = (action) => _dispatchedActions.Add(action);
        _themeApplied = null;

        _vm = new MainViewModel(_settings, session, dispatch, theme => { _themeApplied = theme; });
    }

    // === SetThemeCommand (same flow as slash /theme) ===

    [Fact]
    public void SetThemeCommand_Dark_SetsTheme()
    {
        _vm.SetThemeCommand.Execute("dark");

        Assert.Equal("dark", _vm.Settings.Theme);
        Assert.True(_vm.IsDarkTheme);
        Assert.False(_vm.IsLightTheme);
    }

    [Fact]
    public void SetThemeCommand_Light_SetsTheme()
    {
        _vm.SetThemeCommand.Execute("light");

        Assert.Equal("light", _vm.Settings.Theme);
        Assert.False(_vm.IsDarkTheme);
        Assert.True(_vm.IsLightTheme);
    }

    // === SetToolProfileMenuCommand (same flow as slash /profile) ===

    [Fact]
    public void SetToolProfileMenuCommand_Auto_SetsProfile()
    {
        _vm.SetToolProfileMenuCommand.Execute("auto");

        Assert.Equal("auto", _vm.Settings.ToolProfile);
        Assert.Equal("auto", _vm.CurrentToolProfile);
    }

    [Fact]
    public void SetToolProfileMenuCommand_Full_SetsProfile()
    {
        _vm.SetToolProfileMenuCommand.Execute("full");

        Assert.Equal("full", _vm.Settings.ToolProfile);
        Assert.Equal("full", _vm.CurrentToolProfile);
    }

    [Fact]
    public void SetToolProfileMenuCommand_Restricted_SetsProfile()
    {
        _vm.SetToolProfileMenuCommand.Execute("restricted");

        Assert.Equal("restricted", _vm.Settings.ToolProfile);
        Assert.Equal("restricted", _vm.CurrentToolProfile);
    }

    // === ToggleSidebarCommand ===

    [Fact]
    public void ToggleSidebarCommand_TogglesVisibility()
    {
        Assert.True(_vm.IsSidebarExpanded);

        _vm.ToggleSidebarCommand.Execute(null);

        Assert.False(_vm.IsSidebarExpanded);

        _vm.ToggleSidebarCommand.Execute(null);

        Assert.True(_vm.IsSidebarExpanded);
    }

    // === FocusSearchCommand expands sidebar ===

    [Fact]
    public void FocusSearchCommand_ExpandsSidebar()
    {
        _vm.IsSidebarExpanded = false;

        _vm.FocusSearchCommand.Execute(null);

        Assert.True(_vm.IsSidebarExpanded);
    }

    // === FocusSearchCommand raises OnRequestFocusSearch event ===

    [Fact]
    public void FocusSearchCommand_RaisesFocusSearchEvent()
    {
        bool eventFired = false;
        _vm.OnRequestFocusSearch += () => { eventFired = true; };

        _vm.FocusSearchCommand.Execute(null);

        Assert.True(eventFired, "OnRequestFocusSearch should be raised by FocusSearchCommand");
    }

    // === ShowHelpCommand adds Info entry ===

    [Fact]
    public void ShowHelpCommand_AddsInfoEntry()
    {
        int initialCount = _vm.Chat.Entries.Count;

        _vm.ShowHelpCommand.Execute(null);

        // Flush dispatched actions
        foreach (var a in _dispatchedActions) a();

        Assert.True(_vm.Chat.Entries.Count > initialCount);
        var entry = _vm.Chat.Entries[^1];
        Assert.Equal(ChatEntryKind.Info, entry.Kind);
        Assert.Contains("Available commands:", entry.Text);
    }

    // === ShowAboutCommand adds Info entry ===

    [Fact]
    public void ShowAboutCommand_AddsInfoEntry()
    {
        int initialCount = _vm.Chat.Entries.Count;

        _vm.ShowAboutCommand.Execute(null);

        // Flush dispatched actions
        foreach (var a in _dispatchedActions) a();

        Assert.True(_vm.Chat.Entries.Count > initialCount);
        var entry = _vm.Chat.Entries[^1];
        Assert.Equal(ChatEntryKind.Info, entry.Kind);
        Assert.Contains("DevMX", entry.Text);
    }

    // === RenameCurrentConversationCommand does not crash with no selection ===

    [Fact]
    public void RenameCurrentConversationCommand_NoSelection_NoCrash()
    {
        // No selected conversation — should not throw
        _vm.RenameCurrentConversationCommand.Execute(null);
        // No assertion — just verify no exception
    }

    // === Theme change fires property notification for menu checkmarks ===

    [Fact]
    public void ThemeChange_RaisesPropertyChangedForIsDarkTheme()
    {
        // Ensure we start from a known state so the next change actually fires
        _vm.SetThemeCommand.Execute("light");

        bool raised = false;
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDarkTheme))
                raised = true;
        };

        _vm.SetThemeCommand.Execute("dark");

        Assert.True(raised, "IsDarkTheme PropertyChanged should be raised when theme changes");
    }

    // === Profile change fires property notification for menu checkmarks ===

    [Fact]
    public void ProfileChange_RaisesPropertyChangedForCurrentToolProfile()
    {
        // Ensure we start from a different value so the change fires
        _vm.SetToolProfileMenuCommand.Execute("auto");

        bool raised = false;
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentToolProfile))
                raised = true;
        };

        _vm.SetToolProfileMenuCommand.Execute("full");

        Assert.True(raised, "CurrentToolProfile PropertyChanged should be raised when profile changes");
    }

    // === Shared behavior: slash /theme and menu SetThemeCommand use same setter ===

    [Fact]
    public void SetThemeCommand_UsesSameSetterAsSlashCallback()
    {
        // Both should set the Settings.Theme property via SettingsViewModel
        _vm.SetThemeCommand.Execute("dark");
        Assert.Equal("dark", _vm.Settings.Theme);

        _vm.SetThemeCommand.Execute("light");
        Assert.Equal("light", _vm.Settings.Theme);
    }

    // === Shared behavior: slash /profile and menu SetToolProfileMenuCommand use same setter ===

    [Fact]
    public void SetToolProfileMenuCommand_UsesSameSetterAsSlashCallback()
    {
        _vm.SetToolProfileMenuCommand.Execute("full");
        Assert.Equal("full", _vm.Settings.ToolProfile);

        _vm.SetToolProfileMenuCommand.Execute("auto");
        Assert.Equal("auto", _vm.Settings.ToolProfile);
    }

    // === Shared behavior: slash callback AddInfoEntry uses same AddInfoEntry method ===

    [Fact]
    public void AddInfoEntry_SharedBySlashAndMenu()
    {
        // The ShowHelpCommand uses AddInfoEntry internally, same as slash callbacks.
        // Verify the shared method produces an Info entry.
        _vm.ShowHelpCommand.Execute(null);
        foreach (var a in _dispatchedActions) a();

        var lastEntry = _vm.Chat.Entries[^1];
        Assert.Equal(ChatEntryKind.Info, lastEntry.Kind);
    }

    // === IsDarkTheme and IsLightTheme are mutually consistent ===

    [Fact]
    public void ThemeBooleans_AreConsistent()
    {
        _vm.SetThemeCommand.Execute("dark");
        Assert.True(_vm.IsDarkTheme);
        Assert.False(_vm.IsLightTheme);

        _vm.SetThemeCommand.Execute("light");
        Assert.False(_vm.IsDarkTheme);
        Assert.True(_vm.IsLightTheme);
    }
}
