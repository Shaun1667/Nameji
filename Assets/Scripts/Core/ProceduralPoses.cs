using System;

namespace Shimeji.Core
{
    /// <summary>
    /// 캐릭터 공간(정면 +Z, 위 +Y, 오른쪽 +X) 기준의 의미 단위 포즈. 각도는 도(degree).
    /// 휴머노이드 공용 클립 생성(에디터 베이크)과 제네릭 폴백 절차 애니메이션이 같은 정의를 쓴다.
    /// </summary>
    public struct PoseParams
    {
        /// <summary>엉덩이 높이 대비 하강 비율 (0 = 서 있음, 0.8 = 바닥에 앉음).</summary>
        public float HipsDrop;
        public float HipsPitch, HipsYaw, HipsRoll;
        /// <summary>+ = 앞으로 숙임.</summary>
        public float SpinePitch, ChestPitch;
        public float SpineRoll;
        /// <summary>+ = 고개 숙임, Yaw + = 오른쪽.</summary>
        public float HeadPitch, HeadYaw, HeadRoll;
        public Limb ArmL, ArmR, LegL, LegR;

        public struct Limb
        {
            /// <summary>팔: 몸통 옆에서 벌린 각도 (0 = 아래로 내림, 90 = 수평, 180 = 머리 위). 다리: 옆으로 벌림.</summary>
            public float Spread;
            /// <summary>+ = 앞으로 스윙.</summary>
            public float Forward;
            /// <summary>팔꿈치/무릎 굽힘.</summary>
            public float Bend;

            public Limb(float spread, float forward, float bend) { Spread = spread; Forward = forward; Bend = bend; }

            public static Limb Lerp(Limb a, Limb b, float t) => new Limb(
                MathUtil.Lerp(a.Spread, b.Spread, t), MathUtil.Lerp(a.Forward, b.Forward, t), MathUtil.Lerp(a.Bend, b.Bend, t));
        }

        public static PoseParams Lerp(PoseParams a, PoseParams b, float t)
        {
            return new PoseParams
            {
                HipsDrop = MathUtil.Lerp(a.HipsDrop, b.HipsDrop, t),
                HipsPitch = MathUtil.Lerp(a.HipsPitch, b.HipsPitch, t),
                HipsYaw = MathUtil.Lerp(a.HipsYaw, b.HipsYaw, t),
                HipsRoll = MathUtil.Lerp(a.HipsRoll, b.HipsRoll, t),
                SpinePitch = MathUtil.Lerp(a.SpinePitch, b.SpinePitch, t),
                ChestPitch = MathUtil.Lerp(a.ChestPitch, b.ChestPitch, t),
                SpineRoll = MathUtil.Lerp(a.SpineRoll, b.SpineRoll, t),
                HeadPitch = MathUtil.Lerp(a.HeadPitch, b.HeadPitch, t),
                HeadYaw = MathUtil.Lerp(a.HeadYaw, b.HeadYaw, t),
                HeadRoll = MathUtil.Lerp(a.HeadRoll, b.HeadRoll, t),
                ArmL = Limb.Lerp(a.ArmL, b.ArmL, t),
                ArmR = Limb.Lerp(a.ArmR, b.ArmR, t),
                LegL = Limb.Lerp(a.LegL, b.LegL, t),
                LegR = Limb.Lerp(a.LegR, b.LegR, t),
            };
        }
    }

    /// <summary>상태 키별 포즈 함수 (사인파 기반). 더미 클립과 제네릭 폴백의 공통 원천.</summary>
    public static class ProceduralPoses
    {
        public sealed class ClipSpec
        {
            public string Key;
            public float Length;
            public bool Loop;
        }

        public static readonly ClipSpec[] Specs =
        {
            new ClipSpec { Key = AnimKeys.Idle, Length = 2.4f, Loop = true },
            new ClipSpec { Key = AnimKeys.IdleLookAround, Length = 4f, Loop = true },
            new ClipSpec { Key = AnimKeys.Walk, Length = 1.0f, Loop = true },
            new ClipSpec { Key = AnimKeys.Run, Length = 0.6f, Loop = true },
            new ClipSpec { Key = AnimKeys.Sit, Length = 3f, Loop = true },
            new ClipSpec { Key = AnimKeys.SitEdge, Length = 2f, Loop = true },
            new ClipSpec { Key = AnimKeys.Sleep, Length = 4f, Loop = true },
            new ClipSpec { Key = AnimKeys.Jump, Length = 0.5f, Loop = true },
            new ClipSpec { Key = AnimKeys.Fall, Length = 0.6f, Loop = true },
            new ClipSpec { Key = AnimKeys.Land, Length = 0.4f, Loop = false },
            new ClipSpec { Key = AnimKeys.Dragged, Length = 1.4f, Loop = true },
            new ClipSpec { Key = AnimKeys.Thrown, Length = 0.5f, Loop = true },
            new ClipSpec { Key = AnimKeys.ClimbWall, Length = 1.0f, Loop = true },
            new ClipSpec { Key = AnimKeys.HangCeiling, Length = 2f, Loop = true },
            new ClipSpec { Key = AnimKeys.CrawlCeiling, Length = 1.0f, Loop = true },
            new ClipSpec { Key = AnimKeys.ClickReact, Length = 1.2f, Loop = false },
            new ClipSpec { Key = AnimKeys.Wave, Length = 1.0f, Loop = true },
            new ClipSpec { Key = AnimKeys.Dance, Length = 1.0f, Loop = true },
        };

        public static ClipSpec GetSpec(string key)
        {
            foreach (var s in Specs) if (s.Key == key) return s;
            return null;
        }

        const float Tau = (float)(Math.PI * 2);
        static float Sin(float x) => (float)Math.Sin(x);
        static float Cos(float x) => (float)Math.Cos(x);

        static PoseParams Stand()
        {
            return new PoseParams
            {
                ArmL = new PoseParams.Limb(20, 0, 10),
                ArmR = new PoseParams.Limb(20, 0, 10),
            };
        }

        /// <summary>key 포즈를 time(초)에서 평가. 루프 클립은 Length 주기로 반복.</summary>
        public static PoseParams Evaluate(string key, float time)
        {
            var spec = GetSpec(key) ?? GetSpec(AnimKeys.Idle);
            float len = spec.Length;
            float t = spec.Loop ? (time % len + len) % len / len : MathUtil.Clamp(time / len, 0f, 1f);
            float w = t * Tau; // 한 주기 위상
            var p = Stand();

            switch (spec.Key)
            {
                case AnimKeys.Idle:
                    p.ChestPitch = 2f * Sin(w);
                    p.HeadPitch = 2f + 2f * Sin(w + 0.5f);
                    p.ArmL.Spread = p.ArmR.Spread = 18f + 3f * Sin(w);
                    p.HipsDrop = 0.01f * (1 - Cos(w)) * 0.5f;
                    break;

                case AnimKeys.IdleLookAround:
                    p.HeadYaw = 40f * Sin(w);
                    p.HeadPitch = -5f * Sin(w * 2);
                    p.ChestPitch = 2f * Sin(w * 2);
                    p.SpineRoll = 3f * Sin(w);
                    break;

                case AnimKeys.Walk:
                {
                    float s = Sin(w);
                    p.LegL = new PoseParams.Limb(0, 28f * s, 30f * Math.Max(0, -Cos(w)) + 8f);
                    p.LegR = new PoseParams.Limb(0, -28f * s, 30f * Math.Max(0, Cos(w)) + 8f);
                    p.ArmL = new PoseParams.Limb(15, -25f * s, 20);
                    p.ArmR = new PoseParams.Limb(15, 25f * s, 20);
                    p.HipsDrop = 0.04f * Math.Abs(Cos(w));
                    p.HipsYaw = 6f * s;
                    p.SpinePitch = 4f;
                    p.HeadPitch = -2f;
                    break;
                }

                case AnimKeys.Run:
                {
                    float s = Sin(w);
                    p.LegL = new PoseParams.Limb(0, 45f * s, 70f * Math.Max(0, -Cos(w)) + 20f);
                    p.LegR = new PoseParams.Limb(0, -45f * s, 70f * Math.Max(0, Cos(w)) + 20f);
                    p.ArmL = new PoseParams.Limb(20, -45f * s, 80);
                    p.ArmR = new PoseParams.Limb(20, 45f * s, 80);
                    p.HipsDrop = 0.06f * Math.Abs(Cos(w));
                    p.SpinePitch = 14f;
                    p.HeadPitch = -8f;
                    break;
                }

                case AnimKeys.Sit:
                    p.HipsDrop = 0.82f;
                    p.LegL = new PoseParams.Limb(10, 88f, 5f);
                    p.LegR = new PoseParams.Limb(10, 88f, 5f);
                    p.ArmL = new PoseParams.Limb(15, 35f, 20);
                    p.ArmR = new PoseParams.Limb(15, 35f, 20);
                    p.SpinePitch = -4f + 2f * Sin(w);
                    p.HeadPitch = 3f * Sin(w + 1f);
                    break;

                case AnimKeys.SitEdge:
                {
                    float s = Sin(w);
                    p.HipsDrop = 0.85f;
                    p.LegL = new PoseParams.Limb(8, 88f, 90f + 25f * s);
                    p.LegR = new PoseParams.Limb(8, 88f, 90f - 25f * s);
                    p.ArmL = new PoseParams.Limb(30, 20f, 10);
                    p.ArmR = new PoseParams.Limb(30, 20f, 10);
                    p.SpinePitch = 2f;
                    p.HeadRoll = 6f * Sin(w * 0.5f);
                    break;
                }

                case AnimKeys.Sleep:
                    p.HipsDrop = 0.82f;
                    p.LegL = new PoseParams.Limb(10, 80f, 20f);
                    p.LegR = new PoseParams.Limb(10, 80f, 20f);
                    p.ArmL = new PoseParams.Limb(10, 40f, 40);
                    p.ArmR = new PoseParams.Limb(10, 40f, 40);
                    p.SpinePitch = 30f + 3f * Sin(w);
                    p.ChestPitch = 8f;
                    p.HeadPitch = 30f + 4f * Sin(w + 0.7f);
                    p.HeadRoll = 15f;
                    break;

                case AnimKeys.Jump:
                    p.LegL = new PoseParams.Limb(5, 40f, 70f);
                    p.LegR = new PoseParams.Limb(5, 30f, 60f);
                    p.ArmL = new PoseParams.Limb(150, 10f, 10);
                    p.ArmR = new PoseParams.Limb(150, 10f, 10);
                    p.HeadPitch = -10f;
                    break;

                case AnimKeys.Fall:
                {
                    float s = Sin(w);
                    p.LegL = new PoseParams.Limb(15, 20f + 10f * s, 30f);
                    p.LegR = new PoseParams.Limb(15, -5f - 10f * s, 30f);
                    p.ArmL = new PoseParams.Limb(140 + 15f * s, 0, 15);
                    p.ArmR = new PoseParams.Limb(140 - 15f * s, 0, 15);
                    p.HeadPitch = -12f;
                    break;
                }

                case AnimKeys.Land:
                {
                    float k = Sin(t * (float)Math.PI); // 0 → 1 → 0
                    p.HipsDrop = 0.3f * k;
                    p.LegL = new PoseParams.Limb(8, 45f * k, 90f * k);
                    p.LegR = new PoseParams.Limb(8, 45f * k, 90f * k);
                    p.ArmL = new PoseParams.Limb(20 + 40f * k, 30f * k, 20);
                    p.ArmR = new PoseParams.Limb(20 + 40f * k, 30f * k, 20);
                    p.SpinePitch = 20f * k;
                    break;
                }

                case AnimKeys.Dragged:
                {
                    float s = Sin(w);
                    p.ArmL = new PoseParams.Limb(25 + 8f * s, 5f * s, 15);
                    p.ArmR = new PoseParams.Limb(25 - 8f * s, -5f * s, 15);
                    p.LegL = new PoseParams.Limb(4, 12f * s, 15f);
                    p.LegR = new PoseParams.Limb(4, -12f * s, 15f);
                    p.HeadPitch = 12f;
                    p.HeadRoll = 8f * Sin(w + 1f);
                    break;
                }

                case AnimKeys.Thrown:
                {
                    float s = Sin(w);
                    p.ArmL = new PoseParams.Limb(120 + 20f * s, 0, 10);
                    p.ArmR = new PoseParams.Limb(120 - 20f * s, 0, 10);
                    p.LegL = new PoseParams.Limb(30 + 10f * s, 10f, 20f);
                    p.LegR = new PoseParams.Limb(30 - 10f * s, -10f, 20f);
                    p.SpinePitch = -10f;
                    p.HeadPitch = -15f;
                    break;
                }

                case AnimKeys.ClimbWall:
                {
                    float s = Sin(w);
                    p.ArmL = new PoseParams.Limb(150 + 20f * s, 40f, 30f - 20f * s);
                    p.ArmR = new PoseParams.Limb(150 - 20f * s, 40f, 30f + 20f * s);
                    p.LegL = new PoseParams.Limb(10, 35f + 25f * s, 50f + 30f * s);
                    p.LegR = new PoseParams.Limb(10, 35f - 25f * s, 50f - 30f * s);
                    p.SpinePitch = -5f;
                    p.HeadPitch = -15f;
                    break;
                }

                case AnimKeys.HangCeiling:
                {
                    float s = Sin(w);
                    p.ArmL = new PoseParams.Limb(172, 0, 5);
                    p.ArmR = new PoseParams.Limb(172, 0, 5);
                    p.LegL = new PoseParams.Limb(6, 10f * s, 12f);
                    p.LegR = new PoseParams.Limb(6, -10f * s, 12f);
                    p.HeadPitch = -10f;
                    p.HipsRoll = 3f * s;
                    break;
                }

                case AnimKeys.CrawlCeiling:
                {
                    float s = Sin(w);
                    p.ArmL = new PoseParams.Limb(165 + 10f * s, 15f * s, 10);
                    p.ArmR = new PoseParams.Limb(165 - 10f * s, -15f * s, 10);
                    p.LegL = new PoseParams.Limb(8, 20f * s, 20f);
                    p.LegR = new PoseParams.Limb(8, -20f * s, 20f);
                    p.HeadPitch = -10f;
                    break;
                }

                case AnimKeys.ClickReact:
                {
                    float k = Sin(t * (float)Math.PI);
                    p.ArmL = new PoseParams.Limb(20 + 90f * k, 20f * k, 30f);
                    p.ArmR = new PoseParams.Limb(20 + 90f * k, 20f * k, 30f);
                    p.SpinePitch = -12f * k;
                    p.HeadPitch = -18f * k;
                    p.HeadRoll = 10f * Sin(t * (float)Math.PI * 3f);
                    p.HipsDrop = -0.05f * k;
                    break;
                }

                case AnimKeys.Wave:
                {
                    float s = Sin(w);
                    p.ArmR = new PoseParams.Limb(150, 10f, 40f + 30f * s);
                    p.ArmL = new PoseParams.Limb(15, 0, 15);
                    p.HeadRoll = -8f;
                    p.SpineRoll = 3f * s;
                    break;
                }

                case AnimKeys.Dance:
                {
                    float s = Sin(w);
                    float s2 = Sin(w * 2);
                    p.HipsRoll = 10f * s;
                    p.SpineRoll = -8f * s;
                    p.ArmL = new PoseParams.Limb(110 + 40f * s, 20f, 40);
                    p.ArmR = new PoseParams.Limb(110 - 40f * s, 20f, 40);
                    p.LegL = new PoseParams.Limb(10, 10f * Math.Max(0, s), 25f * Math.Max(0, s));
                    p.LegR = new PoseParams.Limb(10, 10f * Math.Max(0, -s), 25f * Math.Max(0, -s));
                    p.HipsDrop = 0.06f * Math.Abs(s2);
                    p.HeadRoll = 12f * s;
                    break;
                }
            }
            return p;
        }
    }
}
