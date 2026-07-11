using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Threading;

namespace DevMX.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly Action<Action> _dispatch;
    private Func<string, Task>? _onTurnComplete;
    private Action<string, string>? _openDiffTab;
    private Func<string, Task>? _openFile;
    private Action<string, string, string>? _onTaskToolResult;
    private SlashCommandHandler? _slashCommandHandler;
    private CancellationTokenSource? _turnCts;

    /// <summary>Set the callback for notifying the task monitor about task-related tool results.</summary>
    internal void SetTaskToolResultCallback(Action<string, string, string> callback)
    {
        _onTaskToolResult = callback;
    }

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

    /// <summary>
    /// Stop the current turn by cancelling its CancellationTokenSource.
    /// Note: stopping the chat TURN does not kill a delegation already started —
    /// that is by design (DM owns it; the task monitor keeps tracking it,
    /// and its Cancel button is the kill switch).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _turnCts?.Cancel();
    }

    private bool CanStop() => IsBusy;

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

    /// <summary>Set the slash command handler for intercepting "/" commands.</summary>
    internal void SetSlashCommandHandler(SlashCommandHandler handler)
    {
        _slashCommandHandler = handler;
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

    /// <summary>Extracts the job_id from devmind_task_status arg JSON.</summary>
    private static string? ExtractJobId(string argJson)
    {
        try
        {
            var obj = JsonNode.Parse(argJson)?.AsObject();
            return obj?["job_id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the job_id from a collapsed entry text like "[tool] devmind_task_status({"job_id":"job-1"}) \u00d73".</summary>
    private static string? ExtractJobIdFromEntryText(string entryText)
    {
        try
        {
            // Find the JSON args portion between the first '(' and the matching ')'
            int openParen = entryText.IndexOf('(');
            if (openParen < 0) return null;

            // Find the closing paren — need to handle nested braces
            int depth = 0;
            int closeParen = -1;
            for (int i = openParen; i < entryText.Length; i++)
            {
                char c = entryText[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ')' && depth == 0) { closeParen = i; break; }
            }
            if (closeParen < 0) return null;

            string jsonPart = entryText.Substring(openParen + 1, closeParen - openParen - 1);
            var obj = JsonNode.Parse(jsonPart)?.AsObject();
            return obj?["job_id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
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

        // Intercept slash commands before sending to the model
        if (_slashCommandHandler != null && text.StartsWith("/"))
        {
            _dispatch(() =>
            {
                InputText = string.Empty;
                IsBusy = true;
                SendCommand.NotifyCanExecuteChanged();
            });

            try
            {
                _slashCommandHandler.ExecuteCommand(text);
            }
            finally
            {
                _dispatch(() =>
                {
                    IsBusy = false;
                    SendCommand.NotifyCanExecuteChanged();
                });
            }
            return;
        }

        _dispatch(() =>
        {
            Entries.Add(new ChatEntryViewModel(ChatEntryKind.User, text));
            InputText = string.Empty;
            IsBusy = true;
            SendCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        });

        _turnCts = new CancellationTokenSource();

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
                        // Collapse consecutive devmind_task_status calls for the same job_id
                        if (name == "devmind_task_status" && Entries.Count > 0)
                        {
                            var lastEntry = Entries[^1];
                            if (lastEntry.Kind == ChatEntryKind.Tool && lastEntry.Text.StartsWith("[tool] devmind_task_status("))
                            {
                                // Extract job_id from the new call
                                string? newJobId = ExtractJobId(argJson);
                                // Extract job_id from the last entry's text
                                string? lastJobId = ExtractJobIdFromEntryText(lastEntry.Text);

                                if (newJobId != null && lastJobId != null && newJobId == lastJobId)
                                {
                                    // Increment the collapse count
                                    string currentText = lastEntry.Text;
                                    int multiplyIdx = currentText.LastIndexOf("\u00d7");
                                    if (multiplyIdx >= 0)
                                    {
                                        // Already collapsed — increment count
                                        int countStart = multiplyIdx + 1;
                                        if (int.TryParse(currentText[countStart..], out int count))
                                        {
                                            lastEntry.SetText(currentText[..countStart] + (count + 1));
                                        }
                                        else
                                        {
                                            lastEntry.SetText(currentText[..countStart] + "2");
                                        }
                                    }
                                    else
                                    {
                                        // First collapse — add ×2
                                        lastEntry.SetText(lastEntry.Text + " \u00d72");
                                    }
                                    return;
                                }
                            }
                        }

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

                    // Notify task monitor about task-related tool results
                    _onTaskToolResult?.Invoke(name, argJson, resultText);
                },
                ct: _turnCts.Token);

            // Notify caller of successful turn completion (for auto-title, etc.)
            if (_onTurnComplete != null)
            {
                await _onTurnComplete(text);
            }
        }
        catch (OperationCanceledException)
        {
            // Turn was stopped by user — append info message, not an error.
            _dispatch(() =>
            {
                Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info, "[info] turn stopped"));
            });
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
            _turnCts?.Dispose();
            _turnCts = null;
            _dispatch(() =>
            {
                IsBusy = false;
                SendCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            });
        }
    }
}
