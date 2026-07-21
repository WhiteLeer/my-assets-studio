using System;
using System.Collections.Generic;

namespace AnimeStudio;

public static class Sr44LightParser
{
    public static bool TryParse(Object light, out Dictionary<string, object> data, out string error)
    {
        data = null;
        error = string.Empty;
        if (light.type != ClassIDType.Light || light.assetsFile.unityVersion.Split('\r', '\n')[0] != "2019.4.34f1")
            return false;

        var reader = light.reader;
        reader.Position = reader.byteStart;
        try
        {
            data = new Dictionary<string, object>
            {
                ["GameObject"] = ReadPPtr(reader),
                ["Enabled"] = ReadAlignedBoolean(reader),
                ["Type"] = reader.ReadInt32(),
                ["Shape"] = reader.ReadInt32(),
                ["Color"] = reader.ReadColor4(),
                ["Intensity"] = reader.ReadSingle(),
                ["Range"] = reader.ReadSingle(),
                ["SpotAngle"] = reader.ReadSingle(),
                ["InnerSpotAngle"] = reader.ReadSingle(),
                ["CookieSize"] = reader.ReadSingle(),
                ["Shadows"] = ReadShadowSettings(reader),
                ["Cookie"] = ReadPPtr(reader),
                ["DrawHalo"] = ReadAlignedBoolean(reader),
            };
            var tailBytes = (int)reader.BytesLeft();
            data["UnmappedTailBytes"] = tailBytes;
            data["UnmappedTailHex"] = Convert.ToHexString(reader.ReadBytes(tailBytes));
            error = $"Parsed the verified Unity Light prefix; preserved {tailBytes} unmapped SR bytes.";
            return true;
        }
        catch (Exception exception)
        {
            reader.Position = reader.byteStart;
            error = exception.Message;
            return false;
        }
    }

    private static Dictionary<string, object> ReadShadowSettings(ObjectReader reader)
    {
        var matrix = new float[16];
        var data = new Dictionary<string, object>
        {
            ["Type"] = reader.ReadInt32(),
            ["Resolution"] = reader.ReadInt32(),
            ["CustomResolution"] = reader.ReadInt32(),
            ["Strength"] = reader.ReadSingle(),
            ["Bias"] = reader.ReadSingle(),
            ["NormalBias"] = reader.ReadSingle(),
            ["NearPlane"] = reader.ReadSingle(),
        };
        for (var i = 0; i < matrix.Length; i++)
            matrix[i] = reader.ReadSingle();
        data["CullingMatrixOverride"] = matrix;
        data["UseCullingMatrixOverride"] = ReadAlignedBoolean(reader);
        return data;
    }

    private static bool ReadAlignedBoolean(ObjectReader reader)
    {
        var value = reader.ReadBoolean();
        reader.AlignStream();
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
