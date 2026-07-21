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
            && className != "MonoEffectPluginCharaPropRimLight"
            && className != "MonoEffect"
            && className != "MonoEffectPluginCharaPropManager"
            && className != "MonoEffectPluginCharaPropDissolve"
            && className != "MonoEffectPluginRotate"
            && className != "MonoEffectPluginTransform"
            && className != "MonoEffectPluginFade"
            && className != "Effect_LineRendererAni"
            && className != "MonoEffectPluginSyncTargetShaderProperty"
            && className != "MonoEffectPluginPosm"
            && className != "Effect_MaterialPropertySetter"
            && className != "BaseShaderPropertyTransition"
            && className != "CRPOutlineBlurPlugin")
            return false;

        var typeHash = Convert.ToHexString(behaviour.serializedType.m_OldTypeHash);
        var expectedTypeHash = className switch
        {
            "MonoEffectPluginFollow" => "8E792E4CBECAC8662BC546B1DB297B8F",
            "MonoEffectPluginCharaPropPartsSelection" => "328E4230D7863934B58D2F451F05D2C6",
            "MonoEffectPluginCharaPropRimLight" => "015BA2765CFCC39F6BA5D899503BC4FE",
            "MonoEffect" => "A39960593B002EAEBEB69ECF1380DEB9",
            "MonoEffectPluginCharaPropManager" => "704A4465B6F0A048E7F6DFB0A37AD573",
            "MonoEffectPluginCharaPropDissolve" => "E9CC5F1F6F13D21F94181E1EC3FC6B9A",
            "MonoEffectPluginRotate" => "030666805FBCF61A89D261671148686E",
            "MonoEffectPluginTransform" => "71BB6A6B6C8F052F948DB64C7DD3CA4F",
            "MonoEffectPluginFade" => "DF1B122127DB412D9685A2E7DAF6BF13",
            "Effect_LineRendererAni" => "4D0BB70A80692C992E446C4517C9045D",
            "MonoEffectPluginSyncTargetShaderProperty" => "4FF27DA7DBA86D6CF24206482C6FCB18",
            "MonoEffectPluginPosm" => "28C0A16AB5599431F25BFE5B54CF3296",
            "Effect_MaterialPropertySetter" => "56E8E3C9718D60DCCE1C0AA961EB481E",
            "BaseShaderPropertyTransition" => "2719522330085E2BE7D9B6940B52CCEC",
            "CRPOutlineBlurPlugin" => "59197C60AA4CAF42D4C02EDA5A762045",
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
                "MonoEffect" => ParseMonoEffect(reader),
                "MonoEffectPluginCharaPropManager" => ParseCharaPropManager(reader),
                "MonoEffectPluginCharaPropDissolve" => ParseDissolve(reader),
                "MonoEffectPluginRotate" => ParseRotate(reader),
                "MonoEffectPluginTransform" => new Dictionary<string, object>(),
                "MonoEffectPluginFade" => ParseFade(reader),
                "Effect_LineRendererAni" => ParseLineRendererAnimation(reader),
                "MonoEffectPluginSyncTargetShaderProperty" => ParseSyncTargetShaderProperty(reader),
                "MonoEffectPluginPosm" => ParsePosm(reader),
                "Effect_MaterialPropertySetter" => ParseMaterialPropertySetter(reader),
                "BaseShaderPropertyTransition" => ParseShaderPropertyTransition(reader),
                "CRPOutlineBlurPlugin" => ParseOutlineBlur(reader),
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

    private static Dictionary<string, object> ParseMonoEffect(ObjectReader reader)
    {
        var data = new Dictionary<string, object>
        {
            ["SerializedEffectFlags"] = reader.ReadUInt32(),
            ["AdaptScale"] = reader.ReadSingle(),
            ["Delay"] = reader.ReadSingle(),
            ["LocalOffset"] = reader.ReadVector3(),
            ["LocalRotationOffset"] = reader.ReadVector3(),
            ["SelfTimeSlow"] = reader.ReadSingle(),
            ["LocalScale"] = reader.ReadVector3(),
            ["MaxLifeTime"] = reader.ReadSingle(),
            ["WorldOffset"] = reader.ReadVector3(),
            ["EnableSimulateTimeAlign"] = ReadAlignedBoolean(reader),
            ["EnablePOSM"] = ReadAlignedBoolean(reader),
            ["DontSyncCasterVisibility"] = ReadAlignedBoolean(reader),
            ["DontSyncCharaMatEffectVisibility"] = ReadAlignedBoolean(reader),
            ["DontSyncCharacterStealthState"] = ReadAlignedBoolean(reader),
            ["DontDisableIfAttachedIsDisabled"] = ReadAlignedBoolean(reader),
            ["DontSyncCasterDitherAlpha"] = ReadAlignedBoolean(reader),
            ["SyncVisibilityDitherAlpha"] = ReadAlignedBoolean(reader),
            ["SyncVisibilityDitherAlphaThreshold"] = reader.ReadSingle(),
            ["AttachPoint"] = reader.ReadAlignedString(),
            ["HideOnTargetScaled"] = ReadAlignedBoolean(reader),
            ["RandomAttachPoints"] = ReadStringArray(reader),
            ["ForceShow"] = ReadAlignedBoolean(reader),
            ["FollowTimeScale"] = ReadAlignedBoolean(reader),
            ["ShaderSimulationSpeedEnable"] = ReadAlignedBoolean(reader),
            ["FollowEntityTimeScale"] = ReadAlignedBoolean(reader),
            ["FollowEntityAnimVisualTimeScale"] = ReadAlignedBoolean(reader),
            ["IsCrossMapLifeTime"] = ReadAlignedBoolean(reader),
            ["DontFinishOnTargetEntityDie"] = ReadAlignedBoolean(reader),
            ["EnableReplay"] = ReadAlignedBoolean(reader),
            ["AutoHideIfTargetFreezed"] = ReadAlignedBoolean(reader),
            ["EffectPrefab"] = ReadPPtr(reader),
            ["AttachTransformRef"] = ReadPPtr(reader),
        };
        return data;
    }

    private static Dictionary<string, object> ParseCharaPropManager(ObjectReader reader)
    {
        var data = new Dictionary<string, object>
        {
            ["Target"] = ReadPPtr(reader),
            ["UseOfflineTarget"] = ReadAlignedBoolean(reader),
            ["TargetRenderers"] = ReadStringArray(reader),
            ["TargetAttachPointNames"] = ReadStringArray(reader),
            ["TargetRenderMaterials"] = ReadRenderMaterialIndexItems(reader),
            ["NPCReplaceMatKey"] = reader.ReadAlignedString(),
            ["ClearTargetHiddenState"] = ReadAlignedBoolean(reader),
            ["RegisterToTargetMaterials"] = ReadAlignedBoolean(reader),
            ["NeedAdditionalDepth"] = ReadAlignedBoolean(reader),
            ["AdditionalRenderingLayerMasks"] = reader.ReadUInt32(),
            ["ExcludeArtModelEffects"] = ReadAlignedBoolean(reader),
            ["HideArtModelEffects"] = ReadAlignedBoolean(reader),
            ["HideTargetRendersType"] = reader.ReadInt32(),
            ["PartMonsterMaterialBlockArr"] = ReadMaterialBlockItems(reader),
            ["TargetScaleMaterialBlockArr"] = ReadMaterialBlockItems(reader),
            ["IsInheritAllMaterialProperties"] = ReadAlignedBoolean(reader),
            ["IsInheritCharacterStencilSettings"] = ReadAlignedBoolean(reader),
            ["IsInheritCharacterCommonProperties"] = ReadAlignedBoolean(reader),
            ["ToInheritMaterialPropertyArray"] = ReadMaterialBlockItems(reader),
            ["Priority"] = reader.ReadInt32(),
            ["OverlayMutexID"] = reader.ReadInt32(),
            ["IsKeyHideTargetRendersOn"] = ReadAlignedBoolean(reader),
            ["HideTargetRenders"] = ReadAlignedBoolean(reader),
            ["IsResetMaterialProperties"] = ReadAlignedBoolean(reader),
            ["MuteCharaEff"] = ReadAlignedBoolean(reader),
            ["EffectMat"] = ReadPPtr(reader),
            ["EffectMatBlocks"] = ReadReplaceMaterialBlocks(reader),
            ["ForceOverlayInheritMatProperties"] = ReadAlignedBoolean(reader),
            ["CharaProps"] = reader.ReadInt32(),
            ["FaceProps"] = reader.ReadInt32(),
            ["HairProps"] = reader.ReadInt32(),
            ["OtherProps"] = reader.ReadInt32(),
        };
        return data;
    }

    private static Dictionary<string, object> ParseDissolve(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["ToggleEnableDissolve"] = ReadAlignedBoolean(reader),
            ["EnableDissolve"] = ReadAlignedBoolean(reader),
            ["ToggleDissolveMap"] = ReadAlignedBoolean(reader),
            ["DissolveMap"] = ReadPPtr(reader),
            ["ToggleDissolveST"] = ReadAlignedBoolean(reader),
            ["DissolveST"] = reader.ReadVector4(),
            ["ToggleDistortionST"] = ReadAlignedBoolean(reader),
            ["DistortionST"] = reader.ReadVector4(),
            ["ToggleDissolveRate"] = ReadAlignedBoolean(reader),
            ["DissolveRate"] = reader.ReadSingle(),
            ["ToggleDissolveUV"] = ReadAlignedBoolean(reader),
            ["DissolveUV"] = reader.ReadSingle(),
            ["ToggleDissolveDistortionIntensity"] = ReadAlignedBoolean(reader),
            ["DissolveDistortionIntensity"] = reader.ReadSingle(),
            ["ToggleDissolveOutlineSize1"] = ReadAlignedBoolean(reader),
            ["DissolveOutlineSize1"] = reader.ReadSingle(),
            ["ToggleDissolveOutlineSize2"] = ReadAlignedBoolean(reader),
            ["DissolveOutlineSize2"] = reader.ReadSingle(),
            ["ToggleDissolveOutlineEmission"] = ReadAlignedBoolean(reader),
            ["DissolveOutlineEmission"] = reader.ReadSingle(),
            ["ToggleDissolveMapAdd"] = ReadAlignedBoolean(reader),
            ["DissolveMapAdd"] = reader.ReadSingle(),
            ["ToggleDissolveOutlineColor1"] = ReadAlignedBoolean(reader),
            ["DissolveOutlineColor1"] = reader.ReadColor4(),
            ["ToggleDissolveOutlineColor2"] = ReadAlignedBoolean(reader),
            ["DissolveOutlineColor2"] = reader.ReadColor4(),
            ["ToggleDissoveDirecMask"] = ReadAlignedBoolean(reader),
            ["DissoveDirecMask"] = reader.ReadSingle(),
            ["ToggleDissolveUVSpeed"] = ReadAlignedBoolean(reader),
            ["DissolveUVSpeed"] = reader.ReadVector4(),
            ["ToggleDissolveOutlineSmoothStep"] = ReadAlignedBoolean(reader),
            ["DissolveOutlineSmoothStep"] = reader.ReadVector2(),
            ["ToggleDissolveMask"] = ReadAlignedBoolean(reader),
            ["DissolveMask"] = ReadPPtr(reader),
            ["ToggleDissolveMaskUVSet"] = ReadAlignedBoolean(reader),
            ["DissolveMaskUVSet"] = reader.ReadSingle(),
            ["ToggleDissolveComponent"] = ReadAlignedBoolean(reader),
            ["DissolveComponent"] = reader.ReadVector4(),
            ["ToggleDissolvePosMaskOn"] = ReadAlignedBoolean(reader),
            ["DissolvePosMaskOn"] = reader.ReadSingle(),
            ["ToggleDissolvePosMaskWorldOn"] = ReadAlignedBoolean(reader),
            ["DissolvePosMaskWorldOn"] = reader.ReadSingle(),
            ["ToggleDissolvePosMaskFlipOn"] = ReadAlignedBoolean(reader),
            ["DissolvePosMaskFlipOn"] = reader.ReadSingle(),
            ["ToggleDissolvePosMaskRootOffset"] = ReadAlignedBoolean(reader),
            ["DissolvePosMaskRootOffset"] = reader.ReadVector3(),
            ["ToggleDissolvePosTarget"] = ReadAlignedBoolean(reader),
            ["DissolvePosTarget"] = ReadPPtr(reader),
            ["ToggleDissolvePosRange"] = ReadAlignedBoolean(reader),
            ["DissolvePosMaskRange"] = reader.ReadSingle(),
            ["ToggleUseDither"] = ReadAlignedBoolean(reader),
            ["UseDither"] = ReadAlignedBoolean(reader),
            ["ToggleDitherAlpha"] = ReadAlignedBoolean(reader),
            ["DitherAlpha"] = reader.ReadSingle(),
            ["ToggleDitherFadeIn"] = ReadAlignedBoolean(reader),
            ["DitherFadeIn"] = ReadAlignedBoolean(reader),
            ["ToggleDissolveShadowOff"] = ReadAlignedBoolean(reader),
            ["DissolveShadowOff"] = reader.ReadInt32(),
        };
    }

    private static Dictionary<string, object> ParseRotate(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 88);
        var nodes = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            nodes.Add(new Dictionary<string, object>
            {
                ["Node"] = ReadPPtr(reader),
                ["Space"] = ReadEnum(reader, "Space", 0, 1),
                ["FollowTimeScale"] = ReadAlignedBoolean(reader),
                ["Speed"] = reader.ReadVector3(),
                ["IsRandomSpeed"] = ReadAlignedBoolean(reader),
                ["RandomSpeedRangeMin"] = reader.ReadVector3(),
                ["RandomSpeedRangeMax"] = reader.ReadVector3(),
                ["IsRandomOrigin"] = ReadAlignedBoolean(reader),
                ["RandomOriginRangeMin"] = reader.ReadVector3(),
                ["RandomOriginRangeMax"] = reader.ReadVector3(),
            });
        }
        return new Dictionary<string, object>
        {
            ["RotateNodes"] = nodes,
            ["DelayTime"] = reader.ReadSingle(),
        };
    }

    private static Dictionary<string, object> ParseFade(ObjectReader reader)
    {
        var data = new Dictionary<string, object>
        {
            ["EnableCharaEff"] = ReadAlignedBoolean(reader),
            ["IsLogicComponent"] = ReadAlignedBoolean(reader),
            ["EffectFadeInType"] = ReadEnum(reader, "EffectFadeInType", 0, 4),
            ["FadeInTime"] = reader.ReadSingle(),
            ["PromiseFadeInTime"] = ReadAlignedBoolean(reader),
            ["SkipFadeInOnStart"] = ReadAlignedBoolean(reader),
            ["FadeInEndTrigger"] = reader.ReadAlignedString(),
            ["FadeInAnimClipName"] = reader.ReadAlignedString(),
            ["FadeInAudioEffectName"] = reader.ReadAlignedString(),
            ["FadeHoldForever"] = ReadAlignedBoolean(reader),
            ["FadeHoldTime"] = reader.ReadSingle(),
            ["EnterLoopTrigger"] = reader.ReadAlignedString(),
            ["HoldOnDissolveVal"] = reader.ReadSingle(),
            ["StopFollowOnFadeOut"] = ReadAlignedBoolean(reader),
            ["EffectFadeOutType"] = ReadEnum(reader, "EffectFadeOutType", 0, 4),
            ["FadeOutLerp"] = ReadEnum(reader, "FadeOutLerp", 0, 1),
            ["FadeOutTime"] = reader.ReadSingle(),
            ["FadeOutTrigger"] = reader.ReadAlignedString(),
            ["FadeOutAnimClipName"] = reader.ReadAlignedString(),
            ["FadeOutAudioEffectName"] = reader.ReadAlignedString(),
            ["FadeAnimator"] = ReadPPtr(reader),
            ["AffectedRendererList"] = ReadPPtrArray(reader),
            ["HideUnAffectedRenders"] = ReadAlignedBoolean(reader),
            ["EffectFadeOut2NormalizedTimeMapping"] = ReadFadeSyncSets(reader),
            ["DebugFadeIn"] = ReadAlignedBoolean(reader),
            ["DebugFadeOut"] = ReadAlignedBoolean(reader),
            ["DebugForceToHold"] = ReadAlignedBoolean(reader),
        };
        return data;
    }

    private static Dictionary<string, object> ParseLineRendererAnimation(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["LineLength"] = reader.ReadInt32(),
            ["FollowSpeed"] = reader.ReadSingle(),
            ["FollowCurve"] = ReadFloatCurve(reader, "FollowCurve"),
            ["SimulationSpace"] = reader.ReadInt32(),
            ["CustomSimulationSpace"] = ReadPPtr(reader),
            ["UseTimeDelay"] = ReadAlignedBoolean(reader),
            ["TimeDelay"] = reader.ReadSingle(),
            ["TimeBlinkCurve"] = ReadFloatCurve(reader, "TimeBlinkCurve"),
            ["LineTailOffset"] = reader.ReadVector3(),
            ["LineTailOffsetLength"] = reader.ReadSingle(),
            ["LineTailCurveOffsetEnable"] = ReadAlignedBoolean(reader),
            ["LineTailOffsetCurveX"] = ReadFloatCurve(reader, "LineTailOffsetCurveX"),
            ["LineTailOffsetCurveY"] = ReadFloatCurve(reader, "LineTailOffsetCurveY"),
            ["LineTailCenterOffsetEnable"] = ReadAlignedBoolean(reader),
            ["LineTailCenterOffsetRatio"] = reader.ReadSingle(),
            ["UseRatioSym"] = ReadAlignedBoolean(reader),
            ["PointOffsetRandom"] = reader.ReadVector3(),
            ["WaveOffset"] = reader.ReadVector3(),
            ["WaveLength"] = reader.ReadSingle(),
            ["WaveSpeed"] = reader.ReadSingle(),
            ["UseLifeTimeScaling"] = ReadAlignedBoolean(reader),
            ["LifeTime"] = reader.ReadSingle(),
            ["ScalingPosCurve"] = ReadFloatCurve(reader, "ScalingPosCurve"),
            ["ScalingWidthCurve"] = ReadFloatCurve(reader, "ScalingWidthCurve"),
            ["UseTransScale"] = ReadAlignedBoolean(reader),
            ["OriWidthMultiplier"] = reader.ReadSingle(),
            ["UseDynamicMode"] = ReadAlignedBoolean(reader),
            ["CustomDynamicCenter"] = ReadPPtr(reader),
            ["CenterUpdateMode"] = reader.ReadInt32(),
            ["CenterForceScale"] = reader.ReadSingle(),
            ["DragForce"] = reader.ReadSingle(),
            ["DynamicForceBlend"] = reader.ReadSingle(),
            ["NoiseEnable"] = ReadAlignedBoolean(reader),
            ["NoiseStrength"] = reader.ReadSingle(),
            ["NoiseFrequency"] = reader.ReadSingle(),
            ["NoiseQuality"] = reader.ReadInt32(),
            ["ScrollSpeed"] = reader.ReadSingle(),
            ["NoiseRatioCurve"] = ReadFloatCurve(reader, "NoiseRatioCurve"),
            ["UseNoiseSetupRatioTime"] = ReadAlignedBoolean(reader),
            ["NoiseSetupRatioStartTime"] = reader.ReadSingle(),
            ["NoiseSetupRatioEndTime"] = reader.ReadSingle(),
            ["FixedTickRate"] = reader.ReadUInt32(),
            ["UpdatePointInLateTick"] = ReadAlignedBoolean(reader),
            ["BlinkSmooth"] = ReadAlignedBoolean(reader),
            ["BlinkSmoothMaxRange"] = reader.ReadSingle(),
            ["BlinkSmoothFollowSpeedPow"] = reader.ReadSingle(),
            ["FlashCommitMinDistance"] = reader.ReadSingle(),
            ["FlashCommitManual"] = ReadAlignedBoolean(reader),
            ["EditorPreview"] = ReadAlignedBoolean(reader),
            ["UpdateDistanceEnable"] = ReadAlignedBoolean(reader),
            ["UpdateDistanceScale"] = reader.ReadSingle(),
            ["UpdateBoundsSizeScale"] = reader.ReadSingle(),
            ["ContinuousUpdateBounds"] = ReadAlignedBoolean(reader),
            ["LineRenderCullingMode"] = reader.ReadInt32(),
        };
    }

    private static Dictionary<string, object> ParseSyncTargetShaderProperty(ObjectReader reader)
    {
        return new Dictionary<string, object> { ["SyncType"] = reader.ReadInt32() };
    }

    private static Dictionary<string, object> ParsePosm(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["POSMList"] = ReadPPtrArray(reader),
            ["IsCharacter"] = ReadAlignedBoolean(reader),
            ["IsAttachToTarget"] = ReadAlignedBoolean(reader),
            ["NeedUI3DShadow"] = ReadAlignedBoolean(reader),
        };
    }

    private static Dictionary<string, object> ParseMaterialPropertySetter(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["Materials"] = ReadPPtrArray(reader),
            ["MaterialEnableIDs"] = ReadInt32Array(reader),
            ["OnlyFirstUpdate"] = ReadAlignedBoolean(reader),
            ["KeywordNum"] = reader.ReadInt32(),
            ["KeywordName1"] = reader.ReadAlignedString(),
            ["KeywordState1"] = ReadAlignedBoolean(reader),
            ["KeywordName2"] = reader.ReadAlignedString(),
            ["KeywordState2"] = ReadAlignedBoolean(reader),
            ["KeywordName3"] = reader.ReadAlignedString(),
            ["KeywordState3"] = ReadAlignedBoolean(reader),
            ["DataType"] = reader.ReadInt32(),
            ["PropertyName"] = reader.ReadAlignedString(),
            ["IntValue"] = reader.ReadInt32(),
            ["FloatData"] = reader.ReadSingle(),
            ["ColorData"] = reader.ReadColor4(),
            ["VectorData"] = reader.ReadVector4(),
            ["ToggleTexData"] = ReadAlignedBoolean(reader),
            ["TexData"] = ReadPPtr(reader),
        };
    }

    private static Dictionary<string, object> ParseShaderPropertyTransition(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["DitherTimeScale"] = reader.ReadSingle(),
            ["StartDitherAnimation"] = ReadAlignedBoolean(reader),
            ["TargetDitherAlpha"] = reader.ReadSingle(),
            ["AnimationDuration"] = reader.ReadSingle(),
            ["CurrentControlSource"] = reader.ReadInt32(),
            ["ElevationDitherAlpha"] = reader.ReadSingle(),
            ["DistanceDitherAlpha"] = reader.ReadSingle(),
        };
    }

    private static Dictionary<string, object> ParseOutlineBlur(ObjectReader reader)
    {
        return new Dictionary<string, object>
        {
            ["Enable"] = ReadAlignedBoolean(reader),
            ["GlobalGaussianSigma"] = reader.ReadSingle(),
            ["BlurScale"] = reader.ReadSingle(),
            ["NearPlane"] = reader.ReadSingle(),
            ["FarPlane"] = reader.ReadSingle(),
            ["OutlineColor"] = reader.ReadColor4(),
        };
    }

    private static Dictionary<string, object> ReadFloatCurve(ObjectReader reader, string name)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > Math.Max(0, (reader.BytesLeft() - 12) / 28))
            throw new InvalidOperationException($"Invalid {name} key count {count} at object offset {reader.Position - reader.byteStart - 4}.");
        var keys = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            keys.Add(new Dictionary<string, object>
            {
                ["Time"] = reader.ReadSingle(),
                ["Value"] = reader.ReadSingle(),
                ["InSlope"] = reader.ReadSingle(),
                ["OutSlope"] = reader.ReadSingle(),
                ["WeightedMode"] = reader.ReadInt32(),
                ["InWeight"] = reader.ReadSingle(),
                ["OutWeight"] = reader.ReadSingle(),
            });
        }
        return new Dictionary<string, object>
        {
            ["Keys"] = keys,
            ["PreInfinity"] = reader.ReadInt32(),
            ["PostInfinity"] = reader.ReadInt32(),
            ["RotationOrder"] = reader.ReadInt32(),
        };
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

    private static string[] ReadStringArray(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 4);
        var values = new string[count];
        for (var i = 0; i < count; i++)
            values[i] = reader.ReadAlignedString();
        return values;
    }

    private static uint[] ReadUInt32Array(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 4);
        var values = new uint[count];
        for (var i = 0; i < count; i++)
            values[i] = reader.ReadUInt32();
        return values;
    }

    private static int[] ReadInt32Array(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 4);
        var values = new int[count];
        for (var i = 0; i < count; i++)
            values[i] = reader.ReadInt32();
        return values;
    }

    private static List<Dictionary<string, object>> ReadRenderMaterialIndexItems(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 8);
        var values = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(new Dictionary<string, object>
            {
                ["RenderName"] = reader.ReadAlignedString(),
                ["MaterialIndices"] = ReadUInt32Array(reader),
            });
        }
        return values;
    }

    private static List<Dictionary<string, object>> ReadMaterialBlockItems(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 32);
        var values = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(new Dictionary<string, object>
            {
                ["PropertyName"] = reader.ReadAlignedString(),
                ["DataType"] = reader.ReadInt32(),
                ["FloatData"] = reader.ReadSingle(),
                ["AdaptFloatData"] = reader.ReadSingle(),
                ["VectorData"] = reader.ReadVector4(),
            });
        }
        return values;
    }

    private static List<Dictionary<string, object>> ReadReplaceMaterialBlocks(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 8);
        var values = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(new Dictionary<string, object>
            {
                ["RendererName"] = reader.ReadAlignedString(),
                ["Materials"] = ReadPPtrArray(reader),
            });
        }
        return values;
    }

    private static List<Dictionary<string, object>> ReadFadeSyncSets(ObjectReader reader)
    {
        var count = ReadArrayCount(reader, 12);
        var values = new List<Dictionary<string, object>>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(new Dictionary<string, object>
            {
                ["StartNormalizedTime"] = reader.ReadSingle(),
                ["EndNormalizedTime"] = reader.ReadSingle(),
                ["IsNeedFadeout"] = ReadAlignedBoolean(reader),
            });
        }
        return values;
    }

    private static int ReadArrayCount(ObjectReader reader, int minimumElementBytes)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > reader.BytesLeft() / minimumElementBytes)
            throw new InvalidOperationException($"Invalid array length {count}.");
        return count;
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
