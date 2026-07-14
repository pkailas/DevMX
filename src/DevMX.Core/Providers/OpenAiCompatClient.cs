using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DevMX.Core.Providers;

/// <summary>
/// OpenAI-compatible chat provider (works with any OpenAI-compatible API endpoint).
/// Implements IChatProvider for provider-agnostic usage.
/// </summary>
public sealed class OpenAiCompatClient : IChatProvider
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;

    public string ProviderName => "openai";
    public string Model => _model;

    /// <summary>
    /// Creates a client for an OpenAI-compatible endpoint.
    /// </summary>
    /// <param name="baseUrl">Base URL e.g. "http://127.0.0.1:8080/v1" — POST to baseUrl + "/chat/completions".</param>
    /// <param name="apiKey">API key (optional; Authorization header omitted when null/empty).</param>
    /// <param name="model">Model identifier e.g. "gpt-4o".</param>
    /// <param name="handler">Optional HTTP handler for testing.</param>
    public OpenAiCompatClient(string baseUrl, string? apiKey, string model, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _model = model;
        _http = handler is not null ? new HttpClient(handler) : new HttpClient();
        // Reasoning models can think for minutes; but a request must never hang forever (stuck IsBusy).
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Sends a chat completion request to the OpenAI-compatible API.
    /// </summary>
    public async Task<ProviderResponse> CreateMessageAsync(
        IReadOnlyList<JsonNode> history,
        IReadOnlyList<ToolDefinition> tools,
        string? system,
        CancellationToken ct = default)
    {
        // First attempt sends any image parts as-is (vision-capable endpoints accept them).
        var response = await PostChatAsync(BuildRequestMessages(history, system, scrubImages: false), tools, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        // Text-only endpoints reject the whole request over one image part -
        // scrub images to text placeholders and retry once.
        if (!response.IsSuccessStatusCode && responseText.Contains("image_url") && HasImageParts(history))
        {
            response = await PostChatAsync(BuildRequestMessages(history, system, scrubImages: true), tools, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OpenAiCompatApiException(
                $"OpenAI-compatible API request failed with status {response.StatusCode}: {responseText}",
                response.StatusCode,
                responseText);
        }
        var responseObject = JsonNode.Parse(responseText)
            ?? throw new InvalidOperationException("Empty response from OpenAI-compatible API");
        var responseObj = responseObject.AsObject();

        var choices = responseObj["choices"]?.AsArray()
            ?? throw new InvalidOperationException("Response missing 'choices' field");

        if (choices.Count == 0)
            throw new InvalidOperationException("Response 'choices' array is empty");

        var choice = choices[0]?.AsObject()
            ?? throw new InvalidOperationException("Response choice is not an object");

        var message = choice["message"]?.AsObject()
            ?? throw new InvalidOperationException("Response choice missing 'message'");

        var finishReason = choice["finish_reason"]?.GetValue<string>() ?? "stop";

        // Build the assistant message verbatim (what goes over the wire) - minus reasoning_content:
        // reasoning models (DeepSeek) return it, but reject requests that echo it back in history.
        var assistantMessage = CloneNode(message);
        assistantMessage.AsObject().Remove("reasoning_content");

        // Parse text blocks.
        var textBlocks = new List<string>();
        var msgContent = message["content"];
        if (msgContent is JsonValue cv && cv.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            textBlocks.Add(text);
        }

        // Parse tool calls.
        var toolCalls = new List<ParsedToolCall>();
        var toolCallsNode = message["tool_calls"]?.AsArray();
        if (toolCallsNode is not null)
        {
            foreach (var tc in toolCallsNode)
            {
                if (tc?.AsObject() is not { } tcObj) continue;
                var tcId = tcObj["id"]?.GetValue<string>() ?? "";
                var func = tcObj["function"]?.AsObject();
                var tcName = func?["name"]?.GetValue<string>() ?? "";
                var argsStr = func?["arguments"]?.GetValue<string>() ?? "";

                // Parse arguments string into JsonObject; empty/whitespace → empty object.
                // Guard against malformed JSON (e.g. leading-zero numbers) so the turn is not killed.
                JsonObject input;
                string? parseError = null;
                if (string.IsNullOrWhiteSpace(argsStr))
                {
                    input = new JsonObject();
                }
                else
                {
                    try
                    {
                        input = (JsonNode.Parse(argsStr) as JsonObject) ?? new JsonObject();
                    }
                    catch (Exception parseEx)
                    {
                        input = new JsonObject();
                        var rawPreview = argsStr.Length > 200 ? argsStr.Substring(0, 200) : argsStr;
                        parseError = $"{parseEx.Message} | raw: {rawPreview}";
                    }
                }

                toolCalls.Add(new ParsedToolCall(tcId, tcName, input, parseError));
            }
        }

        return new ProviderResponse(assistantMessage, finishReason, toolCalls, textBlocks);
    }

    /// <summary>Build a user message JsonNode from plain text (OpenAI format).</summary>
    public JsonNode BuildUserMessage(string text)
    {
        return new JsonObject { ["role"] = "user", ["content"] = text };
    }

    /// <summary>
    /// Build a user message with attachments (OpenAI multimodal content parts).
    /// Images are sent as image_url parts; text-only endpoints that reject them are
    /// handled by the scrub-and-retry fallback in CreateMessageAsync.
    /// </summary>
    public JsonNode BuildUserMessage(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        var parts = new JsonArray();
        foreach (var att in attachments)
        {
            if (att.IsImage && att.Base64Data is not null)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{att.MediaType};base64,{att.Base64Data}"
                    }
                });
            }
            else if (att.TextContent is not null)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Attached file: {att.FileName}\n```\n{att.TextContent}\n```"
                });
            }
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    /// <summary>
    /// Build tool-result messages (OpenAI: one tool message per result).
    /// </summary>
    public IReadOnlyList<JsonNode> BuildToolResultsMessages(
        IReadOnlyList<(ParsedToolCall Call, string Result)> results)
    {
        var msgs = new List<JsonNode>(results.Count);
        foreach (var (call, result) in results)
        {
            msgs.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["content"] = result
            });
        }
        return msgs;
    }

    private static JsonArray SerializeToolsOpenAi(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var tool in tools)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = CloneNode(tool.InputSchema)
                }
            });
        }
        return arr;
    }

    /// <summary>Build the request messages array: system first (if present), then cloned history.</summary>
    private static JsonArray BuildRequestMessages(IReadOnlyList<JsonNode> history, string? system, bool scrubImages)
    {
        var messages = new JsonArray();
        if (system is not null)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }
        foreach (var msg in history)
        {
            var cloned = CloneNode(msg);
            if (scrubImages)
                ReplaceImagePartsWithText(cloned);
            messages.Add(cloned);
        }
        return messages;
    }

    /// <summary>POST a chat/completions request with the given messages.</summary>
    private async Task<HttpResponseMessage> PostChatAsync(
        JsonArray messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            // 8192: tool calls carry whole delegation briefs in their arguments, and
            // reasoning models spend budget before emitting them - 4096 got responses
            // cut off mid-plan (finish_reason=length, announced action but no call).
            ["max_tokens"] = 8192,
            ["messages"] = messages
        };

        if (tools.Count > 0)
        {
            body["tools"] = SerializeToolsOpenAi(tools);
            body["tool_choice"] = "auto";
        }

        var content = new StringContent(
            body.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = content
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add("Authorization", "Bearer " + _apiKey);
        }

        return await _http.SendAsync(request, ct);
    }

    /// <summary>True if any history message carries an image content part.</summary>
    private static bool HasImageParts(IReadOnlyList<JsonNode> history)
    {
        foreach (var msg in history)
        {
            if (msg is not JsonObject obj || obj["content"] is not JsonArray parts)
                continue;
            foreach (var part in parts)
            {
                var type = (part as JsonObject)?["type"]?.GetValue<string>();
                if (type == "image_url" || type == "image")
                    return true;
            }
        }
        return false;
    }

    /// <summary>Replace image_url / image content parts in a message with text placeholders.</summary>
    private static void ReplaceImagePartsWithText(JsonNode msg)
    {
        if (msg is not JsonObject obj || obj["content"] is not JsonArray parts)
            return;

        for (int i = 0; i < parts.Count; i++)
        {
            var type = (parts[i] as JsonObject)?["type"]?.GetValue<string>();
            if (type == "image_url" || type == "image")
            {
                parts[i] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "[image attachment omitted — this endpoint rejected image input; a copy is staged on disk, see the staged path list in this message]"
                };
            }
        }
    }

    private static JsonNode CloneNode(JsonNode node)
    {
        return JsonNode.Parse(node.ToJsonString()) ?? node;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

/// <summary>
/// Exception thrown when the OpenAI-compatible API returns a non-success status code.
/// </summary>
public sealed class OpenAiCompatApiException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public OpenAiCompatApiException(string message, System.Net.HttpStatusCode statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
