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
                        foreach (var moduleName in new[] { "startSize", "startSizeY", "startSizeZ", "startRotationX", "startRotationY", "startRotation", "randomizeRotationDirection", "maxNumParticles", "size3D", "rotation3D", "gravityModifier" })
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

                        var topLevelStart = reader.Position;

                        // Continue from the end of InitialModule. The first
                        // consumer we need is ColorModule, which contains the
                        // color/alpha-over-lifetime gradient. Decode modules
                        // in serialized order, but stop at the first structure
                        // that the current SR tree cannot safely consume.
                        var initialModuleIndex = nodes.FindIndex(node => node.m_Name == "InitialModule");
                        var initialLevel = initialModuleIndex >= 0 ? nodes[initialModuleIndex].m_Level : -1;
                        var nextTopLevel = initialModuleIndex >= 0
                            ? nodes.FindIndex(initialModuleIndex + 1, node => node.m_Level <= initialLevel)
                            : -1;
                        if (nextTopLevel >= 0)
                        {
                            for (var moduleIndex = nextTopLevel; moduleIndex < nodes.Count;)
                            {
                                if (nodes[moduleIndex].m_Level != initialLevel)
                                {
                                    moduleIndex++;
                                    continue;
                                }

                                var moduleEnd = nodes.FindIndex(moduleIndex + 1, node => node.m_Level <= initialLevel);
                                if (moduleEnd < 0)
                                    moduleEnd = nodes.Count;
                                var moduleName = nodes[moduleIndex].m_Name;
                                var modulePosition = reader.Position;
                                try
                                {
                                    var moduleData = TypeTreeHelper.ReadTypeFromCurrentNodes(
                                        nodes.GetRange(moduleIndex, moduleEnd - moduleIndex), reader);
                                    if (reader.BytesLeft() < 0)
                                        throw new InvalidOperationException($"{moduleName} consumed past the ParticleSystem object.");
                                    modules[moduleName] = moduleData;
                                    moduleIndex = moduleEnd;
                                }
                                catch
                                {
                                    reader.Position = modulePosition;
                                    break;
                                }
                            }
                        }
                        var decodedEnd = reader.Position;
                        if (TryFindColorModule(nodes, topLevelStart, reader, out var colorModule, out _))
                        {
                            // The preceding SR modules can contain extra curve
                            // payloads.  Their stock slice is useful for
                            // inspection, but it is not safe to use its cursor
                            // as the ColorModule boundary.  Locate the fixed
                            // ColorModule structure independently and keep the
                            // sequential cursor unchanged for the remaining
                            // preserved tail.
                            modules["ColorModule"] = colorModule;
                        }
                        reader.Position = decodedEnd;
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

    private static bool TryFindColorModule(
        List<TypeTreeNode> nodes,
        long searchStart,
        ObjectReader reader,
        out OrderedDictionary colorModule,
        out long offset)
    {
        colorModule = null;
        offset = -1;
        var colorIndex = nodes.FindIndex(node => node.m_Name == "ColorModule" && node.m_Level == 1);
        if (colorIndex < 0)
            return false;

        var colorEnd = nodes.FindIndex(colorIndex + 1, node => node.m_Level <= 1);
        if (colorEnd < 0)
            colorEnd = nodes.Count;
        var colorNodes = nodes.GetRange(colorIndex, colorEnd - colorIndex);
        var savedPosition = reader.Position;
        var objectEnd = reader.byteStart + reader.byteSize;
        try
        {
            // ColorModule is a fixed-size Unity structure in this tree.  Its
            // serialized start is four-byte aligned after the preceding
            // variable-length modules.
            for (var candidate = searchStart; candidate + 360 < objectEnd; candidate++)
            {
                try
                {
                    reader.Position = candidate;
                    var decoded = TypeTreeHelper.ReadTypeFromCurrentNodes(colorNodes, reader);
                    var plausible = IsPlausibleColorModule(decoded);
                    if (!plausible || reader.Position - candidate < 360)
                        continue;

                    colorModule = decoded;
                    offset = candidate - searchStart;
                    return true;
                }
                catch
                {
                    // A candidate inside an earlier curve or pointer payload
                    // is expected to fail validation.  Continue scanning.
                }
            }
            return false;
        }
        finally
        {
            reader.Position = savedPosition;
        }
    }

    private static bool IsPlausibleColorModule(OrderedDictionary decoded)
    {
        if (decoded == null || !decoded.Contains("ColorModule") || decoded["ColorModule"] is not OrderedDictionary module)
            return false;
        if (module["enabled"] is not bool)
            return false;
        if (module["gradient"] is not OrderedDictionary gradient)
            return false;

        if (!TryInt(gradient, "minMaxState", out var state) || state < 0 || state > 3)
            return false;
        if (!IsPlausibleColor(gradient, "minColor") || !IsPlausibleColor(gradient, "maxColor"))
            return false;
        foreach (var gradientName in new[] { "maxGradient", "minGradient" })
        {
            if (gradient[gradientName] is not OrderedDictionary nested)
                return false;
            if (!TryInt(nested, "m_Mode", out var mode) || mode < 0 || mode > 4)
                return false;
            if (!TryInt(nested, "m_NumColorKeys", out var colorKeys) || colorKeys < 0 || colorKeys > 8)
                return false;
            if (!TryInt(nested, "m_NumAlphaKeys", out var alphaKeys) || alphaKeys < 0 || alphaKeys > 8)
                return false;
            for (var i = 0; i < 8; i++)
            {
                if (nested[$"key{i}"] is not OrderedDictionary key || !IsFiniteColor(key))
                    return false;
                if (!TryInt(nested, $"ctime{i}", out var colorTime) || colorTime < 0 || colorTime > 65535)
                    return false;
                if (!TryInt(nested, $"atime{i}", out var alphaTime) || alphaTime < 0 || alphaTime > 65535)
                    return false;
            }
        }
        return true;
    }

    private static bool IsPlausibleColor(OrderedDictionary dictionary, string name)
    {
        return dictionary[name] is OrderedDictionary color && IsFiniteColor(color);
    }

    private static bool IsFiniteColor(OrderedDictionary color)
    {
        foreach (var channel in new[] { "r", "g", "b", "a" })
        {
            if (!TryFloat(color, channel, out var value) ||
                float.IsNaN(value) ||
                float.IsInfinity(value) ||
                Math.Abs(value) > 8f ||
                (value != 0f && Math.Abs(value) < 0.000001f))
                return false;
        }
        return true;
    }

    private static bool TryInt(OrderedDictionary dictionary, string name, out int value)
    {
        try
        {
            value = Convert.ToInt32(dictionary[name]);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryFloat(OrderedDictionary dictionary, string name, out float value)
    {
        try
        {
            value = Convert.ToSingle(dictionary[name]);
            return true;
        }
        catch
        {
            value = 0f;
            return false;
        }
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
