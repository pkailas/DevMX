using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

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

    /// <summary>Populate the conversation list from the store (called after init).</summary>
    internal async Task PopulateConversationsAsync(long currentConversationId)
    {
        var list = await _session.Store!.ListConversationsAsync();
        var items = new ObservableCollection<ConversationItemViewModel>();

        foreach (var summary in list)
        {
            var updatedAt = DateTime.TryParse(summary.UpdatedAt, out var dt) ? dt : DateTime.UtcNow;
            items.Add(new ConversationItemViewModel(summary.Id, summary.Title, updatedAt));
        }

        _dispatch(() =>
        {
            Conversations = items;
            var current = Conversations.FirstOrDefault(c => c.Id == currentConversationId);
            SelectedConversation = current;
        });
    }

    /// <summary>Called when a conversation is selected in the ListBox — switch to it.</summary>
    public async Task SelectConversationAsync(ConversationItemViewModel? item)
    {
        if (item == null)
            return;

        if (!_session.IsInitialized)
            return;

        long id = item.Id;

        // Check provider match via store
        var summaries = await _session.Store!.ListConversationsAsync();
        var summary = summaries.FirstOrDefault(s => s.Id == id);
        if (summary == null)
            return;

        string activeProvider = _session.Provider?.ProviderName ?? "openai";

        if (summary.Provider != activeProvider)
        {
            _dispatch(() => _clearChatEntries());
            OnProviderMismatch?.Invoke(id, summary.Provider);
            return;
        }

        // Provider matches — open the conversation
        try
        {
            await _session.OpenConversationAsync(id);
            _dispatch(() =>
            {
                _clearChatEntries();
                _isTitled = true;
            });

            // Load history entries into the chat
            var messages = await _session.Store!.GetMessagesAsync(id);
            var entries = new List<ChatEntryViewModel>();
            foreach (var msg in messages)
            {
                try
                {
                    var content = JsonNode.Parse(msg.ContentJson);
                    if (content == null) continue;

                    if (msg.Role == "user")
                    {
                        string? text = ExtractTextFromContent(content);
                        if (!string.IsNullOrEmpty(text))
                            entries.Add(new ChatEntryViewModel(ChatEntryKind.User, text));
                    }
                    else if (msg.Role == "assistant")
                    {
                        if (content is JsonObject obj && obj["content"] is JsonArray contentArr)
                        {
                            bool hasToolCalls = false;
                            bool hasText = false;
                            foreach (var block in contentArr)
                            {
                                if (block is JsonObject blockObj)
                                {
                                    string? type = blockObj["type"]?.GetValue<string>();
                                    if (type == "tool_use")
                                    {
                                        hasToolCalls = true;
                                        string name = blockObj["name"]?.GetValue<string>() ?? "unknown";
                                        string argsJson = blockObj["input"]?.ToJsonString() ?? "{}";
                                        string argTrunc = argsJson.Length > 120 ? argsJson[..120] + "\u2026" : argsJson;
                                        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, $"[tool] {name}({argTrunc})"));
                                    }
                                    else if (type == "text")
                                    {
                                        hasText = true;
                                        string? text = blockObj["text"]?.GetValue<string>();
                                        if (!string.IsNullOrEmpty(text))
                                            entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, text));
                                    }
                                }
                            }
                            if (!hasToolCalls && !hasText)
                            {
                                string? text = ExtractTextFromContent(content);
                                if (!string.IsNullOrEmpty(text))
                                    entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, text));
                            }
                        }
                        else
                        {
                            string? text = ExtractTextFromContent(content);
                            if (!string.IsNullOrEmpty(text))
                                entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, text));
                        }
                    }
                    // Skip "tool" role messages (plumbing)
                }
                catch
                {
                    // Malformed/unexpected shapes: skip silently
                }
            }

            _dispatch(() =>
            {
                foreach (var entry in entries)
                    OnAddEntry?.Invoke(entry);
            });

            // Load delegations for the task monitor
            try
            {
                var delegations = await _session.Store!.GetDelegationsAsync(id);
                OnDelegationsLoaded?.Invoke(delegations);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SidebarViewModel] GetDelegations failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarViewModel] OpenConversation failed: {ex.Message}");
        }
    }

    private static string? ExtractTextFromContent(JsonNode content)
    {
        if (content is JsonArray arr)
        {
            foreach (var block in arr)
            {
                if (block is JsonObject blockObj && blockObj["type"]?.GetValue<string>() == "text")
                {
                    string? text = blockObj["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
        }
        if (content is JsonObject obj2 && obj2["content"] is JsonNode cn)
        {
            if (cn is JsonArray arr2)
            {
                foreach (var block in arr2)
                {
                    if (block is JsonObject bo && bo["type"]?.GetValue<string>() == "text")
                    {
                        string? text = bo["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text))
                            return text;
                    }
                }
            }
            else
            {
                return cn.ToString();
            }
        }
        return null;
    }

    /// <summary>Fired when a provider mismatch is detected during conversation switch.</summary>
    public event Action<long, string>? OnProviderMismatch;

    /// <summary>Fired when loading history entries for an opened conversation.</summary>
    public event Action<ChatEntryViewModel>? OnAddEntry;

    /// <summary>Fired when delegations are loaded for an opened conversation.</summary>
    public event Action<IReadOnlyList<DevMX.Core.Persistence.StoredDelegation>>? OnDelegationsLoaded;

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        try
        {
            long newId = await _session.CreateNewConversationAsync();
            _dispatch(() =>
            {
                var item = new ConversationItemViewModel(newId, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}", DateTime.UtcNow);
                Conversations.Insert(0, item);
                SelectedConversation = item;
                _clearChatEntries();
                _isTitled = false;
                OnClearProviderMismatch?.Invoke();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarViewModel] NewConversation failed: {ex.Message}");
        }
    }

    /// <summary>Fired when a new conversation is created (to clear mismatch state).</summary>
    public event Action? OnClearProviderMismatch;

    /// <summary>Delete a conversation by ID.</summary>
    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItemViewModel item)
    {
        long id = item.Id;
        bool wasOpen = (id == _session.ConversationId);

        try
        {
            await _session.Store!.DeleteConversationAsync(id);
            _dispatch(() =>
            {
                Conversations.Remove(item);

                if (wasOpen)
                {
                    if (Conversations.Count > 0)
                    {
                        SelectedConversation = Conversations[0];
                        _ = SelectConversationAsync(Conversations[0]);
                    }
                    else
                    {
                        _ = NewConversationAsync();
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarViewModel] DeleteConversation failed: {ex.Message}");
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
                    SelectedConversation.Title = autoTitle;
                _isTitled = true;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SidebarViewModel] AutoTitle failed: {ex.Message}");
        }
    }

    /// <summary>Refresh the conversation list from the store (after reconnect, etc.).</summary>
    internal async Task RefreshListAsync(long currentConversationId)
    {
        await PopulateConversationsAsync(currentConversationId);
    }
}
