using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace DevMX.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly Action<Action> _dispatch;
    private Func<string, Task>? _onTurnComplete;
    private Action<string, string>? _openDiffTab;
    private Func<string, Task>? _openFile;

    // Tool names that carry a file path in their args
    private static readonly HashSet<string> FileToolNames = new()
    {
        "read_file", "open_file", "create_file", "patch_file", "diff_file"
    };

    // Arg keys that hold the file path for various tools
    private static readonly HashSet<string> FilePathArgKeys = new()
    {
        "path", "filename", "file", "fileName"
    };

    [ObservableProperty]
    private ObservableCollection<ChatEntryViewModel> entries;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool isInitialized;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool isSendDisabled;

    /// <summary>Inverse of IsSendDisabled — used for IsEnabled binding on the input TextBox.</summary>
    public bool CanSendInput => !IsSendDisabled;

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

    /// <summary>Set the callback for opening diff tabs from tool results.</summary>
    internal void SetOpenDiffTabCallback(Action<string, string> callback)
    {
        _openDiffTab = callback;
    }

    /// <summary>Set the callback for opening files from clickable tool entries.</summary>
    internal void SetOpenFileCallback(Func<string, Task> callback)
    {
        _openFile = callback;
    }

    /// <summary>Extracts a file path from tool args JSON, if the tool is file-related.</summary>
    private static string? ExtractFilePath(string toolName, string argJson)
    {
        if (!FileToolNames.Contains(toolName))
            return null;

        try
        {
            var obj = JsonNode.Parse(argJson)?.AsObject();
            if (obj == null)
                return null;

            foreach (var key in FilePathArgKeys)
            {
                if (obj.ContainsKey(key))
                {
                    var val = obj[key]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(val))
                        return val;
                }
            }
        }
        catch
        {
            // Best-effort — ignore parse errors
        }
        return null;
    }

    /// <summary>Command to open a file from a clickable tool entry.</summary>
    [RelayCommand]
    private async Task OpenToolFileAsync(ChatEntryViewModel entry)
    {
        if (entry.FilePath == null || _openFile == null)
            return;

        try
        {
            await _openFile(entry.FilePath);
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info, $"[error] Could not open file: {ex.Message}"));
            });
        }
    }

    internal void SetInitialized(bool value)
    {
        IsInitialized = value;
    }

    internal void ClearEntries()
    {
        Entries.Clear();
    }

    internal void ShowProviderMismatchInfo(long conversationId, string providerName)
    {
        _dispatch(() =>
        {
            ClearEntries();
            Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info,
                $"[info] conversation #{conversationId} belongs to provider '{providerName}' — switch provider in Settings to open it"));
            IsSendDisabled = true;
            OnPropertyChanged(nameof(CanSendInput));
        });
    }

    internal void ClearProviderMismatch()
    {
        _dispatch(() =>
        {
            IsSendDisabled = false;
            OnPropertyChanged(nameof(CanSendInput));
        });
    }

    private bool CanSend()
    {
        return !IsBusy && IsInitialized && !IsSendDisabled && !string.IsNullOrWhiteSpace(InputText);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrWhiteSpace(text) || IsBusy || !IsInitialized || IsSendDisabled)
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
                    var filePath = ExtractFilePath(name, argJson);
                    _dispatch(() =>
                    {
                        Entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, $"[tool] {name}({argTrunc})", filePath));
                    });
                },
                onToolResult: (name, argJson, resultText) =>
                {
                    // Auto-open diff tabs when diff_file tool returns
                    if (name == "diff_file" && _openDiffTab != null)
                    {
                        var fileName = ExtractFilePath(name, argJson) ?? "diff";
                        var title = Path.GetFileName(fileName) ?? "diff";
                        _dispatch(() =>
                        {
                            _openDiffTab($"diff: {title}", resultText);
                        });
                    }
                },
                ct: CancellationToken.None);

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
