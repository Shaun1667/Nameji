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
}
");
            ExportGlb(SampleModelFactory.CreateBlob(), Path.Combine(CharactersDir, "SampleBlob", "model.glb"));
            File.WriteAllText(Path.Combine(CharactersDir, "SampleBlob", "character.json"),
@"{
  ""displayName"": ""Sample Blob"",
  ""heightPixels"": 110,
  ""rigType"": ""generic""
}
");
            Debug.Log("[Shimeji] sample characters exported to " + CharactersDir);
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
            PlayerSettings.productName = "Shimeji";
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

            SetLayerName(8, "Character");
            // Hidden/Internal-Colored, UI/Default는 내장 리소스라 항상 포함됨 (목록에 넣으면 빌드가 실패한다)
            AddAlwaysIncludedShaders("Standard", "UniGLTF/UniUnlit", "VRM10/MToon10");
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
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(BuildDir, "Shimeji.exe"),
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
