using System;
using System.Collections.Generic;
using System.IO;
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
            var manifestPath = EditorUtility.OpenFilePanel("Select SR dependencies.json", string.Empty, "json");
            if (string.IsNullOrEmpty(manifestPath))
                return;

            Directory.CreateDirectory(ToAbsolutePath(DefaultOutputFolder));
            var manifest = ReadJson<Manifest>(manifestPath);
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
            var manifest = ReadJson<Manifest>(manifestPath);
            if (manifest.Nodes == null || manifest.Nodes.Length == 0)
                throw new InvalidDataException("The manifest contains no nodes.");
            if (!outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("The output path must be under Assets/.", nameof(outputAssetPath));

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty;
            var nodes = manifest.Nodes.OrderBy(node => PathDepth(node.Path)).ToArray();
            var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var warnings = new List<string>();
            var lightCount = 0;

            foreach (var node in nodes)
            {
                var gameObject = new GameObject(node.Name);
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
                        ApplyTransform(gameObject.transform, ReadComponent<TransformData>(manifestDirectory, component.ParametersFile));
                    else if (component.Type == "Light" && !string.IsNullOrEmpty(component.ParametersFile))
                    {
                        ApplyLight(gameObject.AddComponent<Light>(), ReadComponent<LightData>(manifestDirectory, component.ParametersFile));
                        lightCount++;
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

            var report = new ImportReport
            {
                SourceManifest = Path.GetFullPath(manifestPath),
                SourceUnityVersion = manifest.UnityVersion,
                OutputPrefab = outputAssetPath,
                NodeCount = nodes.Length,
                ReconstructedLightCount = lightCount,
                Warnings = warnings.ToArray(),
            };
            File.WriteAllText(ToAbsolutePath(Path.ChangeExtension(outputAssetPath, ".import-report.json")), JsonUtility.ToJson(report, true));
            AssetDatabase.Refresh();
            Selection.activeObject = prefab;
            Debug.Log($"Rebuilt SR effect prefab '{outputAssetPath}' with {nodes.Length} nodes and {lightCount} Light component(s). " +
                      $"See the adjacent import report for {warnings.Count} preserved SR extension warning(s).");
            return prefab;
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

        private static void SetFinite(float value, Action<float> setter)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value))
                setter(value);
        }

        private static T ReadComponent<T>(string manifestDirectory, string relativePath)
        {
            return ReadJson<T>(Path.Combine(manifestDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static T ReadJson<T>(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("SR prefab manifest data was not found.", path);
            return JsonUtility.FromJson<T>(File.ReadAllText(path));
        }

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
        private sealed class ImportReport
        {
            public string SourceManifest;
            public string SourceUnityVersion;
            public string OutputPrefab;
            public int NodeCount;
            public int ReconstructedLightCount;
            public string[] Warnings;
        }
    }
}
