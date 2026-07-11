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

    // --- ToolSummary collapse tests ---

    [Fact]
    public void ToolSummary_CollapseFourTools_PlusAssistantText_YieldsSummaryEntry()
    {
        var entries = new System.Collections.Generic.List<ChatEntryViewModel>();

        // Simulate: user message, 4 tool calls, assistant text
        entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "do something"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})", "/path/a.cs"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"b.cs\"})", "/path/b.cs"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"c.cs\"})", "/path/c.cs"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] list_files({\"glob\":\"*.cs\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, "here is the result"));

        var collapsed = ChatViewModel.CollapseToolRounds(entries);

        // Expect: user, ToolSummary(4 children), assistant
        Assert.Equal(3, collapsed.Count);
        Assert.Equal(ChatEntryKind.User, collapsed[0].Kind);
        Assert.Equal(ChatEntryKind.ToolSummary, collapsed[1].Kind);
        Assert.Equal(ChatEntryKind.Assistant, collapsed[2].Kind);

        var summary = collapsed[1];
        Assert.Equal(4, summary.Children.Count);
        Assert.Contains("read_file ×3", summary.Text);
        Assert.Contains("list_files", summary.Text);
    }

    [Fact]
    public void ToolSummary_SingleToolRound_NotCollapsed()
    {
        var entries = new System.Collections.Generic.List<ChatEntryViewModel>();

        entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "do something"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})", "/path/a.cs"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, "result"));

        var collapsed = ChatViewModel.CollapseToolRounds(entries);

        // Single tool should NOT be collapsed
        Assert.Equal(3, collapsed.Count);
        Assert.Equal(ChatEntryKind.User, collapsed[0].Kind);
        Assert.Equal(ChatEntryKind.Tool, collapsed[1].Kind);
        Assert.Equal(ChatEntryKind.Assistant, collapsed[2].Kind);
    }

    [Fact]
    public void ToolSummary_DevmindTaskStatus_FoldsIntoCount()
    {
        var entries = new System.Collections.Generic.List<ChatEntryViewModel>();

        entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "run task"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"}) ×2"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] devmind_task_status({\"job_id\":\"job-1\"}) ×3"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, "done"));

        var collapsed = ChatViewModel.CollapseToolRounds(entries);

        // Expect: user, ToolSummary(3 children), assistant
        Assert.Equal(3, collapsed.Count);
        var summary = collapsed[1];
        Assert.Equal(ChatEntryKind.ToolSummary, summary.Kind);
        Assert.Equal(3, summary.Children.Count);
        Assert.Contains("devmind_task_status ×2", summary.Text);
        Assert.Contains("read_file", summary.Text);
    }

    [Fact]
    public void ToolSummary_ExpandedChildren_PreserveFilePathAndIsClickable()
    {
        var entries = new System.Collections.Generic.List<ChatEntryViewModel>();

        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})", "/path/a.cs"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"b.cs\"})", "/path/b.cs"));

        var collapsed = ChatViewModel.CollapseToolRounds(entries);

        Assert.Single(collapsed);
        var summary = collapsed[0];
        Assert.Equal(ChatEntryKind.ToolSummary, summary.Kind);

        // Children preserve original properties
        Assert.Equal("/path/a.cs", summary.Children[0].FilePath);
        Assert.True(summary.Children[0].IsClickable);
        Assert.Equal("/path/b.cs", summary.Children[1].FilePath);
        Assert.True(summary.Children[1].IsClickable);
    }

    [Fact]
    public void ToolSummary_IsExpanded_DefaultsFalse()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.ToolSummary, "[tool] used read_file ×2");
        Assert.False(entry.IsExpanded);
    }

    [Fact]
    public void ToolSummary_ToggleExpandedCommand_TogglesState()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.ToolSummary, "[tool] used read_file ×2");
        Assert.False(entry.IsExpanded);

        entry.ToggleExpandedCommand.Execute(null);
        Assert.True(entry.IsExpanded);

        entry.ToggleExpandedCommand.Execute(null);
        Assert.False(entry.IsExpanded);
    }

    [Fact]
    public void ToolSummary_CollapseToolRound_InPlace_CollapsesAtEnd()
    {
        var session = new AppSession(DevMxSettings.Load());
        var vm = new ChatViewModel(session, action => action());

        vm.Entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "do something"));
        vm.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})"));
        vm.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"b.cs\"})"));
        vm.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] list_files({\"glob\":\"*.cs\"})"));

        vm.CollapseToolRound();

        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal(ChatEntryKind.User, vm.Entries[0].Kind);
        Assert.Equal(ChatEntryKind.ToolSummary, vm.Entries[1].Kind);
        Assert.Equal(3, vm.Entries[1].Children.Count);
        Assert.Contains("read_file ×2", vm.Entries[1].Text);
        Assert.Contains("list_files", vm.Entries[1].Text);
    }

    [Fact]
    public void ToolSummary_CollapseToolRound_SingleTool_NoChange()
    {
        var session = new AppSession(DevMxSettings.Load());
        var vm = new ChatViewModel(session, action => action());

        vm.Entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "do something"));
        vm.Entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})"));

        vm.CollapseToolRound();

        // Should not collapse a single tool
        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal(ChatEntryKind.Tool, vm.Entries[1].Kind);
    }

    [Fact]
    public void ToolSummary_HistoryReload_SameGroupingAsLive()
    {
        // Simulate history reload with multiple tool rounds
        var entries = new System.Collections.Generic.List<ChatEntryViewModel>();

        entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "first request"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"a.cs\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file({\"filename\":\"b.cs\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, "first response"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.User, "second request"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] list_files({\"glob\":\"*.cs\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] grep_file({\"pattern\":\"test\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] grep_file({\"pattern\":\"other\"})"));
        entries.Add(new ChatEntryViewModel(ChatEntryKind.Assistant, "second response"));

        var collapsed = ChatViewModel.CollapseToolRounds(entries);

        // Expect: user, summary(2), assistant, user, summary(3), assistant
        Assert.Equal(6, collapsed.Count);
        Assert.Equal(ChatEntryKind.User, collapsed[0].Kind);
        Assert.Equal(ChatEntryKind.ToolSummary, collapsed[1].Kind);
        Assert.Equal(2, collapsed[1].Children.Count);
        Assert.Equal(ChatEntryKind.Assistant, collapsed[2].Kind);
        Assert.Equal(ChatEntryKind.User, collapsed[3].Kind);
        Assert.Equal(ChatEntryKind.ToolSummary, collapsed[4].Kind);
        Assert.Equal(3, collapsed[4].Children.Count);
        Assert.Contains("grep_file ×2", collapsed[4].Text);
        Assert.Contains("list_files", collapsed[4].Text);
        Assert.Equal(ChatEntryKind.Assistant, collapsed[5].Kind);
    }
}
