using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class SidebarViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly Action<Action> _dispatch;
    private readonly Action _clearChatEntries;
    private bool _isTitled;

    [ObservableProperty]
    private ObservableCollection<ConversationItemViewModel> conversations;

    [ObservableProperty]
    private ConversationItemViewModel? selectedConversation;

    public SidebarViewModel(AppSession session, Action<Action> dispatch, Action clearChatEntries)
    {
        _session = session;
        _dispatch = dispatch;
        _clearChatEntries = clearChatEntries;
        Conversations = new ObservableCollection<ConversationItemViewModel>();
        _isTitled = false;
    }

    internal void AddConversation(long id, string title, DateTime updatedAt)
    {
        var item = new ConversationItemViewModel(id, title, updatedAt);
        Conversations.Add(item);
        SelectedConversation = item;
    }

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        try
        {
            long newId = await _session.CreateNewConversationAsync();
            _dispatch(() =>
            {
                var item = new ConversationItemViewModel(newId, "(untitled)", DateTime.UtcNow);
                Conversations.Add(item);
                SelectedConversation = item;
                _clearChatEntries();
                _isTitled = false;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarViewModel] NewConversation failed: {ex.Message}");
        }
    }

    /// <summary>Called after the first turn to auto-title the conversation.</summary>
    internal async Task AutoTitleAsync(string firstUserMessage)
    {
        if (_isTitled)
            return;

        string autoTitle = firstUserMessage.Length > 48 ? firstUserMessage[..48].Trim() + "\u2026" : firstUserMessage.Trim();
        if (string.IsNullOrEmpty(autoTitle))
            return;

        try
        {
            await _session.UpdateTitleAsync(autoTitle);
            _dispatch(() =>
            {
                if (SelectedConversation != null)
                {
                    SelectedConversation.Title = autoTitle;
                }
                _isTitled = true;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarViewModel] AutoTitle failed: {ex.Message}");
        }
    }
}
