using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyHeadless
{
    /// <summary>
    /// 查询实现。所有输出写入调用方传入的 TextWriter：CLI 模式传 Console.Out，
    /// 服务器模式（Server.cs）传 HTTP 响应流——两条路径共享同一份查询逻辑。
    /// </summary>
    internal static class Commands
    {
        internal static int Execute(Cli cli)
        {
            switch (cli.Noun)
            {
                case "profiles":
                    Boot.EnsureLightBoot(cli);
                    return ProfilesList(cli, Console.Out);

                case "ebx":
                    {
                        RuntimeState st = Boot.EnsureFullBoot(cli);
                        switch (cli.Verb)
                        {
                            case "search": return EbxSearch(cli, st, Console.Out);
                            case "get": return EbxGet(cli, st, Console.Out);
                            case "xml": return EbxXml(cli, st);
                            case "deps": return EbxDeps(cli, st, Console.Out);
                        }
                        break;
                    }

                case "bundles":
                    {
                        RuntimeState st = Boot.EnsureFullBoot(cli);
                        return BundlesList(cli, st, Console.Out);
                    }

                case "bundle":
                    {
                        RuntimeState st = Boot.EnsureFullBoot(cli);
                        return BundleInfo(cli, st, Console.Out);
                    }

                case "res":
                    {
                        RuntimeState st = Boot.EnsureFullBoot(cli);
                        switch (cli.Verb)
                        {
                            case "search": return ResSearch(cli, st, Console.Out);
                            case "get": return ResGet(cli, st, Console.Out);
                            case "export": return ResExportCmd(cli, st);
                        }
                        break;
                    }

                case "types":
                    {
                        Boot.EnsureFullBoot(cli);
                        return TypesSchema(cli, Console.Out);
                    }
            }
            throw new CliException(1, "未知的命令组合: " + cli.Noun + " " + cli.Verb);
        }

        // ---------------------------------------------------------------- profiles

        internal static int ProfilesList(Cli cli, TextWriter tw)
        {
            // ProfilesLibrary 没有公开的 profile 总表，反射私有静态字段 profiles
            // （ProfilesLibrary.cs:32 private static List<Profile> profiles）
            FieldInfo fi = typeof(ProfilesLibrary).GetField("profiles", BindingFlags.NonPublic | BindingFlags.Static);
            if (fi == null)
                throw new CliException(5, "反射失败：ProfilesLibrary.profiles 不存在（SDK 版本不匹配？）");
            List<Profile> all = fi.GetValue(null) as List<Profile>;
            if (all == null)
                throw new CliException(5, "ProfilesLibrary.profiles 类型非预期: " + (fi.GetValue(null) == null ? "null" : fi.GetValue(null).GetType().FullName));

            List<Profile> ordered = all.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("count"); jw.Number(ordered.Count);
            jw.Key("profiles");
            jw.BeginArray();
            foreach (Profile p in ordered)
            {
                bool configured = false;
                string gamePath = null;
                var games = Frosty.Core.Config.Current == null ? null : Frosty.Core.Config.Current.Games;
                if (games != null)
                {
                    foreach (var kv in games)
                    {
                        if (kv.Key != null && kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            configured = true;
                            gamePath = kv.Value == null ? null : kv.Value.GamePath;
                            break;
                        }
                    }
                }
                jw.BeginObject();
                jw.Key("name"); jw.String(p.Name);
                jw.Key("displayName"); jw.String(p.DisplayName);
                jw.Key("dataVersion"); jw.Number(p.DataVersion);
                jw.Key("cacheName"); jw.String(p.CacheName);
                jw.Key("sdkFilename"); jw.String(p.SDKFilename);
                jw.Key("requiresKey"); jw.Bool(p.RequiresKey);
                jw.Key("configured"); jw.Bool(configured);
                jw.Key("gamePath"); jw.String(gamePath);
                jw.EndObject();
            }
            jw.EndArray();
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        // ---------------------------------------------------------------- ebx

        internal static int EbxSearch(Cli cli, RuntimeState st, TextWriter tw)
        {
            string filter = cli.Get("filter") ?? "";
            string type = cli.Get("type") ?? "";
            int limit = cli.LimitOrDefault(100);
            int offset = ParseOffset(cli.Get("offset"));

            // EnumerateEbx 的 type 参数走 TypeLibrary.IsSubClassOf（含子类），未知类型安全返回空集
            IEnumerable<EbxAssetEntry> source = st.AssetManager.EnumerateEbx(type, false, false, true, "");

            int total = 0;
            List<EbxAssetEntry> picked = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in source)
            {
                if (filter.Length > 0 &&
                    (e.Name == null || e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                if (total >= offset)
                {
                    total++;
                    if (limit <= 0 || picked.Count < limit) picked.Add(e);
                }
                else total++;
            }

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("filter"); jw.String(filter.Length == 0 ? null : filter);
            jw.Key("type"); jw.String(type.Length == 0 ? null : type);
            jw.Key("offset"); jw.Number(offset);
            jw.Key("limit"); jw.Number(limit);
            jw.Key("total"); jw.Number(total);
            jw.Key("returned"); jw.Number(picked.Count);
            jw.Key("truncated"); jw.Bool(limit > 0 && total - offset > picked.Count);
            jw.Key("results");
            jw.BeginArray();
            foreach (EbxAssetEntry e in picked)
            {
                jw.BeginObject();
                jw.Key("name"); jw.String(e.Name);
                jw.Key("type"); jw.String(e.Type);
                jw.Key("guid"); jw.String(e.Guid.ToString());
                jw.EndObject();
            }
            jw.EndArray();
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        private static int ParseOffset(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            int n;
            if (!int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n) || n < 0)
                throw new CliException(1, "--offset 必须是非负整数: " + v);
            return n;
        }

        private static EbxAssetEntry RequireEbxEntry(RuntimeState st, string name)
        {
            EbxAssetEntry entry = st.AssetManager.GetEbxEntry(name); // 内部 ToLower + OrdinalIgnoreCase 字典
            if (entry == null)
                throw new CliException(2, "找不到 ebx 资产: " + name);
            return entry;
        }

        private static EbxAsset RequireEbxAsset(RuntimeState st, EbxAssetEntry entry)
        {
            EbxAsset asset;
            try { asset = st.AssetManager.GetEbx(entry, false); }
            catch (Exception ex) { throw new CliException(5, "读取 ebx 失败 (" + entry.Name + "): " + ex.Message); }
            if (asset == null || !asset.IsValid)
                throw new CliException(2, "ebx 资产为空或无效: " + entry.Name);
            return asset;
        }

        internal static int EbxGet(Cli cli, RuntimeState st, TextWriter tw)
        {
            EbxAssetEntry entry;
            string guidStr = cli.Get("guid");
            if (!string.IsNullOrEmpty(guidStr))
            {
                // 按 guid 精确取（ebx get 的引用链跟随入口：PointerRef external 的 fileGuid/classGuid）
                Guid g;
                if (!Guid.TryParse(guidStr, out g))
                    throw new CliException(1, "guid 无效: " + guidStr);
                entry = st.AssetManager.GetEbxEntry(g);
                if (entry == null)
                    throw new CliException(2, "找不到 ebx 资产 (guid=" + guidStr + ")");
            }
            else
            {
                entry = RequireEbxEntry(st, cli.Require("name"));
            }
            EbxAsset asset = RequireEbxAsset(st, entry);
            EbxJson.WriteAsset(new JsonWriter(tw), st, entry, asset);
            return 0;
        }

        /// <summary>生成资产的完整 XML 字节（EbxXmlWriter 用法照抄 EbxToXmlPlugin）。CLI 与服务器共用。</summary>
        internal static byte[] BuildXmlBytes(RuntimeState st, string name)
        {
            EbxAssetEntry entry = RequireEbxEntry(st, name);
            EbxAsset asset = RequireEbxAsset(st, entry);
            using (MemoryStream ms = new MemoryStream())
            {
                using (EbxXmlWriter w = new EbxXmlWriter(ms, st.AssetManager))
                {
                    w.WriteObjects(asset.Objects); // 传 Objects（全部实例），不是 RootObject
                }
                return ms.ToArray(); // MemoryStream 关闭后 ToArray 仍有效
            }
        }

        private static int EbxXml(Cli cli, RuntimeState st)
        {
            string name = cli.Require("name");
            byte[] xml = BuildXmlBytes(st, name);

            string outPath = cli.Get("out");
            if (string.IsNullOrEmpty(outPath))
            {
                using (Stream so = Console.OpenStandardOutput())
                {
                    so.Write(xml, 0, xml.Length);
                    so.Flush();
                }
                return 0;
            }

            // 只读防线（复审加固）：--out 先词法规范化，再做物理路径解析（消除 junction/symlink、
            // subst 盘符、8.3 短名别名——.NET Framework 的 Path.GetFullPath 不解析这些，而
            // File.WriteAllBytes 会按操作系统语义跟随链接），比对黑名单；解析失败 fail-closed 拒绝；
            // 实际写盘与建目录一律使用解析后的物理路径，杜绝“校验词法、写入物理”的错位。
            string full;
            try { full = Path.GetFullPath(outPath); }
            catch (Exception ex) { throw new CliException(1, "--out 路径无效: " + ex.Message); }
            string canonical;
            try { canonical = PathGuard.Canonicalize(full); }
            catch (Exception ex)
            {
                throw new GuardException("无法解析输出路径的物理路径，拒绝写入（fail-closed）: " + full + " — " + ex.Message);
            }
            if (PathGuard.IsBlocked(canonical, st.BlockedRoots))
                throw new GuardException("拒绝写入游戏/Steam 目录: " + canonical);

            string dir = Path.GetDirectoryName(canonical);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(canonical, xml); // 写物理路径：与校验目标一致，不跟随任何别名

            JsonWriter jw = new JsonWriter(Console.Out);
            jw.BeginObject();
            jw.Key("written"); jw.String(canonical);
            jw.Key("bytes"); jw.Number(xml.Length);
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        internal static int EbxDeps(Cli cli, RuntimeState st, TextWriter tw)
        {
            string name = cli.Require("name");
            EbxAssetEntry entry = RequireEbxEntry(st, name);

            // 依赖：本资产引用的其它 ebx（guid 索引解析为资产名）
            List<EbxAssetEntry> deps = new List<EbxAssetEntry>();
            List<Guid> unresolvedDeps = new List<Guid>();
            if (entry.DependentAssets != null)
            {
                foreach (Guid g in entry.DependentAssets)
                {
                    EbxAssetEntry d = st.AssetManager.GetEbxEntry(g);
                    if (d != null) deps.Add(d); else unresolvedDeps.Add(g);
                }
            }

            // 被依赖：全量扫描 DependentAssets（纯内存迭代，无 IO）
            List<EbxAssetEntry> dependents = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in st.AssetManager.EnumerateEbx())
            {
                if (ReferenceEquals(e, entry)) continue;
                if (e.DependentAssets != null && e.DependentAssets.Contains(entry.Guid))
                    dependents.Add(e);
            }

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("name"); jw.String(entry.Name);
            jw.Key("guid"); jw.String(entry.Guid.ToString());

            jw.Key("dependencies");
            jw.BeginArray();
            foreach (EbxAssetEntry d in deps)
            {
                jw.BeginObject();
                jw.Key("name"); jw.String(d.Name);
                jw.Key("type"); jw.String(d.Type);
                jw.Key("guid"); jw.String(d.Guid.ToString());
                jw.EndObject();
            }
            foreach (Guid g in unresolvedDeps) jw.String(g.ToString());
            jw.EndArray();

            jw.Key("dependencyCount"); jw.Number(deps.Count + unresolvedDeps.Count);
            jw.Key("dependents");
            jw.BeginArray();
            foreach (EbxAssetEntry d in dependents)
            {
                jw.BeginObject();
                jw.Key("name"); jw.String(d.Name);
                jw.Key("type"); jw.String(d.Type);
                jw.EndObject();
            }
            jw.EndArray();
            jw.Key("dependentCount"); jw.Number(dependents.Count);
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        // ---------------------------------------------------------------- res

        /// <summary>按 rid（16 位十六进制）或名字解析单个 res 资源。</summary>
        private static ResAssetEntry ResolveResEntry(RuntimeState st, Cli cli)
        {
            string ridStr = cli.Get("rid");
            if (!string.IsNullOrEmpty(ridStr))
            {
                ulong rid;
                if (!ulong.TryParse(ridStr.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out rid))
                    throw new CliException(1, "rid 无效（应为 16 位十六进制）: " + ridStr);
                ResAssetEntry r = st.AssetManager.GetResEntry(rid);
                if (r == null)
                    throw new CliException(2, "找不到 res 资源 (rid=" + ridStr + ")");
                return r;
            }
            string name = cli.Require("name");
            ResAssetEntry entry = null;
            foreach (ResAssetEntry r in st.AssetManager.EnumerateRes())
            {
                if (r.Name != null && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    entry = r;
                    break;
                }
            }
            if (entry == null)
                throw new CliException(2, "找不到 res 资源: " + name);
            return entry;
        }

        /// <summary>单个 res 的元信息；贴图附带尺寸/格式/mip 链（读取 chunk 元数据，不解码像素）。</summary>
        internal static int ResGet(Cli cli, RuntimeState st, TextWriter tw)
        {
            ResAssetEntry r = ResolveResEntry(st, cli);

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("name"); jw.String(r.Name);
            jw.Key("type"); jw.String(r.Type);
            jw.Key("rid"); jw.String(r.ResRid.ToString("X16"));
            jw.Key("sha1"); jw.String(r.Sha1.ToString());
            jw.Key("size"); jw.Number(r.Size);

            if (r.Type != null && r.Type.Equals("Texture", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FrostySdk.Resources.Texture tex = st.AssetManager.GetResAs<FrostySdk.Resources.Texture>(r);
                    if (tex != null)
                    {
                        jw.Key("texture");
                        jw.BeginObject();
                        jw.Key("width"); jw.Number(tex.Width);
                        jw.Key("height"); jw.Number(tex.Height);
                        jw.Key("pixelFormat"); jw.String(tex.PixelFormat);
                        jw.Key("mipCount"); jw.Number(tex.MipCount);
                        jw.Key("firstMip"); jw.Number(tex.FirstMip);
                        jw.Key("textureType"); jw.String(tex.Type.ToString());
                        jw.Key("sliceCount"); jw.Number(tex.SliceCount);
                        jw.EndObject();
                    }
                }
                catch (Exception ex)
                {
                    jw.Key("textureError"); jw.String(ex.Message);
                }
            }

            jw.EndObject();
            jw.Flush();
            return 0;
        }

        /// <summary>CLI 导出：字节到 stdout，或 --out 落盘（走物理路径防线）。</summary>
        internal static int ResExportCmd(Cli cli, RuntimeState st)
        {
            ResAssetEntry r = ResolveResEntry(st, cli);
            byte[] data;
            string contentType;
            ResExport.Export(st, r, cli.Get("format"), out data, out contentType);

            string outPath = cli.Get("out");
            if (string.IsNullOrEmpty(outPath))
            {
                using (Stream so = Console.OpenStandardOutput())
                {
                    so.Write(data, 0, data.Length);
                    so.Flush();
                }
                return 0;
            }

            // 只读防线（与 ebx xml --out 相同）：词法规范化 → 物理路径解析比对黑名单 → 写物理路径
            string full;
            try { full = Path.GetFullPath(outPath); }
            catch (Exception ex) { throw new CliException(1, "--out 路径无效: " + ex.Message); }
            string canonical;
            try { canonical = PathGuard.Canonicalize(full); }
            catch (Exception ex)
            {
                throw new GuardException("无法解析输出路径的物理路径，拒绝写入（fail-closed）: " + full + " — " + ex.Message);
            }
            if (PathGuard.IsBlocked(canonical, st.BlockedRoots))
                throw new GuardException("拒绝写入游戏/Steam 目录: " + canonical);

            string dir = Path.GetDirectoryName(canonical);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(canonical, data);

            JsonWriter jw = new JsonWriter(Console.Out);
            jw.BeginObject();
            jw.Key("written"); jw.String(canonical);
            jw.Key("bytes"); jw.Number(data.Length);
            jw.Key("contentType"); jw.String(contentType);
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        // ---------------------------------------------------------------- types

        /// <summary>ebx 类型 schema：字段名/字段类型/transient 标记（TypeLibrary 的 EbxClasses 动态类型）。</summary>
        internal static int TypesSchema(Cli cli, TextWriter tw)
        {
            string name = cli.Require("name");
            Type t = TypeLibrary.GetType(name);
            if (t == null)
                throw new CliException(2, "未知类型: " + name);

            PropertyInfo[] props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(props, delegate (PropertyInfo a, PropertyInfo b) { return a.MetadataToken.CompareTo(b.MetadataToken); });

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("name"); jw.String(t.Name);
            jw.Key("baseType"); jw.String(t.BaseType == null ? null : t.BaseType.Name);
            jw.Key("fields");
            jw.BeginArray();
            foreach (PropertyInfo pi in props)
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                jw.BeginObject();
                jw.Key("name"); jw.String(pi.Name);
                jw.Key("type"); jw.String(pi.PropertyType.Name);
                jw.Key("isTransient"); jw.Bool(pi.GetCustomAttribute<FrostySdk.Attributes.IsTransientAttribute>() != null);
                jw.EndObject();
            }
            jw.EndArray();
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        // ---------------------------------------------------------------- res search

        /// <summary>
        /// res 资源检索（贴图/网格等二进制资源的元信息；名字子串 + res 类型名子串过滤）。
        /// 只列元数据：名字、类型、rid、所在 bundle——二进制本体导出不在只读范围内。
        /// </summary>
        internal static int ResSearch(Cli cli, RuntimeState st, TextWriter tw)
        {
            string filter = cli.Get("filter") ?? "";
            string type = cli.Get("type") ?? "";
            int limit = cli.LimitOrDefault(100);
            int offset = ParseOffset(cli.Get("offset"));

            int total = 0;
            List<ResAssetEntry> picked = new List<ResAssetEntry>();
            foreach (ResAssetEntry r in st.AssetManager.EnumerateRes())
            {
                if (filter.Length > 0 &&
                    (r.Name == null || r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                if (type.Length > 0 &&
                    (r.Type == null || r.Type.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                if (total >= offset)
                {
                    total++;
                    if (limit <= 0 || picked.Count < limit) picked.Add(r);
                }
                else total++;
            }

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("filter"); jw.String(filter.Length == 0 ? null : filter);
            jw.Key("type"); jw.String(type.Length == 0 ? null : type);
            jw.Key("offset"); jw.Number(offset);
            jw.Key("limit"); jw.Number(limit);
            jw.Key("total"); jw.Number(total);
            jw.Key("returned"); jw.Number(picked.Count);
            jw.Key("truncated"); jw.Bool(limit > 0 && total - offset > picked.Count);
            jw.Key("results");
            jw.BeginArray();
            foreach (ResAssetEntry r in picked)
            {
                jw.BeginObject();
                jw.Key("name"); jw.String(r.Name);
                jw.Key("type"); jw.String(r.Type);
                jw.Key("rid"); jw.String(r.ResRid.ToString("X16"));
                jw.EndObject();
            }
            jw.EndArray();
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        // ---------------------------------------------------------------- bundles

        internal static int BundlesList(Cli cli, RuntimeState st, TextWriter tw)
        {
            string filter = cli.Get("filter") ?? "";
            int limit = cli.LimitOrDefault(100);

            int total = 0;
            List<BundleEntry> picked = new List<BundleEntry>();
            foreach (BundleEntry b in st.AssetManager.EnumerateBundles())
            {
                if (filter.Length > 0 &&
                    (b.Name == null || b.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                total++;
                if (limit <= 0 || picked.Count < limit) picked.Add(b);
            }

            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("filter"); jw.String(filter.Length == 0 ? null : filter);
            jw.Key("total"); jw.Number(total);
            jw.Key("returned"); jw.Number(picked.Count);
            jw.Key("truncated"); jw.Bool(limit > 0 && total > picked.Count);
            jw.Key("bundles");
            jw.BeginArray();
            foreach (BundleEntry b in picked)
            {
                SuperBundleEntry sb = st.AssetManager.GetSuperBundle(b.SuperBundleId);
                jw.BeginObject();
                jw.Key("name"); jw.String(b.Name);
                jw.Key("superbundle"); jw.String(sb == null ? null : sb.Name);
                jw.Key("type"); jw.String(b.Type.ToString());
                jw.EndObject();
            }
            jw.EndArray();
            jw.EndObject();
            jw.Flush();
            return 0;
        }

        internal static int BundleInfo(Cli cli, RuntimeState st, TextWriter tw)
        {
            string name = cli.Require("name");
            int id = st.AssetManager.GetBundleId(name); // 精确匹配（区分大小写），未找到返回 -1
            if (id == -1)
            {
                foreach (BundleEntry b in st.AssetManager.EnumerateBundles())
                {
                    if (b.Name != null && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        id = st.AssetManager.GetBundleId(b.Name);
                        break;
                    }
                }
            }
            if (id == -1)
                throw new CliException(2, "找不到 bundle: " + name);
            BundleEntry be = st.AssetManager.GetBundleEntry(id);
            if (be == null)
                throw new CliException(2, "bundle id 无效: " + id);

            const int EbxNameCap = 2000;
            List<string> ebxNames = new List<string>();
            bool ebxTruncated = false;
            int ebxCount = 0, resCount = 0, chunkCount = 0;
            foreach (EbxAssetEntry e in st.AssetManager.EnumerateEbx(be))
            {
                ebxCount++;
                if (ebxNames.Count < EbxNameCap) ebxNames.Add(e.Name);
                else ebxTruncated = true;
            }
            foreach (ResAssetEntry r in st.AssetManager.EnumerateRes(be)) resCount++;
            foreach (ChunkAssetEntry c in st.AssetManager.EnumerateChunks(be)) chunkCount++;

            SuperBundleEntry sb = st.AssetManager.GetSuperBundle(be.SuperBundleId);
            JsonWriter jw = new JsonWriter(tw);
            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("name"); jw.String(be.Name);
            jw.Key("id"); jw.Number(id);
            jw.Key("superbundle"); jw.String(sb == null ? null : sb.Name);
            jw.Key("type"); jw.String(be.Type.ToString());
            jw.Key("blueprint"); jw.String(be.Blueprint == null ? null : be.Blueprint.Name);
            jw.Key("counts");
            jw.BeginObject();
            jw.Key("ebx"); jw.Number(ebxCount);
            jw.Key("res"); jw.Number(resCount);
            jw.Key("chunk"); jw.Number(chunkCount);
            jw.EndObject();
            jw.Key("ebxTruncated"); jw.Bool(ebxTruncated);
            jw.Key("ebx");
            jw.BeginArray();
            foreach (string n in ebxNames) jw.String(n);
            jw.EndArray();
            jw.EndObject();
            jw.Flush();
            return 0;
        }
    }
}
