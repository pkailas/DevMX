using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class SidebarViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ConversationItemViewModel> conversations;

    [ObservableProperty]
    private ConversationItemViewModel? selectedConversation;

    public SidebarViewModel()
    {
        var now = DateTime.UtcNow;
        Conversations = new ObservableCollection<ConversationItemViewModel>(new[]
        {
            new ConversationItemViewModel("conv-1", "sample: refactor Parsely", now.AddMinutes(-10)),
            new ConversationItemViewModel("conv-2", "sample: fix tests", now.AddMinutes(-5)),
            new ConversationItemViewModel("conv-3", "(untitled)", now),
        });
        SelectedConversation = Conversations[0];
    }

    [RelayCommand]
    private void NewConversation()
    {
        var id = $"conv-{Guid.NewGuid():N[..8]}";
        var item = new ConversationItemViewModel(id, "(untitled)", DateTime.UtcNow);
        Conversations.Add(item);
        SelectedConversation = item;
    }
}
