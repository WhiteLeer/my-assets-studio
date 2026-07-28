using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
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
            {
                if (component.Renderer == null)
                    continue;
                foreach (var pointer in component.Renderer.MaterialPointers
                             .Concat(component.Renderer.MeshPointer == null
                                 ? Enumerable.Empty<EffectPrefabPointer>()
                                 : new[] { component.Renderer.MeshPointer })
                             .Concat(component.Renderer.BonePointers)
                             .Concat(component.Renderer.RootBone == null
                                 ? Enumerable.Empty<EffectPrefabPointer>()
                                 : new[] { component.Renderer.RootBone }))
                    if (!string.IsNullOrEmpty(pointer.SourceCAB))
                        yield return pointer.SourceCAB;
                continue;
            }
            foreach (var pointer in component.ParticleRenderer.MaterialPointers.Concat(component.ParticleRenderer.MeshPointers))
                if (!string.IsNullOrEmpty(pointer.SourceCAB))
                    yield return pointer.SourceCAB;
            if (component.Renderer != null)
            {
                foreach (var pointer in component.Renderer.MaterialPointers
                             .Concat(component.Renderer.MeshPointer == null
                                 ? Enumerable.Empty<EffectPrefabPointer>()
                                 : new[] { component.Renderer.MeshPointer })
                             .Concat(component.Renderer.BonePointers)
                             .Concat(component.Renderer.RootBone == null
                                 ? Enumerable.Empty<EffectPrefabPointer>()
                                 : new[] { component.Renderer.RootBone }))
                    if (!string.IsNullOrEmpty(pointer.SourceCAB))
                        yield return pointer.SourceCAB;
            }
        }

        foreach (var material in Materials)
        {
            if (!string.IsNullOrEmpty(material.SourceCAB))
                yield return material.SourceCAB;
            if (!string.IsNullOrEmpty(material.ShaderSourceCAB))
                yield return material.ShaderSourceCAB;
            foreach (var texture in material.Textures)
                if (!string.IsNullOrEmpty(texture.SourceCAB))
                    yield return texture.SourceCAB;
        }

        foreach (var mesh in Meshes)
            if (!string.IsNullOrEmpty(mesh.SourceCAB))
                yield return mesh.SourceCAB;
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
                try
                {
                    var particleRenderer = obj as ParticleSystemRenderer ?? new ParticleSystemRenderer(obj.reader);
                    AddParticleRendererDependencies(manifest, node, manifestComponent, particleRenderer);
                }
                catch (Exception exception)
                {
                    manifestComponent.ParametersStatus = "renderer-parse-error";
                    manifestComponent.ParametersError = exception.Message;
                }
                handled = true;
            }
            else if (obj.type == ClassIDType.MonoBehaviour)
            {
                AddMonoBehaviourInfo(manifest, node, manifestComponent, obj);
                handled = true;
            }
            else if (obj.type == ClassIDType.MeshRenderer)
            {
                var meshRenderer = obj as MeshRenderer ?? new MeshRenderer(obj.reader);
                AddRendererInfo(manifestComponent, meshRenderer, null);
                AddRendererDependencies(manifest, node, meshRenderer);
                handled = true;
            }
            else if (obj.type == ClassIDType.SkinnedMeshRenderer)
            {
                var skinnedMeshRenderer = obj as SkinnedMeshRenderer ?? new SkinnedMeshRenderer(obj.reader);
                AddRendererInfo(manifestComponent, skinnedMeshRenderer, skinnedMeshRenderer);
                AddRendererDependencies(manifest, node, skinnedMeshRenderer);
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
        component.ParametersStatus = "transform-parsed";
    }

    private static void AddRendererDependencies(EffectPrefabManifest manifest, EffectPrefabNode node, Renderer renderer)
    {
        foreach (var material in renderer.m_Materials ?? new List<PPtr<Material>>())
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

    private static void AddRendererInfo(
        EffectPrefabComponent component,
        Renderer renderer,
        SkinnedMeshRenderer skinnedRenderer)
    {
        component.Renderer = new EffectPrefabRenderer
        {
            Enabled = renderer.m_Enabled,
            MaterialPointers = (renderer.m_Materials ?? new List<PPtr<Material>>()).Select(CreatePointerInfo).ToList(),
        };

        if (skinnedRenderer != null)
        {
            component.Renderer.MeshPointer = CreatePointerInfo(skinnedRenderer.m_Mesh);
            component.Renderer.BonePointers = (skinnedRenderer.m_Bones ?? new List<PPtr<Transform>>()).Select(CreatePointerInfo).ToList();
            component.Renderer.RootBone = CreatePointerInfo(skinnedRenderer.m_RootBone);
            return;
        }

        if (renderer.m_GameObject.TryGet<GameObject>(out var gameObject) &&
            gameObject.m_MeshFilter?.m_Mesh != null)
            component.Renderer.MeshPointer = CreatePointerInfo(gameObject.m_MeshFilter.m_Mesh);
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
            RendererTailParsed = renderer.m_RendererTailParsed,
            RenderMode = renderer.m_RenderMode,
            SortMode = renderer.m_SortMode,
            OrderType = renderer.m_OrderType,
            LodLevel = renderer.m_LodLevel,
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
            EnableGPUInstancing = renderer.m_EnableGPUInstancing,
            ApplyActiveColorSpace = renderer.m_ApplyActiveColorSpace,
            AllowRoll = renderer.m_AllowRoll,
            UseOctagonShape = renderer.m_UseOctagonShape,
            SkipAutoScalingOpt = renderer.m_SkipAutoScalingOpt,
            OctagonExpand = renderer.m_OctagonExpand,
            AlphaThresholdParticles = renderer.m_AlphaThresholdParticles,
            VertexStreamsBytes = renderer.m_VertexStreams?.Length ?? 0,
            MaskInteraction = renderer.m_MaskInteraction,
            BytesReadBeforeTail = renderer.m_BytesReadBeforeTail,
            UnparsedTailBytes = renderer.m_UnparsedTailBytes,
            UnknownTailHex = renderer.m_UnknownTailBytes.Length == 0
                ? string.Empty
                : Convert.ToHexString(renderer.m_UnknownTailBytes),
            MaterialPointers = renderer.m_Materials.Select(CreatePointerInfo).ToList(),
            MeshPointers = renderer.m_Meshes.Select(CreatePointerInfo).ToList(),
        };
        component.ParametersStatus = !renderer.m_RendererTailParsed
            ? "partial-renderer-parse"
            : renderer.m_UnparsedTailBytes == 0
                ? "renderer-parsed"
                : "renderer-parsed-with-unknown-tail";
        foreach (var materialPointer in renderer.m_Materials)
        {
            AddPointerDependency(manifest, node, "Material", materialPointer);
            AddMaterial(manifest, materialPointer);
        }
        foreach (var meshPointer in renderer.m_Meshes)
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
            try
            {
                behaviour.reader.Reset();
                var data = behaviour.ToType();
                if (data != null)
                {
                    component.TypeTreeJson = JsonConvert.SerializeObject(data, Formatting.Indented);
                    component.ParametersStatus = "type-tree-exported";
                    CollectReferences(manifest, node, component, behaviour.assetsFile, data);
                }
                else
                {
                    component.ParametersStatus = "mono-header-parsed";
                }
            }
            catch (Exception exception)
            {
                component.ParametersStatus = "mono-header-parsed";
                component.ParametersError = exception.Message;
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
            if (obj.type == ClassIDType.Light || obj.type == ClassIDType.ParticleSystem || obj.type == ClassIDType.PlayableDirector || obj.type == ClassIDType.MonoBehaviour)
            {
                obj.reader.Reset();
            }
            var typeData = obj.ToType();
            if (typeData == null)
            {
                component.ParametersStatus = "type-tree-unavailable";
                return false;
            }

            component.TypeTreeJson = JsonConvert.SerializeObject(typeData, Formatting.Indented);
            component.ParametersStatus = "exported";
            CollectReferences(manifest, node, component, obj.assetsFile, typeData);
            return true;
        }
        catch (Exception exception)
        {
            component.ParametersStatus = "type-tree-error";
            component.ParametersError = exception.Message;
            return false;
        }
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
            GameVersion = "ZZZ 3.0",
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
    public string TypeTreeSource { get; set; } = string.Empty;
    public string TypeTreeHash { get; set; } = string.Empty;
    public string TypeTreeSchemaFile { get; set; } = string.Empty;
    public List<EffectPrefabReference> References { get; set; } = new();
    public EffectPrefabParticleRenderer ParticleRenderer { get; set; }
    public EffectPrefabRenderer Renderer { get; set; }
    public EffectPrefabMonoBehaviour MonoBehaviour { get; set; }
    [JsonIgnore]
    public string TypeTreeJson { get; set; }
}

public sealed class EffectPrefabRenderer
{
    public bool Enabled { get; set; }
    public List<EffectPrefabPointer> MaterialPointers { get; set; } = new();
    public EffectPrefabPointer MeshPointer { get; set; }
    public List<EffectPrefabPointer> BonePointers { get; set; } = new();
    public EffectPrefabPointer RootBone { get; set; }
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
    public bool RendererTailParsed { get; set; }
    public int RenderMode { get; set; }
    public int SortMode { get; set; }
    public int OrderType { get; set; }
    public int LodLevel { get; set; }
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
    public bool EnableGPUInstancing { get; set; }
    public bool ApplyActiveColorSpace { get; set; }
    public bool AllowRoll { get; set; }
    public bool UseOctagonShape { get; set; }
    public bool SkipAutoScalingOpt { get; set; }
    public float OctagonExpand { get; set; }
    public int AlphaThresholdParticles { get; set; }
    public int VertexStreamsBytes { get; set; }
    public int MaskInteraction { get; set; }
    public int BytesReadBeforeTail { get; set; }
    public int UnparsedTailBytes { get; set; }
    public string UnknownTailHex { get; set; } = string.Empty;
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
