// DevMX.Chat — Console REPL connecting to the DevMind MCP tool server.
// Supports OpenAI/Anthropic LLM providers, auto-discovers models, persists conversations via SQLite, and drives an agentic coding loop.

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevMX.Core;
using DevMX.Core.Persistence;
using DevMX.Core.Providers;

// ── Argument parsing ──────────────────────────────────────────────────────────
string server = @"C:\Users\pkailas\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe";
string workdir = @"C:\Users\pkailas\source\repos\DevMX";
string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMX", "devmx.db");
string provider = "openai";
string? endpointOverride = null;

string? modelEnv = Environment.GetEnvironmentVariable("DEVMX_MODEL");
string? model = modelEnv; // may stay null for openai auto-discovery

string[] cmdArgs = Environment.GetCommandLineArgs();
for (int i = 1; i < cmdArgs.Length; i++)
{
    switch (cmdArgs[i])
    {
        case "--server" when i + 1 < cmdArgs.Length: server = cmdArgs[++i]; break;
        case "--workdir" when i + 1 < cmdArgs.Length: workdir = cmdArgs[++i]; break;
        case "--db" when i + 1 < cmdArgs.Length: dbPath = cmdArgs[++i]; break;
        case "--model" when i + 1 < cmdArgs.Length: model = cmdArgs[++i]; break;
        case "--provider" when i + 1 < cmdArgs.Length: provider = cmdArgs[++i].ToLowerInvariant(); break;
        case "--endpoint" when i + 1 < cmdArgs.Length: endpointOverride = cmdArgs[++i]; break;
    }
}

// Validate provider
if (provider != "openai" && provider != "anthropic")
{
    Console.WriteLine($"Error: Unknown provider '{provider}'. Use 'openai' or 'anthropic'.");
    Environment.Exit(1);
}

// Resolve endpoint
string openaiEndpoint = endpointOverride ?? "http://127.0.0.1:8080/v1";

// Resolve model: anthropic needs a default; openai may auto-discover
if (provider == "anthropic" && string.IsNullOrEmpty(model))
    model = "claude-sonnet-4-5";

// ── Model auto-discovery for openai ───────────────────────────────────────────
string? modelDiscoveryError = null;
if (provider == "openai" && string.IsNullOrEmpty(model))
{
    try
    {
        using var discoveryClient = new HttpClient();
        discoveryClient.Timeout = TimeSpan.FromSeconds(5);
        var resp = await discoveryClient.GetAsync($"{openaiEndpoint}/models");
        if (resp.IsSuccessStatusCode)
        {
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
            {
                model = dataArray[0].GetProperty("id").GetString();
                Console.WriteLine($"Model auto-discovered: {model}");
            }
        }
    }
    catch
    {
        // Auto-discovery failed — will show "(unset)" in banner and error at first turn
        modelDiscoveryError = "Model auto-discovery failed (could not reach /models endpoint).";
    }
}

string displayModel = string.IsNullOrEmpty(model) ? "(unset — pass --model)" : model!;

// ── Banner ────────────────────────────────────────────────────────────────────
string anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║                   DevMX Chat REPL                       ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Server : {server,-43}║");
Console.WriteLine($"║  WorkDir: {workdir,-43}║");
Console.WriteLine($"║  DB     : {dbPath,-43}║");
Console.WriteLine($"║  Provider: {provider,-42}║");
Console.WriteLine($"║  Endpoint: {openaiEndpoint,-42}║");
Console.WriteLine($"║  Model  : {displayModel,-43}║");
Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
if (provider == "anthropic" && string.IsNullOrEmpty(anthropicApiKey))
    Console.WriteLine("║  ⚠  ANTHROPIC_API_KEY MISSING — set it before chatting  ║");
if (provider == "openai" && string.IsNullOrEmpty(model))
    Console.WriteLine($"║  ⚠  {modelDiscoveryError ?? "No model set",-49}║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

// ── Ensure DB directory exists ────────────────────────────────────────────────
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

// ── Open store & start MCP client ─────────────────────────────────────────────
ConversationStore store = await ConversationStore.OpenAsync(dbPath);
DevMxMcpClient mcp = await DevMxMcpClient.StartAsync(server, workdir, CancellationToken.None);

// ── Create initial conversation ───────────────────────────────────────────────
long conversationId = await store.CreateConversationAsync(provider, model ?? "(unset)", workdir);
Console.WriteLine($"Conversation #{conversationId} created.");
Console.WriteLine("Type /help for commands, or start chatting.\n");

// ── System prompt ─────────────────────────────────────────────────────────────
string systemPrompt = $"You are DevMX, a coding agent driving the DevMind tool server. Choose the cheapest tier per step: use read-only tools (read_file, grep_file, find_in_files, find_symbol, list_files) to scout; make small judgment edits directly (patch_file, create_file); delegate well-scoped mechanical work to the local agent via devmind_task_start with a precise junior-dev brief (goal, exact relative paths, constraints, verification), then poll devmind_task_status and review with diff_file and run_build before trusting results. Delegated task states: needs_input means the agent paused with specific questions — fetch devmind_task_result and answer them via devmind_task_continue (ask the user first if you cannot answer them yourself); stopped_incomplete means the work is NOT trustworthy yet — check incomplete_reasons, and for 'thrashing' (the same failure kept recurring) re-brief with more context instead of a bare 'continue'. Working directory: {workdir}.";

// ── State ─────────────────────────────────────────────────────────────────────
bool titled = false; // whether current conversation has been titled
AgenticLoop? loop = null;

// ── Helper: build loop ────────────────────────────────────────────────────────
AgenticLoop BuildLoop(List<JsonNode>? history = null)
{
    if (history is null)
        return new AgenticLoop(CreateLlm(), mcp, store, conversationId, systemPrompt);
    return new AgenticLoop(CreateLlm(), mcp, store, conversationId, systemPrompt, 50, history);
}

IChatProvider CreateLlm()
{
    if (provider == "anthropic")
    {
        string key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set. Set it before chatting.");
        return new AnthropicClient(key, model!);
    }
    if (provider == "openai")
    {
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException("Model is not set. Pass --model or ensure the endpoint /models endpoint is reachable.");
        string? openaiKey = Environment.GetEnvironmentVariable("OPENAI_COMPAT_API_KEY");
        return new OpenAiCompatClient(openaiEndpoint, openaiKey, model!);
    }
    throw new InvalidOperationException($"Unknown provider: {provider}");
}

// ── REPL loop ─────────────────────────────────────────────────────────────────
try
{
    while (true)
    {
        Console.Write("> ");
        string? line = Console.ReadLine();
        if (line is null) break; // EOF
        line = line.Trim();
        if (line.Length == 0) continue;

        // ── /quit ──
        if (line == "/quit")
            break;

        // ── /help ──
        if (line == "/help")
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("  /quit          Exit the REPL");
            Console.WriteLine("  /list          List conversations (newest first)");
            Console.WriteLine("  /new [title]   Start a new conversation");
            Console.WriteLine("  /open <id>     Open an existing conversation by id");
            Console.WriteLine("  /help          Show this help");
            Console.WriteLine();
            continue;
        }

        // ── /list ──
        if (line == "/list")
        {
            try
            {
                var convos = await store.ListConversationsAsync();
                if (convos.Count == 0)
                {
                    Console.WriteLine("  (no conversations)");
                }
                else
                {
                    foreach (var c in convos)
                    {
                        string title = string.IsNullOrEmpty(c.Title) ? "(untitled)" : c.Title;
                        Console.WriteLine($"  #{c.Id,-6} {title,-40} [{c.Provider}/{c.Model}]  {c.UpdatedAt}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.Message}");
            }
            Console.WriteLine();
            continue;
        }

        // ── /new [title] ──
        if (line.StartsWith("/new", StringComparison.Ordinal))
        {
            string? newTitle = line[4..].Trim();
            try
            {
                conversationId = await store.CreateConversationAsync(provider, model ?? "(unset)", workdir, newTitle);
                titled = !string.IsNullOrEmpty(newTitle);
                loop = null;
                Console.WriteLine($"New conversation #{conversationId} created.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            Console.WriteLine();
            continue;
        }

        // ── /open <id> ──
        if (line.StartsWith("/open", StringComparison.Ordinal))
        {
            string idStr = line[5..].Trim();
            if (!long.TryParse(idStr, out long openId))
            {
                Console.WriteLine("Usage: /open <conversation_id>");
                Console.WriteLine();
                continue;
            }
            try
            {
                // Cross-provider guard: check the conversation's provider matches ours
                var convos = await store.ListConversationsAsync();
                var targetConvo = convos.FirstOrDefault(c => c.Id == openId);
                if (targetConvo is null)
                {
                    Console.WriteLine($"Conversation #{openId} not found.");
                    Console.WriteLine();
                    continue;
                }
                if (targetConvo.Provider != provider)
                {
                    Console.WriteLine($"Conversation #{openId} belongs to provider '{targetConvo.Provider}' — restart DevMX.Chat with --provider {targetConvo.Provider} to continue it.");
                    Console.WriteLine();
                    continue;
                }
                var history = await AgenticLoop.LoadHistoryAsync(store, openId);
                conversationId = openId;
                loop = BuildLoop(history);
                titled = true; // reopened convos are assumed titled
                Console.WriteLine($"Opened conversation #{openId} ({history.Count} messages loaded).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            Console.WriteLine();
            continue;
        }

        // ── Chat turn ──
        try
        {
            if (loop is null)
                loop = BuildLoop();

            // Lazy API-key check — AnthropicClient constructor throws if missing
            await loop.RunTurnAsync(
                line,
                onAssistantText: (text) => Console.Write(text),
                onToolCall: (name, argJson) =>
                {
                    string argTrunc = argJson.Length > 120 ? argJson[..120] + "…" : argJson;
                    Console.WriteLine($"[tool] {name}({argTrunc})");
                },
                CancellationToken.None);

            Console.WriteLine();

            // Auto-title untitled conversation after first successful turn
            if (!titled)
            {
                string autoTitle = line.Length > 48 ? line[..48].Trim() + "…" : line.Trim();
                if (!string.IsNullOrEmpty(autoTitle))
                {
                    await store.UpdateTitleAsync(conversationId, autoTitle);
                }
                titled = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine();
        }
    }
}
finally
{
    await mcp.DisposeAsync();
    await store.DisposeAsync();
}

Console.WriteLine("Goodbye.");
