using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevMX.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public SidebarViewModel Sidebar { get; }
    public ChatViewModel Chat { get; }
    public ViewerViewModel Viewer { get; }
    public TaskMonitorViewModel TaskMonitor { get; }

    [ObservableProperty]
    private bool isSidebarExpanded = true;

    [ObservableProperty]
    private string statusText = "Not connected";

    public MainViewModel()
    {
        Sidebar = new SidebarViewModel();
        Chat = new ChatViewModel();
        Viewer = new ViewerViewModel();
        TaskMonitor = new TaskMonitorViewModel();
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
    }
}
