using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevMX.Core.Providers;
using Xunit;

namespace DevMX.Core.Tests;

public class AnthropicClientTests
{
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

    private static JsonArray MakeTextBlock(string text)
    {
        return new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
    }

    private static JsonArray MakeToolUseBlock(string id, string name, JsonObject input)
    {
        return new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input } };
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

    [Fact]
    public async Task WireFormat_AssertsUrlHeadersAndBody()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            ResponseBody = MakeResponse(MakeTextBlock("hello"), "end_turn")
        };
        var client = new AnthropicClient("test-key", "claude-sonnet-4-20250514", handler);

        var messages = new List<ChatMessage>
        {
            new("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Hi" } })
        };
        var tools = new List<ToolDefinition>
        {
            new("read_file", "Read a file", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["filename"] = new JsonObject { ["type"] = "string" } } })
        };

        // Act
        await client.CreateMessageAsync(messages, tools, "You are helpful", default);

        // Assert
        var req = handler.Request!;
        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.AbsoluteUri);
        Assert.Contains(req.Headers, h => h.Key == "x-api-key" && h.Value!.Single() == "test-key");
        Assert.Contains(req.Headers, h => h.Key == "anthropic-version" && h.Value!.Single() == "2023-06-01");

        var bodyText = await req.Content!.ReadAsStringAsync();
        var body = JsonNode.Parse(bodyText)!.AsObject();
        Assert.Equal("claude-sonnet-4-20250514", body["model"]!.GetValue<string>());
        Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("You are helpful", body["system"]!.GetValue<string>());

        var msgs = body["messages"]!.AsArray();
        Assert.Single(msgs);
        var msgObj = msgs[0]!.AsObject();
        Assert.Equal("user", msgObj["role"]!.GetValue<string>());

        var toolArr = body["tools"]!.AsArray();
        Assert.Single(toolArr);
        var toolObj = toolArr[0]!.AsObject();
        Assert.Equal("read_file", toolObj["name"]!.GetValue<string>());
        Assert.NotNull(toolObj["input_schema"]);
    }

    [Fact]
    public async Task WireFormat_SystemOmittedWhenNull()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            ResponseBody = MakeResponse(MakeTextBlock("hello"), "end_turn")
        };
        var client = new AnthropicClient("test-key", "claude-sonnet-4-20250514", handler);

        var messages = new List<ChatMessage>
        {
            new("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Hi" } })
        };

        // Act
        await client.CreateMessageAsync(messages, Array.Empty<ToolDefinition>(), null, default);

        // Assert
        var bodyText = await handler.Request!.Content!.ReadAsStringAsync();
        var body = JsonNode.Parse(bodyText)!.AsObject();
        Assert.Null(body["system"]);
        Assert.Null(body["tools"]);
    }

    [Fact]
    public async Task Non2xx_ThrowsWithStatusCodeAndBody()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "{\"error\":{\"message\":\"Invalid API key\"}}"
        };
        var client = new AnthropicClient("bad-key", "claude-sonnet-4-20250514", handler);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<AnthropicApiException>(() =>
            client.CreateMessageAsync(Array.Empty<ChatMessage>(), Array.Empty<ToolDefinition>(), null, default));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Invalid API key", ex.ResponseBody);
    }

    [Fact]
    public async Task TextOnlyTurn_ReturnsCorrectResponse()
    {
        // Arrange
        var handler = new CapturingHandler
        {
            ResponseBody = MakeResponse(MakeTextBlock("The answer is 42"), "end_turn", 20, 10)
        };
        var client = new AnthropicClient("key", "model", handler);

        // Act
        var response = await client.CreateMessageAsync(
            new List<ChatMessage> { new("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "What is the answer?" } }) },
            Array.Empty<ToolDefinition>(),
            null,
            default);

        // Assert
        Assert.Equal("end_turn", response.StopReason);
        Assert.Equal(20, response.InputTokens);
        Assert.Equal(10, response.OutputTokens);
        Assert.Single(response.Content);
        Assert.Equal("text", response.Content[0]!.AsObject()["type"]!.GetValue<string>());
        Assert.Equal("The answer is 42", response.Content[0]!.AsObject()["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolUseResponse_ReturnsToolUseBlock()
    {
        // Arrange
        var input = new JsonObject { ["filename"] = "x.cs" };
        var handler = new CapturingHandler
        {
            ResponseBody = MakeResponse(MakeToolUseBlock("toolu_01", "read_file", input), "tool_use")
        };
        var client = new AnthropicClient("key", "model", handler);

        // Act
        var response = await client.CreateMessageAsync(
            new List<ChatMessage> { new("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Read x.cs" } }) },
            Array.Empty<ToolDefinition>(),
            null,
            default);

        // Assert
        Assert.Equal("tool_use", response.StopReason);
        Assert.Single(response.Content);
        var block = response.Content[0]!.AsObject();
        Assert.Equal("tool_use", block["type"]!.GetValue<string>());
        Assert.Equal("toolu_01", block["id"]!.GetValue<string>());
        Assert.Equal("read_file", block["name"]!.GetValue<string>());
    }
}
