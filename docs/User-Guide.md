# DevMX — User Guide

DevMX is a desktop chat client for agentic coding. You chat with a model of
your choice — local (zero cost) or Anthropic — and the model works on your code
through DevMind's MCP tool set: reading, searching, editing, running builds,
and delegating whole tasks to DevMind's headless agent. It's a companion that
sits beside Visual Studio, not an editor replacement.

Two front ends share the same core and conversation database:

- **DevMX.App** — the WPF desktop app (themes, syntax-highlighted code panes,
  clipboard image paste).
- **DevMX.Chat** — a console REPL with the same providers and persistence.

---

## Basic use

Type a message; anything that isn't a slash command is a chat turn, sent to the
LLM with MCP tool access. Give tasks the way you'd brief a colleague:

```
In OrderService.cs, the retry loop swallows the last exception.
Fix it to rethrow with the original stack, and build to verify.
```

The model uses DevMind tools to read and change files in the current working
directory, and can delegate bigger jobs to the DevMind headless agent
(`devmind_task_*`), which runs them on your local GPU model. The app surfaces
job states — including `needs_input` and `stopped_incomplete` — so you know
when a delegated task needs a decision or came back untrustworthy.

## Conversations

Conversations persist in a local SQLite database
(`%LOCALAPPDATA%\DevMX\devmx.db` by default) and survive restarts.

**Conversations are provider-scoped.** Each conversation is tied to the
provider that created it; you cannot open an Anthropic conversation while
running with `--provider openai`, or vice versa — history is stored in the
provider's native wire format, and cross-provider resume would send incorrect
request shapes. DevMX refuses with a clear message telling you which provider
to restart with.

## Commands — DevMX.App

| Command | Description |
|---|---|
| `/help` | Show the command list |
| `/dir [path] [-b]` | Show or change working directory (`-b` opens a folder picker) |
| `/new` | Start a new conversation |
| `/open <id>` | Open a conversation by ID |
| `/search <term>` | Search conversations |
| `/theme dark\|light` | Switch theme |
| `/poll <n>` | Set poll throttle (0–60 seconds) for delegated-task status |
| `/profile auto\|full\|restricted` | Set the tool access profile |
| `/handoff` | Write a handoff `.md` of this conversation so a fresh conversation can pick up the work |

## Commands — DevMX.Chat (REPL)

| Command | Description |
|---|---|
| `/list` | List conversations (newest first) |
| `/new [title]` | Start a new conversation (optionally titled) |
| `/open <id>` | Open an existing conversation by id |
| `/help` | Show available commands |
| `/quit` | Exit the REPL |

## Providers and cost

- **Local (default)** — `--provider openai` against DevMind's local server
  (`http://127.0.0.1:8080/v1`): no API key, zero per-token cost. The model is
  auto-discovered from the endpoint's `/models` listing unless you pass
  `--model` or set `DEVMX_MODEL`.
- **Anthropic** — `--provider anthropic` with `ANTHROPIC_API_KEY`: Claude
  plans and reviews with frontier quality; heavy mechanical work can still be
  delegated to the local DevMind agent so tokens go to judgment, not typing.

## The /handoff pattern

Long conversations eventually outgrow their context. `/handoff` writes a
markdown summary of the conversation — what was done, what's in flight, what's
next — designed to be pasted into a fresh conversation (`/new`, then open with
the handoff file) so work continues without dragging the full history along.

## Tips

- Set the working directory (`/dir`) before asking for code changes — it is
  the agent's sandbox.
- Use `/profile restricted` when you want lookups without edits;
  `full` when you trust the session; `auto` to let DevMX choose.
- Delegated DevMind jobs run one at a time on the local GPU. `/poll` controls
  how often DevMX checks status — raise it if the chatter is distracting.
- Review a delegated task's action journal before building on it, exactly as
  you would a junior developer's PR.

---

*DevMX is a product of iOnline Consulting LLC.*
