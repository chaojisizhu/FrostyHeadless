[English](README.md) | 简体中文

# FrostyHeadless

**[Frosty Toolsuite](https://github.com/CadeEvs/FrostyToolsuite) 的无头、严格只读查询层** —— 不启动寒霜编辑器 GUI，通过命令行或本机 HTTP API 以 JSON/XML 浏览、检视、导出 Frostbite 引擎的游戏资产。

为脚本、批量分析和 AI agent 工作流而生：让 agent 指向 `http://127.0.0.1:18765`，它就能毫秒级检索十万余条资产、遍历依赖图、拉取完整对象树。

---

## 亮点

- **接口先行的常驻服务器** —— 启动约 1 秒内 HTTP 接口即开始监听，**此时还没选游戏**。人可以在终端里慢慢选，AI agent 也可以随时 `GET /load?profile=<名>` 自己加载需要的游戏——谁先来谁生效。加载后每次查询**个位数毫秒**（一次性 CLI 进程每次调用都要付一遍缓存加载成本）。
- **交互式游戏选择** —— 启动时终端列出你的 Frosty 安装支持的全部 profile（1.0.6.3 为 25 个游戏）；已在 Frosty 里配置过路径的游戏排前面并标注 `[已配置]`，回车即取默认。
- **丰富的资产视图**
  - `ebx get` —— 完整对象树 JSON：每个实例带 `$type`/`$guid`，字段按声明序排列，`PointerRef`/`ResourceRef` 解析成可读的资产名与类名，深展开带环保护。
  - `ebx xml` —— 与编辑器 *EbxToXml* 插件同款 `EbxXmlWriter` 生成的 XML 导出。
  - `ebx deps` —— 依赖 **和** 被依赖（谁引用了这个资产）。
  - bundle 检索与单个 bundle 的内容（`ebx`/`res`/`chunk` 计数 + 名单）。
- **严格只读** —— 见下方[只读保证](#只读保证)；开发期用游戏目录前后快照对比做过端到端验证。
- **单一小体积 exe，不捆绑任何二进制** —— 运行时绑定*你自己*的 Frosty Toolsuite 安装；本仓库不再分发任何 Frosty 或游戏内容。

## 只读保证

三层独立防线确保游戏文件不被触碰：

1. **代码层** —— 全部代码不包含任何修改类 Frosty API 调用（`ModifyEbx` / `AddEbx` / `RevertAsset` / `FrostyProject` / `FrostyModWriter` ……），也绝不调用 `Config.Save`。查询链路只由 `Enumerate*` / `Get*` / `EbxXmlWriter` 构成。
2. **路径守卫** —— 工作目录钉在 Frosty 安装目录，并经物理路径规范化校验（`CreateFileW` + `GetFinalPathNameByHandle`，可解析 junction、symlink、subst 盘符与 8.3 短名）确认不在游戏目录或任何 `steamapps` 树内。唯一会写文件的 CLI 参数（`ebx xml --out`）同样按物理路径与黑名单比对，任何无法证明安全的路径一律 fail-closed（退出码 4）拒绝。
3. **服务器模式全程零写入** —— XML 以 HTTP 响应体返回，不存在任何落盘端点。工具唯一可能的写盘行为只有：Frosty 数据缓存（在安装目录内，与 GUI 编辑器行为一致）和显式指定的 `--out` 输出文件。

开发期做过端到端验证：完整查询流程前后对游戏目录做递归快照（文件清单 + 大小 + 时间戳），差异为零。

## 环境要求

- Windows x64
- 一份 **Frosty Toolsuite 1.0.6.x 安装**（不随本仓库分发 —— 从其 [releases 页面](https://github.com/CadeEvs/FrostyToolsuite/releases)获取），且至少配置过一个游戏（`%LOCALAPPDATA%\Frosty\editor_config.json` 里有该游戏的 `GamePath`）
- .NET Framework 4.8 targeting pack + [.NET SDK](https://dotnet.microsoft.com/download)（构建用）

## 目录布局与构建

工程要求你的 Frosty 安装（含 `FrostySdk.dll` 的目录）作为名为 `FrostyEditor` 的兄弟目录，即：

```
parent/
├── FrostyEditor/          ← 你的 Frosty Toolsuite 安装（传 --frosty 的话任意名字都行）
└── FrostyHeadless/        ← 本仓库
    ├── FrostyHeadless/    ← 工程（源码 + csproj）
    └── start-server.bat
```

构建：

```
dotnet build FrostyHeadless/FrostyHeadless.csproj -c Release
```

输出：`FrostyHeadless/bin/Release/net48/FrostyHeadless.exe`。

运行时从 exe 位置逐级向上定位 Frosty 安装（含探测兄弟 `FrostyEditor/` 目录）；任何命令都可用 `--frosty <目录>` 覆盖。

## 用法 —— 常驻服务器（推荐）

双击 `start-server.bat`（或运行 `FrostyHeadless.exe serve [--profile <名>] [--port <18765>]`）。

接口**立即**开始监听，此时处于 `unloaded` 状态——`/status` 报告状态，`/profiles` 已经可用。之后两边谁都可以选游戏：

- **人**：终端显示交互选择（已配置的排前面）；选了未配置的游戏会当场提示输入安装目录。
- **AI agent**：`GET /load?profile=bf1` 同步加载一个游戏（缓存热时约 1~2 秒；从未在 Frosty 里配置过的游戏加 `&gamepath=<目录>`）。重复加载同一游戏是幂等操作；往同一进程加载另一个游戏会报错（一个进程一个游戏，换游戏需重启）。

加载完成前，游戏数据类端点返回 **409** 并附提示。加载完成后终端打印接口清单并记录每个请求。**窗口开着 = 服务器在线**；关窗或 Ctrl+C 即停止。

交互选择（不带 `--profile` 时终端显示）：

```
=== FrostyHeadless 只读服务器：选择要加载的游戏（接口已开放，也可由 AI 调 /load 加载）===
   1. bf1  (Battlefield™ 1)  [已配置]
   2. Anthem  (Anthem™)  [需要密钥]
   3. bf4  (Battlefield™ 4)
   ...
输入序号或 profile 名（回车 = bf1，留空等待 AI 调用: ）:
```

请求日志（每个请求一行，显示在终端）：

```
[21:27:56] GET /status -> 200 (4 ms) #1
[21:28:10] GET /load?profile=bf1 -> 200 (1617 ms) #4
[21:28:12] GET /ebx/search?filter=m1911&limit=3 -> 200 (39 ms) #5
```

### HTTP 接口（均为 GET，根地址 `http://127.0.0.1:<port>/`）

| 端点 | 说明 |
| --- | --- |
| `/status` | 服务器状态：`state`（unloaded/loaded）、已加载 profile、游戏目录、资产计数、已服务请求数 |
| `/profiles` | 安装支持的全部 profile（含已配置/需密钥标记） |
| `/load?profile=<名>[&gamepath=<目录>]` | 向运行中的服务器加载一个游戏（同步，热缓存约 1~2 秒；同一游戏幂等） |
| `/ebx/search?filter=&type=&limit=&offset=` | 子串检索（不区分大小写）；`type` 按基类匹配含子类；支持分页 |
| `/ebx/get?name=<资产>` | 完整对象树 JSON |
| `/ebx/xml?name=<资产>` | 同一资产的 XML（`application/xml` 响应体） |
| `/ebx/deps?name=<资产>` | 依赖 + 被依赖清单 |
| `/bundles/list?filter=&limit=` | bundle 检索 |
| `/bundle/info?name=<bundle>` | bundle 详情：`ebx`/`res`/`chunk` 计数 + 前 2000 个 ebx 名 |
| `/res/search?filter=&type=&limit=&offset=` | res 资源（贴图/网格等）元信息检索，按名字与资源类型过滤 |
| `/res/get?name=` 或 `?rid=<16位hex>` | 单个 res 资源元信息；贴图附带尺寸/格式/mip 链 |
| `/res/export?name=&format=png\|dds\|raw` | **二进制导出**：贴图 → PNG（服务端经 DirectXTex 解码）或 DDS；任意 res → 原始流 |
| `/types/schema?name=<类型名>` | ebx 类型字段 schema（字段名/类型/transient） |

`/ebx/get` 也支持 `?guid=`（引用链跟随），`/res/*` 支持 `?rid=`（即 `/ebx/get` 输出里 ResourceRef 的 rid）。

示例：

```
curl "http://127.0.0.1:18765/ebx/search?filter=weapon&limit=20&offset=40"
curl "http://127.0.0.1:18765/ebx/get?name=Gameplay/Weapons/SideArms/ColtM1911/T_1911_3P_CS"
curl "http://127.0.0.1:18765/ebx/xml?name=Gameplay/Weapons/SideArms/ColtM1911/T_1911_3P_CS" -o asset.xml
curl "http://127.0.0.1:18765/bundle/info?name=win32/gameconfigurations/warsaw"
```

错误返回 JSON 体 `{"error":{"code":..,"message":..}}`，HTTP 状态码 400（参数错误）、404（资产/bundle/路径不存在）或 500。

## MCP 服务器（给 AI 客户端）

[`mcp/frosty-mcp.mjs`](mcp/frosty-mcp.mjs) 把全部接口包装成 15 个 MCP 工具（`frosty_*`），零依赖（Node 18+）：贴图导出以内联图片返回，其余为文本。

**内置完整生命周期管理**：工具调用时服务器没在运行，封装层会自动把 `FrostyHeadless.exe serve` 作为分离后台进程拉起（并等待就绪）；用完调 `frosty_server_stop` 经服务器的 `/shutdown` 端点优雅关闭。下次调用又会自动拉起——全程无需人参与。exe 位置可用环境变量 `FROSTY_EXE` 覆盖。

Claude Code 注册示例（项目 `.mcp.json`）：

```json
{
  "mcpServers": {
    "frosty": {
      "command": "node",
      "args": ["<绝对路径>/mcp/frosty-mcp.mjs"],
      "env": { "FROSTY_URL": "http://127.0.0.1:18765" }
    }
  }
}
```

## 给 AI 的话

**如果你是一个正在阅读本文件的 AI agent**：请读 [`AGENTS.md`](AGENTS.md)——那是写给你的自助安装配置指南，涵盖如何检查服务器是否在运行、自己把它拉起为后台进程、用 `/load` 加载游戏、调用接口、给自己注册 MCP，以及必须遵守的只读红线。做任何事之前先读它。

## 用法 —— 一次性 CLI

每条命令也可作为独立进程运行（每次都加载缓存，重复查询请用服务器模式）：

```
FrostyHeadless profiles list
FrostyHeadless ebx search --profile bf1 --filter weapon --limit 10
FrostyHeadless ebx get    --profile bf1 --name <资产>
FrostyHeadless ebx xml    --profile bf1 --name <资产> [--out <文件>]
FrostyHeadless ebx deps   --profile bf1 --name <资产>
FrostyHeadless bundles list --profile bf1 --filter win32 --limit 5
FrostyHeadless bundle info  --profile bf1 --name <bundle>
FrostyHeadless res search   --profile bf1 --filter <子串> [--type Texture] [--limit 20]
```

全局选项：`--frosty <目录>`（安装目录）、`--gamepath <目录>`（本次覆盖，不回写配置）、`--verbose`。

退出码：`0` 成功 · `1` 用法错误 · `2` 未找到 · `3` 启动/配置失败 · `4` 只读防线拦截 · `5` 未处理异常。

## 限制

- 一个进程只能加载一个游戏（每游戏的类型 SDK 在启动期生成）——换游戏需重启再选。
- 网格/音效导出仅原始字节（无 FBX/EALayer3 转码）；贴图可导出 PNG/DDS。
- 需要加密密钥的 profile（`RequiresKey`，如 Battlefront II）暂不支持。
- 游戏更新后首次启动会在 Frosty 安装目录内重建数据缓存（与 GUI 编辑器一致），耗时可达分钟级。

## 法律声明

本项目为非官方社区工具，与 EA、DICE 及 Frosty Toolsuite 作者无隶属关系。仓库**不包含任何 EA/Frosty 代码、二进制或游戏资产** —— 只引用你自备的 Frosty Toolsuite 安装中的程序集，且 [Frosty Toolsuite 本体采用 CC BY-NC-ND 4.0 协议](https://github.com/CadeEvs/FrostyToolsuite)。请只对你拥有的游戏与数据使用本工具。

## 许可证

本仓库代码以 [MIT 许可证](LICENSE)发布。
