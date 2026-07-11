using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class TaskItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string jobId = string.Empty;

    [ObservableProperty]
    private string state = string.Empty;

    [ObservableProperty]
    private string detail = string.Empty;

    [ObservableProperty]
    private bool isLive;

    [ObservableProperty]
    private string journalText = string.Empty;

    [ObservableProperty]
    private string answerText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> changedFiles = new();

    private Action<string, string>? _openDiffTab;
    private Func<string, Task>? _cancelTaskFunc;

    /// <summary>True when the task is in a cancellable state (queued or running and live).</summary>
    public bool IsCancellable => IsLive && (State == "queued" || State == "running");

    public TaskItemViewModel(string jobId, string state, string detail, bool isLive = false)
    {
        JobId = jobId;
        State = state;
        Detail = detail;
        IsLive = isLive;
        ChangedFiles = new ObservableCollection<string>();
    }

    internal void SetOpenDiffTabCallback(Action<string, string> callback)
    {
        _openDiffTab = callback;
    }

    internal void SetCancelTaskCallback(Func<string, Task> callback)
    {
        _cancelTaskFunc = callback;
    }

    [RelayCommand]
    private async Task CancelTaskAsync()
    {
        if (_cancelTaskFunc == null || !IsCancellable) return;
        try
        {
            await _cancelTaskFunc(JobId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TaskItemViewModel] Cancel error for {JobId}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenDiff(string filePath)
    {
        if (_openDiffTab != null && !string.IsNullOrEmpty(filePath))
        {
            var name = Path.GetFileName(filePath) ?? filePath;
            _openDiffTab($"diff: {name}", filePath);
        }
    }

    /// <summary>
    /// Parses changed files from journal JSON text. Looks for action kinds patch/save/create with file paths.
    /// </summary>
    internal void ParseChangedFilesFromJournal()
    {
        if (string.IsNullOrEmpty(JournalText))
            return;

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(JournalText);
            if (parsed.ValueKind == System.Text.Json.JsonValueKind.Object && parsed.TryGetProperty("actions", out var actions))
            {
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var action in actions.EnumerateArray())
                {
                    var kind = action.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (kind is "patch" or "save" or "create")
                    {
                        // Try to get file path from various property names
                        string? filePath = null;
                        if (action.TryGetProperty("file", out var f)) filePath = f.GetString();
                        else if (action.TryGetProperty("filename", out var fn)) filePath = fn.GetString();
                        else if (action.TryGetProperty("path", out var p)) filePath = p.GetString();
                        else if (action.TryGetProperty("detail", out var d))
                        {
                            // Try to extract a file path from the detail string
                            var detailStr = d.GetString() ?? "";
                            var parts = detailStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var part in parts)
                            {
                                if (part.Contains('/') || part.Contains('\\') || part.EndsWith(".cs") || part.EndsWith(".xaml") || part.EndsWith(".json"))
                                {
                                    filePath = part;
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(filePath))
                            files.Add(filePath);
                    }
                }

                // Update the ChangedFiles collection
                var current = new HashSet<string>(ChangedFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    if (!current.Contains(f))
                        ChangedFiles.Add(f);
                }
            }
        }
        catch
        {
            // Best-effort — ignore parse errors
        }
    }
}
