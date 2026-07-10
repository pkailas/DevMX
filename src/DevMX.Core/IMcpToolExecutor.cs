using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DevMX.Core;

/// <summary>
/// Abstraction over MCP tool execution so the agentic loop can be tested without a live server.
/// </summary>
public interface IMcpToolExecutor
{
    /// <summary>
    /// Returns the tool definitions available from the MCP server.
    /// </summary>
    Task<IReadOnlyList<ToolDefinition>> ListToolDefinitionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Invokes a tool by name with the given arguments and returns the text result.
    /// </summary>
    Task<string> CallToolAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);
}

/// <summary>
/// Minimal tool definition used when passing tools to the LLM provider.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema);
