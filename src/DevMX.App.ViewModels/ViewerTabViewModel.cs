using CommunityToolkit.Mvvm.ComponentModel;

namespace DevMX.App.ViewModels;

public partial class ViewerTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string content = string.Empty;

    public ViewerTabViewModel(string title, string content)
    {
        Title = title;
        Content = content;
    }
}
