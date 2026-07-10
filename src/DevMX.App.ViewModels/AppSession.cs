using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevMX.Core;
using DevMX.Core.Persistence;
using DevMX.Core.Providers;

namespace DevMX.App.ViewModels;

/// <summary>
/// Async service owning the runtime agent stack: MCP client, chat provider, conversation store,
/// and the current AgenticLoop. Mirrors the REPL composition from DevMX.Chat/Program.cs.
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    private readonly DevMxSettings _settings;

    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMX", "devmx.db");

    /// <summary>System prompt shown to the LLM — mirrors the REPL constant.</summary>
    public string SystemPrompt { get; }

    private DevMxMcpClient? _mcp;
    private ConversationStore? _store;
    private IChatProvider? _provider;
    private AgenticLoop? _loop;

    public bool IsInitialized { get; private set; }
    public string? Model { get; private set; }
    public long ConversationId { get; private set; }
    public int ToolCount { get; private set; }

    /// <summary>Exposes the conversation store for sidebar operations.</summary>
    public ConversationStore? Store => _store;

    /// <summary>Exposes the current provider for provider-name checks.</summary>
    public IChatProvider? Provider => _provider;

    /// <summary>Creates a session with the given settings.</summary>
    public AppSession(DevMxSettings settings)
    {
        _settings = settings;
        SystemPrompt = $"You are DevMX, a developer assistant. You have tools to read, write, and analyze code in the working directory: {settings.WorkDir}. Be concise and precise. When asked to modify code, use the appropriate tool rather than outputting full file contents.";
    }

    /// <summary>
    /// Initializes the session: opens DB, starts MCP client, discovers model, creates conversation.
    /// Mirrors the REPL startup sequence.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        string dbPath = DefaultDbPath;
        string serverExe = _settings.ServerExe;
        string endpoint = _settings.Endpoint;
        string workDir = _settings.WorkDir;
        string provider = _settings.Provider;
        string? modelOverride = string.IsNullOrEmpty(_settings.Model) ? null : _settings.Model;

        // Ensure DB directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 1. Open conversation store
        Console.WriteLine($"[AppSession] Opening store at {dbPath}...");
        _store = await ConversationStore.OpenAsync(dbPath);

        // 2. Start MCP client
        Console.WriteLine($"[AppSession] Starting MCP server at {serverExe}...");
        _mcp = await DevMxMcpClient.StartAsync(serverExe, workDir, ct);
        var tools = await _mcp.ListToolsAsync(ct);
        ToolCount = tools.Count;
        Console.WriteLine($"[AppSession] MCP connected: {ToolCount} tools available");

        // 3. Model auto-discovery (same logic as REPL)
        string? model = modelOverride;
        string? modelDiscoveryError = null;

        if (provider == "openai" && string.IsNullOrEmpty(model))
        {
            try
            {
                using var discoveryClient = new HttpClient();
                discoveryClient.Timeout = TimeSpan.FromSeconds(5);
                var resp = await discoveryClient.GetAsync($"{endpoint}/models", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
                    {
                        model = dataArray[0].GetProperty("id").GetString();
                        Console.WriteLine($"[AppSession] Model auto-discovered: {model}");
                    }
                }
            }
            catch (Exception ex)
            {
                modelDiscoveryError = $"Model auto-discovery failed: {ex.Message}";
                Console.WriteLine($"[AppSession] {modelDiscoveryError}");
            }
        }

        if (provider == "anthropic" && string.IsNullOrEmpty(model))
        {
            model = "claude-sonnet-4-5";
        }

        if (string.IsNullOrEmpty(model))
        {
            model = "(unset)";
            Model = model;
        }
        else
        {
            Model = model;
        }

        // 4. Create provider
        _provider = CreateProvider(provider, endpoint, model!);

        // 5. Create initial conversation
        ConversationId = await _store.CreateConversationAsync(provider, model!, workDir, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"[AppSession] Created conversation #{ConversationId}");

        // 6. Create initial AgenticLoop
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt);

        IsInitialized = true;
        Console.WriteLine($"[AppSession] Initialized: {ToolCount} tools | model={model} | provider={provider}");
    }

    private static IChatProvider CreateProvider(string provider, string endpoint, string model)
    {
        if (provider == "anthropic")
        {
            string key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
            return new AnthropicClient(key, model);
        }
        return new OpenAiCompatClient(endpoint, Environment.GetEnvironmentVariable("OPENAI_COMPAT_API_KEY"), model);
    }

    /// <summary>
    /// Opens an existing conversation by ID. Loads history and rebuilds the AgenticLoop.
    /// </summary>
    public async Task OpenConversationAsync(long conversationId, CancellationToken ct = default)
    {
        if (_store == null || _provider == null || _mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        ConversationId = conversationId;
        var history = await AgenticLoop.LoadHistoryAsync(_store, conversationId);
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt, 50, history);
        Console.WriteLine($"[AppSession] Opened conversation #{conversationId} ({history.Count} messages)");
    }

    /// <summary>
    /// Runs a single agentic turn for the current conversation.
    /// </summary>
    public async Task StartTurnAsync(
        string userText,
        Action<string> onAssistantText,
        Action<string, string> onToolCall,
        CancellationToken ct = default,
        Action<string, string, string>? onToolResult = null)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("AppSession not initialized. Call InitializeAsync first.");

        if (_loop == null || _store == null || _provider == null)
            throw new InvalidOperationException("AppSession components not ready.");

        // Check for unset model before attempting a real call
        if (Model == "(unset)")
            throw new InvalidOperationException("Model is not set. Could not auto-discover a model from the /models endpoint. Please ensure your local LLM server is running.");

        await _loop.RunTurnAsync(userText, onAssistantText, onToolCall, ct, onToolResult);
    }

    /// <summary>
    /// Fetches file content by calling the MCP read_file tool directly.
    /// Strips any tool-banner prefix lines from the result.
    /// </summary>
    public async Task<string> FetchFileAsync(string path)
    {
        if (_mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        var result = await _mcp.CallToolAsync("read_file",
            new Dictionary<string, object?> { ["filename"] = path, ["force_full"] = true });

        // DM wraps content as: [READ:name] banner lines, then a ``` fence, content, closing ``` fence.
        // Extract between the first and last fence when both exist; otherwise strip leading banner lines.
        var lines = result.Split('\n');
        int firstFence = -1, lastFence = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("```"))
            {
                if (firstFence < 0) firstFence = i;
                lastFence = i;
            }
        }

        if (firstFence >= 0 && lastFence > firstFence)
            return string.Join("\n", lines[(firstFence + 1)..lastFence]);

        var startIdx = 0;
        while (startIdx < lines.Length && lines[startIdx].TrimStart().StartsWith("["))
            startIdx++;
        return string.Join("\n", lines[startIdx..]);
    }

    /// <summary>
    /// Polls the status of a background task by calling the MCP devmind_task_status tool.
    /// Returns the raw JSON result string.
    /// </summary>
    public async Task<string> PollTaskAsync(string jobId)
    {
        if (_mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        return await _mcp.CallToolAsync("devmind_task_status",
            new Dictionary<string, object?> { ["job_id"] = jobId });
    }

    /// <summary>
    /// Fetches the result of a completed task by calling the MCP devmind_task_result tool.
    /// Returns the raw JSON result string (contains journal/answer).
    /// </summary>
    public async Task<string> FetchTaskResultAsync(string jobId)
    {
        if (_mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        return await _mcp.CallToolAsync("devmind_task_result",
            new Dictionary<string, object?> { ["job_id"] = jobId });
    }

    /// <summary>
    /// Fetches a diff for a file by calling the MCP diff_file tool.
    /// Verifies the exact arg name from the tool's input schema at runtime.
    /// Returns the raw diff text.
    /// </summary>
    public async Task<string> FetchDiffAsync(string filePath)
    {
        if (_mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        // Look up the exact arg name from the tool schema
        var argName = "filename"; // default fallback
        try
        {
            var tools = await _mcp.ListToolsAsync();
            var diffTool = tools.FirstOrDefault(t => t.ProtocolTool.Name == "diff_file");
            if (diffTool?.ProtocolTool.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            {
                var schema = System.Text.Json.Nodes.JsonNode.Parse(diffTool.ProtocolTool.InputSchema.GetRawText())
                    as System.Text.Json.Nodes.JsonObject;
                // Check for properties in the schema
                if (schema != null)
                {
                    foreach (var kvp in schema)
                    {
                        if (kvp.Key.Equals("filename", StringComparison.OrdinalIgnoreCase) ||
                            kvp.Key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                            kvp.Key.Equals("file", StringComparison.OrdinalIgnoreCase))
                        {
                            argName = kvp.Key;
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort — use default arg name
        }

        var result = await _mcp.CallToolAsync("diff_file",
            new Dictionary<string, object?> { [argName] = filePath });

        // Strip banner lines like FetchFileAsync does
        return StripToolBanner(result);
    }

    private static string StripToolBanner(string result)
    {
        var lines = result.Split('\n');
        int firstFence = -1, lastFence = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("```"))
            {
                if (firstFence < 0) firstFence = i;
                lastFence = i;
            }
        }

        if (firstFence >= 0 && lastFence > firstFence)
            return string.Join("\n", lines[(firstFence + 1)..lastFence]);

        var startIdx = 0;
        while (startIdx < lines.Length && lines[startIdx].TrimStart().StartsWith("["))
            startIdx++;
        return string.Join("\n", lines[startIdx..]);
    }

    /// <summary>
    /// Creates a new conversation and resets the AgenticLoop.
    /// Returns the new conversation ID.
    /// </summary>
    public async Task<long> CreateNewConversationAsync(CancellationToken ct = default)
    {
        if (_store == null || _provider == null || _mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        ConversationId = await _store.CreateConversationAsync(_settings.Provider, Model!, _settings.WorkDir, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}");
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt);
        Console.WriteLine($"[AppSession] New conversation #{ConversationId}");
        return ConversationId;
    }

    /// <summary>
    /// Updates the title of the current conversation in the store.
    /// </summary>
    public async Task UpdateTitleAsync(string title)
    {
        if (_store == null)
            throw new InvalidOperationException("AppSession not initialized.");

        await _store.UpdateTitleAsync(ConversationId, title);
    }

    /// <summary>
    /// Gets the current AgenticLoop (for internal access if needed).
    /// </summary>
    internal AgenticLoop? Loop => _loop;

    public async ValueTask DisposeAsync()
    {
        if (_mcp != null)
        {
            try { await _mcp.DisposeAsync(); } catch { /* best-effort */ }
        }
        if (_store != null)
        {
            try { await _store.DisposeAsync(); } catch { /* best-effort */ }
        }
        if (_provider is IDisposable d)
        {
            try { d.Dispose(); } catch { /* best-effort */ }
        }
    }
}
