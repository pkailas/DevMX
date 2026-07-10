using CommunityToolkit.Mvvm.ComponentModel;

namespace DevMX.App.ViewModels;

public partial class ConversationItemViewModel : ObservableObject
{
    [ObservableProperty]
    private long id;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private DateTime updatedAt;

    public ConversationItemViewModel(long id, string title, DateTime updatedAt)
    {
        Id = id;
        Title = title;
        UpdatedAt = updatedAt;
    }
}
