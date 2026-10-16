using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrostyHeadless
{
    /// <summary>
    /// 极简流式 JSON 写入器（缩进输出）。手写而非引用 Newtonsoft，
    /// 避免与安装目录 ThirdParty/Newtonsoft.Json.dll 的版本/绑定重定向纠缠。
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly TextWriter _w;
        private readonly bool _indent;
        private readonly Stack<Container> _containers = new Stack<Container>();

        private sealed class Container
        {
            public readonly bool IsObject;
            public bool First = true;
            public Container(bool isObject) { IsObject = isObject; }
        }

        public JsonWriter(TextWriter writer, bool indent = true)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            _w = writer;
            _indent = indent;
        }

        public void Flush() { _w.Flush(); }

        private void NewLine()
        {
            if (!_indent) return;
            _w.WriteLine();
            _w.Write(new string(' ', _containers.Count * 2));
        }

        /// <summary>数组项之间的逗号/换行；对象上下文里键已就位，值紧跟冒号。</summary>
        private void BeforeValue()
        {
            if (_containers.Count == 0) return;
            Container c = _containers.Peek();
            if (c.IsObject) return;
            if (!c.First) _w.Write(',');
            NewLine();
            c.First = false;
        }

        public void BeginObject() { BeforeValue(); _w.Write('{'); _containers.Push(new Container(true)); }
        public void BeginArray() { BeforeValue(); _w.Write('['); _containers.Push(new Container(false)); }

        public void EndObject()
        {
            Container c = _containers.Pop();
            if (!c.First) NewLine();
            _w.Write('}');
        }

        public void EndArray()
        {
            Container c = _containers.Pop();
            if (!c.First) NewLine();
            _w.Write(']');
        }

        /// <summary>在对象上下文写键（随后必须紧跟一个值）。</summary>
        public void Key(string key)
        {
            Container c = _containers.Peek();
            if (!c.IsObject) throw new InvalidOperationException("Key() 只能在对象上下文调用");
            if (!c.First) _w.Write(',');
            NewLine();
            c.First = false;
            WriteEscaped(key ?? "");
            _w.Write(": ");
        }

        public void Null() { BeforeValue(); _w.Write("null"); }

        public void String(string s)
        {
            if (s == null) { Null(); return; }
            BeforeValue();
            WriteEscaped(s);
        }

        public void Bool(bool b) { BeforeValue(); _w.Write(b ? "true" : "false"); }

        public void Number(long v) { BeforeValue(); _w.Write(v.ToString(CultureInfo.InvariantCulture)); }

        public void Number(ulong v) { BeforeValue(); _w.Write(v.ToString(CultureInfo.InvariantCulture)); }

        /// <summary>写入调用方已格式化好的数字字面量（浮点用 "R" + InvariantCulture）。</summary>
        public void RawNumber(string formatted)
        {
            BeforeValue();
            _w.Write(formatted ?? "null");
        }

        private void WriteEscaped(string s)
        {
            _w.Write('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': _w.Write("\\\""); break;
                    case '\\': _w.Write("\\\\"); break;
                    case '\b': _w.Write("\\b"); break;
                    case '\f': _w.Write("\\f"); break;
                    case '\n': _w.Write("\\n"); break;
                    case '\r': _w.Write("\\r"); break;
                    case '\t': _w.Write("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            _w.Write("\\u");
                            _w.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _w.Write(c);
                        }
                        break;
                }
            }
            _w.Write('"');
        }
    }
}
