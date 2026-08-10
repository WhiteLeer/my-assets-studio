using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeStudio.CLI.Properties;
using Newtonsoft.Json;
using static AnimeStudio.CLI.Studio;

namespace AnimeStudio.CLI 
{
    public class Program
    {
        public static void Main(string[] args) => CommandLine.Init(args);

        public static void Run(Options o)
        {
            try
            {
                var game = GameManager.GetGame(o.GameName);

                if (game == null)
                {
                    Console.WriteLine("Invalid Game !!");
                    Console.WriteLine(GameManager.SupportedGames());
                    return;
                }

                Studio.Game = game;
                Logger.Default = new ConsoleLogger();
                Logger.Flags = o.LoggerFlags.Aggregate((e, x) => e |= x);
                Logger.FileLogging = Settings.Default.enableFileLogging;
                AssetsHelper.Minimal = Settings.Default.minimalAssetMap;
                AssetsHelper.IncludeAssetHashes = o.IncludeAssetHashes;
                AssetsHelper.SetUnityVersion(o.UnityVersion);
                ExternalTypeTreeDatabase.Load(o.TypeTreeDump?.FullName);

                TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Settings.Default.types));

                var classTypeFilter = Array.Empty<ClassIDType>();
                if (!o.TypeFilter.IsNullOrEmpty())
                {
                    // Explicit type filters are a parse whitelist; keep only requested types and dependencies.
                    TypeFlags.SetOnly(Array.Empty<ClassIDType>());
                    var exportTexture2D = false;
                    var exportMaterial = false;
                    var classTypeFilterList = new List<ClassIDType>();
                    for (int i = 0; i < o.TypeFilter.Length; i++)
                    {
                        var typeStr = o.TypeFilter[i];
                        var type = ClassIDType.UnknownType;
                        var flag = TypeFlag.Both;
                    
                        try
                        {
                            if (typeStr.Contains(':'))
                            {
                                var param = typeStr.Split(':');
                    
                                flag = (TypeFlag)Enum.Parse(typeof(TypeFlag), param[1], true);
                    
                                typeStr = param[0];
                            }
                    
                            type = (ClassIDType)Enum.Parse(typeof(ClassIDType), typeStr, true);

                            if (type == ClassIDType.Texture2D)
                            {
                                exportTexture2D = flag.HasFlag(TypeFlag.Export);
                            }
                            else if (type == ClassIDType.Material)
                            {
                                exportMaterial = flag.HasFlag(TypeFlag.Export);
                            }
                    
                            TypeFlags.SetType(type, flag.HasFlag(TypeFlag.Parse), flag.HasFlag(TypeFlag.Export));
                    
                            classTypeFilterList.Add(type);
                        }
                        catch(Exception e)
                        {
                            Logger.Error($"{typeStr} has invalid format, skipping...");
                            continue;
                        }
                    }

                    classTypeFilter = classTypeFilterList.ToArray();

                    if (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport())
                    {
                        TypeFlags.SetType(ClassIDType.Texture2D, true, exportTexture2D);
                        if (Settings.Default.exportMaterials || o.MaterialDependencies)
                        {
                            TypeFlags.SetType(ClassIDType.Material, true, exportMaterial);
                            if (o.MaterialDependencies)
                                TypeFlags.SetType(ClassIDType.Material, true, true);
                        }
                        if (ClassIDType.GameObject.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.Animator, true, false);
                        }
                        else if(ClassIDType.Animator.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.GameObject, true, false);
                        }
                    }

                    if (classTypeFilterList.Contains(ClassIDType.SkinnedMeshRenderer))
                    {
                        TypeFlags.SetType(ClassIDType.GameObject, true, false);
                        TypeFlags.SetType(ClassIDType.Mesh, true, true);
                        TypeFlags.SetType(ClassIDType.Transform, true, false);
                        TypeFlags.SetType(ClassIDType.RectTransform, true, false);
                        TypeFlags.SetType(ClassIDType.MeshRenderer, true, false);
                        TypeFlags.SetType(ClassIDType.MeshFilter, true, false);
                        TypeFlags.SetType(ClassIDType.Animator, true, false);
                        TypeFlags.SetType(ClassIDType.Avatar, true, false);
                    }

                    if (classTypeFilterList.Contains(ClassIDType.GameObject) || classTypeFilterList.Contains(ClassIDType.Animator))
                    {
                        // FBX conversion needs the hierarchy and renderer dependencies even when only
                        // GameObject or Animator is selected for export.
                        TypeFlags.SetType(ClassIDType.GameObject, true, classTypeFilterList.Contains(ClassIDType.GameObject));
                        TypeFlags.SetType(ClassIDType.Animator, true, classTypeFilterList.Contains(ClassIDType.Animator));
                        TypeFlags.SetType(ClassIDType.Transform, true, false);
                        TypeFlags.SetType(ClassIDType.RectTransform, true, false);
                        TypeFlags.SetType(ClassIDType.MeshRenderer, true, false);
                        TypeFlags.SetType(ClassIDType.MeshFilter, true, false);
                        TypeFlags.SetType(ClassIDType.SkinnedMeshRenderer, true, false);
                        TypeFlags.SetType(ClassIDType.Mesh, true, false);
                        TypeFlags.SetType(ClassIDType.Avatar, true, false);
                    }

                    if (classTypeFilterList.Contains(ClassIDType.AnimationClip))
                    {
                        // Path recovery for clips may come from a legacy Animation hierarchy,
                        // an Animator controller, or the referenced Avatar TOS.
                        TypeFlags.SetType(ClassIDType.Animation, true, false);
                        TypeFlags.SetType(ClassIDType.AnimatorController, true, false);
                        TypeFlags.SetType(ClassIDType.AnimatorOverrideController, true, false);
                        TypeFlags.SetType(ClassIDType.Avatar, true, false);
                        TypeFlags.SetType(ClassIDType.GameObject, true, ClassIDType.GameObject.CanExport());
                        TypeFlags.SetType(ClassIDType.Transform, true, false);
                        TypeFlags.SetType(ClassIDType.RectTransform, true, false);
                    }
                }

                if (o.AssetExportType == ExportType.Prefab)
                {
                    TypeFlags.SetType(ClassIDType.Material, true, false);
                    TypeFlags.SetType(ClassIDType.Texture2D, true, false);
                    TypeFlags.SetType(ClassIDType.Mesh, true, false);
                }

                if (o.GroupAssetsType == AssetGroupOption.ByContainer)
                {
                    TypeFlags.SetType(ClassIDType.AssetBundle, true, false);
                }

                assetsManager.Silent = o.Silent;
                assetsManager.Game = game;
                assetsManager.SpecifyUnityVersion = o.UnityVersion;
                assetsManager.ProbeReverseDependenciesOnly = o.AssetExportType == ExportType.Prefab && o.ReverseDependencies;
                o.Output.Create();

                if (o.Key != default)
                {
                    MiHoYoBinData.Encrypted = true;
                    MiHoYoBinData.Key = o.Key;
                }

                if (o.AIFile != null && game.Type.IsGISubGroup())
                {
                    ResourceIndex.FromFile(o.AIFile.FullName);
                }

                if (o.DummyDllFolder != null)
                {
                    assemblyLoader.Load(o.DummyDllFolder.FullName);
                }

                Logger.Info("Scanning for files...");
                var files = o.Input.Attributes.HasFlag(FileAttributes.Directory) ? Directory.GetFiles(o.Input.FullName, "*.*", SearchOption.AllDirectories).OrderBy(x => x.Length).ToArray() : new string[] { o.Input.FullName };
                Logger.Info($"Found {files.Length} files");
                var inputRoot = ResolveSourceRoot(o.Input.FullName, o.Input.Attributes.HasFlag(FileAttributes.Directory));
                AssetsHelper.SetBaseFolder(inputRoot);

                if (o.MapOp.HasFlag(MapOpType.CABMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        var cabMapLoaded = o.CabMapPath != null
                            ? AssetsHelper.LoadCABMap(o.CabMapPath.FullName)
                            : AssetsHelper.LoadCABMapInternal(o.MapName);
                        if (!cabMapLoaded)
                        {
                            Logger.Error($"CABMap '{o.CabMapPath?.FullName ?? o.MapName}' could not be loaded.");
                            return;
                        }
                        // Prefab export first inspects the selected seed bundle, then reloads
                        // only the CABs referenced by its manifest. Resolving the broad forward
                        // closure here duplicates work and can load hundreds of unrelated blocks.
                        assetsManager.ResolveDependencies = o.AssetExportType != ExportType.Prefab || o.ReverseDependencies;
                        assetsManager.ResolveReverseDependencies = o.ReverseDependencies;
                        if (o.AnimationMapPath != null && assetsManager.ResolveReverseDependencies)
                        {
                            Logger.Info("Skipping broad reverse dependencies because the SR animation map provides exact related sources.");
                            assetsManager.ResolveReverseDependencies = false;
                        }
                    }
                    else
                    {
                        AssetsHelper.BuildCABMap(files, o.MapName, o.Input.FullName, game);
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.AssetMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        var assetMapPath = o.AssetMapPath?.FullName ?? o.MapName;
                        var mapNameFilter = o.MapNameFilter.IsNullOrEmpty() ? o.NameFilter : o.MapNameFilter;
                        // Prefab export starts from an AnimationClip/controller name, then probes
                        // reverse dependencies for GameObject roots. Do not apply the final
                        // GameObject type filter while selecting the seed source bundle.
                        var mapTypeFilter = o.AssetExportType == ExportType.Prefab
                            ? Array.Empty<ClassIDType>()
                            : classTypeFilter;
                        files = AssetsHelper.ParseAssetMap(assetMapPath, o.MapType, mapTypeFilter, mapNameFilter, o.ContainerFilter, inputRoot);
                        if (o.AnimationMapPath != null)
                        {
                            var relatedAnimationFiles = AssetsHelper.ParseSrRelatedAnimationSources(o.AnimationMapPath.FullName, o.NameFilter, inputRoot);
                            files = files.Concat(relatedAnimationFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            Logger.Info($"Added {relatedAnimationFiles.Length} SR animation source file(s) from the global animation map.");
                        }
                    }
                    else
                    {
                        Task.Run(() => AssetsHelper.BuildAssetMap(files, o.MapName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.Both))
                {
                    Task.Run(() => AssetsHelper.BuildBoth(files, o.MapName, o.Input.FullName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                }
                if (o.MapOp.Equals(MapOpType.None) || o.MapOp.HasFlag(MapOpType.Load))
                {
                    if (files.Length == 0)
                    {
                        Logger.Warning("No source files matched the selected AssetMap filters.");
                        return;
                    }

                    var i = 0;

                    var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
                    ImportHelper.MergeSplitAssets(path);
                    var toReadFile = ImportHelper.ProcessingSplitFiles(files.ToList());

                    var fileList = new List<string>(toReadFile);
                    if (o.BatchLoad)
                    {
                        Logger.Info($"Batch loading {fileList.Count} selected source files.");
                        assetsManager.LoadFiles(fileList.ToArray());
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            BuildAssetData(classTypeFilter, o.NameFilter, o.ContainerFilter, ref i);
                            if (o.MaterialDependencies)
                                FilterMaterialDependencies(o.MaterialRootFilter);
                            if (o.AssetExportType == ExportType.Prefab)
                            {
                                var prefabManifests = exportableAssets
                                    .Where(x => x.Asset is GameObject)
                                    .Select(x => EffectPrefabManifest.Build((GameObject)x.Asset))
                                    .ToArray();
                                var prefabCabs = prefabManifests
                                    .SelectMany(x => x.EnumerateSourceCABs())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                                if (prefabCabs.Length > 0)
                                {
                                    Logger.Info($"Prefab probe found {prefabCabs.Length} directly referenced CAB(s); loading their forward dependency closure.");
                                    var prefabFiles = AssetsHelper.ResolveCABFiles(prefabCabs);
                                    if (prefabFiles.Length > 0)
                                    {
                                        exportableAssets.Clear();
                                        assetsManager.Clear();
                                        assetsManager.ResolveDependencies = false;
                                        assetsManager.ResolveReverseDependencies = false;
                                        assetsManager.ProbeReverseDependenciesOnly = false;
                                        assetsManager.LoadFiles(prefabFiles);
                                        if (assetsManager.assetsFileList.Count > 0)
                                        {
                                            BuildAssetData(classTypeFilter, o.NameFilter, o.ContainerFilter, ref i);
                                            if (o.MaterialDependencies)
                                                FilterMaterialDependencies(o.MaterialRootFilter);
                                        }
                                    }
                                    else
                                    {
                                        Logger.Warning("Prefab probe resolved no dependency block files; keeping the current loaded assets.");
                                    }
                                }
                            }
                            ExportAssets(o.Output.FullName, exportableAssets, o.GroupAssetsType, o.AssetExportType, o.EmbedAnimations);
                        }
                        exportableAssets.Clear();
                        assetsManager.Clear();
                        return;
                    }

                    foreach (var file in fileList)
                    {
                        assetsManager.LoadFiles(file);
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            BuildAssetData(classTypeFilter, o.NameFilter, o.ContainerFilter, ref i);
                            if (o.MaterialDependencies)
                                FilterMaterialDependencies(o.MaterialRootFilter);
                            ExportAssets(o.Output.FullName, exportableAssets, o.GroupAssetsType, o.AssetExportType, o.EmbedAnimations);
                        }
                        exportableAssets.Clear();
                        assetsManager.Clear();
                    }
                }
                if (Properties.Settings.Default.scrapeMonos)
                {
                    File.WriteAllLines("./Maps/PathStrings_Sorted.txt", PathStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/VOStrings_Sorted.txt", VOStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/EventStrings_Sorted.txt", EventStrings.Distinct().OrderBy(p => p));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static string ResolveSourceRoot(string inputPath, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return string.Empty;
            }

            if (!isDirectory)
            {
                return Path.GetDirectoryName(inputPath) ?? string.Empty;
            }

            var normalized = Path.GetFullPath(inputPath);
            var starRailMarker = Path.Combine("StarRail_Data", "StreamingAssets", "Asb", "Windows");
            if (normalized.EndsWith(starRailMarker, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(Path.Combine(normalized, "..", "..", "..", ".."));
            }

            if (Directory.Exists(Path.Combine(normalized, "StarRail_Data")))
            {
                return normalized;
            }

            return normalized;
        }

    }
}
