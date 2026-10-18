using System;
using System.IO;
using System.Linq;
using Shimeji.App;
using UniGLTF;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Shimeji.EditorTools
{
    /// <summary>프로젝트 셋업/샘플 생성/빌드 (메뉴 + 배치모드 -executeMethod).</summary>
    public static class ShimejiEditorTools
    {
        public const string ScenePath = "Assets/Scenes/Main.unity";
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        public static string CharactersDir => Path.Combine(ProjectRoot, "Characters");
        public static string BuildDir => Path.Combine(ProjectRoot, "Builds", "Win64");

        [MenuItem("Shimeji/Setup Everything (settings, samples, clips, scene)")]
        public static void SetupEverything()
        {
            ConfigureProjectSettings();
            ExportSampleCharacters();
            ClipLibraryBaker.BakeAll();
            CreateMainScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[Shimeji] setup complete");
        }

        [MenuItem("Shimeji/Export Sample Characters (GLB)")]
        public static void ExportSampleCharacters()
        {
            ExportGlb(SampleModelFactory.CreateChibi(), Path.Combine(CharactersDir, "SampleChibi", "model.glb"));
            File.WriteAllText(Path.Combine(CharactersDir, "SampleChibi", "character.json"),
@"{
  ""displayName"": ""Sample Chibi"",
  ""heightPixels"": 150,
  ""rigType"": ""auto"",
  ""behaviorProfile"": ""default"",
  ""enableSpringBones"": true,
  ""colliderPreset"": ""chibi""
}\r\n");
            ExportGlb(SampleModelFactory.CreateBlob(), Path.Combine(CharactersDir, "SampleBlob", "model.glb"));
            File.WriteAllText(Path.Combine(CharactersDir, "SampleBlob", "character.json"),
@"{
  ""displayName"": ""Sample Blob"",
  ""heightPixels"": 110,
  ""rigType"": ""generic""
}\r\n");
            RenderSpriteSample(Path.Combine(CharactersDir, "SampleSprite", "image.png"));
            File.WriteAllText(Path.Combine(CharactersDir, "SampleSprite", "character.json"),
@"{
  ""displayName"": ""Sample Sprite (2D)"",
  ""heightPixels"": 150,
  ""spriteFacing"": ""front""
}\r\n");
            Debug.Log("[Shimeji] sample characters exported to " + CharactersDir);
        }

        /// <summary>
        /// 2D 이미지 캐릭터 샘플: 3D 샘플 치비를 정면에서 렌더해 투명 배경 PNG로 저장한다 (자체 제작, 라이선스 문제 없음).
        /// 팔을 살짝 벌린 자세로 그려서 자동 관절 감지가 팔을 찾을 수 있게 한다.
        /// </summary>
        public static void RenderSpriteSample(string path, int size = 512)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var go = SampleModelFactory.CreateChibi(withEmbeddedClip: false);
            var camGo = new GameObject("SpriteSampleCamera");
            var lightGo = new GameObject("SpriteSampleLight");
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                var rig = Shimeji.Characters.CharacterRigBuilder.Build(go, new Shimeji.Core.CharacterConfig { RigType = Shimeji.Core.RigType.Generic });
                var poser = new Shimeji.Characters.PoseApplier(go.transform, rig.Bones);
                var pose = new Shimeji.Core.PoseParams
                {
                    ArmL = new Shimeji.Core.PoseParams.Limb(42, 0, 15),
                    ArmR = new Shimeji.Core.PoseParams.Limb(42, 0, 15),
                    LegL = new Shimeji.Core.PoseParams.Limb(7, 0, 0),
                    LegR = new Shimeji.Core.PoseParams.Limb(7, 0, 0),
                };
                poser.Apply(pose);
                go.transform.rotation = Quaternion.Euler(0, 180, 0); // 정면이 카메라(-Z) 쪽을 보도록

                var b = Shimeji.Characters.CharacterFactory.ComputeBounds(go);
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = b.size.y * 0.54f;
                cam.transform.position = new Vector3(b.center.x, b.center.y, -10f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.targetTexture = rt;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(25f, -20f, 0f);

                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        public static void ExportGlb(GameObject go, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var data = new ExportingGltfData();
                using (var exporter = new gltfExporter(data, new GltfExportSettings(),
                           animationExporter: new EditorAnimationExporter(),
                           textureSerializer: new EditorTextureSerializer()))
                {
                    exporter.Prepare(go);
                    exporter.Export();
                }
                File.WriteAllBytes(path, data.ToGlbBytes());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [MenuItem("Shimeji/Configure Project Settings")]
        public static void ConfigureProjectSettings()
        {
            PlayerSettings.companyName = "ShimejiProject";
            PlayerSettings.productName = "DesktopMascot";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = false;
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.defaultScreenWidth = 256;
            PlayerSettings.defaultScreenHeight = 256;
            PlayerSettings.allowFullscreenSwitch = false;
            // 켜져 있으면 DWM 알파 투명이 안 된다 (진단용: 환경변수 SHIMEJI_FLIP=1)
            PlayerSettings.useFlipModelSwapchain = Environment.GetEnvironmentVariable("SHIMEJI_FLIP") == "1";
            PlayerSettings.forceSingleInstance = true;
            PlayerSettings.usePlayerLog = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            try { PlayerSettings.SplashScreen.show = false; } catch (Exception) { /* 라이선스에 따라 불가 */ }

            // 품질: 그림자/MSAA 끔
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.shadows = ShadowQuality.Disable;
                QualitySettings.antiAliasing = 0;
                QualitySettings.vSyncCount = 0;
            }

            // 메모리/CPU 절약: 소리를 쓰지 않으므로 오디오 엔진(FMOD) 끔, 그래픽 잡 스레드 끔
            PlayerSettings.graphicsJobs = false;
            var audio = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);
            audio.FindProperty("m_DisableAudio").boolValue = true;
            audio.ApplyModifiedProperties();

            SetLayerName(8, "Character");
            // Hidden/Internal-Colored, UI/Default는 내장 리소스라 항상 포함됨 (목록에 넣으면 빌드가 실패한다)
            // Standard는 전체 배리언트가 들어가 런타임 메모리를 크게 쓰므로 빼고, 호환 경량 셰이더(Shimeji/Toon)를 쓴다.
            RemoveAlwaysIncludedShader("Standard");
            AddAlwaysIncludedShaders("Shimeji/Toon", "Shimeji/Sprite", "UniGLTF/UniUnlit", "VRM10/MToon10");
            AssetDatabase.SaveAssets();
            Debug.Log("[Shimeji] project settings configured");
        }

        static void SetLayerName(int index, string name)
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var el = layers.GetArrayElementAtIndex(index);
            if (el.stringValue != name) { el.stringValue = name; tagManager.ApplyModifiedProperties(); }
        }

        static void RemoveAlwaysIncludedShader(string name)
        {
            var sh = Shader.Find(name);
            if (sh == null) return;
            var gs = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var arr = gs.FindProperty("m_AlwaysIncludedShaders");
            for (int i = arr.arraySize - 1; i >= 0; i--)
            {
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue != sh) continue;
                arr.GetArrayElementAtIndex(i).objectReferenceValue = null;
                arr.DeleteArrayElementAtIndex(i);
            }
            gs.ApplyModifiedProperties();
        }

        static void AddAlwaysIncludedShaders(params string[] names)
        {
            var gs = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var arr = gs.FindProperty("m_AlwaysIncludedShaders");
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) { Debug.LogWarning("[Shimeji] shader not found: " + n); continue; }
                bool exists = false;
                for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) exists = true;
                if (exists) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            }
            gs.ApplyModifiedProperties();
        }

        [MenuItem("Shimeji/Create Main Scene")]
        public static void CreateMainScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.allowHDR = false;
            cam.allowMSAA = false;

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(25f, 20f, 0f);

            var appGo = new GameObject("ShimejiApp");
            var app = appGo.AddComponent<ShimejiApp>();
            app.MainCamera = cam;
            app.BaseController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ClipLibraryBaker.ControllerPath);
            app.LibraryClips = AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipLibraryBaker.LibraryDir })
                .Select(g => AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(c => c != null).ToArray();
            if (app.BaseController == null) Debug.LogError("[Shimeji] base controller missing - run Generate Clip Library first");

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[Shimeji] main scene created");
        }

        [MenuItem("Shimeji/Build Windows (x64)")]
        public static void BuildWindows()
        {
            ConfigureProjectSettings();
            Directory.CreateDirectory(BuildDir);
            // 예전 이름(Shimeji.exe, DesktopMate.exe)으로 만든 빌드가 남아 있으면 헷갈리지 않게 지운다
            foreach (var old in new[] { "Shimeji", "DesktopMate" })
            {
                var oldExe = Path.Combine(BuildDir, old + ".exe");
                if (File.Exists(oldExe)) File.Delete(oldExe);
                var oldData = Path.Combine(BuildDir, old + "_Data");
                if (Directory.Exists(oldData)) Directory.Delete(oldData, true);
            }
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(BuildDir, "DesktopMascot.exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[Shimeji] build result: {report.summary.result}, size={report.summary.totalSize / (1024 * 1024)}MB, errors={report.summary.totalErrors}");
            if (report.summary.result != BuildResult.Succeeded)
            {
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            // 샘플 캐릭터를 실행 파일 옆 Characters/로 복사
            CopyDirectory(CharactersDir, Path.Combine(BuildDir, "Characters"));
            // 파일을 그냥 넣으면 바로 캐릭터가 되는 Models/ 폴더
            var models = Path.Combine(BuildDir, "Models");
            Directory.CreateDirectory(models);
            File.WriteAllText(Path.Combine(models, "README.txt"),
                "이 폴더에 캐릭터 파일을 그냥 넣으면 실행 중에도 바로 나타납니다. (파일 하나 = 캐릭터 하나)\r\n" +
                "\r\n" +
                "지원 형식: .fbx .vrm .glb .gltf .obj .dae / 2D 이미지 .png .jpg .bmp .tga\r\n" +
                "- FBX 텍스처는 같은 폴더, textures/ 폴더, 또는 <이름>.fbm/ 폴더에서 찾습니다. (내장 텍스처도 지원)\r\n" +
                "- 사람형 뼈대(Mixamo, VRM, Unreal 이름 등)면 걷기/앉기/매달리기 등 공용 애니메이션이 자동 적용됩니다.\r\n" +
                "- 파일 안 애니메이션 이름(또는 파일 이름)이 Idle, Walk, Run, Sit, Jump, Fall, Wave, Dance 등으로 끝나면 그 동작에 우선 사용됩니다.\r\n" +
                "- 설정은 같은 이름의 .json (예: Hero.fbx → Hero.json)에 둡니다. 키 높이: { \"heightPixels\": 200 }\r\n" +
                "- 파일을 지우면 캐릭터도 사라지고, 덮어쓰면 다시 불러옵니다.\r\n" +
                "\r\n" +
                "애니메이션을 여러 개 붙이려면 Characters/<이름>/ 폴더에 모델과 animations/ 폴더(FBX/GLB 클립)를 넣으세요.\r\n");
        }

        static void CopyDirectory(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>배치모드 진입점: 셋업 후 빌드.</summary>
        public static void SetupAndBuild()
        {
            SetupEverything();
            BuildWindows();
        }
    }
}
