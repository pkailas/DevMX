# DevMX

Model-agnostic desktop chat client with DevMind as agent backend. Not an editor — a Cursor-agent-style companion that sits beside Visual Studio.

Phase 0: headless console spike proving a .NET MCP client can drive the DevMind MCP server end-to-end.

Design doc: see DevMX-Design-Doc (Google Drive).

## Layout
- `src/DevMX.Core` — shared core (MCP client, provider router, persistence)
- `src/DevMX.Spike` — Phase 0 console spike
- `tests/DevMX.Core.Tests` — unit tests
