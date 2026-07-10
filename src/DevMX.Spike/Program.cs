using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevMX.Core;

// ── CLI parsing ──────────────────────────────────────────────────────────────

string serverPath = @"C:\Users\pkailas\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe";
string workdir = @"C:\Users\pkailas\source\repos\DevMX";
bool doDelegate = false;

var parsedArgs = args.ToList();
for (int i = 0; i < parsedArgs.Count; i++)
{
    if (parsedArgs[i] == "--server" && i + 1 < parsedArgs.Count)
        serverPath = parsedArgs[++i];
    else if (parsedArgs[i] == "--workdir" && i + 1 < parsedArgs.Count)
        workdir = parsedArgs[++i];
    else if (parsedArgs[i] == "--delegate")
        doDelegate = true;
}

await RunAsync(serverPath, workdir, doDelegate, CancellationToken.None);

// ── Harness logic ────────────────────────────────────────────────────────────

static async Task RunAsync(string serverPath, string workdir, bool doDelegate, CancellationToken ct)
{
    Console.WriteLine($"[SPIKE] Server: {serverPath}");
    Console.WriteLine($"[SPIKE] Workdir: {workdir}");
    Console.WriteLine();

    await using var client = await DevMxMcpClient.StartAsync(serverPath, workdir, ct);

    // ── a) List tools ─────────────────────────────────────────────────────
    Console.WriteLine("=== LIST TOOLS ===");
    var tools = await client.ListToolsAsync(ct);
    Console.WriteLine($"Tool count: {tools.Count}");
    foreach (var tool in tools.OrderBy(t => t.Name))
    {
        Console.WriteLine($"  - {tool.Name}");
    }
    Console.WriteLine();

    var toolNames = tools.Select(t => t.Name).ToHashSet();
    var missing = new List<string>();
    if (!toolNames.Contains("read_file")) missing.Add("read_file");
    if (!toolNames.Contains("devmind_task_start")) missing.Add("devmind_task_start");

    if (missing.Count > 0)
    {
        Console.WriteLine($"[FAIL] Missing required tools: {string.Join(", ", missing)}");
        Environment.Exit(1);
        return;
    }
    Console.WriteLine("[OK] Required tools present: read_file, devmind_task_start");
    Console.WriteLine();

    // ── b) read_file on README.md ─────────────────────────────────────────
    Console.WriteLine("=== READ_FILE (README.md) ===");
    var readmePath = Path.Combine(workdir, "README.md");
    var readFileResult = await client.CallToolAsync(
        "read_file",
        new Dictionary<string, object?> { { "filename", readmePath } },
        ct);

    // Print first ~5 lines
    var lines = readFileResult.Split('\n');
    int printCount = Math.Min(5, lines.Length);
    for (int i = 0; i < printCount; i++)
    {
        Console.WriteLine(lines[i]);
    }
    if (lines.Length > printCount)
    {
        Console.WriteLine($"  ... ({lines.Length - printCount} more lines)");
    }
    Console.WriteLine();

    if (readFileResult.Contains("[read_file error]") || readFileResult.Contains("file not found", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[FAIL] read_file round-trip failed");
        Environment.Exit(1);
        return;
    }
    Console.WriteLine("[OK] read_file round-trip succeeded");
    Console.WriteLine();

    // ── c) P0 PIPE OK ────────────────────────────────────────────────────
    Console.WriteLine("P0 PIPE OK");

    // ── --delegate mode ──────────────────────────────────────────────────
    if (!doDelegate)
        return;

    Console.WriteLine();
    Console.WriteLine("=== DELEGATE MODE ===");

    // Start a trivial task
    var taskStartResult = await client.CallToolAsync(
        "devmind_task_start",
        new Dictionary<string, object?>
        {
            { "prompt", "create a file named p0-probe.txt in the working dir containing the single line: p0" },
            { "working_dir", workdir },
            { "max_depth", 10 },
            { "timeout_minutes", 10 },
        },
        ct);

    Console.WriteLine($"Task start result: {taskStartResult}");

    // Parse job_id from JSON result
    string? jobId = null;
    try
    {
        using var doc = JsonDocument.Parse(taskStartResult);
        jobId = doc.RootElement.GetProperty("job_id").GetString();
    }
    catch
    {
        Console.WriteLine("[FAIL] Could not parse job_id from task start result");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine($"Job ID: {jobId}");
    Console.WriteLine("Polling task status every 5s (max 10 min)...");

    var deadline = DateTimeOffset.Now.AddMinutes(10);
    string? finalState = null;

    while (DateTimeOffset.Now < deadline)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var statusResult = await client.CallToolAsync(
            "devmind_task_status",
            new Dictionary<string, object?> { { "job_id", jobId } },
            ct);

        Console.WriteLine($"  Status: {statusResult}");

        // Parse state
        try
        {
            using var doc = JsonDocument.Parse(statusResult);
            var state = doc.RootElement.GetProperty("state").GetString();
            finalState = state;
            if (state == "done" || state == "failed" || state == "cancelled")
                break;
        }
        catch
        {
            // If we can't parse, keep polling
        }
    }

    if (finalState == null)
    {
        Console.WriteLine("[WARN] Timed out waiting for task completion");
    }
    else
    {
        Console.WriteLine($"Final state: {finalState}");
        if (finalState == "done")
        {
            Console.WriteLine("[OK] Delegate task completed successfully");
        }
        else
        {
            Console.WriteLine($"[WARN] Task ended with state: {finalState}");
        }
    }
}
