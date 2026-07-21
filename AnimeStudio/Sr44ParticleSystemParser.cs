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
            var tailBytes = (int)reader.BytesLeft();
            data["UnmappedTailBytes"] = tailBytes;
            data["UnmappedTailHex"] = Convert.ToHexString(reader.ReadBytes(tailBytes));
            error = $"Parsed the verified ParticleSystem main prefix; preserved {tailBytes} unmapped bytes.";
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

    private static Dictionary<string, object> ReadPPtr(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["m_FileID"] = reader.ReadInt32(),
            ["m_PathID"] = reader.ReadInt64(),
        };
    }
}
