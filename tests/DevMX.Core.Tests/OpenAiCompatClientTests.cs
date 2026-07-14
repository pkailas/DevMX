using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevMX.Core.Persistence;
using DevMX.Core.Providers;
using Xunit;

namespace DevMX.Core.Tests;

public class OpenAiCompatClientTests : IDisposable
{
    private readonly string _dbFile;

    public OpenAiCompatClientTests()
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

    // --- Stub handlers ---

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; set; }
        public string ResponseBody { get; set; } = "{}";
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = StatusCode,
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            });
        }
    }

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
                new("read_file", "Read a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["filename"] = new JsonObject { ["type"] = "string" } } })
            });
        }

        public Task<string> CallToolAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        {
            Calls.Add((name, args));
            var result = ToolResults.TryGetValue(name, out var r) ? r : "OK";
            return Task.FromResult(result);
        }
    }

    // --- OpenAI response builders ---

    private static string MakeTextResponse(string content, string finishReason = "stop")
    {
        var obj = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = finishReason
                }
            }
        };
        return obj.ToJsonString();
    }

    private static string MakeToolCallsResponse(string toolCallId, string toolName, string argsJson, string finishReason = "tool_calls")
    {
        var obj = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = null,
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = toolCallId,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = toolName,
                                    ["arguments"] = argsJson
                                }
                            }
                        }
                    },
                    ["finish_reason"] = finishReason
                }
            }
        };
        return obj.ToJsonString();
    }

    // --- Test 1: Wire format — URL, headers, body ---
    [Fact]
    public async Task WireFormat_AssertsUrlHeadersAndBody()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            ResponseBody = MakeTextResponse("hello")
        };
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", "test-key", "gpt-4o", handler);

        var history = new List<JsonNode>
        {
            new JsonObject { ["role"] = "user", ["content"] = "Hi" }
        };
        var tools = new List<ToolDefinition>
        {
            new("read_file", "Read a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["filename"] = new JsonObject { ["type"] = "string" } } })
        };

        // Act
        await client.CreateMessageAsync(history, tools, "You are helpful", default);

        // Assert URL
        var req = handler.Request!;
        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", req.RequestUri!.AbsoluteUri);

        // Assert Authorization header present
        Assert.Contains(req.Headers, h => h.Key == "Authorization" && h.Value!.Single() == "Bearer test-key");

        // Assert body
        var bodyText = await req.Content!.ReadAsStringAsync();
        var body = JsonNode.Parse(bodyText)!.AsObject();
        Assert.Equal("gpt-4o", body["model"]!.GetValue<string>());
        Assert.Equal(8192, body["max_tokens"]!.GetValue<int>());

        var msgs = body["messages"]!.AsArray();
        Assert.Equal(2, msgs.Count);
        // System message first
        Assert.Equal("system", msgs[0]!.AsObject()["role"]!.GetValue<string>());
        Assert.Equal("You are helpful", msgs[0]!.AsObject()["content"]!.GetValue<string>());
        // User message second
        Assert.Equal("user", msgs[1]!.AsObject()["role"]!.GetValue<string>());
        Assert.Equal("Hi", msgs[1]!.AsObject()["content"]!.GetValue<string>());

        // Tools in function envelope with "parameters"
        var toolArr = body["tools"]!.AsArray();
        Assert.Single(toolArr);
        var toolObj = toolArr[0]!.AsObject();
        Assert.Equal("function", toolObj["type"]!.GetValue<string>());
        var func = toolObj["function"]!.AsObject();
        Assert.Equal("read_file", func["name"]!.GetValue<string>());
        Assert.NotNull(func["parameters"]);

        // tool_choice
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
    }

    // --- Test 2: No Authorization header when apiKey is null ---
    [Fact]
    public async Task WireFormat_NoAuthHeaderWhenApiKeyNull()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            ResponseBody = MakeTextResponse("hello")
        };
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);

        var history = new List<JsonNode>
        {
            new JsonObject { ["role"] = "user", ["content"] = "Hi" }
        };

        // Act
        await client.CreateMessageAsync(history, Array.Empty<ToolDefinition>(), null, default);

        // Assert no Authorization header
        Assert.DoesNotContain(handler.Request!.Headers, h => h.Key == "Authorization");
    }

    // --- Test 3: Tools omitted when empty ---
    [Fact]
    public async Task WireFormat_ToolsOmittedWhenEmpty()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            ResponseBody = MakeTextResponse("hello")
        };
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);

        var history = new List<JsonNode>
        {
            new JsonObject { ["role"] = "user", ["content"] = "Hi" }
        };

        // Act
        await client.CreateMessageAsync(history, Array.Empty<ToolDefinition>(), null, default);

        // Assert
        var bodyText = await handler.Request!.Content!.ReadAsStringAsync();
        var body = JsonNode.Parse(bodyText)!.AsObject();
        Assert.Null(body["tools"]);
        Assert.Null(body["tool_choice"]);
    }

    // --- Test 4: Text turn via AgenticLoop with OpenAiCompatClient ---
    [Fact]
    public async Task AgenticLoop_TextTurnWithOpenAi()
    {
        // Arrange
        var responseJson = MakeTextResponse("Hello from OpenAI!", "stop");
        var handler = new StubHttpHandler(responseJson);
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("openai", "gpt-4o", "/work");

        var executor = new FakeToolExecutor();
        var loop = new AgenticLoop(client, executor, store, convId, null);

        var assistantTexts = new List<string>();

        // Act
        await loop.RunTurnAsync("Say hello", t => assistantTexts.Add(t), (_, _) => { }, default);

        // Assert
        Assert.Single(assistantTexts);
        Assert.Equal("Hello from OpenAI!", assistantTexts[0]);

        var messages = await store.GetMessagesAsync(convId);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
    }

    // --- Test 5: Tool round-trip via AgenticLoop with OpenAiCompatClient ---
    [Fact]
    public async Task AgenticLoop_ToolRoundTripWithOpenAi()
    {
        // Arrange
        var toolCallResponse = MakeToolCallsResponse("call_1", "read_file", "{\"filename\":\"x\"}", "tool_calls");
        var finalResponse = MakeTextResponse("Done reading!", "stop");
        var handler = new StubHttpHandler(toolCallResponse, finalResponse);
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("openai", "gpt-4o", "/work");

        var executor = new FakeToolExecutor
        {
            ToolResults = { ["read_file"] = "FILE CONTENT" }
        };
        var loop = new AgenticLoop(client, executor, store, convId, null);

        var assistantTexts = new List<string>();
        var toolCalls = new List<(string, string)>();

        // Act
        await loop.RunTurnAsync("Read file x", t => assistantTexts.Add(t), (n, a) => toolCalls.Add((n, a)), default);

        // Assert executor was called with correct args
        Assert.Single(executor.Calls);
        Assert.Equal("read_file", executor.Calls[0].Name);
        Assert.Equal("x", executor.Calls[0].Args["filename"]);

        // Assert tool callback fired
        Assert.Single(toolCalls);
        Assert.Equal("read_file", toolCalls[0].Item1);

        // Assert store has 5 messages: user, assistant (tool_call), tool result, assistant (final)
        // Note: OpenAI returns 1 tool message per result, so we get user, assistant, tool, assistant = 4
        var messages = await store.GetMessagesAsync(convId);
        Assert.Equal(4, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("user", messages[2].Role);
        Assert.Equal("assistant", messages[3].Role);

        // Verify the second request contains the tool result message
        var lastRequest = handler.LastRequest!;
        var lastBody = await lastRequest.Content!.ReadAsStringAsync();
        var lastBodyObj = JsonNode.Parse(lastBody)!.AsObject();
        var lastMessages = lastBodyObj["messages"]!.AsArray();

        // Find the tool message in the request
        bool foundToolMsg = false;
        foreach (JsonNode? msg in lastMessages)
        {
            if (msg?.AsObject()["role"]?.GetValue<string>() == "tool")
            {
                foundToolMsg = true;
                var toolMsg = msg.AsObject();
                Assert.Equal("call_1", toolMsg["tool_call_id"]!.GetValue<string>());
                Assert.Equal("FILE CONTENT", toolMsg["content"]!.GetValue<string>());
                break;
            }
        }
        Assert.True(foundToolMsg, "Expected a tool role message in the second request");
    }

    // --- Test 6: Arguments-string edge — empty or whitespace args ---
    [Fact]
    public async Task AgenticLoop_EmptyArgsDoesNotThrow()
    {
        // Arrange — arguments is empty string
        var toolCallResponse = MakeToolCallsResponse("call_1", "read_file", "", "tool_calls");
        var finalResponse = MakeTextResponse("Done", "stop");
        var handler = new StubHttpHandler(toolCallResponse, finalResponse);
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("openai", "gpt-4o", "/work");

        var executor = new FakeToolExecutor();
        var loop = new AgenticLoop(client, executor, store, convId, null);

        // Act — should not throw
        await loop.RunTurnAsync("Do something", _ => { }, (_, _) => { }, default);

        // Assert: tool was invoked with empty args dict
        Assert.Single(executor.Calls);
        Assert.Equal("read_file", executor.Calls[0].Name);
        Assert.Empty(executor.Calls[0].Args);
    }

    // --- Test 7: Whitespace arguments ---
    [Fact]
    public async Task AgenticLoop_WhitespaceArgsDoesNotThrow()
    {
        // Arrange — arguments is whitespace
        var toolCallResponse = MakeToolCallsResponse("call_1", "read_file", "   ", "tool_calls");
        var finalResponse = MakeTextResponse("Done", "stop");
        var handler = new StubHttpHandler(toolCallResponse, finalResponse);
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("openai", "gpt-4o", "/work");

        var executor = new FakeToolExecutor();
        var loop = new AgenticLoop(client, executor, store, convId, null);

        // Act — should not throw
        await loop.RunTurnAsync("Do something", _ => { }, (_, _) => { }, default);

        // Assert: tool was invoked with empty args dict
        Assert.Single(executor.Calls);
        Assert.Empty(executor.Calls[0].Args);
    }

    // --- Test 8: Non-2xx throws OpenAiCompatApiException ---
    [Fact]
    public async Task Non2xx_ThrowsWithStatusCodeAndBody()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "{\"error\":{\"message\":\"Invalid model\"}}"
        };
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", "key", "gpt-4o", handler);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<OpenAiCompatApiException>(() =>
            client.CreateMessageAsync(Array.Empty<JsonNode>(), Array.Empty<ToolDefinition>(), null, default));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Invalid model", ex.ResponseBody);
    }

    // --- Test 9: ProviderName and Model properties ---
    [Fact]
    public void ProviderProperties_AreCorrect()
    {
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o-turbo");
        Assert.Equal("openai", client.ProviderName);
        Assert.Equal("gpt-4o-turbo", client.Model);
    }

    // --- Test 11: Malformed JSON arguments produce ParseError, no throw ---
    [Fact]
    public async Task MalformedJsonArgs_ProducesParseError()
    {
        // Arrange — arguments with leading-zero number (invalid JSON per System.Text.Json)
        var toolCallResponse = MakeToolCallsResponse("call_1", "read_file", "{\"end_line\":07}", "tool_calls");
        var finalResponse = MakeTextResponse("Done", "stop");
        var handler = new StubHttpHandler(toolCallResponse, finalResponse);
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o", handler);
        var store = await ConversationStore.OpenAsync(_dbFile);
        var convId = await store.CreateConversationAsync("openai", "gpt-4o", "/work");

        var executor = new FakeToolExecutor();
        var loop = new AgenticLoop(client, executor, store, convId, null);

        // Act — should not throw
        await loop.RunTurnAsync("Do something", _ => { }, (_, _) => { }, default);

        // Assert: tool was NOT invoked (executor not called), loop continued to final response
        Assert.Empty(executor.Calls);
    }

    // --- Test 12: BuildToolResultsMessages returns one message per result ---
    [Fact]
    public void BuildToolResultsMessages_ReturnsOnePerResult()
    {
        var client = new OpenAiCompatClient("http://127.0.0.1:8080/v1", null, "gpt-4o");
        var results = new List<(ParsedToolCall Call, string Result)>
        {
            (new ParsedToolCall("call_1", "read_file", new JsonObject { ["filename"] = "x" }), "content of x"),
            (new ParsedToolCall("call_2", "grep_file", new JsonObject { ["pattern"] = "foo" }), "match found")
        };

        var msgs = client.BuildToolResultsMessages(results);

        Assert.Equal(2, msgs.Count);
        var msg0 = msgs[0].AsObject();
        Assert.Equal("tool", msg0["role"]!.GetValue<string>());
        Assert.Equal("call_1", msg0["tool_call_id"]!.GetValue<string>());
        Assert.Equal("content of x", msg0["content"]!.GetValue<string>());

        var msg1 = msgs[1].AsObject();
        Assert.Equal("tool", msg1["role"]!.GetValue<string>());
        Assert.Equal("call_2", msg1["tool_call_id"]!.GetValue<string>());
        Assert.Equal("match found", msg1["content"]!.GetValue<string>());
    }
}
