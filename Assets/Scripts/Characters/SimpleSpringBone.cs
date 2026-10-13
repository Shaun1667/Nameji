using System.Collections.Generic;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    /// <summary>
    /// VRM이 아닌 모델용 가벼운 스프링본 (Verlet). 머리카락/치마/꼬리 등 이름으로 체인을 찾는다.
    /// 애니메이션 평가 후(LateUpdate) 실행되어야 한다.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class SimpleSpringBone : MonoBehaviour
    {
        public float Stiffness = 0.6f;
        public float Drag = 0.35f;
        public Vector3 Gravity = new Vector3(0, -0.6f, 0);

        sealed class Joint
        {
            public Transform Bone;
            public Transform Child;
            public Quaternion RestLocal;
            public Vector3 RestTailLocal; // 부모 기준이 아닌 본 로컬 기준 자식 위치
            public float Length;
            public Vector3 CurrTail, PrevTail;
        }

        readonly List<Joint> _joints = new List<Joint>();
        static readonly string[] Keywords = { "hair", "skirt", "tail", "ribbon", "bust", "breast", "ear", "cloth", "cape", "sleeve", "hood", "sec" };

        public int JointCount => _joints.Count;

        /// <summary>코어 본(매핑된 본)이 아닌 것 중 이름이 키워드를 포함하는 체인을 찾아 등록.</summary>
        public void Setup(Transform root, ICollection<Transform> coreBones)
        {
            _joints.Clear();
            var core = new HashSet<Transform>(coreBones);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (core.Contains(t) || t.childCount == 0) continue;
                if (!IsSpringName(t.name)) continue;
                if (t.GetComponent<Renderer>() != null) continue;
                var child = t.GetChild(0);
                var j = new Joint
                {
                    Bone = t,
                    Child = child,
                    RestLocal = t.localRotation,
                    RestTailLocal = child.localPosition,
                    Length = (child.position - t.position).magnitude,
                };
                if (j.Length < 1e-5f) continue;
                j.CurrTail = j.PrevTail = child.position;
                _joints.Add(j);
            }
            // 부모 → 자식 순서 보장 (계층 깊이 기준)
            _joints.Sort((a, b) => Depth(a.Bone).CompareTo(Depth(b.Bone)));
        }

        static int Depth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { d++; t = t.parent; }
            return d;
        }

        static bool IsSpringName(string name)
        {
            foreach (var token in BoneMapper.Tokenize(name))
                foreach (var k in Keywords)
                    if (token == k || token.StartsWith(k)) return true;
            return false;
        }

        public void ResetState()
        {
            foreach (var j in _joints) j.CurrTail = j.PrevTail = j.Child.position;
        }

        void LateUpdate()
        {
            float dt = Mathf.Min(Time.deltaTime, 1f / 20f);
            if (dt <= 0f) return;
            foreach (var j in _joints)
            {
                if (j.Bone == null || j.Child == null) continue;
                j.Bone.localRotation = j.RestLocal;
                var scale = j.Bone.lossyScale.x;
                float len = j.Length;
                var restTail = j.Bone.TransformPoint(j.RestTailLocal);
                var velocity = (j.CurrTail - j.PrevTail) * (1f - Drag);
                var next = j.CurrTail + velocity
                           + (restTail - j.CurrTail) * Stiffness * dt * 10f
                           + Gravity * (dt * scale);
                // 길이 고정
                next = j.Bone.position + (next - j.Bone.position).normalized * len;
                j.PrevTail = j.CurrTail;
                j.CurrTail = next;
                var from = j.Bone.rotation * (j.RestTailLocal.normalized);
                var to = (next - j.Bone.position).normalized;
                j.Bone.rotation = Quaternion.FromToRotation(from, to) * j.Bone.rotation;
            }
        }
    }
}
