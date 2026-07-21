using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio;

public sealed class EffectPrefabManifest
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "dependency-manifest";
    public List<EffectPrefabNode> Nodes { get; set; } = new();
    public HashSet<string> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnsupportedComponents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static EffectPrefabManifest Build(GameObject root)
    {
        var manifest = new EffectPrefabManifest { Name = root.m_Name };
        if (root.m_Transform != null)
            AddNode(manifest, root.m_Transform, string.Empty);
        return manifest;
    }

    public void Write(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(this, Formatting.Indented));
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
            node.Components.Add(typeName);
            if (obj is not Transform && obj is not MeshRenderer && obj is not SkinnedMeshRenderer &&
                obj is not MeshFilter && obj is not Animator && obj is not Animation)
                manifest.UnsupportedComponents.Add(typeName);
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

    private static void AddRendererDependencies(EffectPrefabManifest manifest, EffectPrefabNode node, Renderer renderer)
    {
        foreach (var material in renderer.m_Materials)
            AddDependency(manifest, node, "Material", material.Name);
        if (renderer is SkinnedMeshRenderer skinned && skinned.m_Mesh.TryGet(out var skinnedMesh))
            AddDependency(manifest, node, "Mesh", skinnedMesh.m_Name);
        else if (renderer.m_GameObject.TryGet<GameObject>(out var gameObject) && gameObject.m_MeshFilter?.m_Mesh.TryGet(out var mesh) == true)
            AddDependency(manifest, node, "Mesh", mesh.m_Name);
    }

    private static void AddDependency(EffectPrefabManifest manifest, EffectPrefabNode node, string kind, string name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        node.Dependencies.Add($"{kind}:{name}");
        manifest.Dependencies.Add($"{kind}:{name}");
    }
}

public sealed class EffectPrefabNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long PathID { get; set; }
    public List<string> Components { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
}
