# AGENTS.md — Self-setup guide for AI agents

You are reading this because you are an AI agent that wants to query Frostbite game data
(via Frosty Toolsuite) headlessly. This file tells you how to check, start, configure and
call **FrostyHeadless** on this machine, by yourself.

Everything is **strictly read-only**: the tool cannot modify game files, and neither should you.

## 1. What you get

A local HTTP JSON API (default `http://127.0.0.1:18765`) over a loaded Frostbite game:
search 100k+ ebx assets, pull full object graphs as JSON/XML, walk dependency graphs,
list bundle contents, and export textures as PNG/DDS. Millisecond latency once loaded.

## 2. Is the server already running?

```bash
curl -s http://127.0.0.1:18765/status
```

- `{"state": "loaded", "profile": ...}` → ready to use, skip to §5.
- `{"state": "unloaded", ...}` → running but no game loaded; call `/load` (§4).
- connection refused / 502 → not running; start it (§3).

If you connected via the **MCP wrapper** (`mcp/frosty-mcp.mjs`), none of this matters: any tool
call auto-starts the server when it is down, and `frosty_server_stop` shuts it down when you are
done. Recommended workflow: work → `frosty_server_stop` → done. The server holds no state worth
keeping (caches live on disk), so stop/start cycles are free.

## 3. Start the server yourself (no human needed)

Prerequisites on disk (workspace-relative):

```
FrostyEditor/                          ← Frosty Toolsuite 1.0.6.x install (contains FrostySdk.dll)
FrostyHeadless/bin/Release/net48/FrostyHeadless.exe
```

If the exe is missing, build it first: `dotnet build FrostyHeadless/FrostyHeadless.csproj -c Release`
(needs .NET Framework 4.8 targeting pack + dotnet SDK).

Start it as a detached background process — it does not need a console or any input:

```bash
# from the workspace root (the folder containing FrostyEditor/)
FrostyHeadless/bin/Release/net48/FrostyHeadless.exe serve --port 18765 < /dev/null &
```

It listens within ~1s in `unloaded` state (stdin closed is fine — that just means
"nobody will pick a game in a terminal"; you pick via `/load` instead).
A human can also double-click `start-server.bat` — same server.

## 4. Load a game

```bash
curl -s "http://127.0.0.1:18765/profiles"          # list games; pick one with configured=true
curl -s "http://127.0.0.1:18765/load?profile=bf1"  # ~1-2s warm; idempotent for the same game
```

- For a game with `configured=false`, append `&gamepath=<install dir>` (never written back).
- One game per server process; loading a second game returns an error — restart the process to switch.
- Games with `requiresKey=true` are NOT supported; pick another.

## 5. API quick reference (all GET, JSON unless noted)

| Endpoint | Use |
| --- | --- |
| `/status` | server/game state |
| `/profiles` | supported games |
| `/ebx/search?filter=&type=&limit=&offset=` | asset search (case-insensitive substring) |
| `/ebx/get?name=` or `?guid=` | full object graph (references resolved) |
| `/ebx/xml?name=` | XML form (text/xml) |
| `/ebx/deps?name=` | dependencies + reverse dependencies |
| `/bundles/list?filter=&limit=` · `/bundle/info?name=` | bundles |
| `/res/search?filter=&type=` | texture/mesh/sound resource metadata search |
| `/res/get?name=` or `?rid=` | one resource's metadata (textures: size/format/mips) |
| `/res/export?name=&format=png\|dds\|raw` | binary body: PNG/DDS for textures, raw for others |
| `/types/schema?name=` | ebx type field schema |
| `/shutdown` | stop the server when you are done (graceful; next call restarts it) |

Errors are `{"error":{"code","message"}}` with 400 (bad args) / 404 (not found) / 409 (no game loaded).

## 6. MCP (for clients that speak it)

`mcp/frosty-mcp.mjs` wraps the API as MCP tools (`frosty_*`, 15 tools; textures come back
as inline images; `frosty_server_start` / `frosty_server_stop` manage the server lifecycle,
and every tool auto-starts the server if it is down). Zero dependencies, Node 18+.
To self-register (Claude Code style), write a project `.mcp.json`:

```json
{
  "mcpServers": {
    "frosty": {
      "command": "node",
      "args": ["<ABSOLUTE_PATH>/mcp/frosty-mcp.mjs"],
      "env": { "FROSTY_URL": "http://127.0.0.1:18765" }
    }
  }
}
```

## 7. Hard rules

1. Never write anything into the game directory or any `steamapps` tree — including exports.
   Save exported files under your workspace (the C# side enforces this for CLI `--out`,
   and the server itself performs zero file writes).
2. The tool is read-only by design: there are no endpoints that modify game data. Don't try to
   build one out of side effects.
3. If the process dies, just restart it (§3) — caches live in the Frosty install dir and survive.

## 8. Troubleshooting

| Symptom | Fix |
| --- | --- |
| connection refused | server not running → §3 |
| 409 "尚未加载游戏" | call `/load?profile=...` |
| `未知 profile` (404) on `/load` | check `/profiles` for the exact name |
| slow first `/load` after a game patch | cache rebuild (minutes) — wait, don't retry-storm |
| exe missing / build errors | ensure FrostyEditor/ sits next to FrostyHeadless/ and dotnet SDK exists |
