using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SrEffectPrefabTools
{
    public static class SrEffectPrefabImporter
    {
        private const string DefaultOutputFolder = "Assets/unity-extraction-validation/SR/ReconstructedPrefabs";

        [MenuItem("Tools/SR/Rebuild Effect Prefab From Manifest...")]
        public static void ImportFromDialog()
        {
            var manifestPath = EditorUtility.OpenFilePanel("Select SR prefab package", string.Empty, string.Empty);
            if (string.IsNullOrEmpty(manifestPath))
                return;

            Directory.CreateDirectory(ToAbsolutePath(DefaultOutputFolder));
            using var source = new ImportSource(manifestPath);
            var manifest = source.Manifest;
            var outputPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{DefaultOutputFolder}/{SanitizeFileName(manifest.Name)}.prefab");
            Import(manifestPath, outputPath);
        }

        public static void ImportFromCommandLine()
        {
            var arguments = Environment.GetCommandLineArgs();
            var manifestPath = ReadArgument(arguments, "-srManifest");
            var outputPath = ReadArgument(arguments, "-srOutput");
            Import(manifestPath, outputPath);
        }

        public static GameObject Import(string manifestPath, string outputAssetPath)
        {
            using var source = new ImportSource(manifestPath);
            var manifest = source.Manifest;
            if (manifest.Nodes == null || manifest.Nodes.Length == 0)
                throw new InvalidDataException("The manifest contains no nodes.");
            if (!outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("The output path must be under Assets/.", nameof(outputAssetPath));

            var nodes = manifest.Nodes.OrderBy(node => PathDepth(node.Path)).ToArray();
            var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var warnings = new List<string>();
            var derivedRoot = GetDerivedRoot(outputAssetPath, manifest.Name);
            if (AssetDatabase.IsValidFolder(derivedRoot))
                AssetDatabase.DeleteAsset(derivedRoot);
            var meshes = CreateMeshes(manifest, derivedRoot);
            var materials = CreateMaterials(source, manifest, outputAssetPath, warnings);
            var lightCount = 0;
            var particleSystemCount = 0;
            var particleRendererCount = 0;
            var animatorCount = 0;

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
                    if (component.Type == "Transform" && !string.IsNullOrEmpty(component.ParametersFile))
                        ApplyTransform(gameObject.transform, source.Read<TransformData>(component.ParametersFile));
                    else if (component.Type == "Light" && !string.IsNullOrEmpty(component.ParametersFile))
                    {
                        ApplyLight(gameObject.GetComponent<Light>() ?? gameObject.AddComponent<Light>(), source.Read<LightData>(component.ParametersFile));
                        lightCount++;
                    }
                    else if (component.Type == "ParticleSystem" && !string.IsNullOrEmpty(component.ParametersFile))
                    {
                        var particleSystem = gameObject.GetComponent<ParticleSystem>() ??
                                             throw new InvalidOperationException($"Failed to create ParticleSystem on '{node.Path}'.");
                        ApplyParticleSystem(particleSystem, source.Read<ParticleSystemData>(component.ParametersFile));
                        particleSystemCount++;
                    }
                    else if (component.Type == "ParticleSystemRenderer")
                    {
                        var particleSystem = gameObject.GetComponent<ParticleSystem>() ??
                                             throw new InvalidOperationException($"Failed to create ParticleSystemRenderer on '{node.Path}'.");
                        var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
                        if (component.ParticleRenderer != null)
                        {
                            renderer.enabled = component.ParticleRenderer.Enabled;
                            renderer.sharedMaterials = (component.ParticleRenderer.MaterialPointers ?? Array.Empty<PointerInfo>())
                                .Select(pointer => FindMaterial(materials, pointer))
                                .ToArray();
                            ApplyParticleRenderer(renderer, component.ParticleRenderer, meshes);
                        }
                        particleRendererCount++;
                    }
                    else if (component.Type == "Animator")
                    {
                        animatorCount++;
                    }
                    else if (component.MonoBehaviour != null && component.MonoBehaviour.ClassName == "CustomAdditionalLightData")
                        warnings.Add($"{node.Path}: CustomAdditionalLightData is preserved in {component.ParametersFile}, but its SR runtime behavior is not reconstructed.");
                }
            }

            var root = objects[nodes[0].Path];
            var outputDirectory = Path.GetDirectoryName(outputAssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(outputDirectory))
                EnsureAssetFolder(outputDirectory);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, outputAssetPath);
            UnityEngine.Object.DestroyImmediate(root);

            AssetDatabase.Refresh();
            Selection.activeObject = prefab;
            Debug.Log($"Rebuilt SR effect prefab '{outputAssetPath}': {nodes.Length} nodes, {particleSystemCount} ParticleSystems, " +
                      $"{particleRendererCount} ParticleSystemRenderers, {animatorCount} Animators, {lightCount} Lights. " +
                      $"Preserved SR extension warnings: {warnings.Count}.");
            return prefab;
        }

        private static Dictionary<string, Mesh> CreateMeshes(Manifest manifest, string derivedRoot)
        {
            var result = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            if (manifest.Meshes == null || manifest.Meshes.Length == 0)
                return result;
            var meshFolder = $"{derivedRoot}/Meshes";
            EnsureAssetFolder(meshFolder);
            foreach (var info in manifest.Meshes)
            {
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
                var meshPath = $"{meshFolder}/{SanitizeFileName(info.Name)}_{info.PathID}.asset";
                AssetDatabase.CreateAsset(mesh, meshPath);
                result[MaterialKey(info.SourceCAB, info.PathID)] = mesh;
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        private static void ApplyParticleRenderer(ParticleSystemRenderer renderer, ParticleRendererInfo info, Dictionary<string, Mesh> meshes)
        {
            if (info.PrefixParsed)
            {
                if (info.RenderMode >= 0 && info.RenderMode <= (int)ParticleSystemRenderMode.None)
                    renderer.renderMode = (ParticleSystemRenderMode)info.RenderMode;
                if (info.SortMode >= 0 && info.SortMode <= (int)ParticleSystemSortMode.OldestInFront)
                    renderer.sortMode = (ParticleSystemSortMode)info.SortMode;
                SetFinite(info.MinParticleSize, value => renderer.minParticleSize = value);
                SetFinite(info.MaxParticleSize, value => renderer.maxParticleSize = value);
                SetFinite(info.CameraVelocityScale, value => renderer.cameraVelocityScale = value);
                SetFinite(info.VelocityScale, value => renderer.velocityScale = value);
                SetFinite(info.LengthScale, value => renderer.lengthScale = value);
                SetFinite(info.SortingFudge, value => renderer.sortingFudge = value);
                SetFinite(info.NormalDirection, value => renderer.normalDirection = value);
                SetFinite(info.ShadowBias, value => renderer.shadowBias = value);
                if (info.RenderAlignment >= 0 && info.RenderAlignment <= (int)ParticleSystemRenderSpace.World)
                    renderer.alignment = (ParticleSystemRenderSpace)info.RenderAlignment;
                renderer.pivot = info.Pivot.ToVector3();
                renderer.flip = info.Flip.ToVector3();
                renderer.enableGPUInstancing = info.EnableGPUInstancing;
                renderer.allowRoll = info.AllowRoll;
                var serialized = new SerializedObject(renderer);
                SetBoolean(serialized, "m_ApplyActiveColorSpace", info.ApplyActiveColorSpace);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            renderer.mesh = (info.MeshPointers ?? Array.Empty<PointerInfo>())
                .Select(pointer => FindMesh(meshes, pointer))
                .FirstOrDefault(mesh => mesh != null);
        }

        private static Mesh FindMesh(Dictionary<string, Mesh> meshes, PointerInfo pointer)
        {
            if (pointer == null || pointer.PathID == 0)
                return null;
            if (meshes.TryGetValue(MaterialKey(pointer.SourceCAB, pointer.PathID), out var exact))
                return exact;
            return meshes.FirstOrDefault(pair => pair.Key.EndsWith($":{pointer.PathID}", StringComparison.Ordinal)).Value;
        }

        private static Vector3[] ToVector3Array(float[] values, int count)
        {
            var stride = values.Length / count;
            return Enumerable.Range(0, count).Select(index => new Vector3(values[index * stride], values[index * stride + 1], values[index * stride + 2])).ToArray();
        }

        private static Vector4[] ToVector4Array(float[] values, int count) => Enumerable.Range(0, count)
            .Select(index => new Vector4(values[index * 4], values[index * 4 + 1], values[index * 4 + 2], values[index * 4 + 3])).ToArray();

        private static Vector2[] ToVector2Array(float[] values, int count)
        {
            var stride = values.Length / count;
            return Enumerable.Range(0, count).Select(index => new Vector2(values[index * stride], values[index * stride + 1])).ToArray();
        }

        private static Color[] ToColorArray(float[] values, int count)
        {
            var stride = values.Length / count;
            return Enumerable.Range(0, count).Select(index => new Color(values[index * stride], values[index * stride + 1], values[index * stride + 2], stride > 3 ? values[index * stride + 3] : 1f)).ToArray();
        }

        private static Dictionary<string, Material> CreateMaterials(ImportSource source, Manifest manifest, string outputAssetPath, List<string> warnings)
        {
            var result = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            if (manifest.Materials == null || manifest.Materials.Length == 0)
                return result;

            var root = GetDerivedRoot(outputAssetPath, manifest.Name);
            var shaderFolder = $"{root}/Shaders";
            var textureFolder = $"{root}/Textures";
            var materialFolder = $"{root}/Materials";
            EnsureAssetFolder(shaderFolder);
            EnsureAssetFolder(textureFolder);
            EnsureAssetFolder(materialFolder);

            var shaderPath = $"{shaderFolder}/SR_ReconstructedParticle.shader";
            File.WriteAllText(ToAbsolutePath(shaderPath), ReconstructedParticleShader);
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath) ?? Shader.Find("Particles/Standard Unlit");
            if (shader == null)
                throw new InvalidOperationException("Failed to create the reconstructed SR particle shader.");

            var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in manifest.Materials)
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
                    if (!textures.TryGetValue(property.PackageEntry, out var texture))
                    {
                        var texturePath = $"{textureFolder}/{SanitizeFileName(Path.GetFileName(property.PackageEntry))}";
                        File.WriteAllBytes(ToAbsolutePath(texturePath), source.ReadBytes(property.PackageEntry));
                        AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
                        texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                        textures[property.PackageEntry] = texture;
                    }
                    material.SetTexture(property.Name, texture);
                    material.SetTextureScale(property.Name, property.Scale.ToVector2());
                    material.SetTextureOffset(property.Name, property.Offset.ToVector2());
                }
                material.shaderKeywords = SplitKeywords(info.ShaderKeywords);
                var materialPath = $"{materialFolder}/{SanitizeFileName(info.Name)}_{info.PathID}.mat";
                AssetDatabase.CreateAsset(material, materialPath);
                result[MaterialKey(info.SourceCAB, info.PathID)] = material;
                if (string.IsNullOrEmpty(info.ShaderName))
                    warnings.Add($"{info.Name}: original shader name unresolved ({info.ShaderSourceCAB}:{info.ShaderPathID}); reconstructed shader applied.");
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        private static Material FindMaterial(Dictionary<string, Material> materials, PointerInfo pointer)
        {
            if (pointer == null || pointer.PathID == 0)
                return null;
            if (materials.TryGetValue(MaterialKey(pointer.SourceCAB, pointer.PathID), out var exact))
                return exact;
            return materials.FirstOrDefault(pair => pair.Key.EndsWith($":{pointer.PathID}", StringComparison.Ordinal)).Value;
        }

        private static string MaterialKey(string cab, long pathId)
        {
            var separator = cab?.IndexOf('.') ?? -1;
            return $"{(separator < 0 ? cab : cab.Substring(0, separator))}:{pathId}";
        }

        private static string GetDerivedRoot(string outputAssetPath, string prefabName) =>
            $"{Path.GetDirectoryName(outputAssetPath)?.Replace('\\', '/')}/{SanitizeFileName(prefabName)}_Assets";

        private static string[] SplitKeywords(string value) => (value ?? string.Empty)
            .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        private static Type[] GetNativeComponentTypes(ManifestNode node)
        {
            var componentTypes = new List<Type>();
            var components = node.Components ?? Array.Empty<ManifestComponent>();
            if (components.Any(component => component.Type == "ParticleSystem" || component.Type == "ParticleSystemRenderer"))
                componentTypes.Add(typeof(ParticleSystem));
            if (components.Any(component => component.Type == "Animator"))
                componentTypes.Add(typeof(Animator));
            if (components.Any(component => component.Type == "Light"))
                componentTypes.Add(typeof(Light));
            return componentTypes.ToArray();
        }

        private static void ApplyTransform(Transform transform, TransformData data)
        {
            transform.localPosition = data.LocalPosition.ToVector3();
            transform.localRotation = data.LocalRotation.ToQuaternion();
            transform.localScale = data.LocalScale.ToVector3();
        }

        private static void ApplyLight(Light light, LightData data)
        {
            light.enabled = data.Enabled;
            if (data.Type >= (int)LightType.Spot && data.Type <= (int)LightType.Disc)
                light.type = (LightType)data.Type;
            light.color = new Color(data.Color.r, data.Color.g, data.Color.b, data.Color.a);
            SetFinite(data.Intensity, value => light.intensity = value);
            SetFinite(data.Range, value => light.range = value);
            SetFinite(data.SpotAngle, value => light.spotAngle = value);
            SetFinite(data.InnerSpotAngle, value => light.innerSpotAngle = value);
            SetFinite(data.CookieSize, value => light.cookieSize = value);

            if (data.Shadows != null)
            {
                if (data.Shadows.Type >= (int)LightShadows.None && data.Shadows.Type <= (int)LightShadows.Soft)
                    light.shadows = (LightShadows)data.Shadows.Type;
                SetFinite(data.Shadows.Strength, value => light.shadowStrength = value);
                SetFinite(data.Shadows.Bias, value => light.shadowBias = value);
                SetFinite(data.Shadows.NormalBias, value => light.shadowNormalBias = value);
                SetFinite(data.Shadows.NearPlane, value => light.shadowNearPlane = value);
                if (data.Shadows.CustomResolution > 0)
                    light.shadowCustomResolution = data.Shadows.CustomResolution;
            }
        }

        private static void ApplyParticleSystem(ParticleSystem particleSystem, ParticleSystemData data)
        {
            var serialized = new SerializedObject(particleSystem);
            SetFloat(serialized, "lengthInSec", Math.Max(0.05f, data.LengthInSec));
            SetFloat(serialized, "simulationSpeed", data.SimulationSpeed);
            SetInteger(serialized, "stopAction", data.StopAction);
            SetInteger(serialized, "cullingMode", data.CullingMode);
            SetInteger(serialized, "ringBufferMode", data.RingBufferMode);
            SetVector2(serialized, "ringBufferLoopRange", data.RingBufferLoopRange.ToVector2());
            SetBoolean(serialized, "looping", data.Looping);
            SetBoolean(serialized, "prewarm", data.Prewarm && data.Looping);
            SetBoolean(serialized, "playOnAwake", data.PlayOnAwake);
            SetBoolean(serialized, "useUnscaledTime", data.UseUnscaledTime);
            SetBoolean(serialized, "autoRandomSeed", data.AutoRandomSeed);
            SetBoolean(serialized, "useRigidbodyForVelocity", data.UseRigidbodyForVelocity);
            SetMinMaxCurve(serialized, "startDelay", data.StartDelay);
            if (data.InitialModule != null)
            {
                SetMinMaxCurve(serialized, "InitialModule.startLifetime", data.InitialModule.StartLifetime);
                SetMinMaxCurve(serialized, "InitialModule.startSpeed", data.InitialModule.StartSpeed);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var main = particleSystem.main;
            if (data.SimulationSpace >= (int)ParticleSystemSimulationSpace.Local &&
                data.SimulationSpace <= (int)ParticleSystemSimulationSpace.Custom)
                main.simulationSpace = (ParticleSystemSimulationSpace)data.SimulationSpace;
            if (data.ScalingMode >= (int)ParticleSystemScalingMode.Hierarchy &&
                data.ScalingMode <= (int)ParticleSystemScalingMode.Shape)
                main.scalingMode = (ParticleSystemScalingMode)data.ScalingMode;
            if (data.RandomSeed != 0)
                particleSystem.randomSeed = data.RandomSeed;
        }

        private static void SetMinMaxCurve(SerializedObject target, string path, MinMaxCurveData curve)
        {
            if (curve == null)
                return;
            SetInteger(target, $"{path}.minMaxState", curve.MinMaxState);
            SetFloat(target, $"{path}.scalar", curve.Scalar);
            SetFloat(target, $"{path}.minScalar", curve.MinScalar);
        }

        private static void SetFloat(SerializedObject target, string name, float value)
        {
            if (IsFinite(value) && target.FindProperty(name) is { } property)
                property.floatValue = value;
        }

        private static void SetInteger(SerializedObject target, string name, int value)
        {
            if (target.FindProperty(name) is { } property)
                property.intValue = value;
        }

        private static void SetBoolean(SerializedObject target, string name, bool value)
        {
            if (target.FindProperty(name) is { } property)
                property.boolValue = value;
        }

        private static void SetVector2(SerializedObject target, string name, Vector2 value)
        {
            if (target.FindProperty(name) is { } property)
                property.vector2Value = value;
        }

        private static void SetFinite(float value, Action<float> setter)
        {
            if (IsFinite(value))
                setter(value);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static int PathDepth(string path) => path.Count(character => character == '/');

        private static string ParentPath(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator < 0 ? string.Empty : path.Substring(0, separator);
        }

        private static string SanitizeFileName(string value)
        {
            return Path.GetInvalidFileNameChars().Aggregate(value, (current, invalid) => current.Replace(invalid, '_'));
        }

        private static void EnsureAssetFolder(string assetPath)
        {
            var parts = assetPath.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string ToAbsolutePath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        private static string ReadArgument(string[] arguments, string name)
        {
            var index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length)
                throw new ArgumentException($"Missing required command-line argument {name}.");
            return arguments[index + 1];
        }

        [Serializable]
        private sealed class Manifest
        {
            public string Name;
            public string UnityVersion;
            public ManifestNode[] Nodes;
            public ManifestMaterial[] Materials;
            public ManifestMesh[] Meshes;
        }

        [Serializable]
        private sealed class ManifestNode
        {
            public string Name;
            public string Path;
            public ManifestComponent[] Components;
        }

        [Serializable]
        private sealed class ManifestComponent
        {
            public string Type;
            public string ParametersFile;
            public MonoBehaviourInfo MonoBehaviour;
            public ParticleRendererInfo ParticleRenderer;
        }

        [Serializable]
        private sealed class ParticleRendererInfo
        {
            public bool Enabled;
            public bool PrefixParsed;
            public int RenderMode; public int SortMode; public float MinParticleSize; public float MaxParticleSize;
            public float CameraVelocityScale; public float VelocityScale; public float LengthScale; public float SortingFudge;
            public float NormalDirection; public float ShadowBias; public int RenderAlignment;
            public Vector3Data Pivot; public Vector3Data Flip;
            public bool EnableGPUInstancing; public bool ApplyActiveColorSpace; public bool AllowRoll;
            public PointerInfo[] MaterialPointers;
            public PointerInfo[] MeshPointers;
        }

        [Serializable] private sealed class PointerInfo { public long PathID; public string SourceCAB; }
        [Serializable] private sealed class ManifestMaterial
        {
            public string SourceCAB; public long PathID; public string Name; public string ShaderName;
            public string ShaderSourceCAB; public long ShaderPathID; public string ShaderKeywords;
            public int RenderQueue; public bool EnableInstancing;
            public FloatProperty[] Floats; public ColorProperty[] Colors; public TextureProperty[] Textures;
        }
        [Serializable] private sealed class FloatProperty { public string Name; public float Value; }
        [Serializable] private sealed class ColorProperty { public string Name; public ColorData Value; }
        [Serializable] private sealed class TextureProperty
        {
            public string Name; public string PackageEntry; public Vector2Data Scale; public Vector2Data Offset;
        }
        [Serializable] private sealed class ManifestMesh
        {
            public string SourceCAB; public long PathID; public string Name; public int VertexCount;
            public float[] Vertices; public float[] Normals; public float[] Tangents; public float[] Colors; public float[] UV0;
            public ManifestSubMesh[] SubMeshes;
        }
        [Serializable] private sealed class ManifestSubMesh { public uint[] Indices; }

        [Serializable]
        private sealed class MonoBehaviourInfo
        {
            public string ClassName;
        }

        [Serializable]
        private sealed class TransformData
        {
            public Vector3Data LocalPosition;
            public QuaternionData LocalRotation;
            public Vector3Data LocalScale;
        }

        [Serializable]
        private struct Vector3Data
        {
            public float X;
            public float Y;
            public float Z;
            public Vector3 ToVector3() => new Vector3(X, Y, Z);
        }

        [Serializable]
        private struct Vector2Data
        {
            public float X;
            public float Y;
            public Vector2 ToVector2() => new Vector2(X, Y);
        }

        [Serializable]
        private struct QuaternionData
        {
            public float X;
            public float Y;
            public float Z;
            public float W;
            public Quaternion ToQuaternion() => new Quaternion(X, Y, Z, W);
        }

        [Serializable]
        private struct ColorData
        {
            public float r;
            public float g;
            public float b;
            public float a;
            public Color ToColor() => new Color(r, g, b, a);
        }

        [Serializable]
        private sealed class LightData
        {
            public bool Enabled;
            public int Type;
            public ColorData Color;
            public float Intensity;
            public float Range;
            public float SpotAngle;
            public float InnerSpotAngle;
            public float CookieSize;
            public ShadowData Shadows;
        }

        [Serializable]
        private sealed class ShadowData
        {
            public int Type;
            public int CustomResolution;
            public float Strength;
            public float Bias;
            public float NormalBias;
            public float NearPlane;
        }

        [Serializable]
        private sealed class ParticleSystemData
        {
            public float LengthInSec;
            public float SimulationSpeed;
            public int StopAction;
            public int CullingMode;
            public int RingBufferMode;
            public Vector2Data RingBufferLoopRange;
            public bool Looping;
            public bool Prewarm;
            public bool PlayOnAwake;
            public bool UseUnscaledTime;
            public bool AutoRandomSeed;
            public bool UseRigidbodyForVelocity;
            public MinMaxCurveData StartDelay;
            public int SimulationSpace;
            public int ScalingMode;
            public uint RandomSeed;
            public InitialModuleData InitialModule;
        }

        [Serializable]
        private sealed class InitialModuleData
        {
            public bool Enabled;
            public int SrExtension0;
            public int SrExtension1;
            public MinMaxCurveData StartLifetime;
            public MinMaxCurveData StartSpeed;
        }

        [Serializable]
        private sealed class MinMaxCurveData
        {
            public int MinMaxState;
            public float Scalar;
            public float MinScalar;
            public AnimationCurveData MaxCurve;
            public AnimationCurveData MinCurve;

            public ParticleSystem.MinMaxCurve ToMinMaxCurve()
            {
                return (ParticleSystemCurveMode)MinMaxState switch
                {
                    ParticleSystemCurveMode.Curve => new ParticleSystem.MinMaxCurve(Scalar, MaxCurve?.ToAnimationCurve()),
                    ParticleSystemCurveMode.TwoCurves => new ParticleSystem.MinMaxCurve(Scalar, MinCurve?.ToAnimationCurve(), MaxCurve?.ToAnimationCurve()),
                    ParticleSystemCurveMode.TwoConstants => new ParticleSystem.MinMaxCurve(MinScalar, Scalar),
                    _ => new ParticleSystem.MinMaxCurve(Scalar),
                };
            }
        }

        [Serializable]
        private sealed class AnimationCurveData
        {
            public CurveKeyData[] Keys;
            public int PreInfinity;
            public int PostInfinity;

            public AnimationCurve ToAnimationCurve()
            {
                var curve = new AnimationCurve((Keys ?? Array.Empty<CurveKeyData>()).Select(key => key.ToKeyframe()).ToArray());
                if (Enum.IsDefined(typeof(WrapMode), PreInfinity))
                    curve.preWrapMode = (WrapMode)PreInfinity;
                if (Enum.IsDefined(typeof(WrapMode), PostInfinity))
                    curve.postWrapMode = (WrapMode)PostInfinity;
                return curve;
            }
        }

        [Serializable]
        private sealed class CurveKeyData
        {
            public float Time;
            public float Value;
            public float InSlope;
            public float OutSlope;
            public int WeightedMode;
            public float InWeight;
            public float OutWeight;

            public Keyframe ToKeyframe()
            {
                return new Keyframe(Time, Value, InSlope, OutSlope, InWeight, OutWeight)
                {
                    weightedMode = (WeightedMode)Mathf.Clamp(WeightedMode, 0, 3),
                };
            }
        }

        private sealed class ImportSource : IDisposable
        {
            private readonly string directory;
            private readonly ZipArchive archive;

            public Manifest Manifest { get; }

            public ImportSource(string path)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("SR prefab package was not found.", path);

                if (string.Equals(Path.GetExtension(path), ".srprefab", StringComparison.OrdinalIgnoreCase))
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
                if (!File.Exists(path))
                    throw new FileNotFoundException("SR prefab component data was not found.", path);
                return JsonUtility.FromJson<T>(File.ReadAllText(path));
            }

            public byte[] ReadBytes(string relativePath)
            {
                if (archive != null)
                {
                    var entry = archive.GetEntry(relativePath.Replace('\\', '/')) ??
                                throw new InvalidDataException($"Package entry '{relativePath}' was not found.");
                    using var input = entry.Open();
                    using var output = new MemoryStream();
                    input.CopyTo(output);
                    return output.ToArray();
                }
                return File.ReadAllBytes(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            public void Dispose() => archive?.Dispose();

            private T ReadEntry<T>(string relativePath)
            {
                var entry = archive.GetEntry(relativePath.Replace('\\', '/')) ??
                            throw new InvalidDataException($"Package entry '{relativePath}' was not found.");
                using var reader = new StreamReader(entry.Open());
                return JsonUtility.FromJson<T>(reader.ReadToEnd());
            }
        }

        private const string ReconstructedParticleShader = @"Shader ""SR/Reconstructed Particle""
{
    Properties
    {
        _MainTex (""Main Texture"", 2D) = ""white"" {}
        _MaskTex (""Mask Texture"", 2D) = ""white"" {}
        _NoiseTex (""Noise Texture"", 2D) = ""gray"" {}
        _DisTex (""Dissolve Texture"", 2D) = ""white"" {}
        _MainColor (""Main Color"", Color) = (1,1,1,1)
        _InsideColor (""Inside Color"", Color) = (1,1,1,1)
        _OutSideColor (""Outside Color"", Color) = (1,1,1,1)
        _MainColorScale (""Main Color Scale"", Float) = 1
        _EmissionIntensity (""Emission Intensity"", Float) = 1
        _AlwaysOnTop (""Always On Top"", Float) = 0
        _CL (""CL"", Float) = 0
        _CL2 (""CL2"", Float) = 0
        _CustomData (""Custom Data"", Float) = 0
        _CustomDstBlend (""Custom Dst Blend"", Float) = 0
        _CustomSrcBlend (""Custom Src Blend"", Float) = 0
        _DecalClip (""Decal Clip"", Float) = 0
        _DECALMASK (""Decal Mask"", Float) = 0
        _DECALNOISE (""Decal Noise"", Float) = 0
        _DisTexG (""Dissolve Texture G"", Float) = 0
        _IsPerParticle (""Per Particle"", Float) = 0
        _MASKCHANEL (""Mask Channel Legacy"", Float) = 0
        _MaskON (""Mask Enabled"", Float) = 0
        _NoiseSwitch (""Noise Switch"", Float) = 0
        _RenderingMode (""Rendering Mode"", Float) = 0
        _Saturate (""Saturate"", Float) = 0
        _SoftFar (""Soft Particle Far"", Float) = 0
        _Stencil (""Stencil"", Float) = 0
        _StencilComp (""Stencil Comparison"", Float) = 8
        _TurnOnAnnularUV (""Annular UV"", Float) = 0
        _MainSpeed (""Main Speed"", Vector) = (0,0,0,0)
        _MaskSpeed (""Mask Speed"", Vector) = (0,0,0,0)
        _NoiseSpeed (""Noise Speed"", Vector) = (0,0,0,0)
        _NoiseSpeed2 (""Noise Speed 2"", Vector) = (0,0,0,0)
        _NoiseSpeedG (""Noise Speed G"", Vector) = (0,0,0,0)
        _DisGSpeed (""Dissolve G Speed"", Vector) = (0,0,0,0)
        _DisRSpeed (""Dissolve R Speed"", Vector) = (0,0,0,0)
        _DisStep (""Dissolve Step"", Vector) = (0,0,0,0)
        _CustomUV (""Custom UV"", Vector) = (0,0,0,0)
        _MainChannel (""Main Channel"", Vector) = (1,0,0,0)
        _MainChannelRGB (""Main Channel RGB"", Vector) = (1,1,1,0)
        _MaskChannel (""Mask Channel"", Vector) = (1,0,0,0)
        _MaskUVoffset (""Mask UV Offset"", Vector) = (0,0,0,0)
        _MidColor (""Mid Color"", Color) = (1,1,1,1)
        _Mid (""Dissolve Mid"", Range(0,1)) = 0
        _SmoothStep (""Dissolve Smoothness"", Vector) = (0.1,0,0,0)
        _EnableClip (""Enable Clip"", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend (""Src Blend"", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend (""Dst Blend"", Float) = 10
        [Enum(Off,0,On,1)] _ZWrite (""ZWrite"", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull (""Cull"", Float) = 0
    }
    SubShader
    {
        Tags { ""Queue""=""Transparent"" ""RenderType""=""Transparent"" ""RenderPipeline""=""UniversalPipeline"" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Cull [_Cull]
        Pass
        {
            Name ""SRReconstructedParticle""
            Tags { ""LightMode""=""SRPDefaultUnlit"" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            sampler2D _MainTex, _MaskTex, _NoiseTex, _DisTex;
            float4 _MainTex_ST, _MainColor, _MainSpeed, _MaskSpeed, _NoiseSpeed, _SmoothStep;
            float _MainColorScale, _EmissionIntensity, _Mid, _EnableClip;
            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color;
                return output;
            }
            fixed4 frag(v2f input) : SV_Target
            {
                float2 mainUv = input.uv + _MainSpeed.xy * _Time.y;
                fixed4 main = tex2D(_MainTex, mainUv);
                fixed mask = tex2D(_MaskTex, input.uv + _MaskSpeed.xy * _Time.y).r;
                fixed noise = tex2D(_NoiseTex, input.uv + _NoiseSpeed.xy * _Time.y).r;
                fixed dissolve = tex2D(_DisTex, input.uv).r;
                fixed4 color = main * input.color * _MainColor * max(_MainColorScale * _EmissionIntensity, 0.0001);
                color.a *= mask;
                if (_EnableClip > 0.5) clip(dissolve + noise * _SmoothStep.x - _Mid);
                return color;
            }
            ENDHLSL
        }
    }
}";
    }
}
