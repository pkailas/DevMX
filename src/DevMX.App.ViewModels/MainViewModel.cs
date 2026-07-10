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

    [ObservableProperty]
    private bool isSidebarExpanded = true;

    [ObservableProperty]
    private string statusText = "Initializing...";

    [ObservableProperty]
    private bool isBusy;

    public MainViewModel(DevMxSettings settings, AppSession session, Action<Action> dispatch)
    {
        _settings = settings;
        _session = session;
        _dispatch = dispatch;

        var chatVm = new ChatViewModel(session, dispatch);
        Sidebar = new SidebarViewModel(session, dispatch, chatVm.ClearEntries);
        // Now wire the auto-title callback (Sidebar is assigned)
        chatVm.SetAutoTitleCallback(async (firstUserMessage) =>
        {
            await Sidebar.AutoTitleAsync(firstUserMessage);
        });
        Chat = chatVm;
        Viewer = new ViewerViewModel();
        TaskMonitor = new TaskMonitorViewModel();

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

        // Settings pane
        Settings = new SettingsViewModel(settings, () =>
        {
            _ = ApplyAndReconnectAsync();
        });
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
                StatusText = $"Connected: {_session.ToolCount} tools | {_session.Model}";
                Chat.SetInitialized(true);
                IsBusy = false;
            });

            // Populate sidebar from store
            await Sidebar.PopulateConversationsAsync(_session.ConversationId);
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

        // Replace ViewModel references
        Chat = newChat;
        Sidebar = newSidebar;

        // Reinitialize
        await _session.InitializeAsync();

        _dispatch(() =>
        {
            StatusText = $"Connected: {_session.ToolCount} tools | {_session.Model}";
            Chat.SetInitialized(true);
            IsBusy = false;
        });

        await newSidebar.PopulateConversationsAsync(_session.ConversationId);
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

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
    }
}
