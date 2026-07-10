using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevMX.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public SidebarViewModel Sidebar { get; }
    public ChatViewModel Chat { get; }
    public ViewerViewModel Viewer { get; }
    public TaskMonitorViewModel TaskMonitor { get; }

    private readonly AppSession _session;
    private readonly Action<Action> _dispatch;

    [ObservableProperty]
    private bool isSidebarExpanded = true;

    [ObservableProperty]
    private string statusText = "Initializing...";

    public MainViewModel(AppSession session, Action<Action> dispatch)
    {
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
    }

    public async Task InitializeAsync()
    {
        try
        {
            _dispatch(() => StatusText = "Connecting to DM...");
            await _session.InitializeAsync();

            _dispatch(() =>
            {
                StatusText = $"Connected: {_session.ToolCount} tools | {_session.Model}";
                Chat.SetInitialized(true);
                // Add the initial conversation to the sidebar
                Sidebar.AddConversation(_session.ConversationId, "(untitled)", DateTime.UtcNow);
            });
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                StatusText = $"DM connection failed: {ex.Message}";
                Console.WriteLine($"[MainViewModel] Init failed: {ex.Message}");
            });
        }
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

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
    }
}
