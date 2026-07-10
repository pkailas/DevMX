# DevMX

Model-agnostic desktop chat client with DevMind as agent backend. Not an editor — a Cursor-agent-style companion that sits beside Visual Studio.

Phase 0: headless console spike proving a .NET MCP client can drive the DevMind MCP server end-to-end.

Design doc: see DevMX-Design-Doc (Google Drive).

## Layout
- `src/DevMX.Core` — shared core (MCP client, provider router, persistence)
- `src/DevMX.Chat` — Phase 1 chat REPL (interactive console client)
- `src/DevMX.Spike` — Phase 0 console spike
- `tests/DevMX.Core.Tests` — unit tests

## Running the chat client

```bash
dotnet run --project src/DevMX.Chat
```

### Providers

DevMX.Chat supports two LLM provider backends:

- **`openai`** (default) — OpenAI-compatible API (works with any OpenAI-compatible endpoint, including DevMind's local server at `http://127.0.0.1:8080/v1`). No API key is required for local use; set `OPENAI_COMPAT_API_KEY` for remote endpoints that require authentication.
- **`anthropic`** — Anthropic Messages API. Requires `ANTHROPIC_API_KEY`.

### Environment variables
| Variable              | Required | Default                              | Description                                    |
|-----------------------|----------|--------------------------------------|------------------------------------------------|
| `ANTHROPIC_API_KEY`   | Yes*     | *(none)*                             | Anthropic API key (required for `--provider anthropic`) |
| `OPENAI_COMPAT_API_KEY` | No     | *(none — no auth for local server)*  | API key for OpenAI-compatible endpoints        |
| `DEVMX_MODEL`         | No       | `claude-sonnet-4-5` (anthropic) / auto-discovered (openai) | Model identifier |

\* The REPL starts without the key but will error on the first chat turn if unset.

When using the `openai` provider without `--model` or `DEVMX_MODEL`, DevMX.Chat will attempt to auto-discover the model by calling `GET <endpoint>/models` (the OpenAI list-models endpoint). If this succeeds, the first model returned is used and printed as `Model auto-discovered: <id>`. If it fails, the banner shows `model: (unset — pass --model)` and the first chat turn will error with a clear message.

### Command-line arguments
| Argument      | Default                                              | Description                                    |
|---------------|------------------------------------------------------|------------------------------------------------|
| `--server`    | `C:\Users\pkailas\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe` | Path to the MCP server executable |
| `--workdir`   | `C:\Users\pkailas\source\repos\DevMX`                | Working directory for the agent                |
| `--db`        | `%LOCALAPPDATA%\DevMX\devmx.db`                      | SQLite database path                           |
| `--model`     | `$DEVMX_MODEL` or auto-discovered (openai)           | Model identifier                               |
| `--provider`  | `openai`                                             | LLM provider: `openai` or `anthropic`          |
| `--endpoint`  | `http://127.0.0.1:8080/v1` (openai) / ignored (anthropic) | Base URL for the OpenAI-compatible API |

### Zero-cost local default

The default configuration (`--provider openai` with the default endpoint) points to DevMind's local server at `http://127.0.0.1:8080/v1`. This requires no API key and costs nothing to run. Simply ensure your DevMind MCP server is running on port 8080.

### Conversations are provider-scoped

Each conversation is tied to the provider that created it. You cannot open an Anthropic conversation while running with `--provider openai`, or vice versa. Attempting to do so with `/open` will print a refusal message:

```
Conversation #N belongs to provider 'X' — restart DevMX.Chat with --provider X to continue it.
```

This is intentional: the message history is stored in the provider's native wire format, and cross-provider resume would send incorrect request shapes.

### REPL commands
| Command        | Description                                        |
|----------------|----------------------------------------------------|
| `/quit`        | Exit the REPL                                      |
| `/list`        | List conversations (newest first)                  |
| `/new [title]` | Start a new conversation (optionally with a title) |
| `/open <id>`   | Open an existing conversation by id                |
| `/help`        | Show available commands                            |

Any other input is treated as a chat turn — sent to the LLM with MCP tool access.
