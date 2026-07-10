using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class ViewerViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ViewerTabViewModel> tabs;

    [ObservableProperty]
    private ViewerTabViewModel? selectedTab;

    public ViewerViewModel()
    {
        Tabs = new ObservableCollection<ViewerTabViewModel>(new[]
        {
            new ViewerTabViewModel("README.md", "# DevMX\n\nA developer assistant application.\n\n## Getting Started\n\nRun `dotnet run` to start the chat CLI."),
            new ViewerTabViewModel("diff: Program.cs", "--- a/Program.cs\n+++ b/Program.cs\n@@ -1,3 +1,5 @@\n+using DevMX.Core;\n \n var loop = new AgenticLoop();\n+loop.Start();\n"),
        });
        SelectedTab = Tabs[0];
    }

    [RelayCommand]
    private void CloseTab(ViewerTabViewModel tab)
    {
        if (Tabs.Remove(tab) && tab == SelectedTab)
        {
            SelectedTab = Tabs.FirstOrDefault();
        }
    }
}
