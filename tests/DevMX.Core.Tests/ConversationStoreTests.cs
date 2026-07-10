using DevMX.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace DevMX.Core.Tests;

public class ConversationStoreTests : IDisposable
{
    private readonly string _dbFile;

    public ConversationStoreTests()
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

    private static string ToolUseJson =>
        "[{\"type\":\"text\",\"text\":\"Let me run a tool.\"},{\"type\":\"tool_use\",\"id\":\"toolu_01A\",\"name\":\"run_shell\",\"input\":{\"command\":\"echo hello\"}}]";

    private static string ToolResultJson =>
        "[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_01A\",\"content\":\"hello\\n\"}]";

    private static string SimpleText(string text) =>
        $"[{{\"type\":\"text\",\"text\":\"{text}\"}}]";

    // --- Test 1: Round-trip fidelity ---
    [Fact]
    public async Task RoundTrip_FidelityPreservedAfterReopen()
    {
        await using (var store = await ConversationStore.OpenAsync(_dbFile))
        {
            var convId = await store.CreateConversationAsync(
                "anthropic", "claude-sonnet-4-20250514", "/tmp/work", "Test Conv");

            await store.AppendMessageAsync(convId, "user", SimpleText("Run a shell command"));
            await store.AppendMessageAsync(convId, "assistant", ToolUseJson, "claude-sonnet-4-20250514");
            await store.AppendMessageAsync(convId, "user", ToolResultJson);
            await store.AppendMessageAsync(convId, "assistant", SimpleText("Done!"));
        }

        await using var store2 = await ConversationStore.OpenAsync(_dbFile);
        var messages = await store2.GetMessagesAsync(1);

        Assert.Equal(4, messages.Count);

        Assert.Equal("user", messages[0].Role);
        Assert.Equal(SimpleText("Run a shell command"), messages[0].ContentJson);

        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal(ToolUseJson, messages[1].ContentJson);

        Assert.Equal("user", messages[2].Role);
        Assert.Equal(ToolResultJson, messages[2].ContentJson);

        Assert.Equal("assistant", messages[3].Role);
        Assert.Equal(SimpleText("Done!"), messages[3].ContentJson);
    }

    // --- Test 2: seq assignment ---
    [Fact]
    public async Task Seq_AssignedSequentiallyAndUniqueAcrossConversations()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var conv1 = await store.CreateConversationAsync("p1", "m1", "/w1");
        var conv2 = await store.CreateConversationAsync("p2", "m2", "/w2");

        await store.AppendMessageAsync(conv1, "user", "[\"msg1a\"]");
        await store.AppendMessageAsync(conv2, "user", "[\"msg2a\"]");
        await store.AppendMessageAsync(conv1, "assistant", "[\"msg1b\"]");
        await store.AppendMessageAsync(conv2, "assistant", "[\"msg2b\"]");
        await store.AppendMessageAsync(conv1, "user", "[\"msg1c\"]");

        var msgs1 = await store.GetMessagesAsync(conv1);
        var msgs2 = await store.GetMessagesAsync(conv2);

        Assert.Equal(3, msgs1.Count);
        Assert.Equal(2, msgs2.Count);

        Assert.Equal(1, msgs1[0].Seq);
        Assert.Equal(2, msgs1[1].Seq);
        Assert.Equal(3, msgs1[2].Seq);

        Assert.Equal(1, msgs2[0].Seq);
        Assert.Equal(2, msgs2[1].Seq);
    }

    // --- Test 3: Delegation lifecycle ---
    [Fact]
    public async Task Delegation_RecordCompleteReadBack()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var convId = await store.CreateConversationAsync("p", "m", "/w");
        var delId = await store.RecordDelegationAsync(convId, "job-abc", "Fix the build");

        Assert.True(delId > 0);

        var dels = await store.GetDelegationsAsync(convId);
        Assert.Single(dels);
        Assert.Null(dels[0].FinalState);
        Assert.Null(dels[0].JournalJson);

        string journalJson = "[{\"step\":1,\"action\":\"build\"},{\"step\":2,\"action\":\"fix\"}]";
        await store.CompleteDelegationAsync("job-abc", "success", journalJson);

        dels = await store.GetDelegationsAsync(convId);
        Assert.Single(dels);
        Assert.Equal("success", dels[0].FinalState);
        Assert.Equal(journalJson, dels[0].JournalJson);
    }

    // --- Test 4: Cascade delete ---
    [Fact]
    public async Task Cascade_DeleteConversationRemovesMessagesAndDelegations()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var convId = await store.CreateConversationAsync("p", "m", "/w");
        await store.AppendMessageAsync(convId, "user", "[\"hello\"]");
        await store.AppendMessageAsync(convId, "assistant", "[\"hi\"]");
        await store.RecordDelegationAsync(convId, "job-x", "Do something");

        using var conn = new SqliteConnection($"Data Source={_dbFile}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", convId);
        cmd.ExecuteNonQuery();

        var msgs = await store.GetMessagesAsync(convId);
        var dels = await store.GetDelegationsAsync(convId);

        Assert.Empty(msgs);
        Assert.Empty(dels);
    }

    // --- Test 5: ListConversationsAsync orders by updated_at desc ---
    [Fact]
    public async Task ListConversations_OrdersByUpdatedAtDesc()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var convA = await store.CreateConversationAsync("p1", "m1", "/w1", "Conv A");
        await Task.Delay(50);
        var convB = await store.CreateConversationAsync("p2", "m2", "/w2", "Conv B");

        await store.AppendMessageAsync(convA, "user", "[\"update A\"]");

        var list = await store.ListConversationsAsync();
        Assert.Equal(2, list.Count);

        Assert.Equal("Conv A", list[0].Title);
        Assert.Equal("Conv B", list[1].Title);
    }

    // --- Test 6: DeleteConversationAsync removes messages/delegations via public API ---
    [Fact]
    public async Task DeleteConversation_RemovesMessagesAndDelegationsViaApi()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var convId = await store.CreateConversationAsync("p", "m", "/w", "To Delete");
        await store.AppendMessageAsync(convId, "user", "[\"hello\"]");
        await store.AppendMessageAsync(convId, "assistant", "[\"hi\"]");
        await store.RecordDelegationAsync(convId, "job-del", "Delegate task");

        // Delete via the public API
        await store.DeleteConversationAsync(convId);

        var msgs = await store.GetMessagesAsync(convId);
        var dels = await store.GetDelegationsAsync(convId);
        var list = await store.ListConversationsAsync();

        Assert.Empty(msgs);
        Assert.Empty(dels);
        Assert.Empty(list);
    }

    // --- Test 7: SearchConversationsAsync matches by title ---
    [Fact]
    public async Task SearchConversations_MatchesByTitle()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        await store.CreateConversationAsync("p", "m", "/w", "Alpha Project");
        await store.CreateConversationAsync("p", "m", "/w", "Beta Project");
        await store.CreateConversationAsync("p", "m", "/w", "Gamma Test");

        var results = await store.SearchConversationsAsync("Beta");
        Assert.Single(results);
        Assert.Equal("Beta Project", results[0].Title);
    }

    // --- Test 8: SearchConversationsAsync matches by message content ---
    [Fact]
    public async Task SearchConversations_MatchesByMessageContent()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var convId = await store.CreateConversationAsync("p", "m", "/w", "Untitled");
        await store.AppendMessageAsync(convId, "user", "[{\"type\":\"text\",\"text\":\"discuss the budget\"}]");

        var results = await store.SearchConversationsAsync("budget");
        Assert.Single(results);
        Assert.Equal("Untitled", results[0].Title);
    }

    // --- Test 9: SearchConversationsAsync returns empty for no match ---
    [Fact]
    public async Task SearchConversations_NoMatchReturnsEmpty()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        await store.CreateConversationAsync("p", "m", "/w", "Alpha");
        await store.CreateConversationAsync("p", "m", "/w", "Beta");

        var results = await store.SearchConversationsAsync("zzznonexistent");
        Assert.Empty(results);
    }

    // --- Test 10: SearchConversationsAsync handles special characters (parameterization) ---
    [Fact]
    public async Task SearchConversations_ParameterizedSpecialChars()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        var convId = await store.CreateConversationAsync("p", "m", "/w", "Test with % and ' chars");
        await store.AppendMessageAsync(convId, "user", "[{\"type\":\"text\",\"text\":\"100% done with it's work\"}]");

        // Query with % should not break (it's a literal, not a wildcard)
        var results1 = await store.SearchConversationsAsync("%");
        Assert.Single(results1);

        // Query with ' should not break SQL injection
        var results2 = await store.SearchConversationsAsync("it's");
        Assert.Single(results2);
    }

    // --- Test 11: SearchConversationsAsync is case-insensitive ---
    [Fact]
    public async Task SearchConversations_CaseInsensitive()
    {
        await using var store = await ConversationStore.OpenAsync(_dbFile);

        await store.CreateConversationAsync("p", "m", "/w", "Hello World");

        var results = await store.SearchConversationsAsync("hello");
        Assert.Single(results);
        Assert.Equal("Hello World", results[0].Title);
    }
}
