using System.Collections.Generic;
using System.IO;
using Shimeji.Characters;
using Shimeji.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Shimeji.EditorTools
{
    /// <summary>
    /// 공용 휴머노이드 클립 라이브러리(더미 클립)를 코드로 생성한다.
    /// 기준 스켈레톤(SampleChibi, T-포즈)에 ProceduralPoses를 적용하고 HumanPoseHandler로
    /// 머슬 공간을 샘플링해 Humanoid .anim으로 저장 → 어떤 휴머노이드 모델에도 리타게팅된다.
    /// </summary>
    public static class ClipLibraryBaker
    {
        public const string LibraryDir = "Assets/Animations/Library";
        public const string ControllerPath = "Assets/Animations/ShimejiBase.controller";
        const float SampleRate = 30f;

        public static List<AnimationClip> BakeAll()
        {
            Directory.CreateDirectory(LibraryDir);
            var root = SampleModelFactory.CreateChibi(withEmbeddedClip: false);
            var clips = new List<AnimationClip>();
            try
            {
                var rig = CharacterRigBuilder.Build(root, new CharacterConfig { RigType = RigType.Humanoid });
                var avatar = rig.Animator.avatar;
                var poser = new PoseApplier(root.transform, rig.Bones);
                var handler = new HumanPoseHandler(avatar, root.transform);
                var pose = new HumanPose();

                int bodyMuscles = HumanTrait.MuscleCount - 40; // 손가락 머슬 제외 (이름 규칙이 다름)
                foreach (var spec in ProceduralPoses.Specs)
                {
                    int frames = Mathf.Max(2, Mathf.CeilToInt(spec.Length * SampleRate) + 1);
                    var muscleCurves = new AnimationCurve[bodyMuscles];
                    for (int m = 0; m < bodyMuscles; m++) muscleCurves[m] = new AnimationCurve();
                    var rootT = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
                    var rootQ = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };

                    for (int f = 0; f < frames; f++)
                    {
                        float t = spec.Length * f / (frames - 1);
                        poser.Apply(ProceduralPoses.Evaluate(spec.Key, t));
                        handler.GetHumanPose(ref pose);
                        for (int m = 0; m < bodyMuscles; m++) muscleCurves[m].AddKey(t, pose.muscles[m]);
                        rootT[0].AddKey(t, pose.bodyPosition.x);
                        rootT[1].AddKey(t, pose.bodyPosition.y);
                        rootT[2].AddKey(t, pose.bodyPosition.z);
                        rootQ[0].AddKey(t, pose.bodyRotation.x);
                        rootQ[1].AddKey(t, pose.bodyRotation.y);
                        rootQ[2].AddKey(t, pose.bodyRotation.z);
                        rootQ[3].AddKey(t, pose.bodyRotation.w);
                    }

                    var clip = new AnimationClip { name = spec.Key, frameRate = SampleRate };
                    for (int m = 0; m < bodyMuscles; m++)
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[m]), muscleCurves[m]);
                    string[] t3 = { "RootT.x", "RootT.y", "RootT.z" };
                    string[] q4 = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
                    for (int i = 0; i < 3; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), t3[i]), rootT[i]);
                    for (int i = 0; i < 4; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), q4[i]), rootQ[i]);

                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    settings.loopTime = spec.Loop;
                    // 루트 모션을 포즈에 굽는다(applyRootMotion=false여도 앉기 등 몸 높이 변화가 보이도록).
                    settings.loopBlendOrientation = true;
                    settings.loopBlendPositionY = true;
                    settings.loopBlendPositionXZ = true;
                    settings.keepOriginalOrientation = true;
                    settings.keepOriginalPositionY = true;
                    settings.keepOriginalPositionXZ = true;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);

                    var path = $"{LibraryDir}/{spec.Key}.anim";
                    var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (existing != null)
                    {
                        EditorUtility.CopySerialized(clip, existing);
                        clips.Add(existing);
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(clip, path);
                        clips.Add(clip);
                    }
                }
                handler.Dispose();
                Object.DestroyImmediate(avatar);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            AssetDatabase.SaveAssets();
            WriteLicense();
            BuildController(clips);
            Debug.Log($"[Shimeji] baked {clips.Count} humanoid clips");
            return clips;
        }

        static void BuildController(List<AnimationClip> clips)
        {
            if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var layers = ctrl.layers;
            layers[0].iKPass = true;
            ctrl.layers = layers;
            var sm = ctrl.layers[0].stateMachine;
            foreach (var c in clips)
            {
                var st = sm.AddState(c.name);
                st.motion = c;
                st.writeDefaultValues = true;
                if (c.name == AnimKeys.Idle) sm.defaultState = st;
            }
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
        }

        static void WriteLicense()
        {
            File.WriteAllText(Path.Combine(LibraryDir, "LICENSES.md"),
@"# Animation Library Licenses

| Clip | Source | License |
|---|---|---|
| " + string.Join(", ", AnimKeys.All) + @" | Generated in-project by `ClipLibraryBaker` from `ProceduralPoses` (sine-based dummy poses) | CC0 / project-owned |

All clips in this folder are generated by code (Shimeji/Generate Clip Library). No third-party motion data is used.
When adding external clips, record the source and license here.
");
        }
    }
}
