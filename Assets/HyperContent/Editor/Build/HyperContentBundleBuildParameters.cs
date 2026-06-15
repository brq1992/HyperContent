using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEngine;
using BuildCompression = UnityEngine.BuildCompression;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// SBP bundle parameters with per-bundle compression (same extension point as Addressables <c>AddressableAssetsBundleBuildParameters</c>).
    /// </summary>
    public sealed class HyperContentBundleBuildParameters : BundleBuildParameters
    {
        readonly Dictionary<string, BuildCompression> _compressionByIdentifier;
        readonly bool _stripUnityVersion;

        public HyperContentBundleBuildParameters(
            BuildTarget pTarget,
            BuildTargetGroup pTargetGroup,
            string pOutputFolder,
            IReadOnlyDictionary<string, BundleCompressionType> pPerBundleCompression,
            bool pUseCache,
            bool pStripUnityVersion = false)
            : base(pTarget, pTargetGroup, pOutputFolder)
        {
            UseCache = pUseCache;
            _stripUnityVersion = pStripUnityVersion;
            _compressionByIdentifier = BundleCompressionTypeMapper.BuildIdentifierToCompressionMap(pPerBundleCompression);

            ApplyOptionalAddressablesGlobalFlags();

            // Default field used if GetCompressionForIdentifier is not called for an id (should not happen after validation).
            BundleCompression = BuildCompression.LZ4;
        }

        void ApplyOptionalAddressablesGlobalFlags()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null)
            {
                ContiguousBundles = settings.ContiguousBundles;
                DisableVisibleSubAssetRepresentations = settings.DisableVisibleSubAssetRepresentations;
#if NONRECURSIVE_DEPENDENCY_DATA
                NonRecursiveDependencies = settings.NonRecursiveBuilding;
#endif
            }

            // StripUnityVersion: Addressables exposes the matching toggle as internal on AddressableAssetSettings
            // (StripUnityVersionFromBundleBuild) and defaults to false. Historically HyperContent hardcoded this to
            // true, producing bundles without the Unity-version chunk (confirmed via AssetRipper comparison vs.
            // Addressables-built bundles). Keep it driven by BuildConfig so we can A/B the difference.
            if (_stripUnityVersion)
                ContentBuildFlags |= ContentBuildFlags.StripUnityVersion;
            else
                ContentBuildFlags &= ~ContentBuildFlags.StripUnityVersion;
        }

        public override BuildCompression GetCompressionForIdentifier(string identifier)
        {
            if (!string.IsNullOrEmpty(identifier))
            {
                if (_compressionByIdentifier.TryGetValue(identifier, out var bc))
                    return bc;
                // Try alternate form (with/without .bundle)
                if (identifier.EndsWith(".bundle", System.StringComparison.OrdinalIgnoreCase))
                {
                    var noExt = identifier.Substring(0, identifier.Length - ".bundle".Length);
                    if (_compressionByIdentifier.TryGetValue(noExt, out bc))
                        return bc;
                }
                else
                {
                    var withExt = identifier + ".bundle";
                    if (_compressionByIdentifier.TryGetValue(withExt, out bc))
                        return bc;
                }
            }

            UnityEngine.Debug.LogError(
                $"[HyperContent] No compression mapping for SBP bundle identifier '{identifier}'. " +
                "Ensure BuildPlan.BundleCompression covers every bundle name.");
            return base.GetCompressionForIdentifier(identifier);
        }
    }
}
