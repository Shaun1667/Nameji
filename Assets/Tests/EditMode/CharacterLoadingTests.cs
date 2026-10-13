using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Shimeji.Characters;
using Shimeji.Core;
using Shimeji.EditorTools;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Shimeji.Tests
{
    /// <summary>
    /// M3/M4: 실제 GLB 파일을 런타임 로더로 읽어 리그 판별/정규화/애니메이션 출처를 확인한다.
    /// 테스트 모델은 SampleModelFactory로 임시 폴더에 생성한다.
    /// </summary>
    public class CharacterLoadingTests
    {
        string _dir;
        CharacterAssets _assets;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "shimeji_chars_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ShimejiEditorTools.ExportGlb(SampleModelFactory.CreateChibi(), Path.Combine(_dir, "Chibi", "model.glb"));
            File.WriteAllText(Path.Combine(_dir, "Chibi", "character.json"), "{ \"heightPixels\": 150 }");
            ShimejiEditorTools.ExportGlb(SampleModelFactory.CreateBlob(), Path.Combine(_dir, "Blob", "model.glb"));
            File.WriteAllText(Path.Combine(_dir, "Blob", "character.json"), "{ \"heightPixels\": 110, \"rigType\": \"generic\" }");
            Directory.CreateDirectory(Path.Combine(_dir, "Broken"));
            File.WriteAllBytes(Path.Combine(_dir, "Broken", "model.glb"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

            _assets = new CharacterAssets
            {
                BaseController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ClipLibraryBaker.ControllerPath),
                LibraryClips = AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipLibraryBaker.LibraryDir })
                    .Select(g => AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(g))).ToArray(),
            };
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        static T Run<T>(Task<T> task)
        {
            // 에디터 모드에서는 ImmediateCaller라 동기적으로 끝난다.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(10);
            if (!task.IsCompleted) Assert.Fail("load task did not complete synchronously in edit mode");
            return task.GetAwaiter().GetResult();
        }

        [Test]
        public void LibraryAssetsExistForAllKeys()
        {
            Assert.IsNotNull(_assets.BaseController, "run Shimeji/Setup first");
            var names = _assets.LibraryClips.Select(c => c.name).ToList();
            foreach (var k in AnimKeys.All)
            {
                CollectionAssert.Contains(names, k);
                var clip = _assets.LibraryClips.First(c => c.name == k);
                Assert.IsTrue(clip.humanMotion, k + " should be a humanoid clip");
            }
            var ctrl = (UnityEditor.Animations.AnimatorController)_assets.BaseController;
            var states = ctrl.layers[0].stateMachine.states.Select(s => s.state.name).ToList();
            CollectionAssert.IsSubsetOf(AnimKeys.All, states);
        }

        [Test]
        public void HumanoidGlb_BuildsAvatar_NormalizesHeight_UsesModelClipFirst()
        {
            var actor = Run(CharacterFactory.CreateFromFolderAsync(Path.Combine(_dir, "Chibi"), _assets));
            try
            {
                Assert.AreEqual(RigKind.Humanoid, actor.Rig.Kind);
                Assert.IsTrue(actor.Rig.Animator.avatar.isHuman);
                Assert.AreEqual(150f, actor.Body.Height, 0.01f);

                // 발바닥이 피벗, 키가 heightPixels
                var b = CharacterFactory.ComputeBounds(actor.gameObject);
                Assert.AreEqual(1.5f, b.size.y, 0.02f);
                Assert.AreEqual(0f, b.min.y, 0.02f);
                Assert.AreEqual(0f, b.center.x, 0.05f);

                // 모델 내장 클립 "chibi_wave" → Wave 키 우선, 나머지는 라이브러리
                Assert.AreEqual(AnimationSource.ModelClip, actor.Anim.ResolveSource(AnimKeys.Wave));
                Assert.AreEqual(AnimationSource.Library, actor.Anim.ResolveSource(AnimKeys.Walk));
                Assert.AreEqual(AnimationSource.Library, actor.Anim.ResolveSource(AnimKeys.Idle));
                Assert.AreEqual(AnimationSource.Library, actor.Anim.ResolveSource(AnimKeys.Sit));

                // 클릭 판정 콜라이더 (머리/몸통/다리)
                var cols = actor.GetComponentsInChildren<Collider>();
                Assert.GreaterOrEqual(cols.Length, 3);
                Assert.IsTrue(cols.All(c => c.gameObject.layer == UnityGlue.CharacterLayer));
                // 머리카락 스프링본
                Assert.IsNotNull(actor.GetComponent<SimpleSpringBone>());
                Assert.Greater(actor.GetComponent<SimpleSpringBone>().JointCount, 0);

                // 재생 전환이 예외 없이 된다
                foreach (var k in new[] { AnimKeys.Idle, AnimKeys.Walk, AnimKeys.Sit, AnimKeys.Wave, AnimKeys.Idle })
                {
                    actor.Anim.Play(k);
                    Assert.AreEqual(k, actor.Anim.CurrentKey);
                }
                Assert.IsTrue(actor.Rig.Animator.enabled);
            }
            finally
            {
                actor.DestroyCharacter();
            }
        }

        [Test]
        public void HumanoidLibraryClip_ActuallyPosesBones()
        {
            var actor = Run(CharacterFactory.CreateFromFolderAsync(Path.Combine(_dir, "Chibi"), _assets));
            try
            {
                var animator = actor.Rig.Animator;
                var hips = actor.Rig.Get(BoneKey.Hips);
                var leftLower = actor.Rig.Get(BoneKey.LeftLowerLeg);
                actor.Anim.Play(AnimKeys.Idle);
                animator.Update(0f);
                animator.Update(0.5f);
                float standHipsY = hips.position.y;

                actor.Anim.Play(AnimKeys.Sit);
                animator.Update(0f);
                animator.Update(1.0f);
                float sitHipsY = hips.position.y;
                Assert.Less(sitHipsY, standHipsY - 0.2f, "Sit should lower the hips (root motion baked into pose)");
                // 앉으면 다리가 앞(+Z 쪽, Visual 회전 고려)으로 뻗는다: 무릎이 엉덩이보다 앞
                var oriented = actor.Oriented;
                var kneeLocal = oriented.InverseTransformPoint(leftLower.position);
                var hipsLocal = oriented.InverseTransformPoint(hips.position);
                Assert.Greater(kneeLocal.z - hipsLocal.z, 0.05f, "knee should be in front of hips when sitting");
            }
            finally
            {
                actor.DestroyCharacter();
            }
        }

        [Test]
        public void GenericGlb_ProceduralFallback_AndModelClipsForIdleWalk()
        {
            var actor = Run(CharacterFactory.CreateFromFolderAsync(Path.Combine(_dir, "Blob"), _assets));
            try
            {
                Assert.AreEqual(RigKind.Generic, actor.Rig.Kind);
                Assert.AreEqual(110f, actor.Body.Height, 0.01f);
                Assert.AreEqual(AnimationSource.ModelClip, actor.Anim.ResolveSource(AnimKeys.Idle));
                Assert.AreEqual(AnimationSource.ModelClip, actor.Anim.ResolveSource(AnimKeys.Walk));
                Assert.AreEqual(AnimationSource.Procedural, actor.Anim.ResolveSource(AnimKeys.Sit));

                // 절차 애니메이션이 실제로 본을 움직인다
                var legL = actor.Rig.Get(BoneKey.LeftUpperLeg);
                var rest = legL.localRotation;
                actor.Anim.Play(AnimKeys.Sit);
                var lateUpdate = typeof(CharacterAnimator).GetMethod("LateUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                for (int i = 0; i < 20; i++) lateUpdate.Invoke(actor.Anim, null);
                Assert.Greater(Quaternion.Angle(rest, legL.localRotation), 30f, "Sit should swing generic legs forward");
                Assert.IsNotNull(actor.GetComponent<SimpleSpringBone>(), "tail spring chain");
            }
            finally
            {
                actor.DestroyCharacter();
            }
        }

        [Test]
        public void BrokenFile_ThrowsAndLeavesNoObjects()
        {
            int before = Object.FindObjectsOfType<GameObject>().Length;
            Assert.Catch<Exception>(() => Run(CharacterFactory.CreateFromFolderAsync(Path.Combine(_dir, "Broken"), _assets)));
            Assert.AreEqual(before, Object.FindObjectsOfType<GameObject>().Length);
        }

        [Test]
        public void MissingRequiredBones_RigException()
        {
            var go = new GameObject("NoBones");
            try
            {
                new GameObject("bone_a").transform.SetParent(go.transform);
                Assert.Throws<RigException>(() => CharacterRigBuilder.Build(go, new CharacterConfig()));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }

    public class FolderWatcherTests
    {
        [Test]
        public void DetectsAddChangeRemove_AndSkipsLockedOrDisabled()
        {
            var root = Path.Combine(Path.GetTempPath(), "shimeji_watch_" + Guid.NewGuid().ToString("N"));
            var w = new CharacterFolderWatcher(root);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "A"));
                File.WriteAllBytes(Path.Combine(root, "A", "model.glb"), new byte[] { 1 });
                Directory.CreateDirectory(Path.Combine(root, "Empty")); // 모델 없음 → 무시
                var c = w.ScanNow();
                Assert.AreEqual(1, c.Count);
                Assert.AreEqual(FolderChangeKind.Added, c[0].Kind);
                Assert.AreEqual("A", c[0].Name);
                Assert.IsEmpty(w.ScanNow());

                File.WriteAllBytes(Path.Combine(root, "A", "model.glb"), new byte[] { 1, 2, 3 });
                c = w.ScanNow();
                Assert.AreEqual(FolderChangeKind.Changed, c.Single().Kind);

                // 복사 중(쓰기 잠금)인 파일은 잠금이 풀릴 때까지 추가하지 않음
                Directory.CreateDirectory(Path.Combine(root, "B"));
                var path = Path.Combine(root, "B", "model.vrm");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(1);
                    Assert.IsEmpty(w.ScanNow());
                }
                Assert.AreEqual("B", w.ScanNow().Single().Name);

                // 비활성 목록
                var disabled = new[] { "B" };
                c = w.ScanNow(disabled);
                Assert.AreEqual(FolderChangeKind.Removed, c.Single().Kind);

                Directory.Delete(Path.Combine(root, "A"), true);
                c = w.ScanNow(disabled);
                Assert.AreEqual(FolderChangeKind.Removed, c.Single().Kind);
                Assert.AreEqual("A", c.Single().Name);
            }
            finally
            {
                w.Dispose();
                Directory.Delete(root, true);
            }
        }
    }
}
