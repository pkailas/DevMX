using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
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
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Pending attachments to send with the next message.</summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Adds a pending attachment (called from paste/drop handlers on the UI thread).</summary>
    public void AddAttachment(AttachmentViewModel attachment)
    {
        Attachments.Add(attachment);
    }

    /// <summary>Surface a non-fatal attachment problem (unsupported file, too large) in the chat.</summary>
    public void ReportAttachmentIssue(string message)
    {
        _dispatch(() => Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info, $"[info] {message}")));
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
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

    /// <summary>
    /// Collapses consecutive Tool entries at the end of the Entries collection into a single ToolSummary entry.
    /// Only collapses if there are 2+ consecutive Tool entries (single tool lines are left as-is).
    /// The round is scanned backwards from the end, stopping at non-Tool entries.
    /// </summary>
    internal void CollapseToolRound()
    {
        if (Entries.Count < 2)
            return;

        // Find the run of consecutive Tool entries at the end
        int lastToolIndex = Entries.Count - 1;
        while (lastToolIndex >= 0 && Entries[lastToolIndex].Kind == ChatEntryKind.Tool)
            lastToolIndex--;

        int firstToolIndex = lastToolIndex + 1;
        int toolCount = Entries.Count - 1 - firstToolIndex + 1; // = Entries.Count - 1 - firstToolIndex + 1

        // Do not collapse a single tool line
        if (toolCount <= 1)
            return;

        // Collect tool entries
        var toolEntries = new List<ChatEntryViewModel>();
        for (int i = firstToolIndex; i < Entries.Count; i++)
        {
            toolEntries.Add(Entries[i]);
        }

        // Build summary header: group by tool name, order of first occurrence, "xN" only when N>1
        var toolCounts = new System.Collections.Generic.Dictionary<string, int>();
        var toolOrder = new List<string>();
        foreach (var entry in toolEntries)
        {
            // Extract tool name from "[tool] name(args)" format
            string text = entry.Text;
            string toolName = "unknown";
            int afterBracket = text.IndexOf("] ") + 2;
            if (afterBracket > 1 && afterBracket < text.Length)
            {
                int parenIdx = text.IndexOf('(', afterBracket);
                toolName = parenIdx > afterBracket ? text.Substring(afterBracket, parenIdx - afterBracket) : text.Substring(afterBracket).TrimEnd(')');
            }

            if (!toolCounts.ContainsKey(toolName))
            {
                toolCounts[toolName] = 0;
                toolOrder.Add(toolName);
            }
            toolCounts[toolName]++;
        }

        var parts = new List<string>();
        foreach (var name in toolOrder)
        {
            int count = toolCounts[name];
            parts.Add(count > 1 ? $"{name} ×{count}" : name);
        }

        string headerText = $"[tool] used {string.Join(", ", (IEnumerable<string>)parts)}";

        // Create the summary entry
        var summary = new ChatEntryViewModel(ChatEntryKind.ToolSummary, headerText);
        foreach (var child in toolEntries)
        {
            summary.Children.Add(child);
        }

        // Remove the individual tool entries and insert the summary
        for (int i = 0; i < toolCount; i++)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
        Entries.Insert(firstToolIndex, summary);
    }

    /// <summary>
    /// Overload for history reload: takes a list of entries and returns a new list with tool rounds collapsed.
    /// </summary>
    internal static List<ChatEntryViewModel> CollapseToolRounds(List<ChatEntryViewModel> entries)
    {
        var result = new List<ChatEntryViewModel>();
        var pendingTools = new List<ChatEntryViewModel>();

        foreach (var entry in entries)
        {
            if (entry.Kind == ChatEntryKind.Tool)
            {
                pendingTools.Add(entry);
            }
            else
            {
                // Flush pending tools if there are 2+
                if (pendingTools.Count >= 2)
                {
                    result.Add(CreateToolSummary(pendingTools));
                }
                else if (pendingTools.Count == 1)
                {
                    result.Add(pendingTools[0]);
                }
                pendingTools.Clear();
                result.Add(entry);
            }
        }

        // Flush remaining tools at end
        if (pendingTools.Count >= 2)
        {
            result.Add(CreateToolSummary(pendingTools));
        }
        else if (pendingTools.Count == 1)
        {
            result.Add(pendingTools[0]);
        }

        return result;
    }

    private static ChatEntryViewModel CreateToolSummary(List<ChatEntryViewModel> toolEntries)
    {
        var toolCounts = new System.Collections.Generic.Dictionary<string, int>();
        var toolOrder = new List<string>();

        foreach (var entry in toolEntries)
        {
            string text = entry.Text;
            string toolName = "unknown";
            int afterBracket = text.IndexOf("] ") + 2;
            if (afterBracket > 1 && afterBracket < text.Length)
            {
                int parenIdx = text.IndexOf('(', afterBracket);
                toolName = parenIdx > afterBracket ? text.Substring(afterBracket, parenIdx - afterBracket) : text.Substring(afterBracket).TrimEnd(')');
            }

            if (!toolCounts.ContainsKey(toolName))
            {
                toolCounts[toolName] = 0;
                toolOrder.Add(toolName);
            }
            toolCounts[toolName]++;
        }

        var parts = new List<string>();
        foreach (var name in toolOrder)
        {
            int count = toolCounts[name];
            parts.Add(count > 1 ? $"{name} ×{count}" : name);
        }

        string headerText = $"[tool] used {string.Join(", ", (IEnumerable<string>)parts)}";

        var summary = new ChatEntryViewModel(ChatEntryKind.ToolSummary, headerText);
        foreach (var child in toolEntries)
        {
            summary.Children.Add(child);
        }
        return summary;
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
        return !IsBusy && IsInitialized && !IsSendDisabled
            && (!string.IsNullOrWhiteSpace(InputText) || HasAttachments);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrWhiteSpace(text) && !HasAttachments)
            return;
        if (IsBusy || !IsInitialized || IsSendDisabled)
        {
            // Never swallow a send silently - say why it was blocked.
            string reason = IsBusy ? "a turn is still running - press Stop (or Esc) to interrupt it"
                : !IsInitialized ? "the session is not connected yet - check the status bar / Reconnect"
                : "sending is disabled for this conversation (provider mismatch)";
            _dispatch(() => Entries.Add(new ChatEntryViewModel(ChatEntryKind.Info, $"[info] message not sent: {reason}")));
            return;
        }

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

        // Snapshot pending attachments for this turn.
        var attachments = Attachments.Count > 0
            ? Attachments.Select(a => a.ToChatAttachment()).ToList()
            : null;

        // Echo attachments in the user bubble so the transcript shows what was sent.
        string displayText = attachments == null
            ? text
            : (string.IsNullOrEmpty(text) ? "" : text + "\n")
              + $"[attached: {string.Join(", ", attachments.Select(a => a.FileName))}]";

        _dispatch(() =>
        {
            Entries.Add(new ChatEntryViewModel(ChatEntryKind.User, displayText));
            InputText = string.Empty;
            Attachments.Clear();
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
                        // If the last entry is a Tool (or ToolSummary), a tool round has ended.
                        // Collapse consecutive Tool entries before adding assistant text.
                        if (Entries.Count > 0 && Entries[^1].Kind == ChatEntryKind.Tool)
                        {
                            CollapseToolRound();
                        }

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
                ct: _turnCts.Token,
                attachments: attachments);

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
                // Turn finished — collapse any remaining tool round
                CollapseToolRound();

                IsBusy = false;
                SendCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            });
        }
    }
}
