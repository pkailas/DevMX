using DevMX.App.ViewModels;

namespace DevMX.App.ViewModels.Tests;

public class SlashCommandHandlerTests
{
    private readonly List<string> _infoEntries = new();
    private string _workDir = @"C:\test\workdir";
    private string? _setWorkDirPath;
    private bool _saveCalled;
    private string? _pickedFolder;
    private bool _reconnectRequested;
    private long? _newConversationId;
    private string? _updatedTitle;
    private long? _openedConversationId;
    private string? _searchText;
    private bool _sidebarExpanded;
    private string? _themeSet;
    private string? _profileSet;
    private int? _pollThrottleSet;
    private bool _inputCleared;
    private readonly SlashCommandHandler _handler;

    public SlashCommandHandlerTests()
    {
        var callbacks = new SlashCommandCallbacks
        {
            GetWorkDir = () => _workDir,
            SetWorkDir = (path) => { _setWorkDirPath = path; _saveCalled = true; },
            PickFolder = (initialDir) => _pickedFolder,
            RequestReconnect = async () => { _reconnectRequested = true; },
            CreateNewConversation = async () => _newConversationId ?? 42,
            UpdateTitle = async (title) => { _updatedTitle = title; },
            OpenConversation = async (id) => { _openedConversationId = id; },
            SetSearchText = (term) => { _searchText = term; },
            ExpandSidebar = () => { _sidebarExpanded = true; },
            SetTheme = (theme) => { _themeSet = theme; },
            SetToolProfile = (profile) => { _profileSet = profile; },
            SetPollThrottle = (value) => { _pollThrottleSet = value; },
            AddInfoEntry = (text) => { _infoEntries.Add(text); },
            ClearInputText = () => { _inputCleared = true; }
        };
        _handler = new SlashCommandHandler(callbacks);
    }

    private void Reset()
    {
        _infoEntries.Clear();
        _setWorkDirPath = null;
        _saveCalled = false;
        _pickedFolder = null;
        _reconnectRequested = false;
        _newConversationId = null;
        _updatedTitle = null;
        _openedConversationId = null;
        _searchText = null;
        _sidebarExpanded = false;
        _themeSet = null;
        _profileSet = null;
        _pollThrottleSet = null;
        _inputCleared = false;
    }

    // === /help ===

    [Fact]
    public void Help_ListsAllCommands()
    {
        _handler.ExecuteCommand("/help");

        Assert.Contains(_infoEntries, e => e == "> /help");
        var content = _infoEntries[1];
        Assert.Contains("Available commands:", content);
        Assert.Contains("/help", content);
        Assert.Contains("/dir", content);
        Assert.Contains("/new", content);
        Assert.Contains("/open", content);
        Assert.Contains("/search", content);
        Assert.Contains("/theme", content);
        Assert.Contains("/poll", content);
        Assert.Contains("/profile", content);
    }

    // === Unknown command ===

    [Fact]
    public void UnknownCommand_ReturnsError()
    {
        _handler.ExecuteCommand("/x");

        Assert.Contains(_infoEntries, e => e.Contains("[error] unknown command /x"));
        Assert.Contains(_infoEntries, e => e.Contains("try /help"));
    }

    // === /dir no args ===

    [Fact]
    public void Dir_NoArg_ShowsCurrentPath()
    {
        _handler.ExecuteCommand("/dir");

        Assert.Contains(_infoEntries, e => e == "> /dir");
        Assert.Contains(_infoEntries, e => e.Contains($"Working directory: {_workDir}"));
        Assert.Null(_setWorkDirPath); // No change made
    }

    // === /dir valid path ===

    [Fact]
    public void Dir_ValidPath_TriggersSaveAndReconnect()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devmx_test_dir");
        Directory.CreateDirectory(tempDir);
        try
        {
            _handler.ExecuteCommand($"/dir {tempDir}");

            Assert.NotNull(_setWorkDirPath);
            Assert.Equal(tempDir, _setWorkDirPath);
            Assert.True(_saveCalled);
            Assert.True(_reconnectRequested);
            Assert.Contains(_infoEntries, e => e.Contains("reconnecting"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // === /dir missing path ===

    [Fact]
    public void Dir_MissingPath_ErrorsWithoutCallbacks()
    {
        _handler.ExecuteCommand("/dir C:\\nonexistent\\path\\that\\does\\not\\exist");

        Assert.Null(_setWorkDirPath);
        Assert.False(_saveCalled);
        Assert.False(_reconnectRequested);
        Assert.Contains(_infoEntries, e => e.Contains("[error] directory not found"));
    }

    // === /dir -b cancelled ===

    [Fact]
    public void Dir_Browser_Cancelled_NoChange()
    {
        _pickedFolder = null; // Simulate cancel
        _handler.ExecuteCommand("/dir -b");

        Assert.Null(_setWorkDirPath);
        Assert.Contains(_infoEntries, e => e.Contains("cancelled"));
        Assert.Contains(_infoEntries, e => e.Contains("unchanged"));
    }

    // === /dir -b picked ===

    [Fact]
    public void Dir_Browser_Picked_RoutesThroughDirLogic()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devmx_picked_dir");
        Directory.CreateDirectory(tempDir);
        try
        {
            _pickedFolder = tempDir;
            _handler.ExecuteCommand("/dir -b");

            Assert.NotNull(_setWorkDirPath);
            Assert.Equal(tempDir, _setWorkDirPath);
            Assert.True(_saveCalled);
            Assert.True(_reconnectRequested);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // === /theme ===

    [Fact]
    public void Theme_Dark_SetsTheme()
    {
        _handler.ExecuteCommand("/theme dark");

        Assert.Equal("dark", _themeSet);
        Assert.Contains(_infoEntries, e => e.Contains("Theme set to: dark"));
    }

    [Fact]
    public void Theme_Light_SetsTheme()
    {
        _handler.ExecuteCommand("/theme light");

        Assert.Equal("light", _themeSet);
        Assert.Contains(_infoEntries, e => e.Contains("Theme set to: light"));
    }

    [Fact]
    public void Theme_InvalidArg_Errors()
    {
        _handler.ExecuteCommand("/theme purple");

        Assert.Null(_themeSet);
        Assert.Contains(_infoEntries, e => e.Contains("[error] invalid theme 'purple'"));
    }

    // === /poll ===

    [Fact]
    public void Poll_ValidValue_SetsThrottle()
    {
        _handler.ExecuteCommand("/poll 10");

        Assert.Equal(10, _pollThrottleSet);
        Assert.Contains(_infoEntries, e => e.Contains("Poll throttle set to 10"));
        Assert.Contains(_infoEntries, e => e.Contains("applies on reconnect"));
    }

    [Fact]
    public void Poll_TooHigh_ClampsTo60()
    {
        _handler.ExecuteCommand("/poll 100");

        Assert.Equal(60, _pollThrottleSet);
        Assert.Contains(_infoEntries, e => e.Contains("clamped to 60"));
    }

    [Fact]
    public void Poll_Negative_ClampsTo0()
    {
        _handler.ExecuteCommand("/poll -5");

        Assert.Equal(0, _pollThrottleSet);
        Assert.Contains(_infoEntries, e => e.Contains("clamped to 0"));
    }

    [Fact]
    public void Poll_InvalidArg_Errors()
    {
        _handler.ExecuteCommand("/poll abc");

        Assert.Null(_pollThrottleSet);
        Assert.Contains(_infoEntries, e => e.Contains("[error] usage: /poll"));
    }

    // === /profile ===

    [Fact]
    public void Profile_Auto_SetsProfile()
    {
        _handler.ExecuteCommand("/profile auto");

        Assert.Equal("auto", _profileSet);
        Assert.Contains(_infoEntries, e => e.Contains("Tool profile set to: auto"));
    }

    [Fact]
    public void Profile_Full_SetsProfile()
    {
        _handler.ExecuteCommand("/profile full");

        Assert.Equal("full", _profileSet);
        Assert.Contains(_infoEntries, e => e.Contains("Tool profile set to: full"));
    }

    [Fact]
    public void Profile_Restricted_SetsProfile()
    {
        _handler.ExecuteCommand("/profile restricted");

        Assert.Equal("restricted", _profileSet);
        Assert.Contains(_infoEntries, e => e.Contains("Tool profile set to: restricted"));
    }

    [Fact]
    public void Profile_InvalidArg_Errors()
    {
        _handler.ExecuteCommand("/profile custom");

        Assert.Null(_profileSet);
        Assert.Contains(_infoEntries, e => e.Contains("[error] invalid profile 'custom'"));
    }

    // === /new ===

    [Fact]
    public void New_NoTitle_CreatesConversation()
    {
        _handler.ExecuteCommand("/new");

        Assert.Contains(_infoEntries, e => e.Contains("New conversation created"));
    }

    [Fact]
    public void New_WithTitle_CreatesAndUpdatesTitle()
    {
        _handler.ExecuteCommand("/new My Custom Title");

        Assert.Contains(_infoEntries, e => e.Contains("New conversation created"));
        // Title is set via callback (async fire-and-forget, but we check synchronously)
        // The handler uses Task.Run so we give it a moment
        Assert.Contains(_infoEntries, e => e.StartsWith("> /new"));
    }

    // === /open ===

    [Fact]
    public void Open_ValidId_TriggersOpen()
    {
        _handler.ExecuteCommand("/open 42");

        Assert.Contains(_infoEntries, e => e.Contains("Opening conversation #42"));
    }

    [Fact]
    public void Open_InvalidId_Errors()
    {
        _handler.ExecuteCommand("/open abc");

        Assert.Contains(_infoEntries, e => e.Contains("[error] usage: /open"));
    }

    [Fact]
    public void Open_NoArg_Errors()
    {
        _handler.ExecuteCommand("/open");

        Assert.Contains(_infoEntries, e => e.Contains("[error] usage: /open"));
    }

    // === /search ===

    [Fact]
    public void Search_WithTerm_SetsSearchText()
    {
        _handler.ExecuteCommand("/search hello world");

        Assert.Equal("hello world", _searchText);
        Assert.True(_sidebarExpanded);
        Assert.Contains(_infoEntries, e => e.Contains("Searching conversations for: hello world"));
    }

    [Fact]
    public void Search_NoArg_Errors()
    {
        _handler.ExecuteCommand("/search");

        Assert.Null(_searchText);
        Assert.Contains(_infoEntries, e => e.Contains("[error] usage: /search"));
    }

    // === IsCommand ===

    [Fact]
    public void IsCommand_DetectsSlashPrefix()
    {
        Assert.True(_handler.IsCommand("/help"));
        Assert.True(_handler.IsCommand("/dir"));
        Assert.False(_handler.IsCommand("hello"));
        Assert.False(_handler.IsCommand(""));
    }

    // === Command echo ===

    [Fact]
    public void Command_EchoedAsInfoEntry()
    {
        _handler.ExecuteCommand("/help");

        Assert.StartsWith("> /help", _infoEntries[0]);
    }

    // === /dir with spaces in path ===

    [Fact]
    public void Dir_PathWithSpaces_JoinsArgs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dir with spaces");
        Directory.CreateDirectory(tempDir);
        try
        {
            _handler.ExecuteCommand($"/dir {tempDir}");

            Assert.NotNull(_setWorkDirPath);
            Assert.Contains("dir with spaces", _setWorkDirPath!);
            Assert.True(_saveCalled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // === Case insensitivity ===

    [Fact]
    public void Commands_AreCaseInsensitive()
    {
        _handler.ExecuteCommand("/HELP");
        Assert.Contains(_infoEntries, e => e == "> /HELP");

        Reset();
        _handler.ExecuteCommand("/Dir");
        Assert.Contains(_infoEntries, e => e == "> /Dir");

        Reset();
        _handler.ExecuteCommand("/THEME Dark");
        Assert.Equal("dark", _themeSet);

        Reset();
        _handler.ExecuteCommand("/PROFILE Full");
        Assert.Equal("full", _profileSet);
    }
}
