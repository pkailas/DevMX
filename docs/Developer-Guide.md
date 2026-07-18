# DevMX — Developer's Guide

DevMX is a model-agnostic desktop chat client that drives the DevMind MCP
server as its agent backend. This guide covers the solution layout and the
architectural decisions a contributor needs.

- **Runtime**: .NET 10 (`net10.0`; the WPF app targets `net10.0-windows`)
- **Repo**: separate from DevMind — DevMX consumes DevMind's MCP server as an
  external process, not as a project reference.

---

## Solution layout

| Project | Purpose |
|---|---|
| `src/DevMX.Core` | Shared core: the .NET MCP **client** (`DevMxMcpClient`), provider router (OpenAI-compatible + Anthropic), agentic loop, SQLite persistence. |
| `src/DevMX.App` | WPF desktop app (`net10.0-windows`, AvalonEdit for code rendering, theme manager). View layer only. |
| `src/DevMX.App.ViewModels` | MVVM view models — testable, WPF-free (`MainViewModel`, `SlashCommandHandler`, `DevMxSettings`). |
| `src/DevMX.Chat` | Interactive console REPL — same core, same database. |
| `src/DevMX.Spike` | Phase 0 console spike that proved a .NET MCP client can drive the DevMind MCP server end-to-end. Historical; keep buildable, don't extend. |
| `tests/DevMX.Core.Tests` | Core unit tests. |
| `tests/DevMX.App.ViewModels.Tests` | View-model tests (settings, command handling). |

Design doc: *DevMX-Design-Doc* (Google Drive).

## Architecture

```
┌───────────────────────────────┐
│ DevMX.App (WPF)  /  DevMX.Chat│   thin front ends
│      └── DevMX.App.ViewModels │   (App only — MVVM, no WPF types)
├───────────────────────────────┤
│ DevMX.Core                    │
│   AgenticLoop                 │   drives provider turns + tool dispatch
│   Provider router             │   openai-compatible | anthropic
│   DevMxMcpClient ── stdio ────┼──► DevMind.McpServer.exe (external process)
│   SQLite persistence          │   %LOCALAPPDATA%\DevMX\devmx.db
└───────────────────────────────┘
```

Key decisions:

- **DevMind stays an external process.** DevMX spawns
  `DevMind.McpServer.exe` (path via `--server`) and speaks MCP over stdio.
  This keeps the two products independently deployable and versioned.
- **Provider-scoped conversations.** Conversation history is persisted in the
  provider's native wire format. Cross-provider resume is refused by design —
  replaying Anthropic-shaped messages at an OpenAI endpoint (or vice versa)
  would send incorrect request shapes. Don't "fix" this with translation
  unless you're prepared to own lossy mapping of tool-use blocks.
- **View models are WPF-free.** `DevMX.App.ViewModels` must not reference WPF
  types; UI marshaling happens through injected dispatch delegates (see
  `MainViewModel`'s `_dispatch`). This is what keeps them unit-testable.
- **Delegated-job awareness.** The agentic loop understands DevMind job states
  — including `needs_input` and `stopped_incomplete` — and surfaces them to
  the UI rather than blindly treating any finished job as success.
- **/handoff** flattens conversation history to readable text and has the
  model write a continuation brief (no tools) so a fresh conversation can pick
  up the work — the escape hatch for context-window growth.

## Front-end responsibilities

- `DevMX.App` — XAML, theming (`ThemeManager`, `Themes/`), AvalonEdit
  integration (`AvalonEditBehavior`), clipboard/image paste. Logic beyond
  view plumbing belongs in ViewModels or Core.
- `SlashCommandHandler` (ViewModels) — App-side command dispatch (`/dir`,
  `/new`, `/open`, `/search`, `/theme`, `/poll`, `/profile`, `/handoff`).
- `DevMX.Chat/Program.cs` — REPL loop and its smaller command set (`/list`,
  `/new`, `/open`, `/quit`, `/help`).

## Build & test

```powershell
dotnet build
dotnet test

dotnet run --project src/DevMX.App     # desktop app
dotnet run --project src/DevMX.Chat    # REPL
```

Publish the app for a pinned local build:

```powershell
dotnet publish src/DevMX.App -c Release -o dist\app
```

## Working on DevMX with DevMind in the loop

When testing against a live DevMind MCP server, remember the coordination
rules from the DevMind side: one delegated job at a time, don't edit files
under a `working_dir` with a running job, and treat DM-changed files as
externally modified. For provider work, the FakeSseServer-style scripted
testing pattern from DevMind.Core applies equally well here — script the SSE
stream, assert the loop's behavior, keep real endpoints out of unit tests.

---

*DevMX is a product of iOnline Consulting LLC.*
