using CommunityToolkit.Mvvm.ComponentModel;

namespace DevMX.App.ViewModels;

public partial class ConversationItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string id = string.Empty;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private DateTime updatedAt;

    public ConversationItemViewModel(string id, string title, DateTime updatedAt)
    {
        Id = id;
        Title = title;
        UpdatedAt = updatedAt;
    }
}
