using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using DevMX.Core.Persistence;

namespace DevMX.App.ViewModels;

public partial class TaskMonitorViewModel : ObservableObject
{
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _pollCts;
    private Func<string, Task<string>>? _pollTaskFunc;
    private Func<string, Task<string>>? _fetchResultFunc;
    private Func<string, Task<string>>? _fetchDiffFunc;
    private Action<string, string>? _openDiffTab;

    [ObservableProperty]
    private ObservableCollection<TaskItemViewModel> tasks;

    [ObservableProperty]
    private TaskItemViewModel? selectedTask;

    [ObservableProperty]
    private string journalText = string.Empty;

    public TaskMonitorViewModel(Action<Action> dispatch)
    {
        _dispatch = dispatch;
        Tasks = new ObservableCollection<TaskItemViewModel>();
    }

    /// <summary>Set the callback for polling task status via MCP.</summary>
    internal void SetPollTaskCallback(Func<string, Task<string>> callback)
    {
        _pollTaskFunc = callback;
    }

    /// <summary>Set the callback for fetching task result via MCP.</summary>
    internal void SetFetchResultCallback(Func<string, Task<string>> callback)
    {
        _fetchResultFunc = callback;
    }

    /// <summary>Set the callback for fetching diff via MCP.</summary>
    internal void SetFetchDiffCallback(Func<string, Task<string>> callback)
    {
        _fetchDiffFunc = callback;
    }

    /// <summary>Set the callback for opening diff tabs in the viewer.</summary>
    internal void SetOpenDiffTabCallback(Action<string, string> callback)
    {
        _openDiffTab = callback;
        // Wire up all existing task items
        foreach (var task in Tasks)
        {
            task.SetOpenDiffTabCallback(callback);
        }
    }

    /// <summary>Start the polling loop for live tasks.</summary>
    internal void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = RunPollLoopAsync(_pollCts.Token);
    }

    /// <summary>Stop the polling loop.</summary>
    internal void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        var maxDuration = TimeSpan.FromMinutes(30);
        var startTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - startTime > maxDuration)
                {
                    // Max duration reached — cancel all live tasks
                    _dispatch(() =>
                    {
                        foreach (var task in Tasks.Where(t => t.IsLive && (t.State == "queued" || t.State == "running")))
                        {
                            task.State = "cancelled";
                            task.IsLive = false;
                        }
                    });
                    break;
                }

                var liveTasks = Tasks.Where(t => t.IsLive && (t.State == "queued" || t.State == "running")).ToList();

                foreach (var task in liveTasks)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        if (_pollTaskFunc == null) continue;

                        var statusResult = await _pollTaskFunc(task.JobId);
                        if (!string.IsNullOrEmpty(statusResult))
                        {
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(statusResult);
                            if (parsed.TryGetProperty("state", out var stateElem))
                            {
                                var newState = stateElem.GetString();

                                // If task completed via polling, fetch result first (async)
                                string? fetchedResult = null;
                                if (newState is "done" or "failed" or "cancelled" && _fetchResultFunc != null)
                                {
                                    try
                                    {
                                        fetchedResult = await _fetchResultFunc(task.JobId);
                                    }
                                    catch
                                    {
                                        // Best-effort
                                    }
                                }

                                _dispatch(() =>
                                {
                                    task.State = newState ?? "unknown";

                                    if (newState is "done" or "failed" or "cancelled")
                                    {
                                        task.IsLive = false;
                                        if (!string.IsNullOrEmpty(fetchedResult))
                                        {
                                            task.JournalText = fetchedResult;
                                            task.ParseChangedFilesFromJournal();

                                            // Update selected task journal display
                                            if (SelectedTask == task)
                                            {
                                                UpdateJournalDisplay();
                                            }
                                        }
                                    }
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _dispatch(() =>
                        {
                            task.State = "(poll error)";
                            task.IsLive = false;
                        });
                        Console.WriteLine($"[TaskMonitorViewModel] Poll error for {task.JobId}: {ex.Message}");
                    }
                }

                // If no live tasks remaining, stop polling
                if (!Tasks.Any(t => t.IsLive && (t.State == "queued" || t.State == "running")))
                {
                    break;
                }

                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TaskMonitorViewModel] Poll loop error: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>
    /// Add or update a task by JobId. Deduplicates: if a task with the same JobId exists, updates it.
    /// New tasks are added at the beginning (newest first).
    /// </summary>
    internal void AddOrUpdateTask(string jobId, string state, string detail, bool isLive = true)
    {
        _dispatch(() =>
        {
            var existing = Tasks.FirstOrDefault(t => t.JobId == jobId);
            if (existing != null)
            {
                existing.State = state;
                existing.Detail = detail;
                existing.IsLive = isLive;
                return;
            }

            var item = new TaskItemViewModel(jobId, state, detail, isLive);
            item.SetOpenDiffTabCallback(_openDiffTab!);
            Tasks.Insert(0, item);
        });
    }

    /// <summary>
    /// Update a task's state and optionally store journal/answer text.
    /// </summary>
    internal void UpdateTaskResult(string jobId, string state, string? journalText = null, string? answerText = null)
    {
        _dispatch(() =>
        {
            var item = Tasks.FirstOrDefault(t => t.JobId == jobId);
            if (item == null) return;

            item.State = state;
            if (!string.IsNullOrEmpty(journalText))
            {
                item.JournalText = journalText;
                item.ParseChangedFilesFromJournal();
            }
            if (!string.IsNullOrEmpty(answerText))
            {
                item.AnswerText = answerText;
            }

            // If completed, mark as not live
            if (state is "done" or "failed" or "cancelled")
            {
                item.IsLive = false;
            }

            // Update journal display if this is the selected task
            if (SelectedTask == item)
            {
                UpdateJournalDisplay();
            }
        });
    }

    /// <summary>
    /// Populate tasks from historical delegations (IsLive=false, read-only).
    /// </summary>
    internal void PopulateFromDelegations(IReadOnlyList<StoredDelegation> delegations)
    {
        _dispatch(() =>
        {
            Tasks.Clear();
            foreach (var del in delegations)
            {
                var state = del.FinalState ?? "pending";
                var item = new TaskItemViewModel(del.JobId, state, del.Brief, isLive: false);
                if (!string.IsNullOrEmpty(del.JournalJson))
                {
                    item.JournalText = del.JournalJson;
                    item.ParseChangedFilesFromJournal();
                }
                item.SetOpenDiffTabCallback(_openDiffTab!);
                Tasks.Add(item);
            }
            // Clear selection when repopulating
            SelectedTask = null;
            JournalText = string.Empty;
        });
    }

    /// <summary>
    /// Called when a task is selected — update the journal display.
    /// </summary>
    private void UpdateJournalDisplay()
    {
        if (SelectedTask == null)
        {
            JournalText = string.Empty;
            return;
        }

        var item = SelectedTask;
        var lines = new List<string>();

        // Show readable journal rendering if available
        if (!string.IsNullOrEmpty(item.JournalText))
        {
            lines.Add($"=== Journal for {item.JobId} ({item.State}) ===");
            lines.Add(string.Empty);

            // Try to render as structured JSON
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(item.JournalText);
                if (parsed.ValueKind == System.Text.Json.JsonValueKind.Object && parsed.TryGetProperty("actions", out var actions))
                {
                    // Render one line per action: "kind: detail [ok|FAIL]"
                    foreach (var action in actions.EnumerateArray())
                    {
                        var kind = action.TryGetProperty("kind", out var k) ? k.GetString() : "unknown";
                        var detail = action.TryGetProperty("detail", out var d) ? d.GetString() : "";
                        var status = action.TryGetProperty("status", out var s) ? s.GetString() : "";
                        var statusMarker = status?.ToLower() switch
                        {
                            "ok" or "success" or "done" => "[ok]",
                            "fail" or "failed" or "error" => "[FAIL]",
                            _ => ""
                        };
                        lines.Add($"{kind}: {detail} {statusMarker}".Trim());
                    }
                }
                else
                {
                    // Raw JSON text
                    lines.Add(item.JournalText);
                }
            }
            catch
            {
                // Raw text fallback
                lines.Add(item.JournalText);
            }
        }

        if (!string.IsNullOrEmpty(item.AnswerText))
        {
            lines.Add(string.Empty);
            lines.Add($"=== Answer ===");
            lines.Add(item.AnswerText);
        }

        if (lines.Count == 0)
        {
            JournalText = $"Task: {item.JobId} | State: {item.State} | Detail: {item.Detail}";
        }
        else
        {
            JournalText = string.Join("\n", lines);
        }
    }

    partial void OnSelectedTaskChanged(TaskItemViewModel? value)
    {
        UpdateJournalDisplay();
    }

    /// <summary>
    /// Fetch diff for a changed file and open it in the viewer.
    /// </summary>
    [RelayCommand]
    private async Task OpenDiffForFileAsync(string filePath)
    {
        if (_fetchDiffFunc == null || string.IsNullOrEmpty(filePath)) return;

        try
        {
            var diffText = await _fetchDiffFunc(filePath);
            var name = Path.GetFileName(filePath) ?? filePath;
            _openDiffTab?.Invoke($"diff: {name}", diffText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TaskMonitorViewModel] Diff fetch error for {filePath}: {ex.Message}");
        }
    }

    /// <summary>Cleanup polling on disposal.</summary>
    public void Dispose()
    {
        StopPolling();
    }
}
