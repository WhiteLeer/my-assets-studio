using System;
using System.Collections.Generic;

namespace AnimeStudio;

public static class Sr44MonoBehaviourParser
{
    public static bool TryParse(MonoBehaviour behaviour, string className, out Dictionary<string, object> data, out bool complete, out string error)
    {
        data = null;
        complete = false;
        error = string.Empty;
        if (className != "MonoEffectPluginFollow"
            && className != "MonoEffectPluginCharaPropPartsSelection"
            && className != "MonoEffectPluginCharaPropRimLight")
            return false;

        var typeHash = Convert.ToHexString(behaviour.serializedType.m_OldTypeHash);
        var expectedTypeHash = className switch
        {
            "MonoEffectPluginFollow" => "8E792E4CBECAC8662BC546B1DB297B8F",
            "MonoEffectPluginCharaPropPartsSelection" => "328E4230D7863934B58D2F451F05D2C6",
            "MonoEffectPluginCharaPropRimLight" => "015BA2765CFCC39F6BA5D899503BC4FE",
            _ => string.Empty,
        };
        if (!string.Equals(typeHash, expectedTypeHash, StringComparison.Ordinal))
        {
            error = $"SR 4.4 schema hash mismatch: expected {expectedTypeHash}, got {typeHash}.";
            return false;
        }

        var reader = behaviour.reader;
        _ = new MonoBehaviour(reader);
        var payloadStart = reader.Position;
        try
        {
            reader.Position = payloadStart;
            var candidate = className switch
            {
                "MonoEffectPluginFollow" => ParseFollow(reader),
                "MonoEffectPluginCharaPropPartsSelection" => ParsePartsSelection(reader),
                "MonoEffectPluginCharaPropRimLight" => ParseRimLight(reader),
                _ => throw new InvalidOperationException($"Unsupported SR 4.4 schema {className}."),
            };
            if (reader.BytesLeft() == 0)
            {
                data = candidate;
                complete = true;
                return true;
            }
            if (className == "MonoEffectPluginFollow" && reader.BytesLeft() == 8)
            {
                candidate["UnmappedBehaviorData"] = Convert.ToHexString(reader.ReadBytes(8));
                data = candidate;
                error = "The final 8-byte behavior payload is not mapped by the public 4.4 SDK.";
                return true;
            }
            error = $"The SR 4.4 schema left {reader.BytesLeft()} payload bytes unread.";
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        reader.Position = payloadStart;
        return false;
    }

    private static Dictionary<string, object> ParsePartsSelection(ObjectReader reader)
    {
        var data = new Dictionary<string, object>();
        foreach (var name in new[]
        {
            "HideCharaParts", "ShowID0", "EnableShadowID0", "ShowID1", "EnableShadowID1",
            "ShowID2", "EnableShadowID2", "ShowID3", "EnableShadowID3", "ShowID4",
            "EnableShadowID4", "ShowID5", "EnableShadowID5", "ShowID6", "EnableShadowID6",
            "ShowID7", "EnableShadowID7",
        })
        {
            data[name] = reader.ReadBoolean();
            reader.AlignStream();
        }
        return data;
    }

    private static Dictionary<string, object> ParseFollow(ObjectReader reader)
    {
        var data = new Dictionary<string, object>();
        data["PositionOption"] = ReadEnum(reader, "PositionOption", 0, 10);
        data["RotationOption"] = ReadEnum(reader, "RotationOption", 0, 9);
        data["ScaleOption"] = ReadEnum(reader, "ScaleOption", 0, 8);
        data["ModelFlipOption"] = ReadEnum(reader, "ModelFlipOption", 0, 2);
        data["FollowBeforeCameraShake"] = ReadAlignedBoolean(reader);
        data["UseRandomOffsetFollow"] = ReadAlignedBoolean(reader);
        data["RandomFollowOffsetRadius"] = reader.ReadSingle();
        data["RandomFollowType"] = ReadEnum(reader, "RandomFollowType", 0, 3);
        data["RandomFollowRatio"] = reader.ReadSingle();
        data["RandomFollowSpeed"] = reader.ReadSingle();
        data["UseSmoothFollow"] = ReadAlignedBoolean(reader);
        data["SmoothFollowType"] = ReadEnum(reader, "SmoothFollowType", 0, 3);
        data["SmoothFollowRatio"] = reader.ReadSingle();
        data["SmoothFollowSpeed"] = reader.ReadSingle();
        data["SmoothDampFollowTime"] = reader.ReadSingle();
        data["SmoothDampFollowMaxSpeed"] = reader.ReadSingle();
        data["SmoothDampExpFollowTimeX"] = reader.ReadSingle();
        data["SmoothDampExpFollowTimeY"] = reader.ReadSingle();
        data["SmoothDampExpFollowTimeZ"] = reader.ReadSingle();
        data["SmoothDampExpDistanceThreshold"] = reader.ReadSingle();
        data["SmoothFollowMaxRange"] = reader.ReadSingle();
        data["DampRotationFollow"] = ReadAlignedBoolean(reader);
        data["RotationSmoothFollowType"] = ReadEnum(reader, "RotationSmoothFollowType", 0, 3);
        data["AngleDampTime"] = reader.ReadVector3();
        data["RotationSmoothFollowRatio"] = reader.ReadSingle();
        data["RotationSmoothFollowSpeed"] = reader.ReadSingle();
        data["RotationDampSnapDegrees"] = reader.ReadSingle();
        data["SpeedChangeMaxRange"] = reader.ReadSingle();
        data["OnlyFirstFrame"] = ReadAlignedBoolean(reader);
        data["ManuallFollowOnFormationChange"] = ReadAlignedBoolean(reader);
        data["StopFollow"] = ReadAlignedBoolean(reader);
        data["FollowPoints"] = ReadPPtrArray(reader);
        data["IsRotationUseFollowPoints"] = ReadAlignedBoolean(reader);
        data["RotateAroundX"] = ReadAlignedBoolean(reader);
        data["RotateAroundY"] = ReadAlignedBoolean(reader);
        data["RotateAroundZ"] = ReadAlignedBoolean(reader);
        data["BaseFollowScale"] = reader.ReadVector3();
        data["MaxFollowScale"] = reader.ReadVector3();
        data["LockPositionToGround"] = ReadAlignedBoolean(reader);
        data["CameraDistK"] = reader.ReadSingle();
        data["CameraDistA"] = reader.ReadSingle();
        data["CameraDistB"] = reader.ReadSingle();
        return data;
    }

    private static Dictionary<string, object> ParseRimLight(ObjectReader reader)
    {
        var data = new Dictionary<string, object>
        {
            ["ToggleFilter"] = ReadAlignedBoolean(reader),
            ["Filter"] = ReadEnum(reader, "Filter", 0, 15),
            ["ToggleRimBasicBias"] = ReadAlignedBoolean(reader),
            ["RimBasicBias"] = reader.ReadSingle(),
            ["ToggleRimWidth"] = ReadAlignedBoolean(reader),
            ["RimWidth"] = reader.ReadSingle(),
            ["ToggleRimOffset"] = ReadAlignedBoolean(reader),
            ["RimOffset"] = reader.ReadVector4(),
            ["ToggleFresnelColor"] = ReadAlignedBoolean(reader),
            ["FresnelColor"] = reader.ReadColor4(),
            ["ToggleFresnelBSI"] = ReadAlignedBoolean(reader),
            ["FresnelBSI"] = reader.ReadVector4(),
            ["ToggleFresnelColorStrength"] = ReadAlignedBoolean(reader),
            ["FresnelColorStrength"] = reader.ReadSingle(),
        };
        for (var i = 0; i < 8; i++)
        {
            data[$"ToggleRColor{i}"] = ReadAlignedBoolean(reader);
            data[$"RimColor{i}"] = reader.ReadColor4();
        }
        data["ToggleRColorIntensity"] = ReadAlignedBoolean(reader);
        data["RimColorIntensity"] = reader.ReadSingle();
        return data;
    }

    private static bool ReadAlignedBoolean(ObjectReader reader)
    {
        var value = reader.ReadBoolean();
        reader.AlignStream();
        return value;
    }

    private static int ReadEnum(ObjectReader reader, string name, int minimum, int maximum)
    {
        var value = reader.ReadInt32();
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} value {value} is outside [{minimum}, {maximum}].");
        return value;
    }

    private static List<Dictionary<string, object>> ReadPPtrArray(ObjectReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > reader.BytesLeft() / 12)
            throw new InvalidOperationException($"Invalid PPtr array length {count}.");
        var values = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
            values.Add(ReadPPtr(reader));
        return values;
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
