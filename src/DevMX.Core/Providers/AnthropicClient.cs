using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DevMX.Core.Providers;

/// <summary>
/// Chat message with content stored as a raw JSON array of content blocks.
/// Kept for backward compatibility with DevMX.Chat and existing tests.
/// </summary>
public sealed record ChatMessage(string Role, JsonArray Content);

/// <summary>
/// Response from the Anthropic Messages API.
/// Kept for backward compatibility.
/// </summary>
public sealed record AnthropicResponse(
    JsonArray Content,
    string StopReason,
    int InputTokens,
    int OutputTokens);

/// <summary>
/// Non-streaming client for the Anthropic Messages API.
/// Implements IChatProvider for provider-agnostic usage.
/// </summary>
public sealed class AnthropicClient : IChatProvider
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;

    public string ProviderName => "anthropic";
    public string Model => _model;

    /// <summary>
    /// Creates a client using the specified API key and model.
    /// </summary>
    /// <param name="apiKey">Anthropic API key.</param>
    /// <param name="model">Model identifier (e.g. "claude-sonnet-4-20250514").</param>
    public AnthropicClient(string apiKey, string model)
        : this(apiKey, model, null)
    {
    }

    /// <summary>
    /// Creates a client with an injectable HTTP handler for testing.
    /// </summary>
    public AnthropicClient(string apiKey, string model, HttpMessageHandler? handler)
    {
        _apiKey = apiKey;
        _model = model;
        _http = handler is not null ? new HttpClient(handler) : new HttpClient();
        // Reasoning models can think for minutes; but a request must never hang forever (stuck IsBusy).
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Sends a message to the Anthropic API and returns the response.
    /// Backward-compatible API accepting ChatMessage list.
    /// </summary>
    public async Task<AnthropicResponse> CreateMessageAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? system,
        CancellationToken ct = default)
    {
        // Convert ChatMessage list to JsonNode list for the shared implementation.
        var history = new List<JsonNode>(messages.Count);
        foreach (var msg in messages)
        {
            history.Add(BuildChatMessageNode(msg.Role, msg.Content));
        }
        var (contentArray, stopReason, inputTokens, outputTokens) = await CreateMessageAsyncImpl(history, tools, system, ct);
        return new AnthropicResponse(contentArray, stopReason, inputTokens, outputTokens);
    }

    /// <summary>
    /// IChatProvider implementation: sends a message using opaque JsonNode history.
    /// </summary>
    public async Task<ProviderResponse> CreateMessageAsync(
        IReadOnlyList<JsonNode> history,
        IReadOnlyList<ToolDefinition> tools,
        string? system,
        CancellationToken ct = default)
    {
        var (contentArray, stopReason, _, _) = await CreateMessageAsyncImpl(history, tools, system, ct);

        // Parse tool calls and text blocks from the content array.
        var toolCalls = new List<ParsedToolCall>();
        var textBlocks = new List<string>();
        foreach (var block in contentArray)
        {
            if (block?.AsObject() is not { } blockObj) continue;
            var type = blockObj["type"]?.GetValue<string>();
            if (type == "tool_use")
            {
                toolCalls.Add(new ParsedToolCall(
                    blockObj["id"]!.GetValue<string>(),
                    blockObj["name"]!.GetValue<string>(),
                    blockObj["input"]!.AsObject()));
            }
            else if (type == "text")
            {
                textBlocks.Add(blockObj["text"]!.GetValue<string>());
            }
        }

        return new ProviderResponse(contentArray, stopReason, toolCalls, textBlocks);
    }

    private async Task<(JsonArray Content, string StopReason, int InputTokens, int OutputTokens)> CreateMessageAsyncImpl(
        IReadOnlyList<JsonNode> history,
        IReadOnlyList<ToolDefinition> tools,
        string? system,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 4096,
            ["messages"] = SerializeJsonNodeMessages(history)
        };

        if (system is not null)
        {
            body["system"] = system;
        }

        if (tools.Count > 0)
        {
            body["tools"] = SerializeTools(tools);
        }

        var content = new StringContent(
            body.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = content
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var bodyText = await response.Content.ReadAsStringAsync(ct);
            throw new AnthropicApiException(
                $"Anthropic API request failed with status {response.StatusCode}: {bodyText}",
                response.StatusCode,
                bodyText);
        }

        var responseText = await response.Content.ReadAsStringAsync(ct);
        var responseObject = JsonNode.Parse(responseText) ?? throw new InvalidOperationException("Empty response from Anthropic API");
        var responseObj = responseObject.AsObject();

        var contentArray = responseObj["content"]?.AsArray()
            ?? throw new InvalidOperationException("Response missing 'content' field");

        var stopReason = responseObj["stop_reason"]?.GetValue<string>() ?? "end_turn";

        var usage = responseObj["usage"]?.AsObject() ?? new JsonObject();
        var inputTokens = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = usage["output_tokens"]?.GetValue<int>() ?? 0;

        return (contentArray, stopReason, inputTokens, outputTokens);
    }

    /// <summary>Build a user message JsonNode from plain text (Anthropic format).</summary>
    public JsonNode BuildUserMessage(string text)
    {
        return BuildChatMessageNode("user", new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text }
        });
    }

    /// <summary>Build a user message with attachments (Anthropic multimodal content blocks).</summary>
    public JsonNode BuildUserMessage(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        var blocks = new JsonArray();
        foreach (var att in attachments)
        {
            if (att.IsImage && att.Base64Data is not null)
            {
                blocks.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = att.MediaType,
                        ["data"] = att.Base64Data
                    }
                });
            }
            else if (att.TextContent is not null)
            {
                blocks.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Attached file: {att.FileName}\n```\n{att.TextContent}\n```"
                });
            }
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        return BuildChatMessageNode("user", blocks);
    }

    /// <summary>
    /// Build tool-result messages (Anthropic: one user message with all tool_result blocks).
    /// </summary>
    public IReadOnlyList<JsonNode> BuildToolResultsMessages(
        IReadOnlyList<(ParsedToolCall Call, string Result)> results)
    {
        var contentBlocks = new JsonArray();
        foreach (var (call, result) in results)
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = call.Id,
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = result }
                }
            });
        }
        return new[] { BuildChatMessageNode("user", contentBlocks) };
    }

    private static JsonNode BuildChatMessageNode(string role, JsonArray content)
    {
        return new JsonObject
        {
            ["role"] = role,
            ["content"] = CloneJsonArray(content)
        };
    }

    private static JsonArray SerializeJsonNodeMessages(IReadOnlyList<JsonNode> messages)
    {
        var arr = new JsonArray();
        foreach (var msg in messages)
        {
            arr.Add(CloneJsonNode(msg));
        }
        return arr;
    }

    private static JsonArray CloneJsonArray(JsonArray source)
    {
        var cloned = new JsonArray();
        foreach (var node in source)
        {
            cloned.Add(CloneJsonNode(node));
        }
        return cloned;
    }

    private static JsonNode? CloneJsonNode(JsonNode? node)
    {
        if (node is null) return null;
        return JsonNode.Parse(node.ToJsonString());
    }

    private static JsonArray SerializeTools(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var tool in tools)
        {
            var obj = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                // Clone to avoid "node already has a parent" on repeated calls.
                ["input_schema"] = CloneJsonNode(tool.InputSchema)
            };
            arr.Add(obj);
        }
        return arr;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

/// <summary>
/// Exception thrown when the Anthropic API returns a non-success status code.
/// </summary>
public sealed class AnthropicApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public AnthropicApiException(string message, HttpStatusCode statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
