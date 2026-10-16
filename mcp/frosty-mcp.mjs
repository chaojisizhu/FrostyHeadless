#!/usr/bin/env node
/**
 * FrostyHeadless MCP 服务器（零依赖，Node 18+）。
 * 把运行中的 FrostyHeadless HTTP 接口包装成 MCP tools，供 Claude 等 AI 客户端原生调用。
 *
 * 用法：先双击 start-server.bat 让 FrostyHeadless 服务器跑起来，再在 MCP 客户端配置：
 *   Claude Code  (.mcp.json):
 *     { "mcpServers": { "frosty": {
 *         "command": "node",
 *         "args": ["<绝对路径>/mcp/frosty-mcp.mjs"],
 *         "env": { "FROSTY_URL": "http://127.0.0.1:18765" } } } }
 *   Claude Desktop (claude_desktop_config.json) 同构。
 *
 * 环境变量：
 *   FROSTY_URL      FrostyHeadless 服务器地址（默认 http://127.0.0.1:18765）
 *   FROSTY_MCP_OUT  大文件导出目录（默认 ./frosty-exports；禁止指向游戏/Steam 目录）
 */
import readline from "node:readline";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const BASE = process.env.FROSTY_URL || "http://127.0.0.1:18765";
const OUT_DIR = process.env.FROSTY_MCP_OUT || "frosty-exports";
// 与 C# 侧 PathGuard 同向的防线：导出路径绝不允许落进 Steam 目录
const isSteamPath = (p) => /[\\/]steamapps[\\/]/i.test(p);

const URL_OBJ = new URL(BASE);
const PORT = URL_OBJ.port || "18765";
// 服务器 exe：默认取本脚本仓库内的构建产物，可用 FROSTY_EXE 覆盖
const HERE = path.dirname(fileURLToPath(import.meta.url));
const EXE =
  process.env.FROSTY_EXE ||
  path.resolve(HERE, "..", "FrostyHeadless", "bin", "Release", "net48", "FrostyHeadless.exe");

const PROTOCOL_VERSION = "2024-11-05";
const SERVER_INFO = { name: "frosty-headless", version: "1.1.0" };

async function api(path) {
  const res = await fetch(BASE + path, { signal: AbortSignal.timeout(600_000) }).catch((e) => {
    throw new Error(`无法连接 FrostyHeadless 服务器 (${BASE}): ${e.message}`);
  });
  const buf = Buffer.from(await res.arrayBuffer());
  return { status: res.status, contentType: res.headers.get("content-type") || "", buf };
}

/** 服务器是否在监听（不抛异常） */
async function isUp() {
  try {
    await fetch(BASE + "/status", { signal: AbortSignal.timeout(2000) });
    return true;
  } catch {
    return false;
  }
}

const fsMod = await import("node:fs");

/** 拉起服务器（分离的后台进程，无人值守：stdin 关闭、无控制台依赖），等待监听就绪。 */
async function startServer() {
  if (await isUp()) return { alreadyRunning: true };
  if (!fsMod.existsSync(EXE)) {
    throw new Error(
      `未找到 ${EXE}。请先构建：dotnet build FrostyHeadless/FrostyHeadless.csproj -c Release` +
        `（或用环境变量 FROSTY_EXE 指定 FrostyHeadless.exe 的位置）`,
    );
  }
  // detached + stdio ignore + unref：进程独立存活，MCP 退出也不影响
  const child = spawn(EXE, ["serve", "--port", PORT], {
    detached: true,
    stdio: "ignore",
    cwd: path.dirname(EXE),
  });
  child.unref();
  for (let i = 0; i < 100; i++) {
    if (await isUp()) return { started: true, pid: child.pid };
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error(`服务器进程已启动（pid=${child.pid}）但 ${BASE} 30 秒内未就绪`);
}

/** 带自动拉起的 api：连接失败时先 startServer 再重试一次 */
async function apiAuto(path) {
  try {
    return await api(path);
  } catch (e) {
    if (/无法连接|fetch failed|ECONNREFUSED/i.test(e.message)) {
      await startServer();
      return await api(path);
    }
    throw e;
  }
}

function qs(args) {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(args || {})) {
    if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  }
  const s = p.toString();
  return s ? "?" + s : "";
}

const MAX_TEXT = 400_000; // 超长 JSON 截断阈值（字符）

function textResult(buf, status) {
  let s = buf.toString("utf8");
  let truncated = false;
  if (s.length > MAX_TEXT) {
    s = s.slice(0, MAX_TEXT);
    truncated = true;
  }
  return {
    content: [
      { type: "text", text: status !== 200 ? s : s || "(空响应)" },
      ...(truncated ? [{ type: "text", text: `[结果超过 ${MAX_TEXT} 字符已截断；用 limit/offset 分页或 --out 导出]` }] : []),
    ],
    isError: status >= 400,
  };
}

async function imageResult(name, format) {
  const { status, contentType, buf } = await apiAuto(
    `/res/export${qs({ name, format: format || "png" })}`,
  );
  if (status !== 200) return textResult(buf, status);
  if ((contentType.includes("image/png") || contentType.includes("xml") === false) && contentType.includes("image/")) {
    if (buf.length <= 4 * 1024 * 1024) {
      return { content: [{ type: "image", data: buf.toString("base64"), mimeType: contentType }] };
    }
    // 大图落盘（客户端内联显示代价太高）
  }
  // 非图片（dds/raw）或超大图片：写入 OUT_DIR 返回路径
  const fs = await import("node:fs");
  const path = await import("node:path");
  const safe = name.replace(/[^\w.\-]+/g, "_");
  const ext = contentType.includes("image/png") ? "png" : contentType.includes("xml") ? "xml" : (format === "dds" ? "dds" : "bin");
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.resolve(OUT_DIR, safe + "." + ext);
  if (isSteamPath(file)) return { content: [{ type: "text", text: "拒绝写入 Steam 目录" }], isError: true };
  fs.writeFileSync(file, buf);
  return { content: [{ type: "text", text: `已导出 ${buf.length} 字节到 ${file}` }] };
}

/** tool 名 → 执行器（返回 MCP tools/call 结果）。查询类工具经 apiAuto：服务器没开时自动拉起。 */
const TOOLS = {
  frosty_server_start: {
    description: "拉起 FrostyHeadless 服务器（分离的后台进程；已运行则幂等）。其他工具在服务器未运行时也会自动拉起，此工具用于显式预热",
    input: { type: "object", properties: {} },
    run: async () => {
      const r = await startServer();
      return { content: [{ type: "text", text: JSON.stringify(r) + ` (${BASE})` }] };
    },
  },
  frosty_server_stop: {
    description: "用完关闭 FrostyHeadless 服务器（优雅停止；只读无状态，下次调用会自动再拉起）",
    input: { type: "object", properties: {} },
    run: async () => {
      if (!(await isUp()))
        return { content: [{ type: "text", text: "服务器本来就没在运行" }] };
      try { await api("/shutdown"); } catch { /* 服务器停监听时可能来不及送完响应体，以端口状态为准 */ }
      for (let i = 0; i < 30; i++) {
        if (!(await isUp()))
          return { content: [{ type: "text", text: "已关闭 FrostyHeadless 服务器" }] };
        await new Promise((r) => setTimeout(r, 300));
      }
      return { content: [{ type: "text", text: "关闭请求已发出但端口仍在监听，请稍后重查 frosty_status" }], isError: true };
    },
  },
  frosty_status: {
    description: "FrostyHeadless 服务器状态（是否已加载游戏、加载了哪个、资产计数）",
    input: { type: "object", properties: {} },
    run: async () => { const r = await apiAuto("/status"); return textResult(r.buf, r.status); },
  },
  frosty_profiles: {
    description: "列出 Frosty 安装支持的全部游戏 profile（含是否已配置、是否需要密钥）",
    input: { type: "object", properties: {} },
    run: async () => { const r = await apiAuto("/profiles"); return textResult(r.buf, r.status); },
  },
  frosty_load_game: {
    description: "加载一个游戏到运行中的服务器（未加载时使用；同一游戏幂等）",
    input: {
      type: "object",
      properties: {
        profile: { type: "string", description: "profile 名，如 bf1（frosty_profiles 可查）" },
        gamepath: { type: "string", description: "该游戏从未配置过时必须提供安装目录" },
      },
      required: ["profile"],
    },
    run: async (a) => { const r = await apiAuto(`/load${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_ebx_search: {
    description: "检索游戏 ebx 资产（名字子串不区分大小写；type 按类型含子类；offset 分页）",
    input: {
      type: "object",
      properties: {
        filter: { type: "string" }, type: { type: "string" },
        limit: { type: "number", description: "默认 100，0=不限" }, offset: { type: "number" },
      },
    },
    run: async (a) => { const r = await apiAuto(`/ebx/search${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_ebx_get: {
    description: "读取一个 ebx 资产的完整 JSON 对象树（引用已解析为资产名/类名；name 或 guid 二选一）",
    input: {
      type: "object",
      properties: { name: { type: "string" }, guid: { type: "string" } },
    },
    run: async (a) => { const r = await apiAuto(`/ebx/get${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_ebx_xml: {
    description: "读取一个 ebx 资产的 XML（与编辑器 EbxToXml 插件同款格式）",
    input: { type: "object", properties: { name: { type: "string" } }, required: ["name"] },
    run: async (a) => { const r = await apiAuto(`/ebx/xml${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_ebx_deps: {
    description: "一个 ebx 资产的依赖与被依赖清单",
    input: { type: "object", properties: { name: { type: "string" } }, required: ["name"] },
    run: async (a) => { const r = await apiAuto(`/ebx/deps${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_bundles_list: {
    description: "检索游戏 bundle",
    input: { type: "object", properties: { filter: { type: "string" }, limit: { type: "number" } } },
    run: async (a) => { const r = await apiAuto(`/bundles/list${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_bundle_info: {
    description: "单个 bundle 详情（ebx/res/chunk 计数与前 2000 个 ebx 名）",
    input: { type: "object", properties: { name: { type: "string" } }, required: ["name"] },
    run: async (a) => { const r = await apiAuto(`/bundle/info${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_res_search: {
    description: "检索 res 资源（贴图/网格/音效等二进制资源的元信息）",
    input: {
      type: "object",
      properties: {
        filter: { type: "string" }, type: { type: "string", description: "资源类型如 Texture" },
        limit: { type: "number" }, offset: { type: "number" },
      },
    },
    run: async (a) => { const r = await apiAuto(`/res/search${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_res_get: {
    description: "单个 res 资源元信息（贴图含尺寸/格式/mip 链；name 或 rid 二选一）",
    input: { type: "object", properties: { name: { type: "string" }, rid: { type: "string", description: "16 位十六进制" } } },
    run: async (a) => { const r = await apiAuto(`/res/get${qs(a)}`); return textResult(r.buf, r.status); },
  },
  frosty_res_export: {
    description: "导出 res 资源：贴图默认 PNG（小图内联返回，大图/dds/raw 落盘返回路径）",
    input: {
      type: "object",
      properties: { name: { type: "string" }, rid: { type: "string" }, format: { type: "string", description: "png | dds | raw" } },
    },
    run: async (a) => imageResult(a.name || a.rid, a.format),
  },
  frosty_types_schema: {
    description: "查询一个 ebx 类型的字段 schema（字段名/类型/transient）",
    input: { type: "object", properties: { name: { type: "string" } }, required: ["name"] },
    run: async (a) => { const r = await apiAuto(`/types/schema${qs(a)}`); return textResult(r.buf, r.status); },
  },
};

// ---- stdio JSON-RPC ----

function reply(id, result) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, result }) + "\n");
}
function replyErr(id, code, message) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }) + "\n");
}

const rl = readline.createInterface({ input: process.stdin });
// 串行处理请求：tools/call 按到达顺序逐个执行（生命周期工具与查询之间不该竞争）
let chain = Promise.resolve();
rl.on("line", (line) => {
  line = line.trim();
  if (!line) return;
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return;
  }
  if (msg.id === undefined || msg.id === null) return; // 通知（notifications/initialized 等）：无需应答

  chain = chain.then(() => handleMessage(msg)).catch(() => {});
});

async function handleMessage(msg) {
  try {
    switch (msg.method) {
      case "initialize":
        reply(msg.id, { protocolVersion: PROTOCOL_VERSION, capabilities: { tools: {} }, serverInfo: SERVER_INFO });
        break;
      case "ping":
        reply(msg.id, {});
        break;
      case "tools/list":
        reply(msg.id, {
          tools: Object.entries(TOOLS).map(([name, t]) => ({
            name,
            description: t.description,
            inputSchema: t.input,
          })),
        });
        break;
      case "tools/call": {
        const name = msg.params?.name;
        const tool = TOOLS[name];
        if (!tool) {
          reply(msg.id, { content: [{ type: "text", text: `未知工具: ${name}` }], isError: true });
          break;
        }
        reply(msg.id, await tool.run(msg.params?.arguments || {}));
        break;
      }
      default:
        replyErr(msg.id, -32601, `method not found: ${msg.method}`);
    }
  } catch (e) {
    reply(msg.id, { content: [{ type: "text", text: `工具执行失败: ${e.message}` }], isError: true });
  }
}

process.stderr.write(`[frosty-mcp] ready, proxying ${BASE}\n`);
// 客户端断开（EPIPE）时安静退出，而不是抛未处理异常
process.stdout.on("error", (e) => {
  if (e.code === "EPIPE") process.exit(0);
  throw e;
});
