using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Shimeji.Characters;
using Shimeji.Core;
using Shimeji.EditorTools;
using UnityEngine;

namespace Shimeji.Tests
{
    public class SpriteRiggingTests
    {
        /// <summary>
        /// 합성 치비 실루엣 (200x300): 큰 머리 원, 좁은 목, 몸통, 바깥으로 뻗은 두 팔, 갈라진 두 다리.
        /// </summary>
        static AlphaMask Figure(bool armsOut = true, bool legsSplit = true)
        {
            int w = 200, h = 300;
            var a = new byte[w * h];
            void Fill(Func<int, int, bool> f)
            {
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (f(x, y)) a[y * w + x] = 255;
            }
            Fill((x, y) => (x - 100) * (x - 100) + (y - 70) * (y - 70) < 60 * 60);        // 머리 (y 10~130)
            Fill((x, y) => x >= 90 && x < 110 && y >= 125 && y < 145);                   // 목
            Fill((x, y) => x >= 72 && x < 128 && y >= 140 && y < 215);                   // 몸통
            if (armsOut)
            {
                Fill((x, y) => x >= 30 && x < 72 && Math.Abs(y - (150 + (72 - x))) < 7);  // 왼쪽(이미지) 팔: 아래로 비스듬히
                Fill((x, y) => x >= 128 && x < 170 && Math.Abs(y - (150 + (x - 128))) < 7);
            }
            if (legsSplit)
            {
                Fill((x, y) => x >= 75 && x < 97 && y >= 210 && y < 290);
                Fill((x, y) => x >= 103 && x < 125 && y >= 210 && y < 290);
            }
            else Fill((x, y) => x >= 75 && x < 125 && y >= 210 && y < 290);
            return new AlphaMask(w, h, a);
        }

        [Test]
        public void DetectsNeckLegsArms()
        {
            var r = SpriteJointDetector.Detect(Figure());
            Assert.IsTrue(r.NeckFound, string.Join(";", r.Notes));
            Assert.IsTrue(r.LegsSeparated);
            Assert.IsTrue(r.ArmAFound && r.ArmBFound);
            Assert.AreEqual(1f, r.Confidence, 1e-4f);
            Assert.AreEqual(135f, r.J(SpriteJoints.Neck).Y, 12f);
            Assert.AreEqual(10f, r.J(SpriteJoints.Top).Y, 1f);
            Assert.AreEqual(289f, r.J(SpriteJoints.FootA).Y, 1f);
            Assert.AreEqual(86f, r.J(SpriteJoints.FootA).X, 4f);
            Assert.AreEqual(114f, r.J(SpriteJoints.FootB).X, 4f);
            Assert.Less(r.J(SpriteJoints.HandA).X, 40f);   // 이미지 왼쪽 손
            Assert.Greater(r.J(SpriteJoints.HandB).X, 160f);
            Assert.AreEqual(210f, r.J(SpriteJoints.HipA).Y, 12f);
        }

        [Test]
        public void FallsBackWhenLegsAndArmsNotSeparable()
        {
            var r = SpriteJointDetector.Detect(Figure(armsOut: false, legsSplit: false));
            Assert.IsFalse(r.LegsSeparated);
            Assert.IsFalse(r.ArmAFound);
            Assert.Less(r.Confidence, 0.5f);
            // 그래도 모든 관절이 있다 (비율로 추정)
            foreach (var j in SpriteJoints.All) Assert.IsTrue(r.Joints.ContainsKey(j), j);
            Assert.Less(r.J(SpriteJoints.HipA).X, r.J(SpriteJoints.HipB).X);
        }

        [Test]
        public void OverridesReplaceJointsAndRecomputeMidpoints()
        {
            var m = Figure();
            var r = SpriteJointDetector.Detect(m);
            SpriteJointDetector.ApplyOverrides(r, new System.Collections.Generic.Dictionary<string, Vec2>
            {
                [SpriteJoints.HandA] = new Vec2(0.1f, 0.2f),
                ["nonsense"] = new Vec2(0.5f, 0.5f),
            }, m.Width, m.Height);
            Assert.AreEqual(20f, r.J(SpriteJoints.HandA).X, 1e-3f);
            Assert.AreEqual(60f, r.J(SpriteJoints.HandA).Y, 1e-3f);
            var mid = (r.J(SpriteJoints.ShoulderA) + r.J(SpriteJoints.HandA)) * 0.5f;
            Assert.AreEqual(mid.X, r.J(SpriteJoints.ElbowA).X, 1e-3f);
        }

        [Test]
        public void MeshCoversOpaqueAreaOnly_WeightsNormalized_ArmsDoNotMoveTorso()
        {
            var m = Figure();
            var r = SpriteJointDetector.Detect(m);
            var bones = SpriteSkinning.BuildBones(r);
            var mesh = SpriteSkinning.BuildMesh(m, r.Bounds, 6);
            SpriteSkinning.ComputeWeights(mesh, bones, r);
            Assert.Greater(mesh.Triangles.Count, 0);
            Assert.AreEqual(mesh.Vertices.Count, mesh.Weights.Count);
            // 빈 모서리(0,0) 근처에는 정점이 없다
            Assert.IsFalse(mesh.Vertices.Any(v => v.X < 20 && v.Y < 20));
            foreach (var w in mesh.Weights)
            {
                Assert.AreEqual(1f, w.Sum(x => x.w), 1e-4f);
                Assert.LessOrEqual(w.Length, 2);
            }
            int armA = bones.FindIndex(b => b.Name == "UpperArm_R");
            int legB = bones.FindIndex(b => b.Name == "UpperLeg_L");
            int head = bones.FindIndex(b => b.Name == "Head");
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                var bonesOfV = mesh.Weights[i].Select(x => x.bone).ToArray();
                // 몸통 한가운데 정점은 팔 본에 묶이지 않는다
                if (v.X > 88 && v.X < 112 && v.Y > 150 && v.Y < 200) CollectionAssert.DoesNotContain(bonesOfV, armA);
                // 머리 위쪽 정점은 머리 본을 따른다
                if (v.Y < 60) CollectionAssert.Contains(bonesOfV, head);
                // 이미지 왼쪽 다리 정점은 오른쪽(B) 다리 본에 묶이지 않는다
                if (v.X < 95 && v.Y > 240) CollectionAssert.DoesNotContain(bonesOfV, legB);
            }
        }

        [Test]
        public void ConfigParsesSpriteFields()
        {
            var w = new System.Collections.Generic.List<string>();
            var c = CharacterConfig.Parse(@"{ ""spriteFacing"": ""left"", ""spriteLimbAmount"": 0.5,
                ""joints"": { ""neck"": [0.5, 0.42], ""handA"": [0.1, 0.6], ""bad"": [3, 3] } }", "S", w);
            Assert.AreEqual("left", c.SpriteFacing);
            Assert.AreEqual(0.5f, c.SpriteLimbAmount, 1e-4f);
            Assert.AreEqual(2, c.Joints.Count);
            Assert.AreEqual(0.42f, c.Joints["neck"].Y, 1e-4f);
            Assert.AreEqual(1, w.Count);
        }
    }

    public class SpriteLoadingTests
    {
        string _dir;
        CharacterAssets _assets;

        [OneTimeSetUp]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "shimeji_sprite_" + Guid.NewGuid().ToString("N"));
            ShimejiEditorTools.RenderSpriteSample(Path.Combine(_dir, "Sprite", "image.png"));
            Directory.CreateDirectory(Path.Combine(_dir, "Opaque"));
            var tex = new Texture2D(32, 32);
            var px = Enumerable.Repeat(new Color32(200, 50, 50, 255), 32 * 32).ToArray();
            tex.SetPixels32(px);
            File.WriteAllBytes(Path.Combine(_dir, "Opaque", "image.png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            _assets = new CharacterAssets();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        static T Run<T>(System.Threading.Tasks.Task<T> task)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(10);
            if (!task.IsCompleted) Assert.Fail("not completed");
            return task.GetAwaiter().GetResult();
        }

        [Test]
        public void FolderWithOnlyPngIsDetectedAsCharacter()
        {
            Assert.AreEqual(Path.Combine(_dir, "Sprite", "image.png"), ModelImporterRegistry.FindModelFile(Path.Combine(_dir, "Sprite")));
        }

        [Test]
        public void SamplePngLoads_AutoRigged_AndPosesMoveLimbs()
        {
            var actor = Run(CharacterFactory.CreateFromFolderAsync(Path.Combine(_dir, "Sprite"), _assets));
            try
            {
                Assert.IsTrue(actor.IsSprite);
                Assert.AreEqual(RigKind.Sprite2D, actor.Rig.Kind);
                var rig = actor.Sprite.Rig;
                TestContext.WriteLine($"confidence={rig.Confidence} notes={string.Join(";", rig.Notes)}");
                Assert.IsTrue(rig.LegsSeparated, "sample has separated legs");
                Assert.IsTrue(rig.ArmAFound && rig.ArmBFound, "sample has arms spread out");
                Assert.IsTrue(rig.NeckFound);

                // 키 정규화 (발 = 0, 기본 키 160px = 1.6 월드)
                var b = CharacterFactory.ComputeBounds(actor.gameObject);
                TestContext.WriteLine($"bounds min={b.min} max={b.max} imageBounds={rig.Bounds} verts={actor.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.bounds} orientedScale={actor.Oriented.localScale} orientedPos={actor.Oriented.localPosition} model={actor.Oriented.GetChild(0).localScale}/{actor.Oriented.GetChild(0).localPosition} hips={actor.Rig.Get(BoneKey.Hips).localPosition}");
                Assert.AreEqual(1.6f, b.size.y, 0.03f);
                Assert.AreEqual(0f, b.min.y, 0.03f);

                // 절차 포즈: 걷기/앉기/손 흔들기가 실제로 본을 움직인다
                var lateUpdate = typeof(CharacterAnimator).GetMethod("LateUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var armL = actor.Rig.Get(BoneKey.LeftUpperArm);
                var thigh = actor.Rig.Get(BoneKey.RightUpperLeg);
                var hips = actor.Rig.Get(BoneKey.Hips);
                actor.Anim.Play(AnimKeys.Idle);
                for (int i = 0; i < 10; i++) lateUpdate.Invoke(actor.Anim, null);
                float idleHipsY = hips.localPosition.y;
                actor.Anim.Play(AnimKeys.Sit);
                for (int i = 0; i < 20; i++) lateUpdate.Invoke(actor.Anim, null);
                Assert.Less(thigh.localScale.y, 0.6f, "sitting foreshortens thighs");
                Assert.Less(hips.localPosition.y, idleHipsY - 0.03f, "sitting lowers hips");
                actor.Anim.Play(AnimKeys.HangCeiling);
                for (int i = 0; i < 20; i++) lateUpdate.Invoke(actor.Anim, null);
                Assert.Greater(Mathf.DeltaAngle(0, armL.localEulerAngles.z), 90f, "hanging raises arms");

                // 클릭 판정 콜라이더
                Assert.GreaterOrEqual(actor.GetComponentsInChildren<Collider>().Length, 3);
            }
            finally
            {
                actor.DestroyCharacter();
            }
        }

        [Test]
        public void OpaqueImageIsRejected()
        {
            Assert.Catch<Exception>(() => Run(CharacterFactory.CreateFromFolderAsync(Path.Combine(_dir, "Opaque"), _assets)));
        }
    }
}
