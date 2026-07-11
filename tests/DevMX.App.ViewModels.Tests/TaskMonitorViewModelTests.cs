using DevMX.App.ViewModels;
using DevMX.Core.Persistence;

namespace DevMX.App.ViewModels.Tests;

public class TaskMonitorViewModelTests
{
    private TaskMonitorViewModel CreateVm() => new(TaskCache);
    private void TaskCache(Action action) => action();

    [Fact]
    public void Constructor_EmptyTasks()
    {
        var vm = CreateVm();
        Assert.Empty(vm.Tasks);
        Assert.Null(vm.SelectedTask);
        Assert.Equal(string.Empty, vm.JournalText);
    }

    [Fact]
    public void AddOrUpdateTask_AddsNewTask()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "queued", "create file.txt", isLive: true);

        Assert.Single(vm.Tasks);
        var task = vm.Tasks[0];
        Assert.Equal("job-1", task.JobId);
        Assert.Equal("queued", task.State);
        Assert.Equal("create file.txt", task.Detail);
        Assert.True(task.IsLive);
    }

    [Fact]
    public void AddOrUpdateTask_DedupeByJobId()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "queued", "initial detail", isLive: true);
        vm.AddOrUpdateTask("job-1", "running", "updated detail", isLive: true);

        Assert.Single(vm.Tasks);
        var task = vm.Tasks[0];
        Assert.Equal("job-1", task.JobId);
        Assert.Equal("running", task.State);
        Assert.Equal("updated detail", task.Detail);
        Assert.True(task.IsLive);
    }

    [Fact]
    public void AddOrUpdateTask_NewestFirst()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "queued", "first", isLive: true);
        vm.AddOrUpdateTask("job-2", "queued", "second", isLive: true);

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("job-2", vm.Tasks[0].JobId);
        Assert.Equal("job-1", vm.Tasks[1].JobId);
    }

    [Fact]
    public void UpdateTaskResult_UpdatesExistingTask()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "queued", "detail", isLive: true);
        vm.UpdateTaskResult("job-1", "done", "{\"actions\":[]}", "answer text");

        var task = vm.Tasks[0];
        Assert.Equal("done", task.State);
        Assert.Equal("{\"actions\":[]}", task.JournalText);
        Assert.Equal("answer text", task.AnswerText);
        Assert.False(task.IsLive);
    }

    [Fact]
    public void UpdateTaskResult_IgnoresUnknownJobId()
    {
        var vm = CreateVm();
        vm.UpdateTaskResult("nonexistent", "done");
        Assert.Empty(vm.Tasks);
    }

    [Fact]
    public void UpdateTaskResult_CompletedStatesMarkNotLive()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "running", "detail", isLive: true);

        vm.UpdateTaskResult("job-1", "done");
        Assert.False(vm.Tasks[0].IsLive);

        vm.AddOrUpdateTask("job-2", "running", "detail", isLive: true);
        vm.UpdateTaskResult("job-2", "failed");
        Assert.False(vm.Tasks[1].IsLive);

        vm.AddOrUpdateTask("job-3", "running", "detail", isLive: true);
        vm.UpdateTaskResult("job-3", "cancelled");
        Assert.False(vm.Tasks[2].IsLive);
    }

    [Fact]
    public void PopulateFromDelegations_ClearsAndPopulates()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("old-job", "done", "old", isLive: false);

        var delegations = new List<StoredDelegation>
        {
            new StoredDelegation(1, 100, "del-1", "fix bug", "done", "{\"actions\":[]}", "2025-01-01"),
            new StoredDelegation(2, 100, "del-2", "add feature", "failed", null, "2025-01-02"),
        };

        vm.PopulateFromDelegations(delegations);

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("del-1", vm.Tasks[0].JobId);
        Assert.False(vm.Tasks[0].IsLive);
        Assert.Equal("done", vm.Tasks[0].State);
        Assert.Equal("fix bug", vm.Tasks[0].Detail);

        Assert.Equal("del-2", vm.Tasks[1].JobId);
        Assert.Equal("failed", vm.Tasks[1].State);
        Assert.Null(vm.SelectedTask);
        Assert.Equal(string.Empty, vm.JournalText);
    }

    [Fact]
    public void PopulateFromDelegations_ParsesJournalJson()
    {
        var vm = CreateVm();
        var journalJson = "{\"actions\":[{\"kind\":\"patch\",\"file\":\"src/Service.cs\",\"detail\":\"fixed bug\"}]}";
        var delegations = new List<StoredDelegation>
        {
            new StoredDelegation(1, 100, "job-1", "fix", "done", journalJson, "2025-01-01"),
        };

        vm.PopulateFromDelegations(delegations);

        Assert.Single(vm.Tasks);
        Assert.Single(vm.Tasks[0].ChangedFiles);
        Assert.Contains("src/Service.cs", vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void JournalText_FromStructuredJson()
    {
        var vm = CreateVm();
        var journalJson = "{\"actions\":[{\"kind\":\"patch\",\"detail\":\"src/Service.cs\",\"status\":\"ok\"},{\"kind\":\"create\",\"detail\":\"test.txt\",\"status\":\"fail\"}]}";
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = journalJson;
        vm.Tasks[0].ParseChangedFilesFromJournal();
        vm.SelectedTask = vm.Tasks[0];

        Assert.Contains("patch: src/Service.cs [ok]", vm.JournalText);
        Assert.Contains("create: test.txt [FAIL]", vm.JournalText);
    }

    [Fact]
    public void JournalText_RawFallback()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "some raw text that is not json";
        vm.SelectedTask = vm.Tasks[0];

        Assert.Contains("some raw text that is not json", vm.JournalText);
    }

    [Fact]
    public void JournalText_EmptyWhenNoSelection()
    {
        var vm = CreateVm();
        Assert.Equal(string.Empty, vm.JournalText);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_PatchAction()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "{\"actions\":[{\"kind\":\"patch\",\"file\":\"src/A.cs\"}]}";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Single(vm.Tasks[0].ChangedFiles);
        Assert.Contains("src/A.cs", vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_SaveAction()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "{\"actions\":[{\"kind\":\"save\",\"filename\":\"src/B.cs\"}]}";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Single(vm.Tasks[0].ChangedFiles);
        Assert.Contains("src/B.cs", vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_CreateAction()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "{\"actions\":[{\"kind\":\"create\",\"path\":\"new/file.txt\"}]}";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Single(vm.Tasks[0].ChangedFiles);
        Assert.Contains("new/file.txt", vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_IgnoresNonFileActions()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "{\"actions\":[{\"kind\":\"read\",\"file\":\"src/A.cs\"},{\"kind\":\"patch\",\"file\":\"src/B.cs\"}]}";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Single(vm.Tasks[0].ChangedFiles);
        Assert.Contains("src/B.cs", vm.Tasks[0].ChangedFiles);
        Assert.DoesNotContain("src/A.cs", vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_MultipleActions()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "{\"actions\":[{\"kind\":\"patch\",\"file\":\"A.cs\"},{\"kind\":\"create\",\"file\":\"B.cs\"},{\"kind\":\"save\",\"file\":\"C.cs\"}]}";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Equal(3, vm.Tasks[0].ChangedFiles.Count);
        Assert.Contains("A.cs", vm.Tasks[0].ChangedFiles);
        Assert.Contains("B.cs", vm.Tasks[0].ChangedFiles);
        Assert.Contains("C.cs", vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_InvalidJson_NoCrash()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "not valid json {{{";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Empty(vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void ParseChangedFilesFromJournal_EmptyJournal_NoCrash()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix", isLive: false);
        vm.Tasks[0].JournalText = "";
        vm.Tasks[0].ParseChangedFilesFromJournal();

        Assert.Empty(vm.Tasks[0].ChangedFiles);
    }

    [Fact]
    public void StopPolling_StopsCancellationToken()
    {
        var vm = CreateVm();
        vm.StartPolling();
        vm.StopPolling();
        // Should not throw; the poll loop should exit cleanly
    }

    [Fact]
    public void SelectedTask_ChangesJournalDisplay()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "first", isLive: false);
        vm.AddOrUpdateTask("job-2", "done", "second", isLive: false);

        // AddOrUpdateTask adds newest first, so job-2 is at index 0, job-1 at index 1
        vm.SelectedTask = vm.Tasks[0]; // job-2
        Assert.Contains("job-2", vm.JournalText);

        vm.SelectedTask = vm.Tasks[1]; // job-1
        Assert.Contains("job-1", vm.JournalText);
    }

    [Fact]
    public void CancelTaskCallback_InvokedWithJobId()
    {
        // Arrange
        var vm = CreateVm();
        string? capturedJobId = null;
        vm.SetCancelTaskCallback(async (jobId) =>
        {
            capturedJobId = jobId;
        });

        vm.AddOrUpdateTask("job-1", "running", "fix bug", isLive: true);

        // Act
        vm.Tasks[0].CancelTaskCommand.Execute(null);

        // Assert
        Assert.Equal("job-1", capturedJobId);
    }

    [Fact]
    public void CancelTaskCallback_NotInvokedForNonCancellableState()
    {
        // Arrange
        var vm = CreateVm();
        bool called = false;
        vm.SetCancelTaskCallback(async (jobId) =>
        {
            called = true;
        });

        vm.AddOrUpdateTask("job-1", "done", "fix bug", isLive: false);

        // Act
        vm.Tasks[0].CancelTaskCommand.Execute(null);

        // Assert — done tasks are not cancellable
        Assert.False(called);
    }

    [Fact]
    public void IsCancellable_True_ForRunningLiveTask()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "running", "fix bug", isLive: true);
        Assert.True(vm.Tasks[0].IsCancellable);
    }

    [Fact]
    public void IsCancellable_True_ForQueuedLiveTask()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "queued", "fix bug", isLive: true);
        Assert.True(vm.Tasks[0].IsCancellable);
    }

    [Fact]
    public void IsCancellable_False_ForDoneTask()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "done", "fix bug", isLive: false);
        Assert.False(vm.Tasks[0].IsCancellable);
    }

    [Fact]
    public void IsCancellable_False_ForFailedTask()
    {
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "failed", "fix bug", isLive: false);
        Assert.False(vm.Tasks[0].IsCancellable);
    }

    [Fact]
    public void IsCancellable_False_ForNonLiveRunningTask()
    {
        // Historical task — running but not live
        var vm = CreateVm();
        vm.AddOrUpdateTask("job-1", "running", "fix bug", isLive: false);
        Assert.False(vm.Tasks[0].IsCancellable);
    }
}
