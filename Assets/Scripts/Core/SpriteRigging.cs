using System;
using System.Collections.Generic;

namespace Shimeji.Core
{
    /// <summary>이미지 알파 마스크. 좌상단 원점, y 아래로 증가, row-major.</summary>
    public sealed class AlphaMask
    {
        public readonly int Width, Height;
        public readonly byte[] Alpha;
        public byte Threshold = 32;

        public AlphaMask(int w, int h, byte[] alpha)
        {
            Width = w; Height = h; Alpha = alpha;
            if (alpha.Length != w * h) throw new ArgumentException("alpha size mismatch");
        }

        public bool Solid(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Alpha[y * Width + x] > Threshold;
    }

    /// <summary>
    /// 2D 캐릭터 관절. 좌표는 이미지 정규화 좌표 (0..1, 좌상단 원점).
    /// 좌우는 "캐릭터 기준"이 아니라 이미지 기준이다: A = 이미지 왼쪽, B = 이미지 오른쪽.
    /// </summary>
    public static class SpriteJoints
    {
        public const string Top = "top", Neck = "neck", Hips = "hips";
        public const string ShoulderA = "shoulderA", ElbowA = "elbowA", HandA = "handA";
        public const string ShoulderB = "shoulderB", ElbowB = "elbowB", HandB = "handB";
        public const string HipA = "hipA", KneeA = "kneeA", FootA = "footA";
        public const string HipB = "hipB", KneeB = "kneeB", FootB = "footB";

        public static readonly string[] All =
        {
            Top, Neck, Hips, ShoulderA, ElbowA, HandA, ShoulderB, ElbowB, HandB, HipA, KneeA, FootA, HipB, KneeB, FootB,
        };
    }

    public sealed class SpriteRigResult
    {
        /// <summary>관절 → 이미지 픽셀 좌표.</summary>
        public readonly Dictionary<string, Vec2> Joints = new Dictionary<string, Vec2>();
        /// <summary>캐릭터가 차지하는 영역 (픽셀, Right/Bottom 배타).</summary>
        public RectPx Bounds;
        /// <summary>몸통 중심부 X 범위 (팔 가중치 판정용).</summary>
        public float CoreLeft, CoreRight;
        public bool LegsSeparated, ArmAFound, ArmBFound, NeckFound;
        public readonly List<string> Notes = new List<string>();

        /// <summary>0..1 대략적인 자동 감지 신뢰도.</summary>
        public float Confidence =>
            (NeckFound ? 0.3f : 0f) + (LegsSeparated ? 0.3f : 0f) + (ArmAFound ? 0.2f : 0f) + (ArmBFound ? 0.2f : 0f);

        public Vec2 J(string name) => Joints[name];

        public Dictionary<string, Vec2> ToNormalized(int imageW, int imageH)
        {
            var d = new Dictionary<string, Vec2>();
            foreach (var kv in Joints) d[kv.Key] = new Vec2(kv.Value.X / imageW, kv.Value.Y / imageH);
            return d;
        }
    }

    /// <summary>
    /// 한 장짜리 캐릭터 그림(정면/반측면, 서 있는 자세 가정)에서 관절 위치를 휴리스틱으로 추정한다.
    ///  - 목: 위쪽 20~65% 구간에서 중심 덩어리 폭이 가장 좁은 행
    ///  - 가랑이: 아래에서부터 중심선 양쪽으로 갈라져 있는 가장 높은 행 (치마 등으로 안 갈라지면 비율로)
    ///  - 손: 몸통 중심 폭보다 바깥으로 튀어나온 가장 먼 점 (붙어 있으면 몸통 옆선)
    /// 틀릴 수 있으므로 결과는 character.json의 joints로 덮어쓸 수 있고, 앱의 관절 편집 UI로 고칠 수 있다.
    /// </summary>
    public static class SpriteJointDetector
    {
        struct Run { public int L, R; public int Mid => (L + R) / 2; public int Len => R - L + 1; }

        static List<Run> Runs(AlphaMask m, int y)
        {
            var runs = new List<Run>();
            int x = 0;
            while (x < m.Width)
            {
                while (x < m.Width && !m.Solid(x, y)) x++;
                if (x >= m.Width) break;
                int l = x;
                while (x < m.Width && m.Solid(x, y)) x++;
                runs.Add(new Run { L = l, R = x - 1 });
            }
            return runs;
        }

        /// <summary>cx를 포함하거나 가장 가까운 덩어리.</summary>
        static Run? Central(List<Run> runs, float cx)
        {
            Run? best = null;
            float bestD = float.MaxValue;
            foreach (var r in runs)
            {
                float d = cx < r.L ? r.L - cx : cx > r.R ? cx - r.R : 0;
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        public static SpriteRigResult Detect(AlphaMask m)
        {
            var res = new SpriteRigResult();
            int minX = m.Width, maxX = -1, minY = m.Height, maxY = -1;
            long sumX = 0, count = 0;
            for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                {
                    if (!m.Solid(x, y)) continue;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    sumX += x; count++;
                }
            if (count == 0) throw new ArgumentException("image has no opaque pixels");
            res.Bounds = new RectPx(minX, minY, maxX + 1, maxY + 1);
            float H = maxY - minY + 1, W = maxX - minX + 1;

            // 중심선: 아래쪽 40% (다리/몸통)의 질량 중심 — 머리 장식/팔에 덜 휘둘린다
            float cx;
            {
                long sx = 0, n = 0;
                for (int y = (int)(minY + H * 0.45f); y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                        if (m.Solid(x, y)) { sx += x; n++; }
                cx = n > 0 ? (float)sx / n : (float)sumX / count;
            }

            // --- 목: 중심 덩어리 폭 프로파일의 최소 ---
            int y0 = (int)(minY + H * 0.2f), y1 = (int)(minY + H * 0.65f);
            var widths = new float[m.Height];
            for (int y = minY; y <= maxY; y++)
            {
                var c = Central(Runs(m, y), cx);
                widths[y] = c.HasValue ? c.Value.Len : 0;
            }
            int neckY = (int)(minY + H * 0.42f);
            float minW = float.MaxValue, headMaxW = 0;
            for (int y = minY; y < y0; y++) headMaxW = Math.Max(headMaxW, widths[y]);
            for (int y = y0; y <= y1; y++)
            {
                // 3행 평균으로 노이즈 완화
                float w = (widths[Math.Max(minY, y - 1)] + widths[y] + widths[Math.Min(maxY, y + 1)]) / 3f;
                if (w > 0 && w < minW) { minW = w; neckY = y; }
            }
            // 머리 폭 대비 충분히 좁아야 목으로 인정
            res.NeckFound = minW < float.MaxValue && headMaxW > 0 && minW < headMaxW * 0.8f;
            if (!res.NeckFound)
            {
                neckY = (int)(minY + H * 0.42f);
                res.Notes.Add("neck not found; using 42% of height");
            }

            // --- 가랑이: 아래에서 위로, 중심선 근처가 비어 있고 양쪽에 덩어리가 있는 행 ---
            int crotchY = -1;
            int legAx = -1, legBx = -1;
            for (int y = maxY - (int)(H * 0.02f); y > neckY + H * 0.1f; y--)
            {
                var runs = Runs(m, y);
                bool gap = !m.Solid((int)cx, y);
                Run? left = null, right = null;
                foreach (var r in runs)
                {
                    if (r.R < cx && r.Len > W * 0.03f) left = left.HasValue && left.Value.R > r.R ? left : r;
                    if (r.L > cx && r.Len > W * 0.03f) right = right.HasValue && right.Value.L < r.L ? right : r;
                }
                if (gap && left.HasValue && right.HasValue)
                {
                    crotchY = y;
                    if (legAx < 0) { legAx = left.Value.Mid; legBx = right.Value.Mid; }
                }
                else if (crotchY >= 0) break; // 갈라진 구간이 끝남
            }
            if (crotchY >= 0 && crotchY < maxY - H * 0.06f)
            {
                res.LegsSeparated = true;
            }
            else
            {
                crotchY = (int)(maxY - H * 0.22f);
                legAx = (int)(cx - W * 0.12f);
                legBx = (int)(cx + W * 0.12f);
                res.Notes.Add("legs not separated; using proportions");
            }
            // 발 x: 맨 아래 근처 덩어리
            {
                var bottom = Runs(m, maxY - (int)(H * 0.02f));
                if (bottom.Count >= 2)
                {
                    legAx = bottom[0].Mid;
                    legBx = bottom[bottom.Count - 1].Mid;
                }
            }

            // --- 몸통 중심 폭 (팔 판정 기준): 목~가랑이 아래쪽 절반의 중심 덩어리 폭 중앙값 ---
            var torsoW = new List<float>();
            for (int y = (neckY + crotchY) / 2; y < crotchY; y++) if (widths[y] > 0) torsoW.Add(widths[y]);
            torsoW.Sort();
            float coreW = torsoW.Count > 0 ? torsoW[torsoW.Count / 2] : W * 0.35f;
            // 팔이 몸에 붙어 있으면 중앙값이 부풀어 있으므로 목 바로 아래 폭과 비교해 작은 쪽 사용
            float chestW = widths[Math.Min(maxY, neckY + (int)(H * 0.05f))];
            if (chestW > 0) coreW = Math.Min(coreW, Math.Max(chestW * 1.3f, W * 0.15f));
            res.CoreLeft = cx - coreW / 2f;
            res.CoreRight = cx + coreW / 2f;

            // --- 팔: 몸통 밴드에서 몸통 중심 폭 바깥으로 가장 멀리 나간 점 ---
            float margin = Math.Max(2f, W * 0.04f);
            Vec2? handA = null, handB = null;
            for (int y = neckY; y <= crotchY; y++)
            {
                var runs = Runs(m, y);
                if (runs.Count == 0) continue;
                int l = runs[0].L, r = runs[runs.Count - 1].R;
                if (l < res.CoreLeft - margin && (!handA.HasValue || l < handA.Value.X || (l == handA.Value.X)))
                    handA = new Vec2(l, y);
                if (r > res.CoreRight + margin && (!handB.HasValue || r > handB.Value.X || (r == handB.Value.X)))
                    handB = new Vec2(r, y);
            }
            float bandH = crotchY - neckY;
            var shoulderA = new Vec2(res.CoreLeft + coreW * 0.1f, neckY + bandH * 0.12f);
            var shoulderB = new Vec2(res.CoreRight - coreW * 0.1f, neckY + bandH * 0.12f);
            res.ArmAFound = handA.HasValue;
            res.ArmBFound = handB.HasValue;
            if (!handA.HasValue) { handA = new Vec2(res.CoreLeft, neckY + bandH * 0.6f); res.Notes.Add("arm A attached to body"); }
            if (!handB.HasValue) { handB = new Vec2(res.CoreRight, neckY + bandH * 0.6f); res.Notes.Add("arm B attached to body"); }

            var hips = new Vec2(cx, crotchY - H * 0.04f);
            var hipA = new Vec2(legAx, crotchY - H * 0.02f);
            var hipB = new Vec2(legBx, crotchY - H * 0.02f);
            var footA = new Vec2(legAx, maxY);
            var footB = new Vec2(legBx, maxY);

            var j = res.Joints;
            j[SpriteJoints.Top] = new Vec2(cx, minY);
            j[SpriteJoints.Neck] = new Vec2(cx, neckY);
            j[SpriteJoints.Hips] = hips;
            j[SpriteJoints.ShoulderA] = shoulderA;
            j[SpriteJoints.HandA] = handA.Value;
            j[SpriteJoints.ElbowA] = (shoulderA + handA.Value) * 0.5f;
            j[SpriteJoints.ShoulderB] = shoulderB;
            j[SpriteJoints.HandB] = handB.Value;
            j[SpriteJoints.ElbowB] = (shoulderB + handB.Value) * 0.5f;
            j[SpriteJoints.HipA] = hipA;
            j[SpriteJoints.FootA] = footA;
            j[SpriteJoints.KneeA] = (hipA + footA) * 0.5f;
            j[SpriteJoints.HipB] = hipB;
            j[SpriteJoints.FootB] = footB;
            j[SpriteJoints.KneeB] = (hipB + footB) * 0.5f;
            return res;
        }

        /// <summary>사용자가 지정한 정규화 좌표로 일부/전체 관절을 덮어쓴다.</summary>
        public static void ApplyOverrides(SpriteRigResult res, IDictionary<string, Vec2> normalized, int imageW, int imageH)
        {
            if (normalized == null) return;
            foreach (var kv in normalized)
            {
                if (Array.IndexOf(SpriteJoints.All, kv.Key) < 0) continue;
                res.Joints[kv.Key] = new Vec2(kv.Value.X * imageW, kv.Value.Y * imageH);
            }
            // 팔꿈치/무릎을 지정하지 않았으면 중간점으로 다시 맞춘다
            if (!normalized.ContainsKey(SpriteJoints.ElbowA)) res.Joints[SpriteJoints.ElbowA] = (res.J(SpriteJoints.ShoulderA) + res.J(SpriteJoints.HandA)) * 0.5f;
            if (!normalized.ContainsKey(SpriteJoints.ElbowB)) res.Joints[SpriteJoints.ElbowB] = (res.J(SpriteJoints.ShoulderB) + res.J(SpriteJoints.HandB)) * 0.5f;
            if (!normalized.ContainsKey(SpriteJoints.KneeA)) res.Joints[SpriteJoints.KneeA] = (res.J(SpriteJoints.HipA) + res.J(SpriteJoints.FootA)) * 0.5f;
            if (!normalized.ContainsKey(SpriteJoints.KneeB)) res.Joints[SpriteJoints.KneeB] = (res.J(SpriteJoints.HipB) + res.J(SpriteJoints.FootB)) * 0.5f;
            // 몸통 폭 판정도 어깨 기준으로 갱신
            res.CoreLeft = Math.Min(res.CoreLeft, res.J(SpriteJoints.ShoulderA).X);
            res.CoreRight = Math.Max(res.CoreRight, res.J(SpriteJoints.ShoulderB).X);
        }
    }

    /// <summary>2D 본 (이미지 픽셀 좌표의 선분).</summary>
    public sealed class SpriteBone
    {
        public string Name;
        public int Parent = -1;
        public Vec2 Head, Tail;
        public SpriteBoneRegion Region;
        public SpriteBone(string name, int parent, Vec2 head, Vec2 tail, SpriteBoneRegion region)
        {
            Name = name; Parent = parent; Head = head; Tail = tail; Region = region;
        }
    }

    public enum SpriteBoneRegion { Body, Head, ArmA, ArmB, LegA, LegB, None }

    public sealed class SpriteMesh
    {
        public readonly List<Vec2> Vertices = new List<Vec2>();   // 이미지 픽셀 좌표
        public readonly List<int> Triangles = new List<int>();
        /// <summary>정점마다 (본 인덱스, 가중치) 최대 4개.</summary>
        public readonly List<(int bone, float w)[]> Weights = new List<(int, float)[]>();
    }

    /// <summary>격자 메시 생성 + 거리/영역 기반 자동 가중치.</summary>
    public static class SpriteSkinning
    {
        /// <summary>
        /// 표준 본 구성: Hips(루트) → Spine → Head, Spine → 위팔/아래팔(A/B), Hips → 허벅지/정강이(A/B), 끝에 손/발(가중치 없음).
        /// BoneMapper가 인식할 수 있는 이름을 쓴다.
        /// </summary>
        public static List<SpriteBone> BuildBones(SpriteRigResult r)
        {
            var b = new List<SpriteBone>();
            int Add(string n, int p, string h, string t, SpriteBoneRegion reg)
            {
                b.Add(new SpriteBone(n, p, r.J(h), r.J(t), reg));
                return b.Count - 1;
            }
            int hips = Add("Hips", -1, SpriteJoints.Hips, SpriteJoints.Hips, SpriteBoneRegion.Body);
            int spine = Add("Spine", hips, SpriteJoints.Hips, SpriteJoints.Neck, SpriteBoneRegion.Body);
            Add("Head", spine, SpriteJoints.Neck, SpriteJoints.Top, SpriteBoneRegion.Head);
            // 이미지 왼쪽(A)이 정면을 보는 캐릭터의 오른쪽
            int uaA = Add("UpperArm_R", spine, SpriteJoints.ShoulderA, SpriteJoints.ElbowA, SpriteBoneRegion.ArmA);
            int laA = Add("LowerArm_R", uaA, SpriteJoints.ElbowA, SpriteJoints.HandA, SpriteBoneRegion.ArmA);
            Add("Hand_R", laA, SpriteJoints.HandA, SpriteJoints.HandA, SpriteBoneRegion.None);
            int uaB = Add("UpperArm_L", spine, SpriteJoints.ShoulderB, SpriteJoints.ElbowB, SpriteBoneRegion.ArmB);
            int laB = Add("LowerArm_L", uaB, SpriteJoints.ElbowB, SpriteJoints.HandB, SpriteBoneRegion.ArmB);
            Add("Hand_L", laB, SpriteJoints.HandB, SpriteJoints.HandB, SpriteBoneRegion.None);
            int ulA = Add("UpperLeg_R", hips, SpriteJoints.HipA, SpriteJoints.KneeA, SpriteBoneRegion.LegA);
            int llA = Add("LowerLeg_R", ulA, SpriteJoints.KneeA, SpriteJoints.FootA, SpriteBoneRegion.LegA);
            Add("Foot_R", llA, SpriteJoints.FootA, SpriteJoints.FootA, SpriteBoneRegion.None);
            int ulB = Add("UpperLeg_L", hips, SpriteJoints.HipB, SpriteJoints.KneeB, SpriteBoneRegion.LegB);
            int llB = Add("LowerLeg_L", ulB, SpriteJoints.KneeB, SpriteJoints.FootB, SpriteBoneRegion.LegB);
            Add("Foot_L", llB, SpriteJoints.FootB, SpriteJoints.FootB, SpriteBoneRegion.None);
            return b;
        }

        /// <summary>불투명 픽셀이 있는 격자 칸만 사각형 2개로 만든다. cell = 칸 크기(px).</summary>
        public static SpriteMesh BuildMesh(AlphaMask m, RectPx bounds, int cell)
        {
            var mesh = new SpriteMesh();
            int x0 = Math.Max(0, (int)bounds.Left - cell), y0 = Math.Max(0, (int)bounds.Top - cell);
            int x1 = Math.Min(m.Width, (int)bounds.Right + cell), y1 = Math.Min(m.Height, (int)bounds.Bottom + cell);
            int cols = (x1 - x0 + cell - 1) / cell, rows = (y1 - y0 + cell - 1) / cell;
            var index = new Dictionary<long, int>();
            int V(int cxI, int cyI)
            {
                long key = (long)cyI * 100000 + cxI;
                if (index.TryGetValue(key, out int i)) return i;
                i = mesh.Vertices.Count;
                // 캐릭터 경계 밖 정점은 경계로 잘라 낸다 (바깥은 투명이라 모양은 그대로, 키/발 위치가 정확해진다)
                mesh.Vertices.Add(new Vec2(
                    MathUtil.Clamp(x0 + cxI * cell, bounds.Left, bounds.Right),
                    MathUtil.Clamp(y0 + cyI * cell, bounds.Top, bounds.Bottom)));
                index[key] = i;
                return i;
            }
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    bool any = false;
                    int px0 = x0 + c * cell, py0 = y0 + r * cell;
                    // 칸 + 1px 테두리 안에 불투명 픽셀이 있으면 포함 (가장자리 안티앨리어싱 보존)
                    for (int y = py0 - 1; y <= py0 + cell && !any; y++)
                        for (int x = px0 - 1; x <= px0 + cell && !any; x++)
                            if (m.Solid(x, y)) any = true;
                    if (!any) continue;
                    int a = V(c, r), b = V(c + 1, r), d = V(c, r + 1), e = V(c + 1, r + 1);
                    var pa = mesh.Vertices[a]; var pe = mesh.Vertices[e];
                    if (pe.X - pa.X < 0.5f || pe.Y - pa.Y < 0.5f) continue; // 경계에 눌려 면적이 없어진 칸
                    mesh.Triangles.AddRange(new[] { a, b, d, b, e, d });
                }
            return mesh;
        }

        static float SegDist(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            float len2 = ab.X * ab.X + ab.Y * ab.Y;
            float t = len2 < 1e-6f ? 0f : MathUtil.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0f, 1f);
            var q = new Vec2(a.X + ab.X * t, a.Y + ab.Y * t);
            return (p - q).Length;
        }

        /// <summary>
        /// 거리 기반 가중치 + 영역 규칙:
        ///  - 팔 본은 몸통 중심 폭 바깥(또는 어깨 근처) 정점만, 해당 쪽만
        ///  - 다리 본은 가랑이 아래 정점만, 해당 쪽만
        ///  - 머리 본은 목 근처 위쪽 정점만
        /// → 팔을 움직여도 몸통이 같이 찢어지지 않는다.
        /// </summary>
        public static void ComputeWeights(SpriteMesh mesh, List<SpriteBone> bones, SpriteRigResult r)
        {
            float H = r.Bounds.Height;
            float neckY = r.J(SpriteJoints.Neck).Y;
            float crotchY = Math.Min(r.J(SpriteJoints.HipA).Y, r.J(SpriteJoints.HipB).Y);
            float cx = (r.CoreLeft + r.CoreRight) * 0.5f;
            float margin = H * 0.015f;
            mesh.Weights.Clear();
            var cand = new List<(int bone, float w)>();
            foreach (var v in mesh.Vertices)
            {
                cand.Clear();
                for (int i = 0; i < bones.Count; i++)
                {
                    var bone = bones[i];
                    bool allowed;
                    switch (bone.Region)
                    {
                        case SpriteBoneRegion.None: allowed = false; break;
                        case SpriteBoneRegion.Head: allowed = v.Y < neckY + H * 0.03f; break;
                        case SpriteBoneRegion.ArmA:
                            allowed = v.X < cx && v.Y < crotchY + H * 0.05f &&
                                      (v.X < r.CoreLeft - margin || (v - r.J(SpriteJoints.ShoulderA)).Length < H * 0.04f);
                            break;
                        case SpriteBoneRegion.ArmB:
                            allowed = v.X > cx && v.Y < crotchY + H * 0.05f &&
                                      (v.X > r.CoreRight + margin || (v - r.J(SpriteJoints.ShoulderB)).Length < H * 0.04f);
                            break;
                        case SpriteBoneRegion.LegA: allowed = v.X <= cx && v.Y > crotchY - H * 0.02f; break;
                        case SpriteBoneRegion.LegB: allowed = v.X >= cx && v.Y > crotchY - H * 0.02f; break;
                        default: allowed = true; break; // Body
                    }
                    if (!allowed) continue;
                    float d = SegDist(v, bone.Head, bone.Tail) / Math.Max(1f, H) + 0.01f;
                    // 몸통(Hips/Spine)은 같은 거리일 때 약간 덜 선호 → 팔다리 영역에서 팔다리를 따르게
                    if (bone.Region == SpriteBoneRegion.Body) d *= 1.6f;
                    cand.Add((i, 1f / (d * d * d * d)));
                }
                if (cand.Count == 0) cand.Add((1, 1f)); // Spine
                cand.Sort((a, b) => b.w.CompareTo(a.w));
                int n = Math.Min(2, cand.Count);
                float sum = 0f;
                for (int k = 0; k < n; k++) sum += cand[k].w;
                var arr = new (int, float)[n];
                for (int k = 0; k < n; k++) arr[k] = (cand[k].bone, cand[k].w / sum);
                mesh.Weights.Add(arr);
            }
        }
    }
}
