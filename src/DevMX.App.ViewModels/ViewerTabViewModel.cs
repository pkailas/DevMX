using CommunityToolkit.Mvvm.ComponentModel;

namespace DevMX.App.ViewModels;

public partial class ViewerTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string content = string.Empty;

    [ObservableProperty]
    private ViewerTabKind kind = ViewerTabKind.File;

    [ObservableProperty]
    private string fileExtension = string.Empty;

    public ViewerTabViewModel(string title, string content, ViewerTabKind kind = ViewerTabKind.File, string fileExtension = "")
    {
        Title = title;
        Content = content;
        Kind = kind;
        FileExtension = fileExtension;
    }
}
