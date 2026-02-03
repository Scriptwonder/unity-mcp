# MCP for Unity — Editor Plugin Guide (CLI)

Use this guide to run MCP for Unity inside the Unity Editor with the CLI bridge. Installation is covered elsewhere; this document focuses on the Editor window and connection workflow.

## Open the window
- Unity menu: Window > MCP for Unity

The window has four areas: Connect, Tools, Scripts, and Advanced.

---

## Quick start (CLI)
1. Open Window > MCP for Unity.
2. Start the CLI bridge server:
   - Terminal: `unity-mcp server start`
   - or click “Start Server” in the Connect tab.
3. Unity will auto-connect to a local server when HTTP scope is Local (default).
   - If not connected, click “Start Session”.
4. Run CLI commands from your terminal (see `docs/guides/CLI_USAGE.md`).

---

## Connect (Server)
- Server Scope: Local or Remote.
- HTTP URL: Base URL for the CLI bridge server.
- Local Server: Start/Stop the server when using Local scope.
- Session: Start/End the Unity ↔ server session.
- Manual Server Launch: Copy the exact `uvx` command used to launch the server.

---

## Tools
- View and manage custom tools exposed to the CLI bridge.
- Toggle tool availability and refresh registration.

---

## Scripts
- Validation Level options:
  - Basic — Only syntax checks
  - Standard — Syntax + Unity practices
  - Comprehensive — All checks + semantic analysis
  - Strict — Full semantic validation (requires Roslyn)

---

## Advanced
- UV path override
- Server source override (Git URL)
- Debug logs toggle
- Dev-mode refresh flags for `uvx`
- Package deployment helpers
- Test connection button

---

## Troubleshooting
- Local server won’t start: Install `uv` or set the UV path override in Advanced.
- Unity won’t connect: Verify the HTTP URL and confirm the server is running.
