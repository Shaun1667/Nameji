using System;
using System.Collections.Generic;
using System.IO;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    /// <summary>
    /// 관절 설정 결과를 눈으로 확인할 수 있도록 원본 그림 위에 뼈대(선 + 관절 점)를 그린 PNG를 만든다.
    /// 캐릭터 폴더에 skeleton.png로 저장된다 (캐릭터 그림으로 인식되지 않음).
    /// </summary>
    public static class SpriteSkeletonImage
    {
        public const string FileStem = "skeleton";

        static readonly Color32 Bone = new Color32(255, 255, 255, 255);
        static readonly Color32 Outline = new Color32(30, 30, 40, 255);
        static readonly Color32 SideA = new Color32(255, 110, 90, 255);
        static readonly Color32 SideB = new Color32(80, 160, 255, 255);
        static readonly Color32 Center = new Color32(255, 215, 50, 255);

        public static Color32 JointColor(string joint) =>
            joint.EndsWith("A") ? SideA : joint.EndsWith("B") ? SideB : Center;

        /// <param name="jointsPx">관절 → 이미지 픽셀 좌표 (좌상단 원점)</param>
        public static byte[] RenderPng(Texture2D source, IReadOnlyDictionary<string, Vec2> jointsPx)
        {
            int w = source.width, h = source.height;
            var px = source.GetPixels32();
            // 원본을 살짝 흐리게 해서 뼈대가 잘 보이게
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                px[i] = new Color32((byte)(c.r * 0.7f + 40), (byte)(c.g * 0.7f + 40), (byte)(c.b * 0.7f + 40), c.a);
            }
            float scale = Mathf.Max(w, h) / 512f;
            int lineR = Mathf.Max(2, Mathf.RoundToInt(3 * scale));
            int dotR = Mathf.Max(4, Mathf.RoundToInt(7 * scale));

            foreach (var (a, b) in SpriteJoints.Links)
            {
                if (!jointsPx.TryGetValue(a, out var pa) || !jointsPx.TryGetValue(b, out var pb)) continue;
                Line(px, w, h, pa, pb, lineR + 1, Outline);
            }
            foreach (var (a, b) in SpriteJoints.Links)
            {
                if (!jointsPx.TryGetValue(a, out var pa) || !jointsPx.TryGetValue(b, out var pb)) continue;
                Line(px, w, h, pa, pb, lineR, Bone);
            }
            foreach (var kv in jointsPx)
            {
                Disc(px, w, h, kv.Value, dotR + 1, Outline);
                Disc(px, w, h, kv.Value, dotR, JointColor(kv.Key));
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(px);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                UnityGlue.SafeDestroy(tex);
            }
        }

        public static string Save(string folder, Texture2D source, IReadOnlyDictionary<string, Vec2> jointsPx)
        {
            var path = Path.Combine(folder, FileStem + ".png");
            File.WriteAllBytes(path, RenderPng(source, jointsPx));
            return path;
        }

        // 이미지 좌표(y 아래로) → 텍스처 픽셀 배열(아래 행부터)
        static void Disc(Color32[] px, int w, int h, Vec2 c, int r, Color32 color)
        {
            int cx = Mathf.RoundToInt(c.X), cy = h - 1 - Mathf.RoundToInt(c.Y);
            for (int y = cy - r; y <= cy + r; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r * r) px[y * w + x] = color;
                }
            }
        }

        static void Line(Color32[] px, int w, int h, Vec2 a, Vec2 b, int r, Color32 color)
        {
            float len = (b - a).Length;
            int steps = Math.Max(1, Mathf.CeilToInt(len / Math.Max(1, r * 0.5f)));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Disc(px, w, h, new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), r, color);
            }
        }
    }
}
