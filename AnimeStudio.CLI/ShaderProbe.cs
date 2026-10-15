using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class ShaderProbe
    {
        public static void WriteReport(
            string outputDirectory,
            IEnumerable<SerializedFile> sourceFiles,
            Regex[] nameFilters,
            string pathIdText,
            string sourceSuffix = null)
        {
            Directory.CreateDirectory(outputDirectory);
            var pathIds = ParsePathIds(pathIdText);
            var shaders = sourceFiles
                .SelectMany(file => file.Objects.OfType<Shader>())
                .Where(shader => pathIds == null || pathIds.Contains(shader.m_PathID))
                .Where(shader => nameFilters.IsNullOrEmpty() || nameFilters.Any(filter => filter.IsMatch(shader.Name)))
                .OrderBy(shader => shader.assetsFile.fileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(shader => shader.m_PathID)
                .ToArray();
            var shaderKeys = shaders.Select(GetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var materials = sourceFiles
                .SelectMany(file => file.Objects.OfType<Material>())
                .Where(material => IsReferenceToSelectedShader(material, shaderKeys))
                .OrderBy(material => material.assetsFile.fileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(material => material.m_PathID)
                .ToArray();
            var shaderProperties = shaders.ToDictionary(
                GetKey,
                shader => new HashSet<string>(
                    shader.m_ParsedForm?.m_PropInfo?.m_Props?.Select(x => x.m_Name) ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal));

            var root = new JObject
            {
                ["schema"] = "anime-studio.shader-report.v1",
                ["game"] = Studio.Game?.Name ?? string.Empty,
                ["sourceFiles"] = new JArray(sourceFiles.Select(ToSourceFile)),
                ["selection"] = new JObject
                {
                    ["pathIds"] = pathIds == null ? JValue.CreateNull() : new JArray(pathIds.OrderBy(x => x)),
                    ["nameFilters"] = nameFilters.IsNullOrEmpty() ? new JArray() : new JArray(nameFilters.Select(x => x.ToString()))
                },
                ["notes"] = new JArray(
                    "All names, indices, counts and references in this report are read from the parsed asset data.",
                    "For SR, large platform-specific compiled blobs are intentionally not loaded by the parser unless --shader_blobs is explicitly used.",
                    "A parsed Shader metadata record is not equivalent to recoverable original HLSL source."
                ),
                ["shaders"] = new JArray(shaders.Select(shader => ToShader(shader, outputDirectory))),
                ["materials"] = new JArray(materials.Select(material =>
                    ToMaterial(material, GetShaderProperties(material, shaderProperties))))
            };

            var fileName = string.IsNullOrWhiteSpace(sourceSuffix)
                ? "shader-report.json"
                : $"shader-report-{Sanitize(sourceSuffix)}.json";
            var reportPath = Path.Combine(outputDirectory, fileName);
            File.WriteAllText(reportPath, root.ToString(Formatting.Indented));
            WriteUnityBindingManifest(outputDirectory, shaders, materials, shaderProperties, root["shaders"] as JArray);
            Logger.Info($"Shader report wrote {shaders.Length} Shader(s) and {materials.Length} referencing Material(s): {reportPath}");
        }

        private static void WriteUnityBindingManifest(
            string outputDirectory,
            IReadOnlyList<Shader> shaders,
            IReadOnlyList<Material> materials,
            IReadOnlyDictionary<string, HashSet<string>> shaderProperties,
            JArray shaderReports)
        {
            var manifest = new JObject
            {
                ["schema"] = "anime-studio.unity-material-bindings.v2",
                ["notes"] = new JArray(
                    "Only exact property-name intersections are emitted as safe bindings.",
                    "Unbound material slots remain listed and are not redirected to another shader property.",
                    "Texture sourceFile and pathId identify the parsed asset reference; they are not filesystem paths.",
                    "Generated ShaderLab variants are indexed separately; a material is not assigned to a variant unless its evidence is sufficient."),
                ["shaders"] = new JArray(shaders.Select(shader => new JObject
                {
                    ["name"] = shader.Name ?? string.Empty,
                    ["pathId"] = shader.m_PathID,
                    ["source"] = ToObjectSource(shader),
                    ["properties"] = new JArray(shader.m_ParsedForm?.m_PropInfo?.m_Props?.Select(x => x.m_Name) ?? Enumerable.Empty<string>()),
                    ["variantFiles"] = ToSourceVariantFiles(outputDirectory, shader, shaderReports)
                })),
                ["materials"] = new JArray(materials.Select(material =>
                    ToUnityMaterialBinding(material, GetShaderProperties(material, shaderProperties))))
            };
            File.WriteAllText(
                Path.Combine(outputDirectory, "unity-material-bindings.json"),
                manifest.ToString(Formatting.Indented));
        }

        private static JObject ToUnityMaterialBinding(Material material, ISet<string> shaderProperties)
        {
            var textureEntries = material.m_SavedProperties?.m_TexEnvs ?? new List<KeyValuePair<string, UnityTexEnv>>();
            var floatEntries = material.m_SavedProperties?.m_Floats ?? new List<KeyValuePair<string, float>>();
            var intEntries = material.m_SavedProperties?.m_Ints ?? new List<KeyValuePair<string, int>>();
            var colorEntries = material.m_SavedProperties?.m_Colors ?? new List<KeyValuePair<string, Color>>();
            var propertyNames = textureEntries.Select(x => x.Key)
                .Concat(floatEntries.Select(x => x.Key))
                .Concat(intEntries.Select(x => x.Key))
                .Concat(colorEntries.Select(x => x.Key))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var declared = shaderProperties == null
                ? Array.Empty<string>()
                : propertyNames.Where(shaderProperties.Contains).ToArray();
            var unbound = propertyNames.Except(declared, StringComparer.Ordinal).ToArray();

            return new JObject
            {
                ["name"] = material.Name ?? string.Empty,
                ["pathId"] = material.m_PathID,
                ["source"] = ToObjectSource(material),
                ["shader"] = new JObject
                {
                    ["fileId"] = material.m_Shader?.m_FileID ?? 0,
                    ["pathId"] = material.m_Shader?.m_PathID ?? 0,
                    ["sourceFile"] = material.m_Shader?.SourceFileName ?? string.Empty,
                    ["resolvedName"] = material.m_Shader?.Name ?? string.Empty
                },
                ["shaderKeywords"] = material.m_ShaderKeywords ?? string.Empty,
                ["shaderKeywordNames"] = new JArray(SplitKeywordString(material.m_ShaderKeywords)),
                ["validKeywords"] = new JArray(material.m_ValidKeywords ?? Array.Empty<string>()),
                ["invalidKeywords"] = new JArray(material.m_InvalidKeywords ?? Array.Empty<string>()),
                ["disabledShaderPasses"] = new JArray(material.m_DisabledShaderPasses ?? Array.Empty<string>()),
                ["status"] = shaderProperties == null ? "shader_not_resolved" : "ready_for_exact_import",
                ["declaredProperties"] = new JArray(declared),
                ["unboundProperties"] = new JArray(unbound),
                ["textures"] = new JArray(textureEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(ToTexEnv)),
                ["floats"] = new JObject(floatEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(x => new JProperty(x.Key, x.Value))),
                ["floatValues"] = new JArray(floatEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(x => new JObject { ["property"] = x.Key, ["value"] = x.Value })),
                ["ints"] = new JObject(intEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(x => new JProperty(x.Key, x.Value))),
                ["intValues"] = new JArray(intEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(x => new JObject { ["property"] = x.Key, ["value"] = x.Value })),
                ["colors"] = new JObject(colorEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(x => new JProperty(x.Key, new JArray(x.Value.R, x.Value.G, x.Value.B, x.Value.A)))),
                ["colorValues"] = new JArray(colorEntries
                    .Where(x => shaderProperties?.Contains(x.Key) == true)
                    .Select(x => new JObject
                    {
                        ["property"] = x.Key,
                        ["value"] = new JArray(x.Value.R, x.Value.G, x.Value.B, x.Value.A)
                    }))
            };
        }

        private static JArray ToSourceVariantFiles(string outputDirectory, Shader shader, JArray shaderReports)
        {
            var directory = Path.Combine(outputDirectory, "shader-source");
            if (!Directory.Exists(directory))
                return new JArray();

            var stem = Sanitize(shader.Name ?? "Shader") + "_SS";
            var report = shaderReports?.FirstOrDefault(x =>
                string.Equals((string)x["name"], shader.Name, StringComparison.Ordinal) &&
                (long?)x["pathId"] == shader.m_PathID) as JObject;
            var mappings = report?["sourceShader"]?["variantMappings"] as JArray;
            return new JArray(Directory.EnumerateFiles(directory, stem + "*.shader")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var file = Path.GetFileName(path);
                    var mapping = mappings?.FirstOrDefault(x =>
                        string.Equals(Path.GetFileName((string)x["shaderPath"]), file, StringComparison.OrdinalIgnoreCase)) as JObject;
                    return new JObject
                    {
                        ["file"] = file,
                        ["relativePath"] = Path.Combine("shader-source", file).Replace('\\', '/'),
                        ["shaderLabName"] = ReadShaderLabName(path),
                        ["platform"] = mapping?["platform"] ?? string.Empty,
                        ["subShader"] = mapping?["subShader"] ?? -1,
                        ["pass"] = mapping?["pass"] ?? -1,
                        ["variant"] = mapping?["variant"] ?? -1,
                        ["keywordIndices"] = mapping == null ? new JObject() : new JObject
                        {
                            ["vertex"] = mapping["vertex"]?["keywordIndices"] ?? new JObject(),
                            ["fragment"] = mapping["fragment"]?["keywordIndices"] ?? new JObject()
                        },
                        ["keywordNames"] = ToVariantKeywordNames(mapping),
                        ["compilerFeatureStringsBeforeFirstGlobalBuffer"] = ToVariantFeatureStrings(mapping)
                    };
                }));
        }

        private static JArray ToVariantKeywordNames(JObject mapping)
        {
            if (mapping == null)
                return new JArray();
            return new JArray(
                new[] { mapping["vertex"]?["keywordNames"], mapping["fragment"]?["keywordNames"] }
                    .SelectMany(value => value is JArray array ? array.Values<string>() : Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal));
        }

        private static JArray ToVariantFeatureStrings(JObject mapping)
        {
            if (mapping == null)
                return new JArray();
            return new JArray(
                new[] { mapping["vertex"]?["infoRecord"]?["leadingStringsBeforeFirstGlobalBuffer"], mapping["fragment"]?["infoRecord"]?["leadingStringsBeforeFirstGlobalBuffer"] }
                    .SelectMany(value => value is JArray array
                        ? array.Select(token => (string)token["value"] ?? string.Empty)
                        : Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal));
        }

        private static IEnumerable<string> SplitKeywordString(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ' ', '\r', '\n', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(keyword => !string.Equals(keyword, ",", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal);
        }

        private static string ReadShaderLabName(string path)
        {
            try
            {
                using var reader = new StreamReader(path);
                var line = reader.ReadLine();
                if (line == null)
                    return string.Empty;
                var match = Regex.Match(line, "^Shader\\s+\\\"(?<name>[^\\\"]+)\\\"");
                return match.Success ? match.Groups["name"].Value : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static JObject ToSourceFile(SerializedFile file) => new()
        {
            ["fileName"] = file.fileName ?? string.Empty,
            ["originalPath"] = file.originalPath ?? string.Empty,
            ["unityVersion"] = file.unityVersion ?? string.Empty
        };

        private static JObject ToShader(Shader shader, string outputDirectory)
        {
            var parsed = shader.m_ParsedForm;
            var result = new JObject
            {
                ["name"] = shader.Name ?? string.Empty,
                ["pathId"] = shader.m_PathID,
                ["source"] = ToObjectSource(shader),
                ["byteSize"] = shader.byteSize,
                ["typeTreeHash"] = shader.serializedType?.m_OldTypeHash == null
                    ? string.Empty
                    : Convert.ToHexString(shader.serializedType.m_OldTypeHash),
                ["dataStatus"] = new JObject
                {
                    ["parsedMetadata"] = parsed != null,
                    ["compiledBlob"] = shader.m_SRShaderBlobsInlineDataPresent || shader.m_SubProgramBlob != null || shader.compressedBlob != null || shader.platformInfos != null,
                    ["compiledBlobReadAttempted"] = shader.m_SRShaderBlobsReadAttempted,
                    ["compiledBlobTruncated"] = shader.m_SRShaderBlobsTruncated,
                    ["compiledBlobInlineDataPresent"] = shader.m_SRShaderBlobsInlineDataPresent,
                    ["originalShaderSource"] = shader.m_Script != null && shader.m_Script.Length > 0,
                    ["bindings"] = parsed != null && HasBindingRecords(parsed)
                },
                ["compiledData"] = new JObject
                {
                    ["platforms"] = new JArray(shader.platforms?.Select(x => x.ToString()) ?? Enumerable.Empty<string>()),
                    ["subProgramBlobBytes"] = shader.m_SubProgramBlob?.Length ?? 0,
                    ["compressedBlobBytes"] = shader.compressedBlob?.Length ?? 0,
                    ["platformInfoCount"] = shader.platformInfos?.Length ?? 0,
                    ["objectStart"] = shader.m_SRShaderObjectStart,
                    ["objectEnd"] = shader.m_SRShaderObjectEnd,
                    ["parsedFormEnd"] = shader.m_SRShaderParsedFormEnd,
                    ["platformsEnd"] = shader.m_SRShaderPlatformsEnd,
                    ["platformInfosEnd"] = shader.m_SRShaderPlatformInfosEnd,
                    ["objectRemainingAfterPlatforms"] = shader.m_SRShaderObjectRemainingAfterPlatforms,
                    ["readFailureOffset"] = shader.m_SRShaderBlobsFailureOffset,
                    ["readRemainingBytes"] = shader.m_SRShaderBlobsRemaining,
                    ["readFailureObjectOffset"] = shader.m_SRShaderBlobsObjectOffset,
                    ["readObjectRemainingBytes"] = shader.m_SRShaderBlobsObjectRemaining,
                    ["tailPrefixHex"] = shader.m_SRShaderTailPrefix == null
                        ? string.Empty
                        : Convert.ToHexString(shader.m_SRShaderTailPrefix),
                    ["platformInfoRecords"] = new JArray(shader.platformInfos?.Select(ToPlatformInfo) ?? Enumerable.Empty<JObject>()),
                    ["usesExternalBlobs"] = shader.m_UseExternalBlobs,
                    ["stageCounts"] = shader.stageCounts == null ? new JArray() : new JArray(shader.stageCounts)
                }
            };

            if (parsed == null)
            {
                result["parseWarnings"] = new JArray("Serialized Shader metadata was not parsed.");
                return result;
            }

            result["serialized"] = new JObject
            {
                ["name"] = parsed.m_Name ?? string.Empty,
                ["customEditor"] = parsed.m_CustomEditorName ?? string.Empty,
                ["fallback"] = parsed.m_FallbackName ?? string.Empty,
                ["defaultKeywords"] = new JArray(parsed.m_DefaultKeywords ?? Array.Empty<string>()),
                ["keywordNames"] = new JArray(parsed.m_KeywordNames ?? Array.Empty<string>()),
                ["keywordFlags"] = new JArray(parsed.m_KeywordFlags ?? Array.Empty<byte>()),
                ["globalKeywordCount"] = parsed.m_GlobalKeywordCount,
                ["properties"] = new JArray(parsed.m_PropInfo?.m_Props?.Select(ToProperty) ?? Enumerable.Empty<JObject>()),
                ["dependencies"] = new JArray(parsed.m_Dependencies?.Select(x => new JObject { ["from"] = x.from, ["to"] = x.to }) ?? Enumerable.Empty<JObject>()),
                ["subShaders"] = new JArray(parsed.m_SubShaders?.Select(ToSubShader) ?? Enumerable.Empty<JObject>())
            };
            result["shaderLab"] = TryWriteShaderLab(shader, outputDirectory);
            result["sourceShader"] = TryWriteSourceShader(shader, outputDirectory);
            result["hlsl"] = TryWriteHlsl(shader, outputDirectory);
            if (shader.m_SRShaderBlobsTruncated)
            {
                result["parseWarnings"] = new JArray(
                    "SR compiled shader blob reading was attempted but stopped at end of object data.",
                    $"Failure reader offset: {shader.m_SRShaderBlobsFailureOffset}; remaining bytes: {shader.m_SRShaderBlobsRemaining}."
                );
            }
            else if (shader.m_SRShaderBlobsReadAttempted && !shader.m_SRShaderBlobsInlineDataPresent)
            {
                result["parseWarnings"] = new JArray(
                    "SR shader object contains serialized metadata and platform identifiers, but no inline compiled blob data.",
                    $"Object bytes remaining after platforms: {shader.m_SRShaderObjectRemainingAfterPlatforms}."
                );
            }
            return result;
        }

        private static JObject TryWriteShaderLab(Shader shader, string outputDirectory)
        {
            if (shader.platformInfos == null && shader.m_SubProgramBlob == null && shader.compressedBlob == null)
                return new JObject { ["status"] = "not_available" };

            try
            {
                var shaderLab = shader.Convert();
                if (string.IsNullOrWhiteSpace(shaderLab))
                    return new JObject { ["status"] = "converter_returned_empty" };

                var shaderDirectory = Path.Combine(outputDirectory, "shader-lab");
                Directory.CreateDirectory(shaderDirectory);
                var fileName = Sanitize(shader.Name ?? $"Shader_{shader.m_PathID}") + ".shader";
                var filePath = Path.Combine(shaderDirectory, fileName);
                File.WriteAllText(filePath, shaderLab);
                return new JObject
                {
                    ["status"] = "written",
                    ["format"] = "unity_serialized_shader",
                    ["directSourceImport"] = false,
                    ["path"] = filePath
                };
            }
            catch (Exception error)
            {
                return new JObject
                {
                    ["status"] = "failed",
                    ["errorType"] = error.GetType().FullName,
                    ["message"] = error.Message
                };
            }
        }

        private static JObject TryWriteHlsl(Shader shader, string outputDirectory)
        {
            if (shader.platformInfos == null || shader.m_ParsedForm?.m_SubShaders == null)
                return new JObject { ["status"] = "not_available" };

            try
            {
                var programs = ShaderConverter.BuildHsrShaderPrograms(shader);
                var directory = Path.Combine(outputDirectory, "hlsl");
                Directory.CreateDirectory(directory);
                var files = new JArray();
                var skipped = new JArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (var platformIndex = 0; platformIndex < programs.Length; platformIndex++)
                {
                    var platform = platformIndex < shader.platforms.Length
                        ? shader.platforms[platformIndex].ToString()
                        : $"platform_{platformIndex}";
                    var program = programs[platformIndex];
                    for (var subShaderIndex = 0; subShaderIndex < shader.m_ParsedForm.m_SubShaders.Count; subShaderIndex++)
                    {
                        var passes = shader.m_ParsedForm.m_SubShaders[subShaderIndex].m_Passes ?? new List<SerializedPass>();
                        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
                        {
                            var pass = passes[passIndex];
                            foreach (var stage in EnumerateStages(pass))
                            {
                                foreach (var subProgram in stage.Program?.m_SubPrograms ?? Enumerable.Empty<SerializedSubProgram>())
                                {
                                    var blobIndex = subProgram.m_BlobIndex;
                                    if (blobIndex >= program.m_SubPrograms.Length)
                                        continue;

                                    var compiled = program.m_SubPrograms[blobIndex];
                                    var source = string.Empty;
                                    var error = "program is unavailable";
                                    if (compiled == null || !compiled.TryExportHlsl(out source, out error))
                                    {
                                        skipped.Add(new JObject
                                        {
                                            ["platform"] = platform,
                                            ["stage"] = stage.Name,
                                            ["blobIndex"] = blobIndex,
                                            ["programType"] = compiled?.m_ProgramType.ToString() ?? "unknown",
                                            ["reason"] = error ?? "program is unavailable"
                                        });
                                        continue;
                                    }

                                    var identity = platform + "\n" + stage.Name + "\n" + source;
                                    if (!seen.Add(identity))
                                        continue;

                                    var stem = Sanitize(shader.Name ?? "Shader");
                                    var stageKey = stage.Name.ToLowerInvariant();
                                    counters.TryGetValue(stageKey, out var sequence);
                                    sequence++;
                                    counters[stageKey] = sequence;
                                    var fileName = $"{stem}_{stageKey}_{sequence:000}.hlsl";
                                    var path = Path.Combine(directory, fileName);
                                    var contents = $"// Source Shader: {shader.Name}\n" +
                                                   $"// Platform: {platform}\n" +
                                                   $"// Stage: {stage.Name}\n" +
                                                   $"// BlobIndex: {blobIndex}\n" +
                                                   $"// ProgramType: {compiled.m_ProgramType}\n\n" + source;
                                    File.WriteAllText(path, contents);
                                    files.Add(new JObject
                                    {
                                        ["path"] = path,
                                        ["platform"] = platform,
                                        ["stage"] = stage.Name,
                                        ["blobIndex"] = blobIndex,
                                        ["programType"] = compiled.m_ProgramType.ToString()
                                    });
                                }
                            }
                        }
                    }
                }

                return new JObject
                {
                    ["status"] = files.Count == 0 ? "no_supported_programs" : "written",
                    ["directory"] = directory,
                    ["uniqueProgramCount"] = files.Count,
                    ["files"] = files,
                    ["skippedProgramCount"] = skipped.Count,
                    ["skippedPrograms"] = skipped
                };
            }
            catch (Exception error)
            {
                return new JObject
                {
                    ["status"] = "failed",
                    ["errorType"] = error.GetType().FullName,
                    ["message"] = error.Message
                };
            }
        }

        private static JObject TryWriteSourceShader(Shader shader, string outputDirectory)
        {
            if (shader.platformInfos == null || shader.m_ParsedForm?.m_SubShaders == null)
                return new JObject { ["status"] = "not_available" };

            try
            {
                var programs = ShaderConverter.BuildHsrShaderPrograms(shader);
                var directory = Path.Combine(outputDirectory, "shader-source");
                Directory.CreateDirectory(directory);
                var files = new JArray();
                var skipped = new JArray();
                var variantMappings = new JArray();
                var sourceToPath = new Dictionary<string, string>(StringComparer.Ordinal);
                var sourceCount = 0;

                for (var platformIndex = 0; platformIndex < programs.Length; platformIndex++)
                {
                    if (platformIndex >= shader.platforms.Length ||
                        shader.platforms[platformIndex] != ShaderCompilerPlatform.D3D11)
                        continue;

                    var program = programs[platformIndex];
                    for (var subShaderIndex = 0; subShaderIndex < shader.m_ParsedForm.m_SubShaders.Count; subShaderIndex++)
                    {
                        var subShader = shader.m_ParsedForm.m_SubShaders[subShaderIndex];
                        var passes = subShader.m_Passes ?? new List<SerializedPass>();
                        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
                        {
                            var pass = passes[passIndex];
                            var vertices = pass.progVertex?.m_SubPrograms ?? new List<SerializedSubProgram>();
                            var fragments = pass.progFragment?.m_SubPrograms ?? new List<SerializedSubProgram>();
                            var count = Math.Min(vertices.Count, fragments.Count);
                            for (var variantIndex = 0; variantIndex < count; variantIndex++)
                            {
                                var vertexBlobIndex = vertices[variantIndex].m_BlobIndex;
                                var fragmentBlobIndex = fragments[variantIndex].m_BlobIndex;
                                if (vertexBlobIndex >= program.m_SubPrograms.Length || fragmentBlobIndex >= program.m_SubPrograms.Length)
                                {
                                    skipped.Add(new JObject
                                    {
                                        ["subShader"] = subShaderIndex,
                                        ["pass"] = passIndex,
                                        ["variant"] = variantIndex,
                                        ["reason"] = "BlobIndex is outside the decoded platform program range"
                                    });
                                    continue;
                                }

                                if (!ShaderConverter.TryConvertHsrSourceVariant(
                                    shader,
                                    subShader,
                                    pass,
                                    variantIndex,
                                    program.m_SubPrograms[vertexBlobIndex],
                                    program.m_SubPrograms[fragmentBlobIndex],
                                    out var source,
                                    out var reason))
                                {
                                    skipped.Add(new JObject
                                    {
                                        ["subShader"] = subShaderIndex,
                                        ["pass"] = passIndex,
                                        ["variant"] = variantIndex,
                                        ["vertexBlobIndex"] = vertexBlobIndex,
                                        ["fragmentBlobIndex"] = fragmentBlobIndex,
                                        ["reason"] = reason
                                    });
                                    continue;
                                }

                                sourceCount++;
                                string filePath;
                                if (!sourceToPath.TryGetValue(source, out filePath))
                                {
                                    var fileName = $"{Sanitize(shader.Name ?? "Shader")}_SS{subShaderIndex + 1:00}_P{passIndex + 1:00}_V{variantIndex + 1:000}.shader";
                                    filePath = Path.Combine(directory, fileName);
                                    File.WriteAllText(filePath, source);
                                    sourceToPath[source] = filePath;
                                    files.Add(new JObject
                                    {
                                        ["path"] = filePath,
                                        ["subShader"] = subShaderIndex,
                                        ["pass"] = passIndex,
                                        ["variant"] = variantIndex,
                                        ["vertexBlobIndex"] = vertexBlobIndex,
                                        ["fragmentBlobIndex"] = fragmentBlobIndex
                                    });
                                }

                                variantMappings.Add(new JObject
                                {
                                    ["platform"] = shader.platforms[platformIndex].ToString(),
                                    ["subShader"] = subShaderIndex,
                                    ["pass"] = passIndex,
                                    ["variant"] = variantIndex,
                                    ["shaderPath"] = filePath,
                                    ["shaderRelativePath"] = Path.Combine("shader-source", Path.GetFileName(filePath)).Replace('\\', '/'),
                                    ["shaderLabName"] = ReadShaderLabName(filePath),
                                    ["vertex"] = ToVariantStage(shader, shader.platformInfos[platformIndex], vertices[variantIndex], program.m_SubPrograms[vertexBlobIndex]),
                                    ["fragment"] = ToVariantStage(shader, shader.platformInfos[platformIndex], fragments[variantIndex], program.m_SubPrograms[fragmentBlobIndex])
                                });
                            }
                        }
                    }
                }

                return new JObject
                {
                    ["status"] = files.Count == 0 ? "no_supported_variants" : "written",
                    ["format"] = "shaderlab_source",
                    ["directSourceImport"] = true,
                    ["directory"] = directory,
                    ["variantCount"] = sourceCount,
                    ["uniqueSourceCount"] = files.Count,
                    ["variantMappings"] = variantMappings,
                    ["skippedVariantCount"] = skipped.Count,
                    ["files"] = files,
                    ["skippedVariants"] = skipped
                };
            }
            catch (Exception error)
            {
                return new JObject
                {
                    ["status"] = "failed",
                    ["errorType"] = error.GetType().FullName,
                    ["message"] = error.Message
                };
            }
        }

        private static IEnumerable<(string Name, SerializedProgram Program)> EnumerateStages(SerializedPass pass)
        {
            yield return ("Vertex", pass.progVertex);
            yield return ("Fragment", pass.progFragment);
            yield return ("Geometry", pass.progGeometry);
            yield return ("Hull", pass.progHull);
            yield return ("Domain", pass.progDomain);
            yield return ("RayTracing", pass.progRayTracing);
        }

        private static JObject ToVariantStage(
            Shader shader,
            ShaderPlatformInfos platformInfo,
            SerializedSubProgram serialized,
            ShaderSubProgram decoded)
        {
            return new JObject
            {
                ["blobIndex"] = serialized?.m_BlobIndex ?? 0,
                ["gpuProgramType"] = serialized?.m_GpuProgramType.ToString() ?? string.Empty,
                ["decodedProgramType"] = decoded?.m_ProgramType.ToString() ?? string.Empty,
                ["hardwareTier"] = serialized?.m_ShaderHardwareTier ?? 0,
                    ["keywordIndices"] = new JObject
                    {
                        ["combined"] = new JArray(serialized?.m_KeywordIndices ?? Array.Empty<ushort>()),
                        ["global"] = new JArray(serialized?.m_GlobalKeywordIndices ?? Array.Empty<ushort>()),
                        ["local"] = new JArray(serialized?.m_LocalKeywordIndices ?? Array.Empty<ushort>())
                    },
                    ["keywordNames"] = new JArray((serialized?.m_KeywordIndices ?? Array.Empty<ushort>())
                        .Select(index => shader.m_ParsedForm?.m_KeywordNames != null && index < shader.m_ParsedForm.m_KeywordNames.Length
                            ? shader.m_ParsedForm.m_KeywordNames[index]
                            : string.Empty)
                        .Where(name => !string.IsNullOrWhiteSpace(name))),
                    ["infoRecord"] = ToInfoRecordSummary(platformInfo, serialized?.m_BlobIndex ?? uint.MaxValue)
            };
        }

        private static JObject ToInfoRecordSummary(ShaderPlatformInfos info, uint index)
        {
            if (info?.m_SubProgramInfoBlob == null ||
                info.m_SubProgramInfoOffsets == null ||
                info.m_SubProgramInfoLengths == null ||
                index >= info.m_SubProgramInfoOffsets.Length ||
                index >= info.m_SubProgramInfoLengths.Length)
            {
                return new JObject { ["status"] = "not_available" };
            }

            var offset = info.m_SubProgramInfoOffsets[index];
            var length = info.m_SubProgramInfoLengths[index];
            if ((ulong)offset > (ulong)info.m_SubProgramInfoBlob.Length ||
                (ulong)length > (ulong)info.m_SubProgramInfoBlob.Length - offset ||
                length > int.MaxValue)
            {
                return new JObject
                {
                    ["status"] = "invalid_range",
                    ["index"] = index,
                    ["offset"] = offset,
                    ["length"] = length
                };
            }

            var bytes = info.m_SubProgramInfoBlob.AsSpan((int)offset, (int)length);
            var strings = ExtractAsciiStringObjects(bytes);
            var leadingStrings = new JArray();
            foreach (var token in strings)
            {
                if (string.Equals((string)token["value"], "$Globals", StringComparison.Ordinal))
                    break;
                leadingStrings.Add(token);
            }
            return new JObject
            {
                ["status"] = "available",
                ["index"] = index,
                ["offset"] = offset,
                ["length"] = length,
                ["asciiStrings"] = strings,
                ["leadingStringsBeforeFirstGlobalBuffer"] = leadingStrings
            };
        }

        private static JArray ExtractAsciiStringObjects(ReadOnlySpan<byte> bytes)
        {
            var result = new JArray();
            var start = -1;
            for (var index = 0; index <= bytes.Length; index++)
            {
                var printable = index < bytes.Length && bytes[index] >= 32 && bytes[index] <= 126;
                if (printable && start < 0)
                    start = index;
                if (!printable && start >= 0)
                {
                    var length = index - start;
                    if (length >= 3)
                    {
                        result.Add(new JObject
                        {
                            ["offset"] = start,
                            ["length"] = length,
                            ["value"] = Encoding.ASCII.GetString(bytes.Slice(start, length))
                        });
                    }
                    start = -1;
                }
            }
            return result;
        }

        private static JObject ToProperty(SerializedProperty property) => new()
        {
            ["name"] = property.m_Name ?? string.Empty,
            ["description"] = property.m_Description ?? string.Empty,
            ["attributes"] = new JArray(property.m_Attributes ?? Array.Empty<string>()),
            ["type"] = property.m_Type.ToString(),
            ["flags"] = property.m_Flags.ToString(),
            ["defaultValue"] = new JArray(property.m_DefValue ?? Array.Empty<float>()),
            ["defaultTexture"] = new JObject
            {
                ["name"] = property.m_DefTexture?.m_DefaultName ?? string.Empty,
                ["dimension"] = property.m_DefTexture?.m_TexDim.ToString() ?? string.Empty
            }
        };

        private static JObject ToPlatformInfo(ShaderPlatformInfos info) => new()
        {
            ["subProgramInfoOffsets"] = new JArray(info.m_SubProgramInfoOffsets ?? Array.Empty<uint>()),
            ["subProgramInfoLengths"] = new JArray(info.m_SubProgramInfoLengths ?? Array.Empty<uint>()),
            ["subProgramInfoBlobBytes"] = info.m_SubProgramInfoBlob?.Length ?? 0,
            ["subProgramInfoBlobPrefixHex"] = info.m_SubProgramInfoBlob == null
                ? string.Empty
                : Convert.ToHexString(info.m_SubProgramInfoBlob.Take(256).ToArray()),
            ["compressDictionaryBytes"] = info.m_CompressDictionary?.Length ?? 0,
            ["subProgramCodeOffsets"] = new JArray(info.m_SubProgramCodeOffsets ?? Array.Empty<uint>()),
            ["subProgramCodeCompressedLengths"] = new JArray(info.m_SubProgramCodeCompressedLengths ?? Array.Empty<uint>()),
            ["subProgramCodeDecompressedLengths"] = new JArray(info.m_SubProgramCodeDecompressedLengths ?? Array.Empty<uint>()),
            ["compressMask"] = new JArray(info.m_CompressMask ?? Array.Empty<ulong>()),
            ["compressedCodeBlobBytes"] = info.m_CompressedCodeBlob?.Length ?? 0,
            ["compressedCodeBlobPrefixHex"] = info.m_CompressedCodeBlob == null
                ? string.Empty
                : Convert.ToHexString(info.m_CompressedCodeBlob.Take(32).ToArray()),
            ["infoRecordStringSamples"] = ExtractInfoRecordStringSamples(info),
            ["infoRecordHexSamples"] = ExtractInfoRecordHexSamples(info),
            ["codeDecodeSamples"] = new JArray(Enumerable.Range(0, Math.Min(8, info.m_SubProgramCodeOffsets?.Length ?? 0))
                .Select(index => TryDecodeCodeSample(info, index))),
            ["infoParseSample"] = TryParseInfoSample(info, 0)
        };

        private static JArray ExtractInfoRecordStringSamples(ShaderPlatformInfos info)
        {
            var result = new JArray();
            if (info.m_SubProgramInfoBlob == null ||
                info.m_SubProgramInfoOffsets == null ||
                info.m_SubProgramInfoLengths == null)
                return result;

            var count = Math.Min(3, Math.Min(info.m_SubProgramInfoOffsets.Length, info.m_SubProgramInfoLengths.Length));
            for (var index = 0; index < count; index++)
            {
                var offset = info.m_SubProgramInfoOffsets[index];
                var length = info.m_SubProgramInfoLengths[index];
                if ((ulong)offset > (ulong)info.m_SubProgramInfoBlob.Length ||
                    (ulong)length > (ulong)info.m_SubProgramInfoBlob.Length - offset ||
                    length > int.MaxValue)
                    continue;

                var bytes = info.m_SubProgramInfoBlob.AsSpan((int)offset, (int)length);
                var strings = new JArray();
                var current = new StringBuilder();
                foreach (var value in bytes)
                {
                    if (value >= 32 && value <= 126)
                    {
                        current.Append((char)value);
                    }
                    else
                    {
                        if (current.Length >= 3)
                            strings.Add(current.ToString());
                        current.Clear();
                    }
                }
                if (current.Length >= 3)
                    strings.Add(current.ToString());

                result.Add(new JObject
                {
                    ["recordIndex"] = index,
                    ["recordLength"] = length,
                    ["asciiStrings"] = strings
                });
            }
            return result;
        }

        private static JArray ExtractInfoRecordHexSamples(ShaderPlatformInfos info)
        {
            var result = new JArray();
            if (info.m_SubProgramInfoBlob == null ||
                info.m_SubProgramInfoOffsets == null ||
                info.m_SubProgramInfoLengths == null)
                return result;

            var count = Math.Min(3, Math.Min(info.m_SubProgramInfoOffsets.Length, info.m_SubProgramInfoLengths.Length));
            for (var index = 0; index < count; index++)
            {
                var offset = info.m_SubProgramInfoOffsets[index];
                var length = info.m_SubProgramInfoLengths[index];
                if ((ulong)offset > (ulong)info.m_SubProgramInfoBlob.Length ||
                    (ulong)length > (ulong)info.m_SubProgramInfoBlob.Length - offset ||
                    length > int.MaxValue)
                    continue;

                var bytes = info.m_SubProgramInfoBlob.AsSpan((int)offset, (int)length).ToArray();
                result.Add(new JObject
                {
                    ["recordIndex"] = index,
                    ["recordLength"] = length,
                    ["hex"] = Convert.ToHexString(bytes)
                });
            }
            return result;
        }

        private static JObject TryParseInfoSample(ShaderPlatformInfos info, int index)
        {
            if (info.m_SubProgramInfoBlob == null ||
                info.m_SubProgramInfoOffsets == null ||
                info.m_SubProgramInfoLengths == null ||
                index < 0 ||
                index >= info.m_SubProgramInfoOffsets.Length ||
                index >= info.m_SubProgramInfoLengths.Length)
            {
                return new JObject { ["status"] = "invalid_range" };
            }

            var offset = info.m_SubProgramInfoOffsets[index];
            var length = info.m_SubProgramInfoLengths[index];
            if ((ulong)offset > (ulong)info.m_SubProgramInfoBlob.Length ||
                (ulong)length > (ulong)info.m_SubProgramInfoBlob.Length - offset ||
                length > int.MaxValue)
            {
                return new JObject { ["status"] = "invalid_range" };
            }

            try
            {
                using var stream = new MemoryStream(info.m_SubProgramInfoBlob, (int)offset, (int)length, writable: false);
                using var reader = new EndianBinaryReader(stream, EndianType.LittleEndian);
                var subProgram = new ShaderSubProgram(reader, hasUpdatedGpuProgram: false);
                return new JObject
                {
                    ["status"] = "parsed",
                    ["recordLength"] = length,
                    ["bytesConsumed"] = reader.Position,
                    ["bytesRemaining"] = reader.Remaining,
                    ["programType"] = subProgram.m_ProgramType.ToString(),
                    ["programCodeBytes"] = subProgram.m_ProgramCode?.Length ?? 0,
                    ["programCodePrefixHex"] = subProgram.m_ProgramCode == null
                        ? string.Empty
                        : Convert.ToHexString(subProgram.m_ProgramCode.Take(32).ToArray())
                };
            }
            catch (Exception error)
            {
                return new JObject
                {
                    ["status"] = "failed",
                    ["errorType"] = error.GetType().FullName,
                    ["message"] = error.Message
                };
            }
        }

        private static JObject TryDecodeCodeSample(ShaderPlatformInfos info, int index)
        {
            var offset = info.m_SubProgramCodeOffsets[index];
            var compressedLength = info.m_SubProgramCodeCompressedLengths[index];
            var decompressedLength = info.m_SubProgramCodeDecompressedLengths[index];
            var result = new JObject
            {
                ["index"] = index,
                ["offset"] = offset,
                ["compressedLength"] = compressedLength,
                ["decompressedLength"] = decompressedLength
            };

            if (info.m_CompressedCodeBlob == null ||
                (ulong)offset > (ulong)info.m_CompressedCodeBlob.Length ||
                (ulong)compressedLength > (ulong)info.m_CompressedCodeBlob.Length - offset ||
                decompressedLength > int.MaxValue)
            {
                result["status"] = "invalid_range";
                return result;
            }

            try
            {
                var plain = new byte[(int)decompressedLength];
                var plainWritten = LZ4.Instance.Decompress(
                    info.m_CompressedCodeBlob.AsSpan((int)offset, (int)compressedLength),
                    plain.AsSpan());
                result["plainLz4Status"] = plainWritten == decompressedLength ? "ok" : "size_mismatch";
                result["plainLz4Written"] = plainWritten;
            }
            catch (Exception error)
            {
                result["plainLz4Status"] = "failed";
                result["plainLz4ErrorType"] = error.GetType().FullName;
                result["plainLz4Message"] = error.Message;
            }

            try
            {
                if (info.m_CompressDictionary == null || info.m_CompressDictionary.Length == 0)
                {
                    result["dictionaryLz4Status"] = "no_dictionary";
                    return result;
                }

                var withDictionary = new byte[info.m_CompressDictionary.Length + (int)decompressedLength];
                var dictionaryWritten = LZ4.Instance.Decompress(
                    info.m_CompressedCodeBlob.AsSpan((int)offset, (int)compressedLength),
                    withDictionary.AsSpan(),
                    info.m_CompressDictionary);
                result["dictionaryLz4Status"] = dictionaryWritten == decompressedLength ? "ok" : "size_mismatch";
                result["dictionaryLz4Written"] = dictionaryWritten;
                result["prefixHex"] = Convert.ToHexString(withDictionary
                    .AsSpan(info.m_CompressDictionary.Length, (int)Math.Min(decompressedLength, 32))
                    .ToArray());
                var program = withDictionary.AsSpan(info.m_CompressDictionary.Length, (int)decompressedLength);
                result["programPayloadPrefixHex"] = program.Length <= 38
                    ? string.Empty
                    : Convert.ToHexString(program[38..].Slice(0, Math.Min(64, program.Length - 38)).ToArray());
                result["programPayloadHasDxbcHeader"] = program.Length >= 42 &&
                    program[38] == (byte)'D' && program[39] == (byte)'X' &&
                    program[40] == (byte)'B' && program[41] == (byte)'C';
            }
            catch (Exception error)
            {
                result["dictionaryLz4Status"] = "failed";
                result["dictionaryLz4ErrorType"] = error.GetType().FullName;
                result["dictionaryLz4Message"] = error.Message;
            }

            return result;
        }

        private static JObject ToSubShader(SerializedSubShader subShader) => new()
        {
            ["lod"] = subShader.m_LOD,
            ["tags"] = ToTags(subShader.m_Tags),
            ["passes"] = new JArray(subShader.m_Passes?.Select(ToPass) ?? Enumerable.Empty<JObject>())
        };

        private static JObject ToPass(SerializedPass pass) => new()
        {
            ["name"] = pass.m_Name ?? string.Empty,
            ["useName"] = pass.m_UseName ?? string.Empty,
            ["textureName"] = pass.m_TextureName ?? string.Empty,
            ["type"] = pass.m_Type.ToString(),
            ["programMask"] = pass.m_ProgramMask,
            ["hasInstancingVariant"] = pass.m_HasInstancingVariant,
            ["nameIndices"] = new JObject(pass.m_NameIndices?.Select(x => new JProperty(x.Key, x.Value)) ?? Enumerable.Empty<JProperty>()),
            ["tags"] = ToTags(pass.m_Tags),
            ["state"] = ToState(pass.m_State),
            ["keywordIndices"] = new JObject
            {
                ["localMask"] = new JArray(pass.m_LocalKeywordMask ?? Array.Empty<ushort>()),
                ["globalMask"] = new JArray(pass.m_GlobalKeywordMask ?? Array.Empty<ushort>()),
                ["serializedStateMask"] = new JArray(pass.m_SerializedKeywordStateMask ?? Array.Empty<ushort>()),
                ["default"] = new JArray(pass.m_DefaultKeywords ?? Array.Empty<ushort>())
            },
            ["stages"] = new JObject
            {
                ["vertex"] = ToProgram(pass.progVertex),
                ["fragment"] = ToProgram(pass.progFragment),
                ["geometry"] = ToProgram(pass.progGeometry),
                ["hull"] = ToProgram(pass.progHull),
                ["domain"] = ToProgram(pass.progDomain),
                ["rayTracing"] = ToProgram(pass.progRayTracing)
            }
        };

        private static JObject ToState(SerializedShaderState state)
        {
            if (state == null)
                return null;
            return new JObject
            {
                ["name"] = state.m_Name ?? string.Empty,
                ["zTest"] = ToFloatValue(state.zTest),
                ["zWrite"] = ToFloatValue(state.zWrite),
                ["culling"] = ToFloatValue(state.culling),
                ["alphaToMask"] = ToFloatValue(state.alphaToMask),
                ["fogMode"] = state.fogMode.ToString(),
                ["lighting"] = state.lighting,
                ["tags"] = ToTags(state.m_Tags),
                ["blendTargets"] = new JArray(state.rtBlend?.Select(ToBlend) ?? Enumerable.Empty<JObject>())
            };
        }

        private static JObject ToBlend(SerializedShaderRTBlendState blend) => new()
        {
            ["srcBlend"] = ToFloatValue(blend.srcBlend),
            ["destBlend"] = ToFloatValue(blend.destBlend),
            ["srcBlendAlpha"] = ToFloatValue(blend.srcBlendAlpha),
            ["destBlendAlpha"] = ToFloatValue(blend.destBlendAlpha),
            ["blendOp"] = ToFloatValue(blend.blendOp),
            ["blendOpAlpha"] = ToFloatValue(blend.blendOpAlpha),
            ["colorMask"] = ToFloatValue(blend.colMask)
        };

        private static JObject ToProgram(SerializedProgram program)
        {
            if (program == null)
                return null;
            return new JObject
            {
                ["subProgramCount"] = program.m_SubPrograms?.Count ?? 0,
                ["subPrograms"] = new JArray(program.m_SubPrograms?.Select(ToSubProgram) ?? Enumerable.Empty<JObject>()),
                ["playerSubProgramGroups"] = program.m_PlayerSubPrograms?.Count ?? 0,
                ["parameterBlobIndices"] = new JArray(program.m_ParameterBlobIndices?.Select(x => new JArray(x)) ?? Enumerable.Empty<JArray>()),
                ["commonParameters"] = ToParameters(program.m_CommonParameters)
            };
        }

        private static JObject ToSubProgram(SerializedSubProgram subProgram) => new()
        {
            ["blobIndex"] = subProgram.m_BlobIndex,
            ["gpuProgramType"] = subProgram.m_GpuProgramType.ToString(),
            ["hardwareTier"] = subProgram.m_ShaderHardwareTier,
            ["keywordIndices"] = new JObject
            {
                ["combined"] = new JArray(subProgram.m_KeywordIndices ?? Array.Empty<ushort>()),
                ["global"] = new JArray(subProgram.m_GlobalKeywordIndices ?? Array.Empty<ushort>()),
                ["local"] = new JArray(subProgram.m_LocalKeywordIndices ?? Array.Empty<ushort>())
            },
            ["channels"] = subProgram.m_Channels == null ? null : new JObject
            {
                ["sourceMap"] = subProgram.m_Channels.m_SourceMap,
                ["count"] = subProgram.m_Channels.m_Channels?.Count ?? 0,
                ["bindings"] = new JArray(subProgram.m_Channels.m_Channels?.Select(c => new JObject
                {
                    ["source"] = c.source,
                    ["target"] = c.target
                }) ?? Enumerable.Empty<JObject>())
            },
            ["parameters"] = ToParameters(subProgram.m_Parameters) ?? ToLegacyParameters(subProgram)
        };

        private static JObject ToParameters(SerializedProgramParameters parameters)
        {
            if (parameters == null)
                return null;
            return new JObject
            {
                ["vectors"] = new JArray(parameters.m_VectorParams?.Select(ToVector) ?? Enumerable.Empty<JObject>()),
                ["matrices"] = new JArray(parameters.m_MatrixParams?.Select(ToMatrix) ?? Enumerable.Empty<JObject>()),
                ["textures"] = new JArray(parameters.m_TextureParams?.Select(ToTexture) ?? Enumerable.Empty<JObject>()),
                ["buffers"] = new JArray(parameters.m_BufferParams?.Select(ToBuffer) ?? Enumerable.Empty<JObject>()),
                ["constantBuffers"] = new JArray(parameters.m_ConstantBuffers?.Select(ToConstantBuffer) ?? Enumerable.Empty<JObject>()),
                ["constantBufferBindings"] = new JArray(parameters.m_ConstantBufferBindings?.Select(ToBuffer) ?? Enumerable.Empty<JObject>()),
                ["uavs"] = new JArray(parameters.m_UAVParams?.Select(x => new JObject { ["nameIndex"] = x.m_NameIndex, ["index"] = x.m_Index, ["originalIndex"] = x.m_OriginalIndex }) ?? Enumerable.Empty<JObject>()),
                ["samplers"] = new JArray(parameters.m_Samplers?.Select(x => new JObject { ["sampler"] = x.sampler, ["bindPoint"] = x.bindPoint }) ?? Enumerable.Empty<JObject>()),
                ["descriptorSets"] = new JArray(parameters.m_DescriptorSetParams?.Select(ToDescriptorSet) ?? Enumerable.Empty<JObject>())
            };
        }

        private static JObject ToLegacyParameters(SerializedSubProgram program) => new()
        {
            ["vectors"] = new JArray(program.m_VectorParams?.Select(ToVector) ?? Enumerable.Empty<JObject>()),
            ["matrices"] = new JArray(program.m_MatrixParams?.Select(ToMatrix) ?? Enumerable.Empty<JObject>()),
            ["textures"] = new JArray(program.m_TextureParams?.Select(ToTexture) ?? Enumerable.Empty<JObject>()),
            ["buffers"] = new JArray(program.m_BufferParams?.Select(ToBuffer) ?? Enumerable.Empty<JObject>()),
            ["constantBuffers"] = new JArray(program.m_ConstantBuffers?.Select(ToConstantBuffer) ?? Enumerable.Empty<JObject>()),
            ["constantBufferBindings"] = new JArray(program.m_ConstantBufferBindings?.Select(ToBuffer) ?? Enumerable.Empty<JObject>()),
            ["uavs"] = new JArray(program.m_UAVParams?.Select(x => new JObject { ["nameIndex"] = x.m_NameIndex, ["index"] = x.m_Index, ["originalIndex"] = x.m_OriginalIndex }) ?? Enumerable.Empty<JObject>()),
            ["samplers"] = new JArray(program.m_Samplers?.Select(x => new JObject { ["sampler"] = x.sampler, ["bindPoint"] = x.bindPoint }) ?? Enumerable.Empty<JObject>())
        };

        private static JObject ToVector(VectorParameter x) => new() { ["nameIndex"] = x.m_NameIndex, ["index"] = x.m_Index, ["arraySize"] = x.m_ArraySize, ["type"] = x.m_Type, ["dimension"] = x.m_Dim };
        private static JObject ToMatrix(MatrixParameter x) => new() { ["nameIndex"] = x.m_NameIndex, ["index"] = x.m_Index, ["arraySize"] = x.m_ArraySize, ["type"] = x.m_Type, ["rows"] = x.m_RowCount };
        private static JObject ToTexture(TextureParameter x) => new() { ["nameIndex"] = x.m_NameIndex, ["index"] = x.m_Index, ["registerSpace"] = x.m_RegisterSpace, ["bindCount"] = x.m_BindCount, ["samplerIndex"] = x.m_SamplerIndex, ["samplerSpace"] = x.m_SamplerSpace, ["dimension"] = x.m_Dim };
        private static JObject ToBuffer(BufferBinding x) => new() { ["nameIndex"] = x.m_NameIndex, ["index"] = x.m_Index, ["registerSpace"] = x.m_RegisterSpace, ["bindCount"] = x.m_BindCount, ["arraySize"] = x.m_ArraySize };
        private static JObject ToConstantBuffer(ConstantBuffer x) => new()
        {
            ["nameIndex"] = x.m_NameIndex,
            ["size"] = x.m_Size,
            ["isPartial"] = x.m_IsPartialCB,
            ["vectors"] = new JArray(x.m_VectorParams?.Select(ToVector) ?? Enumerable.Empty<JObject>()),
            ["matrices"] = new JArray(x.m_MatrixParams?.Select(ToMatrix) ?? Enumerable.Empty<JObject>()),
            ["structs"] = new JArray(x.m_StructParams?.Select(ToStruct) ?? Enumerable.Empty<JObject>())
        };

        private static JObject ToStruct(StructParameter x) => new()
        {
            ["vectors"] = new JArray(x.m_VectorParams?.Select(ToVector) ?? Enumerable.Empty<JObject>()),
            ["matrices"] = new JArray(x.m_MatrixParams?.Select(ToMatrix) ?? Enumerable.Empty<JObject>())
        };
        private static JObject ToDescriptorSet(DescriptorSetParam x) => new() { ["nameIndex"] = x.m_NameIndex, ["setId"] = x.m_SetId, ["maxBindingIndex"] = x.m_MaxBindingIndex, ["bindingCount"] = x.m_SetBindings?.Count ?? 0 };

        private static ISet<string> GetShaderProperties(
            Material material,
            IReadOnlyDictionary<string, HashSet<string>> shaderProperties)
        {
            if (material.m_Shader == null)
                return null;

            if (material.m_Shader.TryGet(out var shader) &&
                shaderProperties.TryGetValue(GetKey(shader), out var resolved))
                return resolved;

            var referenceKey = $"{material.m_Shader.SourceFileName}:{material.m_Shader.m_PathID}";
            return shaderProperties.TryGetValue(referenceKey, out resolved) ? resolved : null;
        }

        private static JObject ToMaterial(Material material, ISet<string> shaderProperties)
        {
            var textureNames = material.m_SavedProperties?.m_TexEnvs?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var floatNames = material.m_SavedProperties?.m_Floats?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var intNames = material.m_SavedProperties?.m_Ints?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var colorNames = material.m_SavedProperties?.m_Colors?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var result = new JObject
            {
                ["name"] = material.Name ?? string.Empty,
                ["pathId"] = material.m_PathID,
                ["source"] = ToObjectSource(material),
                ["shader"] = new JObject
                {
                    ["fileId"] = material.m_Shader?.m_FileID ?? 0,
                    ["pathId"] = material.m_Shader?.m_PathID ?? 0,
                    ["sourceFile"] = material.m_Shader?.SourceFileName ?? string.Empty,
                    ["resolvedName"] = material.m_Shader?.Name ?? string.Empty
                },
                ["shaderKeywords"] = material.m_ShaderKeywords ?? string.Empty,
                ["disabledShaderPasses"] = new JArray(material.m_DisabledShaderPasses ?? Array.Empty<string>()),
                ["textures"] = new JArray(material.m_SavedProperties?.m_TexEnvs?.Select(ToTexEnv) ?? Enumerable.Empty<JObject>()),
                ["floats"] = new JObject(material.m_SavedProperties?.m_Floats?.Select(x => new JProperty(x.Key, x.Value)) ?? Enumerable.Empty<JProperty>()),
                ["ints"] = new JObject(material.m_SavedProperties?.m_Ints?.Select(x => new JProperty(x.Key, x.Value)) ?? Enumerable.Empty<JProperty>()),
                ["colors"] = new JObject(material.m_SavedProperties?.m_Colors?.Select(x => new JProperty(x.Key, new JArray(x.Value.R, x.Value.G, x.Value.B, x.Value.A))) ?? Enumerable.Empty<JProperty>()),
                ["bindingEvidence"] = ToBindingEvidence(shaderProperties, textureNames, floatNames, intNames, colorNames)
            };
            return result;
        }

        private static JObject ToBindingEvidence(
            ISet<string> shaderProperties,
            IEnumerable<string> textureNames,
            IEnumerable<string> floatNames,
            IEnumerable<string> intNames,
            IEnumerable<string> colorNames)
        {
            var materialNames = textureNames
                .Concat(floatNames)
                .Concat(intNames)
                .Concat(colorNames)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var declared = shaderProperties == null
                ? Array.Empty<string>()
                : materialNames.Where(shaderProperties.Contains).ToArray();
            var undeclared = shaderProperties == null
                ? materialNames
                : materialNames.Where(name => !shaderProperties.Contains(name)).ToArray();
            return new JObject
            {
                ["status"] = shaderProperties == null ? "shader_not_resolved" : "exact_name_intersection",
                ["declaredMaterialProperties"] = new JArray(declared),
                ["undeclaredMaterialProperties"] = new JArray(undeclared)
            };
        }

        private static JObject ToTexEnv(KeyValuePair<string, UnityTexEnv> value) => new()
        {
            ["property"] = value.Key,
            ["texture"] = value.Value?.m_Texture == null ? null : new JObject
            {
                ["fileId"] = value.Value.m_Texture.m_FileID,
                ["pathId"] = value.Value.m_Texture.m_PathID,
                ["sourceFile"] = value.Value.m_Texture.SourceFileName,
                ["resolvedName"] = value.Value.m_Texture.Name
            },
            ["scale"] = new JArray(value.Value?.m_Scale.X ?? 0, value.Value?.m_Scale.Y ?? 0),
            ["offset"] = new JArray(value.Value?.m_Offset.X ?? 0, value.Value?.m_Offset.Y ?? 0)
        };

        private static JObject ToFloatValue(SerializedShaderFloatValue value) => value == null ? null : new JObject { ["value"] = value.val, ["name"] = value.name ?? string.Empty };
        private static JObject ToTags(SerializedTagMap tags) => tags == null ? null : new JObject(tags.tags?.Select(x => new JProperty(x.Key, x.Value)) ?? Enumerable.Empty<JProperty>());
        private static JObject ToObjectSource(Object obj) => new() { ["fileName"] = obj.assetsFile?.fileName ?? string.Empty, ["originalPath"] = obj.assetsFile?.originalPath ?? string.Empty };

        private static bool HasBindingRecords(SerializedShader shader)
        {
            return shader.m_SubShaders?.SelectMany(x => x.m_Passes ?? Enumerable.Empty<SerializedPass>())
                .SelectMany(GetStagePrograms)
                .Where(x => x != null)
                .SelectMany(x => x.m_SubPrograms ?? Enumerable.Empty<SerializedSubProgram>())
                .Any(HasBindingRecords) == true;
        }

        private static IEnumerable<SerializedProgram> GetStagePrograms(SerializedPass pass)
        {
            yield return pass.progVertex;
            yield return pass.progFragment;
            yield return pass.progGeometry;
            yield return pass.progHull;
            yield return pass.progDomain;
            yield return pass.progRayTracing;
        }

        private static bool HasBindingRecords(SerializedSubProgram program)
        {
            var parameters = program.m_Parameters;
            return (parameters?.m_VectorParams?.Count ?? program.m_VectorParams?.Count ?? 0) > 0 ||
                   (parameters?.m_MatrixParams?.Count ?? program.m_MatrixParams?.Count ?? 0) > 0 ||
                   (parameters?.m_TextureParams?.Count ?? program.m_TextureParams?.Count ?? 0) > 0 ||
                   (parameters?.m_BufferParams?.Count ?? program.m_BufferParams?.Count ?? 0) > 0 ||
                   (parameters?.m_ConstantBuffers?.Count ?? program.m_ConstantBuffers?.Count ?? 0) > 0 ||
                   (parameters?.m_ConstantBufferBindings?.Count ?? program.m_ConstantBufferBindings?.Count ?? 0) > 0 ||
                   (parameters?.m_UAVParams?.Count ?? program.m_UAVParams?.Count ?? 0) > 0 ||
                   (parameters?.m_Samplers?.Count ?? program.m_Samplers?.Count ?? 0) > 0;
        }

        private static bool IsReferenceToSelectedShader(Material material, HashSet<string> selectedKeys)
        {
            if (material.m_Shader == null || material.m_Shader.m_PathID == 0)
                return false;
            var key = $"{material.m_Shader.SourceFileName}:{material.m_Shader.m_PathID}";
            if (selectedKeys.Contains(key))
                return true;
            return material.m_Shader.TryGet(out var shader) && selectedKeys.Contains(GetKey(shader));
        }

        private static string GetKey(Object obj) => $"{obj.assetsFile?.fileName}:{obj.m_PathID}";

        private static HashSet<long> ParsePathIds(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var result = new HashSet<long>();
            foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(token, out var value))
                    result.Add(value);
            }
            return result.Count == 0 ? null : result;
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
