using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using FrostySdk;
using FrostySdk.Managers;

namespace FrostyHeadless
{
    /// <summary>
    /// 常驻只读服务器：**接口先行，游戏后挂**——启动即监听 127.0.0.1，此时处于 unloaded 状态；
    /// 人可以在终端交互选游戏（像原版编辑器），AI 也可以随时 POST/GET /load?profile=<名> 自己挂载，
    /// 谁先来谁生效。加载完成后提供 JSON/HTTP 查询，直到终端窗口被关闭（或 Ctrl+C）。
    /// 单线程串行处理请求（AssetManager 非线程安全）；全程零文件写入。
    /// </summary>
    internal static class Server
    {
        private sealed class ServerState
        {
            internal readonly object Lock = new object();
            /// <summary>null = 未加载任何游戏；加载/查询都在锁内进行。</summary>
            internal RuntimeState Loaded;
            internal bool Booting;
        }

        internal static int Run(Cli cli)
        {
            Boot.EnsureLightBoot(cli); // Profiles + Config，只读、亚秒级：保证 /profiles 一开始就能用

            int port = ParsePort(cli.Get("port"));
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { listener.Start(); }
            catch (Exception ex)
            {
                throw new CliException(3, "无法监听 127.0.0.1:" + port + "（端口被占用？用 --port 换一个）: " + ex.Message);
            }

            ServerState ss = new ServerState();
            DateTime started = DateTime.Now;
            PrintWaitingBanner(port);

            // 请求线程：立即对外服务（unloaded 状态下 /status 与 /profiles 可用，其余返回 409）
            Thread worker = new Thread(delegate() { ListenLoop(cli, ss, listener, started); });
            worker.IsBackground = true;
            worker.Start();

            // 主线程：人机交互（不阻塞接口）
            string presetProfile = cli.Get("profile");
            if (!string.IsNullOrEmpty(presetProfile))
            {
                TryBoot(cli, ss, presetProfile); // --profile 直启：仍先开接口再加载
            }
            else
            {
                string picked = PickProfileInteractive();
                if (picked == null)
                {
                    Console.Error.WriteLine("[INFO] 终端未选择游戏，服务器保持 unloaded，等待 /load 接口调用。");
                }
                else
                {
                    lock (ss.Lock)
                    {
                        if (ss.Loaded != null)
                        {
                            Console.Error.WriteLine("[INFO] 已由接口加载 " + ss.Loaded.Profile + "，忽略本次终端选择: " + picked);
                            picked = null;
                        }
                        else if (ss.Booting)
                        {
                            Console.Error.WriteLine("[INFO] 接口正在加载游戏，忽略本次终端选择: " + picked);
                            picked = null;
                        }
                    }
                    if (picked != null)
                    {
                        if (!IsProfileConfigured(picked) && string.IsNullOrEmpty(cli.Get("gamepath")))
                            PromptGamePathInto(cli); // 选了未配置的游戏：当场问目录（不回写配置）
                        string err = TryBoot(cli, ss, picked);
                        if (err != null) Console.Error.WriteLine("[ERROR] " + err);
                    }
                }
            }

            // 保持进程存活（窗口关闭/Ctrl+C 即退出）
            while (listener.IsListening)
                Thread.Sleep(500);
            return 0;
        }

        // ---------------------------------------------------------------- 加载游戏（终端与 /load 共用）

        /// <summary>加载一个游戏（幂等：已加载同一游戏直接返回；加载中则等待）。返回错误消息或 null。</summary>
        private static string TryBoot(Cli cli, ServerState ss, string profile)
        {
            lock (ss.Lock)
            {
                if (ss.Loaded != null)
                {
                    if (!ss.Loaded.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase))
                        return "本进程已加载 " + ss.Loaded.Profile + "（一个进程只能加载一个游戏，换游戏请重启后再选）";
                    return null; // 已是该游戏，幂等成功
                }
                while (ss.Booting)
                    Monitor.Wait(ss.Lock);
                if (ss.Loaded != null) // 别人在等待期间完成了加载
                {
                    if (!ss.Loaded.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase))
                        return "本进程已加载 " + ss.Loaded.Profile;
                    return null;
                }

                ss.Booting = true;
            }
            try
            {
                Console.Error.WriteLine("[INFO] boot: profile=" + profile);
                // 用临时 Cli 携带 profile/gamepath，复用 EnsureFullBoot 的全部校验与防线
                Cli bootCli = Cli.ForServer("serve", "run", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                bootCli.Opts["profile"] = profile;
                string gp = cli.Get("gamepath");
                if (!string.IsNullOrEmpty(gp)) bootCli.Opts["gamepath"] = gp;

                RuntimeState st = Boot.EnsureFullBoot(bootCli);
                lock (ss.Lock)
                {
                    ss.Loaded = st;
                    ss.Booting = false;
                    Monitor.PulseAll(ss.Lock);
                }
                PrintLoadedBanner(st);
                return null;
            }
            catch (Exception ex)
            {
                lock (ss.Lock)
                {
                    ss.Booting = false;
                    Monitor.PulseAll(ss.Lock);
                }
                int code = ex is CliException ? ((CliException)ex).ExitCode : (ex is GuardException ? ((GuardException)ex).ExitCode : 5);
                string msg = ex.Message;
                Console.Error.WriteLine("[ERROR] 加载失败 (" + profile + "): " + msg);
                return "加载失败 (" + profile + "): " + msg + (code == 3 ? "" : "（code=" + code + "）");
            }
        }

        // ---------------------------------------------------------------- 交互选游戏

        private sealed class ProfileRow
        {
            internal Profile Profile;
            internal bool Configured;
            internal string GamePath;
        }

        /// <summary>
        /// 终端里列出全部支持的 profile（已配置的排前面并标记），按序号或名字选择，回车取默认。
        /// 与 /profiles 端点同一数据源：ProfilesLibrary 私有 profiles + Config.Current.Games。
        /// </summary>
        private static string PickProfileInteractive()
        {
            FieldInfo fi = typeof(ProfilesLibrary).GetField("profiles", BindingFlags.NonPublic | BindingFlags.Static);
            if (fi == null)
                throw new CliException(5, "反射失败：ProfilesLibrary.profiles 不存在（SDK 版本不匹配？）");
            List<Profile> all = (fi.GetValue(null) as List<Profile>) ?? new List<Profile>();

            List<ProfileRow> rows = new List<ProfileRow>();
            foreach (Profile p in all)
            {
                ProfileRow row = new ProfileRow { Profile = p };
                var games = Frosty.Core.Config.Current == null ? null : Frosty.Core.Config.Current.Games;
                if (games != null)
                {
                    foreach (var kv in games)
                    {
                        if (kv.Key != null && kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            row.Configured = true;
                            row.GamePath = kv.Value == null ? null : kv.Value.GamePath;
                            break;
                        }
                    }
                }
                rows.Add(row);
            }
            List<ProfileRow> ordered = rows
                .OrderByDescending(r => r.Configured)
                .ThenBy(r => r.Profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("=== FrostyHeadless 只读服务器：选择要加载的游戏（接口已开放，也可由 AI 调 /load 加载）===");
            for (int i = 0; i < ordered.Count; i++)
            {
                ProfileRow r = ordered[i];
                string tag = r.Configured ? "  [已配置]" : "";
                string key = r.Profile.RequiresKey ? "  [需要密钥]" : "";
                Console.WriteLine("  {0,2}. {1}  ({2}){3}{4}", i + 1, r.Profile.Name, r.Profile.DisplayName, tag, key);
            }
            int defIdx = ordered.FindIndex(r => r.Configured);
            if (defIdx < 0) defIdx = 0;
            Console.WriteLine();
            Console.Write("输入序号或 profile 名（回车 = {0}，留空等待 AI 调用: ）: ", ordered[defIdx].Profile.Name);

            string line = Console.ReadLine();
            if (line == null) return null; // stdin 无输入（如无终端）：保持 unloaded 等接口
            line = line.Trim();
            if (line.Length == 0) return ordered[defIdx].Profile.Name;

            int n;
            if (int.TryParse(line, out n) && n >= 1 && n <= ordered.Count)
                return ordered[n - 1].Profile.Name;

            ProfileRow match = ordered.FirstOrDefault(r => r.Profile.Name.Equals(line, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Profile.Name;

            Console.Error.WriteLine("[ERROR] 无效选择: " + line + "（保持 unloaded，等待 /load）");
            return null;
        }

        private static bool IsProfileConfigured(string profileName)
        {
            var games = Frosty.Core.Config.Current == null ? null : Frosty.Core.Config.Current.Games;
            if (games == null || profileName == null) return false;
            foreach (var kv in games)
            {
                if (kv.Key != null && kv.Key.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value != null && !string.IsNullOrEmpty(kv.Value.GamePath);
            }
            return false;
        }

        /// <summary>选了未配置的游戏时在终端问游戏目录，塞进 cli 的 gamepath（不回写配置）。</summary>
        private static void PromptGamePathInto(Cli cli)
        {
            Console.WriteLine();
            Console.WriteLine("该游戏还没有配置过游戏目录（原版编辑器的 %LocalAppData%\\Frosty\\editor_config.json 里没有它）。");
            Console.Write("请输入游戏安装目录（留空取消）: ");
            string dir = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(dir)) cli.Opts["gamepath"] = dir.Trim();
        }

        private static int ParsePort(string v)
        {
            if (string.IsNullOrEmpty(v)) return 18765;
            int n;
            if (!int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n) || n < 1 || n > 65535)
                throw new CliException(1, "--port 必须是 1-65535 的整数: " + v);
            return n;
        }

        // ---------------------------------------------------------------- HTTP

        private static void PrintWaitingBanner(int port)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine("  FrostyHeadless 只读服务器已监听（尚未加载游戏）");
            Console.WriteLine("  接口根地址:  http://127.0.0.1:" + port + "/");
            Console.WriteLine("  · 在本终端选择游戏，或由 AI 调用 GET /load?profile=<名>");
            Console.WriteLine("  · /profiles 可列出全部支持的游戏（configured=true 的可直接加载）");
            Console.WriteLine("  · 关闭本窗口或 Ctrl+C 即停止服务");
            Console.WriteLine("==============================================================");
            Console.WriteLine();
        }

        private static void PrintLoadedBanner(RuntimeState st)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine("  已加载: " + st.Profile + "  (" + ProfilesLibrary.DisplayName + ")");
            Console.WriteLine("  游戏目录: " + st.GamePath);
            Console.WriteLine("  GET /status | /profiles | /ebx/search | /ebx/get | /ebx/xml");
            Console.WriteLine("  GET /ebx/deps | /bundles/list | /bundle/info | /res/search");
            Console.WriteLine("==============================================================");
            Console.WriteLine();
        }

        private static void ListenLoop(Cli cli, ServerState ss, HttpListener listener, DateTime started)
        {
            long served = 0;
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { break; }

                served++;
                Stopwatch sw = Stopwatch.StartNew();
                int status;
                try { status = Handle(ctx, cli, ss, listener, served, started); }
                catch (Exception ex) { status = WriteError(ctx.Response, 500, 5, ex.ToString()); }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
                sw.Stop();
                Console.Error.WriteLine("[{0:HH:mm:ss}] {1} {2} -> {3} ({4} ms) #{5}",
                    DateTime.Now, ctx.Request.HttpMethod, ctx.Request.Url.PathAndQuery, status, sw.ElapsedMilliseconds, served);
            }
        }

        /// <summary>处理一个请求。返回 HTTP 状态码。</summary>
        private static int Handle(HttpListenerContext ctx, Cli cli, ServerState ss, HttpListener listener, long served, DateTime started)
        {
            string path = (ctx.Request.Url.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            NameValueCollection q = ctx.Request.QueryString;

            // 不需要游戏数据的端点
            if (path == "/" || path == "/status")
                return WriteStatus(ctx.Response, ss, served, started);
            if (path == "/profiles")
                return WriteJson(ctx.Response, 200, tw => Commands.ProfilesList(cli, tw));
            if (path == "/load")
                return HandleLoad(ctx, cli, ss, q);

            // 以下端点需要已加载游戏
            RuntimeState st;
            lock (ss.Lock) { st = ss.Loaded; }
            if (st == null)
            {
                return WriteError(ctx.Response, 409, 3,
                    "尚未加载游戏。GET /load?profile=<名>[&gamepath=<目录>] 加载（/profiles 查看列表）；终端里也可以选择。");
            }

            switch (path)
            {
                case "/ebx/search":
                    return WriteJson(ctx.Response, 200, tw => Commands.EbxSearch(ForQuery("ebx", "search", q), st, tw));

                case "/ebx/get":
                    return WriteJson(ctx.Response, 200, tw => Commands.EbxGet(ForQuery("ebx", "get", q), st, tw));

                case "/ebx/xml":
                    return WriteXml(ctx.Response, st, q);

                case "/ebx/deps":
                    return WriteJson(ctx.Response, 200, tw => Commands.EbxDeps(ForQuery("ebx", "deps", q), st, tw));

                case "/bundles/list":
                    return WriteJson(ctx.Response, 200, tw => Commands.BundlesList(ForQuery("bundles", "list", q), st, tw));

                case "/bundle/info":
                    return WriteJson(ctx.Response, 200, tw => Commands.BundleInfo(ForQuery("bundle", "info", q), st, tw));

                case "/res/search":
                    return WriteJson(ctx.Response, 200, tw => Commands.ResSearch(ForQuery("res", "search", q), st, tw));

                default:
                    return WriteError(ctx.Response, 404, 2, "未知路径: " + path + "（接口清单见 /status 或启动横幅）");
            }
        }

        /// <summary>/load?profile=<名>[&gamepath=<目录>]：同步加载（有缓存时数秒~数十秒），完成后返回。</summary>
        private static int HandleLoad(HttpListenerContext ctx, Cli cli, ServerState ss, NameValueCollection q)
        {
            string profile = q["profile"];
            if (string.IsNullOrEmpty(profile))
                return WriteError(ctx.Response, 400, 1, "缺少必选参数: profile（/load?profile=bf1；列表见 /profiles）");

            if (!ProfilesLibrary.HasProfile(profile))
                return WriteError(ctx.Response, 404, 3, "未知 profile: " + profile + "（用 /profiles 查看全部）");

            if (string.IsNullOrEmpty(q["gamepath"]) && string.IsNullOrEmpty(cli.Get("gamepath")) && !IsProfileConfigured(profile))
                return WriteError(ctx.Response, 400, 3,
                    "profile " + profile + " 未配置过游戏目录，请带 &gamepath=<游戏安装目录> 调用（不会回写配置）");

            if (!string.IsNullOrEmpty(q["gamepath"]))
            {
                // 把接口传入的 gamepath 塞进本次启动上下文（仅本进程内生效）
                cli.Opts["gamepath"] = q["gamepath"];
            }

            Stopwatch sw = Stopwatch.StartNew();
            string err = TryBoot(cli, ss, profile);
            sw.Stop();
            if (err != null)
                return WriteError(ctx.Response, 409, 3, err);

            RuntimeState st;
            lock (ss.Lock) { st = ss.Loaded; }
            return WriteJson(ctx.Response, 200, tw =>
            {
                JsonWriter jw = new JsonWriter(tw);
                jw.BeginObject();
                jw.Key("loaded"); jw.Bool(true);
                jw.Key("profile"); jw.String(st.Profile);
                jw.Key("displayName"); jw.String(ProfilesLibrary.DisplayName);
                jw.Key("gamePath"); jw.String(st.GamePath);
                jw.Key("loadMs"); jw.Number(sw.ElapsedMilliseconds);
                jw.EndObject();
                jw.Flush();
            });
        }

        /// <summary>把 HTTP 查询参数包成 Cli，让服务器与 CLI 共享同一套参数解析与校验。</summary>
        private static Cli ForQuery(string noun, string verb, NameValueCollection q)
        {
            Dictionary<string, string> opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in q.AllKeys)
            {
                if (k == null) continue;
                string key = k.TrimStart('-').ToLowerInvariant();
                opts[key] = q[k];
            }
            return Cli.ForServer(noun, verb, opts);
        }

        private static int WriteStatus(HttpListenerResponse resp, ServerState ss, long served, DateTime started)
        {
            RuntimeState st;
            lock (ss.Lock) { st = ss.Loaded; }

            int ebxCount = 0, bundleCount = 0;
            if (st != null)
            {
                try { ebxCount = (int)st.AssetManager.GetEbxCount(); } catch { }
                bundleCount = st.AssetManager.EnumerateBundles().Count();
            }

            return WriteJson(resp, 200, tw =>
            {
                JsonWriter jw = new JsonWriter(tw);
                jw.BeginObject();
                jw.Key("ok"); jw.Bool(true);
                jw.Key("state"); jw.String(st == null ? "unloaded" : "loaded");
                if (st != null)
                {
                    jw.Key("profile"); jw.String(st.Profile);
                    jw.Key("displayName"); jw.String(ProfilesLibrary.DisplayName);
                    jw.Key("gamePath"); jw.String(st.GamePath);
                    jw.Key("ebxCount"); jw.Number(ebxCount);
                    jw.Key("bundleCount"); jw.Number(bundleCount);
                }
                else
                {
                    jw.Key("hint"); jw.String("GET /load?profile=<名> 加载游戏；/profiles 列出全部支持的游戏");
                }
                jw.Key("startedAt"); jw.String(started.ToString("yyyy-MM-dd HH:mm:ss"));
                jw.Key("uptimeSec"); jw.Number((long)(DateTime.Now - started).TotalSeconds);
                jw.Key("requestsServed"); jw.Number(served);
                jw.Key("readOnly"); jw.Bool(true);
                jw.EndObject();
                jw.Flush();
            });
        }

        private delegate void BodyWriter(System.IO.TextWriter tw);

        private static int WriteJson(HttpListenerResponse resp, int status, BodyWriter body)
        {
            // 先在内存里完整生成再写响应：body 抛出的 CliException/GuardException 发生在任何
            // 字节落到响应流之前，仍能改写状态码与错误体（响应流一旦开写就来不及了）。
            string content;
            try
            {
                using (StringWriter sw = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
                {
                    body(sw);
                    content = sw.ToString();
                }
            }
            catch (CliException ex) { return WriteError(resp, MapStatus(ex.ExitCode), ex.ExitCode, ex.Message); }
            catch (GuardException ex) { return WriteError(resp, 403, ex.ExitCode, ex.Message); }

            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            byte[] b = Encoding.UTF8.GetBytes(content);
            resp.OutputStream.Write(b, 0, b.Length);
            return status;
        }

        private static int WriteXml(HttpListenerResponse resp, RuntimeState st, NameValueCollection q)
        {
            try
            {
                string name = q["name"];
                if (string.IsNullOrEmpty(name))
                    return WriteError(resp, 400, 1, "缺少必选参数: name");
                byte[] xml = Commands.BuildXmlBytes(st, name);
                resp.StatusCode = 200;
                resp.ContentType = "application/xml; charset=utf-8";
                resp.Headers["Access-Control-Allow-Origin"] = "*";
                resp.OutputStream.Write(xml, 0, xml.Length);
                return 200;
            }
            catch (CliException ex) { return WriteError(resp, MapStatus(ex.ExitCode), ex.ExitCode, ex.Message); }
            catch (GuardException ex) { return WriteError(resp, 403, ex.ExitCode, ex.Message); }
        }

        private static int MapStatus(int cliCode)
        {
            // 2 未找到 → 404；1 用法 → 400；其余（3/5）→ 500；4（防线）由 GuardException 分支处理
            if (cliCode == 2) return 404;
            if (cliCode == 1) return 400;
            return 500;
        }

        private static int WriteError(HttpListenerResponse resp, int httpStatus, int code, string message)
        {
            try
            {
                resp.StatusCode = httpStatus;
                resp.ContentType = "application/json; charset=utf-8";
                resp.Headers["Access-Control-Allow-Origin"] = "*";
                using (StreamWriter sw = new StreamWriter(resp.OutputStream, new UTF8Encoding(false)))
                {
                    JsonWriter jw = new JsonWriter(sw);
                    jw.BeginObject();
                    jw.Key("error");
                    jw.BeginObject();
                    jw.Key("code"); jw.Number(code);
                    jw.Key("message"); jw.String(message);
                    jw.EndObject();
                    jw.EndObject();
                    jw.Flush();
                }
            }
            catch { /* 客户端已断开等情况，尽力而为 */ }
            return httpStatus;
        }
    }
}
