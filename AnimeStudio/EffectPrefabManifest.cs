using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

    public void Write(string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        foreach (var component in Nodes.SelectMany(node => node.Components))
        {
            if (component.TypeTreeJson == null)
                continue;

            var relativePath = Path.Combine("Components", $"{SanitizeFileName(component.Type)}_{component.PathID}.json");
            var componentPath = Path.Combine(outputDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(componentPath)!);
            File.WriteAllText(componentPath, component.TypeTreeJson);
            component.ParametersFile = relativePath.Replace('\\', '/');
        }
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(this, Formatting.Indented));
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
            if (obj is Transform componentTransform)
            {
                AddTransformData(manifestComponent, componentTransform);
            }
            else if (obj is not MeshRenderer && obj is not SkinnedMeshRenderer &&
                obj is not MeshFilter && obj is not Animator && obj is not Animation)
            {
                manifest.UnsupportedComponents.Add(typeName);
                AddTypeTreeData(manifest, node, manifestComponent, obj);
            }
            if (obj.type == ClassIDType.ParticleSystemRenderer)
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
            }
            if (obj.type == ClassIDType.MonoBehaviour)
                AddMonoBehaviourInfo(manifest, node, manifestComponent, obj);
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
            AddDependency(manifest, node, "Material", material.Name);
        if (renderer is SkinnedMeshRenderer skinned && skinned.m_Mesh.TryGet(out var skinnedMesh))
            AddDependency(manifest, node, "Mesh", skinnedMesh.m_Name);
        else if (renderer.m_GameObject.TryGet<GameObject>(out var gameObject) && gameObject.m_MeshFilter?.m_Mesh.TryGet(out var mesh) == true)
            AddDependency(manifest, node, "Mesh", mesh.m_Name);
    }

    private static void AddParticleRendererDependencies(
        EffectPrefabManifest manifest,
        EffectPrefabNode node,
        EffectPrefabComponent component,
        ParticleSystemRenderer renderer)
    {
        component.ParticleRenderer = new EffectPrefabParticleRenderer
        {
            BytesReadBeforeTail = renderer.m_BytesReadBeforeTail,
            UnparsedTailBytes = renderer.m_UnparsedTailBytes,
            MaterialPointers = renderer.m_Materials.Select(CreatePointerInfo).ToList(),
            MeshPointers = renderer.m_Meshes.Select(CreatePointerInfo).ToList(),
        };
        component.ParametersStatus = "partial-renderer-parse";
        foreach (var materialPointer in renderer.m_Materials)
            AddPointerDependency(manifest, node, "Material", materialPointer);
        foreach (var meshPointer in renderer.m_Meshes)
            AddPointerDependency(manifest, node, "Mesh", meshPointer);
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

    private static void AddTypeTreeData(EffectPrefabManifest manifest, EffectPrefabNode node, EffectPrefabComponent component, Object obj)
    {
        try
        {
            if (Sr44LightParser.TryParse(obj, out var lightData, out var lightError))
            {
                component.TypeTreeJson = JsonConvert.SerializeObject(lightData, Formatting.Indented);
                component.ParametersStatus = "sr44-light-partial";
                component.ParametersError = lightError;
                CollectReferences(manifest, node, component, obj.assetsFile, lightData);
                return;
            }
            var typeData = obj.ToType();
            if (typeData == null)
            {
                component.ParametersStatus = "type-tree-unavailable";
                return;
            }

            component.TypeTreeJson = JsonConvert.SerializeObject(typeData, Formatting.Indented);
            component.ParametersStatus = "exported";
            CollectReferences(manifest, node, component, obj.assetsFile, typeData);
        }
        catch (Exception exception)
        {
            component.ParametersStatus = "type-tree-error";
            component.ParametersError = exception.Message;
        }
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
