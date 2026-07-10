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
        ConversationId = await _store.CreateConversationAsync(provider, model!, workDir, "(untitled)");
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

        var result = await _mcp.CallToolAsync("read_file", new Dictionary<string, object?> { ["filename"] = path });

        // Best-effort strip tool-banner prefix lines (e.g. "[READ:...]" or code fences)
        var lines = result.Split('\n');
        var startIdx = 0;
        var endIdx = lines.Length;

        // Strip leading banner lines (lines starting with "[")
        while (startIdx < endIdx && lines[startIdx].TrimStart().StartsWith("["))
            startIdx++;

        // Strip leading/trailing code fences (``` lines)
        while (startIdx < endIdx && lines[startIdx].Trim().StartsWith("```"))
            startIdx++;
        while (endIdx > startIdx && lines[endIdx - 1].Trim().StartsWith("```"))
            endIdx--;

        if (startIdx >= endIdx)
            return result; // nothing useful to strip, return as-is

        return string.Join("\n", lines[startIdx..endIdx]);
    }

    /// <summary>
    /// Creates a new conversation and resets the AgenticLoop.
    /// Returns the new conversation ID.
    /// </summary>
    public async Task<long> CreateNewConversationAsync(CancellationToken ct = default)
    {
        if (_store == null || _provider == null || _mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        ConversationId = await _store.CreateConversationAsync(_settings.Provider, Model!, _settings.WorkDir, "(untitled)");
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
