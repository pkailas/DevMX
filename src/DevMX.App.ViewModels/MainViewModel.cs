using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevMX.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public SidebarViewModel Sidebar { get; private set; }
    public ChatViewModel Chat { get; private set; }
    public ViewerViewModel Viewer { get; }
    public TaskMonitorViewModel TaskMonitor { get; }
    public SettingsViewModel Settings { get; }

    private readonly DevMxSettings _settings;
    private AppSession _session;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _onThemeChanged;

    [ObservableProperty]
    private bool isSidebarExpanded = true;

    [ObservableProperty]
    private string statusText = "Initializing...";

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

        await _session.DisposeAsync();

        // Create a new session with updated settings
        var newSession = new AppSession(_settings);

        // We need to swap the session references. Since AppSession is sealed and MainViewModel
        // holds a reference, we'll reinitialize the existing session by creating a new one
        // and replacing internal references. Since we can't swap _session directly,
        // we'll use a different approach: dispose and re-create via the composition root.
        // Actually, the simplest approach: dispose old, create new, update all references.
        // But MainViewModel holds _session as readonly... Let me fix that.
        
        // For now, just dispose and recreate. We'll update the field.
        await DisposeSessionAsync();
        
        // Create fresh session - we need to update the session reference
        // Since _session is readonly, we need to change it. Let me restructure.
        // Actually, the cleanest approach is to make _session non-readonly and reassign.
        // Let me just update the implementation below.
        
        // For the current structure, let's just reinitialize by creating a new AppSession
        // and passing it through. We'll need to update the MainWindow to support this.
        // The simplest fix: make the session replaceable.
        
        // Let me just do a simple re-init pattern: dispose, create new, notify UI.
        // We'll handle this properly by making _session non-readonly.
        
        // Re-create session
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

        // Replace ViewModel references
        Chat = newChat;
        Sidebar = newSidebar;

        // Reinitialize
        await _session.InitializeAsync();

        _dispatch(() =>
        {
            StatusText = $"Connected: {_session.ToolCount} tools | {_session.Model} | tools: {_session.EffectiveToolProfile}";
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
}
