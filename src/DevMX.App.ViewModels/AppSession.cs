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
    public string SystemPrompt => _systemPromptValue ?? DefaultSystemPrompt;

    private static string DefaultSystemPrompt => "You are DevMX, a developer assistant. You have tools to read, write, and analyze code in the working directory. Be concise and precise. When asked to modify code, use the appropriate tool rather than outputting full file contents.";

    private DevMxMcpClient? _mcp;
    private ConversationStore? _store;
    private IChatProvider? _provider;
    private AgenticLoop? _loop;

    public bool IsInitialized { get; private set; }
    public string? Model { get; private set; }
    public long ConversationId { get; private set; }
    public int ToolCount { get; private set; }
    public string EffectiveToolProfile { get; private set; } = DevMX.Core.ToolProfiles.Full;

    private string? _systemPromptValue;

    /// <summary>Exposes the conversation store for sidebar operations.</summary>
    public ConversationStore? Store => _store;

    /// <summary>Exposes the current provider for provider-name checks.</summary>
    public IChatProvider? Provider => _provider;

    /// <summary>Creates a session with the given settings.</summary>
    public AppSession(DevMxSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Resolves the effective tool profile from the settings.
    /// "auto" → "full" for loopback endpoints, "restricted" for remote.
    /// </summary>
    private static string ResolveEffectiveToolProfile(string toolProfile, string endpoint, string provider)
    {
        if (toolProfile != DevMX.Core.ToolProfiles.Auto)
        {
            return toolProfile;
        }

        // Auto mode: loopback → full, remote → restricted
        // Anthropic is always remote
        if (provider == "anthropic")
        {
            return DevMX.Core.ToolProfiles.Restricted;
        }

        // Check if endpoint is loopback
        if (IsLoopbackEndpoint(endpoint))
        {
            return DevMX.Core.ToolProfiles.Full;
        }

        return DevMX.Core.ToolProfiles.Restricted;
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            return false;

        try
        {
            var uri = new UriBuilder(endpoint);
            var host = uri.Host.ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == "[::1]" || host == "::1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the system prompt for the given tool profile.
    /// Restricted profile removes direct-edit guidance and adds delegation instruction.
    /// </summary>
    private static string BuildSystemPrompt(string workDir, string effectiveProfile)
    {
        if (effectiveProfile == DevMX.Core.ToolProfiles.Restricted)
        {
            return $"You are DevMX, a developer assistant. You have read-only and delegation tools only. For ANY file modification, write a precise brief and delegate via devmind_task_start; review with diff_file and run_build. Working directory: {workDir}. Be concise and precise.";
        }

        return $"You are DevMX, a developer assistant. You have tools to read, write, and analyze code in the working directory: {workDir}. Be concise and precise. When asked to modify code, use the appropriate tool rather than outputting full file contents.";
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

        // 4. Resolve effective tool profile & build system prompt
        var effectiveProfile = ResolveEffectiveToolProfile(_settings.ToolProfile, endpoint, provider);
        EffectiveToolProfile = effectiveProfile;
        _systemPromptValue = BuildSystemPrompt(workDir, effectiveProfile);
        Console.WriteLine($"[AppSession] Tool profile: {effectiveProfile}");

        // 5. Create provider
        _provider = CreateProvider(provider, endpoint, model!);

        // 6. Create initial conversation
        ConversationId = await _store.CreateConversationAsync(provider, model!, workDir, $"Session {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"[AppSession] Created conversation #{ConversationId}");

        // 7. Create initial AgenticLoop with tool profile
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt, 50, effectiveProfile, ClampPollThrottle(_settings.PollThrottleSeconds));

        IsInitialized = true;
        Console.WriteLine($"[AppSession] Initialized: {ToolCount} tools | model={model} | provider={provider} | profile={effectiveProfile}");
    }

    /// <summary>Clamps poll throttle seconds to 0..60.</summary>
    private static int ClampPollThrottle(int value) => Math.Clamp(value, 0, 60);

    /// <summary>Reads an env var from process scope, falling back to User/Machine registry scope
    /// (a setx after launch, or a launcher with a stale environment, otherwise loses the key).</summary>
    private static string? GetEnvVar(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private static IChatProvider CreateProvider(string provider, string endpoint, string model)
    {
        if (provider == "anthropic")
        {
            string key = GetEnvVar("ANTHROPIC_API_KEY") ?? "";
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
            return new AnthropicClient(key, model);
        }
        return new OpenAiCompatClient(endpoint, GetEnvVar("OPENAI_COMPAT_API_KEY"), model);
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
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt, 50, history, EffectiveToolProfile, ClampPollThrottle(_settings.PollThrottleSeconds));
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
    /// Cancels a running/queued task by calling the MCP devmind_task_cancel tool.
    /// </summary>
    public async Task<string> CancelTaskAsync(string jobId)
    {
        if (_mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        return await _mcp.CallToolAsync("devmind_task_cancel",
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
            if (diffTool != null && diffTool.ProtocolTool.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined)
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
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt, 50, EffectiveToolProfile, ClampPollThrottle(_settings.PollThrottleSeconds));
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
