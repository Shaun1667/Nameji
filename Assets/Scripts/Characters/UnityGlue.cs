using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    public static class UnityGlue
    {
        public static Vector3 ToVector3(this Vec2 v, float z = 0f) => new Vector3(v.X, v.Y, z);
        public static Vec2 ToVec2(this Vector2 v) => new Vec2(v.x, v.y);
        public static Vec2 ToVec2(this Vector3 v) => new Vec2(v.x, v.y);
        public static Vector2 ToVector2(this Vec2 v) => new Vector2(v.X, v.Y);

        /// <summary>클릭 판정 콜라이더 전용 레이어.</summary>
        public const int CharacterLayer = 8;

        /// <summary>플레이 모드면 Destroy, 에디터(테스트) 모드면 DestroyImmediate.</summary>
        public static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        public static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursively(c.gameObject, layer);
        }
    }

    /// <summary>Core 로그를 Unity 콘솔로.</summary>
    public sealed class UnityLogSink : ILogSink
    {
        public void Write(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Error: Debug.LogError("[Shimeji] " + message); break;
                case LogLevel.Warn: Debug.LogWarning("[Shimeji] " + message); break;
                default: Debug.Log("[Shimeji] " + message); break;
            }
        }
    }
}
