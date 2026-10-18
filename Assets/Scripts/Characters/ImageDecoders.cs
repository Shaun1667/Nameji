using System;
using System.IO;
using UnityEngine;

namespace Shimeji.Characters
{
    /// <summary>
    /// Texture2D.LoadImage가 지원하지 않는 텍스처 형식의 최소 디코더.
    /// FBX 모델은 .tga/.bmp 텍스처를 많이 쓴다.
    /// </summary>
    public static class ImageDecoders
    {
        /// <summary>바이트에서 텍스처 생성. PNG/JPG는 Unity, TGA/BMP는 자체 디코더. 실패하면 null.</summary>
        public static Texture2D Decode(byte[] data, string nameOrHint)
        {
            if (data == null || data.Length < 4) return null;
            var hint = (nameOrHint ?? "").ToLowerInvariant();
            bool isPng = data[0] == 0x89 && data[1] == 0x50;
            bool isJpg = data[0] == 0xFF && data[1] == 0xD8;
            bool isBmp = data[0] == (byte)'B' && data[1] == (byte)'M';
            Texture2D tex = null;
            try
            {
                if (isPng || isJpg)
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!tex.LoadImage(data, false)) { UnityGlue.SafeDestroy(tex); return null; }
                }
                else if (isBmp) tex = DecodeBmp(data);
                else if (hint.EndsWith("tga") || hint.EndsWith(".tga")) tex = DecodeTga(data);
                else tex = DecodeTga(data); // TGA는 매직 넘버가 없어서 마지막으로 시도
            }
            catch (Exception)
            {
                if (tex != null) UnityGlue.SafeDestroy(tex);
                return null;
            }
            if (tex != null)
            {
                tex.name = Path.GetFileName(nameOrHint ?? "texture");
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Bilinear;
            }
            return tex;
        }

        /// <summary>TGA: 무압축/RLE, 트루컬러(24/32bit)와 흑백(8bit).</summary>
        public static Texture2D DecodeTga(byte[] d)
        {
            if (d.Length < 18) return null;
            int idLen = d[0], cmapType = d[1], type = d[2];
            int w = d[12] | (d[13] << 8), h = d[14] | (d[15] << 8);
            int bpp = d[16], desc = d[17];
            bool rle = type == 10 || type == 11;
            bool gray = type == 3 || type == 11;
            if (cmapType != 0 || !(type == 2 || type == 3 || type == 10 || type == 11)) return null;
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
            int bytesPP = bpp / 8;
            if (!(bytesPP == 1 || bytesPP == 3 || bytesPP == 4)) return null;
            bool topDown = (desc & 0x20) != 0;
            int pos = 18 + idLen;
            var px = new Color32[w * h];
            int count = w * h, i = 0;
            Color32 Read()
            {
                if (pos + bytesPP > d.Length) throw new InvalidDataException("truncated tga");
                Color32 c;
                if (bytesPP == 1) { byte g = d[pos]; c = new Color32(g, g, g, 255); }
                else c = new Color32(d[pos + 2], d[pos + 1], d[pos], bytesPP == 4 ? d[pos + 3] : (byte)255);
                pos += bytesPP;
                return c;
            }
            while (i < count)
            {
                if (rle)
                {
                    int header = d[pos++];
                    int n = (header & 0x7F) + 1;
                    if ((header & 0x80) != 0) { var c = Read(); for (int k = 0; k < n && i < count; k++) px[i++] = c; }
                    else for (int k = 0; k < n && i < count; k++) px[i++] = Read();
                }
                else px[i++] = Read();
            }
            if (gray && bytesPP != 1) return null;
            // Unity 텍스처는 아래 행부터 → TGA가 위에서부터 저장됐으면 뒤집는다
            if (topDown) FlipRows(px, w, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>BMP: 무압축 24/32bit.</summary>
        public static Texture2D DecodeBmp(byte[] d)
        {
            int dataOffset = BitConverter.ToInt32(d, 10);
            int w = BitConverter.ToInt32(d, 18), hRaw = BitConverter.ToInt32(d, 22);
            int bpp = BitConverter.ToInt16(d, 28), compression = BitConverter.ToInt32(d, 30);
            if ((compression != 0 && compression != 3) || (bpp != 24 && bpp != 32)) return null;
            bool topDown = hRaw < 0;
            int h = Math.Abs(hRaw);
            int stride = ((w * bpp / 8) + 3) & ~3;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = dataOffset + y * stride;
                int dst = (topDown ? h - 1 - y : y) * w;
                for (int x = 0; x < w; x++)
                {
                    int p = row + x * (bpp / 8);
                    px[dst + x] = new Color32(d[p + 2], d[p + 1], d[p], bpp == 32 ? d[p + 3] : (byte)255);
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        static void FlipRows(Color32[] px, int w, int h)
        {
            var tmp = new Color32[w];
            for (int y = 0; y < h / 2; y++)
            {
                Array.Copy(px, y * w, tmp, 0, w);
                Array.Copy(px, (h - 1 - y) * w, px, y * w, w);
                Array.Copy(tmp, 0, px, (h - 1 - y) * w, w);
            }
        }

        /// <summary>텍스처에 의미 있는 투명 픽셀이 있는지 (머리카락 등 컷아웃 판단용, 샘플링).</summary>
        public static bool HasTransparency(Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels32();
                int step = Math.Max(1, px.Length / 20000), transparent = 0, n = 0;
                for (int i = 0; i < px.Length; i += step, n++) if (px[i].a < 200) transparent++;
                return transparent > n / 200; // 0.5% 이상
            }
            catch (Exception) { return false; }
        }
    }
}
