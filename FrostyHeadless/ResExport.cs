using System;
using System.IO;
using System.Runtime.InteropServices;
using Frosty.Core.Viewport;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;

namespace FrostyHeadless
{
    /// <summary>
    /// res 二进制导出：贴图 → DDS（照抄 TexturePlugin/TextureExporter.WriteToDDS 的头与数据布局）
    /// → PNG（安装目录 ThirdParty/dxtex.dll 的 ConvertDDSToImage，与编辑器导出 PNG 同一条原生链路）；
    /// 其余 res 类型 → 原始字节流（AssetManager.GetRawStream，任意 CAS/SuperBundle/Cache 位置通吃）。
    /// 全部只读：不落盘，字节返回给调用方。
    /// </summary>
    internal static class ResExport
    {
        // ---- dxtex 绑定（"thirdparty/dxtex.dll" 相对路径，CWD 已钉在安装目录）----

        internal enum DxtexImageFormat
        {
            PNG = 0,
            TGA = 1,
            HDR = 2,
            DDS = 3,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlobData
        {
            private IntPtr data;
            private long size;
            internal byte[] ToArray()
            {
                byte[] a = new byte[size];
                Marshal.Copy(data, a, 0, (int)size);
                return a;
            }
        }

        private static class Dxtex
        {
            [DllImport("thirdparty/dxtex.dll")]
            public static extern void ConvertDDSToImage(byte[] pData, long iDataSize, DxtexImageFormat format, ref BlobData pOutData);

            [DllImport("thirdparty/dxtex.dll")]
            public static extern void ReleaseBlob(BlobData pData);
        }

        /// <summary>DDS → PNG（BC1/2/3/4/5/6/7 解压由 DirectXTex 完成）。失败抛 CliException(5)。</summary>
        internal static byte[] DdsToPng(byte[] dds)
        {
            BlobData blob = new BlobData();
            try
            {
                Dxtex.ConvertDDSToImage(dds, dds.Length, DxtexImageFormat.PNG, ref blob);
                return blob.ToArray();
            }
            catch (Exception ex)
            {
                throw new CliException(5, "dxtex DDS→PNG 转换失败: " + ex.Message);
            }
            finally
            {
                try { Dxtex.ReleaseBlob(blob); } catch { }
            }
        }

        // ---- Texture → DDS ----

        /// <summary>
        /// 照抄 TexturePlugin/TextureExporter.cs WriteToDDS()（v1.0.6.3）：DDSHeader 字段、
        /// 格式映射表（含 SRGB 标志替换）、cube/array 的切片交错布局，全部保持一致。
        /// </summary>
        internal static byte[] TextureToDds(Texture tex)
        {
            TextureUtils.DDSHeader header = new TextureUtils.DDSHeader
            {
                dwHeight = tex.Height,
                dwWidth = tex.Width,
                dwPitchOrLinearSize = (int)tex.MipSizes[0],
                dwMipMapCount = tex.MipCount,
            };
            if (tex.MipCount > 1)
            {
                header.dwFlags = (TextureUtils.DDSFlags)((int)header.dwFlags | 0x20000);
                header.dwCaps = (TextureUtils.DDSCaps)((int)header.dwCaps | 0x400008);
            }
            int texType = (int)tex.Type;
            switch (texType)
            {
                case 0: // 2D
                    header.ExtendedHeader.resourceDimension = (ResourceDimension)3;
                    header.ExtendedHeader.arraySize = 1u;
                    break;
                case 3: // 2D array
                    header.ExtendedHeader.resourceDimension = (ResourceDimension)3;
                    header.ExtendedHeader.arraySize = tex.Depth;
                    break;
                case 1: // cube
                    header.dwCaps2 = (TextureUtils.DDSCaps2)65024;
                    header.ExtendedHeader.resourceDimension = (ResourceDimension)3;
                    header.ExtendedHeader.arraySize = 1u;
                    header.ExtendedHeader.miscFlag = 4u;
                    break;
                case 2: // 3D
                    header.dwFlags = (TextureUtils.DDSFlags)((int)header.dwFlags | 0x800000);
                    header.dwCaps2 = (TextureUtils.DDSCaps2)((int)header.dwCaps2 | 0x200000);
                    header.dwDepth = tex.Depth;
                    header.ExtendedHeader.resourceDimension = (ResourceDimension)4;
                    header.ExtendedHeader.arraySize = 1u;
                    break;
            }

            string fmt = tex.PixelFormat;
            if (fmt.StartsWith("BC", StringComparison.Ordinal) && ((int)tex.Flags & 2) != 0)
                fmt = fmt.Replace("UNORM", "SRGB");

            switch (fmt)
            {
                case "NormalDXT1": header.ddspf.dwFourCC = 827611204; break;
                case "NormalDXN": header.ddspf.dwFourCC = 843666497; break;
                case "BC1A_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)72; break;
                case "BC1A_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)71; break;
                case "BC1_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)72; break;
                case "BC1_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)71; break;
                case "BC2_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)75; break;
                case "BC3_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)78; break;
                case "BC3A_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)78; break;
                case "BC3_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)77; break;
                case "BC3A_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)77; break;
                case "BC4_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)80; break;
                case "BC5_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)83; break;
                case "BC6U_FLOAT": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)95; break;
                case "BC7": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)98; break;
                case "BC7_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)99; break;
                case "BC7_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)98; break;
                case "R8_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)61; break;
                case "R16G16B16A16_FLOAT": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)10; break;
                case "ARGB32F": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)2; break;
                case "R32G32B32A32_FLOAT": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)2; break;
                case "R9G9B9E5F": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)67; break;
                case "R9G9B9E5_FLOAT": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)67; break;
                case "R8G8B8A8_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)28; break;
                case "R8G8B8A8_SRGB": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)29; break;
                case "R10G10B10A2_UNORM": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)24; break;
                case "L8": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)61; break;
                case "L16": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)56; break;
                case "ARGB8888": header.HasExtendedHeader = true; header.ExtendedHeader.dxgiFormat = (SharpDX.DXGI.Format)28; break;
                default: header.ddspf.dwFourCC = 0; break;
            }
            if (header.HasExtendedHeader)
                header.ddspf.dwFourCC = 808540228; // "DX10"

            // 数据体：2D 直接整块；cube/array 按切片交错重排（照 TextureExporter）
            byte[] dataBytes;
            MemoryStream ms = tex.Data as MemoryStream;
            if (texType == 1 || texType == 3)
            {
                int slices = texType == 1 ? 6 : tex.Depth;
                uint[] mipOffsets = new uint[tex.MipCount];
                for (int i = 0; i < tex.MipCount - 1; i++)
                    mipOffsets[i + 1] = mipOffsets[i] + tex.MipSizes[i] * (uint)slices;

                using (MemoryStream outMs = new MemoryStream())
                using (NativeWriter nw = new NativeWriter(outMs))
                {
                    header.Write(nw);
                    byte[] buffer = new byte[tex.MipSizes[0]];
                    for (int s = 0; s < slices; s++)
                    {
                        for (int m = 0; m < tex.MipCount; m++)
                        {
                            int sz = (int)tex.MipSizes[m];
                            Stream src = tex.Data;
                            src.Position = mipOffsets[m] + sz * s;
                            if (buffer.Length < sz) buffer = new byte[sz];
                            src.Read(buffer, 0, sz);
                            nw.Write(buffer, 0, sz);
                        }
                    }
                    dataBytes = outMs.ToArray();
                    return dataBytes;
                }
            }

            using (MemoryStream outMs = new MemoryStream())
            using (NativeWriter nw = new NativeWriter(outMs))
            {
                header.Write(nw);
                if (ms != null) nw.Write(ms.ToArray());
                else
                {
                    Stream src = tex.Data;
                    src.Position = 0;
                    byte[] buf = new byte[81920];
                    int read;
                    while ((read = src.Read(buf, 0, buf.Length)) > 0)
                        nw.Write(buf, 0, read);
                }
                return outMs.ToArray();
            }
        }

        // ---- 通用导出入口 ----

        /// <summary>
        /// 导出一个 res 资源。format：png/dds 仅贴图；raw 任意类型（原始字节流）。
        /// 贴图缺省 png，其他类型缺省 raw。返回 (字节, MIME 类型)。
        /// </summary>
        internal static void Export(RuntimeState st, ResAssetEntry entry, string format, out byte[] data, out string contentType)
        {
            bool isTexture = entry.Type != null && entry.Type.Equals("Texture", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(format))
                format = isTexture ? "png" : "raw";
            format = format.ToLowerInvariant();

            if (format == "raw")
            {
                using (Stream s = st.AssetManager.GetRawStream(entry))
                {
                    if (s == null) throw new CliException(2, "资源没有原始数据流: " + entry.Name);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        data = ms.ToArray();
                    }
                }
                contentType = "application/octet-stream";
                return;
            }

            if (!isTexture)
                throw new CliException(1, "format=" + format + " 只支持 Texture 类型资源（该资源是 " + entry.Type + "，用 format=raw）");

            Texture tex;
            try { tex = st.AssetManager.GetResAs<Texture>(entry); }
            catch (Exception ex) { throw new CliException(5, "读取贴图资源失败 (" + entry.Name + "): " + ex.Message); }
            if (tex == null) throw new CliException(2, "贴图资源为空: " + entry.Name);

            byte[] dds = TextureToDds(tex);
            if (format == "dds")
            {
                data = dds;
                contentType = "application/octet-stream";
                return;
            }
            if (format == "png")
            {
                data = DdsToPng(dds);
                contentType = "image/png";
                return;
            }
            throw new CliException(1, "未知 format: " + format + "（png | dds | raw）");
        }
    }
}
