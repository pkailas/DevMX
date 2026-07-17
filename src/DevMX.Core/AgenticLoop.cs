using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private readonly string _toolProfile;
    private readonly int _pollThrottleSeconds;
    private readonly int _compactThresholdChars;
    private readonly Dictionary<string, DateTime> _lastPollTimeByJobId = new();

    /// <summary>Sentinel prefix marking an auto-compaction digest message; history
    /// loads skip everything before the last message carrying it.</summary>
    public const string DigestPrefix = "[[conversation digest]]";

    /// <summary>Approximate size of the in-memory history in JSON characters (~4 chars/token).</summary>
    public long HistoryChars
    {
        get
        {
            long total = 0;
            foreach (var m in _history)
                total += m.ToJsonString().Length;
            return total;
        }
    }

    /// <summary>
    /// Creates a new AgenticLoop starting with an empty conversation history.
    /// </summary>
    public AgenticLoop(
        IChatProvider llm,
        IMcpToolExecutor tools,
        ConversationStore store,
        long conversationId,
        string? systemPrompt = null,
        int maxIterations = 50,
        string toolProfile = ToolProfiles.Full,
        int pollThrottleSeconds = 5,
        int compactThresholdTokens = 0)
        : this(llm, tools, store, conversationId, systemPrompt, maxIterations, new List<JsonNode>(), toolProfile, pollThrottleSeconds, compactThresholdTokens)
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
        List<JsonNode> history,
        string toolProfile = ToolProfiles.Full,
        int pollThrottleSeconds = 5,
        int compactThresholdTokens = 0)
    {
        _llm = llm;
        _tools = tools;
        _store = store;
        _conversationId = conversationId;
        _systemPrompt = systemPrompt;
        _maxIterations = maxIterations;
        _history = history;
        _toolProfile = toolProfile;
        _pollThrottleSeconds = pollThrottleSeconds;
        _compactThresholdChars = compactThresholdTokens * 4; // ~4 chars/token
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
            // content_json has gone through several shapes over the project's life:
            //   1. a bare content array: [{type:text,...}]
            //   2. a full message object: {role, content:"string"|[...], tool_calls?:[...]}
            //   3. anything else (defensive)
            // Normalize ALL of them to {role, content:[{type:text,text}]} and never throw over one row.
            // Orphaned tool_calls are dropped: their results were persisted as text rows already, and
            // resending unbalanced tool_calls would make the API reject the whole resumed history.
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(msg.ContentJson); }
            catch { continue; }
            if (parsed == null)
                continue;

            string role = msg.Role;
            JsonArray content;

            switch (parsed)
            {
                case JsonArray arr:
                    content = arr;
                    break;
                case JsonObject obj:
                    var objRole = obj["role"]?.GetValue<string>();
                    if (objRole == "assistant") role = "assistant";
                    else if (objRole is "user" or "tool") role = "user";

                    if (obj["content"] is JsonArray contentArr)
                        content = contentArr;
                    else if (obj["content"] is JsonValue cv && cv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                        content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = s });
                    else
                        continue; // null/empty content and (dropped) tool_calls only - nothing to replay
                    break;
                default:
                    continue;
            }

            if (content.Count == 0)
                continue;

            history.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content.DeepClone()
            });
        }

        // Auto-compaction persists a digest of everything before it; on reload,
        // drop the messages the digest replaces.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (IsDigestMessage(history[i]))
            {
                if (i > 0)
                    history.RemoveRange(0, i);
                break;
            }
        }
        return history;
    }

    private static bool IsDigestMessage(JsonNode msg)
    {
        if (msg is not JsonObject obj || obj["role"]?.GetValue<string>() != "user")
            return false;
        if (obj["content"] is not JsonArray arr || arr.Count == 0 || arr[0] is not JsonObject first)
            return false;
        return first["type"]?.GetValue<string>() == "text"
            && (first["text"]?.GetValue<string>() ?? "").StartsWith(DigestPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a single turn: user sends text, LLM responds (potentially with tool calls), loop until final answer.
    /// </summary>
    public async Task RunTurnAsync(
        string userText,
        Action<string> onAssistantText,
        Action<string, string> onToolCall,
        CancellationToken ct = default,
        Action<string, string, string>? onToolResult = null,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        // 0. Auto-compact an overlong history before adding the new request.
        var compactNote = await MaybeCompactAsync(ct);
        if (compactNote != null)
            onAssistantText(compactNote + "\n\n");

        // 1. Build user message and append to history + store.
        var userMsg = attachments is { Count: > 0 }
            ? _llm.BuildUserMessage(userText, attachments)
            : _llm.BuildUserMessage(userText);
        _history.Add(userMsg);

        // Persist: extract content from the provider's user message format.
        var userContentJson = ExtractContentJson(userMsg);
        await _store.AppendMessageAsync(_conversationId, "user", userContentJson);

        // Fetch tool definitions once per turn, then apply profile filter.
        var rawToolDefs = await _tools.ListToolDefinitionsAsync(ct);
        var toolDefs = ToolProfiles.Filter(rawToolDefs, _toolProfile);

        // 2. Main loop.
        for (int i = 0; i < _maxIterations; i++)
        {
            // Call LLM.
            ct.ThrowIfCancellationRequested();
            var response = await _llm.CreateMessageAsync(_history, toolDefs, _systemPrompt, ct);

            // Append assistant response verbatim to history + store.
            _history.Add(response.AssistantMessage);
            var assistantContentJson = ExtractContentJson(response.AssistantMessage);
            await _store.AppendMessageAsync(_conversationId, "assistant", assistantContentJson);

            // Invoke callbacks for text blocks.
            foreach (var text in response.TextBlocks)
            {
                onAssistantText(text);
            }

            // If no tool calls, we are done.
            if (response.ToolCalls.Count == 0)
            {
                // Truncated responses (finish_reason=length) often lost a planned tool
                // call - never swallow that silently, the user just sees a dead stop.
                if (response.StopReason is "length" or "max_tokens")
                {
                    onAssistantText(
                        "\n\n[warning] response was cut off by the output-token limit " +
                        $"(finish_reason={response.StopReason}) - a planned tool call may have been lost. " +
                        "Send \"continue\" to let the model pick up where it stopped.");
                }
                else if (!response.TextBlocks.Any(t => !string.IsNullOrWhiteSpace(t)))
                {
                    // Empty response with a normal stop reason: typical of an overlong
                    // conversation degrading the model. Surface it instead of dead air.
                    onAssistantText(
                        $"[warning] the model returned an empty response (finish_reason={response.StopReason}). " +
                        "This usually means the conversation has grown too long for the model to handle reliably - " +
                        "consider starting a new conversation.");
                }
                break;
            }

            // Execute tools and build tool_result messages.
            var callResults = new List<(ParsedToolCall Call, string Result)>();
            try
            {
                foreach (var call in response.ToolCalls)
                {
                    var args = ConvertToJsonDictionary(call.Input);

                    // Defense-in-depth: deny execution of tools not in the active profile.
                    string result;
                    if (call.ParseError != null)
                    {
                        // Malformed tool-argument JSON: feed error back to model for retry.
                        result = $"[error] tool arguments were not valid JSON: {call.ParseError}. Re-send the tool call with corrected, strictly valid JSON.";
                    }
                    else if (!ToolProfiles.IsToolAllowed(call.Name, _toolProfile))
                    {
                        result = ToolProfiles.DenyMessage(call.Name);
                    }
                    else
                    {
                        // Throttle devmind_task_status polls for the same job_id.
                        if (call.Name == "devmind_task_status" && _pollThrottleSeconds > 0)
                        {
                            string? jobId = args["job_id"] as string;
                            if (jobId != null)
                            {
                                if (_lastPollTimeByJobId.TryGetValue(jobId, out var lastPoll))
                                {
                                    var elapsed = DateTime.UtcNow - lastPoll;
                                    var throttleWindow = TimeSpan.FromSeconds(_pollThrottleSeconds);
                                    if (elapsed < throttleWindow)
                                    {
                                        var remaining = throttleWindow - elapsed;
                                        if (!ct.IsCancellationRequested)
                                        {
                                            await Task.Delay(remaining, ct);
                                        }
                                    }
                                }
                                _lastPollTimeByJobId[jobId] = DateTime.UtcNow;
                            }
                        }
                        result = await _tools.CallToolAsync(call.Name, args, ct).WaitAsync(ct);
                    }

                    onToolCall(call.Name, call.Input.ToJsonString());
                    onToolResult?.Invoke(call.Name, call.Input.ToJsonString(), result);
                    callResults.Add((call, result));

                    // Capture delegation lifecycle.
                    CaptureDelegation(call.Name, args, result);
                }
            }
            catch (OperationCanceledException)
            {
                // History-consistency: append "[cancelled by user]" tool results for all
                // outstanding tool calls (those not yet completed) so the next turn has
                // balanced history (every tool_calls needs tool results).
                var completedIds = new HashSet<string>(callResults.Select(cr => cr.Call.Id));
                var outstandingCalls = response.ToolCalls.Where(tc => !completedIds.Contains(tc.Id)).ToList();

                if (outstandingCalls.Count > 0)
                {
                    var cancelledResults = outstandingCalls
                        .Select(tc => (tc, "[cancelled by user]"))
                        .ToList();

                    // Merge completed + cancelled results
                    var allResults = callResults.Concat(cancelledResults).ToList();

                    // Build tool result message(s) via the provider for ALL calls.
                    var cancelledToolResultMessages = _llm.BuildToolResultsMessages(allResults);
                    foreach (var toolResultMsg in cancelledToolResultMessages)
                    {
                        _history.Add(toolResultMsg);
                        var contentJson = ExtractContentJson(toolResultMsg);
                        await _store.AppendMessageAsync(_conversationId, "user", contentJson);
                    }
                }
                else if (callResults.Count > 0)
                {
                    // All tools completed before cancellation — still build results normally.
                    var completedToolResultMessages = _llm.BuildToolResultsMessages(callResults);
                    foreach (var toolResultMsg in completedToolResultMessages)
                    {
                        _history.Add(toolResultMsg);
                        var contentJson = ExtractContentJson(toolResultMsg);
                        await _store.AppendMessageAsync(_conversationId, "user", contentJson);
                    }
                }
                throw;
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

    /// <summary>
    /// When history exceeds the compaction threshold: summarize EVERYTHING so far into a
    /// digest via the LLM (no tools), then replace all but a recent verbatim tail with the
    /// digest message. The digest is also persisted, so reloads skip the replaced rows.
    /// Returns a user-facing note, or null when no compaction happened.
    /// </summary>
    private async Task<string?> MaybeCompactAsync(CancellationToken ct)
    {
        if (_compactThresholdChars <= 0 || _history.Count < 6)
            return null;
        long total = HistoryChars;
        if (total < _compactThresholdChars)
            return null;

        try
        {
            // Keep a verbatim tail (~25% of the threshold) ending at a safe cut point:
            // a plain user message, never a tool-result carrier (cutting before one would
            // orphan the preceding tool_use and make providers reject the history).
            long keepChars = _compactThresholdChars / 4;
            long tail = 0;
            int cut = -1;
            for (int i = _history.Count - 1; i > 0; i--)
            {
                tail += _history[i].ToJsonString().Length;
                if (tail >= keepChars && IsSafeCutBoundary(i))
                {
                    cut = i;
                    break;
                }
            }
            if (cut <= 0)
                return null;

            string rendered = RenderForDigest(_history, maxChars: 400_000);
            string digestPrompt =
                "Summarize the conversation below into a dense digest (at most ~1500 words) that preserves: " +
                "project context; every decision and requirement; work completed; user preferences and corrections; " +
                "unfinished tasks with their full requirements. Output ONLY the digest text.\n\n" + rendered;

            var resp = await _llm.CreateMessageAsync(
                new List<JsonNode> { _llm.BuildUserMessage(digestPrompt) },
                Array.Empty<ToolDefinition>(), null, ct);
            string digest = string.Join("\n", resp.TextBlocks).Trim();
            if (digest.Length == 0)
                return null;

            var digestMsg = _llm.BuildUserMessage(DigestPrefix + "\n" + digest);
            _history.RemoveRange(0, cut);
            _history.Insert(0, digestMsg);
            await _store.AppendMessageAsync(_conversationId, "user", ExtractContentJson(digestMsg));

            return $"[info] conversation auto-compacted: ~{total / 4000}k → ~{HistoryChars / 4000}k tokens (older turns summarized into a digest)";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Compaction is best-effort - never block the actual turn over it.
            Console.WriteLine($"[AgenticLoop] Compaction failed: {ex.Message}");
            return null;
        }
    }

    private bool IsSafeCutBoundary(int i)
    {
        if (_history[i] is not JsonObject obj || obj["role"]?.GetValue<string>() != "user")
            return false;
        // Anthropic tool_result carriers have role=user - not a turn boundary.
        if (obj["content"] is JsonArray arr && arr.Count > 0 &&
            arr[0] is JsonObject first && first["type"]?.GetValue<string>() == "tool_result")
            return false;
        return true;
    }

    /// <summary>Flatten history to readable text for digest/handoff prompts (text blocks +
    /// tool-call names; individual messages truncated). When over the cap, the head and
    /// tail are kept and the middle omitted - the newest turns carry the outstanding work.</summary>
    private static string RenderForDigest(List<JsonNode> history, int maxChars)
    {
        var entries = new List<string>();
        foreach (var msg in history)
        {
            if (msg is not JsonObject obj)
                continue;
            string role = obj["role"]?.GetValue<string>() ?? "?";

            if (obj["content"] is JsonValue cv && cv.TryGetValue<string>(out var plain))
            {
                AddEntry(entries, role, plain);
            }
            else if (obj["content"] is JsonArray blocks)
            {
                foreach (var block in blocks)
                {
                    if (block is not JsonObject b)
                        continue;
                    switch (b["type"]?.GetValue<string>())
                    {
                        case "text":
                            AddEntry(entries, role, b["text"]?.GetValue<string>() ?? "");
                            break;
                        case "tool_use":
                            AddEntry(entries, role, $"(called tool {b["name"]?.GetValue<string>()})");
                            break;
                        case "tool_result":
                            AddEntry(entries, "tool", b["content"]?.ToJsonString() ?? "");
                            break;
                    }
                }
            }
            // OpenAI-style tool_calls on the message object itself
            if (obj["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var name = (tc as JsonObject)?["function"]?["name"]?.GetValue<string>();
                    if (name != null)
                        AddEntry(entries, role, $"(called tool {name})");
                }
            }
        }

        long total = 0;
        foreach (var e in entries) total += e.Length;
        var sb = new System.Text.StringBuilder();
        if (total <= maxChars)
        {
            foreach (var e in entries) sb.Append(e);
            return sb.ToString();
        }

        // Over budget: ~30% head, rest tail, middle omitted.
        long headBudget = maxChars * 3 / 10;
        int i = 0;
        for (; i < entries.Count && sb.Length + entries[i].Length <= headBudget; i++)
            sb.Append(entries[i]);

        long tailBudget = maxChars - sb.Length - 64;
        long tailLen = 0;
        int j = entries.Count;
        while (j - 1 > i && tailLen + entries[j - 1].Length <= tailBudget)
        {
            j--;
            tailLen += entries[j].Length;
        }
        sb.AppendLine("[... middle of the conversation omitted for length ...]").AppendLine();
        for (int k = j; k < entries.Count; k++)
            sb.Append(entries[k]);
        return sb.ToString();

        static void AddEntry(List<string> entries, string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (text.Length > 1500)
                text = text[..1500] + " …[truncated]";
            entries.Add($"{role}: {text}\n\n");
        }
    }

    /// <summary>
    /// Generate a structured handoff document for the whole conversation via the LLM
    /// (no tools) - used by /handoff so a fresh conversation can pick up the work.
    /// </summary>
    public async Task<string> GenerateHandoffAsync(CancellationToken ct = default)
    {
        string rendered = RenderForDigest(_history, maxChars: 400_000);
        if (rendered.Length == 0)
            throw new InvalidOperationException("conversation is empty - nothing to hand off");

        string prompt =
            "You are writing a handoff document so a fresh AI assistant (with code and tool access, " +
            "but NO memory of this conversation) can continue the work seamlessly. From the conversation " +
            "below, write a markdown document with exactly these sections:\n" +
            "# Project handoff\n" +
            "## What this project is\n" +
            "## Key decisions made\n" +
            "## Work completed\n" +
            "## User preferences and corrections to respect\n" +
            "## Current outstanding tasks (capture EVERY stated requirement in detail)\n" +
            "## Open questions / loose ends\n" +
            "Be faithful to the conversation - do not invent details; mark anything ambiguous as '(unclear)'. " +
            "Output ONLY the markdown document.\n\n" + rendered;

        var resp = await _llm.CreateMessageAsync(
            new List<JsonNode> { _llm.BuildUserMessage(prompt) },
            Array.Empty<ToolDefinition>(), null, ct);
        string doc = string.Join("\n", resp.TextBlocks).Trim();
        if (doc.Length == 0)
            throw new InvalidOperationException("the model returned an empty handoff document");
        return doc;
    }

    /// <summary>Extract the content JSON from a provider message node for persistence.</summary>
    private static string ExtractContentJson(JsonNode msgNode)
    {
        // AnthropicClient returns the content array directly; OpenAiCompatClient returns a full message object.
        if (msgNode is JsonArray contentArr)
        {
            return contentArr.ToJsonString();
        }

        var obj = msgNode.AsObject();
        if (obj["content"] is JsonArray innerArray)
        {
            // Full message with content array: { "role": ..., "content": [...] }
            return innerArray.ToJsonString();
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
                    // needs_input and stopped_incomplete are terminal for THIS job id —
                    // resuming mints a NEW job id via devmind_task_continue, so leaving
                    // the delegation record open would orphan it.
                    if (state is "done" or "failed" or "cancelled" or "needs_input" or "stopped_incomplete")
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
