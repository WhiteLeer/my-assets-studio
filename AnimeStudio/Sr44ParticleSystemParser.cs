using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Specialized;
using Newtonsoft.Json;

namespace AnimeStudio;

public static class Sr44ParticleSystemParser
{
    public static bool TryParse(Object particleSystem, out Dictionary<string, object> data, out string error)
    {
        data = null;
        error = string.Empty;
        if (particleSystem.type != ClassIDType.ParticleSystem)
            return false;

        var reader = particleSystem.reader;
        reader.Position = reader.byteStart;
        try
        {
            data = new Dictionary<string, object>
            {
                ["GameObject"] = ReadPPtr(reader),
                ["LengthInSec"] = ReadFiniteSingle(reader, "LengthInSec", 0.001f, 3600f),
                ["SimulationSpeed"] = ReadFiniteSingle(reader, "SimulationSpeed", 0f, 100f),
                ["StopAction"] = ReadEnum(reader, "StopAction", 0, 3),
                ["CullingMode"] = ReadEnum(reader, "CullingMode", 0, 4),
                ["RingBufferMode"] = ReadEnum(reader, "RingBufferMode", 0, 3),
                ["RingBufferLoopRange"] = reader.ReadVector2(),
                ["Looping"] = ReadBoolean(reader, "Looping"),
                ["Prewarm"] = ReadBoolean(reader, "Prewarm"),
                ["PlayOnAwake"] = ReadBoolean(reader, "PlayOnAwake"),
                ["UseUnscaledTime"] = ReadBoolean(reader, "UseUnscaledTime"),
                ["AutoRandomSeed"] = ReadBoolean(reader, "AutoRandomSeed"),
                ["UseRigidbodyForVelocity"] = ReadBoolean(reader, "UseRigidbodyForVelocity"),
            };
            reader.AlignStream();
            var extendedStart = reader.Position;
            try
            {
                data["StartDelay"] = ReadMinMaxCurve(reader, "StartDelay");
                data["SimulationSpace"] = ReadEnum(reader, "SimulationSpace", 0, 2);
                data["CustomSimulationSpace"] = ReadPPtr(reader);
                data["ScalingMode"] = ReadEnum(reader, "ScalingMode", 0, 2);
                data["RandomSeed"] = reader.ReadUInt32();
                error = "Parsed the verified ParticleSystem main and simulation prefix.";

                var initialModuleStart = reader.Position;
                try
                {
                    var initialModule = new Dictionary<string, object>
                    {
                        ["Enabled"] = ReadBoolean(reader, "InitialModule.enabled"),
                    };
                    reader.AlignStream();
                    initialModule["SrExtension0"] = ReadEnum(reader, "InitialModule.srExtension0", 0, 1);
                    initialModule["SrExtension1"] = ReadEnum(reader, "InitialModule.srExtension1", 0, 1);
                    initialModule["StartLifetime"] = ReadMinMaxCurve(reader, "InitialModule.startLifetime");
                    initialModule["StartSpeed"] = ReadMinMaxCurve(reader, "InitialModule.startSpeed");
                    data["InitialModule"] = initialModule;
                    var tailStart = reader.Position;
                    try
                    {
                        var nodes = GetParticleTypeTreeNodes(particleSystem);
                        // Some AssetStudio builds normalize the reflected type
                        // or level fields differently, but the field name is
                        // stable in the valid Unity tree.
                        var startColorIndex = nodes?.FindIndex(node => node.m_Name == "startColor") ?? -1;
                        if (startColorIndex < 0)
                            throw new InvalidOperationException("The ParticleSystem TypeTree has no stock startColor node.");

                        var startColorNode = nodes[startColorIndex];
                        var nextModuleIndex = nodes.FindIndex(startColorIndex + 1, node =>
                            node.m_Level == startColorNode.m_Level && node.m_Name != startColorNode.m_Name);
                        if (nextModuleIndex < 0)
                            nextModuleIndex = nodes.Count;

                        var moduleNodes = nodes.GetRange(startColorIndex, nextModuleIndex - startColorIndex);
                        var moduleStart = reader.Position;
                        var decodedModule = TypeTreeHelper.ReadTypeFromCurrentNodes(moduleNodes, reader);
                        var consumed = reader.Position - moduleStart;
                        if (reader.BytesLeft() < 0)
                            throw new InvalidOperationException($"startColor consumed past the ParticleSystem object by {-reader.BytesLeft()} bytes.");

                        var modules = new OrderedDictionary
                        {
                            ["startColor"] = decodedModule,
                        };
                        var nextModule = startColorIndex;
                        foreach (var moduleName in new[] { "startSize", "startSizeY", "startSizeZ" })
                        {
                            var moduleIndex = nodes.FindIndex(nextModule + 1, node =>
                                node.m_Name == moduleName && node.m_Level == startColorNode.m_Level);
                            if (moduleIndex < 0)
                                break;
                            var moduleEnd = nodes.FindIndex(moduleIndex + 1, node =>
                                node.m_Level == startColorNode.m_Level && node.m_Name != moduleName);
                            if (moduleEnd < 0)
                                moduleEnd = nodes.Count;
                            var modulePosition = reader.Position;
                            try
                            {
                                var moduleData = TypeTreeHelper.ReadTypeFromCurrentNodes(
                                    nodes.GetRange(moduleIndex, moduleEnd - moduleIndex), reader);
                                if (reader.BytesLeft() < 0)
                                    throw new InvalidOperationException($"{moduleName} consumed past the ParticleSystem object.");
                                modules[moduleName] = moduleData;
                                nextModule = moduleIndex;
                            }
                            catch
                            {
                                reader.Position = modulePosition;
                                break;
                            }
                        }
                        data["Modules"] = modules;
                        error = $"Parsed the verified ParticleSystem prefix and decoded startColor/startSize modules ({consumed} bytes minimum).";
                    }
                    catch (Exception tailException)
                    {
                        reader.Position = tailStart;
                        error = $"Parsed the verified ParticleSystem main, simulation, and InitialModule lifetime/speed prefix. Module tail decode failed: {tailException.Message}";
                    }
                }
                catch (Exception exception)
                {
                    reader.Position = initialModuleStart;
                    error = $"{error} InitialModule prefix failed: {exception.Message}";
                }
            }
            catch (Exception exception)
            {
                reader.Position = extendedStart;
                error = $"Parsed the verified ParticleSystem main prefix; simulation prefix failed: {exception.Message}";
            }
            var tailBytes = (int)reader.BytesLeft();
            data["UnmappedTailBytes"] = tailBytes;
            data["UnmappedTailHex"] = Convert.ToHexString(reader.ReadBytes(tailBytes));
            error = $"{error} Preserved {tailBytes} unmapped bytes.";
            return true;
        }
        catch (Exception exception)
        {
            reader.Position = reader.byteStart;
            data = null;
            error = exception.Message;
            return false;
        }
    }

    private static float ReadFiniteSingle(ObjectReader reader, string name, float minimum, float maximum)
    {
        var value = reader.ReadSingle();
        if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} value {value} is outside the verified range.");
        return value;
    }

    private static List<TypeTreeNode> GetParticleTypeTreeNodes(Object particleSystem)
    {
        var reflectedNodes = particleSystem.serializedType?.m_Type?.m_Nodes;
        if (particleSystem.serializedType?.m_IsExternalTypeTree == true &&
            IsUsableParticleTree(reflectedNodes))
            return reflectedNodes;

        var schemaPath = Environment.GetEnvironmentVariable("SR_PARTICLE_STOCK_TREE");
        // The Unity stock tree is not an SR 4.4 schema. Never use it silently:
        // doing so produces plausible-looking but offset particle values.
        if (string.IsNullOrWhiteSpace(schemaPath))
        {
            if (reflectedNodes != null && reflectedNodes.Any(node => node.m_Name == "startColor"))
                Logger.Warning("Ignoring embedded ParticleSystem TypeTree because it is not marked as an external SR 4.4 tree.");
            return null;
        }

        if (!File.Exists(schemaPath))
        {
            Logger.Warning($"Explicit SR_PARTICLE_STOCK_TREE path does not exist: {schemaPath}");
            return null;
        }

        Logger.Warning($"Using explicitly opted-in stock ParticleSystem schema: {schemaPath}");
        var schema = JsonConvert.DeserializeObject<StockParticleTypeTree>(File.ReadAllText(schemaPath));
        var nodes = schema?.Nodes?.Select(node => new TypeTreeNode
        {
            m_Type = node.Type,
            m_Name = node.Name,
            m_ByteSize = node.ByteSize,
            m_Index = node.Index,
            m_TypeFlags = node.TypeFlags,
            m_Version = node.Version,
            m_MetaFlag = node.MetaFlag,
            m_Level = node.Level,
            m_RefTypeHash = node.RefTypeHash,
        }).ToList();
        return IsUsableParticleTree(nodes) ? nodes : null;
    }

    private static bool IsUsableParticleTree(IReadOnlyList<TypeTreeNode> nodes)
    {
        if (nodes == null || nodes.Count < 2 || !nodes.Any(node => node.m_Name == "startColor"))
            return false;

        return nodes.All(node =>
            !string.IsNullOrWhiteSpace(node.m_Type) &&
            !string.IsNullOrWhiteSpace(node.m_Name) &&
            !node.m_Type.StartsWith("<invalid-", StringComparison.Ordinal) &&
            !node.m_Name.StartsWith("<invalid-", StringComparison.Ordinal));
    }

    private sealed class StockParticleTypeTree
    {
        public StockParticleTypeTreeNode[] Nodes { get; set; }
    }

    private sealed class StockParticleTypeTreeNode
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public int ByteSize { get; set; }
        public int Index { get; set; }
        public int TypeFlags { get; set; }
        public int Version { get; set; }
        public int MetaFlag { get; set; }
        public int Level { get; set; }
        public ulong RefTypeHash { get; set; }
    }

    private static int ReadEnum(ObjectReader reader, string name, int minimum, int maximum)
    {
        var value = reader.ReadInt32();
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} value {value} is outside [{minimum}, {maximum}].");
        return value;
    }

    private static bool ReadBoolean(ObjectReader reader, string name)
    {
        var value = reader.ReadByte();
        if (value > 1)
            throw new InvalidOperationException($"{name} byte {value} is not a serialized Boolean.");
        return value != 0;
    }

    private static Dictionary<string, object> ReadMinMaxCurve(ObjectReader reader, string name)
    {
        var state = reader.ReadUInt16();
        if (state > 3)
            throw new InvalidOperationException($"{name}.minMaxState value {state} is outside [0, 3].");
        reader.AlignStream();
        var result = new Dictionary<string, object>
        {
            ["MinMaxState"] = state,
            ["Scalar"] = ReadFiniteSingle(reader, $"{name}.scalar", -100000f, 100000f),
            ["MinScalar"] = ReadFiniteSingle(reader, $"{name}.minScalar", -100000f, 100000f),
        };
        try
        {
            result["MaxCurve"] = ReadAnimationCurve(reader, $"{name}.maxCurve");
            result["MinCurve"] = ReadAnimationCurve(reader, $"{name}.minCurve");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{name} curve decode failed at 0x{reader.Position:X}: {exception.Message}", exception);
        }
        return result;
    }

    private static Dictionary<string, object> ReadAnimationCurve(ObjectReader reader, string name)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 1024)
            throw new InvalidOperationException($"{name} key count {count} is outside [0, 1024].");
        var keys = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            keys.Add(new Dictionary<string, object>
            {
                ["Time"] = ReadCurveSingle(reader, $"{name}[{i}].time"),
                ["Value"] = ReadCurveSingle(reader, $"{name}[{i}].value"),
                ["InSlope"] = ReadCurveSingle(reader, $"{name}[{i}].inSlope", true),
                ["OutSlope"] = ReadCurveSingle(reader, $"{name}[{i}].outSlope", true),
                ["WeightedMode"] = ReadEnum(reader, $"{name}[{i}].weightedMode", 0, 3),
                ["InWeight"] = ReadCurveSingle(reader, $"{name}[{i}].inWeight"),
                ["OutWeight"] = ReadCurveSingle(reader, $"{name}[{i}].outWeight"),
            });
        }
        return new Dictionary<string, object>
        {
            ["Keys"] = keys,
            ["PreInfinity"] = ReadEnum(reader, $"{name}.preInfinity", 0, 8),
            ["PostInfinity"] = ReadEnum(reader, $"{name}.postInfinity", 0, 8),
            ["RotationOrder"] = ReadEnum(reader, $"{name}.rotationOrder", 0, 5),
        };
    }

    private static float ReadCurveSingle(ObjectReader reader, string name, bool allowInfinity = false)
    {
        var value = reader.ReadSingle();
        if (float.IsNaN(value) || (!allowInfinity && float.IsInfinity(value)))
            throw new InvalidOperationException($"{name} is not a valid curve value.");
        return value;
    }

    private static Dictionary<string, object> ReadPPtr(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["m_FileID"] = reader.ReadInt32(),
            ["m_PathID"] = reader.ReadInt64(),
        };
    }
}
