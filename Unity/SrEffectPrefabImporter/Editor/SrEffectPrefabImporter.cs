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
                            renderer.enabled = component.ParticleRenderer.Enabled;
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
            serialized.ApplyModifiedPropertiesWithoutUndo();
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
        }

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

            public void Dispose() => archive?.Dispose();

            private T ReadEntry<T>(string relativePath)
            {
                var entry = archive.GetEntry(relativePath.Replace('\\', '/')) ??
                            throw new InvalidDataException($"Package entry '{relativePath}' was not found.");
                using var reader = new StreamReader(entry.Open());
                return JsonUtility.FromJson<T>(reader.ReadToEnd());
            }
        }
    }
}
