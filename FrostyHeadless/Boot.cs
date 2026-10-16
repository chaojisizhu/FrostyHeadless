using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace FrostyHeadless
{
    /// <summary>ILogger 的 stderr 实现（接口面见 FrostySdk.Interfaces/ILogger.cs，仅 3 个方法）。</summary>
    internal sealed class ConsoleLogger : ILogger
    {
        private readonly bool _verbose;
        internal ConsoleLogger(bool verbose) { _verbose = verbose; }

        public void Log(string text, params object[] vars) { Write("INFO ", text, vars); }
        public void LogWarning(string text, params object[] vars) { Write("WARN ", text, vars); }
        public void LogError(string text, params object[] vars) { Write("ERROR", text, vars); }

        private void Write(string level, string text, object[] vars)
        {
            if (text == null) return;
            // Frosty 在缓存重建期间会刷大量 "progress:{0}" 行（AssetManager.cs:1708），默认抑制
            if (!_verbose && text.StartsWith("progress:", StringComparison.Ordinal)) return;
            string msg;
            try { msg = (vars == null || vars.Length == 0) ? text : string.Format(text, vars); }
            catch { msg = text; }
            Console.Error.WriteLine("[" + level + "] " + msg);
        }
    }

    /// <summary>一次完整启动后的运行态（单命令进程内缓存）。</summary>
    internal sealed class RuntimeState
    {
        internal AssetManager AssetManager;
        internal string Profile;
        internal string GamePath;
        internal List<string> BlockedRoots;
    }

    /// <summary>
    /// 只读路径防线。依据 fre-analysis/notes/read-only-audit.md constraints #1/#4，并按复审加固：
    /// 黑名单比对基于<b>物理路径</b>而非词法路径——Path.GetFullPath 在 .NET Framework 上不解析
    /// reparse point（junction/symlink）、subst 盘符与 8.3 短名，若操作者自建别名并以 --out 传入，
    /// 词法比较会漏判而 File.WriteAllBytes 按操作系统语义跟随链接写入游戏目录。
    /// 本实现用 CreateFileW(FILE_FLAG_BACKUP_SEMANTICS) + GetFinalPathNameByHandleW 把路径
    /// 解析到物理形态后再比对；实际写盘也使用解析后的物理路径；任何解析失败一律 fail-closed（拒绝）。
    /// 黑名单：游戏目录、其 steamapps 祖先、Steam 库根（steamapps 的父目录）。
    /// 已知残余（无法检测）：指向游戏内文件的<b>硬链接</b>——它是同一文件的另一个名字，无 reparse 数据；
    /// 但创建硬链接需要操作者事先主动操作，本工具任何代码路径都不创建别名。
    /// </summary>
    internal static class PathGuard
    {
        // ---- Win32 物理路径解析 ----
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_ALL = 7; // READ | WRITE | DELETE
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, System.Text.StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        /// <summary>
        /// 解析物理路径：对最深的<b>已存在</b>祖先（目录用 BACKUP_SEMANTICS 句柄）取
        /// GetFinalPathNameByHandle，消除 junction/symlink、subst 盘符与 8.3 短名别名，
        /// 再拼回不存在的尾部（不存在则不可能携带别名）。失败抛 IOException（调用方 fail-closed）。
        /// </summary>
        internal static string Canonicalize(string path)
        {
            string full = Path.GetFullPath(path); // 词法规范化：消解 ..、.、相对段
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return full; // 非盘根路径的极端形态，交由 fail-closed 兜底
            string trimmed = full.TrimEnd('\\');
            string[] segs = trimmed.Length > root.Length
                ? trimmed.Substring(root.Length).Split('\\')
                : new string[0];

            // 逐段推进到最深已存在祖先；末段允许是文件（覆盖写场景）
            string deepest = root;      // 已确认存在的目录（以 \ 结尾）
            int consumed = 0;
            for (int i = 0; i < segs.Length; i++)
            {
                string cand = deepest + segs[i];
                bool isLast = i == segs.Length - 1;
                if (isLast && File.Exists(cand)) { deepest = cand; consumed = i + 1; break; }
                if (!Directory.Exists(cand)) break;
                deepest = cand + "\\";
                consumed = i + 1;
            }

            string resolved = ResolveHandle(deepest);
            for (int i = consumed; i < segs.Length; i++)
            {
                resolved = resolved.TrimEnd('\\') + "\\" + segs[i];
            }
            return resolved;
        }

        private static string ResolveHandle(string fileSystemPath)
        {
            using (Microsoft.Win32.SafeHandles.SafeFileHandle h = CreateFileW(
                fileSystemPath, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (h.IsInvalid)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    throw new IOException("无法打开路径以解析物理路径 (Win32Error=" + err + "): " + fileSystemPath);
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder(4096);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                if (n == 0)
                    throw new IOException("GetFinalPathNameByHandle 失败: " + fileSystemPath);
                if (n >= (uint)sb.Capacity)
                {
                    sb = new System.Text.StringBuilder((int)n + 1);
                    n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= (uint)sb.Capacity)
                        throw new IOException("GetFinalPathNameByHandle 失败(缓冲区重试): " + fileSystemPath);
                }
                string s = sb.ToString();
                if (s.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) s = "\\" + s.Substring(7);
                else if (s.StartsWith("\\\\?\\", StringComparison.Ordinal)) s = s.Substring(4);
                return s;
            }
        }

        /// <summary>路径任一段是否为 steamapps（大小写不敏感）。</summary>
        internal static bool HasSteamAppsSegment(string path)
        {
            string[] segs = path.Split('\\');
            foreach (string seg in segs)
            {
                if (seg.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>返回物理形态的黑名单根（均带尾部分隔符）：游戏目录 + steamapps 祖先 + Steam 库根。</summary>
        internal static List<string> ComputeBlockedRoots(string gamePath)
        {
            string canon = Canonicalize(gamePath); // 调用方负责 fail-closed
            string full = canon.TrimEnd('\\') + "\\";

            List<string> roots = new List<string>();
            roots.Add(full); // 游戏目录本体

            string[] segs = full.Split('\\');
            for (int i = 1; i < segs.Length; i++)
            {
                if (segs[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(string.Join("\\", segs, 0, i + 1) + "\\"); // steamapps 树

                    // Steam 库根（steamapps 的父目录）整体拉黑：红线为“任何 Steam 目录”，
                    // 且可拦住 8.3 关闭时经不存在的 STEAMA~1 字面段在库根下建目录的边缘情形。
                    // 库根即盘根（如 D:\steamapps\...）时不加（整盘拉黑过度）。
                    string libRoot = string.Join("\\", segs, 0, i);
                    if (libRoot.Length > 2 && libRoot[1] == ':')
                        roots.Add(libRoot + "\\");
                    break;
                }
            }
            return roots;
        }

        /// <summary>
        /// candidate 是否落在黑名单内。先做物理路径比对（权威判定），再做词法层 steamapps 段兜底（纵深防御）；
        /// 物理路径解析失败即视为 blocked（fail-closed，绝不放行无法证明安全的路径）。
        /// </summary>
        internal static bool IsBlocked(string candidate, List<string> roots)
        {
            string canon;
            try { canon = Canonicalize(candidate); }
            catch { return true; }
            foreach (string root in roots)
            {
                if (canon.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (HasSteamAppsSegment(canon)) return true;
            if (HasSteamAppsSegment(Path.GetFullPath(candidate))) return true; // 词法层兜底
            return false;
        }
    }

    /// <summary>
    /// 无头启动序列，逐步对应 fre-analysis/notes/api-cheatsheet.md §1 的 bootSequence
    /// （原始参照 FrostyEditor/App.cs:132-145,235-302 与 SplashWindow.cs:151-175）。
    /// 审计确认可跳过的步骤一律跳过（read-only-audit.md §3）：
    /// pluginManager.Initialize / 本地化 / strings.txt / legacy 自定义资产管理器 / Config.Save。
    /// </summary>
    internal static class Boot
    {
        private static string s_installDir;
        private static ConsoleLogger s_logger;
        private static bool s_lightBooted;
        private static RuntimeState s_state;

        internal static string InstallDir { get { return s_installDir; } }

        /// <summary>步骤 0/1：定位安装目录 → 钉 CWD → 挂 AssemblyResolve。必须在触碰任何 Frosty 类型之前完成。</summary>
        internal static void EnsurePin(Cli cli)
        {
            if (s_installDir != null) return;

            string overrideDir = cli == null ? null : cli.Get("frosty");
            s_installDir = LocateInstallDir(overrideDir);

            // api-cheatsheet §5.1：Caches/、Profiles/、Plugins/、thirdparty native dll 全按 CWD 相对解析
            Environment.CurrentDirectory = s_installDir;

            // 审计 constraint 1 的第一道闸（无需 GamePath 即可判定）：安装目录绝不允许位于任何
            // Steam 目录（steamapps 树）内，否则后续所有相对路径写盘（Caches/ 等）都会落进 Steam 目录。
            // 按复审加固：用物理路径判定（--frosty 指向的目录可能是 junction/subst/8.3 别名）。
            string canonInstall;
            try { canonInstall = PathGuard.Canonicalize(s_installDir); }
            catch (Exception ex)
            {
                throw new GuardException("无法解析 Frosty 安装目录的物理路径，拒绝启动（fail-closed）: " +
                    s_installDir + " — " + ex.Message);
            }
            if (PathGuard.HasSteamAppsSegment(canonInstall) || PathGuard.HasSteamAppsSegment(s_installDir))
                throw new GuardException("Frosty 安装目录位于 Steam 目录（steamapps）内，拒绝启动: " +
                    s_installDir + " → " + canonInstall);

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static string LocateInstallDir(string overrideDir)
        {
            if (!string.IsNullOrEmpty(overrideDir))
            {
                string dir = Path.GetFullPath(overrideDir);
                if (!File.Exists(Path.Combine(dir, "FrostySdk.dll")))
                    throw new CliException(3, "--frosty 指定的目录里没有 FrostySdk.dll: " + dir);
                return dir;
            }
            // 从 exe 位置逐级向上找：先看本目录，再看同名布局的兄弟 FrostyEditor/ 子目录
            // （工作区布局：根/FrostyEditor 安装目录 + 根/FrostyHeadless/bin/... 本 exe）
            string cur = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(cur))
            {
                if (File.Exists(Path.Combine(cur, "FrostySdk.dll"))) return cur;
                string sibling = Path.Combine(cur, "FrostyEditor");
                if (File.Exists(Path.Combine(sibling, "FrostySdk.dll"))) return sibling;
                cur = Path.GetDirectoryName(cur.TrimEnd('\\'));
            }
            throw new CliException(3,
                "未能定位 Frosty 安装目录（从 exe 位置向上未找到 FrostySdk.dll）。请用 --frosty <安装目录> 指定。");
        }

        /// <summary>
        /// 程序集解析（照 FrostyEditor/App.cs:165-185 的写法泛化）：
        /// EbxClasses → 安装目录/Profiles/&lt;SDKFilename&gt;.dll；其余先试安装目录根，再试 ThirdParty/。
        /// 本 exe 可能不在安装目录内运行，因此 Frosty 程序集本身也需要在这里解析。
        ///
        /// 关键约束：本方法体内只允许引用 BCL 类型。处理器是在“程序集绑定失败”的回调里执行的，
        /// 若它自身的 JIT 又需要加载 Frosty 程序集（例如引用 ProfilesLibrary），
        /// 会再次触发 AssemblyResolve → 重入尚未 JIT 完的本方法 → 无限递归 StackOverflow。
        /// 因此对 ProfilesLibrary（FrostySdk）的触碰必须隔离到带 NoInlining 的独立方法里。
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string name = args.Name;
            int comma = name.IndexOf(',');
            if (comma >= 0) name = name.Substring(0, comma);
            name = name.Trim();
            if (name.Length == 0) return null;
            // 程序集简单名不允许路径字符；拒绝任何穿越尝试
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            if (name.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase))
                return ResolveEbxClasses();

            string candidate = Path.Combine(s_installDir, name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFile(candidate);

            candidate = Path.Combine(s_installDir, "ThirdParty", name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFile(candidate);

            return null;
        }

        /// <summary>延迟触碰 FrostySdk 类型（ProfilesLibrary.SDKFilename），见 OnAssemblyResolve 的约束说明。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Assembly ResolveEbxClasses()
        {
            try
            {
                string sdk = ProfilesLibrary.SDKFilename; // 需要 effectiveProfile 已选定
                if (!string.IsNullOrEmpty(sdk))
                {
                    string p = Path.Combine(s_installDir, "Profiles", sdk + ".dll");
                    if (File.Exists(p)) return Assembly.LoadFile(p);
                }
            }
            catch { /* profile 未选时 SDKFilename 会 NRE——此时也不该有 EbxClasses 解析请求 */ }
            return null;
        }

        /// <summary>轻启动：到 Config.Load 为止（profiles list 只需要这些）。</summary>
        internal static void EnsureLightBoot(Cli cli)
        {
            EnsurePin(cli);
            if (s_lightBooted) return;

            // bootSequence 步骤 2-5, 7
            s_logger = new ConsoleLogger(cli != null && cli.Verbose);
            App.Logger = s_logger;

            // PluginManager 构造只做 Plugins/ 目录的只读枚举（PluginManager.cs:75-90）。
            // 必须实例化并塞进 App.PluginManager：Config.Load/Save 内部读 ManagerType（Config.cs:435）。
            PluginManager pm = new PluginManager(s_logger, PluginManagerType.Editor);
            App.PluginManager = pm;
            ProfilesLibrary.Initialize(pm.Profiles);

            try { Config.Load(); } // %LocalAppData%\Frosty\editor_config.json，只读
            catch (Exception ex) { s_logger.LogWarning("读取 Frosty 配置失败（按空配置继续）: {0}", ex.Message); }

            s_lightBooted = true;
        }

        private static string TryGetConfiguredGamePath()
        {
            try { return Config.Get<string>("GamePath", null, ConfigScope.Game); }
            catch { return null; } // Games 字典缺当前 profile 键 → KeyNotFoundException（Config.cs:149）
        }

        /// <summary>完整启动：选 profile → 守卫校验 → FileSystem/ResourceManager/AssetManager/TypeLibrary。</summary>
        internal static RuntimeState EnsureFullBoot(Cli cli)
        {
            if (s_state != null) return s_state;
            EnsureLightBoot(cli);

            // 步骤 6：选 profile（大小写不敏感精确匹配）
            // 不能依赖 Initialize 的返回值判存在性：Profile 是 struct，Find 未命中返回 default
            // 而非 null → Initialize 恒返回 true（见 Server.cs 同款注释）。用 HasProfile 判定。
            string profile = cli.Require("profile");
            if (!ProfilesLibrary.HasProfile(profile))
                throw new CliException(3, "未知 profile: " + profile + "（用 profiles list 查看全部）");
            ProfilesLibrary.Initialize(profile);

            // 步骤 8：GamePath 以配置为准；--gamepath 仅本次覆盖，绝不回写（禁调 Config.Save）
            string gamePath = cli.Get("gamepath");
            if (string.IsNullOrEmpty(gamePath)) gamePath = TryGetConfiguredGamePath();
            if (string.IsNullOrEmpty(gamePath))
                throw new CliException(3, "该 profile 未配置 GamePath（%LocalAppData%\\Frosty\\editor_config.json）。" +
                    "请先在 Frosty 编辑器中配置，或用 --gamepath 指定。");
            gamePath = Path.GetFullPath(gamePath);
            if (!Directory.Exists(gamePath))
                throw new CliException(3, "游戏目录不存在: " + gamePath);

            // 审计 constraint 1：CWD（已被钉为安装目录）严禁位于游戏目录或任何 Steam 目录内
            List<string> blocked;
            try { blocked = PathGuard.ComputeBlockedRoots(gamePath); }
            catch (Exception ex)
            {
                throw new CliException(3, "无法解析游戏目录的物理路径（fail-closed）: " + gamePath + " — " + ex.Message);
            }
            if (PathGuard.IsBlocked(Environment.CurrentDirectory, blocked))
                throw new GuardException("Frosty 安装目录（当前工作目录）位于游戏/Steam 目录内，拒绝启动: " + Environment.CurrentDirectory);

            // 步骤 9：缓存目录（相对路径 → 安装目录；缓存已存在时零写盘，见审计 §1.3）
            if (!Directory.Exists("Caches")) Directory.CreateDirectory("Caches");

            // 步骤 10-12
            FileSystem fs = new FileSystem(gamePath);
            foreach (FileSystemSource src in ProfilesLibrary.Sources)
                fs.AddSource(src.Path, src.SubDirs);
            fs.Initialize(null); // bf1（DataVersion 20160927/20161021）不需要加密密钥

            ResourceManager rm = new ResourceManager(fs);
            rm.SetLogger(s_logger);
            rm.Initialize();

            // 步骤 13-15, 18, 19
            AssetManager am = new AssetManager(fs, rm);
            TypeLibrary.Initialize(); // 触发 EbxClasses → Profiles/<SDKFilename>.dll 的 AssemblyResolve
            // 跳过：pluginManager.Initialize()、本地化、Utils.LoadStringList、
            //       RegisterCustomAssetManager("legacy", ...) —— 审计 §3 确认对只读查询零影响
            am.SetLogger(s_logger);
            am.Initialize(true, null); // additionalStartup:true（审计 constraint 7；缓存命中时 DoEbxIndexing 早退）

            s_state = new RuntimeState
            {
                AssetManager = am,
                Profile = ProfilesLibrary.ProfileName,
                GamePath = gamePath,
                BlockedRoots = blocked
            };
            return s_state;
        }
    }
}
