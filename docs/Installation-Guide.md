# DevMX — Installation Guide

DevMX is a model-agnostic desktop chat client with DevMind as its agent
backend. It is not an editor — it's a Cursor-agent-style companion that sits
beside Visual Studio. It ships two front ends over a shared core:

- **DevMX.App** — the WPF desktop application (Windows).
- **DevMX.Chat** — an interactive console REPL (useful for quick sessions and
  for environments without the desktop app).

There is currently **no packaged installer** for DevMX — you build it from
source. This takes about two minutes.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10/11 | DevMX.App is WPF (`net10.0-windows`). DevMX.Chat is a console app. |
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10.0 |
| A DevMind install | DevMX launches and drives `DevMind.McpServer.exe` as its agent backend. Install DevMind first — see the *DevMind TUI — Installation Guide*. The default server path DevMX probes is `<DevMind repo>\dist\mcp\DevMind.McpServer.exe`; pass `--server` if yours lives elsewhere. |
| An LLM provider | Either a local OpenAI-compatible endpoint (zero cost — the default points at DevMind's local server on `http://127.0.0.1:8080/v1`) or an Anthropic API key. For a complete, known-good local stack (llama.cpp model servers, embeddings, search, vector store), see [DevMind's LLM Server & Services Setup Guide](https://github.com/pkailas/DevMind/blob/master/docs/LLM-Server-Setup-Guide.md). |
| Access to the DevMX repository | Clone access to the DevMX repo. |

---

## Build and run

```powershell
git clone <devmx-repo-url> C:\Users\<you>\source\repos\DevMX
cd C:\Users\<you>\source\repos\DevMX

# Desktop app
dotnet run --project src/DevMX.App

# — or the console REPL —
dotnet run --project src/DevMX.Chat
```

For a runnable build you can pin to, publish instead:

```powershell
dotnet publish src/DevMX.App -c Release -o dist\app
```

and launch `dist\app\DevMX.App.exe`.

---

## Configuration

### Providers

DevMX supports two provider backends:

- **`openai`** (default) — any OpenAI-compatible API, including DevMind's
  local server at `http://127.0.0.1:8080/v1`. No API key needed for local use;
  set `OPENAI_COMPAT_API_KEY` for remote endpoints that require auth.
- **`anthropic`** — the Anthropic Messages API. Requires `ANTHROPIC_API_KEY`.

### Environment variables

| Variable | Required | Description |
|---|---|---|
| `ANTHROPIC_API_KEY` | For `--provider anthropic` | Anthropic API key. The REPL starts without it but errors on the first chat turn if unset. |
| `OPENAI_COMPAT_API_KEY` | No | API key for OpenAI-compatible endpoints (local servers usually need none). |
| `DEVMX_MODEL` | No | Model identifier. Default: `claude-sonnet-4-5` (anthropic) or auto-discovered via `GET <endpoint>/models` (openai). |

### Command-line arguments (DevMX.Chat)

| Argument | Default | Description |
|---|---|---|
| `--server` | `<DevMind repo>\dist\mcp\DevMind.McpServer.exe` | Path to the DevMind MCP server executable |
| `--workdir` | the DevMX repo | Working directory for the agent |
| `--db` | `%LOCALAPPDATA%\DevMX\devmx.db` | SQLite conversation database |
| `--model` | `$DEVMX_MODEL` or auto-discovered | Model identifier |
| `--provider` | `openai` | `openai` or `anthropic` |
| `--endpoint` | `http://127.0.0.1:8080/v1` | Base URL for the OpenAI-compatible API (ignored for anthropic) |

### Zero-cost local default

The out-of-the-box configuration (`--provider openai`, default endpoint) uses
DevMind's local model server on port 8080: no API key, no per-token cost.
Just make sure your local model server is running before you start chatting.

---

## Verifying the install

1. Start your local model server (or export `ANTHROPIC_API_KEY` and plan to
   run with `--provider anthropic`).
2. `dotnet run --project src/DevMX.Chat`
3. The banner should show the model (auto-discovered for openai). Type `/help`
   to see commands, then send a message — the reply confirms the provider is
   wired. If the agent tools work (`ask it to list files in the workdir`), the
   DevMind MCP server path is correct too.

---

*DevMX is a product of iOnline Consulting LLC.*
