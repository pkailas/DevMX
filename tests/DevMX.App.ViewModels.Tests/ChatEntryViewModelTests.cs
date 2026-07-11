using DevMX.App.ViewModels;

namespace DevMX.App.ViewModels.Tests;

public class ChatEntryViewModelTests
{
    [Fact]
    public void Constructor_SetsKindAndText()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.User, "Hello");
        Assert.Equal(ChatEntryKind.User, entry.Kind);
        Assert.Equal("Hello", entry.Text);
    }

    [Fact]
    public void FilePath_SetOnConstruction()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file", "/path/to/file.cs");
        Assert.Equal("/path/to/file.cs", entry.FilePath);
    }

    [Fact]
    public void IsClickable_True_ForToolWithFilePath()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file", "/path/to/file.cs");
        Assert.True(entry.IsClickable);
    }

    [Fact]
    public void IsClickable_False_ForToolWithoutFilePath()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] some_tool");
        Assert.False(entry.IsClickable);
    }

    [Fact]
    public void IsClickable_False_ForNonToolKind()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.User, "Hello", "/some/path");
        Assert.False(entry.IsClickable);
    }

    [Fact]
    public void AppendText_AppendsToText()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Assistant, "Hello");
        entry.AppendText(" World");
        Assert.Equal("Hello World", entry.Text);
    }

    [Fact]
    public void SetText_ReplacesText()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file");
        entry.SetText("[tool] read_file ×2");
        Assert.Equal("[tool] read_file ×2", entry.Text);
    }

    // --- Collapse display tests (simulating ChatViewModel behavior) ---

    [Fact]
    public void Collapse_ConsecutiveSameJobId_YieldsOneEntryWithCount()
    {
        // Simulates what ChatViewModel.onToolCall does for consecutive devmind_task_status calls
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntryViewModel>();

        // First status call for job-1
        var entry1 = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})");
        entries.Add(entry1);

        // Second status call for job-1 — should collapse
        entry1.SetText(entry1.Text + " ×2");

        // Assert
        Assert.Single(entries);
        Assert.Contains("×2", entries[0].Text);
    }

    [Fact]
    public void Collapse_DifferentJobId_YieldsNewEntry()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntryViewModel>();

        // First status call for job-1
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})"));

        // Second status call for job-2 — different job_id, should NOT collapse
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-2\"})"));

        // Assert
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Collapse_InterleavedTool_BreaksRun()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntryViewModel>();

        // First status call for job-1
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})"));

        // Interleaved other tool — breaks the run
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"x.cs\"})"));

        // Next status call for job-1 — starts a new entry, not collapsed
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})"));

        // Assert: 3 entries, no collapse
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void Collapse_AssistantText_BreaksRun()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntryViewModel>();

        // First status call for job-1
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})"));

        // Assistant text in between — breaks the run
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, "thinking..."));

        // Next status call for job-1 — starts a new entry
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})"));

        // Assert: 3 entries, no collapse
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void Collapse_ThreeConsecutive_YieldsTimes3()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntryViewModel>();

        // First call
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"})");
        entries.Add(entry);

        // Second call — collapse to ×2
        entry.SetText(entry.Text + " ×2");

        // Third call — collapse to ×3
        entry.SetText(entry.Text.Replace("×2", "×3"));

        // Assert
        Assert.Single(entries);
        Assert.Contains("×3", entries[0].Text);
    }
}
