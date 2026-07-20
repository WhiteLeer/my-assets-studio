using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using SevenZip;
using System;

namespace AnimeStudio
{
    public static class AnimationClipExtensions
    {
        public static float DefaultFloatWeight => 1.0f / 3.0f;
        public static Vector3 DefaultVector3Weight => new Vector3(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f);
        public static Quaternion DefaultQuaternionWeight => new Quaternion(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f);

        #region AnimationClip
        public static IEnumerable<GameObject> FindRoots(this AnimationClip clip)
        {
            foreach (var asset in clip.assetsFile.assetsManager.assetsFileList.SelectMany(x => x.Objects))
            {
                switch (asset.type)
                {
                    case ClassIDType.Animator:
                        if (asset is Animator animator && clip.IsAnimatorContainsClip(animator))
                        {
                            if (animator.m_GameObject.TryGet(out var go))
                            {
                                yield return go;
                            }
                        }
                        break;

                    case ClassIDType.Animation:
                        if (asset is Animation animation && clip.IsAnimationContainsClip(animation))
                        {
                            if (animation.m_GameObject.TryGet(out var go))
                            {
                                yield return go;
                            }
                        }
                        break;
                }
            }

            yield break;
        }
        public static Dictionary<uint, string> FindTOS(this AnimationClip clip)
        {
            var tos = new Dictionary<uint, string>() { { 0, string.Empty } };
            var loadedAssets = clip.assetsFile.assetsManager.assetsFileList
                .SelectMany(x => x.Objects)
                .OrderBy(x => x.type)
                .ToArray();
            foreach (var asset in loadedAssets)
            {
                switch (asset.type)
                {
                    case ClassIDType.Avatar:
                        if (asset is Avatar avatar && clip.AddAvatarTOS(avatar, tos))
                        {
                            return tos;
                        }
                        break;
                    case ClassIDType.Animator:
                        if (asset is Animator animator && clip.IsAnimatorContainsClip(animator))
                        {
                            if (clip.AddAnimatorTOS(animator, tos))
                            {
                                return tos;
                            }
                        }
                        break;
                    case ClassIDType.Animation:
                        if (asset is Animation animation && clip.IsAnimationContainsClip(animation))
                        {
                            if (clip.AddAnimationTOS(animation, tos))
                            {
                                return tos;
                            }
                        }
                        break;
                }
            }

            // Timeline/effect clips are often not referenced by an Animation component.
            // Their hashes can still be recovered from loaded root GameObject hierarchies.
            foreach (var gameObject in loadedAssets.OfType<GameObject>())
            {
                var transform = gameObject.m_Transform;
                if (transform == null || !transform.m_Father.IsNull)
                    continue;

                if (clip.AddTOS(gameObject.BuildTOS(), tos))
                    return tos;
            }

            clip.AddTOS(BuildAllRelativeTOS(loadedAssets.OfType<Transform>()), tos);
            return tos;
        }
        private static bool AddAvatarTOS(this AnimationClip clip, Avatar avatar, Dictionary<uint, string> tos)
        {
            if (avatar?.m_TOS == null)
                return false;

            return clip.AddTOS(avatar.m_TOS.ToDictionary(x => x.Key, x => x.Value), tos);
        }
        private static bool AddAnimatorTOS(this AnimationClip clip, Animator animator, Dictionary<uint, string> tos)
        {
            if (animator == null)
                return false;

            if (animator.m_Avatar.TryGet(out var avatar))
            {
                if (clip.AddAvatarTOS(avatar, tos))
                {
                    return true;
                }
            }

            Dictionary<uint, string> animatorTOS = animator.BuildTOS();
            return clip.AddTOS(animatorTOS, tos);
        }
        private static bool AddAnimationTOS(this AnimationClip clip, Animation animation, Dictionary<uint, string> tos)
        {
            if (animation == null)
                return false;

            if (animation.m_GameObject.TryGet(out var go))
            {
                Dictionary<uint, string> animationTOS = go.BuildTOS();
                return clip.AddTOS(animationTOS, tos);
            }
            return false;
        }
        private static bool AddTOS(this AnimationClip clip, Dictionary<uint, string> src, Dictionary<uint, string> dest)
        {
            if (src == null)
                return false;

            int tosCount = clip.m_ClipBindingConstant.genericBindings.Count;
            for (int i = 0; i < tosCount; i++)
            {
                var binding = clip.m_ClipBindingConstant.genericBindings[i];
                if (src.TryGetValue(binding.path, out string path))
                {
                    dest[binding.path] = path;
                    if (dest.Count == tosCount)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        private static bool IsAnimationContainsClip(this AnimationClip clip, Animation animation)
        {
            return animation.IsContainsAnimationClip(clip);
        }
        private static bool IsAnimatorContainsClip(this AnimationClip clip, Animator animator)
        {
            if (animator.m_Controller.TryGet(out var runtime))
            {
                return runtime.IsContainsAnimationClip(clip);
            }
            else
            {
                return false;
            }
        }
        public static string Convert(this AnimationClip clip)
        {
            ExpandCurves(clip);
            return ConvertSerializedAnimationClip(clip);
        }
        public static string ConvertCombined(this AnimationClip overlay, AnimationClip body)
        {
            ExpandCurves(body);
            ExpandCurves(overlay);
            overlay.m_RotationCurves = MergeCurves(body.m_RotationCurves, overlay.m_RotationCurves, x => x.path);
            overlay.m_EulerCurves = MergeCurves(body.m_EulerCurves, overlay.m_EulerCurves, x => x.path);
            overlay.m_PositionCurves = MergeCurves(body.m_PositionCurves, overlay.m_PositionCurves, x => x.path);
            overlay.m_ScaleCurves = MergeCurves(body.m_ScaleCurves, overlay.m_ScaleCurves, x => x.path);
            overlay.m_FloatCurves = MergeCurves(body.m_FloatCurves, overlay.m_FloatCurves,
                x => $"{x.path}\0{x.attribute}\0{(int)x.classID}");
            overlay.m_PPtrCurves = MergeCurves(body.m_PPtrCurves, overlay.m_PPtrCurves,
                x => $"{x.path}\0{x.attribute}\0{x.classID}");
            return ConvertSerializedAnimationClip(overlay);
        }
        private static void ExpandCurves(AnimationClip clip)
        {
            if (!clip.m_Legacy || clip.m_MuscleClip != null)
            {
                var converter = AnimationClipConverter.Process(clip);
                clip.m_RotationCurves = converter.Rotations.Union(clip.m_RotationCurves).ToList();
                clip.m_EulerCurves = converter.Eulers.Union(clip.m_EulerCurves).ToList();
                clip.m_PositionCurves = converter.Translations.Union(clip.m_PositionCurves).ToList();
                clip.m_ScaleCurves = converter.Scales.Union(clip.m_ScaleCurves).ToList();
                clip.m_FloatCurves = converter.Floats.Union(clip.m_FloatCurves).ToList();
                clip.m_PPtrCurves = converter.PPtrs.Union(clip.m_PPtrCurves).ToList();
            }
        }
        private static List<T> MergeCurves<T>(IEnumerable<T> body, IEnumerable<T> overlay, Func<T, string> getKey)
        {
            var overlayCurves = overlay.ToList();
            var overlayKeys = overlayCurves.Select(getKey).ToHashSet(StringComparer.Ordinal);
            return body.Where(x => !overlayKeys.Contains(getKey(x))).Concat(overlayCurves).ToList();
        }
        public static string ConvertSerializedAnimationClip(AnimationClip animationClip)
        {
            var sb = new StringBuilder();
            using (var stringWriter = new StringWriter(sb))
            {
                YAMLWriter writer = new YAMLWriter();
                YAMLDocument doc = ExportYAMLDocument(animationClip);
                writer.AddDocument(doc);
                writer.Write(stringWriter);
                return sb.ToString();
            }
        }

        public static YAMLDocument ExportYAMLDocument(AnimationClip animationClip)
        {
            var document = new YAMLDocument();
            var root = document.CreateMappingRoot();
            root.Tag = ((int)ClassIDType.AnimationClip).ToString();
            root.Anchor = ((int)ClassIDType.AnimationClip * 100000).ToString();
            var node = animationClip.ExportYAML(animationClip.version);
            root.Add(ClassIDType.AnimationClip.ToString(), node);
            return document;
        }
        public static YAMLMappingNode ExportYAML(this AnimationClip clip, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add(nameof(clip.m_Name), clip.m_Name);
            node.AddSerializedVersion(ToSerializedVersion(version));
            node.Add(nameof(clip.m_Legacy), clip.m_Legacy);
            node.Add(nameof(clip.m_Compressed), clip.m_Compressed);
            node.Add(nameof(clip.m_UseHighQualityCurve), clip.m_UseHighQualityCurve);
            node.Add(nameof(clip.m_RotationCurves), clip.m_RotationCurves.ExportYAML(version));
            node.Add(nameof(clip.m_CompressedRotationCurves), clip.m_CompressedRotationCurves.ExportYAML(version));
            node.Add(nameof(clip.m_EulerCurves), clip.m_EulerCurves.ExportYAML(version));
            node.Add(nameof(clip.m_PositionCurves), clip.m_PositionCurves.ExportYAML(version));
            node.Add(nameof(clip.m_ScaleCurves), clip.m_ScaleCurves.ExportYAML(version));
            node.Add(nameof(clip.m_FloatCurves), clip.m_FloatCurves.ExportYAML(version));
            node.Add(nameof(clip.m_PPtrCurves), clip.m_PPtrCurves.ExportYAML(version));
            node.Add(nameof(clip.m_SampleRate), clip.m_SampleRate);
            node.Add(nameof(clip.m_WrapMode), clip.m_WrapMode);
            node.Add(nameof(clip.m_Bounds), clip.m_Bounds.ExportYAML(version));
            node.Add(nameof(clip.m_ClipBindingConstant), clip.m_ClipBindingConstant.ExportYAML(version));
            node.Add("m_AnimationClipSettings", clip.m_MuscleClip != null ? clip.m_MuscleClip.ExportYAML(version) : new YAMLMappingNode());
            node.Add(nameof(clip.m_Events), clip.m_Events.ExportYAML(version));
            return node;
        }

        public static int ToSerializedVersion(int[] version)
        {
            if (version[0] >= 5)
            {
                return 6;
            }
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3))
            {
                return 4;
            }
            if (version[0] > 2 || (version[0] == 2 && version[1] >= 6))
            {
                return 3;
            }
            return 2;
        }
        #endregion

        #region Others
        private static bool IsContainsAnimationClip(this Animation animation, AnimationClip clip)
        {
            if (animation?.m_Animations == null)
                return false;

            foreach (PPtr<AnimationClip> ptr in animation.m_Animations)
            {
                if (ptr.TryGet(out var animationClip) && animationClip.Equals(clip))
                {
                    return true;
                }
            }
            return false;
        }
        
        private static Dictionary<uint, string> BuildTOS(this Animator animator)
        {
            if (animator.version[0] > 4 || (animator.version[0] == 4 && animator.version[1] >= 3))
            {
                if (animator.m_HasTransformHierarchy)
                {
                    if (animator.m_GameObject.TryGet(out var go))
                    {
                        return go.BuildTOS();
                    }
                }
                else
                {
                    return new Dictionary<uint, string>() { { 0, string.Empty } };
                }
            }
            else
            {
                if (animator.m_GameObject.TryGet(out var go))
                {
                    return go.BuildTOS();
                }
            }
            return null;
        }
        private static Dictionary<uint, string> BuildTOS(this GameObject gameObject)
        {
            Dictionary<uint, string> tos = new Dictionary<uint, string>() { { 0, string.Empty } };
            if (gameObject?.m_Transform == null)
                return tos;

            gameObject.BuildTOS(string.Empty, tos);
            return tos;
        }
        private static void BuildTOS(this GameObject parent, string parentPath, Dictionary<uint, string> tos)
        {
            Transform transform = parent.m_Transform;
            if (transform?.m_Children == null)
                return;

            foreach (PPtr<Transform> childPtr in transform.m_Children)
            {
                if (childPtr.TryGet(out var childTransform))
                {
                    if (childTransform.m_GameObject.TryGet(out var child))
                    {
                        string path = parentPath != string.Empty ? parentPath + '/' + child.m_Name : child.m_Name;
                        var pathHash = CRC.CalculateDigestUTF8(path);
                        tos[pathHash] = path;
                        BuildTOS(child, path, tos);
                    }
                }
            }
        }

        private static Dictionary<uint, string> BuildAllRelativeTOS(IEnumerable<Transform> transforms)
        {
            var tos = new Dictionary<uint, string>();
            foreach (var transform in transforms)
            {
                if (!transform.m_GameObject.TryGet(out var gameObject))
                    continue;

                var path = gameObject.m_Name;
                var father = transform.m_Father;
                var visited = new HashSet<long>();
                while (!father.IsNull && father.TryGet(out var parent) && visited.Add(parent.m_PathID))
                {
                    tos[CRC.CalculateDigestUTF8(path)] = path;
                    if (!parent.m_GameObject.TryGet(out var parentGameObject))
                        break;

                    path = parentGameObject.m_Name + '/' + path;
                    father = parent.m_Father;
                }
            }
            return tos;
        }

        
        private static bool IsContainsAnimationClip(this RuntimeAnimatorController runtimeAnimatorController, AnimationClip clip)
        {
            if (runtimeAnimatorController is AnimatorController animatorController)
            {
                foreach (PPtr<AnimationClip> ptr in animatorController.m_AnimationClips)
                {
                    if (ptr.TryGet(out var animationClip) && animationClip.Equals(clip))
                    {
                        return true;
                    }
                }
            }
            else if (runtimeAnimatorController is AnimatorOverrideController overrideController)
            {
                foreach (var clipOverride in overrideController.m_Clips)
                {
                    if ((clipOverride.m_OriginalClip.TryGet(out var originalClip) && originalClip.Equals(clip)) ||
                        (clipOverride.m_OverrideClip.TryGet(out var overrideClip) && overrideClip.Equals(clip)))
                    {
                        return true;
                    }
                }

                if (overrideController.m_Controller.TryGet(out var baseController))
                {
                    return baseController.IsContainsAnimationClip(clip);
                }
            }
            return false;
        }
        public static int GetDimension(this GenericBinding binding)
        {
            return binding.attribute == 2 ? 4 : 3;
        }

        public static HumanoidMuscleType GetHumanoidMuscle(this GenericBinding binding)
        {
            return ((HumanoidMuscleType)binding.attribute).Update(binding.version);
        }
        #endregion
    }

    public enum BindingCustomType : byte
    {
        None = 0,
        Transform = 4,
        AnimatorMuscle = 8,

        BlendShape = 20,
        Renderer = 21,
        RendererMaterial = 22,
        SpriteRenderer = 23,
        MonoBehaviour = 24,
        Light = 25,
        RendererShadows = 26,
        ParticleSystem = 27,
        RectTransform = 28,
        LineRenderer = 29,
        TrailRenderer = 30,
        PositionConstraint = 31,
        RotationConstraint = 32,
        ScaleConstraint = 33,
        AimConstraint = 34,
        ParentConstraint = 35,
        LookAtConstraint = 36,
        Camera = 37,
    }
}
