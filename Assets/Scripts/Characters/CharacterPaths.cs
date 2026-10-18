using System;
using System.IO;

namespace Shimeji.Characters
{
    /// <summary>
    /// 캐릭터 "항목" 경로 규약. 항목은 두 가지:
    ///  - 폴더: Characters/이름/ (model.* 또는 image.*, character.json, animations/, skeleton.png)
    ///  - 파일: Models/이름.fbx 처럼 폴더 없이 넣은 모델/이미지 한 개 (설정은 이름.json, 미리보기는 이름.skeleton.png)
    /// </summary>
    public static class CharacterPaths
    {
        public const string ConfigFileName = "character.json";
        const string SkeletonSuffix = ".skeleton";

        public static bool IsFolderEntry(string entry) => Directory.Exists(entry);

        /// <summary>폴더 없이 넣은 파일이 캐릭터 항목인지 (모델/이미지, 보조 파일 제외).</summary>
        public static bool IsLooseEntry(string file)
        {
            if (!File.Exists(file)) return false;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith(SkeletonSuffix, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(stem, "icon", StringComparison.OrdinalIgnoreCase)) return false;
            return Array.IndexOf(ModelImporterRegistry.Extensions, ext) >= 0 || SpriteModelBuilder.IsImage(file);
        }

        public static string Name(string entry) =>
            IsFolderEntry(entry) ? Path.GetFileName(entry.TrimEnd('/', '\\')) : Path.GetFileNameWithoutExtension(entry);

        public static string ModelFile(string entry) =>
            IsFolderEntry(entry) ? ModelImporterRegistry.FindModelFile(entry) : (File.Exists(entry) ? entry : null);

        public static string ConfigFile(string entry) =>
            IsFolderEntry(entry)
                ? Path.Combine(entry, ConfigFileName)
                : Path.Combine(Path.GetDirectoryName(entry) ?? ".", Path.GetFileNameWithoutExtension(entry) + ".json");

        /// <summary>모델 전용 애니메이션 폴더 (폴더 항목만).</summary>
        public static string AnimationsDir(string entry) => IsFolderEntry(entry) ? Path.Combine(entry, "animations") : null;

        public static string SkeletonFile(string entry) =>
            IsFolderEntry(entry)
                ? Path.Combine(entry, SpriteSkeletonImage.FileStem + ".png")
                : Path.Combine(Path.GetDirectoryName(entry) ?? ".", Path.GetFileNameWithoutExtension(entry) + SkeletonSuffix + ".png");
    }
}
