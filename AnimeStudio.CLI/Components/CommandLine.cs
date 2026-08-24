using System;
using System.IO;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace AnimeStudio.CLI
{
    public static class CommandLine
    {
        public static void Init(string[] args)
        {
            var rootCommand = RegisterOptions();
            rootCommand.Invoke(args);
        }
        public static RootCommand RegisterOptions()
        {
            var optionsBinder = new OptionsBinder();
            var rootCommand = new RootCommand()
            {
                optionsBinder.Silent,
                optionsBinder.LoggerFlags,
                optionsBinder.TypeFilter,
                optionsBinder.NameFilter,
                optionsBinder.MaterialRootFilter,
                optionsBinder.MapNameFilter,
                optionsBinder.ContainerFilter,
                optionsBinder.GameName,
                optionsBinder.MapOp,
                optionsBinder.MapType,
                optionsBinder.MapName,
                optionsBinder.CabMapPath,
                optionsBinder.AssetMapPath,
                optionsBinder.AnimationMapPath,
                optionsBinder.BatchLoad,
                optionsBinder.MaterialDependencies,
                optionsBinder.ReverseDependencies,
                optionsBinder.EmbedAnimations,
                optionsBinder.UnityVersion,
                optionsBinder.GroupAssetsType,
                optionsBinder.AssetExportType,
                optionsBinder.Key,
                optionsBinder.AIFile,
                optionsBinder.DummyDllFolder,
                optionsBinder.TypeTreeDump,
                optionsBinder.ForceExternalTypeTreeClasses,
                optionsBinder.Input,
                optionsBinder.Output
            };

            rootCommand.SetHandler(Program.Run, optionsBinder);

            return rootCommand;
        }
    }
    public class Options
    {
        public bool Silent { get; set; }
        public LoggerEvent[] LoggerFlags { get; set; }
        public string[] TypeFilter { get; set; }
        public Regex[] NameFilter { get; set; }
        public Regex[] MaterialRootFilter { get; set; }
        public Regex[] MapNameFilter { get; set; }
        public Regex[] ContainerFilter { get; set; }
        public string GameName { get; set; }
        public MapOpType MapOp { get; set; }
        public bool IncludeAssetHashes { get; set; }
        public ExportListType MapType { get; set; }
        public string MapName { get; set; }
        public FileInfo CabMapPath { get; set; }
        public FileInfo AssetMapPath { get; set; }
        public FileInfo AnimationMapPath { get; set; }
        public bool BatchLoad { get; set; }
        public bool MaterialDependencies { get; set; }
        public bool ReverseDependencies { get; set; }
        public bool EmbedAnimations { get; set; }
        public string UnityVersion { get; set; }
        public AssetGroupOption GroupAssetsType { get; set; }
        public ExportType AssetExportType { get; set; }
        public byte Key { get; set; }
        public FileInfo AIFile { get; set; }
        public DirectoryInfo DummyDllFolder { get; set; }
        public FileInfo TypeTreeDump { get; set; }
        public string ForceExternalTypeTreeClasses { get; set; }
        public FileInfo Input { get; set; }
        public DirectoryInfo Output { get; set; }
    }

    public class OptionsBinder : BinderBase<Options>
    {
        public readonly Option<bool> Silent;
        public readonly Option<LoggerEvent[]> LoggerFlags;
        public readonly Option<string[]> TypeFilter;
        public readonly Option<Regex[]> NameFilter;
        public readonly Option<Regex[]> MaterialRootFilter;
        public readonly Option<Regex[]> MapNameFilter;
        public readonly Option<Regex[]> ContainerFilter;
        public readonly Option<string> GameName;
        public readonly Option<MapOpType> MapOp;
        public readonly Option<bool> IncludeAssetHashes;
        public readonly Option<ExportListType> MapType;
        public readonly Option<string> MapName;
        public readonly Option<FileInfo> CabMapPath;
        public readonly Option<FileInfo> AssetMapPath;
        public readonly Option<FileInfo> AnimationMapPath;
        public readonly Option<bool> BatchLoad;
        public readonly Option<bool> MaterialDependencies;
        public readonly Option<bool> ReverseDependencies;
        public readonly Option<bool> EmbedAnimations;
        public readonly Option<string> UnityVersion;
        public readonly Option<AssetGroupOption> GroupAssetsType;
        public readonly Option<ExportType> AssetExportType;
        public readonly Option<byte> Key;
        public readonly Option<FileInfo> AIFile;
        public readonly Option<DirectoryInfo> DummyDllFolder;
        public readonly Option<FileInfo> TypeTreeDump;
        public readonly Option<string> ForceExternalTypeTreeClasses;
        public readonly Argument<FileInfo> Input;
        public readonly Argument<DirectoryInfo> Output;

        public OptionsBinder()
        {
            Silent = new Option<bool>("--silent", "Hide log messages.");
            LoggerFlags = new Option<LoggerEvent[]>("--logger_flags", "Flags to control toggle log events.") { AllowMultipleArgumentsPerToken = true, ArgumentHelpName = "Verbose|Debug|Info|etc.." };
            TypeFilter = new Option<string[]>("--types", "Specify unity class type(s)") { AllowMultipleArgumentsPerToken = true, ArgumentHelpName = "Texture2D|Shader:Parse|Sprite:Both|etc.." };
            NameFilter = new Option<Regex[]>("--names", result => 
            {
                var items = new List<Regex>();
                var value = result.Tokens.Single().Value;
                if (File.Exists(value))
                {
                    var lines = File.ReadLines(value);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            items.Add(new Regex(line, RegexOptions.IgnoreCase));
                        }
                        catch (ArgumentException e)
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    items.AddRange(result.Tokens.Select(x => new Regex(x.Value, RegexOptions.IgnoreCase)).ToArray());
                }

                return items.ToArray();
            }, false, "Specify name regex filter(s).") { AllowMultipleArgumentsPerToken = true };
            MaterialRootFilter = new Option<Regex[]>("--material_roots", result =>
            {
                var items = new List<Regex>();
                var value = result.Tokens.Single().Value;
                if (File.Exists(value))
                {
                    foreach (var line in File.ReadLines(value))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            items.Add(new Regex(line, RegexOptions.IgnoreCase));
                    }
                }
                else
                {
                    items.AddRange(result.Tokens.Select(x => new Regex(x.Value, RegexOptions.IgnoreCase)));
                }
                return items.ToArray();
            }, false, "Material name regex roots used by --material_dependencies.") { AllowMultipleArgumentsPerToken = true };
            MapNameFilter = new Option<Regex[]>("--map_names", result =>
            {
                return result.Tokens.Select(x => new Regex(x.Value, RegexOptions.IgnoreCase)).ToArray();
            }, false, "Specify AssetMap source-selection name regex filter(s).") { AllowMultipleArgumentsPerToken = true };
            ContainerFilter = new Option<Regex[]>("--containers", result =>
            {
                var items = new List<Regex>();
                var value = result.Tokens.Single().Value;
                if (File.Exists(value))
                {
                    var lines = File.ReadLines(value);
                    foreach(var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            items.Add(new Regex(line, RegexOptions.IgnoreCase));
                        }
                        catch (ArgumentException e)
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    items.AddRange(result.Tokens.Select(x => new Regex(x.Value, RegexOptions.IgnoreCase)).ToArray());
                }

                return items.ToArray();
            }, false, "Specify container regex filter(s).") { AllowMultipleArgumentsPerToken = true };
            GameName = new Option<string>("--game", () => GameType.SR.ToString(), "SR 4.4 only.");
            MapOp = new Option<MapOpType>("--map_op", "Specify which map to build.");
            IncludeAssetHashes = new Option<bool>("--map_hashes", "Calculate per-object hashes while building AssetMap.");
            MapType = new Option<ExportListType>("--map_type", "AssetMap output type.");
            MapName = new Option<string>("--map_name", () => "assets_map", "Specify AssetMap file name.");
            CabMapPath = new Option<FileInfo>("--cab_map", "CABMap file to load when resolving cross-bundle dependencies.").LegalFilePathsOnly();
            AssetMapPath = new Option<FileInfo>("--asset_map", "AssetMap file to load when using AssetMapLoad.").LegalFilePathsOnly();
            AnimationMapPath = new Option<FileInfo>("--animation_map", "Global AnimationClip AssetMap used to include SR shared body animation bundles.").LegalFilePathsOnly();
            BatchLoad = new Option<bool>("--batch_load", "Load all selected source files together so cross-file objects remain available during export.");
            MaterialDependencies = new Option<bool>("--material_dependencies", "Export only Texture2D objects referenced by materials matching --material_roots.");
            ReverseDependencies = new Option<bool>("--reverse_dependencies", "Load bundles that directly reference selected bundles before resolving forward dependencies.");
            EmbedAnimations = new Option<bool>("--embed_animations", "Embed selected AnimationClips into each exported FBX.");
            UnityVersion = new Option<string>("--unity_version", "Specify Unity version.");
            GroupAssetsType = new Option<AssetGroupOption>("--group_assets", "Specify how exported assets should be grouped.");
            AssetExportType = new Option<ExportType>("--export_type", "Specify how assets should be exported.");
            AIFile = new Option<FileInfo>("--ai_file", "Specify asset_index json file path (to recover GI containers).").LegalFilePathsOnly();
            DummyDllFolder = new Option<DirectoryInfo>("--dummy_dlls", "Specify DummyDll path.").LegalFilePathsOnly();
            TypeTreeDump = new Option<FileInfo>("--type_tree_dump", "External structs.dump used when serialized files have stripped type trees.").LegalFilePathsOnly();
            ForceExternalTypeTreeClasses = new Option<string>("--force_type_tree_classes", "Comma-separated class IDs that must use the supplied external TypeTree, even when an embedded tree exists.");
            Input = new Argument<FileInfo>("input_path", "Input file/folder.").LegalFilePathsOnly();
            Output = new Argument<DirectoryInfo>("output_path", "Output folder.").LegalFilePathsOnly();

            Key = new Option<byte>("--key", result =>
            {
                return ParseKey(result.Tokens.Single().Value);
            }, false, "XOR key to decrypt MiHoYoBinData.");

            LoggerFlags.AddValidator(FilterValidator);
            TypeFilter.AddValidator(FilterValidator);
            NameFilter.AddValidator(FilterValidator);
            MaterialRootFilter.AddValidator(FilterValidator);
            MapNameFilter.AddValidator(FilterValidator);
            ContainerFilter.AddValidator(FilterValidator);
            Key.AddValidator(result =>
            {
                var value = result.Tokens.Single().Value;
                try
                {
                    ParseKey(value);
                }
                catch (Exception e)
                {
                    result.ErrorMessage = "Invalid byte value.\n" + e.Message;
                }
            });

            GameName.FromAmong(GameType.SR.ToString());

            LoggerFlags.SetDefaultValue(new LoggerEvent[] { LoggerEvent.Debug, LoggerEvent.Info, LoggerEvent.Warning, LoggerEvent.Error });
            GroupAssetsType.SetDefaultValue(AssetGroupOption.ByType);
            AssetExportType.SetDefaultValue(ExportType.FBX);
            MapOp.SetDefaultValue(MapOpType.None);
            MapType.SetDefaultValue(ExportListType.XML);
        }
        
        public byte ParseKey(string value)
        {
            if (value.StartsWith("0x"))
            {
                value = value[2..];
                return Convert.ToByte(value, 0x10);
            }
            else
            {
                return byte.Parse(value);
            }
        }

        public void FilterValidator(OptionResult result)
        {
            var values = result.Tokens.Select(x => x.Value).ToArray();
            foreach (var val in values)
            {
                if (string.IsNullOrWhiteSpace(val))
                {
                    result.ErrorMessage = "Empty string.";
                    return;
                }

                // Name/container filters also accept a file containing one regex per line.
                if (File.Exists(val))
                {
                    continue;
                }

                try
                {
                    Regex.Match("", val, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException e)
                {
                    result.ErrorMessage = "Invalid Regex.\n" + e.Message;
                    return;
                }
            }
        }

        protected override Options GetBoundValue(BindingContext bindingContext) =>
        new()
        {
            Silent = bindingContext.ParseResult.GetValueForOption(Silent),
            LoggerFlags = bindingContext.ParseResult.GetValueForOption(LoggerFlags),
            TypeFilter = bindingContext.ParseResult.GetValueForOption(TypeFilter),
            NameFilter = bindingContext.ParseResult.GetValueForOption(NameFilter),
            MaterialRootFilter = bindingContext.ParseResult.GetValueForOption(MaterialRootFilter),
            MapNameFilter = bindingContext.ParseResult.GetValueForOption(MapNameFilter),
            ContainerFilter = bindingContext.ParseResult.GetValueForOption(ContainerFilter),
            GameName = bindingContext.ParseResult.GetValueForOption(GameName),
            MapOp = bindingContext.ParseResult.GetValueForOption(MapOp),
            IncludeAssetHashes = bindingContext.ParseResult.GetValueForOption(IncludeAssetHashes),
            MapType = bindingContext.ParseResult.GetValueForOption(MapType),
            MapName = bindingContext.ParseResult.GetValueForOption(MapName),
            CabMapPath = bindingContext.ParseResult.GetValueForOption(CabMapPath),
            AssetMapPath = bindingContext.ParseResult.GetValueForOption(AssetMapPath),
            AnimationMapPath = bindingContext.ParseResult.GetValueForOption(AnimationMapPath),
            BatchLoad = bindingContext.ParseResult.GetValueForOption(BatchLoad),
            MaterialDependencies = bindingContext.ParseResult.GetValueForOption(MaterialDependencies),
            ReverseDependencies = bindingContext.ParseResult.GetValueForOption(ReverseDependencies),
            EmbedAnimations = bindingContext.ParseResult.GetValueForOption(EmbedAnimations),
            UnityVersion = bindingContext.ParseResult.GetValueForOption(UnityVersion),
            GroupAssetsType = bindingContext.ParseResult.GetValueForOption(GroupAssetsType),
            AssetExportType = bindingContext.ParseResult.GetValueForOption(AssetExportType),
            Key = bindingContext.ParseResult.GetValueForOption(Key),
            AIFile = bindingContext.ParseResult.GetValueForOption(AIFile),
            DummyDllFolder = bindingContext.ParseResult.GetValueForOption(DummyDllFolder),
            TypeTreeDump = bindingContext.ParseResult.GetValueForOption(TypeTreeDump),
            ForceExternalTypeTreeClasses = bindingContext.ParseResult.GetValueForOption(ForceExternalTypeTreeClasses),
            Input = bindingContext.ParseResult.GetValueForArgument(Input),
            Output = bindingContext.ParseResult.GetValueForArgument(Output)
        };
    }
}
