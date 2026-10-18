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

namespace Shimeji.Tests
{
    /// <summary>
    /// 런타임 FBX 로딩 (Assimp). 테스트용 FBX는 샘플 GLB를 Assimp로 변환해 임시 폴더에 만든다.
    /// 환경변수 SHIMEJI_TEST_FBX(세미콜론 구분 경로)를 주면 실제 FBX 파일도 검사한다.
    /// </summary>
    public class FbxLoadingTests
    {
        string _dir, _models, _chibiFbx;
        CharacterAssets _assets;

        [OneTimeSetUp]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "shimeji_fbx_" + Guid.NewGuid().ToString("N"));
            _models = Path.Combine(_dir, "Models");
            Directory.CreateDirectory(_models);
            var glb = Path.Combine(_dir, "src", "chibi.glb");
            ShimejiEditorTools.ExportGlb(SampleModelFactory.CreateChibi(), glb);
            FbxModelImporter.EnsureLibrary();
            _chibiFbx = Path.Combine(_models, "Chibi.fbx");
            using (var ctx = new Assimp.AssimpContext())
                Assert.IsTrue(ctx.ConvertFromFileToFile(glb, _chibiFbx, "fbx"), "assimp fbx export");
            _assets = new CharacterAssets
            {
                BaseController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ClipLibraryBaker.ControllerPath),
                LibraryClips = AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipLibraryBaker.LibraryDir })
                    .Select(g => AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(g))).ToArray(),
            };
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        static T Run<T>(Task<T> task)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.Elapsed.TotalSeconds < 60) Thread.Sleep(10);
            if (!task.IsCompleted) Assert.Fail("not completed");
            return task.GetAwaiter().GetResult();
        }

        [Test]
        public void LooseFbxInModelsFolderIsAnEntry()
        {
            Assert.IsTrue(CharacterPaths.IsLooseEntry(_chibiFbx));
            Assert.AreEqual("Chibi", CharacterPaths.Name(_chibiFbx));
            Assert.AreEqual(_chibiFbx, CharacterPaths.ModelFile(_chibiFbx));
            Assert.AreEqual(Path.Combine(_models, "Chibi.json"), CharacterPaths.ConfigFile(_chibiFbx));
            Assert.IsFalse(CharacterPaths.IsLooseEntry(Path.Combine(_models, "Chibi.json")));

            var w = new CharacterFolderWatcher(_models);
            try
            {
                var c = w.ScanNow();
                Assert.AreEqual(1, c.Count);
                Assert.AreEqual("Chibi", c[0].Name);
                Assert.AreEqual(_chibiFbx, c[0].Path);
                // 같은 이름의 설정 파일을 만들면 Changed
                File.WriteAllText(Path.Combine(_models, "Chibi.json"), "{ \"heightPixels\": 140 }");
                c = w.ScanNow();
                Assert.AreEqual(FolderChangeKind.Changed, c.Single().Kind);
            }
            finally
            {
                w.Dispose();
                File.Delete(Path.Combine(_models, "Chibi.json"));
            }
        }

        [Test]
        public void FbxLoadsAsHumanoid_WithCorrectSizeAndOrientation()
        {
            var actor = Run(CharacterFactory.CreateFromFolderAsync(_chibiFbx, _assets));
            try
            {
                Assert.AreEqual(RigKind.Humanoid, actor.Rig.Kind, "J_Bip names → runtime humanoid avatar");
                Assert.IsTrue(actor.Rig.Animator.avatar.isHuman);
                var b = CharacterFactory.ComputeBounds(actor.gameObject);
                Assert.AreEqual(1.6f, b.size.y, 0.05f, "default height 160px");
                Assert.AreEqual(0f, b.min.y, 0.03f, "feet on pivot");
                // 정면 보정: 오른손이 +X 쪽(Oriented 공간), 머리가 위
                var o = actor.Oriented;
                var r = o.InverseTransformPoint(actor.Rig.Get(BoneKey.RightHand).position);
                var l = o.InverseTransformPoint(actor.Rig.Get(BoneKey.LeftHand).position);
                var head = o.InverseTransformPoint(actor.Rig.Get(BoneKey.Head).position);
                var hips = o.InverseTransformPoint(actor.Rig.Get(BoneKey.Hips).position);
                Assert.Greater(r.x, l.x, "right hand on +X after orientation fix");
                Assert.Greater(head.y, hips.y, "head above hips");
                // 스킨 메시가 렌더러로 만들어졌다
                Assert.Greater(actor.GetComponentsInChildren<SkinnedMeshRenderer>().Length, 0);
                // 공용 클립 재생이 된다
                actor.Anim.Play(AnimKeys.Sit);
                actor.Rig.Animator.Update(0f);
                actor.Rig.Animator.Update(1f);
                Assert.AreEqual(AnimationSource.Library, actor.Anim.CurrentSource);
            }
            finally
            {
                actor.DestroyCharacter();
            }
        }

        [Test]
        public void GenericClipNamesAreRecognized()
        {
            Assert.IsTrue(FbxModelImporter.IsGenericClipName("mixamo.com"));
            Assert.IsTrue(FbxModelImporter.IsGenericClipName("Armature|mixamo.com"));
            Assert.IsTrue(FbxModelImporter.IsGenericClipName("Take 001"));
            Assert.IsFalse(FbxModelImporter.IsGenericClipName("Walk"));
            Assert.IsFalse(FbxModelImporter.IsGenericClipName("Armature|Idle"));
            // 여러 테이크면 "<파일이름>_1" 형태 → 끝 숫자는 무시하고 키와 맞춘다
            Assert.IsTrue(ClipNameMatcher.Matches("NinjaRun_1", AnimKeys.Run));
            Assert.IsTrue(ClipNameMatcher.Matches("Walk01", AnimKeys.Walk));
        }

        [Test]
        public void TgaDecoderReadsUncompressedAndRle()
        {
            // 2x1 32bit 무압축 (BGRA), 아래→위
            byte[] raw = { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 32, 8, 255, 0, 0, 255, 0, 255, 0, 128 };
            var t = ImageDecoders.DecodeTga(raw);
            Assert.AreEqual(2, t.width);
            Assert.AreEqual(new Color32(0, 0, 255, 255), (Color32)t.GetPixel(0, 0));
            Assert.AreEqual(128, ((Color32)t.GetPixel(1, 0)).a);
            UnityEngine.Object.DestroyImmediate(t);
            // RLE: 같은 픽셀 2개
            byte[] rle = { 0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0, 0x81, 10, 20, 30 };
            t = ImageDecoders.DecodeTga(rle);
            Assert.AreEqual(new Color32(30, 20, 10, 255), (Color32)t.GetPixel(1, 0));
            UnityEngine.Object.DestroyImmediate(t);
        }

        /// <summary>실제 FBX 파일 검사 (환경변수로 경로를 준 경우에만).</summary>
        [Test]
        public void RealWorldFbxFilesLoad()
        {
            var list = Environment.GetEnvironmentVariable("SHIMEJI_TEST_FBX");
            if (string.IsNullOrEmpty(list)) Assert.Ignore("SHIMEJI_TEST_FBX not set");
            foreach (var path in list.Split(';').Where(p => p.Length > 0))
            {
                Assume.That(File.Exists(path), path);
                var importer = new FbxModelImporter();
                var loaded = Run(importer.LoadAsync(path));
                var la = loaded.Root != null ? loaded.Root.GetComponentInChildren<UnityEngine.Animation>() : null;
                if (la != null)
                    foreach (AnimationState st in la)
                        TestContext.WriteLine($"  clip {st.name} len={st.length:F2} empty={st.clip.empty}");
                else TestContext.WriteLine("  (no Animation component)");
                try
                {
                    var actor = CharacterFactory.Assemble(Path.GetFileNameWithoutExtension(path), loaded, CharacterConfig.Default("x"), _assets, null);
                    var b = CharacterFactory.ComputeBounds(actor.gameObject);
                    TestContext.WriteLine($"{Path.GetFileName(path)}: rig={actor.Rig.Kind} bones={actor.Rig.Bones.Count} size={b.size} " +
                                          $"renderers={actor.GetComponentsInChildren<Renderer>().Length} modelClips={string.Join(",", actor.Anim.ModelClipMap.Keys)}");
                    Assert.AreEqual(1.6f, b.size.y, 0.08f);
                    actor.DestroyCharacter();
                }
                catch (RigException e)
                {
                    // 사람형이 아닌 소품/애니메이션 전용 파일은 필수 본이 없을 수 있다
                    TestContext.WriteLine($"{Path.GetFileName(path)}: not a character ({e.Message})");
                    loaded.Resources?.Dispose();
                    if (loaded.Root != null) UnityEngine.Object.DestroyImmediate(loaded.Root);
                }
            }
        }
    }
}
