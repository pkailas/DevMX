using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DevMX.Core.Persistence;
using DevMX.Core.Providers;

namespace DevMX.Core;

/// <summary>
/// Drives an agentic loop: user prompt → LLM → tool calls → LLM → ... → final answer.
/// Provider-agnostic: depends on IChatProvider, history is List[JsonNode].
/// </summary>
public sealed class AgenticLoop
{
    private readonly IChatProvider _llm;
    private readonly IMcpToolExecutor _tools;
    private readonly ConversationStore _store;
    private readonly long _conversationId;
    private readonly string? _systemPrompt;
    private readonly int _maxIterations;
    private readonly List<JsonNode> _history;

    /// <summary>
    /// Creates a new AgenticLoop starting with an empty conversation history.
    /// </summary>
    public AgenticLoop(
        IChatProvider llm,
        IMcpToolExecutor tools,
        ConversationStore store,
        long conversationId,
        string? systemPrompt = null,
        int maxIterations = 50)
        : this(llm, tools, store, conversationId, systemPrompt, maxIterations, new List<JsonNode>())
    {
    }

    /// <summary>
    /// Creates a new AgenticLoop with preloaded history (e.g. from a reopened conversation).
    /// </summary>
    public AgenticLoop(
        IChatProvider llm,
        IMcpToolExecutor tools,
        ConversationStore store,
        long conversationId,
        string? systemPrompt,
        int maxIterations,
        List<JsonNode> history)
    {
        _llm = llm;
        _tools = tools;
        _store = store;
        _conversationId = conversationId;
        _systemPrompt = systemPrompt;
        _maxIterations = maxIterations;
        _history = history;
    }

    /// <summary>
    /// Rebuilds a JsonNode history list from the conversation store for a given conversation.
    /// Each message is wrapped as { "role": ..., "content": ... } for provider-agnostic use.
    /// </summary>
    public static async Task<List<JsonNode>> LoadHistoryAsync(ConversationStore store, long conversationId)
    {
        var messages = await store.GetMessagesAsync(conversationId);
        var history = new List<JsonNode>(messages.Count);
        foreach (var msg in messages)
        {
            var content = JsonNode.Parse(msg.ContentJson) as JsonArray
                ?? throw new InvalidOperationException($"Invalid content_json in message {msg.Id}: {msg.ContentJson}");
            history.Add(new JsonObject
            {
                ["role"] = msg.Role,
                ["content"] = content
            });
        }
        return history;
    }

    /// <summary>
    /// Runs a single turn: user sends text, LLM responds (potentially with tool calls), loop until final answer.
    /// </summary>
    public async Task RunTurnAsync(
        string userText,
        Action<string> onAssistantText,
        Action<string, string> onToolCall,
        CancellationToken ct = default)
    {
        // 1. Build user message and append to history + store.
        var userMsg = _llm.BuildUserMessage(userText);
        _history.Add(userMsg);

        // Persist: extract content from the provider's user message format.
        var userContentJson = ExtractContentJson(userMsg);
        await _store.AppendMessageAsync(_conversationId, "user", userContentJson);

        // Fetch tool definitions once per turn.
        var toolDefs = await _tools.ListToolDefinitionsAsync(ct);

        // 2. Main loop.
        for (int i = 0; i < _maxIterations; i++)
        {
            // Call LLM.
            var response = await _llm.CreateMessageAsync(_history, toolDefs, _systemPrompt, ct);

            // Append assistant response verbatim to history + store.
            _history.Add(response.AssistantMessage);
            await _store.AppendMessageAsync(_conversationId, "assistant", response.AssistantMessage.ToJsonString());

            // Invoke callbacks for text blocks.
            foreach (var text in response.TextBlocks)
            {
                onAssistantText(text);
            }

            // If no tool calls, we are done.
            if (response.ToolCalls.Count == 0)
            {
                break;
            }

            // Execute tools and build tool_result messages.
            var callResults = new List<(ParsedToolCall Call, string Result)>();
            foreach (var call in response.ToolCalls)
            {
                var args = ConvertToJsonDictionary(call.Input);
                var result = await _tools.CallToolAsync(call.Name, args, ct);
                onToolCall(call.Name, call.Input.ToJsonString());
                callResults.Add((call, result));

                // Capture delegation lifecycle.
                CaptureDelegation(call.Name, args, result);
            }

            // Build tool result message(s) via the provider.
            var toolResultMessages = _llm.BuildToolResultsMessages(callResults);
            foreach (var toolResultMsg in toolResultMessages)
            {
                _history.Add(toolResultMsg);
                var contentJson = ExtractContentJson(toolResultMsg);
                await _store.AppendMessageAsync(_conversationId, "user", contentJson);
            }
        }
    }

    /// <summary>Extract the content JSON from a provider message node for persistence.</summary>
    private static string ExtractContentJson(JsonNode msgNode)
    {
        var obj = msgNode.AsObject();
        if (obj["content"] is JsonArray contentArray)
        {
            // Anthropic-style: { "role": ..., "content": [...] }
            return contentArray.ToJsonString();
        }
        if (obj["content"] is JsonValue contentValue)
        {
            // OpenAI-style string content: { "role": ..., "content": "text" }
            return $"[{new JsonObject { ["type"] = "text", ["text"] = contentValue.GetValue<string>() }.ToJsonString()}]";
        }
        if (obj["content"] is null)
        {
            // OpenAI-style with tool_calls but no text content
            return "[]";
        }
        // Fallback: serialize the whole content node
        return obj["content"]!.ToJsonString();
    }

    private void CaptureDelegation(string toolName, IReadOnlyDictionary<string, object?> args, string result)
    {
        try
        {
            if (toolName == "devmind_task_start")
            {
                // Parse job_id from result JSON.
                var resultObj = JsonNode.Parse(result)?.AsObject();
                if (resultObj?["job_id"]?.GetValue<string>() is string jobId)
                {
                    var brief = args.TryGetValue("prompt", out var p) ? (p?.ToString() ?? "") : "";
                    _store.RecordDelegationAsync(_conversationId, jobId, brief).GetAwaiter().GetResult();
                }
            }
            else if (toolName == "devmind_task_status" || toolName == "devmind_task_result")
            {
                var resultObj = JsonNode.Parse(result)?.AsObject();
                if (resultObj != null)
                {
                    var state = resultObj["state"]?.GetValue<string>();
                    if (state is "done" or "failed" or "cancelled")
                    {
                        var jobId = resultObj["job_id"]?.GetValue<string>() ?? "";
                        var journalJson = (toolName == "devmind_task_result") ? result : null;
                        _store.CompleteDelegationAsync(jobId, state, journalJson).GetAwaiter().GetResult();
                    }
                }
            }
        }
        catch
        {
            // Best-effort — swallow all errors.
        }
    }

    private static Dictionary<string, object?> ConvertToJsonDictionary(JsonNode? node)
    {
        var dict = new Dictionary<string, object?>();
        if (node?.AsObject() is { } obj)
        {
            foreach (var kvp in obj)
            {
                dict[kvp.Key] = ConvertJsonNodeToObject(kvp.Value);
            }
        }
        return dict;
    }

    private static object? ConvertJsonNodeToObject(JsonNode? node)
    {
        return node switch
        {
            JsonValue v => UnwrapJsonValue(v),
            JsonObject o => ConvertToJsonDictionary(o),
            JsonArray a => ConvertJsonArrayToObject(a),
            _ => null
        };
    }

    private static object? UnwrapJsonValue(JsonValue v)
    {
        // JsonValue stores the underlying CLR type. Unwrap it properly.
        return v.TryGetValue<string>(out var s) ? s as object
            : v.TryGetValue<int>(out var i) ? i as object
            : v.TryGetValue<long>(out var l) ? l as object
            : v.TryGetValue<double>(out var d) ? d as object
            : v.TryGetValue<bool>(out var b) ? b as object
            : v.ToString();
    }

    private static object?[] ConvertJsonArrayToObject(JsonArray arr)
    {
        var result = new object?[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            result[i] = ConvertJsonNodeToObject(arr[i]);
        }
        return result;
    }
}
