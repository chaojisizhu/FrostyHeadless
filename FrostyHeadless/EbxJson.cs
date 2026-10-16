using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyHeadless
{
    /// <summary>
    /// EbxAsset → JSON 对象树。遍历模板照抄 FrostySdk.IO.EbxXmlWriter
    /// （属性按 MetadataToken 排序保持声明序、跳过 [IsTransient]、List/结构体递归展开），
    /// PointerRef/ResourceRef 解析为可读引用；不使用 dynamic，无环风险（Internal 指针只引用 guid）。
    /// </summary>
    internal static class EbxJson
    {
        /// <summary>资产级 JSON：头部元信息 + 全部实例（EbxAsset.Objects 顺序）。</summary>
        internal static void WriteAsset(JsonWriter jw, RuntimeState st, EbxAssetEntry entry, EbxAsset asset)
        {
            EbxJsonContext ctx = new EbxJsonContext(st.AssetManager);

            jw.BeginObject();
            jw.Key("profile"); jw.String(st.Profile);
            jw.Key("name"); jw.String(entry.Name);
            jw.Key("type"); jw.String(entry.Type);
            jw.Key("guid"); jw.String(entry.Guid.ToString());
            jw.Key("sha1"); jw.String(entry.Sha1.ToString());
            jw.Key("size"); jw.Number(entry.Size);
            jw.Key("fileGuid"); jw.String(asset.FileGuid.ToString());

            // 所在 bundle
            jw.Key("bundles");
            jw.BeginArray();
            if (entry.Bundles != null)
            {
                foreach (int bid in entry.Bundles)
                {
                    BundleEntry b = st.AssetManager.GetBundleEntry(bid);
                    jw.String(b == null ? null : b.Name);
                }
            }
            jw.EndArray();

            // 依赖的其它 ebx（entry.DependentAssets，索引期收集；解析为资产名）
            jw.Key("dependencies");
            jw.BeginArray();
            if (entry.DependentAssets != null)
            {
                foreach (Guid g in entry.DependentAssets)
                {
                    jw.BeginObject();
                    jw.Key("guid"); jw.String(g.ToString());
                    EbxAssetEntry d = ctx.LookupEbx(g);
                    jw.Key("name"); jw.String(d == null ? null : d.Name);
                    jw.Key("type"); jw.String(d == null ? null : d.Type);
                    jw.EndObject();
                }
            }
            jw.EndArray();

            // 全部实例（含内嵌非导出实例），objects[0] 为根实例
            jw.Key("objectCount");
            int count = 0;
            foreach (object o in asset.Objects) count++;
            jw.Number(count);

            jw.Key("objects");
            jw.BeginArray();
            int i = 0;
            foreach (object o in asset.Objects)
            {
                WriteObjectNode(jw, ctx, o, i, i == 0);
                i++;
            }
            jw.EndArray();

            jw.EndObject();
            jw.Flush();
        }

        /// <summary>实例 guid：私有字段 __Guid（AssetClassGuid）。已导出 → guid 字符串；否则 internal:&lt;id&gt;。</summary>
        private static string InstanceGuidString(object obj)
        {
            if (obj == null) return null;
            FieldInfo f = obj.GetType().GetField("__Guid", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) return null;
            AssetClassGuid g = (AssetClassGuid)f.GetValue(obj);
            return g.IsExported ? g.ExportedGuid.ToString() : ("internal:" + g.InternalId.ToString("x12"));
        }

        private static void WriteObjectNode(JsonWriter jw, EbxJsonContext ctx, object obj, int index, bool isRoot)
        {
            Type t = obj.GetType();
            jw.BeginObject();
            jw.Key("$index"); jw.Number(index);
            if (isRoot) { jw.Key("$root"); jw.Bool(true); }
            jw.Key("$type"); jw.String(t.Name);
            jw.Key("$guid"); jw.String(InstanceGuidString(obj));
            jw.Key("fields");
            WriteFields(jw, ctx, obj, t);
            jw.EndObject();
        }

        /// <summary>属性遍历：按 MetadataToken 升序（声明序），跳过索引属性与 [IsTransient]（EbxXmlWriter.cs:58-83）。</summary>
        private static void WriteFields(JsonWriter jw, EbxJsonContext ctx, object obj, Type t)
        {
            PropertyInfo[] props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(props, delegate (PropertyInfo a, PropertyInfo b) { return a.MetadataToken.CompareTo(b.MetadataToken); });
            jw.BeginObject();
            foreach (PropertyInfo pi in props)
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                if (pi.GetCustomAttribute<FrostySdk.Attributes.IsTransientAttribute>() != null) continue;

                object value;
                try { value = pi.GetValue(obj, null); }
                catch (Exception ex)
                {
                    jw.Key(pi.Name); jw.String("$error: " + ex.Message);
                    continue;
                }
                jw.Key(pi.Name);
                WriteValue(jw, ctx, value);
            }
            jw.EndObject();
        }

        private static void WriteValue(JsonWriter jw, EbxJsonContext ctx, object value)
        {
            if (value == null) { jw.Null(); return; }
            Type t = value.GetType();

            // FrostySdk.Ebx 值语义包装类型
            if (t == typeof(CString)) { jw.String(value.ToString() ?? ""); return; }
            if (t == typeof(PointerRef)) { WritePointer(jw, ctx, (PointerRef)value); return; }
            if (t == typeof(ResourceRef)) { WriteResource(jw, ctx, (ResourceRef)value); return; }
            if (t == typeof(FileRef)) { jw.String(value.ToString() ?? ""); return; }
            if (t == typeof(TypeRef)) { WriteTypeRef(jw, (TypeRef)value); return; }
            if (t == typeof(BoxedValueRef)) { WriteBoxed(jw, ctx, (BoxedValueRef)value); return; }

            if (t.IsEnum) { jw.String(value.ToString()); return; }

            if (value is IList)
            {
                jw.BeginArray();
                foreach (object item in (IList)value) WriteValue(jw, ctx, item);
                jw.EndArray();
                return;
            }

            if (t == typeof(bool)) { jw.Bool((bool)value); return; }
            if (t == typeof(byte)) { jw.Number((long)(byte)value); return; }
            if (t == typeof(sbyte)) { jw.Number((long)(sbyte)value); return; }
            if (t == typeof(short)) { jw.Number((long)(short)value); return; }
            if (t == typeof(ushort)) { jw.Number((long)(ushort)value); return; }
            if (t == typeof(int)) { jw.Number((long)(int)value); return; }
            if (t == typeof(uint)) { jw.Number((long)(uint)value); return; }
            if (t == typeof(long)) { jw.Number((long)value); return; }
            if (t == typeof(ulong)) { jw.Number((ulong)value); return; }
            if (t == typeof(float)) { WriteFloat(jw, (float)value); return; }
            if (t == typeof(double)) { WriteDouble(jw, (double)value); return; }
            if (t == typeof(Guid)) { jw.String(((Guid)value).ToString()); return; }
            if (t == typeof(string)) { jw.String((string)value); return; }
            if (t == typeof(char)) { jw.String(((char)value).ToString()); return; }

            // FrostySdk.Ebx 结构体（Vec3 等）：递归展开（判定照抄 EbxXmlWriter.cs:117）
            if (t.IsValueType && t.Namespace == "FrostySdk.Ebx" && t.BaseType != typeof(Enum))
            {
                jw.BeginObject();
                string g = InstanceGuidString(value);
                if (g != null) { jw.Key("$guid"); jw.String(g); }
                WriteFields(jw, ctx, value, t);
                jw.EndObject();
                return;
            }

            // EbxClasses 引用类型实例（如 List 字段里直接持有的内嵌实例）：深展开全部字段
            // （与 EbxXmlWriter 的 ClassToXml 递归一致）。环保护：实例在展开栈上再出现时只出浅引用。
            if (t.Namespace == "FrostySdk.Ebx")
            {
                WriteClassInstance(jw, ctx, value, t);
                return;
            }

            jw.String(Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString());
        }

        /// <summary>深展开一个 EbxClasses 实例（类）。同一实例重复出现（环）时输出 $cycle 浅引用。</summary>
        private static void WriteClassInstance(JsonWriter jw, EbxJsonContext ctx, object obj, Type t)
        {
            if (ctx.OnStack(obj))
            {
                jw.BeginObject();
                jw.Key("$cycle"); jw.Bool(true);
                jw.Key("$type"); jw.String(t.Name);
                jw.Key("$guid"); jw.String(InstanceGuidString(obj));
                jw.EndObject();
                return;
            }
            try
            {
                jw.BeginObject();
                jw.Key("$type"); jw.String(t.Name);
                string g = InstanceGuidString(obj);
                if (g != null) { jw.Key("$guid"); jw.String(g); }
                jw.Key("fields");
                WriteFields(jw, ctx, obj, t);
                jw.EndObject();
            }
            finally
            {
                ctx.PopStack(obj);
            }
        }

        private static void WritePointer(JsonWriter jw, EbxJsonContext ctx, PointerRef pr)
        {
            if (pr.Type == PointerRefType.Null) { jw.Null(); return; }
            jw.BeginObject();
            jw.Key("$kind"); jw.String("PointerRef");
            if (pr.Type == PointerRefType.Internal)
            {
                jw.Key("ref"); jw.String("internal");
                object target = pr.Internal;
                if (target == null)
                {
                    jw.Key("targetType"); jw.Null();
                    jw.Key("targetGuid"); jw.Null();
                }
                else
                {
                    jw.Key("targetType"); jw.String(target.GetType().Name);
                    jw.Key("targetGuid"); jw.String(InstanceGuidString(target));
                }
            }
            else
            {
                jw.Key("ref"); jw.String("external");
                jw.Key("fileGuid"); jw.String(pr.External.FileGuid.ToString());
                jw.Key("classGuid"); jw.String(pr.External.ClassGuid.ToString());
                EbxAssetEntry target = ctx.LookupEbx(pr.External.FileGuid);
                jw.Key("asset"); jw.String(target == null ? null : target.Name);
            }
            jw.EndObject();
        }

        private static void WriteResource(JsonWriter jw, EbxJsonContext ctx, ResourceRef rr)
        {
            ulong rid = (ulong)rr;
            jw.BeginObject();
            jw.Key("$kind"); jw.String("ResourceRef");
            jw.Key("rid"); jw.String(rid.ToString("X16"));
            ResAssetEntry re = ctx.LookupRes(rid);
            jw.Key("asset"); jw.String(re == null ? null : re.Name);
            if (re != null) { jw.Key("resType"); jw.String(re.Type); }
            jw.EndObject();
        }

        private static void WriteTypeRef(JsonWriter jw, TypeRef tr)
        {
            jw.BeginObject();
            jw.Key("$kind"); jw.String("TypeRef");
            jw.Key("name"); jw.String(tr.Name);
            jw.Key("guid"); jw.String(tr.Guid.ToString());
            jw.EndObject();
        }

        private static void WriteBoxed(JsonWriter jw, EbxJsonContext ctx, BoxedValueRef bvr)
        {
            jw.BeginObject();
            jw.Key("$kind"); jw.String("BoxedValueRef");
            jw.Key("type"); jw.String(bvr.Type.ToString());
            jw.Key("value"); WriteValue(jw, ctx, bvr.Value);
            jw.EndObject();
        }

        private static void WriteFloat(JsonWriter jw, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) { jw.String(f.ToString(CultureInfo.InvariantCulture)); return; }
            jw.RawNumber(f.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteDouble(JsonWriter jw, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { jw.String(d.ToString(CultureInfo.InvariantCulture)); return; }
            jw.RawNumber(d.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>guid/rid → entry 解析缓存 + 深展开环保护栈（object 默认引用相等语义）。</summary>
    internal sealed class EbxJsonContext
    {
        private readonly AssetManager _am;
        private readonly Dictionary<Guid, EbxAssetEntry> _ebxByGuid = new Dictionary<Guid, EbxAssetEntry>();
        private readonly Dictionary<ulong, ResAssetEntry> _resByRid = new Dictionary<ulong, ResAssetEntry>();
        private readonly HashSet<object> _expandStack = new HashSet<object>();

        internal EbxJsonContext(AssetManager am) { _am = am; }

        /// <summary>入栈；已在栈上（环）返回 true。</summary>
        internal bool OnStack(object o) { return !_expandStack.Add(o); }

        internal void PopStack(object o) { _expandStack.Remove(o); }

        internal EbxAssetEntry LookupEbx(Guid g)
        {
            EbxAssetEntry e;
            if (!_ebxByGuid.TryGetValue(g, out e))
            {
                e = _am.GetEbxEntry(g);
                _ebxByGuid[g] = e;
            }
            return e;
        }

        internal ResAssetEntry LookupRes(ulong rid)
        {
            ResAssetEntry r;
            if (!_resByRid.TryGetValue(rid, out r))
            {
                r = _am.GetResEntry(rid);
                _resByRid[rid] = r;
            }
            return r;
        }
    }
}
