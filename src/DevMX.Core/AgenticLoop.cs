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
/// </summary>
public sealed class AgenticLoop
{
    private readonly AnthropicClient _llm;
    private readonly IMcpToolExecutor _tools;
    private readonly ConversationStore _store;
    private readonly long _conversationId;
    private readonly string? _systemPrompt;
    private readonly int _maxIterations;
    private readonly List<ChatMessage> _history;

    /// <summary>
    /// Creates a new AgenticLoop starting with an empty conversation history.
    /// </summary>
    public AgenticLoop(
        AnthropicClient llm,
        IMcpToolExecutor tools,
        ConversationStore store,
        long conversationId,
        string? systemPrompt = null,
        int maxIterations = 50)
        : this(llm, tools, store, conversationId, systemPrompt, maxIterations, new List<ChatMessage>())
    {
    }

    /// <summary>
    /// Creates a new AgenticLoop with preloaded history (e.g. from a reopened conversation).
    /// </summary>
    public AgenticLoop(
        AnthropicClient llm,
        IMcpToolExecutor tools,
        ConversationStore store,
        long conversationId,
        string? systemPrompt,
        int maxIterations,
        List<ChatMessage> history)
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
    /// Rebuilds a ChatMessage list from the conversation store for a given conversation.
    /// </summary>
    public static async Task<List<ChatMessage>> LoadHistoryAsync(ConversationStore store, long conversationId)
    {
        var messages = await store.GetMessagesAsync(conversationId);
        var history = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            var content = JsonNode.Parse(msg.ContentJson) as JsonArray
                ?? throw new InvalidOperationException($"Invalid content_json in message {msg.Id}: {msg.ContentJson}");
            history.Add(new ChatMessage(msg.Role, content));
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
        var userContent = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = userText }
        };
        var userMsg = new ChatMessage("user", userContent);
        _history.Add(userMsg);
        await _store.AppendMessageAsync(_conversationId, "user", userContent.ToJsonString());

        // Fetch tool definitions once per turn.
        var toolDefs = await _tools.ListToolDefinitionsAsync(ct);

        // 2. Main loop.
        for (int i = 0; i < _maxIterations; i++)
        {
            // Call LLM.
            var response = await _llm.CreateMessageAsync(_history, toolDefs, _systemPrompt, ct);

            // Append assistant response verbatim to history + store.
            _history.Add(new ChatMessage("assistant", response.Content));
            await _store.AppendMessageAsync(_conversationId, "assistant", response.Content.ToJsonString());

            // Invoke callbacks for text blocks.
            foreach (var block in response.Content)
            {
                var blockObj = block?.AsObject();
                if (blockObj?["type"]?.GetValue<string>() == "text")
                {
                    var text = blockObj["text"]?.GetValue<string>() ?? "";
                    onAssistantText(text);
                }
            }

            // 3. Check stop reason.
            if (response.StopReason != "tool_use")
            {
                return;
            }

            // 4. Handle tool_use — collect all tool_use blocks, call tools, build tool_result blocks.
            var toolResults = new JsonArray();
            foreach (var block in response.Content)
            {
                var blockObj = block?.AsObject();
                if (blockObj?["type"]?.GetValue<string>() != "tool_use")
                    continue;

                var toolUseId = blockObj["id"]?.GetValue<string>() ?? "";
                var toolName = blockObj["name"]?.GetValue<string>() ?? "";
                var toolInput = blockObj["input"];
                var toolInputJson = toolInput?.ToJsonString() ?? "{}";

                // Convert input to dictionary for the executor.
                var args = ConvertToJsonDictionary(toolInput);

                // Call the tool.
                var result = await _tools.CallToolAsync(toolName, args, ct);

                // Fire callback.
                onToolCall(toolName, toolInputJson);

                // Delegation capture (best-effort).
                try
                {
                    CaptureDelegation(toolName, args, result);
                }
                catch
                {
                    // Swallow parse failures.
                }

                // Build tool_result block.
                var resultBlock = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = result }
                    }
                };
                toolResults.Add(resultBlock);
            }

            // Build a single user message with all tool results.
            var toolResultMsg = new ChatMessage("user", toolResults);
            _history.Add(toolResultMsg);
            await _store.AppendMessageAsync(_conversationId, "user", toolResults.ToJsonString());
        }
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
