using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevMX.Core;

/// <summary>
/// Thin wrapper around the ModelContextProtocol stdio client for driving the DevMind MCP server.
/// </summary>
public sealed class DevMxMcpClient : IMcpToolExecutor, IAsyncDisposable
{
    private readonly McpClient _client;

    private DevMxMcpClient(McpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Creates and initializes a client connected to the MCP server at <paramref name="serverExePath"/>.
    /// </summary>
    /// <param name="serverExePath">Absolute path to the DevMind.McpServer.exe.</param>
    /// <param name="workingDir">Working directory passed as --dir to the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DevMxMcpClient> StartAsync(
        string serverExePath,
        string workingDir,
        CancellationToken cancellationToken = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = serverExePath,
            Arguments = new List<string> { "--dir", workingDir },
            WorkingDirectory = workingDir,
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new DevMxMcpClient(client);
    }

    /// <summary>
    /// Lists all tools exposed by the server.
    /// </summary>
    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        return await _client.ListToolsAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns tool definitions suitable for passing to an LLM provider.
    /// </summary>
    public async Task<IReadOnlyList<ToolDefinition>> ListToolDefinitionsAsync(CancellationToken ct = default)
    {
        var tools = await ListToolsAsync(ct);
        var definitions = new List<ToolDefinition>(tools.Count);
        foreach (var tool in tools)
        {
            // Protocol.Tool.InputSchema is JsonElement — convert to JsonObject for our wire format.
            JsonObject inputSchema;
            if (tool.ProtocolTool.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                inputSchema = new JsonObject { ["type"] = "object" };
            }
            else
            {
                inputSchema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            }
            definitions.Add(new ToolDefinition(
                Name: tool.ProtocolTool.Name,
                Description: tool.ProtocolTool.Description ?? "",
                InputSchema: inputSchema));
        }
        return definitions;
    }

    /// <summary>
    /// Calls a tool on the server and returns the concatenated text content from the result.
    /// </summary>
    /// <param name="toolName">Name of the tool to call (e.g. "read_file").</param>
    /// <param name="arguments">Key-value argument dictionary matching the tool's schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text content returned by the tool.</returns>
    public async Task<string> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

        if (result.IsError == true)

        {
            var text = result.Content.OfType<TextContentBlock>()
                .Select(b => b.Text)
                .FirstOrDefault() ?? "(no error text)";
            return $"[ERROR] {text}";
        }

        return result.Content.OfType<TextContentBlock>()
            .Select(b => b.Text)
            .FirstOrDefault() ?? "(empty response)";
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}
