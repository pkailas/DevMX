using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly Action<Action> _dispatch;
    private Func<string, Task>? _onTurnComplete;

    [ObservableProperty]
    private ObservableCollection<ChatEntryViewModel> entries;

    [ObservableProperty]
    private string inputText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isInitialized;

    public ChatViewModel(AppSession session, Action<Action> dispatch)
    {
        _session = session;
        _dispatch = dispatch;
        Entries = new ObservableCollection<ChatEntryViewModel>();
    }

    internal void SetAutoTitleCallback(Func<string, Task> callback)
    {
        _onTurnComplete = callback;
    }

    internal void SetInitialized(bool value)
    {
        IsInitialized = value;
    }

    internal void ClearEntries()
    {
        Entries.Clear();
    }

    private bool CanSend()
    {
        return !IsBusy && IsInitialized && !string.IsNullOrWhiteSpace(InputText);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrWhiteSpace(text) || IsBusy || !IsInitialized)
            return;

        _dispatch(() =>
        {
            Entries.Add(new ChatEntryViewModel(ChatEntryKind.User, text));
            InputText = string.Empty;
            IsBusy = true;
            SendCommand.NotifyCanExecuteChanged();
        });

        try
        {
            await _session.StartTurnAsync(
                text,
                onAssistantText: (chunk) =>
                {
                    _dispatch(() =>
                    {
                        // Append to the last assistant entry, or create one
                        if (Entries.Count > 0 && Entries[^1].Kind == ChatEntryKind.Assistant)
                        {
                            Entries[^1].Text += chunk;
                        }
                        else
                        {
                            Entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, chunk));
                        }
                    });
                },
                onToolCall: (name, argJson) =>
                {
                    string argTrunc = argJson.Length > 120 ? argJson[..120] + "\u2026" : argJson;
                    _dispatch(() =>
                    {
                        Entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, $"[tool] {name}({argTrunc})"));
                    });
                },
                CancellationToken.None);

            // Notify caller of successful turn completion (for auto-title, etc.)
            if (_onTurnComplete != null)
            {
                await _onTurnComplete(text);
            }
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                Entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, $"[error] {ex.Message}"));
            });
        }
        finally
        {
            _dispatch(() =>
            {
                IsBusy = false;
                SendCommand.NotifyCanExecuteChanged();
            });
        }
    }
}
