using System;
using System.Collections.Generic;
using System.Globalization;

namespace FrostyHeadless
{
    /// <summary>入口：参数解析、分发、统一错误输出。stdout 只出 JSON，日志一律走 stderr。</summary>
    internal static class Program
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { /* 附加到无控制台句柄等场景，忽略 */ }

            try
            {
                Cli cli = Cli.Parse(args);
                if (cli == null)
                {
                    Console.Error.WriteLine(Usage.Text);
                    EmitError(1, "参数无法解析，用法见 stderr。");
                    return 1;
                }
                // 必须在进入任何“方法体引用了 Frosty 类型”的代码之前完成钉 CWD + 挂 AssemblyResolve，
                // 否则那些方法的 JIT 会抢先按标准探测加载 FrostySdk.dll 而失败（exe 不在安装目录时）。
                // Main 自身只引用 BCL/FrostyHeadless 类型，可安全执行 Pin。
                Boot.EnsurePin(cli);
                if (cli.Noun == "serve")
                    return Server.Run(cli);
                return Commands.Execute(cli);
            }
            catch (CliException ex)
            {
                EmitError(ex.ExitCode, ex.Message);
                return ex.ExitCode;
            }
            catch (GuardException ex)
            {
                EmitError(ex.ExitCode, ex.Message);
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                EmitError(5, ex.ToString());
                return 5;
            }
        }

        /// <summary>错误也输出为 JSON 对象到 stdout，保持输出契约统一。</summary>
        private static void EmitError(int code, string message)
        {
            JsonWriter jw = new JsonWriter(Console.Out);
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

    /// <summary>参数错误 / 资源未找到等可预期失败。ExitCode 见 README。</summary>
    internal sealed class CliException : Exception
    {
        public readonly int ExitCode;
        public CliException(int exitCode, string message) : base(message) { ExitCode = exitCode; }
    }

    /// <summary>只读防线拦截（写入目标落在游戏/Steam 目录等），退出码 4。</summary>
    internal sealed class GuardException : Exception
    {
        public readonly int ExitCode = 4;
        public GuardException(string message) : base(message) { }
    }

    internal sealed class Cli
    {
        internal readonly string Noun;
        internal readonly string Verb;
        internal readonly Dictionary<string, string> Opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal bool Verbose;

        /// <summary>服务器模式构造：把 HTTP 查询参数包成等价的 Cli，与 CLI 共享参数解析与校验。</summary>
        internal static Cli ForServer(string noun, string verb, Dictionary<string, string> opts)
        {
            Cli cli = new Cli(noun, verb);
            foreach (KeyValuePair<string, string> kv in opts)
                cli.Opts[kv.Key] = kv.Value;
            return cli;
        }

        private Cli(string noun, string verb) { Noun = noun; Verb = verb; }

        internal string Get(string key)
        {
            string v; Opts.TryGetValue(key, out v); return v;
        }

        internal string Require(string key)
        {
            string v = Get(key);
            if (string.IsNullOrEmpty(v))
                throw new CliException(1, "缺少必选参数: --" + key);
            return v;
        }

        /// <summary>--limit 解析；默认 100，0 = 不限制。</summary>
        internal int LimitOrDefault(int defaultLimit)
        {
            string v = Get("limit");
            if (string.IsNullOrEmpty(v)) return defaultLimit;
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0)
                throw new CliException(1, "--limit 必须是非负整数（0 表示不限制）: " + v);
            return n;
        }

        private static readonly Dictionary<string, HashSet<string>> ValidVerbs = new Dictionary<string, HashSet<string>>
        {
            { "profiles", new HashSet<string>(StringComparer.Ordinal) { "list" } },
            { "ebx", new HashSet<string>(StringComparer.Ordinal) { "search", "get", "xml", "deps" } },
            { "bundles", new HashSet<string>(StringComparer.Ordinal) { "list" } },
            { "bundle", new HashSet<string>(StringComparer.Ordinal) { "info" } },
            { "serve", new HashSet<string>(StringComparer.Ordinal) { "run" } }, // 单字命令：serve（无需第二个词）
        };

        private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "profile", "name", "filter", "type", "limit", "offset", "out", "frosty", "gamepath", "port"
        };

        internal static Cli Parse(string[] args)
        {
            if (args == null || args.Length < 1) return null;
            string noun = args[0].ToLowerInvariant();

            // serve 是单字命令：不带子动词，选项从 args[1] 起解析
            bool isServe = noun == "serve";
            if (args.Length < (isServe ? 1 : 2)) return null;
            string verb = isServe ? "run" : args[1].ToLowerInvariant();

            HashSet<string> verbs;
            if (!ValidVerbs.TryGetValue(noun, out verbs) || !verbs.Contains(verb)) return null;

            Cli cli = new Cli(noun, verb);
            for (int i = isServe ? 1 : 2; i < args.Length; i++)
            {
                string tok = args[i];
                if (!tok.StartsWith("--", StringComparison.Ordinal)) return null;
                string key = tok.Substring(2);
                if (key == "verbose") { cli.Verbose = true; continue; }
                if (!ValueOptions.Contains(key)) return null;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
                cli.Opts[key] = args[++i];
            }
            return cli;
        }
    }

    internal static class Usage
    {
        internal const string Text =
@"FrostyHeadless —— Frosty Toolsuite 只读查询 CLI / 常驻服务器（JSON 到 stdout，日志到 stderr）

一次性命令（每次调用都会重新加载缓存，慢）:
  FrostyHeadless profiles list
  FrostyHeadless ebx search --profile <p> [--filter <子串>] [--type <类型>] [--limit <n>] [--offset <n>]
  FrostyHeadless ebx get --name <资产名> --profile <p>
  FrostyHeadless ebx xml --name <资产名> --profile <p> [--out <文件>]
  FrostyHeadless ebx deps --name <资产名> --profile <p>
  FrostyHeadless bundles list --profile <p> [--filter <子串>] [--limit <n>]
  FrostyHeadless bundle info --name <bundle名> --profile <p>

常驻服务器（推荐，加载一次缓存后毫秒级响应）:
  FrostyHeadless serve [--profile <p>] [--port <18765>]
  不带 --profile 时在终端里交互选择游戏（已配置的排前面，回车取默认）。
  接口（GET，根地址 http://127.0.0.1:<port>/）:
    /status   /profiles
    /ebx/search?filter=&type=&limit=&offset=    /ebx/get?name=
    /ebx/xml?name=（XML 响应体）    /ebx/deps?name=
    /bundles/list?filter=&limit=    /bundle/info?name=
  关闭终端窗口或 Ctrl+C 即停止。服务器模式全程零文件写入。

全局选项:
  --frosty <目录>    Frosty 安装目录（含 FrostySdk.dll）。默认从 exe 位置逐级向上查找（含兄弟 FrostyEditor/）。
  --gamepath <目录>  覆盖配置文件里的游戏目录（不回写配置）。
  --verbose          输出进度日志到 stderr。
  --limit 0          list/search 类命令不限制条数（默认 100）。

退出码: 0 成功 | 1 用法错误 | 2 未找到 | 3 启动/配置失败 | 4 只读防线拦截 | 5 未处理异常";
    }
}
