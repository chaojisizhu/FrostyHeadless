English | [简体中文](README.zh-CN.md)

# FrostyHeadless

**Headless, strictly read-only query layer for [Frosty Toolsuite](https://github.com/CadeEvs/FrostyToolsuite)** — browse, inspect and export Frostbite engine game assets as JSON/XML from the command line or a local HTTP API, without ever launching the Frosty GUI.

Built for scripting, batch analysis and AI-agent workflows: point an agent at `http://127.0.0.1:18765` and let it search 100k+ assets, walk dependency graphs and pull object graphs in milliseconds.

> 无头只读版寒霜编辑器：双击启动一个终端选好游戏后，本机暴露 HTTP 查询接口给脚本或 AI 调用；绝不写入游戏目录。（代码内注释为中文）

---

## Highlights

- **Interface-first resident server** — the HTTP API is listening within ~1s of launch, *before* any game is picked. A human can choose a game in the terminal, or an AI agent can load what it needs itself via `GET /load?profile=<name>` — whoever comes first wins. Queries answer in **single-digit milliseconds** once loaded (a one-shot CLI process pays the full cache-load cost on every invocation).
- **Interactive game picker** — on startup the terminal lists every profile your Frosty install supports (25 games for 1.0.6.3); games you have already configured in Frosty are sorted first and tagged `[已配置]`, Enter picks the default.
- **Rich asset views**
  - `ebx get` — full object graph as JSON: every instance with `$type`/`$guid`, fields in declaration order, `PointerRef`/`ResourceRef` resolved to readable asset names and class names, cycle-safe deep expansion.
  - `ebx xml` — XML export produced by the same `EbxXmlWriter` the editor's *EbxToXml* plugin uses.
  - `ebx deps` — dependencies **and** reverse-dependencies (who references this asset).
  - bundle listings and per-bundle contents (`ebx`/`res`/`chunk` counts + names).
- **Strictly read-only** — see the [guarantee](#read-only-guarantee) below; verified by before/after game-directory snapshots.
- **Single small exe, no bundled binaries** — binds against *your* Frosty Toolsuite installation at run time; nothing from Frosty or the games is redistributed here.

## Read-only guarantee

Three independent layers keep your game files untouched:

1. **Code level** — the entire codebase contains no calls to any mutating Frosty API (`ModifyEbx` / `AddEbx` / `RevertAsset` / `FrostyProject` / `FrostyModWriter` …) and never calls `Config.Save`. Queries are built exclusively from `Enumerate*` / `Get*` / `EbxXmlWriter`.
2. **Path guard** — the working directory is pinned to the Frosty install dir and verified (via physical-path canonicalization using `CreateFileW` + `GetFinalPathNameByHandle`, which resolves junctions, symlinks, subst drives and 8.3 short names) to never sit inside the game directory or any `steamapps` tree. The one CLI flag that writes a file (`ebx xml --out`) validates the destination's *physical* path against the same blacklist and fails closed (exit code 4) on anything it cannot prove safe.
3. **Server mode writes nothing at all** — XML is returned as an HTTP response body; there is no file-writing endpoint. The only writes the tool can ever perform are the Frosty data cache (in the install dir, identical to the GUI editor's behavior) and explicitly requested `--out` files.

During development this was verified end-to-end: recursive before/after snapshots of the game directory (file list + sizes + timestamps) showed zero differences after full query runs.

## Requirements

- Windows x64
- A **Frosty Toolsuite 1.0.6.x installation** (not included — get it from its [releases page](https://github.com/CadeEvs/FrostyToolsuite/releases)) with at least one game configured (the game's `GamePath` in `%LOCALAPPDATA%\Frosty\editor_config.json`)
- .NET Framework 4.8 targeting pack + [.NET SDK](https://dotnet.microsoft.com/download) (for building)

## Layout and build

The project expects your Frosty install (the folder containing `FrostySdk.dll`) as a sibling folder named `FrostyEditor`, i.e.:

```
parent/
├── FrostyEditor/          ← your Frosty Toolsuite install (any name works if you pass --frosty)
└── FrostyHeadless/        ← this repository
    ├── FrostyHeadless/    ← the project (sources + csproj)
    └── start-server.bat
```

Build:

```
dotnet build FrostyHeadless/FrostyHeadless.csproj -c Release
```

Output: `FrostyHeadless/bin/Release/net48/FrostyHeadless.exe`.

At run time the tool locates the Frosty install by walking up from the exe (also probing sibling `FrostyEditor/` folders); override with `--frosty <dir>` in any command.

## Usage — resident server (recommended)

Double-click `start-server.bat` (or run `FrostyHeadless.exe serve [--profile <name>] [--port <18765>]`).

The API starts listening immediately in `unloaded` state — `/status` reports the state and `/profiles` already works. From there, either side can pick the game:

- **Human**: the terminal shows the interactive picker (configured games first); for an unconfigured game it prompts for the install path.
- **AI agent**: `GET /load?profile=bf1` loads a game synchronously (≈1–2s with a warm cache; add `&gamepath=<dir>` for a game that was never configured in Frosty). Loading the same game again is a no-op; loading a different one into the same process returns an error (one game per process — restart to switch).

Game-data endpoints return **409** with a hint until a game is loaded. After loading, the terminal prints the endpoint cheat-sheet and logs every request. **Keep the window open to keep the server online**; close it (or Ctrl+C) to stop.

The interactive picker (shown in the terminal when no `--profile` is passed):

```
=== FrostyHeadless 只读服务器：选择要加载的游戏（接口已开放，也可由 AI 调 /load 加载）===
   1. bf1  (Battlefield™ 1)  [已配置]
   2. Anthem  (Anthem™)  [需要密钥]
   3. bf4  (Battlefield™ 4)
   ...
输入序号或 profile 名（回车 = bf1，留空等待 AI 调用: ）:
```

Request log (one line per call, in the terminal):

```
[21:27:56] GET /status -> 200 (4 ms) #1
[21:28:10] GET /load?profile=bf1 -> 200 (1617 ms) #4
[21:28:12] GET /ebx/search?filter=m1911&limit=3 -> 200 (39 ms) #5
```

### HTTP API (all GET, root `http://127.0.0.1:<port>/`)

| Endpoint | Description |
| --- | --- |
| `/status` | Server status: `state` (unloaded/loaded), loaded profile, game path, asset counts, requests served |
| `/profiles` | All profiles supported by the install (with configured/key flags) |
| `/load?profile=<name>[&gamepath=<dir>]` | Load a game into the running server (synchronous, ≈1–2s warm; idempotent for the same game) |
| `/ebx/search?filter=&type=&limit=&offset=` | Case-insensitive substring search; `type` matches base types incl. subclasses; paginated |
| `/ebx/get?name=<asset>` | Full object graph as JSON |
| `/ebx/xml?name=<asset>` | Same asset as XML (`application/xml` response body) |
| `/ebx/deps?name=<asset>` | Dependencies + reverse dependencies |
| `/bundles/list?filter=&limit=` | Bundle search |
| `/bundle/info?name=<bundle>` | Bundle detail: `ebx`/`res`/`chunk` counts + first 2000 ebx names |
| `/res/search?filter=&type=&limit=&offset=` | res asset (texture/mesh/etc.) metadata search by name and resource type |

Examples:

```
curl "http://127.0.0.1:18765/ebx/search?filter=weapon&limit=20&offset=40"
curl "http://127.0.0.1:18765/ebx/get?name=Gameplay/Weapons/SideArms/ColtM1911/T_1911_3P_CS"
curl "http://127.0.0.1:18765/ebx/xml?name=Gameplay/Weapons/SideArms/ColtM1911/T_1911_3P_CS" -o asset.xml
curl "http://127.0.0.1:18765/bundle/info?name=win32/gameconfigurations/warsaw"
```

Errors return a JSON body `{"error":{"code":..,"message":..}}` with HTTP 400 (bad parameter), 404 (unknown asset/bundle/path) or 500.

## Usage — one-shot CLI

Every command is also available as a standalone process (loads the cache each time, so prefer the server for repeated queries):

```
FrostyHeadless profiles list
FrostyHeadless ebx search --profile bf1 --filter weapon --limit 10
FrostyHeadless ebx get    --profile bf1 --name <asset>
FrostyHeadless ebx xml    --profile bf1 --name <asset> [--out <file>]
FrostyHeadless ebx deps   --profile bf1 --name <asset>
FrostyHeadless bundles list --profile bf1 --filter win32 --limit 5
FrostyHeadless bundle info  --profile bf1 --name <bundle>
FrostyHeadless res search   --profile bf1 --filter <substring> [--type Texture] [--limit 20]
```

Global options: `--frosty <dir>` (install dir), `--gamepath <dir>` (one-shot override, never written back), `--verbose`.

Exit codes: `0` ok · `1` usage error · `2` not found · `3` boot/config failure · `4` read-only guard rejection · `5` unhandled exception.

## Limitations

- One game per server process (the per-game type SDK is emitted at boot) — restart and pick another game to switch.
- `res` / `chunk` binary export is not implemented (ebx query/export/analysis only).
- Profiles that require an encryption key (`RequiresKey`, e.g. Battlefront II) are not supported yet.
- After a game patch, the first launch rebuilds the data cache inside the Frosty install dir (same as the GUI editor); this can take minutes.

## Legal

This is an unofficial community tool and is not affiliated with EA, DICE or the Frosty Toolsuite authors. The repository contains **no EA/Frosty code, binaries or game assets** — it only references the assemblies of a Frosty Toolsuite installation you supply, and [Frosty Toolsuite itself is licensed CC BY-NC-ND 4.0](https://github.com/CadeEvs/FrostyToolsuite). Use it only with games and data you own.

## License

The code in this repository is released under the [MIT License](LICENSE).
