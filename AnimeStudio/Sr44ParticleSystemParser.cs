using System;
using System.Collections.Generic;

namespace AnimeStudio;

public static class Sr44ParticleSystemParser
{
    public static bool TryParse(Object particleSystem, out Dictionary<string, object> data, out string error)
    {
        data = null;
        error = string.Empty;
        if (particleSystem.type != ClassIDType.ParticleSystem ||
            particleSystem.assetsFile.unityVersion.Split('\r', '\n')[0] != "2019.4.34f1")
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
                    error = "Parsed the verified ParticleSystem main, simulation, and InitialModule lifetime/speed prefix.";
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
        return new Dictionary<string, object>
        {
            ["MinMaxState"] = state,
            ["Scalar"] = ReadFiniteSingle(reader, $"{name}.scalar", -100000f, 100000f),
            ["MinScalar"] = ReadFiniteSingle(reader, $"{name}.minScalar", -100000f, 100000f),
            ["MaxCurve"] = ReadAnimationCurve(reader, $"{name}.maxCurve"),
            ["MinCurve"] = ReadAnimationCurve(reader, $"{name}.minCurve"),
        };
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
