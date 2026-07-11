using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevMX.Core.Persistence;
using DevMX.Core.Providers;
using Xunit;

namespace DevMX.Core.Tests;

public class AgenticLoopTests : IDisposable
{
    private readonly string _dbFile;

    public AgenticLoopTests()
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

    // --- Fake implementations for testing ---

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public StubHttpHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public HttpRequestMessage? LastRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var body = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeToolExecutor : IMcpToolExecutor
    {
        public List<(string Name, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = new();
        public Dictionary<string, string> ToolResults { get; set; } = new();

        public Task<IReadOnlyList<ToolDefinition>> ListToolDefinitionsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ToolDefinition>>(new List<ToolDefinition>
            {
                new("read_file", "Read a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
                new("devmind_task_start", "Start a task", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })
            });
        }

        public Task<string> CallToolAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        {
            Calls.Add((name, args));
            var result = ToolResults.TryGetValue(name, out var r) ? r : "OK";
            return Task.FromResult(result);
        }
    }

    private static string MakeResponse(JsonArray content, string stopReason, int inputTokens = 10, int outputTokens = 5)
    {
        var obj = new JsonObject
        {
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens }
        };
        return obj.ToJsonString();
    }

    private static JsonArray MakeTextContent(string text)
    {
        return new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
    }

    private static JsonArray MakeToolUseContent(string id, string name, JsonObject input)
    {
        return new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input } };
    }

    // --- Test 1: Text-only turn ---
    [Fact]
    public async Task TextOnlyTurn_HistoryHasUserAndAssistant()
    {
        // Arrange
        var responseJson = MakeResponse(MakeTextContent("Hello there!"), "end_turn");
        var handler = new StubHttpHandler(responseJson);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutor();
        var loop = new AgenticLoop(client, executor, store, convId, null);

        var assistantTexts = new List<string>();
        var toolCalls = new List<(string, string)>();

        // Act
        await loop.RunTurnAsync("Say hello", t => assistantTexts.Add(t), (n, a) => toolCalls.Add((n, a)), default);

        // Assert
        Assert.Single(assistantTexts);
        Assert.Equal("Hello there!", assistantTexts[0]);
        Assert.Empty(toolCalls);

        var messages = await store.GetMessagesAsync(convId);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
    }

    // --- Test 2: Tool round-trip ---
    [Fact]
    public async Task ToolRoundTrip_ExecutorCalledAndHistoryCorrect()
    {
        // Arrange
        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "read_file", new JsonObject { ["filename"] = "x" }),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done reading!"), "end_turn");
        var handler = new StubHttpHandler(toolUseResponse, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutor
        {
            ToolResults = { ["read_file"] = "FILE CONTENT" }
        };
        var loop = new AgenticLoop(client, executor, store, convId, null);

        var assistantTexts = new List<string>();
        var toolCalls = new List<(string, string)>();

        // Act
        await loop.RunTurnAsync("Read file x", t => assistantTexts.Add(t), (n, a) => toolCalls.Add((n, a)), default);

        // Assert executor was called
        Assert.Single(executor.Calls);
        Assert.Equal("read_file", executor.Calls[0].Name);
        Assert.Equal("x", executor.Calls[0].Args["filename"]);

        // Assert tool callback fired
        Assert.Single(toolCalls);
        Assert.Equal("read_file", toolCalls[0].Item1);

        // Assert store has 4 messages: user, assistant (tool_use), user (tool_result), assistant (final)
        var messages = await store.GetMessagesAsync(convId);
        Assert.Equal(4, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("user", messages[2].Role);
        Assert.Equal("assistant", messages[3].Role);

        // Verify the tool_result message contains the correct tool_use_id and content.
        var toolResultContent = JsonNode.Parse(messages[2].ContentJson)!.AsArray();
        Assert.Single(toolResultContent);
        var resultBlock = toolResultContent[0]!.AsObject();
        Assert.Equal("tool_result", resultBlock["type"]!.GetValue<string>());
        Assert.Equal("toolu_01", resultBlock["tool_use_id"]!.GetValue<string>());
        var innerContent = resultBlock["content"]!.AsArray();
        Assert.Single(innerContent);
        Assert.Equal("FILE CONTENT", innerContent[0]!.AsObject()["text"]!.GetValue<string>());

        // Verify the second request's last message is the tool_result user message.
        var lastRequest = handler.LastRequest!;
        var lastBody = await lastRequest.Content!.ReadAsStringAsync();
        var lastBodyObj = JsonNode.Parse(lastBody)!.AsObject();
        var lastMessages = lastBodyObj["messages"]!.AsArray();
        // The last message in the second request should be the tool_result user message.
        var lastMsg = lastMessages[lastMessages.Count - 1]!.AsObject();
        Assert.Equal("user", lastMsg["role"]!.GetValue<string>());
    }

    // --- Test 3: Delegation capture ---
    [Fact]
    public async Task DelegationCapture_RecordsDelegation()
    {
        // Arrange
        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "devmind_task_start", new JsonObject { ["prompt"] = "Fix the build" }),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Task started!"), "end_turn");
        var handler = new StubHttpHandler(toolUseResponse, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutor
        {
            ToolResults = { ["devmind_task_start"] = "{\"job_id\":\"job-9\",\"state\":\"queued\"}" }
        };
        var loop = new AgenticLoop(client, executor, store, convId, null);

        // Act
        await loop.RunTurnAsync("Start a task", _ => { }, (_, _) => { }, default);

        // Assert
        var delegations = await store.GetDelegationsAsync(convId);
        Assert.Single(delegations);
        Assert.Equal("job-9", delegations[0].JobId);
        Assert.Equal("Fix the build", delegations[0].Brief);
    }

    // --- Test 4: maxIterations stops without throwing ---
    [Fact]
    public async Task MaxIterations_StopsAfterNIterations()
    {
        // Arrange
        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "read_file", new JsonObject { ["filename"] = "x" }),
            "tool_use");
        // Queue enough responses for more than maxIterations.
        var responses = new string[20];
        for (int i = 0; i < 20; i++)
            responses[i] = toolUseResponse;

        var handler = new StubHttpHandler(responses);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutor
        {
            ToolResults = { ["read_file"] = "OK" }
        };
        var loop = new AgenticLoop(client, executor, store, convId, null, maxIterations: 3);

        // Act — should not throw.
        await loop.RunTurnAsync("Loop forever", _ => { }, (_, _) => { }, default);

        // Assert: exactly 3 tool calls (one per iteration).
        Assert.Equal(3, executor.Calls.Count);
    }

    // --- Test 5: LoadHistoryAsync rebuilds from store ---
    [Fact]
    public async Task LoadHistory_RebuildsFromStore()
    {
        // Arrange
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");
        await store.AppendMessageAsync(convId, "user", "[{\"type\":\"text\",\"text\":\"Hello\"}]");
        await store.AppendMessageAsync(convId, "assistant", "[{\"type\":\"text\",\"text\":\"Hi there\"}]");

        // Act
        var history = await AgenticLoop.LoadHistoryAsync(store, convId);

        // Assert
        Assert.Equal(2, history.Count);
        // History is now List<JsonNode> with { role, content } shape.
        var msg0 = history[0]!.AsObject();
        Assert.Equal("user", msg0["role"]!.GetValue<string>());
        Assert.Equal("Hello", msg0["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>());

        var msg1 = history[1]!.AsObject();
        Assert.Equal("assistant", msg1["role"]!.GetValue<string>());
        Assert.Equal("Hi there", msg1["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>());
    }

    // --- Test 6: Preloaded history continues conversation ---
    [Fact]
    public async Task PreloadedHistory_ContinuesConversation()
    {
        // Arrange
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        // Preload some history as JsonNode messages.
        var history = new List<JsonNode>
        {
            new JsonObject { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Previous turn" } } },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Previous reply" } } }
        };

        var responseJson = MakeResponse(MakeTextContent("Final answer"), "end_turn");
        var handler = new StubHttpHandler(responseJson);
        var client = new AnthropicClient("key", "model", handler);
        var executor = new FakeToolExecutor();

        var loop = new AgenticLoop(client, executor, store, convId, null, 50, history);

        var assistantTexts = new List<string>();

        // Act
        await loop.RunTurnAsync("New question", t => assistantTexts.Add(t), (_, _) => { }, default);

        // Assert
        Assert.Single(assistantTexts);
        Assert.Equal("Final answer", assistantTexts[0]);

        var messages = await store.GetMessagesAsync(convId);
        // Only the new messages from this turn (user + assistant).
        Assert.Equal(2, messages.Count);
    }

    // --- Test 7: onToolResult callback fires with tool result ---
    [Fact]
    public async Task OnToolResult_CallbackFiresWithResult()
    {
        // Arrange
        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "read_file", new JsonObject { ["path"] = "test.cs" }),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done!"), "end_turn");
        var handler = new StubHttpHandler(toolUseResponse, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutor
        {
            ToolResults = { ["read_file"] = "FILE CONTENT FROM EXECUTOR" }
        };
        var loop = new AgenticLoop(client, executor, store, convId, null);

        var toolResults = new List<(string Name, string Args, string Result)>();

        // Act
        await loop.RunTurnAsync("Read file",
            _ => { },
            (_, _) => { },
            default,
            (name, args, result) => toolResults.Add((name, args, result)));

        // Assert
        Assert.Single(toolResults);
        Assert.Equal("read_file", toolResults[0].Name);
        Assert.Equal("FILE CONTENT FROM EXECUTOR", toolResults[0].Result);
        Assert.Contains("path", toolResults[0].Args);
    }

    // --- Test 8: Restricted profile denies excluded tool execution ---
    [Fact]
    public async Task RestrictedProfile_DeniesExcludedTool()
    {
        // Arrange — executor exposes both read_file and patch_file
        var executor = new FakeToolExecutorWithPatchFile();
        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "patch_file", new JsonObject { ["filename"] = "x.cs" }),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done!"), "end_turn");
        var handler = new StubHttpHandler(toolUseResponse, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var loop = new AgenticLoop(client, executor, store, convId, null, 50, ToolProfiles.Restricted);

        var toolResults = new List<(string Name, string Args, string Result)>();

        // Act
        await loop.RunTurnAsync("Patch file",
            _ => { },
            (_, _) => { },
            default,
            (name, args, result) => toolResults.Add((name, args, result)));

        // Assert — patch_file was denied, executor CallToolAsync NOT called for it
        Assert.Single(toolResults);
        Assert.Equal("patch_file", toolResults[0].Name);
        Assert.Contains("[denied]", toolResults[0].Result);
        Assert.Contains("patch_file", toolResults[0].Result);
        Assert.Contains("devmind_task_start", toolResults[0].Result);
        // Executor should not have been called for patch_file
        Assert.Empty(executor.Calls);
    }

    // --- Test 9: Restricted profile allows allowlisted tool execution ---
    [Fact]
    public async Task RestrictedProfile_AllowsAllowlistedTool()
    {
        // Arrange
        var executor = new FakeToolExecutor
        {
            ToolResults = { ["read_file"] = "ALLOWED CONTENT" }
        };
        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "read_file", new JsonObject { ["filename"] = "x.cs" }),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done!"), "end_turn");
        var handler = new StubHttpHandler(toolUseResponse, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var loop = new AgenticLoop(client, executor, store, convId, null, 50, ToolProfiles.Restricted);

        var toolResults = new List<(string Name, string Args, string Result)>();

        // Act
        await loop.RunTurnAsync("Read file",
            _ => { },
            (_, _) => { },
            default,
            (name, args, result) => toolResults.Add((name, args, result)));

        // Assert — read_file was allowed
        Assert.Single(toolResults);
        Assert.Equal("read_file", toolResults[0].Name);
        Assert.Equal("ALLOWED CONTENT", toolResults[0].Result);
        Assert.Single(executor.Calls);
    }

    // Fake executor that exposes patch_file in tool definitions
    private sealed class FakeToolExecutorWithPatchFile : IMcpToolExecutor
    {
        public List<(string Name, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = new();

        public Task<IReadOnlyList<ToolDefinition>> ListToolDefinitionsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ToolDefinition>>(new List<ToolDefinition>
            {
                new("read_file", "Read a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
                new("patch_file", "Patch a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })
            });
        }

        public Task<string> CallToolAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        {
            Calls.Add((name, args));
            return Task.FromResult("EXECUTED");
        }
    }

    // --- Throttle tests ---

    // Fake executor that exposes devmind_task_status
    private sealed class FakeToolExecutorWithStatus : IMcpToolExecutor
    {
        public List<(string Name, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = new();

        public Task<IReadOnlyList<ToolDefinition>> ListToolDefinitionsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ToolDefinition>>(new List<ToolDefinition>
            {
                new("devmind_task_status", "Check task status", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
                new("read_file", "Read a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })
            });
        }

        public Task<string> CallToolAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        {
            Calls.Add((name, args));
            return Task.FromResult("{\"state\":\"running\"}");
        }
    }

    [Fact]
    public async Task PollThrottle_SameJobId_DelaysSecondCall()
    {
        // Arrange — pollThrottleSeconds=1 so the test runs fast
        var statusInput1 = new JsonObject { ["job_id"] = "job-1" };
        var statusInput2 = new JsonObject { ["job_id"] = "job-1" };

        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "devmind_task_status", statusInput1),
            "tool_use");
        var toolUseResponse2 = MakeResponse(
            MakeToolUseContent("toolu_02", "devmind_task_status", statusInput2),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done!"), "end_turn");

        var handler = new StubHttpHandler(toolUseResponse, toolUseResponse2, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutorWithStatus();
        var loop = new AgenticLoop(client, executor, store, convId, null, 50, ToolProfiles.Full, pollThrottleSeconds: 1);

        // Act
        var sw = Stopwatch.StartNew();
        await loop.RunTurnAsync("Check status", _ => { }, (_, _) => { }, default);
        sw.Stop();

        // Assert: two calls executed, total time >= 1s (first instant, second throttled by ~1s)
        Assert.Equal(2, executor.Calls.Count);
        Assert.True(sw.ElapsedMilliseconds >= 900, $"Expected >=900ms but got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task PollThrottle_DifferentJobId_NoDelay()
    {
        // Arrange
        var statusInput1 = new JsonObject { ["job_id"] = "job-1" };
        var statusInput2 = new JsonObject { ["job_id"] = "job-2" };

        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "devmind_task_status", statusInput1),
            "tool_use");
        var toolUseResponse2 = MakeResponse(
            MakeToolUseContent("toolu_02", "devmind_task_status", statusInput2),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done!"), "end_turn");

        var handler = new StubHttpHandler(toolUseResponse, toolUseResponse2, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutorWithStatus();
        var loop = new AgenticLoop(client, executor, store, convId, null, 50, ToolProfiles.Full, pollThrottleSeconds: 5);

        // Act
        var sw = Stopwatch.StartNew();
        await loop.RunTurnAsync("Check status", _ => { }, (_, _) => { }, default);
        sw.Stop();

        // Assert: two calls, no throttle delay (different job_ids)
        Assert.Equal(2, executor.Calls.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected <2000ms but got {sw.ElapsedMilliseconds}ms — different job_ids should not throttle");
    }

    [Fact]
    public async Task PollThrottle_OtherTool_NoDelay()
    {
        // Arrange — read_file should NOT be throttled
        var readInput = new JsonObject { ["filename"] = "x.cs" };

        var toolUseResponse = MakeResponse(
            MakeToolUseContent("toolu_01", "read_file", readInput),
            "tool_use");
        var finalResponse = MakeResponse(MakeTextContent("Done!"), "end_turn");

        var handler = new StubHttpHandler(toolUseResponse, finalResponse);
        var client = new AnthropicClient("key", "model", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("anthropic", "model", "/work");

        var executor = new FakeToolExecutor
        {
            ToolResults = { ["read_file"] = "FILE CONTENT" }
        };
        var loop = new AgenticLoop(client, executor, store, convId, null, 50, ToolProfiles.Full, pollThrottleSeconds: 5);

        // Act
        var sw = Stopwatch.StartNew();
        await loop.RunTurnAsync("Read file", _ => { }, (_, _) => { }, default);
        sw.Stop();

        // Assert: tool executed, no throttle delay
        Assert.Single(executor.Calls);
        Assert.Equal("read_file", executor.Calls[0].Name);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected <2000ms but got {sw.ElapsedMilliseconds}ms — non-status tools should not throttle");
    }
}
