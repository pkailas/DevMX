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
        Tabs = new ObservableCollection<ViewerTabViewModel>();
    }

    /// <summary>Opens a file tab, deduplicating by title (re-selects and refreshes content if already open).</summary>
    public void OpenFileTab(string title, string content, string extension = "")
    {
        var existing = Tabs.FirstOrDefault(t => t.Title == title);
        if (existing != null)
        {
            existing.Content = content;
            existing.FileExtension = extension;
            existing.Kind = ViewerTabKind.File;
            SelectedTab = existing;
            return;
        }

        var tab = new ViewerTabViewModel(title, content, ViewerTabKind.File, extension);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    /// <summary>Opens a diff tab with unified diff text, deduplicating by title.</summary>
    public void OpenDiffTab(string title, string unifiedDiffText)
    {
        var existing = Tabs.FirstOrDefault(t => t.Title == title);
        if (existing != null)
        {
            existing.Content = unifiedDiffText;
            existing.Kind = ViewerTabKind.Diff;
            SelectedTab = existing;
            return;
        }

        var tab = new ViewerTabViewModel(title, unifiedDiffText, ViewerTabKind.Diff);
        Tabs.Add(tab);
        SelectedTab = tab;
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
