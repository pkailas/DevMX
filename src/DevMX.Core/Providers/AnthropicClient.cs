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
/// </summary>
public sealed record ChatMessage(string Role, JsonArray Content);

/// <summary>
/// Response from the Anthropic Messages API.
/// </summary>
public sealed record AnthropicResponse(
    JsonArray Content,
    string StopReason,
    int InputTokens,
    int OutputTokens);

/// <summary>
/// Non-streaming client for the Anthropic Messages API.
/// </summary>
public sealed class AnthropicClient
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;

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
    }

    /// <summary>
    /// Sends a message to the Anthropic API and returns the response.
    /// </summary>
    public async Task<AnthropicResponse> CreateMessageAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? system,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 4096,
            ["messages"] = SerializeMessages(messages)
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

        return new AnthropicResponse(contentArray, stopReason, inputTokens, outputTokens);
    }

    private static JsonArray SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var msg in messages)
        {
            // Clone the content array to avoid "node already has a parent" errors
            // when the same ChatMessage is sent in multiple API requests.
            var clonedContent = CloneJsonArray(msg.Content);
            var obj = new JsonObject
            {
                ["role"] = msg.Role,
                ["content"] = clonedContent
            };
            arr.Add(obj);
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
