using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

/// <summary>
/// Callbacks required by SlashCommandHandler to interact with the app.
/// All callbacks are pure delegates — no System.Windows dependencies.
/// </summary>
public sealed class SlashCommandCallbacks
{
    /// <summary>Get the current working directory from settings.</summary>
    public Func<string> GetWorkDir;

    /// <summary>Set the working directory in settings and persist.</summary>
    public Action<string> SetWorkDir;

    /// <summary>Open a folder picker dialog; returns picked path or null if cancelled.</summary>
    public Func<string, string?> PickFolder;

    /// <summary>Trigger the same reconnect flow as "Apply &amp; Reconnect".</summary>
    public Func<Task> RequestReconnect;

    /// <summary>Create a new conversation (sidebar flow). Returns the new conversation id.</summary>
    public Func<Task<long>> CreateNewConversation;

    /// <summary>Update the title of the current conversation and mark it titled.</summary>
    public Func<string, Task> UpdateTitle;

    /// <summary>Open a conversation by numeric ID (sidebar switch flow).</summary>
    public Func<long, Task> OpenConversation;

    /// <summary>Set the sidebar search text (expands sidebar if collapsed).</summary>
    public Action<string> SetSearchText;

    /// <summary>Expand the sidebar if collapsed (for /search).</summary>
    public Action? ExpandSidebar;

    /// <summary>Set the theme (same path as Settings buttons).</summary>
    public Action<string> SetTheme;

    /// <summary>Set the tool profile (same path as Settings buttons).</summary>
    public Action<string> SetToolProfile;

    /// <summary>Set the poll throttle seconds (clamped 0..60) and persist.</summary>
    public Action<int> SetPollThrottle;

    /// <summary>Add an Info entry to the chat entries collection.</summary>
    public Action<string> AddInfoEntry;

    /// <summary>Clear the chat input text field.</summary>
    public Action ClearInputText;

    public SlashCommandCallbacks()
    {
        GetWorkDir = () => string.Empty;
        SetWorkDir = _ => { };
        PickFolder = _ => (string?)null;
        RequestReconnect = async () => { };
        CreateNewConversation = async () => 0L;
        UpdateTitle = _ => Task.CompletedTask;
        OpenConversation = _ => Task.CompletedTask;
        SetSearchText = _ => { };
        ExpandSidebar = null!;
        SetTheme = _ => { };
        SetToolProfile = _ => { };
        SetPollThrottle = _ => { };
        AddInfoEntry = _ => { };
        ClearInputText = () => { };
    }
}

/// <summary>
/// Handles slash commands entered in the chat input.
/// Commands are intercepted before reaching the model, never persisted, and produce Info entries only.
/// </summary>
public sealed class SlashCommandHandler
{
    private readonly SlashCommandCallbacks _callbacks;

    public SlashCommandHandler(SlashCommandCallbacks callbacks)
    {
        _callbacks = callbacks;
    }

    /// <summary>
    /// Returns true if the text is a slash command (starts with "/").
    /// </summary>
    public bool IsCommand(string text)
    {
        return text.StartsWith("/");
    }

    /// <summary>
    /// Executes the slash command. Returns true if handled, false if not a recognized command.
    /// The command echo and result are added as Info entries via callbacks.
    /// </summary>
    public bool ExecuteCommand(string rawText)
    {
        var text = rawText.Trim();
        if (!text.StartsWith("/"))
            return false;

        // Echo the command as a dim Info line
        _callbacks.AddInfoEntry($"> {text}");

        // Parse command and arguments
        var parts = text[1..].Split(new[] { ' ' }, 2);
        string command = parts[0].ToLowerInvariant();
        string? args = parts.Length > 1 ? parts[1].Trim() : null;

        return command switch
        {
            "help" => HandleHelp(),
            "dir" => HandleDir(args),
            "new" => HandleNew(args),
            "open" => HandleOpen(args),
            "search" => HandleSearch(args),
            "theme" => HandleTheme(args),
            "poll" => HandlePoll(args),
            "profile" => HandleProfile(args),
            _ => HandleUnknown(text)
        };
    }

    private bool HandleHelp()
    {
        var lines = new[]
        {
            "Available commands:",
            "  /help              Show this help message",
            "  /dir               Show current working directory",
            "  /dir <path>        Change working directory (reconnects)",
            "  /dir -b            Pick directory via folder browser",
            "  /new [title]       Create new conversation",
            "  /open <id>         Open conversation by numeric ID",
            "  /search <term>     Search conversations (expands sidebar)",
            "  /theme dark|light  Switch theme",
            "  /poll <n>          Set poll throttle (0-60, applies on reconnect)",
            "  /profile auto|full|restricted  Set tool profile (applies on reconnect)"
        };
        _callbacks.AddInfoEntry(string.Join("\n", lines));
        return true;
    }

    private bool HandleDir(string? args)
    {
        if (string.IsNullOrEmpty(args))
        {
            // No args — show current working directory
            var workDir = _callbacks.GetWorkDir();
            _callbacks.AddInfoEntry($"Working directory: {workDir}");
            return true;
        }

        if (args == "-b")
        {
            // Folder picker
            var currentDir = _callbacks.GetWorkDir();
            var picked = _callbacks.PickFolder(currentDir);
            if (string.IsNullOrEmpty(picked))
            {
                _callbacks.AddInfoEntry("Directory pick cancelled — working directory unchanged.");
                return true;
            }
            // Route through the same logic as /dir <path>
            return HandleDirPath(picked);
        }

        // Path argument — may contain spaces, so args is the full remainder
        return HandleDirPath(args);
    }

    private bool HandleDirPath(string path)
    {
        string? resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _callbacks.AddInfoEntry($"[error] invalid path '{path}': {ex.Message}");
            return true;
        }

        if (!Directory.Exists(resolvedPath))
        {
            _callbacks.AddInfoEntry($"[error] directory not found: {resolvedPath}");
            return true;
        }

        _callbacks.SetWorkDir(resolvedPath);
        _callbacks.AddInfoEntry($"Working directory changed to: {resolvedPath} — reconnecting…");
        _ = _callbacks.RequestReconnect();
        return true;
    }

    private bool HandleNew(string? args)
    {
        // Fire-and-forget: create new conversation, optionally set title
        _ = Task.Run(async () =>
        {
            try
            {
                await _callbacks.CreateNewConversation();
                if (!string.IsNullOrEmpty(args))
                {
                    await _callbacks.UpdateTitle(args!);
                }
            }
            catch (Exception ex)
            {
                _callbacks.AddInfoEntry($"[error] new conversation failed: {ex.Message}");
            }
        });
        _callbacks.AddInfoEntry("New conversation created.");
        return true;
    }

    private bool HandleOpen(string? args)
    {
        if (string.IsNullOrEmpty(args) || !long.TryParse(args, out long id))
        {
            _callbacks.AddInfoEntry("[error] usage: /open <id>");
            return true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _callbacks.OpenConversation(id);
            }
            catch (Exception ex)
            {
                _callbacks.AddInfoEntry($"[error] open failed: {ex.Message}");
            }
        });
        _callbacks.AddInfoEntry($"Opening conversation #{id}…");
        return true;
    }

    private bool HandleSearch(string? args)
    {
        if (string.IsNullOrEmpty(args))
        {
            _callbacks.AddInfoEntry("[error] usage: /search <term>");
            return true;
        }

        _callbacks.ExpandSidebar?.Invoke();
        _callbacks.SetSearchText(args);
        _callbacks.AddInfoEntry($"Searching conversations for: {args}");
        return true;
    }

    private bool HandleTheme(string? args)
    {
        if (string.IsNullOrEmpty(args))
        {
            _callbacks.AddInfoEntry("[error] usage: /theme dark|light");
            return true;
        }

        var theme = args.ToLowerInvariant();
        if (theme != "dark" && theme != "light")
        {
            _callbacks.AddInfoEntry($"[error] invalid theme '{args}' — use dark or light");
            return true;
        }

        _callbacks.SetTheme(theme);
        _callbacks.AddInfoEntry($"Theme set to: {theme}");
        return true;
    }

    private bool HandlePoll(string? args)
    {
        if (string.IsNullOrEmpty(args) || !int.TryParse(args, out int value))
        {
            _callbacks.AddInfoEntry("[error] usage: /poll <n> (0-60)");
            return true;
        }

        var clamped = Math.Clamp(value, 0, 60);
        _callbacks.SetPollThrottle(clamped);
        if (clamped != value)
        {
            _callbacks.AddInfoEntry($"Poll throttle clamped to {clamped} (applies on reconnect).");
        }
        else
        {
            _callbacks.AddInfoEntry($"Poll throttle set to {clamped} (applies on reconnect).");
        }
        return true;
    }

    private bool HandleProfile(string? args)
    {
        if (string.IsNullOrEmpty(args))
        {
            _callbacks.AddInfoEntry("[error] usage: /profile auto|full|restricted");
            return true;
        }

        var profile = args.ToLowerInvariant();
        if (profile != "auto" && profile != "full" && profile != "restricted")
        {
            _callbacks.AddInfoEntry($"[error] invalid profile '{args}' — use auto, full, or restricted");
            return true;
        }

        _callbacks.SetToolProfile(profile);
        _callbacks.AddInfoEntry($"Tool profile set to: {profile} (applies on reconnect).");
        return true;
    }

    private bool HandleUnknown(string command)
    {
        _callbacks.AddInfoEntry($"[error] unknown command {command} — try /help");
        return true;
    }
}
