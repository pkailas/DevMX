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
    private const string DefaultServerExe = @"C:\Users\pkailas\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe";
    private const string DefaultWorkDir = @"C:\Users\pkailas\source\repos\DevMX";
    private const string DefaultEndpoint = "http://127.0.0.1:8080/v1";

    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMX", "devmx.db");

    /// <summary>System prompt shown to the LLM — mirrors the REPL constant.</summary>
    public string SystemPrompt { get; }

    private readonly string _workDir;
    private DevMxMcpClient? _mcp;
    private ConversationStore? _store;
    private IChatProvider? _provider;
    private AgenticLoop? _loop;

    public bool IsInitialized { get; private set; }
    public string? Model { get; private set; }
    public long ConversationId { get; private set; }
    public int ToolCount { get; private set; }

    /// <summary>Creates a session with the default settings (matching the REPL defaults).</summary>
    public AppSession()
        : this(DefaultWorkDir)
    {
    }

    /// <summary>Creates a session with a custom working directory.</summary>
    public AppSession(string workDir)
    {
        _workDir = workDir;
        SystemPrompt = $"You are DevMX, a developer assistant. You have tools to read, write, and analyze code in the working directory: {_workDir}. Be concise and precise. When asked to modify code, use the appropriate tool rather than outputting full file contents.";
    }

    /// <summary>
    /// Initializes the session: opens DB, starts MCP client, discovers model, creates conversation.
    /// Mirrors the REPL startup sequence.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        string dbPath = DefaultDbPath;
        string serverExe = DefaultServerExe;
        string endpoint = DefaultEndpoint;

        // Ensure DB directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 1. Open conversation store
        Console.WriteLine($"[AppSession] Opening store at {dbPath}...");
        _store = await ConversationStore.OpenAsync(dbPath);

        // 2. Start MCP client
        Console.WriteLine($"[AppSession] Starting MCP server at {serverExe}...");
        _mcp = await DevMxMcpClient.StartAsync(serverExe, _workDir, ct);
        var tools = await _mcp.ListToolsAsync(ct);
        ToolCount = tools.Count;
        Console.WriteLine($"[AppSession] MCP connected: {ToolCount} tools available");

        // 3. Model auto-discovery (same logic as REPL)
        string? model = null;
        string? modelDiscoveryError = null;
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

        if (string.IsNullOrEmpty(model))
        {
            // Model is unset — we'll surface the error on first send, but still create the provider
            // with a placeholder model so the session is "initialized" (send will fail with a clear message).
            model = "(unset)";
            Model = model;
        }
        else
        {
            Model = model;
        }

        // 4. Create provider
        _provider = new OpenAiCompatClient(endpoint, null, model!);

        // 5. Create initial conversation
        ConversationId = await _store.CreateConversationAsync("openai", model!, _workDir, "(untitled)");
        Console.WriteLine($"[AppSession] Created conversation #{ConversationId}");

        // 6. Create initial AgenticLoop
        _loop = new AgenticLoop(_provider, _mcp, _store, ConversationId, SystemPrompt);

        IsInitialized = true;
        Console.WriteLine($"[AppSession] Initialized: {ToolCount} tools | model={model}");
    }

    /// <summary>
    /// Runs a single agentic turn for the current conversation.
    /// </summary>
    /// <param name="userText">User's input text.</param>
    /// <param name="onAssistantText">Called with each text block from the assistant.</param>
    /// <param name="onToolCall">Called with tool name and JSON args when a tool is invoked.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartTurnAsync(
        string userText,
        Action<string> onAssistantText,
        Action<string, string> onToolCall,
        CancellationToken ct = default)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("AppSession not initialized. Call InitializeAsync first.");

        if (_loop == null || _store == null || _provider == null)
            throw new InvalidOperationException("AppSession components not ready.");

        // Check for unset model before attempting a real call
        if (Model == "(unset)")
            throw new InvalidOperationException("Model is not set. Could not auto-discover a model from the /models endpoint. Please ensure your local LLM server is running.");

        await _loop.RunTurnAsync(userText, onAssistantText, onToolCall, ct);
    }

    /// <summary>
    /// Creates a new conversation and resets the AgenticLoop.
    /// Returns the new conversation ID.
    /// </summary>
    public async Task<long> CreateNewConversationAsync(CancellationToken ct = default)
    {
        if (_store == null || _provider == null || _mcp == null)
            throw new InvalidOperationException("AppSession not initialized.");

        ConversationId = await _store.CreateConversationAsync("openai", Model!, _workDir, "(untitled)");
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

    /// <summary>Gets the current AgenticLoop (for internal access if needed).</summary>
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
