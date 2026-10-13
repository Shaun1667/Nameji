using System;

namespace Shimeji.Core
{
    /// <summary>화면 픽셀 공간 2D 벡터 (좌상단 원점, y 아래로 증가).</summary>
    [Serializable]
    public struct Vec2 : IEquatable<Vec2>
    {
        public float X;
        public float Y;

        public Vec2(float x, float y) { X = x; Y = y; }

        public static readonly Vec2 Zero = new Vec2(0, 0);

        public float Length => (float)Math.Sqrt(X * X + Y * Y);

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Y / s);

        public bool Equals(Vec2 o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Vec2 o && Equals(o);
        public override int GetHashCode() => (X.GetHashCode() * 397) ^ Y.GetHashCode();
        public override string ToString() => $"({X:0.##}, {Y:0.##})";
    }

    /// <summary>화면 픽셀 사각형. Right/Bottom은 배타적 경계(Win32 RECT와 동일).</summary>
    [Serializable]
    public struct RectPx : IEquatable<RectPx>
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;

        public RectPx(float left, float top, float right, float bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public static RectPx FromXYWH(float x, float y, float w, float h) => new RectPx(x, y, x + w, y + h);

        public float Width => Right - Left;
        public float Height => Bottom - Top;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public Vec2 Center => new Vec2((Left + Right) * 0.5f, (Top + Bottom) * 0.5f);

        public bool Contains(Vec2 p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

        public bool ContainsInclusive(Vec2 p, float tol = 0f) =>
            p.X >= Left - tol && p.X <= Right + tol && p.Y >= Top - tol && p.Y <= Bottom + tol;

        public bool Intersects(RectPx o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;

        public RectPx Expanded(float d) => new RectPx(Left - d, Top - d, Right + d, Bottom + d);

        public static RectPx Union(RectPx a, RectPx b) =>
            new RectPx(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

        public RectPx Offset(float dx, float dy) => new RectPx(Left + dx, Top + dy, Right + dx, Bottom + dy);

        public bool Equals(RectPx o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
        public override bool Equals(object obj) => obj is RectPx o && Equals(o);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = Left.GetHashCode();
                h = h * 397 ^ Top.GetHashCode();
                h = h * 397 ^ Right.GetHashCode();
                return h * 397 ^ Bottom.GetHashCode();
            }
        }
        public override string ToString() => $"[{Left},{Top} - {Right},{Bottom}]";
    }

    public static class MathUtil
    {
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta) return target;
            return current + Math.Sign(target - current) * maxDelta;
        }
    }
}
