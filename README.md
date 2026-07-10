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

### Environment variables
| Variable             | Required | Default                          | Description                        |
|----------------------|----------|----------------------------------|------------------------------------|
| `ANTHROPIC_API_KEY`  | Yes*     | *(none)*                         | Anthropic API key for LLM calls    |
| `DEVMX_MODEL`        | No       | `claude-sonnet-4-5`              | Anthropic model identifier         |

\* The REPL starts without the key but will error on the first chat turn if unset.

### Command-line arguments
| Argument     | Default                                              | Description                        |
|--------------|------------------------------------------------------|------------------------------------|
| `--server`   | `C:\Users\pkailas\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe` | Path to the MCP server executable |
| `--workdir`  | `C:\Users\pkailas\source\repos\DevMX`                | Working directory for the agent    |
| `--db`       | `%LOCALAPPDATA%\DevMX\devmx.db`                      | SQLite database path               |
| `--model`    | `$DEVMX_MODEL` or `claude-sonnet-4-5`                | Anthropic model to use             |

### REPL commands
| Command        | Description                                        |
|----------------|----------------------------------------------------|
| `/quit`        | Exit the REPL                                      |
| `/list`        | List conversations (newest first)                  |
| `/new [title]` | Start a new conversation (optionally with a title) |
| `/open <id>`   | Open an existing conversation by id                |
| `/help`        | Show available commands                            |

Any other input is treated as a chat turn — sent to the LLM with MCP tool access.
