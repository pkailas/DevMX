using DevMX.App.ViewModels;
using DevMX.Core.Persistence;

namespace DevMX.App.ViewModels.Tests;

public class SidebarViewModelTests : IDisposable
{
    private readonly string _dbFile;

    public SidebarViewModelTests()
    {
        _dbFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbFile))
                File.Delete(_dbFile);
        }
        catch { }
    }

    /// <summary>BeginRename sets IsEditing=true and EditText=Title.</summary>
    [Fact]
    public void BeginRename_SetsEditingState()
    {
        var item = new ConversationItemViewModel(1, "Old Title", DateTime.UtcNow);
        var vm = CreateViewModel();

        vm.BeginRenameCommand.Execute(item);

        Assert.True(item.IsEditing);
        Assert.Equal("Old Title", item.EditText);
    }

    /// <summary>CancelRename sets IsEditing=false.</summary>
    [Fact]
    public void CancelRename_ClearsEditingState()
    {
        var item = new ConversationItemViewModel(1, "Old Title", DateTime.UtcNow);
        var vm = CreateViewModel();

        vm.BeginRenameCommand.Execute(item);
        Assert.True(item.IsEditing);

        vm.CancelRenameCommand.Execute(item);

        Assert.False(item.IsEditing);
        Assert.Equal("Old Title", item.Title); // Title unchanged
    }

    /// <summary>CommitRename with empty text cancels (sets IsEditing=false, title unchanged).</summary>
    [Fact]
    public async Task CommitRename_EmptyTextCancels()
    {
        var item = new ConversationItemViewModel(1, "Old Title", DateTime.UtcNow);
        var vm = CreateViewModel();

        vm.BeginRenameCommand.Execute(item);
        item.EditText = "   "; // whitespace only

        await vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.False(item.IsEditing);
        Assert.Equal("Old Title", item.Title);
    }

    /// <summary>HasSearchText is false when SearchText is empty.</summary>
    [Fact]
    public void HasSearchText_FalseWhenEmpty()
    {
        var vm = CreateViewModel();
        Assert.False(vm.HasSearchText);
    }

    /// <summary>HasSearchText is true when SearchText is non-empty.</summary>
    [Fact]
    public void HasSearchText_TrueWhenNonEmpty()
    {
        var vm = CreateViewModel();
        vm.SearchText = "hello";
        Assert.True(vm.HasSearchText);
    }

    /// <summary>SearchText clear resets HasSearchText.</summary>
    [Fact]
    public void HasSearchText_ResetsOnClear()
    {
        var vm = CreateViewModel();
        vm.SearchText = "hello";
        Assert.True(vm.HasSearchText);

        vm.SearchText = string.Empty;
        Assert.False(vm.HasSearchText);
    }

    /// <summary>Integration: CommitRename updates title in real store.</summary>
    [Fact]
    public async Task CommitRename_IntegrationUpdatesStore()
    {
        // Open a real store and create a conversation
        await using var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("p", "m", "/w", "Original Title");

        var fakeSession = FakeAppSession(store, convId);

        var vm = new SidebarViewModel(
            fakeSession,
            action => action(),
            () => { });

        var item = new ConversationItemViewModel(convId, "Original Title", DateTime.UtcNow);
        vm.BeginRenameCommand.Execute(item);
        item.EditText = "New Title";

        await vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.False(item.IsEditing);
        Assert.Equal("New Title", item.Title);

        // Verify store was updated
        var list = await store.ListConversationsAsync();
        Assert.Single(list);
        Assert.Equal("New Title", list[0].Title);
    }

    /// <summary>Integration: CommitRename on current conversation sets _isTitled, preventing auto-title clobber.</summary>
    [Fact]
    public async Task CommitRename_CurrentConversationSetsTitled()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("p", "m", "/w", "Original");

        var fakeSession = FakeAppSession(store, convId);

        var vm = new SidebarViewModel(
            fakeSession,
            action => action(),
            () => { });

        var item = new ConversationItemViewModel(convId, "Original", DateTime.UtcNow);
        vm.BeginRenameCommand.Execute(item);
        item.EditText = "Manual Title";

        await vm.CommitRenameCommand.ExecuteAsync(item);

        // Now call AutoTitleAsync - it should be a no-op because _isTitled was set
        await vm.AutoTitleAsync("some first message that would normally auto-title");
        Assert.Equal("Manual Title", item.Title); // Title should NOT change to auto-title
    }

    /// <summary>SearchText change triggers search debounce (integration with real store).</summary>
    [Fact]
    public async Task SearchText_TriggerSearch()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);
        await store.CreateConversationAsync("p", "m", "/w", "Alpha Chat");
        await store.CreateConversationAsync("p", "m", "/w", "Beta Chat");
        await store.CreateConversationAsync("p", "m", "/w", "Gamma Chat");

        var fakeSession = FakeAppSession(store, 0);

        var vm = new SidebarViewModel(
            fakeSession,
            action => action(), // sync dispatch
            () => { });

        // Set SearchText - debounce should fire after 300ms
        vm.SearchText = "Alpha";

        // Wait for debounce
        await Task.Delay(500);

        Assert.Single(vm.Conversations);
        Assert.Equal("Alpha Chat", vm.Conversations[0].Title);
    }

    /// <summary>SearchText cleared restores full list.</summary>
    [Fact]
    public async Task SearchText_ClearRestoresFullList()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);
        await store.CreateConversationAsync("p", "m", "/w", "Alpha");
        await store.CreateConversationAsync("p", "m", "/w", "Beta");

        var fakeSession = FakeAppSession(store, 1);

        var vm = new SidebarViewModel(
            fakeSession,
            action => action(),
            () => { });

        // Populate full list
        await vm.PopulateConversationsAsync(1);
        Assert.Equal(2, vm.Conversations.Count);

        // Search to filter
        vm.SearchText = "Alpha";
        await Task.Delay(500);
        Assert.Single(vm.Conversations);

        // Clear search
        vm.SearchText = string.Empty;
        await Task.Delay(500);
        Assert.Equal(2, vm.Conversations.Count);
    }

    private SidebarViewModel CreateViewModel()
    {
        var fakeSession = FakeAppSession(null!, 0);
        return new SidebarViewModel(
            fakeSession,
            action => action(),
            () => { });
    }

    /// <summary>Minimal fake AppSession for testing SidebarViewModel without Moq (uses reflection on sealed class).</summary>
    private static AppSession FakeAppSession(ConversationStore? store, long conversationId)
    {
        var session = new AppSession(new DevMxSettings());
        // Use reflection to set private fields
        var storeField = typeof(AppSession).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        storeField.SetValue(session, store);

        var convIdProp = typeof(AppSession).GetProperty("ConversationId")!;
        convIdProp.SetValue(session, conversationId);

        return session;
    }
}
