using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using FrostySdk;
using FrostySdk.Managers;

namespace FrostyHeadless
{
    /// <summary>
    /// 常驻只读服务器：终端里选游戏 → 一次性加载缓存 → 在 127.0.0.1 上提供 JSON/HTTP 查询接口，
    /// 直到终端窗口被关闭（或 Ctrl+C）。单线程串行处理请求（AssetManager 非线程安全），
    /// 全程零文件写入——XML 以响应体返回，不提供任何落盘端点。
    /// </summary>
    internal static class Server
    {
        internal static int Run(Cli cli)
        {
            Boot.EnsureLightBoot(cli); // Profiles + Config，只读

            // --profile 缺省时在终端交互选择（像原版编辑器的启动选择窗口）
            if (string.IsNullOrEmpty(cli.Get("profile")))
            {
                string picked = PickProfileInteractive();
                if (picked == null)
                {
                    Console.Error.WriteLine("[ERROR] 未选择游戏，退出。");
                    return 1;
                }
                cli.Opts["profile"] = picked;
            }

            // 先验证 profile 合法再问目录：未知 profile 应立刻报错，而不是误导用户去输游戏目录。
            // 注意必须用 HasProfile 而不是看 Initialize 的返回值：Profile 是 struct，
            // Initialize 内部 profiles.Find 找不到时返回 default(Profile) 而非 null，
            // 包装成 Profile? 后 HasValue 恒为 true —— Initialize 对任何名字都返回 true（Frosty 自身的坑）。
            // HasProfile 用 FindIndex 判定，语义正确。
            if (!ProfilesLibrary.HasProfile(cli.Get("profile")))
                throw new CliException(3, "未知 profile: " + cli.Get("profile") + "（用 /profiles 端点或 profiles list 查看全部）");

            // 该 profile 未配置过 GamePath 且未用 --gamepath 覆盖 → 当场输入目录（不回写配置）
            if (string.IsNullOrEmpty(cli.Get("gamepath")) && !IsProfileConfigured(cli.Get("profile")))
            {
                Console.WriteLine();
                Console.WriteLine("该游戏还没有配置过游戏目录（原版编辑器的 %LocalAppData%\\Frosty\\editor_config.json 里没有它）。");
                Console.Write("请输入游戏安装目录（留空取消）: ");
                string dir = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(dir)) { Console.Error.WriteLine("[ERROR] 未提供游戏目录，退出。"); return 1; }
                cli.Opts["gamepath"] = dir.Trim();
            }

            Console.WriteLine();
            Console.WriteLine("正在加载游戏数据（已有缓存则数秒，首次会重建缓存、可能需要几分钟）……");
            Console.Error.WriteLine("[INFO] boot: profile=" + cli.Get("profile"));
            RuntimeState st = Boot.EnsureFullBoot(cli); // 唯一一次重负载

            int port = ParsePort(cli.Get("port"));
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { listener.Start(); }
            catch (Exception ex)
            {
                throw new CliException(3, "无法监听 127.0.0.1:" + port + "（端口被占用？用 --port 换一个）: " + ex.Message);
            }

            DateTime started = DateTime.Now;
            long served = 0;
            PrintBanner(st, port);

            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { break; } // 监听器被关闭（进程退出）

                served++;
                Stopwatch sw = Stopwatch.StartNew();
                int status;
                long bytes;
                try { status = Handle(ctx, cli, st, served, started); bytes = ctx.Response.ContentLength64; }
                catch (Exception ex) { status = WriteError(ctx.Response, 500, 5, ex.ToString()); bytes = 0; }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
                sw.Stop();
                Console.Error.WriteLine("[{0:HH:mm:ss}] {1} {2} -> {3} ({4} ms) #{5}",
                    DateTime.Now, ctx.Request.HttpMethod, ctx.Request.Url.PathAndQuery, status, sw.ElapsedMilliseconds, served);
            }
            return 0;
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
        /// 数据来源与 /profiles 端点一致：ProfilesLibrary 私有 profiles + Config.Current.Games。
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
            // 已配置在前、名字排序；无任何已配置时默认第一个
            List<ProfileRow> ordered = rows
                .OrderByDescending(r => r.Configured)
                .ThenBy(r => r.Profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("=== FrostyHeadless 只读服务器：选择要加载的游戏 ===");
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
            Console.Write("输入序号或 profile 名（回车 = {0}）: ", ordered[defIdx].Profile.Name);

            string line = Console.ReadLine();
            if (line == null) return null; // stdin 重定向且无输入（无终端场景应改用 --profile）
            line = line.Trim();
            if (line.Length == 0) return ordered[defIdx].Profile.Name;

            int n;
            if (int.TryParse(line, out n) && n >= 1 && n <= ordered.Count)
                return ordered[n - 1].Profile.Name;

            ProfileRow match = ordered.FirstOrDefault(r => r.Profile.Name.Equals(line, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Profile.Name;

            Console.Error.WriteLine("[ERROR] 无效选择: " + line);
            return null;
        }

        private static bool IsProfileConfigured(string profileName)
        {
            var games = Frosty.Core.Config.Current == null ? null : Frosty.Core.Config.Current.Games;
            if (games == null || profileName == null) return false;
            foreach (var kv in games)
            {
                if (kv.Key != null && kv.Key.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                {
                    // 配置里必须有有效 GamePath 才算已配置
                    return kv.Value != null && !string.IsNullOrEmpty(kv.Value.GamePath);
                }
            }
            return false;
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

        private static void PrintBanner(RuntimeState st, int port)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine("  FrostyHeadless 只读服务器已启动（保持本窗口开启即在线）");
            Console.WriteLine("  接口根地址:  http://127.0.0.1:" + port + "/");
            Console.WriteLine("  已加载:       " + st.Profile + "  (" + ProfilesLibrary.DisplayName + ")");
            Console.WriteLine("  游戏目录:     " + st.GamePath);
            Console.WriteLine("  关闭本窗口或 Ctrl+C 即停止服务");
            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine("  GET /status                    服务器状态");
            Console.WriteLine("  GET /profiles                  全部支持的 profile");
            Console.WriteLine("  GET /ebx/search?filter=&type=&limit=&offset=");
            Console.WriteLine("  GET /ebx/get?name=<资产名>");
            Console.WriteLine("  GET /ebx/xml?name=<资产名>     （返回 XML 响应体）");
            Console.WriteLine("  GET /ebx/deps?name=<资产名>");
            Console.WriteLine("  GET /bundles/list?filter=&limit=");
            Console.WriteLine("  GET /bundle/info?name=<bundle名>");
            Console.WriteLine("  GET /res/search?filter=&type=&limit=&offset=");
            Console.WriteLine("==============================================================");
            Console.WriteLine();
        }

        /// <summary>处理一个请求。返回 HTTP 状态码。</summary>
        private static int Handle(HttpListenerContext ctx, Cli cli, RuntimeState st, long served, DateTime started)
        {
            string path = (ctx.Request.Url.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            NameValueCollection q = ctx.Request.QueryString;

            switch (path)
            {
                case "/":
                case "/status":
                    return WriteStatus(ctx.Response, st, served, started);

                case "/profiles":
                    return WriteJson(ctx.Response, 200, tw => Commands.ProfilesList(cli, tw));

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

        private static int WriteStatus(HttpListenerResponse resp, RuntimeState st, long served, DateTime started)
        {
            int ebxCount = 0;
            try { ebxCount = (int)st.AssetManager.GetEbxCount(); } catch { }
            int bundleCount = st.AssetManager.EnumerateBundles().Count();

            return WriteJson(resp, 200, tw =>
            {
                JsonWriter jw = new JsonWriter(tw);
                jw.BeginObject();
                jw.Key("ok"); jw.Bool(true);
                jw.Key("profile"); jw.String(st.Profile);
                jw.Key("displayName"); jw.String(ProfilesLibrary.DisplayName);
                jw.Key("gamePath"); jw.String(st.GamePath);
                jw.Key("ebxCount"); jw.Number(ebxCount);
                jw.Key("bundleCount"); jw.Number(bundleCount);
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
