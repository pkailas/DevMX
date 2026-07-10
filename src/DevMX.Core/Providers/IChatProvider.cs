using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace DevMX.Core.Providers;

/// <summary>
/// Provider-agnostic chat interface. The loop orchestrates over opaque JsonNode
/// messages; the provider owns the wire format entirely.
/// </summary>
public interface IChatProvider
{
    /// <summary>Provider identifier, e.g. "anthropic" | "openai".</summary>
    string ProviderName { get; }

    /// <summary>Model identifier, e.g. "claude-sonnet-4-20250514" | "gpt-4o".</summary>
    string Model { get; }

    /// <summary>
    /// Sends a chat completion request.
    /// </summary>
    /// <param name="history">Conversation history as opaque JsonNode messages.</param>
    /// <param name="tools">Available tool definitions (may be empty).</param>
    /// <param name="system">Optional system prompt (injected at request time, not persisted).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProviderResponse> CreateMessageAsync(
        IReadOnlyList<JsonNode> history,
        IReadOnlyList<ToolDefinition> tools,
        string? system,
        CancellationToken ct = default);

    /// <summary>Build a user message JsonNode from plain text.</summary>
    JsonNode BuildUserMessage(string text);

    /// <summary>
    /// Build tool-result message(s) from tool call results.
    /// Anthropic returns a 1-element list (one user message with all tool_result blocks).
    /// OpenAI returns N elements (one tool message per result).
    /// </summary>
    IReadOnlyList<JsonNode> BuildToolResultsMessages(
        IReadOnlyList<(ParsedToolCall Call, string Result)> results);
}

/// <summary>Response from any chat provider, normalised for the agentic loop.</summary>
public sealed record ProviderResponse(
    /// <summary>The assistant message verbatim, ready to append to history and store.</summary>
    JsonNode AssistantMessage,

    /// <summary>Provider-specific stop reason string, e.g. "end_turn" | "stop" | "tool_calls".</summary>
    string StopReason,

    /// <summary>Parsed tool calls if any (empty = no tool calls).</summary>
    IReadOnlyList<ParsedToolCall> ToolCalls,

    /// <summary>Text blocks extracted from the response (for callbacks).</summary>
    IReadOnlyList<string> TextBlocks);

/// <summary>A single parsed tool call from a provider response.</summary>
public sealed record ParsedToolCall(
    string Id,
    string Name,
    JsonObject Input);
