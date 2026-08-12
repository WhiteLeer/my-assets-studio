using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AnimeStudio;

public sealed class EffectPrefabManifest
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "dependency-manifest";
    public string SourceCAB { get; set; } = string.Empty;
    public string SourceBlock { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public long RootPathID { get; set; }
    public List<EffectPrefabNode> Nodes { get; set; } = new();
    public HashSet<string> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnsupportedComponents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EffectPrefabMaterial> Materials { get; set; } = new();
    public List<EffectPrefabMesh> Meshes { get; set; } = new();

    [JsonIgnore]
    private readonly Dictionary<string, byte[]> textureFiles = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    private readonly Dictionary<string, string> typeTreeFiles = new(StringComparer.OrdinalIgnoreCase);

    public static EffectPrefabManifest Build(GameObject root)
    {
        var manifest = new EffectPrefabManifest
        {
            Name = root.m_Name,
            SourceCAB = root.assetsFile.fileName,
            SourceBlock = root.assetsFile.originalPath ?? string.Empty,
            UnityVersion = root.assetsFile.unityVersion.Split('\r', '\n')[0],
            RootPathID = root.m_PathID,
        };
        if (root.m_Transform != null)
            AddNode(manifest, root.m_Transform, string.Empty);
        return manifest;
    }

    public IEnumerable<string> EnumerateSourceCABs()
    {
        if (!string.IsNullOrEmpty(SourceCAB))
            yield return SourceCAB;

        foreach (var component in Nodes.SelectMany(node => node.Components))
        {
            if (!string.IsNullOrEmpty(component.SourceCAB))
                yield return component.SourceCAB;
            foreach (var reference in component.References)
                if (!string.IsNullOrEmpty(reference.SourceCAB))
                    yield return reference.SourceCAB;
            if (component.MonoBehaviour?.ScriptPointer != null && !string.IsNullOrEmpty(component.MonoBehaviour.ScriptPointer.SourceCAB))
                yield return component.MonoBehaviour.ScriptPointer.SourceCAB;
            if (component.ParticleRenderer == null)
                continue;
            foreach (var pointer in component.ParticleRenderer.MaterialPointers.Concat(component.ParticleRenderer.MeshPointers))
                if (!string.IsNullOrEmpty(pointer.SourceCAB))
                    yield return pointer.SourceCAB;
        }
    }

    public void Write(string outputPath)
    {
        var componentFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in Nodes.SelectMany(node => node.Components))
        {
            if (component.TypeTreeJson == null)
                continue;

            var relativePath = Path.Combine("Components", $"{SanitizeFileName(component.Type)}_{component.PathID}.json");
            component.ParametersFile = relativePath.Replace('\\', '/');
            componentFiles[component.ParametersFile] = component.TypeTreeJson;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (string.Equals(Path.GetExtension(outputPath), ".srprefab", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
            WriteArchiveEntry(archive, "manifest.json", JsonConvert.SerializeObject(this, Formatting.Indented));
            foreach (var pair in componentFiles)
                WriteArchiveEntry(archive, pair.Key, pair.Value);
            foreach (var pair in typeTreeFiles)
                WriteArchiveEntry(archive, pair.Key, pair.Value);
            foreach (var pair in textureFiles)
                WriteArchiveEntry(archive, pair.Key, pair.Value);
            return;
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        foreach (var pair in componentFiles)
        {
            var componentPath = Path.Combine(outputDirectory, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(componentPath)!);
            File.WriteAllText(componentPath, pair.Value);
        }
        foreach (var pair in typeTreeFiles)
        {
            var typeTreePath = Path.Combine(outputDirectory, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(typeTreePath)!);
            File.WriteAllText(typeTreePath, pair.Value);
        }
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static void WriteArchiveEntry(ZipArchive archive, string path, string contents)
    {
        var entry = archive.CreateEntry(path.Replace('\\', '/'), CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static void WriteArchiveEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path.Replace('\\', '/'), CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(contents, 0, contents.Length);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');
        return value;
    }

    private static void AddNode(EffectPrefabManifest manifest, Transform transform, string parentPath)
    {
        if (!transform.m_GameObject.TryGet(out var gameObject))
            return;

        var path = string.IsNullOrEmpty(parentPath) ? gameObject.m_Name : $"{parentPath}/{gameObject.m_Name}";
        var node = new EffectPrefabNode { Name = gameObject.m_Name, Path = path, PathID = gameObject.m_PathID };
        foreach (var component in gameObject.m_Components)
        {
            if (!component.TryGet<Object>(out var obj))
                continue;
            var typeName = obj.type.ToString();
            var manifestComponent = new EffectPrefabComponent
            {
                Type = typeName,
                FileID = component.m_FileID,
                PathID = component.m_PathID,
                SourceCAB = component.SourceFileName,
            };
            node.Components.Add(manifestComponent);
            var handled = false;
            if (obj is Transform componentTransform)
            {
                AddTransformData(manifestComponent, componentTransform);
                handled = true;
            }
            else if (obj.type == ClassIDType.ParticleSystemRenderer)
            {
                handled = AddTypeTreeData(manifest, node, manifestComponent, obj);
                if (!handled)
                {
                    try
                    {
                        var particleRenderer = obj as ParticleSystemRenderer ?? new ParticleSystemRenderer(obj.reader);
                        AddParticleRendererDependencies(manifest, node, manifestComponent, particleRenderer);
                        handled = true;
                    }
                    catch (Exception exception)
                    {
                        manifestComponent.ParametersStatus = "renderer-parse-error";
                        manifestComponent.ParametersError = exception.Message;
                    }
                }
            }
            else if (obj.type == ClassIDType.MonoBehaviour)
            {
                AddMonoBehaviourInfo(manifest, node, manifestComponent, obj);
                handled = true;
            }
            else
            {
                handled = AddTypeTreeData(manifest, node, manifestComponent, obj);
            }

            if (!handled)
            {
                manifest.UnsupportedComponents.Add(typeName);
            }
        }

        if (gameObject.m_MeshRenderer != null)
            AddRendererDependencies(manifest, node, gameObject.m_MeshRenderer);
        if (gameObject.m_SkinnedMeshRenderer != null)
            AddRendererDependencies(manifest, node, gameObject.m_SkinnedMeshRenderer);
        if (gameObject.m_Animator?.m_Controller != null)
            AddDependency(manifest, node, "AnimatorController", gameObject.m_Animator.m_Controller.Name);
        if (gameObject.m_Animation != null)
            foreach (var clip in gameObject.m_Animation.m_Animations)
                AddDependency(manifest, node, "AnimationClip", clip.Name);

        manifest.Nodes.Add(node);
        foreach (var child in transform.m_Children)
            if (child.TryGet(out var childTransform))
                AddNode(manifest, childTransform, path);
    }

    private static void AddTransformData(EffectPrefabComponent component, Transform transform)
    {
        component.TypeTreeJson = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["LocalPosition"] = transform.m_LocalPosition,
            ["LocalRotation"] = transform.m_LocalRotation,
            ["LocalScale"] = transform.m_LocalScale,
        }, Formatting.Indented);
        component.ParametersStatus = "sr44-transform-parsed";
    }

    private static void AddRendererDependencies(EffectPrefabManifest manifest, EffectPrefabNode node, Renderer renderer)
    {
        foreach (var material in renderer.m_Materials)
        {
            AddDependency(manifest, node, "Material", material.Name);
            AddMaterial(manifest, material);
        }

        if (renderer is SkinnedMeshRenderer skinned && skinned.m_Mesh.TryGet(out var skinnedMesh))
        {
            AddDependency(manifest, node, "Mesh", skinnedMesh.m_Name);
            AddMesh(manifest, skinned.m_Mesh);
        }
        else if (renderer.m_GameObject.TryGet<GameObject>(out var gameObject) && gameObject.m_MeshFilter?.m_Mesh.TryGet(out var mesh) == true)
        {
            AddDependency(manifest, node, "Mesh", mesh.m_Name);
            AddMesh(manifest, gameObject.m_MeshFilter.m_Mesh);
        }
    }

    private static void AddParticleRendererDependencies(
        EffectPrefabManifest manifest,
        EffectPrefabNode node,
        EffectPrefabComponent component,
        ParticleSystemRenderer renderer)
    {
        component.ParticleRenderer = new EffectPrefabParticleRenderer
        {
            Enabled = renderer.m_Enabled,
            PrefixParsed = renderer.m_RendererPrefixParsed,
            RenderMode = renderer.m_RenderMode,
            SortMode = renderer.m_SortMode,
            MinParticleSize = renderer.m_MinParticleSize,
            MaxParticleSize = renderer.m_MaxParticleSize,
            CameraVelocityScale = renderer.m_CameraVelocityScale,
            VelocityScale = renderer.m_VelocityScale,
            LengthScale = renderer.m_LengthScale,
            SortingFudge = renderer.m_SortingFudge,
            NormalDirection = renderer.m_NormalDirection,
            ShadowBias = renderer.m_ShadowBias,
            RenderAlignment = renderer.m_RenderAlignment,
            Pivot = renderer.m_Pivot,
            Flip = renderer.m_Flip,
            UseCustomVertexStreams = renderer.m_UseCustomVertexStreams,
            VertexStreams = new List<int>(),
            EnableGPUInstancing = renderer.m_EnableGPUInstancing,
            ApplyActiveColorSpace = renderer.m_ApplyActiveColorSpace,
            AllowRoll = renderer.m_AllowRoll,
            BytesReadBeforeTail = renderer.m_BytesReadBeforeTail,
            UnparsedTailBytes = renderer.m_UnparsedTailBytes,
            MaterialPointers = renderer.m_Materials
                .Where(pointer => !pointer.IsNull)
                .Select(CreatePointerInfo)
                .ToList(),
            MeshPointers = renderer.m_Meshes
                .Where(pointer => !pointer.IsNull)
                .Select(CreatePointerInfo)
                .ToList(),
        };
        component.ParametersStatus = "partial-renderer-parse";
        foreach (var materialPointer in renderer.m_Materials.Where(pointer => !pointer.IsNull))
        {
            AddPointerDependency(manifest, node, "Material", materialPointer);
            AddMaterial(manifest, materialPointer);
        }
        foreach (var meshPointer in renderer.m_Meshes.Where(pointer => !pointer.IsNull))
        {
            AddPointerDependency(manifest, node, "Mesh", meshPointer);
            AddMesh(manifest, meshPointer);
        }
    }

    private static void AddMesh(EffectPrefabManifest manifest, PPtr<Mesh> pointer)
    {
        if (!pointer.TryGet(out var mesh) || mesh.m_VertexCount <= 0 || mesh.m_Vertices == null)
            return;
        var key = $"{mesh.assetsFile.fileName}:{mesh.m_PathID}";
        if (manifest.Meshes.Any(existing => existing.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return;

        var info = new EffectPrefabMesh
        {
            Key = key,
            SourceCAB = mesh.assetsFile.fileName,
            PathID = mesh.m_PathID,
            Name = mesh.m_Name,
            VertexCount = mesh.m_VertexCount,
            Vertices = mesh.m_Vertices,
            Normals = mesh.m_Normals,
            Tangents = mesh.m_Tangents,
            Colors = mesh.m_Colors,
            UV0 = mesh.m_UV0,
        };
        var indexOffset = 0;
        foreach (var subMesh in mesh.m_SubMeshes)
        {
            var indexCount = (int)subMesh.indexCount;
            info.SubMeshes.Add(new EffectPrefabSubMesh
            {
                Indices = mesh.m_Indices.Skip(indexOffset).Take(indexCount).ToArray(),
            });
            indexOffset += indexCount;
        }
        manifest.Meshes.Add(info);
    }

    private static void AddMaterial(EffectPrefabManifest manifest, PPtr<Material> pointer)
    {
        if (!pointer.TryGet(out var material))
            return;
        var key = $"{material.assetsFile.fileName}:{material.m_PathID}";
        if (manifest.Materials.Any(existing => existing.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return;

        var info = new EffectPrefabMaterial
        {
            Key = key,
            SourceCAB = material.assetsFile.fileName,
            PathID = material.m_PathID,
            Name = material.m_Name,
            ShaderSourceCAB = material.m_Shader.SourceFileName,
            ShaderPathID = material.m_Shader.m_PathID,
            ShaderName = ReadNamedObjectName(material.m_Shader),
            ShaderKeywords = material.m_ShaderKeywords,
            RenderQueue = material.m_CustomRenderQueue,
            EnableInstancing = material.m_EnableInstancingVariants,
            Tags = material.m_StringTagMap.Select(value => new EffectPrefabStringProperty { Name = value.Key, Value = value.Value }).ToList(),
            DisabledShaderPasses = material.m_DisabledShaderPasses,
            Floats = material.m_SavedProperties.m_Floats.Select(value => new EffectPrefabFloatProperty { Name = value.Key, Value = value.Value }).ToList(),
            Colors = material.m_SavedProperties.m_Colors.Select(value => new EffectPrefabColorProperty { Name = value.Key, Value = value.Value }).ToList(),
        };
        foreach (var textureProperty in material.m_SavedProperties.m_TexEnvs)
        {
            var texture = new EffectPrefabTextureProperty
            {
                Name = textureProperty.Key,
                Scale = textureProperty.Value.m_Scale,
                Offset = textureProperty.Value.m_Offset,
            };
            if (textureProperty.Value.m_Texture.TryGet(out var sourceTexture))
            {
                texture.TextureName = sourceTexture.Name;
                texture.SourceCAB = sourceTexture.assetsFile.fileName;
                texture.PathID = sourceTexture.m_PathID;
                if (sourceTexture is Texture2D texture2D)
                {
                    try
                    {
                        texture.PackageEntry = $"Textures/{SanitizeFileName(sourceTexture.assetsFile.fileName)}_{sourceTexture.m_PathID}_{SanitizeFileName(sourceTexture.Name)}.png";
                        if (!manifest.textureFiles.ContainsKey(texture.PackageEntry))
                        {
                            using var stream = texture2D.ConvertToStream(ImageFormat.Png, true);
                            manifest.textureFiles.Add(texture.PackageEntry, stream.ToArray());
                        }
                    }
                    catch (Exception exception)
                    {
                        texture.Error = exception.Message;
                    }
                }
            }
            info.Textures.Add(texture);
        }
        manifest.Materials.Add(info);
    }

    private static string ReadNamedObjectName<T>(PPtr<T> pointer) where T : Object
    {
        if (!pointer.TryGet<Object>(out var source))
            return string.Empty;
        try
        {
            return new NamedObjectReference(source.reader).m_Name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class NamedObjectReference : NamedObject
    {
        public NamedObjectReference(ObjectReader reader) : base(reader)
        {
        }
    }

    private static void AddPointerDependency<T>(EffectPrefabManifest manifest, EffectPrefabNode node, string kind, PPtr<T> pointer) where T : Object
    {
        if (pointer.IsNull)
            return;
        if (pointer.TryGet(out var target) && !string.IsNullOrEmpty(target.Name))
            AddDependency(manifest, node, kind, target.Name);
        else
            AddDependency(manifest, node, $"{kind}Ref", $"{pointer.SourceFileName}:{pointer.m_PathID}");
    }

    private static EffectPrefabPointer CreatePointerInfo<T>(PPtr<T> pointer) where T : Object
    {
        return new EffectPrefabPointer
        {
            FileID = pointer.m_FileID,
            PathID = pointer.m_PathID,
            SourceCAB = pointer.SourceFileName,
            Resolved = pointer.TryGet(out _),
        };
    }

    private static void AddMonoBehaviourInfo(
        EffectPrefabManifest manifest,
        EffectPrefabNode node,
        EffectPrefabComponent component,
        Object obj)
    {
        try
        {
            var behaviour = obj as MonoBehaviour ?? new MonoBehaviour(obj.reader);
            component.MonoBehaviour = new EffectPrefabMonoBehaviour
            {
                Name = behaviour.m_Name,
                ScriptPointer = CreatePointerInfo(behaviour.m_Script),
                ObjectByteSize = behaviour.byteSize,
                HeaderBytes = (int)(behaviour.reader.Position - behaviour.reader.byteStart),
                PayloadBytes = behaviour.reader.BytesLeft(),
                TypeHash = Convert.ToHexString(behaviour.serializedType.m_OldTypeHash),
            };

            var scriptPointer = new PPtr<Object>(behaviour.m_Script.m_FileID, behaviour.m_Script.m_PathID, behaviour.assetsFile);
            if (!scriptPointer.TryGet(out var scriptObject))
                return;
            var script = scriptObject as MonoScript ?? new MonoScript(scriptObject.reader);
            component.MonoBehaviour.ClassName = script.m_ClassName;
            component.MonoBehaviour.Namespace = script.m_Namespace;
            component.MonoBehaviour.AssemblyName = script.m_AssemblyName;
            AddDependency(manifest, node, "MonoScript", $"{script.m_AssemblyName}:{script.m_Namespace}.{script.m_ClassName}");
            if (Sr44MonoBehaviourParser.TryParse(behaviour, script.m_ClassName, out var data, out var complete, out var parseError))
            {
                component.TypeTreeJson = JsonConvert.SerializeObject(data, Formatting.Indented);
                component.ParametersStatus = complete ? "sr44-schema-parsed" : "sr44-schema-partial";
                component.ParametersError = parseError;
                CollectReferences(manifest, node, component, behaviour.assetsFile, data);
            }
            else
            {
                component.ParametersStatus = "mono-header-parsed";
                component.ParametersError = parseError;
            }
        }
        catch (Exception exception)
        {
            component.ParametersStatus = "mono-header-error";
            component.ParametersError = exception.Message;
        }
    }

    private static void AddDependency(EffectPrefabManifest manifest, EffectPrefabNode node, string kind, string name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        node.Dependencies.Add($"{kind}:{name}");
        manifest.Dependencies.Add($"{kind}:{name}");
    }

    private static bool AddTypeTreeData(EffectPrefabManifest manifest, EffectPrefabNode node, EffectPrefabComponent component, Object obj)
    {
        AddBundledTypeTreeSchema(manifest, component, obj);
        try
        {
            if (Sr44LightParser.TryParse(obj, out var lightData, out var lightError))
            {
                component.TypeTreeJson = JsonConvert.SerializeObject(lightData, Formatting.Indented);
                component.ParametersStatus = "sr44-light-partial";
                component.ParametersError = lightError;
                CollectReferences(manifest, node, component, obj.assetsFile, lightData);
                return true;
            }
            if (obj.type == ClassIDType.ParticleSystem)
            {
                // A supplied SR dump is more authoritative than the guarded
                // prefix parser. Use it first when the serialized file had no
                // embedded tree; otherwise the heuristic output can overwrite
                // valid module data with a partial object.
                if (obj.serializedType?.m_IsExternalTypeTree == true)
                {
                    var externalParticleData = ReadTypeTreeExactly(obj, component, "External ParticleSystem TypeTree");
                    if (externalParticleData != null)
                    {
                        NormalizeParticleSystemData(externalParticleData);
                        component.TypeTreeJson = JsonConvert.SerializeObject(externalParticleData, Formatting.Indented);
                        component.ParametersStatus = "external-type-tree-exported";
                        CollectReferences(manifest, node, component, obj.assetsFile, externalParticleData);
                        return true;
                    }
                }

                // SR 4.4's ParticleSystem layout is close enough to the stock
                // Unity TypeTree to decode without throwing, but the generic
                // reader becomes misaligned in InitialModule and silently
                // produces garbage values. Prefer the guarded SR parser before
                // falling back to the generic TypeTree reader.
                var srParticleParsed = Sr44ParticleSystemParser.TryParse(obj, out var srParticleData, out var srParticleError);
                if (Environment.GetEnvironmentVariable("SR_PARTICLE_GENERIC") != "1" && srParticleParsed)
                {
                    NormalizeParticleSystemData(srParticleData);
                    component.TypeTreeJson = JsonConvert.SerializeObject(srParticleData, Formatting.Indented);
                    component.ParametersStatus = "sr44-particle-partial";
                    component.ParametersError = srParticleError;
                    CollectReferences(manifest, node, component, obj.assetsFile, srParticleData);
                    return true;
                }
                if (!srParticleParsed)
                    Logger.Info($"SR ParticleSystem parser skipped object {obj.m_PathID}: {srParticleError}");

                try
                {
                    var particleTypeData = ReadTypeTreeExactly(obj, component, "Bundled ParticleSystem TypeTree");
                    if (particleTypeData != null)
                    {
                        NormalizeParticleSystemData(particleTypeData);
                        component.TypeTreeJson = JsonConvert.SerializeObject(particleTypeData, Formatting.Indented);
                        component.ParametersStatus = "type-tree-exported";
                        CollectReferences(manifest, node, component, obj.assetsFile, particleTypeData);
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    component.ParametersError = $"Bundled type tree failed: {exception.Message}";
                }

                // The SR parser was attempted above; retain the generic
                // TypeTree result only when its guarded parse also fails.
            }
            var typeData = ReadTypeTreePreservingTail(obj, component, "TypeTree", out var complete);
            if (typeData == null)
            {
                component.ParametersStatus = "type-tree-unavailable";
                return false;
            }

            component.TypeTreeJson = JsonConvert.SerializeObject(typeData, Formatting.Indented);
            component.ParametersStatus = obj.serializedType?.m_IsExternalTypeTree == true
                ? (complete ? "external-type-tree-exported" : "external-type-tree-partial")
                : (complete ? "exported" : "type-tree-partial");
            CollectReferences(manifest, node, component, obj.assetsFile, typeData);
            if (obj.type == ClassIDType.ParticleSystemRenderer)
                AddParticleRendererFromTypeTree(manifest, node, component, obj.assetsFile, typeData);
            return true;
        }
        catch (Exception exception)
        {
            component.ParametersStatus = "type-tree-error";
            component.ParametersError = exception.Message;
            return false;
        }
    }

    private static OrderedDictionary ReadTypeTreeExactly(Object obj, EffectPrefabComponent component, string source)
    {
        obj.reader.Reset();
        var typeData = obj.ToType();
        var bytesRead = checked((int)(obj.reader.Position - obj.reader.byteStart));
        component.ObjectByteSize = obj.byteSize;
        component.ParsedBytes = bytesRead;
        component.RemainingBytes = checked((int)obj.byteSize - bytesRead);

        if (typeData != null && bytesRead != obj.byteSize)
        {
            throw new InvalidDataException(
                $"{source} consumed {bytesRead} of {obj.byteSize} bytes " +
                $"({component.RemainingBytes} bytes remain). The schema does not match this object.");
        }

        return typeData;
    }

    private static OrderedDictionary ReadTypeTreePreservingTail(
        Object obj,
        EffectPrefabComponent component,
        string source,
        out bool complete)
    {
        obj.reader.Reset();
        var typeData = obj.ToType();
        var bytesRead = checked((int)(obj.reader.Position - obj.reader.byteStart));
        component.ObjectByteSize = obj.byteSize;
        component.ParsedBytes = bytesRead;
        component.RemainingBytes = checked((int)obj.byteSize - bytesRead);
        complete = component.RemainingBytes == 0;

        if (typeData == null || component.RemainingBytes < 0)
            return null;

        if (!complete)
        {
            typeData["UnmappedTailBytes"] = component.RemainingBytes;
            typeData["UnmappedTailHex"] = Convert.ToHexString(obj.reader.ReadBytes(component.RemainingBytes));
            component.ParametersError =
                $"{source} consumed {bytesRead} of {obj.byteSize} bytes; preserved {component.RemainingBytes} unmapped bytes.";
        }

        return typeData;
    }

    private static void AddParticleRendererFromTypeTree(
        EffectPrefabManifest manifest,
        EffectPrefabNode node,
        EffectPrefabComponent component,
        SerializedFile sourceFile,
        IDictionary data)
    {
        var materials = ReadPointerArray<Material>(data["m_Materials"], sourceFile).ToList();
        var meshes = new[] { "m_Mesh", "m_Mesh1", "m_Mesh2", "m_Mesh3" }
            .Where(data.Contains)
            .Select(name => ReadPointer<Mesh>(data[name], sourceFile))
            .Where(pointer => pointer != null && !pointer.IsNull)
            .ToList();

        component.ParticleRenderer = new EffectPrefabParticleRenderer
        {
            Enabled = ReadBoolean(data, "m_Enabled"),
            PrefixParsed = true,
            RenderMode = ReadInt32(data, "m_RenderMode"),
            SortMode = ReadInt32(data, "m_SortMode"),
            MinParticleSize = ReadSingle(data, "m_MinParticleSize"),
            MaxParticleSize = ReadSingle(data, "m_MaxParticleSize"),
            CameraVelocityScale = ReadSingle(data, "m_CameraVelocityScale"),
            VelocityScale = ReadSingle(data, "m_VelocityScale"),
            LengthScale = ReadSingle(data, "m_LengthScale"),
            SortingFudge = ReadSingle(data, "m_SortingFudge"),
            NormalDirection = ReadSingle(data, "m_NormalDirection"),
            ShadowBias = ReadSingle(data, "m_ShadowBias"),
            RenderAlignment = ReadInt32(data, "m_RenderAlignment"),
            Pivot = ReadVector3(data, "m_Pivot"),
            Flip = ReadVector3(data, "m_Flip"),
            UseCustomVertexStreams = ReadBoolean(data, "m_UseCustomVertexStreams"),
            VertexStreams = ReadIntList(data, "m_VertexStreams").ToList(),
            EnableGPUInstancing = ReadBoolean(data, "m_EnableGPUInstancing"),
            ApplyActiveColorSpace = ReadBoolean(data, "m_ApplyActiveColorSpace"),
            AllowRoll = ReadBoolean(data, "m_AllowRoll"),
            BytesReadBeforeTail = checked((int)component.ObjectByteSize),
            UnparsedTailBytes = 0,
            MaterialPointers = materials.Select(CreatePointerInfo).ToList(),
            MeshPointers = meshes.Select(CreatePointerInfo).ToList(),
        };

        foreach (var material in materials)
        {
            AddPointerDependency(manifest, node, "Material", material);
            AddMaterial(manifest, material);
        }
        foreach (var mesh in meshes)
        {
            AddPointerDependency(manifest, node, "Mesh", mesh);
            AddMesh(manifest, mesh);
        }
    }

    private static IEnumerable<PPtr<T>> ReadPointerArray<T>(object value, SerializedFile sourceFile) where T : Object
    {
        if (value is not IEnumerable values || value is string || value is byte[])
            yield break;

        foreach (var item in values)
        {
            var pointer = ReadPointer<T>(item, sourceFile);
            if (pointer != null && !pointer.IsNull)
                yield return pointer;
        }
    }

    private static PPtr<T> ReadPointer<T>(object value, SerializedFile sourceFile) where T : Object
    {
        return value is IDictionary dictionary && TryReadPPtr(dictionary, out var fileID, out var pathID)
            ? new PPtr<T>(fileID, pathID, sourceFile)
            : null;
    }

    private static int ReadInt32(IDictionary data, string name) =>
        data.Contains(name) ? Convert.ToInt32(data[name]) : 0;

    private static float ReadSingle(IDictionary data, string name) =>
        data.Contains(name) ? Convert.ToSingle(data[name]) : 0f;

    private static bool ReadBoolean(IDictionary data, string name) =>
        data.Contains(name) && Convert.ToBoolean(data[name]);

    private static IEnumerable<int> ReadIntList(IDictionary data, string name)
    {
        if (!data.Contains(name) || data[name] is not IEnumerable values || data[name] is string || data[name] is byte[])
            return Array.Empty<int>();

        var result = new List<int>();
        foreach (var value in values)
        {
            if (value == null)
                continue;
            try
            {
                result.Add(Convert.ToInt32(value));
            }
            catch
            {
                // Ignore malformed stream entries instead of invalidating the prefab.
            }
        }
        return result;
    }

    // The serialized SR tree uses lower-case Unity field names. The Unity-side
    // importer intentionally consumes a small, stable PascalCase projection so
    // it does not depend on JsonUtility's handling of arbitrary dictionaries.
    private static void NormalizeParticleSystemData(IDictionary data)
    {
        if (data == null)
            return;

        if (TryGetDictionary(data, "InitialModule", out var initial))
        {
            CopyNormalizedCurve(initial, data, "startSize", "StartSize");
            CopyNormalizedCurve(initial, data, "startSizeX", "StartSizeX");
            CopyNormalizedCurve(initial, data, "startSizeY", "StartSizeY");
            CopyNormalizedCurve(initial, data, "startSizeZ", "StartSizeZ");
            CopyNormalizedCurve(initial, data, "gravityModifier", "GravityModifier");
            CopyNormalizedGradient(initial, data, "startColor", "StartColor");
            CopyScalar(initial, data, "maxNumParticles", "MaxNumParticles");
            CopyBoolean(initial, data, "size3D", "Size3D");
            CopyBoolean(initial, data, "rotation3D", "Rotation3D");
        }

        if (TryGetDictionary(data, "Modules", out var modules))
        {
            CopyNormalizedCurve(modules, data, "startSize", "StartSize");
            CopyNormalizedCurve(modules, data, "startSizeX", "StartSizeX");
            CopyNormalizedCurve(modules, data, "startSizeY", "StartSizeY");
            CopyNormalizedCurve(modules, data, "startSizeZ", "StartSizeZ");
            CopyNormalizedGradient(modules, data, "startColor", "StartColor");
            if (modules.Contains("startSizeY") || modules.Contains("startSizeZ"))
                data["Size3D"] = true;

            if (modules["ColorModule"] is IDictionary rawColorModule)
            {
                var colorModule = UnwrapModuleValue(rawColorModule, "ColorModule");
                CopyBoolean(colorModule, data, "enabled", "ColorOverLifetimeEnabled");
                if (colorModule["gradient"] is IDictionary rawGradient)
                {
                    var gradient = UnwrapModuleValue(rawGradient, "gradient");
                    var normalized = new Dictionary<string, object>();
                    CopyValue(gradient, normalized, "minMaxState", "MinMaxState");
                    CopyColor(gradient, normalized, "minColor", "MinColor");
                    CopyColor(gradient, normalized, "maxColor", "MaxColor");
                    CopyNormalizedSerializedGradient(gradient, normalized, "maxGradient", "MaxGradient");
                    CopyNormalizedSerializedGradient(gradient, normalized, "minGradient", "MinGradient");
                    if (normalized.Count > 0)
                        data["ColorOverLifetime"] = normalized;
                }
            }
        }
    }

    private static void CopyNormalizedCurve(IDictionary source, IDictionary target, string sourceName, string targetName)
    {
        if (!source.Contains(sourceName) || source[sourceName] is not IDictionary curve)
            return;

        curve = UnwrapModuleValue(curve, sourceName);

        var normalized = new Dictionary<string, object>();
        CopyValue(curve, normalized, "minMaxState", "MinMaxState");
        CopyValue(curve, normalized, "scalar", "Scalar");
        CopyValue(curve, normalized, "minScalar", "MinScalar");
        CopyNormalizedAnimationCurve(curve, normalized, "maxCurve", "MaxCurve");
        CopyNormalizedAnimationCurve(curve, normalized, "minCurve", "MinCurve");
        if (normalized.Count > 0)
            target[targetName] = normalized;
    }

    private static void CopyNormalizedAnimationCurve(
        IDictionary source,
        IDictionary target,
        string sourceName,
        string targetName)
    {
        if (!source.Contains(sourceName) || source[sourceName] is not IDictionary curve)
            return;

        var normalized = new Dictionary<string, object>();
        var keys = curve.Contains("Keys") ? curve["Keys"] : curve.Contains("m_Curve") ? curve["m_Curve"] : null;
        if (keys is IEnumerable keyValues && keys is not string && keys is not byte[])
        {
            var normalizedKeys = new List<Dictionary<string, object>>();
            foreach (var keyValue in keyValues)
            {
                if (keyValue is not IDictionary key)
                    continue;
                var normalizedKey = new Dictionary<string, object>();
                CopyValue(key, normalizedKey, "Time", "Time");
                CopyValue(key, normalizedKey, "time", "Time");
                CopyValue(key, normalizedKey, "Value", "Value");
                CopyValue(key, normalizedKey, "value", "Value");
                CopyValue(key, normalizedKey, "InSlope", "InSlope");
                CopyValue(key, normalizedKey, "inSlope", "InSlope");
                CopyValue(key, normalizedKey, "OutSlope", "OutSlope");
                CopyValue(key, normalizedKey, "outSlope", "OutSlope");
                CopyValue(key, normalizedKey, "WeightedMode", "WeightedMode");
                CopyValue(key, normalizedKey, "weightedMode", "WeightedMode");
                CopyValue(key, normalizedKey, "InWeight", "InWeight");
                CopyValue(key, normalizedKey, "inWeight", "InWeight");
                CopyValue(key, normalizedKey, "OutWeight", "OutWeight");
                CopyValue(key, normalizedKey, "outWeight", "OutWeight");
                if (normalizedKey.Count > 0)
                    normalizedKeys.Add(normalizedKey);
            }
            normalized["Keys"] = normalizedKeys;
        }

        CopyValue(curve, normalized, "PreInfinity", "PreInfinity");
        CopyValue(curve, normalized, "m_PreInfinity", "PreInfinity");
        CopyValue(curve, normalized, "PostInfinity", "PostInfinity");
        CopyValue(curve, normalized, "m_PostInfinity", "PostInfinity");
        if (normalized.Count > 0)
            target[targetName] = normalized;
    }

    private static void CopyNormalizedGradient(IDictionary source, IDictionary target, string sourceName, string targetName)
    {
        if (!source.Contains(sourceName) || source[sourceName] is not IDictionary gradient)
            return;

        gradient = UnwrapModuleValue(gradient, sourceName);

        var normalized = new Dictionary<string, object>();
        CopyValue(gradient, normalized, "minMaxState", "MinMaxState");
        CopyColor(gradient, normalized, "minColor", "MinColor");
        CopyColor(gradient, normalized, "maxColor", "MaxColor");
        CopyNormalizedSerializedGradient(gradient, normalized, "maxGradient", "MaxGradient");
        CopyNormalizedSerializedGradient(gradient, normalized, "minGradient", "MinGradient");
        if (normalized.Count > 0)
            target[targetName] = normalized;
    }

    private static void CopyNormalizedSerializedGradient(
        IDictionary source,
        IDictionary target,
        string sourceName,
        string targetName)
    {
        if (!source.Contains(sourceName) || source[sourceName] is not IDictionary gradient)
            return;

        var normalized = new Dictionary<string, object>();
        CopyValue(gradient, normalized, "m_Mode", "Mode");
        var colorKeys = new List<Dictionary<string, object>>();
        var alphaKeys = new List<Dictionary<string, object>>();
        var colorCount = ReadBoundedCount(gradient, "m_NumColorKeys");
        var alphaCount = ReadBoundedCount(gradient, "m_NumAlphaKeys");
        var keyCount = Math.Max(colorCount, alphaCount);
        for (var index = 0; index < keyCount; index++)
        {
            if (gradient[$"key{index}"] is not IDictionary key)
                continue;

            if (index < colorCount)
            {
                var color = new Dictionary<string, object>();
                CopyValue(key, color, "r", "r");
                CopyValue(key, color, "g", "g");
                CopyValue(key, color, "b", "b");
                CopyValue(key, color, "a", "a");
                var colorKey = new Dictionary<string, object>
                {
                    ["Color"] = color,
                    ["Time"] = ReadGradientTime(gradient, "ctime", index),
                };
                colorKeys.Add(colorKey);
            }

            if (index < alphaCount)
            {
                var alphaKey = new Dictionary<string, object>
                {
                    ["Alpha"] = ReadColorChannel(key, "a"),
                    ["Time"] = ReadGradientTime(gradient, "atime", index),
                };
                alphaKeys.Add(alphaKey);
            }
        }

        if (colorKeys.Count > 0)
            normalized["ColorKeys"] = colorKeys;
        if (alphaKeys.Count > 0)
            normalized["AlphaKeys"] = alphaKeys;
        if (normalized.Count > 0)
            target[targetName] = normalized;
    }

    private static int ReadBoundedCount(IDictionary source, string name)
    {
        try
        {
            return Math.Clamp(Convert.ToInt32(source[name]), 0, 8);
        }
        catch
        {
            return 0;
        }
    }

    private static float ReadGradientTime(IDictionary source, string prefix, int index)
    {
        try
        {
            return Math.Clamp(Convert.ToSingle(source[$"{prefix}{index}"]) / 65535f, 0f, 1f);
        }
        catch
        {
            return index == 0 ? 0f : 1f;
        }
    }

    private static object ReadColorChannel(IDictionary source, string name)
    {
        if (!source.Contains(name))
            return 0f;
        try
        {
            return Convert.ToSingle(source[name]);
        }
        catch
        {
            return 0f;
        }
    }

    private static void CopyScalar(IDictionary source, IDictionary target, string sourceName, string targetName)
    {
        if (source.Contains(sourceName))
            target[targetName] = source[sourceName];
    }

    private static void CopyBoolean(IDictionary source, IDictionary target, string sourceName, string targetName)
    {
        if (!source.Contains(sourceName))
            return;
        try
        {
            target[targetName] = Convert.ToBoolean(source[sourceName]);
        }
        catch
        {
        }
    }

    private static void CopyValue(IDictionary source, IDictionary target, string sourceName, string targetName)
    {
        if (source.Contains(sourceName))
            target[targetName] = source[sourceName];
    }

    private static void CopyColor(IDictionary source, IDictionary target, string sourceName, string targetName)
    {
        if (!source.Contains(sourceName) || source[sourceName] is not IDictionary color)
            return;

        var normalized = new Dictionary<string, object>();
        CopyValue(color, normalized, "r", "r");
        CopyValue(color, normalized, "g", "g");
        CopyValue(color, normalized, "b", "b");
        CopyValue(color, normalized, "a", "a");
        if (normalized.Count > 0)
            target[targetName] = normalized;
    }

    private static IDictionary UnwrapModuleValue(IDictionary value, string name)
    {
        if (value.Count == 1 && value.Contains(name) && value[name] is IDictionary nested)
            return nested;
        return value;
    }

    private static bool TryGetDictionary(IDictionary source, string name, out IDictionary value)
    {
        value = null;
        if (!source.Contains(name) || source[name] is not IDictionary dictionary)
            return false;
        value = dictionary;
        return true;
    }

    private static Vector3 ReadVector3(IDictionary data, string name)
    {
        if (!data.Contains(name) || data[name] is not IDictionary vector)
            return new Vector3();
        return new Vector3(ReadSingle(vector, "x"), ReadSingle(vector, "y"), ReadSingle(vector, "z"));
    }

    private static void AddBundledTypeTreeSchema(
        EffectPrefabManifest manifest,
        EffectPrefabComponent component,
        Object obj)
    {
        var serializedType = obj.serializedType;
        if (serializedType?.m_IsExternalTypeTree == true)
        {
            component.TypeTreeSource = "external";
            component.TypeTreeHash = serializedType.m_OldTypeHash == null
                ? string.Empty
                : Convert.ToHexString(serializedType.m_OldTypeHash);
            return;
        }

        var nodes = serializedType?.m_Type?.m_Nodes;
        if (nodes == null || nodes.Count == 0)
        {
            component.TypeTreeSource = "unavailable";
            return;
        }

        var typeHash = serializedType.m_OldTypeHash == null
            ? "NO_HASH"
            : Convert.ToHexString(serializedType.m_OldTypeHash);
        var relativePath = $"TypeTrees/{SanitizeFileName(component.Type)}_{typeHash}.json";
        component.TypeTreeSource = "bundled";
        component.TypeTreeHash = typeHash;
        component.TypeTreeSchemaFile = relativePath;
        if (manifest.typeTreeFiles.ContainsKey(relativePath))
            return;

        var schema = new
        {
            GameVersion = "SR 4.4",
            UnityVersion = obj.assetsFile.unityVersion.Split('\r', '\n')[0],
            SourceCAB = obj.assetsFile.fileName,
            ClassID = serializedType.classID,
            TypeHash = typeHash,
            NodeCount = nodes.Count,
            Nodes = nodes.Select((typeNode, index) => new
            {
                Order = index,
                Level = typeNode.m_Level,
                Type = typeNode.m_Type,
                Name = typeNode.m_Name,
                ByteSize = typeNode.m_ByteSize,
                Index = typeNode.m_Index,
                TypeFlags = typeNode.m_TypeFlags,
                Version = typeNode.m_Version,
                MetaFlag = typeNode.m_MetaFlag,
                RefTypeHash = typeNode.m_RefTypeHash,
            }).ToArray(),
        };
        manifest.typeTreeFiles[relativePath] = JsonConvert.SerializeObject(schema, Formatting.Indented);
    }

    private static void CollectReferences(
        EffectPrefabManifest manifest,
        EffectPrefabNode node,
        EffectPrefabComponent component,
        SerializedFile sourceFile,
        object value)
    {
        if (value is IDictionary dictionary)
        {
            if (TryReadPPtr(dictionary, out var fileID, out var pathID) && pathID != 0)
            {
                var pointer = new PPtr<Object>(fileID, pathID, sourceFile);
                var reference = new EffectPrefabReference
                {
                    FileID = fileID,
                    PathID = pathID,
                    SourceCAB = pointer.SourceFileName,
                };
                if (pointer.TryGet(out var target))
                {
                    reference.Type = target.type.ToString();
                    reference.Name = target.Name;
                    AddDependency(manifest, node, reference.Type, string.IsNullOrEmpty(reference.Name) ? pathID.ToString() : reference.Name);
                    if (target is Material)
                        AddMaterial(manifest, new PPtr<Material>(fileID, pathID, sourceFile));
                    else if (target is Mesh)
                        AddMesh(manifest, new PPtr<Mesh>(fileID, pathID, sourceFile));
                }
                else
                {
                    reference.Status = "unresolved";
                }
                component.References.Add(reference);
                return;
            }

            foreach (DictionaryEntry entry in dictionary)
                CollectReferences(manifest, node, component, sourceFile, entry.Value);
            return;
        }

        if (value is IEnumerable enumerable && value is not string && value is not byte[])
            foreach (var item in enumerable)
                CollectReferences(manifest, node, component, sourceFile, item);
    }

    private static bool TryReadPPtr(IDictionary dictionary, out int fileID, out long pathID)
    {
        fileID = 0;
        pathID = 0;
        if (!dictionary.Contains("m_FileID") || !dictionary.Contains("m_PathID"))
            return false;

        try
        {
            fileID = Convert.ToInt32(dictionary["m_FileID"]);
            pathID = Convert.ToInt64(dictionary["m_PathID"]);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class EffectPrefabNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long PathID { get; set; }
    public List<EffectPrefabComponent> Components { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
}

public sealed class EffectPrefabComponent
{
    public string Type { get; set; } = string.Empty;
    public int FileID { get; set; }
    public long PathID { get; set; }
    public string SourceCAB { get; set; } = string.Empty;
    public string ParametersStatus { get; set; } = "not-required";
    public string ParametersFile { get; set; } = string.Empty;
    public string ParametersError { get; set; } = string.Empty;
    public uint ObjectByteSize { get; set; }
    public int ParsedBytes { get; set; }
    public int RemainingBytes { get; set; }
    public string TypeTreeSource { get; set; } = string.Empty;
    public string TypeTreeHash { get; set; } = string.Empty;
    public string TypeTreeSchemaFile { get; set; } = string.Empty;
    public List<EffectPrefabReference> References { get; set; } = new();
    public EffectPrefabParticleRenderer ParticleRenderer { get; set; }
    public EffectPrefabMonoBehaviour MonoBehaviour { get; set; }
    [JsonIgnore]
    public string TypeTreeJson { get; set; }
}

public sealed class EffectPrefabMonoBehaviour
{
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public uint ObjectByteSize { get; set; }
    public int HeaderBytes { get; set; }
    public int PayloadBytes { get; set; }
    public string TypeHash { get; set; } = string.Empty;
    public EffectPrefabPointer ScriptPointer { get; set; }
}

public sealed class EffectPrefabParticleRenderer
{
    public bool Enabled { get; set; }
    public bool PrefixParsed { get; set; }
    public int RenderMode { get; set; }
    public int SortMode { get; set; }
    public float MinParticleSize { get; set; }
    public float MaxParticleSize { get; set; }
    public float CameraVelocityScale { get; set; }
    public float VelocityScale { get; set; }
    public float LengthScale { get; set; }
    public float SortingFudge { get; set; }
    public float NormalDirection { get; set; }
    public float ShadowBias { get; set; }
    public int RenderAlignment { get; set; }
    public Vector3 Pivot { get; set; }
    public Vector3 Flip { get; set; }
    public bool UseCustomVertexStreams { get; set; }
    public List<int> VertexStreams { get; set; } = new();
    public bool EnableGPUInstancing { get; set; }
    public bool ApplyActiveColorSpace { get; set; }
    public bool AllowRoll { get; set; }
    public int BytesReadBeforeTail { get; set; }
    public int UnparsedTailBytes { get; set; }
    public List<EffectPrefabPointer> MaterialPointers { get; set; } = new();
    public List<EffectPrefabPointer> MeshPointers { get; set; } = new();
}

public sealed class EffectPrefabPointer
{
    public int FileID { get; set; }
    public long PathID { get; set; }
    public string SourceCAB { get; set; } = string.Empty;
    public bool Resolved { get; set; }
}

public sealed class EffectPrefabReference
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int FileID { get; set; }
    public long PathID { get; set; }
    public string SourceCAB { get; set; } = string.Empty;
    public string Status { get; set; } = "resolved";
}

public sealed class EffectPrefabMesh
{
    public string Key { get; set; } = string.Empty;
    public string SourceCAB { get; set; } = string.Empty;
    public long PathID { get; set; }
    public string Name { get; set; } = string.Empty;
    public int VertexCount { get; set; }
    public float[] Vertices { get; set; } = Array.Empty<float>();
    public float[] Normals { get; set; } = Array.Empty<float>();
    public float[] Tangents { get; set; } = Array.Empty<float>();
    public float[] Colors { get; set; } = Array.Empty<float>();
    public float[] UV0 { get; set; } = Array.Empty<float>();
    public List<EffectPrefabSubMesh> SubMeshes { get; set; } = new();
}

public sealed class EffectPrefabSubMesh
{
    public uint[] Indices { get; set; } = Array.Empty<uint>();
}

public sealed class EffectPrefabMaterial
{
    public string Key { get; set; } = string.Empty;
    public string SourceCAB { get; set; } = string.Empty;
    public long PathID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ShaderName { get; set; } = string.Empty;
    public string ShaderSourceCAB { get; set; } = string.Empty;
    public long ShaderPathID { get; set; }
    public string ShaderKeywords { get; set; } = string.Empty;
    public int RenderQueue { get; set; }
    public bool EnableInstancing { get; set; }
    public List<EffectPrefabStringProperty> Tags { get; set; } = new();
    public string[] DisabledShaderPasses { get; set; } = Array.Empty<string>();
    public List<EffectPrefabFloatProperty> Floats { get; set; } = new();
    public List<EffectPrefabColorProperty> Colors { get; set; } = new();
    public List<EffectPrefabTextureProperty> Textures { get; set; } = new();
}

public sealed class EffectPrefabStringProperty
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class EffectPrefabFloatProperty
{
    public string Name { get; set; } = string.Empty;
    public float Value { get; set; }
}

public sealed class EffectPrefabColorProperty
{
    public string Name { get; set; } = string.Empty;
    public Color Value { get; set; }
}

public sealed class EffectPrefabTextureProperty
{
    public string Name { get; set; } = string.Empty;
    public string TextureName { get; set; } = string.Empty;
    public string SourceCAB { get; set; } = string.Empty;
    public long PathID { get; set; }
    public string PackageEntry { get; set; } = string.Empty;
    public Vector2 Scale { get; set; }
    public Vector2 Offset { get; set; }
    public string Error { get; set; } = string.Empty;
}
