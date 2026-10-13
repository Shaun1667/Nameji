using System.IO;
using UnityEditor;
using UnityEngine;

namespace Shimeji.EditorTools
{
    /// <summary>
    /// 에디터 전용 FBX 경로: Assets/Characters/ 에 FBX를 넣으면
    ///  1) 휴머노이드 리그로 임포트 (아바타가 유효하지 않으면 제네릭으로 재임포트)
    ///  2) Assets/Resources/BuiltinCharacters/&lt;이름&gt;.prefab 생성 → 앱이 내장 캐릭터로 로드
    ///  3) 같은 폴더의 &lt;이름&gt;.json(character.json 형식)을 &lt;이름&gt;.character.txt로 복사
    /// FBX는 런타임 로딩이 불가능하므로 배포용 캐릭터는 VRM/GLB를 쓴다.
    /// </summary>
    public sealed class FbxCharacterPostprocessor : AssetPostprocessor
    {
        const string DropFolder = "Assets/Characters/";
        const string PrefabFolder = "Assets/Resources/BuiltinCharacters";
        const string GenericMarker = "shimeji:generic";

        static bool IsCharacterFbx(string path) =>
            path.StartsWith(DropFolder) && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);

        void OnPreprocessModel()
        {
            if (!IsCharacterFbx(assetPath)) return;
            var importer = (ModelImporter)assetImporter;
            bool forceGeneric = importer.userData == GenericMarker;
            importer.animationType = forceGeneric ? ModelImporterAnimationType.Generic : ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importCameras = false;
            importer.importLights = false;
            importer.isReadable = true;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported)
            {
                if (!IsCharacterFbx(path)) continue;
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (importer == null || model == null) continue;

                var avatar = AssetDatabase.LoadAllAssetsAtPath(path) is var all ? System.Array.Find(all, o => o is Avatar) as Avatar : null;
                if (importer.animationType == ModelImporterAnimationType.Human && (avatar == null || !avatar.isValid))
                {
                    Debug.LogWarning($"[Shimeji] {path}: humanoid avatar invalid, reimporting as Generic");
                    importer.userData = GenericMarker;
                    importer.SaveAndReimport();
                    continue;
                }

                Directory.CreateDirectory(PrefabFolder);
                var name = Path.GetFileNameWithoutExtension(path);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
                try
                {
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    PrefabUtility.SaveAsPrefabAsset(instance, $"{PrefabFolder}/{name}.prefab");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }

                var json = Path.ChangeExtension(path, ".json");
                if (File.Exists(json)) File.Copy(json, $"{PrefabFolder}/{name}.character.txt", true);
                AssetDatabase.Refresh();
                Debug.Log($"[Shimeji] FBX character prefab created: {name} ({importer.animationType})");
            }
        }
    }
}
