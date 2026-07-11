using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevMX.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private SidebarViewModel sidebar = null!;

    [ObservableProperty]
    private ChatViewModel chat = null!;
    public ViewerViewModel Viewer { get; }
    public TaskMonitorViewModel TaskMonitor { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Fired when the menu/shortcut requests focus on the sidebar search box.</summary>
    public event Action? OnRequestFocusSearch;

    private readonly DevMxSettings _settings;
    private AppSession _session;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _onThemeChanged;

    [ObservableProperty]
    private bool isSidebarExpanded = true;

    [ObservableProperty]
    private string statusText = "Initializing...";

    [ObservableProperty]
    private string windowTitle = "DevMX";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    private bool isBusy;

    public MainViewModel(DevMxSettings settings, AppSession session, Action<Action> dispatch, Action<string> onThemeChanged = null!)
    {
        _settings = settings;
        _session = session;
        _dispatch = dispatch;
        _onThemeChanged = onThemeChanged;

        var chatVm = new ChatViewModel(session, dispatch);
        Sidebar = new SidebarViewModel(session, dispatch, chatVm.ClearEntries);
        // Now wire the auto-title callback (Sidebar is assigned)
        chatVm.SetAutoTitleCallback(async (firstUserMessage) =>
        {
            await Sidebar.AutoTitleAsync(firstUserMessage);
        });
        Chat = chatVm;
        Viewer = new ViewerViewModel();
        TaskMonitor = new TaskMonitorViewModel(dispatch);

        // Wire TaskMonitor callbacks to AppSession
        TaskMonitor.SetPollTaskCallback(async (jobId) => await session.PollTaskAsync(jobId));
        TaskMonitor.SetFetchResultCallback(async (jobId) => await session.FetchTaskResultAsync(jobId));
        TaskMonitor.SetFetchDiffCallback(async (filePath) => await session.FetchDiffAsync(filePath));
        TaskMonitor.SetOpenDiffTabCallback((title, diffText) =>
        {
            _dispatch(() => Viewer.OpenDiffTab(title, diffText));
        });
        TaskMonitor.SetCancelTaskCallback(async (jobId) => await session.CancelTaskAsync(jobId));

        // Wire viewer callbacks into ChatViewModel
        chatVm.SetOpenDiffTabCallback((title, diffText) =>
        {
            _dispatch(() => Viewer.OpenDiffTab(title, diffText));
        });

        // Wire ChatViewModel onToolResult to TaskMonitor for task event tracking
        chatVm.SetTaskToolResultCallback((toolName, argJson, resultText) =>
        {
            HandleTaskToolResult(toolName, argJson, resultText);
        });
        chatVm.SetOpenFileCallback(async (filePath) =>
        {
            try
            {
                var content = await _session.FetchFileAsync(filePath);
                var fileName = Path.GetFileName(filePath) ?? filePath;
                var extension = Path.GetExtension(filePath);
                _dispatch(() => Viewer.OpenFileTab(fileName, content, extension));
            }
            catch (Exception ex)
            {
                _dispatch(() =>
                {
                    Chat.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info,
                        $"[error] Could not open file '{filePath}': {ex.Message}"));
                });
            }
        });

        // Wire slash command handler
        var slashCallbacks = new SlashCommandCallbacks
        {
            GetWorkDir = () => _settings.WorkDir,
            SetWorkDir = (path) => { _settings.WorkDir = path; _settings.Save(); },
            PickFolder = (initialDir) => null, // Will be overridden by MainWindow
            RequestReconnect = async () => await ApplyAndReconnectAsync(),
            CreateNewConversation = async () =>
            {
                long newId = await _session.CreateNewConversationAsync();
                _dispatch(() =>
                {
                    var item = new ConversationItemViewModel(newId, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}", DateTime.UtcNow);
                    Sidebar.Conversations.Insert(0, item);
                    var fullList = typeof(SidebarViewModel).GetField("_fullList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Sidebar) as System.Collections.ObjectModel.ObservableCollection<ConversationItemViewModel>;
                    fullList?.Insert(0, item);
                    Sidebar.SelectedConversation = item;
                    Chat.ClearEntries();
                    var isTitledField = typeof(SidebarViewModel).GetField("_isTitled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    isTitledField.SetValue(Sidebar, false);
                    var clearEvent = typeof(SidebarViewModel).GetEvent("OnClearProviderMismatch")!;
                    var raiseMethod = clearEvent.GetRaiseMethod()!;
                    raiseMethod.Invoke(Sidebar, null!);
                });
                return newId;
            },
            UpdateTitle = async (title) =>
            {
                await _session.UpdateTitleAsync(title);
                _dispatch(() =>
                {
                    if (Sidebar.SelectedConversation != null)
                        Sidebar.SelectedConversation.Title = title;
                    var isTitledField = typeof(SidebarViewModel).GetField("_isTitled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        ;
                    isTitledField.SetValue(Sidebar, true);
                });
            },
            OpenConversation = async (id) =>
            {
                // Find the conversation item and trigger the sidebar switch flow
                var item = Sidebar.Conversations.FirstOrDefault(c => c.Id == id);
                if (item == null)
                {
                    throw new InvalidOperationException($"Conversation #{id} not found");
                }
                await Sidebar.SelectConversationAsync(item);
            },
            SetSearchText = (term) => { Sidebar.SearchText = term; },
            ExpandSidebar = () => { IsSidebarExpanded = true; },
            SetTheme = (theme) => { Settings?.SetThemeCommand?.Execute(theme); },
            SetToolProfile = (profile) => { Settings?.SetToolProfileCommand?.Execute(profile); },
            SetPollThrottle = (value) => { _settings.PollThrottleSeconds = value; _settings.Save(); },
            AddInfoEntry = (text) => AddInfoEntry(text),
            ClearInputText = () =>
            {
                _dispatch(() => Chat.InputText = string.Empty);
            }
        };
        chatVm.SetSlashCommandHandler(new SlashCommandHandler(slashCallbacks));

        // Wire sidebar events
        Sidebar.OnProviderMismatch += (convId, providerName) =>
        {
            _dispatch(() =>
            {
                Chat.ShowProviderMismatchInfo(convId, providerName);
            });
        };
        Sidebar.OnAddEntry += (entry) =>
        {
            _dispatch(() =>
            {
                Chat.Entries.Add(entry);
            });
        };
        Sidebar.OnClearProviderMismatch += () =>
        {
            _dispatch(() =>
            {
                Chat.ClearProviderMismatch();
            });
        };
        Sidebar.OnDelegationsLoaded += (delegations) =>
        {
            TaskMonitor.PopulateFromDelegations(delegations);
        };

        // Settings pane
        Settings = new SettingsViewModel(settings, () =>
        {
            _ = ApplyAndReconnectAsync();
        }, _onThemeChanged);

        // Wire Settings property changes to update menu checkmarks
        ((System.ComponentModel.INotifyPropertyChanged)Settings).PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Settings.Theme) || e.PropertyName == nameof(Settings.ToolProfile))
            {
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(CurrentToolProfile));
            }
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            _dispatch(() =>
            {
                StatusText = "Connecting to DM...";
                IsBusy = true;
            });

            await _session.InitializeAsync();

            _dispatch(() =>
            {
                StatusText = $"Connected: {_session.ToolCount} tools | {_session.Model} | tools: {_session.EffectiveToolProfile}";
                WindowTitle = $"DevMX - {_settings.WorkDir}";
                Chat.SetInitialized(true);
                IsBusy = false;
            });

            // Populate sidebar from store
            await Sidebar.PopulateConversationsAsync(_session.ConversationId);

            // Start task monitor polling loop
            TaskMonitor.StartPolling();
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                StatusText = $"DM connection failed: {ex.Message}";
                IsBusy = false;
                Console.WriteLine($"[MainViewModel] Init failed: {ex.Message}");
            });
        }
    }

    /// <summary>Apply settings changes and reconnect (teardown + reinit).</summary>
    private async Task ApplyAndReconnectAsync()
    {
        if (IsBusy)
            return;

        try
        {
            _dispatch(() => StatusText = "Reconnecting...");
            await ReinitializeAsync();
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                StatusText = $"Reconnect failed: {ex.Message}";
                Console.WriteLine($"[MainViewModel] Reconnect failed: {ex.Message}");
            });
        }
    }

    /// <summary>Reconnect with current settings (for when the server was down at launch).</summary>
    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private async Task ReconnectAsync()
    {
        if (IsBusy)
            return;

        try
        {
            _dispatch(() => StatusText = "Reconnecting...");
            await ReinitializeAsync();
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                StatusText = $"Reconnect failed: {ex.Message}";
                Console.WriteLine($"[MainViewModel] Reconnect failed: {ex.Message}");
            });
        }
    }

    private async Task ReinitializeAsync()
    {
        // Reload settings from disk (they may have been saved by SettingsViewModel)
        var freshSettings = DevMxSettings.Load();
        // Update our settings instance
        _settings.Endpoint = freshSettings.Endpoint;
        _settings.Model = freshSettings.Model;
        _settings.Provider = freshSettings.Provider;
        _settings.WorkDir = freshSettings.WorkDir;
        _settings.ServerExe = freshSettings.ServerExe;
        _settings.ToolProfile = freshSettings.ToolProfile;

        // Teardown
        _dispatch(() =>
        {
            IsBusy = true;
            StatusText = "Tearing down...";
        });

        await DisposeSessionAsync();
                _session = new AppSession(_settings);
        // Re-create ViewModels with the new session
        var newChat = new ChatViewModel(_session, _dispatch);
        newChat.SetAutoTitleCallback(async (msg) => await Sidebar.AutoTitleAsync(msg));

        // Re-wire TaskMonitor callbacks to new session
        TaskMonitor.StopPolling();
        TaskMonitor.SetPollTaskCallback(async (jobId) => await _session.PollTaskAsync(jobId));
        TaskMonitor.SetFetchResultCallback(async (jobId) => await _session.FetchTaskResultAsync(jobId));
        TaskMonitor.SetFetchDiffCallback(async (filePath) => await _session.FetchDiffAsync(filePath));

        // Re-wire viewer callbacks
        newChat.SetOpenDiffTabCallback((title, diffText) =>
        {
            _dispatch(() => Viewer.OpenDiffTab(title, diffText));
        });
        newChat.SetOpenFileCallback(async (filePath) =>
        {
            try
            {
                var content = await _session.FetchFileAsync(filePath);
                var fileName = Path.GetFileName(filePath) ?? filePath;
                var extension = Path.GetExtension(filePath);
                _dispatch(() => Viewer.OpenFileTab(fileName, content, extension));
            }
            catch (Exception ex)
            {
                _dispatch(() =>
                {
                    Chat.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info,
                        $"[error] Could not open file '{filePath}': {ex.Message}"));
                });
            }
        });

        // Re-wire sidebar events
        var newSidebar = new SidebarViewModel(_session, _dispatch, newChat.ClearEntries);
        newSidebar.OnProviderMismatch += (convId, providerName) =>
        {
            _dispatch(() => newChat.ShowProviderMismatchInfo(convId, providerName));
        };
        newSidebar.OnAddEntry += (entry) =>
        {
            _dispatch(() => newChat.Entries.Add(entry));
        };
        newSidebar.OnClearProviderMismatch += () =>
        {
            _dispatch(() => newChat.ClearProviderMismatch());
        };
        newSidebar.OnDelegationsLoaded += (delegations) =>
        {
            TaskMonitor.PopulateFromDelegations(delegations);
        };

        // Wire ChatViewModel onToolResult to TaskMonitor for task event tracking
        newChat.SetTaskToolResultCallback((toolName, argJson, resultText) =>
        {
            HandleTaskToolResult(toolName, argJson, resultText);
        });

        // Re-wire slash command handler with updated Sidebar reference
        var slashCallbacks2 = new SlashCommandCallbacks
        {
            GetWorkDir = () => _settings.WorkDir,
            SetWorkDir = (path) => { _settings.WorkDir = path; _settings.Save(); },
            PickFolder = (initialDir) => null,
            RequestReconnect = async () => await ApplyAndReconnectAsync(),
            CreateNewConversation = async () =>
            {
                long newId = await _session.CreateNewConversationAsync();
                _dispatch(() =>
                {
                    var item = new ConversationItemViewModel(newId, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}", DateTime.UtcNow);
                    newSidebar.Conversations.Insert(0, item);
                    var fullList = typeof(SidebarViewModel).GetField("_fullList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(newSidebar) as System.Collections.ObjectModel.ObservableCollection<ConversationItemViewModel>;
                    fullList?.Insert(0, item);
                    newSidebar.SelectedConversation = item;
                    newChat.ClearEntries();
                    var isTitledField = typeof(SidebarViewModel).GetField("_isTitled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    isTitledField.SetValue(newSidebar, false);
                    var clearEvent = typeof(SidebarViewModel).GetEvent("OnClearProviderMismatch")!;
                    var raiseMethod = clearEvent.GetRaiseMethod()!;
                    raiseMethod.Invoke(newSidebar, null!);
                });
                return newId;
            },
            UpdateTitle = async (title) =>
            {
                await _session.UpdateTitleAsync(title);
                _dispatch(() =>
                {
                    if (newSidebar.SelectedConversation != null)
                        newSidebar.SelectedConversation.Title = title;
                    var isTitledField = typeof(SidebarViewModel).GetField("_isTitled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    isTitledField.SetValue(newSidebar, true);
                });
            },
            OpenConversation = async (id) =>
            {
                var item = newSidebar.Conversations.FirstOrDefault(c => c.Id == id);
                if (item == null)
                    throw new InvalidOperationException($"Conversation #{id} not found");
                await newSidebar.SelectConversationAsync(item);
            },
            SetSearchText = (term) => { newSidebar.SearchText = term; },
            ExpandSidebar = () => { IsSidebarExpanded = true; },
            SetTheme = (theme) => { Settings?.SetThemeCommand?.Execute(theme); },
            SetToolProfile = (profile) => { Settings?.SetToolProfileCommand?.Execute(profile); },
            SetPollThrottle = (value) => { _settings.PollThrottleSeconds = value; _settings.Save(); },
            AddInfoEntry = (text) =>
            {
                _dispatch(() => newChat.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info, text)));
            },
            ClearInputText = () =>
            {
                _dispatch(() => newChat.InputText = string.Empty);
            }
        };
        newChat.SetSlashCommandHandler(new SlashCommandHandler(slashCallbacks2));

        // Replace ViewModel references
        Chat = newChat;
        Sidebar = newSidebar;

        // Reinitialize
        await _session.InitializeAsync();

        _dispatch(() =>
        {
            StatusText = $"Connected: {_session.ToolCount} tools | {_session.Model} | tools: {_session.EffectiveToolProfile}";
                WindowTitle = $"DevMX - {_settings.WorkDir}";
            Chat.SetInitialized(true);
            IsBusy = false;
        });

        await newSidebar.PopulateConversationsAsync(_session.ConversationId);

        // Restart task monitor polling loop
        TaskMonitor.StartPolling();
    }

    public async ValueTask DisposeSessionAsync()
    {
        try
        {
            await _session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel] Dispose error: {ex.Message}");
        }
    }

    private bool CanReconnect() => !IsBusy;

    /// <summary>
    /// Handles task-related tool results from the agentic loop and updates the TaskMonitor.
    /// </summary>
    private void HandleTaskToolResult(string toolName, string argJson, string resultText)
    {
        try
        {
            if (toolName == "devmind_task_start")
            {
                // Parse job_id and prompt from result/args
                var resultObj = System.Text.Json.Nodes.JsonNode.Parse(resultText)?.AsObject();
                string? jobId = resultObj?["job_id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(jobId)) return;

                // Extract prompt from args for detail text
                string detail = "";
                var argObj = System.Text.Json.Nodes.JsonNode.Parse(argJson)?.AsObject();
                if (argObj != null)
                {
                    foreach (var key in new[] { "prompt", "brief", "task" })
                    {
                        if (argObj.ContainsKey(key))
                        {
                            detail = argObj[key]?.GetValue<string>() ?? "";
                            break;
                        }
                    }
                }
                // Truncate detail to ~80 chars
                if (detail.Length > 80)
                    detail = detail[..80] + "\u2026";

                TaskMonitor.AddOrUpdateTask(jobId, "queued", detail, isLive: true);
            }
            else if (toolName == "devmind_task_status")
            {
                var resultObj = System.Text.Json.Nodes.JsonNode.Parse(resultText)?.AsObject();
                if (resultObj == null) return;

                string? jobId = resultObj["job_id"]?.GetValue<string>();
                string? state = resultObj["state"]?.GetValue<string>();
                if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(state)) return;

                // Update existing task state
                TaskMonitor.UpdateTaskResult(jobId, state);
            }
            else if (toolName == "devmind_task_result")
            {
                var resultObj = System.Text.Json.Nodes.JsonNode.Parse(resultText)?.AsObject();
                if (resultObj == null) return;

                string? jobId = resultObj["job_id"]?.GetValue<string>();
                string? state = resultObj["state"]?.GetValue<string>();
                if (string.IsNullOrEmpty(jobId)) return;

                // Extract journal and answer text
                string? journalText = null;
                string? answerText = null;

                if (resultObj.ContainsKey("journal"))
                    journalText = resultObj["journal"]?.GetValue<string>();
                if (resultObj.ContainsKey("answer"))
                    answerText = resultObj["answer"]?.GetValue<string>();

                // If no structured fields, use the whole result as journal
                if (string.IsNullOrEmpty(journalText) && string.IsNullOrEmpty(answerText))
                    journalText = resultText;

                TaskMonitor.UpdateTaskResult(jobId, state ?? "done", journalText, answerText);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel] HandleTaskToolResult error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
    }

    // ===== Menu bar commands (thin wrappers sharing slash-command logic) =====

    /// <summary>Change working directory — same flow as /dir -b (folder picker wired by MainWindow).</summary>
    [RelayCommand]
    private async Task ChangeWorkingDirectoryAsync()
    {
        // This triggers the /dir -b flow via the slash command handler's PickFolder callback.
        // The actual folder picker is wired in MainWindow.xaml.cs.
        // We invoke the shared dir-change logic:
        string? currentDir = _settings.WorkDir;
        // PickFolder callback is set by MainWindow; if null, show info
        // We call the slash handler directly to reuse the /dir -b flow
        var slashHandlerField = typeof(ChatViewModel).GetField("_slashCommandHandler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var handler = slashHandlerField.GetValue(Chat) as SlashCommandHandler;
        if (handler != null)
        {
            handler.ExecuteCommand("/dir -b");
        }
    }

    /// <summary>Create a new conversation — same flow as /new and Sidebar.NewConversationCommand.</summary>
    [RelayCommand]
    private async Task NewConversationAsync()
    {
        await ExecuteNewConversationAsync();
    }

    /// <summary>Begin rename on the currently selected sidebar item.</summary>
    [RelayCommand]
    private void RenameCurrentConversation()
    {
        if (Sidebar.SelectedConversation != null)
        {
            Sidebar.BeginRenameCommand.Execute(Sidebar.SelectedConversation);
        }
    }

    /// <summary>Expand sidebar and focus search — same flow as /search.</summary>
    [RelayCommand]
    private void FocusSearch()
    {
        IsSidebarExpanded = true;
        OnRequestFocusSearch?.Invoke();
    }

    /// <summary>Show the /help output as an Info entry in chat.</summary>
    [RelayCommand]
    private void ShowHelp()
    {
        AddInfoEntry(GenerateHelpText());
    }

    /// <summary>Show about info as an Info entry in chat.</summary>
    [RelayCommand]
    private void ShowAbout()
    {
        AddInfoEntry("DevMX — AI-powered developer assistant\n\n" +
            "A local-first development tool integrating with your MCP server.\n\n" +
            "Repository: https://github.com/DevMX/DevMX");
    }

    /// <summary>Set the theme (dark/light) — same setter as settings segmented buttons.</summary>
    [RelayCommand]
    private void SetTheme(string themeName)
    {
        Settings.SetThemeCommand.Execute(themeName);
    }

    /// <summary>Set the tool profile — same setter as settings segmented buttons.</summary>
    [RelayCommand]
    private void SetToolProfileMenu(string profileName)
    {
        Settings.SetToolProfileCommand.Execute(profileName);
    }

    /// <summary>Check if the current theme is dark.</summary>
    public bool IsDarkTheme => Settings.Theme == "dark";

    /// <summary>Check if the current theme is light.</summary>
    public bool IsLightTheme => Settings.Theme == "light";

    /// <summary>Current tool profile for menu checkmarks.</summary>
    public string CurrentToolProfile => Settings.ToolProfile;

    // ===== Shared private methods (used by both slash commands and menu commands) =====

    private async Task ExecuteNewConversationAsync()
    {
        try
        {
            long newId = await _session.CreateNewConversationAsync();
            _dispatch(() =>
            {
                var item = new ConversationItemViewModel(newId, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}", DateTime.UtcNow);
                Sidebar.Conversations.Insert(0, item);
                var fullList = typeof(SidebarViewModel).GetField("_fullList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Sidebar) as System.Collections.ObjectModel.ObservableCollection<ConversationItemViewModel>;
                fullList?.Insert(0, item);
                Sidebar.SelectedConversation = item;
                Chat.ClearEntries();
                var isTitledField = typeof(SidebarViewModel).GetField("_isTitled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                isTitledField.SetValue(Sidebar, false);
                var clearEvent = typeof(SidebarViewModel).GetEvent("OnClearProviderMismatch")!;
                var raiseMethod = clearEvent.GetRaiseMethod()!;
                raiseMethod.Invoke(Sidebar, null!);
            });
        }
        catch (Exception ex)
        {
            AddInfoEntry($"[error] Could not create conversation: {ex.Message}");
        }
    }

    private void AddInfoEntry(string text)
    {
        _dispatch(() => Chat.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info, text)));
    }

    private string GenerateHelpText()
    {
        return @"Available commands:
  /help                Show this help
  /dir [path] [-b]     Show or change working directory (-b opens folder picker)
  /new                 Start a new conversation
  /open <id>           Open conversation by ID
  /search <term>       Search conversations
  /theme dark|light    Switch theme
  /poll <n>            Set poll throttle (0-60 seconds)
  /profile auto|full|restricted  Set tool access profile";
    }
}
