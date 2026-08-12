using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZzzEffectPrefabTools
{
    public static class ZzzEffectPrefabImporter
    {
        private const string DefaultOutputFolder = "Assets/unity-extraction-validation/ZZZ/ReconstructedPrefabs";

        [MenuItem("Tools/ZZZ/Rebuild Prefab From Manifest...")]
        public static void ImportFromDialog()
        {
            var manifestPath = EditorUtility.OpenFilePanel("Select ZZZ prefab package", string.Empty, string.Empty);
            if (string.IsNullOrEmpty(manifestPath))
                return;

            using var source = new ImportSource(manifestPath);
            var outputPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{DefaultOutputFolder}/{SanitizeFileName(source.Manifest.Name)}.prefab");
            Import(manifestPath, outputPath);
        }

        public static void ImportFromCommandLine()
        {
            var arguments = Environment.GetCommandLineArgs();
            Import(ReadArgument(arguments, "-zzzManifest"), ReadArgument(arguments, "-zzzOutput"));
        }

        public static void ImportDirectoryFromCommandLine()
        {
            var arguments = Environment.GetCommandLineArgs();
            var packageRoot = ReadArgument(arguments, "-zzzPackageRoot");
            var outputRoot = ReadArgument(arguments, "-zzzOutputRoot");
            if (!Directory.Exists(packageRoot))
                throw new DirectoryNotFoundException(packageRoot);
            if (!outputRoot.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("The output root must be under Assets/.", nameof(outputRoot));

            var packages = Directory.GetFiles(packageRoot, "*.zzzprefab", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var imported = 0;
            foreach (var package in packages)
            {
                using var source = new ImportSource(package);
                var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(packageRoot, package)) ?? string.Empty;
                var relativeOutputDirectory = string.IsNullOrEmpty(relativeDirectory)
                    ? outputRoot
                    : $"{outputRoot}/{relativeDirectory.Replace('\\', '/') }";
                var outputPath = NormalizeAssetPath($"{relativeOutputDirectory}/{SanitizeFileName(source.Manifest.Name)}.prefab");
                Debug.Log($"ZZZ batch importing '{package}' -> '{outputPath}'");
                Import(package, outputPath);
                imported++;
            }
            Debug.Log($"ZZZ batch prefab import finished: {imported}/{packages.Length} packages.");
        }

        public static GameObject Import(string manifestPath, string outputAssetPath)
        {
            outputAssetPath = NormalizeAssetPath(outputAssetPath);
            if (!outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("The output path must be under Assets/.", nameof(outputAssetPath));

            using var source = new ImportSource(manifestPath);
            var manifest = source.Manifest;
            if (manifest.Nodes == null || manifest.Nodes.Length == 0)
                throw new InvalidDataException("The manifest contains no nodes.");

            EnsureAssetFolder(Path.GetDirectoryName(outputAssetPath)?.Replace('\\', '/') ?? DefaultOutputFolder);
            var derivedRoot = GetDerivedRoot(outputAssetPath, manifest.Name);
            if (AssetDatabase.IsValidFolder(derivedRoot))
                AssetDatabase.DeleteAsset(derivedRoot);

            var meshes = CreateMeshes(manifest, derivedRoot);
            var materials = CreateMaterials(source, manifest, derivedRoot);
            var nodes = manifest.Nodes.OrderBy(node => PathDepth(node.Path)).ToArray();
            var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var transformByPathId = new Dictionary<long, Transform>();
            var particleCount = 0;
            var rendererCount = 0;
            var skinnedCount = 0;
            var missingParameters = 0;

            foreach (var node in nodes)
            {
                var gameObject = new GameObject(node.Name, GetNativeComponentTypes(node));
                objects.Add(node.Path, gameObject);
                var parentPath = ParentPath(node.Path);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    if (!objects.TryGetValue(parentPath, out var parent))
                        throw new InvalidDataException($"Missing parent node '{parentPath}' for '{node.Path}'.");
                    gameObject.transform.SetParent(parent.transform, false);
                }

                foreach (var component in node.Components ?? Array.Empty<ManifestComponent>())
                {
                    if (component.Type == "Transform")
                    {
                        transformByPathId[component.PathID] = gameObject.transform;
                        if (!string.IsNullOrEmpty(component.ParametersFile))
                            ApplyTransform(gameObject.transform, source.Read<TransformData>(component.ParametersFile));
                    }
                    else if (component.Type == "MeshRenderer" && component.Renderer != null)
                    {
                        var renderer = gameObject.GetComponent<MeshRenderer>();
                        renderer.enabled = component.Renderer.Enabled;
                        renderer.sharedMaterials = ResolveMaterials(materials, component.Renderer.MaterialPointers);
                        var filter = gameObject.GetComponent<MeshFilter>();
                        filter.sharedMesh = FindMesh(meshes, component.Renderer.MeshPointer);
                        rendererCount++;
                    }
                    else if (component.Type == "SkinnedMeshRenderer" && component.Renderer != null)
                    {
                        var renderer = gameObject.GetComponent<SkinnedMeshRenderer>();
                        renderer.enabled = component.Renderer.Enabled;
                        renderer.sharedMaterials = ResolveMaterials(materials, component.Renderer.MaterialPointers);
                        renderer.sharedMesh = FindMesh(meshes, component.Renderer.MeshPointer);
                        skinnedCount++;
                    }
                    else if (component.Type == "ParticleSystemRenderer" && component.ParticleRenderer != null)
                    {
                        var particleSystem = gameObject.GetComponent<ParticleSystem>();
                        var renderer = gameObject.GetComponent<ParticleSystemRenderer>();
                        renderer.enabled = component.ParticleRenderer.Enabled;
                        renderer.sharedMaterials = ResolveMaterials(materials, component.ParticleRenderer.MaterialPointers);
                        ApplyParticleRenderer(renderer, component.ParticleRenderer, meshes);
                        particleCount++;
                    }
                    else if (component.Type == "ParticleSystem" && string.IsNullOrEmpty(component.ParametersFile))
                    {
                        missingParameters++;
                    }
                }
            }

            foreach (var node in nodes)
            {
                if (!objects.TryGetValue(node.Path, out var gameObject))
                    continue;
                foreach (var component in node.Components ?? Array.Empty<ManifestComponent>())
                {
                    if (component.Type != "SkinnedMeshRenderer" || component.Renderer == null)
                        continue;
                    var renderer = gameObject.GetComponent<SkinnedMeshRenderer>();
                    renderer.bones = (component.Renderer.BonePointers ?? Array.Empty<PointerInfo>())
                        .Select(pointer => transformByPathId.TryGetValue(pointer.PathID, out var bone) ? bone : null)
                        .Where(bone => bone != null)
                        .ToArray();
                    if (component.Renderer.RootBone != null)
                    {
                        if (transformByPathId.TryGetValue(component.Renderer.RootBone.PathID, out var rootBone))
                            renderer.rootBone = rootBone;
                    }
                }
            }

            var rootNode = nodes.FirstOrDefault(node => string.IsNullOrEmpty(ParentPath(node.Path))) ?? nodes[0];
            var root = objects[rootNode.Path];
            var avatar = BuildGenericAvatar(root, derivedRoot, manifest.Name);
            var animator = root.GetComponent<Animator>();
            if (animator != null && avatar != null)
                animator.avatar = avatar;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, outputAssetPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = prefab;
            Debug.Log($"Rebuilt ZZZ prefab '{outputAssetPath}': {nodes.Length} nodes, {rendererCount} MeshRenderers, " +
                      $"{skinnedCount} SkinnedMeshRenderers, {particleCount} ParticleSystemRenderers, " +
                      $"{manifest.Materials?.Length ?? 0} materials, {manifest.Meshes?.Length ?? 0} meshes. " +
                      $"ParticleSystem parameters unavailable: {missingParameters}.");
            return prefab;
        }

        private static Avatar BuildGenericAvatar(GameObject root, string derivedRoot, string manifestName)
        {
            var avatar = AvatarBuilder.BuildGenericAvatar(root, string.Empty);
            if (avatar == null)
            {
                Debug.LogWarning($"ZZZ Avatar generation returned null for '{manifestName}'.");
                return null;
            }

            avatar.name = $"{SanitizeFileName(manifestName)}_Avatar";
            var avatarPath = $"{derivedRoot}/Avatar/{avatar.name}.asset";
            EnsureAssetFolder(Path.GetDirectoryName(avatarPath)?.Replace('\\', '/') ?? derivedRoot);
            AssetDatabase.CreateAsset(avatar, avatarPath);
            Debug.Log($"Generated ZZZ Generic Avatar '{avatarPath}' (valid={avatar.isValid}, human={avatar.isHuman}).");
            return avatar;
        }

        private static Dictionary<string, Mesh> CreateMeshes(Manifest manifest, string derivedRoot)
        {
            var result = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in manifest.Meshes ?? Array.Empty<ManifestMesh>())
            {
                if (info.VertexCount <= 0 || info.Vertices == null || info.Vertices.Length < info.VertexCount * 3)
                    continue;
                var mesh = new Mesh { name = info.Name };
                if (info.VertexCount > ushort.MaxValue)
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = ToVector3Array(info.Vertices, info.VertexCount);
                if (info.Normals?.Length >= info.VertexCount * 3)
                    mesh.normals = ToVector3Array(info.Normals, info.VertexCount);
                if (info.Tangents?.Length >= info.VertexCount * 4)
                    mesh.tangents = ToVector4Array(info.Tangents, info.VertexCount);
                if (info.Colors?.Length >= info.VertexCount * 3)
                    mesh.colors = ToColorArray(info.Colors, info.VertexCount);
                if (info.UV0?.Length >= info.VertexCount * 2)
                    mesh.uv = ToVector2Array(info.UV0, info.VertexCount);
                mesh.subMeshCount = info.SubMeshes?.Length ?? 0;
                for (var index = 0; index < mesh.subMeshCount; index++)
                    mesh.SetTriangles((info.SubMeshes[index].Indices ?? Array.Empty<uint>()).Select(value => (int)value).ToArray(), index, false);
                if (mesh.normals == null || mesh.normals.Length == 0)
                    mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var meshPath = $"{derivedRoot}/Meshes/{SanitizeFileName(info.Name)}_{info.PathID}.asset";
                EnsureAssetFolder(Path.GetDirectoryName(meshPath)?.Replace('\\', '/') ?? derivedRoot);
                AssetDatabase.CreateAsset(mesh, meshPath);
                result[MaterialKey(info.SourceCAB, info.PathID)] = mesh;
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        private static Dictionary<string, Material> CreateMaterials(ImportSource source, Manifest manifest, string derivedRoot)
        {
            var result = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var shader = CreateReconstructedShader(manifest, derivedRoot);
            if (shader == null)
                throw new InvalidOperationException("Failed to create the reconstructed ZZZ shader.");

            var textureUsesColorSpace = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in manifest.Materials ?? Array.Empty<ManifestMaterial>())
            {
                foreach (var property in info.Textures ?? Array.Empty<TextureProperty>())
                {
                    if (string.IsNullOrEmpty(property.PackageEntry))
                        continue;
                    var isColorTexture = IsColorTextureProperty(property.Name);
                    if (textureUsesColorSpace.TryGetValue(property.PackageEntry, out var existing))
                        textureUsesColorSpace[property.PackageEntry] = existing || isColorTexture;
                    else
                        textureUsesColorSpace[property.PackageEntry] = isColorTexture;
                }
            }

            var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in manifest.Materials ?? Array.Empty<ManifestMaterial>())
            {
                var material = new Material(shader) { name = info.Name, enableInstancing = info.EnableInstancing };
                if (info.RenderQueue >= 0)
                    material.renderQueue = info.RenderQueue;
                foreach (var property in info.Floats ?? Array.Empty<FloatProperty>())
                    if (material.HasProperty(property.Name))
                        material.SetFloat(property.Name, property.Value);
                foreach (var property in info.Colors ?? Array.Empty<ColorProperty>())
                    if (material.HasProperty(property.Name))
                        material.SetColor(property.Name, property.Value.ToColor());
                foreach (var property in info.Textures ?? Array.Empty<TextureProperty>())
                {
                    if (string.IsNullOrEmpty(property.PackageEntry) || !material.HasProperty(property.Name))
                        continue;
                    var texturePath = $"{derivedRoot}/Textures/{ShortenAssetFileName(Path.GetFileName(property.PackageEntry))}";
                    EnsureAssetFolder(Path.GetDirectoryName(texturePath)?.Replace('\\', '/') ?? derivedRoot);
                    var absoluteTexturePath = ToAbsolutePath(texturePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(absoluteTexturePath) ?? throw new InvalidOperationException(
                        $"Could not resolve texture directory for '{texturePath}'."));
                    if (!textures.TryGetValue(property.PackageEntry, out var texture))
                    {
                        if (!File.Exists(absoluteTexturePath))
                            File.WriteAllBytes(absoluteTexturePath, source.ReadBytes(property.PackageEntry));
                        AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
                        if (AssetImporter.GetAtPath(texturePath) is TextureImporter importer)
                        {
                            importer.sRGBTexture = textureUsesColorSpace.TryGetValue(property.PackageEntry, out var useColorSpace) && useColorSpace;
                            importer.wrapMode = TextureWrapMode.Repeat;
                            importer.filterMode = FilterMode.Bilinear;
                            importer.mipmapEnabled = true;
                            importer.SaveAndReimport();
                        }
                        texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                        textures[property.PackageEntry] = texture;
                    }
                    material.SetTexture(property.Name, texture);
                    material.SetTextureScale(property.Name, property.Scale.ToVector2());
                    material.SetTextureOffset(property.Name, property.Offset.ToVector2());
                }
                var materialPath = $"{derivedRoot}/Materials/{SanitizeFileName(info.Name)}_{info.PathID}.mat";
                EnsureAssetFolder(Path.GetDirectoryName(materialPath)?.Replace('\\', '/') ?? derivedRoot);
                AssetDatabase.CreateAsset(material, materialPath);
                result[MaterialKey(info.SourceCAB, info.PathID)] = material;
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        private static Shader CreateReconstructedShader(Manifest manifest, string derivedRoot)
        {
            var shaderPath = $"{derivedRoot}/Shaders/ZZZ_Reconstructed.shader";
            EnsureAssetFolder(Path.GetDirectoryName(shaderPath)?.Replace('\\', '/') ?? derivedRoot);
            File.WriteAllText(ToAbsolutePath(shaderPath), BuildShaderSource(manifest));
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        }

        private static string BuildShaderSource(Manifest manifest)
        {
            var textureNames = new HashSet<string>(StringComparer.Ordinal);
            var colorNames = new HashSet<string>(StringComparer.Ordinal);
            var floatNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in manifest.Materials ?? Array.Empty<ManifestMaterial>())
            {
                foreach (var property in material.Textures ?? Array.Empty<TextureProperty>())
                    if (IsShaderPropertyName(property.Name))
                        textureNames.Add(property.Name);
                foreach (var property in material.Colors ?? Array.Empty<ColorProperty>())
                    if (IsShaderPropertyName(property.Name) && !textureNames.Contains(property.Name))
                        colorNames.Add(property.Name);
                foreach (var property in material.Floats ?? Array.Empty<FloatProperty>())
                    if (IsShaderPropertyName(property.Name) && !textureNames.Contains(property.Name) && !colorNames.Contains(property.Name))
                        floatNames.Add(property.Name);
            }

            textureNames.Add("_MainTex");
            colorNames.Add("_Color");
            var source = new StringBuilder();
            source.AppendLine("Shader \"ZZZ/Reconstructed\"");
            source.AppendLine("{");
            source.AppendLine("    Properties");
            source.AppendLine("    {");
            foreach (var name in textureNames.OrderBy(name => name, StringComparer.Ordinal))
                source.AppendLine($"        {name}(\"{name}\", 2D) = \"white\" {{ }}");
            foreach (var name in colorNames.OrderBy(name => name, StringComparer.Ordinal))
                source.AppendLine($"        {name}(\"{name}\", Color) = (1, 1, 1, 1)");
            foreach (var name in floatNames.OrderBy(name => name, StringComparer.Ordinal))
                source.AppendLine($"        {name}(\"{name}\", Float) = 0");
            source.AppendLine("    }");
            source.AppendLine("    SubShader");
            source.AppendLine("    {");
            source.AppendLine("        Tags { \"RenderType\" = \"Opaque\" \"Queue\" = \"Geometry\" }");
            source.AppendLine("        Pass");
            source.AppendLine("        {");
            source.AppendLine("            CGPROGRAM");
            source.AppendLine("            #pragma vertex vert");
            source.AppendLine("            #pragma fragment frag");
            source.AppendLine("            #include \"UnityCG.cginc\"");
            source.AppendLine("            sampler2D _MainTex;");
            source.AppendLine("            float4 _MainTex_ST;");
            source.AppendLine("            fixed4 _Color;");
            source.AppendLine("            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };");
            source.AppendLine("            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };");
            source.AppendLine("            v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }");
            source.AppendLine("            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv) * _Color; }");
            source.AppendLine("            ENDCG");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("}");
            return source.ToString();
        }

        private static bool IsShaderPropertyName(string name)
        {
            if (string.IsNullOrEmpty(name) || (name[0] != '_' && !char.IsLetter(name[0])))
                return false;
            return name.All(character => character == '_' || char.IsLetterOrDigit(character));
        }

        private static bool IsColorTextureProperty(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return false;
            return string.Equals(propertyName, "_MainTex", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "_BaseMap", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "_BaseColorMap", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "_AlbedoMap", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "_DiffuseMap", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "_EmissionMap", StringComparison.OrdinalIgnoreCase) ||
                   propertyName.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("Ramp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("MatCap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("EyeColor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyParticleRenderer(ParticleSystemRenderer renderer, ParticleRendererInfo info, Dictionary<string, Mesh> meshes)
        {
            if (info.PrefixParsed)
            {
                if (Enum.IsDefined(typeof(ParticleSystemRenderMode), info.RenderMode))
                    renderer.renderMode = (ParticleSystemRenderMode)info.RenderMode;
                if (Enum.IsDefined(typeof(ParticleSystemSortMode), info.SortMode))
                    renderer.sortMode = (ParticleSystemSortMode)info.SortMode;
                SetFinite(info.MinParticleSize, value => renderer.minParticleSize = value);
                SetFinite(info.MaxParticleSize, value => renderer.maxParticleSize = value);
                SetFinite(info.CameraVelocityScale, value => renderer.cameraVelocityScale = value);
                SetFinite(info.VelocityScale, value => renderer.velocityScale = value);
                SetFinite(info.LengthScale, value => renderer.lengthScale = value);
                SetFinite(info.SortingFudge, value => renderer.sortingFudge = value);
                SetFinite(info.NormalDirection, value => renderer.normalDirection = value);
                SetFinite(info.ShadowBias, value => renderer.shadowBias = value);
                if (Enum.IsDefined(typeof(ParticleSystemRenderSpace), info.RenderAlignment))
                    renderer.alignment = (ParticleSystemRenderSpace)info.RenderAlignment;
                renderer.pivot = info.Pivot.ToVector3();
                renderer.flip = info.Flip.ToVector3();
                renderer.enableGPUInstancing = info.EnableGPUInstancing;
                renderer.allowRoll = info.AllowRoll;
                var serialized = new SerializedObject(renderer);
                SetBoolean(serialized, "m_ApplyActiveColorSpace", info.ApplyActiveColorSpace);
                SetBoolean(serialized, "m_UseOctagonShape", info.UseOctagonShape);
                SetBoolean(serialized, "m_SkipAutoScalingOpt", info.SkipAutoScalingOpt);
                SetInteger(serialized, "m_OrderType", info.OrderType);
                SetInteger(serialized, "m_LodLevel", info.LodLevel);
                SetInteger(serialized, "m_MaskInteraction", info.MaskInteraction);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            renderer.mesh = (info.MeshPointers ?? Array.Empty<PointerInfo>())
                .Select(pointer => FindMesh(meshes, pointer)).FirstOrDefault(mesh => mesh != null);
        }

        private static Material[] ResolveMaterials(Dictionary<string, Material> materials, IEnumerable<PointerInfo> pointers) =>
            (pointers ?? Array.Empty<PointerInfo>()).Select(pointer => FindMaterial(materials, pointer)).ToArray();

        private static Mesh FindMesh(Dictionary<string, Mesh> meshes, PointerInfo pointer)
        {
            if (pointer == null || pointer.PathID == 0)
                return null;
            if (meshes.TryGetValue(MaterialKey(pointer.SourceCAB, pointer.PathID), out var exact))
                return exact;
            return meshes.FirstOrDefault(pair => pair.Key.EndsWith($":{pointer.PathID}", StringComparison.Ordinal)).Value;
        }

        private static Material FindMaterial(Dictionary<string, Material> materials, PointerInfo pointer)
        {
            if (pointer == null || pointer.PathID == 0)
                return null;
            if (materials.TryGetValue(MaterialKey(pointer.SourceCAB, pointer.PathID), out var exact))
                return exact;
            return materials.FirstOrDefault(pair => pair.Key.EndsWith($":{pointer.PathID}", StringComparison.Ordinal)).Value;
        }

        private static Type[] GetNativeComponentTypes(ManifestNode node)
        {
            var types = new List<Type>();
            var components = node.Components ?? Array.Empty<ManifestComponent>();
            if (components.Any(component => component.Type == "ParticleSystem" || component.Type == "ParticleSystemRenderer"))
                types.Add(typeof(ParticleSystem));
            if (components.Any(component => component.Type == "MeshRenderer"))
                types.Add(typeof(MeshFilter));
            if (components.Any(component => component.Type == "MeshRenderer"))
                types.Add(typeof(MeshRenderer));
            if (components.Any(component => component.Type == "SkinnedMeshRenderer"))
                types.Add(typeof(SkinnedMeshRenderer));
            if (components.Any(component => component.Type == "Animator"))
                types.Add(typeof(Animator));
            if (components.Any(component => component.Type == "Light"))
                types.Add(typeof(Light));
            return types.ToArray();
        }

        private static void ApplyTransform(Transform transform, TransformData data)
        {
            transform.localPosition = data.LocalPosition.ToVector3();
            transform.localRotation = data.LocalRotation.ToQuaternion();
            transform.localScale = data.LocalScale.ToVector3();
        }

        private static Vector3[] ToVector3Array(float[] values, int count)
        {
            var stride = values.Length / count;
            return Enumerable.Range(0, count).Select(i => new Vector3(values[i * stride], values[i * stride + 1], values[i * stride + 2])).ToArray();
        }

        private static Vector4[] ToVector4Array(float[] values, int count) => Enumerable.Range(0, count)
            .Select(i => new Vector4(values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3])).ToArray();

        private static Vector2[] ToVector2Array(float[] values, int count)
        {
            var stride = values.Length / count;
            return Enumerable.Range(0, count).Select(i => new Vector2(values[i * stride], values[i * stride + 1])).ToArray();
        }

        private static Color[] ToColorArray(float[] values, int count)
        {
            var stride = values.Length / count;
            return Enumerable.Range(0, count).Select(i => new Color(values[i * stride], values[i * stride + 1], values[i * stride + 2], stride > 3 ? values[i * stride + 3] : 1f)).ToArray();
        }

        private static void SetFinite(float value, Action<float> setter)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value))
                setter(value);
        }

        private static void SetBoolean(SerializedObject target, string name, bool value)
        {
            if (target.FindProperty(name) is { } property)
                property.boolValue = value;
        }

        private static void SetInteger(SerializedObject target, string name, int value)
        {
            if (target.FindProperty(name) is { } property)
                property.intValue = value;
        }

        private static string MaterialKey(string cab, long pathId)
        {
            var separator = cab?.IndexOf('.') ?? -1;
            return $"{(separator < 0 ? cab : cab.Substring(0, separator))}:{pathId}";
        }

        private static string GetDerivedRoot(string outputAssetPath, string prefabName) =>
            $"{Path.GetDirectoryName(outputAssetPath)?.Replace('\\', '/')}/{SanitizeFileName(prefabName)}_Assets";

        private static string NormalizeAssetPath(string path)
        {
            path = (path ?? string.Empty).Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path.Substring(2);
            return path;
        }

        private static int PathDepth(string path) => path.Count(character => character == '/');
        private static string ParentPath(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator < 0 ? string.Empty : path.Substring(0, separator);
        }

        private static string SanitizeFileName(string value) =>
            Path.GetInvalidFileNameChars().Aggregate(value ?? "Unnamed", (current, invalid) => current.Replace(invalid, '_'));

        private static string ShortenAssetFileName(string value)
        {
            var sanitized = SanitizeFileName(value);
            const int maxLength = 48;
            if (sanitized.Length <= maxLength)
                return sanitized;

            var extension = Path.GetExtension(sanitized);
            var stem = Path.GetFileNameWithoutExtension(sanitized);
            using var sha256 = SHA256.Create();
            var hash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(sanitized)))
                .Replace("-", string.Empty).Substring(0, 12).ToLowerInvariant();
            var prefixLength = Math.Max(1, maxLength - extension.Length - hash.Length - 2);
            return $"{stem.Substring(0, Math.Min(prefixLength, stem.Length))}_{hash}{extension}";
        }

        private static void EnsureAssetFolder(string assetPath)
        {
            assetPath = NormalizeAssetPath(assetPath);
            var parts = assetPath.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            Directory.CreateDirectory(ToAbsolutePath(assetPath));
        }

        private static string ToAbsolutePath(string assetPath) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        private static string ReadArgument(string[] arguments, string name)
        {
            var index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length)
                throw new ArgumentException($"Missing required command-line argument {name}.");
            return arguments[index + 1];
        }

        [Serializable] private sealed class Manifest { public string Name; public ManifestNode[] Nodes; public ManifestMaterial[] Materials; public ManifestMesh[] Meshes; }
        [Serializable] private sealed class ManifestNode { public string Name; public string Path; public long PathID; public ManifestComponent[] Components; }
        [Serializable] private sealed class ManifestComponent
        {
            public string Type; public long PathID; public string ParametersFile;
            public ParticleRendererInfo ParticleRenderer; public RendererInfo Renderer;
        }
        [Serializable] private sealed class RendererInfo
        {
            public bool Enabled; public PointerInfo[] MaterialPointers; public PointerInfo MeshPointer;
            public PointerInfo[] BonePointers; public PointerInfo RootBone;
        }
        [Serializable] private sealed class ParticleRendererInfo
        {
            public bool Enabled; public bool PrefixParsed; public bool RendererTailParsed;
            public int RenderMode; public int SortMode; public int OrderType; public int LodLevel;
            public float MinParticleSize; public float MaxParticleSize; public float CameraVelocityScale;
            public float VelocityScale; public float LengthScale; public float SortingFudge; public float NormalDirection;
            public float ShadowBias; public int RenderAlignment; public Vector3Data Pivot; public Vector3Data Flip;
            public bool EnableGPUInstancing; public bool ApplyActiveColorSpace; public bool AllowRoll;
            public bool UseOctagonShape; public bool SkipAutoScalingOpt; public int MaskInteraction;
            public PointerInfo[] MaterialPointers; public PointerInfo[] MeshPointers;
        }
        [Serializable] private sealed class PointerInfo { public int FileID; public long PathID; public string SourceCAB; }
        [Serializable] private sealed class ManifestMesh
        {
            public string SourceCAB; public long PathID; public string Name; public int VertexCount;
            public float[] Vertices; public float[] Normals; public float[] Tangents; public float[] Colors; public float[] UV0;
            public ManifestSubMesh[] SubMeshes;
        }
        [Serializable] private sealed class ManifestSubMesh { public uint[] Indices; }
        [Serializable] private sealed class ManifestMaterial
        {
            public string SourceCAB; public long PathID; public string Name; public int RenderQueue; public bool EnableInstancing;
            public FloatProperty[] Floats; public ColorProperty[] Colors; public TextureProperty[] Textures;
        }
        [Serializable] private sealed class FloatProperty { public string Name; public float Value; }
        [Serializable] private sealed class ColorProperty { public string Name; public ColorData Value; }
        [Serializable] private sealed class TextureProperty { public string Name; public string PackageEntry; public Vector2Data Scale; public Vector2Data Offset; }
        [Serializable] private sealed class TransformData { public Vector3Data LocalPosition; public QuaternionData LocalRotation; public Vector3Data LocalScale; }
        [Serializable] private struct Vector3Data { public float X; public float Y; public float Z; public Vector3 ToVector3() => new Vector3(X, Y, Z); }
        [Serializable] private struct Vector2Data { public float X; public float Y; public Vector2 ToVector2() => new Vector2(X, Y); }
        [Serializable] private struct QuaternionData { public float X; public float Y; public float Z; public float W; public Quaternion ToQuaternion() => new Quaternion(X, Y, Z, W); }
        [Serializable] private struct ColorData { public float r; public float g; public float b; public float a; public Color ToColor() => new Color(r, g, b, a); }

        private sealed class ImportSource : IDisposable
        {
            private readonly string directory;
            private readonly ZipArchive archive;
            public Manifest Manifest { get; }

            public ImportSource(string path)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("ZZZ prefab package was not found.", path);
                if (string.Equals(Path.GetExtension(path), ".zzzprefab", StringComparison.OrdinalIgnoreCase))
                {
                    archive = ZipFile.OpenRead(path);
                    Manifest = ReadEntry<Manifest>("manifest.json");
                }
                else
                {
                    directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
                    Manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
                }
            }

            public T Read<T>(string relativePath)
            {
                if (archive != null)
                    return ReadEntry<T>(relativePath);
                var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                return JsonUtility.FromJson<T>(File.ReadAllText(path));
            }

            public byte[] ReadBytes(string relativePath)
            {
                if (archive == null)
                    return File.ReadAllBytes(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                var entry = archive.GetEntry(relativePath.Replace('\\', '/')) ?? throw new InvalidDataException($"Package entry '{relativePath}' was not found.");
                using var input = entry.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            }

            public void Dispose() => archive?.Dispose();

            private T ReadEntry<T>(string relativePath)
            {
                var entry = archive.GetEntry(relativePath.Replace('\\', '/')) ?? throw new InvalidDataException($"Package entry '{relativePath}' was not found.");
                using var reader = new StreamReader(entry.Open());
                return JsonUtility.FromJson<T>(reader.ReadToEnd());
            }
        }
    }
}
