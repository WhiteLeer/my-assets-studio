using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static AnimeStudio.CLI.Exporter;
using System.Globalization;
using System.Xml;
using SevenZip;

namespace AnimeStudio.CLI
{
    [Flags]
    public enum MapOpType
    {
        None,
        Load,
        CABMap,
        AssetMap = 4,
        Both = 8,
        All = Both | Load,
        CABMapLoad = Load | CABMap,
        AssetMapLoad = Load | AssetMap,
        AllMaps = Load | CABMap | AssetMap,
    }

    internal static class Studio
    {
        public static Game Game;
        public static bool SkipContainer = false;
        public static AssetsManager assetsManager = new AssetsManager() { ResolveDependencies = false };
        public static AssemblyLoader assemblyLoader = new AssemblyLoader();
        public static List<AssetItem> exportableAssets = new List<AssetItem>();

        public static Dictionary<ulong, string> Paths {  get; set; } = new Dictionary<ulong, string>();
        public static List<string> PathStrings { get; set; } = new List<string>();
        public static List<string> VOStrings { get; set; } = new List<string>();
        public static List<string> EventStrings { get; set; } = new List<string>();
        private static readonly HashSet<uint> MajorBodyPathHashes = new[]
        {
            "Main/Root_M",
            "Main/Root_M/Spine1_M",
            "Main/Root_M/Spine1_M/Spine2_M",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M/Scapula_L",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M/Scapula_R",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M/Scapula_L/Shoulder_L",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M/Scapula_R/Shoulder_R",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M/Scapula_L/Shoulder_L/Elbow_L",
            "Main/Root_M/Spine1_M/Spine2_M/Chest_M/Scapula_R/Shoulder_R/Elbow_R",
            "Main/Root_M/Hip_L",
            "Main/Root_M/Hip_R",
            "Main/Root_M/Hip_L/Knee_L",
            "Main/Root_M/Hip_R/Knee_R"
        }.Select(CRC.CalculateDigestUTF8).ToHashSet();

        public static int ExtractFolder(string path, string savePath)
        {
            int extractedCount = 0;
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var fileOriPath = Path.GetDirectoryName(file);
                var fileSavePath = fileOriPath.Replace(path, savePath);
                extractedCount += ExtractFile(file, fileSavePath);
            }
            return extractedCount;
        }

        public static int ExtractFile(string[] fileNames, string savePath)
        {
            int extractedCount = 0;
            for (var i = 0; i < fileNames.Length; i++)
            {
                var fileName = fileNames[i];
                extractedCount += ExtractFile(fileName, savePath);
            }
            return extractedCount;
        }

        public static int ExtractFile(string fileName, string savePath)
        {
            int extractedCount = 0;
            var reader = new FileReader(fileName);
            reader = reader.PreProcessing(Game);
            if (reader.FileType == FileType.BundleFile)
                extractedCount += ExtractBundleFile(reader, savePath);
            else if (reader.FileType == FileType.WebFile)
                extractedCount += ExtractWebDataFile(reader, savePath);
            else if (reader.FileType == FileType.BlkFile)
                extractedCount += ExtractBlkFile(reader, savePath);
            else if (reader.FileType == FileType.BlockFile)
                extractedCount += ExtractBlockFile(reader, savePath);
            else
                reader.Dispose();
            return extractedCount;
        }

        private static int ExtractBundleFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            try
            {
                var bundleFile = new BundleFile(reader, Game);
                reader.Dispose();
                if (bundleFile.fileList.Count > 0)
                {
                    var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                    return ExtractStreamFile(extractPath, bundleFile.fileList);
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Mr0k)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            return 0;
        }

        private static int ExtractWebDataFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            var webFile = new WebFile(reader);
            reader.Dispose();
            if (webFile.fileList.Count > 0)
            {
                var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                return ExtractStreamFile(extractPath, webFile.fileList);
            }
            return 0;
        }

        private static int ExtractBlkFile(FileReader reader, string savePath)
        {
            int total = 0;
            Logger.Info($"Decompressing {reader.FileName} ...");
            try
            {
                using var stream = BlkUtils.Decrypt(reader, (Blk)Game);
                do
                {
                    stream.Offset = stream.AbsolutePosition;
                    var dummyPath = Path.Combine(reader.FullPath, stream.AbsolutePosition.ToString("X8"));
                    var subReader = new FileReader(dummyPath, stream, true);
                    var subSavePath = Path.Combine(savePath, reader.FileName + "_unpacked");
                    switch (subReader.FileType)
                    {
                        case FileType.BundleFile:
                            total += ExtractBundleFile(subReader, subSavePath);
                            break;
                        case FileType.MhyFile:
                            total += ExtractMhyFile(subReader, subSavePath);
                            break;
                    }
                } while (stream.Remaining > 0);
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Blk)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            return total;
        }

        private static int ExtractBlockFile(FileReader reader, string savePath)
        {
            int total = 0;
            Logger.Info($"Decompressing {reader.FileName} ...");
            using var stream = new OffsetStream(reader.BaseStream, 0);
            do
            {
                stream.Offset = stream.AbsolutePosition;
                var subSavePath = Path.Combine(savePath, reader.FileName + "_unpacked");
                var dummyPath = Path.Combine(reader.FullPath, stream.AbsolutePosition.ToString("X8"));
                var subReader = new FileReader(dummyPath, stream, true);
                total += ExtractBundleFile(subReader, subSavePath);
            } while (stream.Remaining > 0);
            return total;
        }

        private static int ExtractMhyFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            try
            {
                var mhy0File = new MhyFile(reader, (Mhy)Game);
                reader.Dispose();
                if (mhy0File.fileList.Count > 0)
                {
                    var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                    return ExtractStreamFile(extractPath, mhy0File.fileList);
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error($"Game type mismatch, Expected {nameof(Mhy)} but got {Game.Name} ({Game.GetType().Name}) !!");
            }
            return 0;
        }

        private static int ExtractStreamFile(string extractPath, List<StreamFile> fileList)
        {
            int extractedCount = 0;
            foreach (var file in fileList)
            {
                var filePath = Path.Combine(extractPath, file.path);
                var fileDirectory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }
                if (!File.Exists(filePath))
                {
                    using (var fileStream = File.Create(filePath))
                    {
                        file.stream.CopyTo(fileStream);
                    }
                    extractedCount += 1;
                }
                file.stream.Dispose();
            }
            return extractedCount;
        }

        public static void UpdateContainers()
        {
            if (exportableAssets.Count > 0)
            {
                Logger.Info("Updating Containers...");
                foreach (var asset in exportableAssets)
                {
                    if (int.TryParse(asset.Container, out var value))
                    {
                        var last = unchecked((uint)value);
                        var name = Path.GetFileNameWithoutExtension(asset.SourceFile.originalPath);
                        if (uint.TryParse(name, out var id))
                        {
                            var path = ResourceIndex.GetContainer(id, last);
                            if (!string.IsNullOrEmpty(path))
                            {
                                asset.Container = path;
                                if (asset.Type == ClassIDType.MiHoYoBinData)
                                {
                                    asset.Text = Path.GetFileNameWithoutExtension(path);
                                }
                            }
                        }
                    }
                }
                Logger.Info("Updated !!");
            }
        }

        public static void BuildAssetData(ClassIDType[] typeFilters, Regex[] nameFilters, Regex[] containerFilters, ref int i)
        {
            var objectAssetItemDic = new Dictionary<Object, AssetItem>();
            var mihoyoBinDataNames = new List<(PPtr<Object>, string)>();
            var containers = new List<(PPtr<Object>, string)>();
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    ProcessAssetData(asset, objectAssetItemDic, mihoyoBinDataNames, containers, ref i);
                }
            }
            foreach ((var pptr, var name) in mihoyoBinDataNames)
            {
                if (pptr.TryGet<MiHoYoBinData>(out var obj))
                {
                    var assetItem = objectAssetItemDic[obj];
                    if (int.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
                    {
                        assetItem.Text = name;
                        assetItem.Container = hash.ToString();
                    }
                    else assetItem.Text = $"BinFile #{assetItem.m_PathID}";
                }
            }
            if (!SkipContainer)
            {
                foreach ((var pptr, var container) in containers)
                {
                    if (pptr.TryGet(out var obj))
                    {
                        objectAssetItemDic[obj].Container = container;
                    }
                }
                containers.Clear();
                if (Game.Type.IsGISubGroup())
                {
                    UpdateContainers();
                }
            }

            const string sharedGirlPrefix = "Avatar_Girl";
            // SR locomotion clips split shared body motion from character-specific secondary bones.
            var selectedCharacterClips = exportableAssets
                .Where(x => x.Asset is AnimationClip &&
                            TryGetSharedBodySuffix(x.Text, out _) &&
                            (nameFilters.IsNullOrEmpty() || nameFilters.Any(y => y.IsMatch(x.Text))))
                .Select(x => (AnimationClip)x.Asset)
                .ToHashSet();
            var sharedAnimationSuffixes = exportableAssets
                .Where(x => x.Type == ClassIDType.AnimationClip &&
                            TryGetSharedBodySuffix(x.Text, out _) &&
                            (nameFilters.IsNullOrEmpty() || nameFilters.Any(y => y.IsMatch(x.Text))))
                .Select(x => GetSharedBodySuffix(x.Text))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linkedOriginalClips = new HashSet<AnimationClip>();
            foreach (var controller in assetsManager.assetsFileList.SelectMany(x => x.Objects).OfType<AnimatorOverrideController>())
            {
                foreach (var clipOverride in controller.m_Clips)
                {
                    if (clipOverride.m_OverrideClip.TryGet(out var overrideClip) &&
                        selectedCharacterClips.Contains(overrideClip) &&
                        clipOverride.m_OriginalClip.TryGet(out var originalClip) &&
                        linkedOriginalClips.Add(originalClip))
                    {
                        Logger.Info($"Resolved SR body animation: {overrideClip.m_Name} <= {originalClip.m_Name}");
                    }
                }
            }

            var matches = exportableAssets.Where(x =>
            {
                var isSharedBodyAnimation = x.Type == ClassIDType.AnimationClip &&
                    x.Text.StartsWith(sharedGirlPrefix, StringComparison.OrdinalIgnoreCase) &&
                    sharedAnimationSuffixes.Contains(x.Text.Substring(sharedGirlPrefix.Length));
                var isLinkedOriginalAnimation = x.Asset is AnimationClip animationClip && linkedOriginalClips.Contains(animationClip);
                var isMatchRegex = nameFilters.IsNullOrEmpty() || nameFilters.Any(y => y.IsMatch(x.Text)) ||
                    isSharedBodyAnimation || isLinkedOriginalAnimation;
                var isFilteredType = typeFilters.IsNullOrEmpty() || typeFilters.Contains(x.Type);
                var isContainerMatch = containerFilters.IsNullOrEmpty() || containerFilters.Any(y => y.IsMatch(x.Container));
                return isMatchRegex && isFilteredType && isContainerMatch;
            }).DistinctBy(x => (
                x.SourceFile.originalPath ?? x.SourceFile.fileName,
                x.m_PathID,
                x.Type)).ToList();
            var sharedAnimationCount = matches.Count(x => x.Asset is AnimationClip animationClip &&
                (linkedOriginalClips.Contains(animationClip) ||
                 (x.Text.StartsWith(sharedGirlPrefix, StringComparison.OrdinalIgnoreCase) &&
                  sharedAnimationSuffixes.Contains(x.Text.Substring(sharedGirlPrefix.Length)))));
            if (sharedAnimationCount > 0)
            {
                Logger.Info($"Included {sharedAnimationCount} shared body animation(s) for selected SR character(s).");
            }
            var girlBodyClips = matches
                .Where(x => x.Asset is AnimationClip && x.Text.StartsWith(sharedGirlPrefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Text.Substring(sharedGirlPrefix.Length), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderByDescending(candidate => HasMajorBodyCurves((AnimationClip)candidate.Asset))
                          .ThenBy(candidate => candidate.Text, StringComparer.OrdinalIgnoreCase)
                          .First(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var characterAsset in matches.Where(x => x.Asset is AnimationClip &&
                         TryGetSharedBodySuffix(x.Text, out _) &&
                         !IsAuxiliaryAnimation(x.Text)))
            {
                var characterClip = (AnimationClip)characterAsset.Asset;
                var suffix = GetSharedBodySuffix(characterAsset.Text);
                var hasBodyCandidate = girlBodyClips.TryGetValue(suffix, out var bodyAsset);
                var characterHasMajorBodyCurves = HasMajorBodyCurves(characterClip);
                var bodyHasMajorBodyCurves = hasBodyCandidate && HasMajorBodyCurves((AnimationClip)bodyAsset.Asset);
                Logger.Info($"SR pair probe: {characterAsset.Text} -> {(hasBodyCandidate ? bodyAsset.Text : "missing")} (characterMajor={characterHasMajorBodyCurves}, bodyMajor={bodyHasMajorBodyCurves})");
                if (!characterHasMajorBodyCurves && hasBodyCandidate && bodyHasMajorBodyCurves)
                {
                    characterAsset.PairedBodyAnimation = (AnimationClip)bodyAsset.Asset;
                }
            }
            var supportBodyCount = matches.RemoveAll(x => x.Asset is AnimationClip &&
                x.Text.StartsWith(sharedGirlPrefix, StringComparison.OrdinalIgnoreCase) &&
                sharedAnimationSuffixes.Contains(x.Text.Substring(sharedGirlPrefix.Length)) &&
                !nameFilters.IsNullOrEmpty() &&
                !nameFilters.Any(y => y.IsMatch(x.Text)));
            if (supportBodyCount > 0)
            {
                Logger.Info($"Using {supportBodyCount} shared body animation(s) as merge-only support assets.");
            }
            exportableAssets.Clear();
            exportableAssets.AddRange(matches);
        }

        public static void FilterMaterialDependencies(Regex[] materialRootFilters)
        {
            materialRootFilters ??= Array.Empty<Regex>();
            var materials = exportableAssets
                .Where(x => x.Asset is Material &&
                            (materialRootFilters.Length == 0 || materialRootFilters.Any(y => y.IsMatch(x.Text))))
                .Select(x => (Material)x.Asset)
                .ToHashSet();

            if (materials.Count == 0)
            {
                Logger.Warning("Material dependency filter found no matching material roots.");
                exportableAssets.Clear();
                return;
            }

            var textures = new HashSet<Texture2D>();
            foreach (var material in materials)
            {
                foreach (var texEnv in material.m_SavedProperties?.m_TexEnvs ?? Enumerable.Empty<KeyValuePair<string, UnityTexEnv>>())
                {
                    if (texEnv.Value?.m_Texture.TryGet<Texture2D>(out var texture) == true)
                        textures.Add(texture);
                }
            }

            exportableAssets.Clear();
            foreach (var texture in textures)
            {
                exportableAssets.Add(new AssetItem(texture));
            }
            Logger.Info($"Material dependency filter kept {materials.Count} material root(s) and {textures.Count} referenced Texture2D asset(s).");
        }

        public static void ProcessAssetData(Object asset, Dictionary<Object, AssetItem> objectAssetItemDic, List<(PPtr<Object>, string)> mihoyoBinDataNames, List<(PPtr<Object>, string)> containers, ref int i) 
        {
            var assetItem = new AssetItem(asset);
            objectAssetItemDic.Add(asset, assetItem);
            assetItem.UniqueID = "#" + i++;
            var exportable = false;
            switch (asset)
            {
                case GameObject m_GameObject:
                    exportable = ClassIDType.GameObject.CanExport() &&
                        (m_GameObject.HasModel() || m_GameObject.HasEffectComponents());
                    break;
                case Texture2D m_Texture2D:
                    if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                        assetItem.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                    exportable = ClassIDType.Texture2D.CanExport();
                    break;
                case AudioClip m_AudioClip:
                    if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                        assetItem.FullSize = asset.byteSize + m_AudioClip.m_Size;
                    exportable = ClassIDType.AudioClip.CanExport();
                    break;
                case VideoClip m_VideoClip:
                    if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                        assetItem.FullSize = asset.byteSize + m_VideoClip.m_ExternalResources.m_Size;
                    exportable = ClassIDType.VideoClip.CanExport();
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    exportable = ClassIDType.MonoBehaviour.CanExport();
                    break;
                case AssetBundle m_AssetBundle:
                    foreach (var m_Container in m_AssetBundle.m_Container)
                    {
                        string container = m_Container.Key;

                        if (ulong.TryParse(container, out var hash) && Paths.TryGetValue(hash, out var path))
                        {
                            container = path;
                        }
                        else if (hash == 0) //Allows HSR or other games with actual containers to extract byContainer, without needing my JSON, or other external files.
                        {
                            container = m_Container.Key;
                        }
                        else
                        {
                            container = null;
                        }

                        var preloadIndex = m_Container.Value.preloadIndex;
                        var preloadSize = m_Container.Value.preloadSize;
                        var preloadEnd = preloadIndex + preloadSize;
                        for (int k = preloadIndex; k < preloadEnd; k++)
                        {
                            containers.Add((m_AssetBundle.m_PreloadTable[k], container));
                        }
                    }

                    exportable = ClassIDType.AssetBundle.CanExport();
                    break;
                case IndexObject m_IndexObject:
                    foreach (var index in m_IndexObject.AssetMap)
                    {
                        mihoyoBinDataNames.Add((index.Value.Object, index.Key));
                    }

                    exportable = ClassIDType.IndexObject.CanExport();
                    break;
                case ResourceManager m_ResourceManager:
                    foreach (var m_Container in m_ResourceManager.m_Container)
                    {
                        containers.Add((m_Container.Value, m_Container.Key));
                    }

                    exportable = ClassIDType.GameObject.CanExport();
                    break;
                case SkinnedMeshRenderer m_SkinnedMeshRenderer when ClassIDType.SkinnedMeshRenderer.CanExport():
                    if (m_SkinnedMeshRenderer.m_GameObject.TryGet<GameObject>(out var skinnedGameObject))
                    {
                        assetItem.Text = skinnedGameObject.m_Name;
                    }
                    exportable = true;
                    break;
                case Mesh _ when ClassIDType.Mesh.CanExport():
                case TextAsset _ when ClassIDType.TextAsset.CanExport():
                case AnimationClip _ when ClassIDType.Font.CanExport():
                case Font _ when ClassIDType.GameObject.CanExport():
                case MovieTexture _ when ClassIDType.MovieTexture.CanExport():
                case Sprite _ when ClassIDType.Sprite.CanExport():
                case Material _ when ClassIDType.Material.CanExport():
                case MiHoYoBinData _ when ClassIDType.MiHoYoBinData.CanExport():
                case Shader _ when ClassIDType.Shader.CanExport():
                case Animator _ when ClassIDType.Animator.CanExport():
                    exportable = true;
                    break;
            }
            // In a scenario where a specific case doesn't exist, still allows export, without needing a class file for them.
            // Best used when --export_type Raw or Dump.
            if (!exportable && assetItem.Type.CanExport())
            {
                exportable = true;
            }
            if (assetItem.Text == "")
            {
                assetItem.Text = assetItem.TypeString + assetItem.UniqueID;
            }

            if (exportable)
            {
                exportableAssets.Add(assetItem);
            }
        }

        public static void ExportAssets(string savePath, List<AssetItem> toExportAssets, AssetGroupOption assetGroupOption, ExportType exportType, bool embedAnimations = false)
        {
            int toExportCount = toExportAssets.Count;
            int exportedCount = 0;
            WriteSrAnimationValidationReport(savePath, toExportAssets);
            var animationList = embedAnimations
                ? toExportAssets.Where(x => x.Type == ClassIDType.AnimationClip).ToList()
                : null;
            foreach (var asset in toExportAssets)
            {
                string exportPath;
                switch (assetGroupOption)
                {
                    case AssetGroupOption.ByType: //type name
                        exportPath = Path.Combine(savePath, asset.TypeString);
                        break;
                    case AssetGroupOption.ByContainer: //container path
                        if (!string.IsNullOrEmpty(asset.Container))
                        {
                            exportPath = Path.HasExtension(asset.Container) ? Path.Combine(savePath, Path.GetDirectoryName(asset.Container)) : Path.Combine(savePath, asset.Container);
                        }
                        else
                        {
                            exportPath = Path.Combine(savePath, asset.TypeString);
                        }
                        break;
                    case AssetGroupOption.BySource: //source file
                        if (string.IsNullOrEmpty(asset.SourceFile.originalPath))
                        {
                            exportPath = Path.Combine(savePath, asset.SourceFile.fileName + "_export");
                        }
                        else
                        {
                            exportPath = Path.Combine(savePath, Path.GetFileName(asset.SourceFile.originalPath) + "_export", asset.SourceFile.fileName);
                        }
                        break;
                    case AssetGroupOption.ByModel:
                        exportPath = asset.Type switch
                        {
                            ClassIDType.AnimationClip => Path.Combine(savePath, "Animations", GetAnimationModelGroup(asset.Text), GetAnimationActionGroup(asset.Text)),
                            ClassIDType.Animator => Path.Combine(savePath, "Models", asset.Text),
                            // GameObject export already creates a folder named after the object.
                            ClassIDType.GameObject => Path.Combine(savePath, "Models"),
                            _ => Path.Combine(savePath, "Models", asset.Text)
                        };
                        break;
                    default:
                        exportPath = savePath;
                        break;
                }
                exportPath += Path.DirectorySeparatorChar;
                Logger.Info($"[{exportedCount}/{toExportCount}] Exporting {asset.TypeString}: {asset.Text}");
                try
                {
                    switch (exportType)
                    {
                        case ExportType.Raw:
                            if (ExportRawFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.Dump:
                            if (ExportDumpFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.Convert:
                            if (ExportConvertFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.FBX:
                            if (ExportFbxFile(asset, exportPath, animationList))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.JSON:
                            if (ExportJSONFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.Prefab:
                            if (ExportPrefab(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Export {asset.Type}:{asset.Text} error\r\n{ex.Message}\r\n{ex.StackTrace}");
                }
            }

            var statusText = exportedCount == 0 ? "Nothing exported." : $"Finished exporting {exportedCount} assets.";

            if (toExportCount > exportedCount)
            {
                statusText += $" {toExportCount - exportedCount} assets skipped (not extractable or files already exist)";
            }

            Logger.Info(statusText);
        }

        private static void WriteSrAnimationValidationReport(string savePath, List<AssetItem> assets)
        {
            const string sparklePrefix = "Avatar_Sparkle_00";
            var sparkleAssets = assets
                .Where(x => x.Asset is AnimationClip && x.Text.StartsWith(sparklePrefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
            if (sparkleAssets.Length == 0)
                return;

            var rows = new List<string>
            {
                "# SR Animation Extraction Validation",
                string.Empty,
                "A SecondaryOnly clip contains no major body-bone curves. Merged means the exported Sparkle clip includes its shared body curves.",
                string.Empty,
                "| Sparkle clip | Clip classification | Shared body clip | Extraction result |",
                "| --- | --- | --- | --- |"
            };
            var failedCount = 0;
            var mergedCount = 0;
            foreach (var asset in sparkleAssets.OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase))
            {
                var clip = (AnimationClip)asset.Asset;
                if (IsAuxiliaryAnimation(clip.m_Name))
                {
                    rows.Add($"| {clip.m_Name} | Auxiliary | - | NotApplicable |");
                    continue;
                }

                if (HasMajorBodyCurves(clip))
                {
                    rows.Add($"| {clip.m_Name} | CompleteBody | self | Success |");
                    continue;
                }

                if (asset.PairedBodyAnimation != null)
                {
                    mergedCount++;
                    rows.Add($"| {clip.m_Name} | SecondaryOnly | {asset.PairedBodyAnimation.m_Name} | Merged |");
                }
                else
                {
                    failedCount++;
                    rows.Add($"| {clip.m_Name} | SecondaryOnly | missing | Failed |");
                }
            }

            Directory.CreateDirectory(savePath);
            File.WriteAllLines(Path.Combine(savePath, "animation_extraction_report.md"), rows);
            Logger.Info($"SR animation validation: {mergedCount} secondary-only clip(s) merged with shared body animations.");
            if (failedCount > 0)
                Logger.Error($"SR animation validation failed: {failedCount} secondary-only clip(s) have no complete body animation loaded.");
        }

        private static bool HasMajorBodyCurves(AnimationClip clip)
        {
            return clip.m_ClipBindingConstant?.genericBindings?.Any(x => MajorBodyPathHashes.Contains(x.path)) == true;
        }

        private static bool TryGetSharedBodySuffix(string name, out string suffix)
        {
            suffix = null;
            if (string.IsNullOrWhiteSpace(name) ||
                !name.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Avatar_Girl", StringComparison.OrdinalIgnoreCase))
                return false;

            var marker = name.IndexOf("_Adv_Ani_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                marker = name.IndexOf("_Ani_", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0)
                return false;

            suffix = name.Substring(marker);
            return true;
        }

        private static string GetSharedBodySuffix(string name)
        {
            return TryGetSharedBodySuffix(name, out var suffix) ? suffix : string.Empty;
        }

        private static bool IsAuxiliaryAnimation(string name)
        {
            return name.Contains("_Camera", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("_Effect", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Eff_", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAnimationModelGroup(string animationName)
        {
            if (animationName.Contains("_Camera", StringComparison.OrdinalIgnoreCase))
                return "Camera";
            var modelPrefix = GetAnimationModelPrefix(animationName);
            if (animationName.StartsWith("Eff_", StringComparison.OrdinalIgnoreCase) ||
                animationName.Contains("_Effect", StringComparison.OrdinalIgnoreCase))
                return $"{modelPrefix}_Model_Effect";
            if (animationName.Contains("_Prop", StringComparison.OrdinalIgnoreCase) ||
                animationName.Contains("_Others", StringComparison.OrdinalIgnoreCase))
                return $"{modelPrefix}_Model_Others";
            return $"{modelPrefix}_Model_Chara";
        }

        private static string GetAnimationModelPrefix(string animationName)
        {
            var marker = animationName.IndexOf("_Adv_Ani_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                marker = animationName.IndexOf("_Ani_", StringComparison.OrdinalIgnoreCase);
            if (marker > 0)
            {
                var prefix = animationName[..marker];
                if (prefix.StartsWith("Eff_", StringComparison.OrdinalIgnoreCase))
                    prefix = prefix[4..];
                return prefix;
            }

            return "Avatar_Sparkle_00";
        }

        private static string GetAnimationActionGroup(string animationName)
        {
            const string effectPrefix = "Eff_Avatar_Sparkle_00_";
            var marker = animationName.LastIndexOf("_Ani_", StringComparison.OrdinalIgnoreCase);
            var action = animationName.StartsWith(effectPrefix, StringComparison.OrdinalIgnoreCase)
                ? animationName.Substring(effectPrefix.Length)
                : marker >= 0 ? animationName.Substring(marker + 5) : animationName;
            if (action.StartsWith("FastRun", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("Run", StringComparison.OrdinalIgnoreCase))
                return "Run";
            if (action.StartsWith("Walk", StringComparison.OrdinalIgnoreCase))
                return "Walk";
            if (action.StartsWith("Turn", StringComparison.OrdinalIgnoreCase))
                return "Turn";
            if (action.StartsWith("Common_Idle", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("Idle", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("StandBy", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("TeamStandBy", StringComparison.OrdinalIgnoreCase))
                return "Idle";
            if (action.StartsWith("Skill", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("MazeSkill", StringComparison.OrdinalIgnoreCase))
                return "Skill";
            if (action.StartsWith("BeHit", StringComparison.OrdinalIgnoreCase) ||
                action.StartsWith("Hit", StringComparison.OrdinalIgnoreCase))
                return "Hit";
            if (action.StartsWith("MazeAttack", StringComparison.OrdinalIgnoreCase))
                return "Attack";
            if (action.StartsWith("UseProp", StringComparison.OrdinalIgnoreCase))
                return "Prop";
            if (action.StartsWith("LookAtPhone", StringComparison.OrdinalIgnoreCase))
                return "LookAtPhone";

            var family = Regex.Match(action, "^[A-Za-z]+", RegexOptions.CultureInvariant).Value;
            return string.IsNullOrEmpty(family) ? "Other" : family;
        }

        public static void ExportAssetsMap(string savePath, List<AssetEntry> toExportAssets, string exportListName, ExportListType exportListType)
        {
            string filename;
            switch (exportListType)
            {
                case ExportListType.XML:
                    filename = Path.Combine(savePath, $"{exportListName}.xml");
                    var settings = new XmlWriterSettings() { Indent = true };
                    using (XmlWriter writer = XmlWriter.Create(filename, settings))
                    {
                        writer.WriteStartDocument();
                        writer.WriteStartElement("Assets");
                        writer.WriteAttributeString("filename", filename);
                        writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("s"));
                        foreach (var asset in toExportAssets)
                        {
                            writer.WriteStartElement("Asset");
                            writer.WriteElementString("Name", asset.Name);
                            writer.WriteElementString("Container", asset.Container);
                            writer.WriteStartElement("Type");
                            writer.WriteAttributeString("id", ((int)asset.Type).ToString());
                            writer.WriteValue(asset.Type.ToString());
                            writer.WriteEndElement();
                            writer.WriteElementString("PathID", asset.PathID.ToString());
                            writer.WriteElementString("Source", asset.Source);
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                        writer.WriteEndDocument();
                    }
                    break;
                case ExportListType.JSON:
                    filename = Path.Combine(savePath, $"{exportListName}.json");
                    using (StreamWriter file = File.CreateText(filename))
                    {
                        JsonSerializer serializer = new JsonSerializer() { Formatting = Newtonsoft.Json.Formatting.Indented };
                        serializer.Converters.Add(new StringEnumConverter());
                        serializer.Serialize(file, toExportAssets);
                    }
                    break;
            }

            var statusText = $"Finished exporting asset list with {toExportAssets.Count()} items.";

            Logger.Info(statusText);

            Logger.Info($"AssetMap build successfully !!");
        }

        public static TypeTree MonoBehaviourToTypeTree(MonoBehaviour m_MonoBehaviour)
        {
            return m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
        }
    }
}
