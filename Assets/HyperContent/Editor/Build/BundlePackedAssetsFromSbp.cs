using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Passed into <see cref="ContentPipeline.BuildAssetBundles"/> so a post task can store SBP write data for reporting.
    /// </summary>
    public sealed class HyperContentBundleWriteDataCapture : IContextObject
    {
        public IBundleWriteData WriteData { get; set; }

        /// <summary>
        /// SBP object-level dependency analysis (<c>CalculateAssetDependencyData</c> output). Provides
        /// <c>AssetInfo[guid].referencedObjects</c> / <c>SceneInfo[guid].referencedObjects</c>, the same
        /// data SBP uses to pack bundles. Consumed by
        /// <see cref="DefaultBuildExecutor.BuildAssetDependencyBundlesFromSbp"/> to build the in-bundle
        /// entry→entry reference graph (the one-hop SBP boundary that <c>AssetToFiles</c> cannot expose),
        /// replacing a separate <c>AssetDatabase.GetDependencies</c> pass.
        /// </summary>
        public IDependencyData DependencyData { get; set; }
    }

    /// <summary>
    /// Runs after SBP writing; copies <see cref="IBundleWriteData"/> for bundle report (full packed asset list)
    /// and <see cref="IDependencyData"/> for the asset-level dependency entry graph.
    /// </summary>
    internal sealed class HyperContentCaptureBundleWriteDataTask : IBuildTask
    {
        public int Version => 1;

#pragma warning disable 649
        [InjectContext]
        IBundleWriteData _writeData;

        [InjectContext(ContextUsage.In)]
        IDependencyData _dependencyData;

        [InjectContext(ContextUsage.In)]
        HyperContentBundleWriteDataCapture _capture;
#pragma warning restore 649

        public ReturnCode Run()
        {
            if (_capture != null)
            {
                _capture.WriteData = _writeData;
                _capture.DependencyData = _dependencyData;
            }
            return ReturnCode.Success;
        }
    }

    /// <summary>
    /// Fills <see cref="BuildContext.BundleToPackedAssetPaths"/> from SBP <see cref="IWriteData.AssetToFiles"/> /
    /// <see cref="IBundleWriteData.FileToBundle"/> (same source as Addressables build layout).
    /// </summary>
    public static class BundlePackedAssetsFromSbp
    {
        /// <summary>
        /// Map SBP bundle identifiers (as in <see cref="IBundleWriteData.FileToBundle"/>) to expected HyperContent bundle names.
        /// </summary>
        public static Dictionary<string, string> BuildSbpNameToExpectedMap(BuildContext context)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (context?.BundleToAssets == null)
                return dict;
            foreach (var expectedName in context.BundleToAssets.Keys)
            {
                if (string.IsNullOrEmpty(expectedName))
                    continue;
                dict[expectedName] = expectedName;
                dict[expectedName + ".bundle"] = expectedName;
            }
            return dict;
        }

        /// <summary>
        /// Populate packed paths per bundle; no-op if <paramref name="writeData"/> is null.
        /// </summary>
        public static void TryPopulatePackedAssetPaths(BuildContext context, IBundleWriteData writeData)
        {
            if (context == null || writeData == null)
                return;

            var assetToFiles = writeData.AssetToFiles;
            var fileToBundle = writeData.FileToBundle;
            if (assetToFiles == null || assetToFiles.Count == 0 || fileToBundle == null || fileToBundle.Count == 0)
            {
                Debug.LogWarning("[HyperContent] SBP write data has no AssetToFiles/FileToBundle — bundle report will list root assets only.");
                return;
            }

            var sbpToExpected = BuildSbpNameToExpectedMap(context);
            var byBundle = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in assetToFiles)
            {
                var guid = kvp.Key;
                var files = kvp.Value;
                if (files == null || files.Count == 0)
                    continue;

                var primaryFile = files[0];
                if (string.IsNullOrEmpty(primaryFile) || !fileToBundle.TryGetValue(primaryFile, out var sbpBundleName))
                    continue;

                if (!TryMapSbpBundleToExpected(sbpBundleName, sbpToExpected, out var expectedBundleName))
                    continue;

                var path = ResolveAssetPath(context, guid);
                if (string.IsNullOrEmpty(path))
                    continue;

                if (!byBundle.TryGetValue(expectedBundleName, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    byBundle[expectedBundleName] = set;
                }
                set.Add(path);
            }

            context.BundleToPackedAssetPaths = byBundle;
            Debug.Log($"[HyperContent] Bundle report: packed asset paths from SBP for {byBundle.Count} bundles (includes implicit dependencies).");
        }

        private static bool TryMapSbpBundleToExpected(
            string sbpBundleName,
            Dictionary<string, string> sbpToExpected,
            out string expectedName)
        {
            expectedName = null;
            if (string.IsNullOrEmpty(sbpBundleName) || sbpToExpected == null)
                return false;
            if (sbpToExpected.TryGetValue(sbpBundleName, out expectedName))
                return true;
            if (sbpBundleName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = sbpBundleName.Substring(0, sbpBundleName.Length - ".bundle".Length);
                if (sbpToExpected.TryGetValue(stripped, out expectedName))
                    return true;
            }
            return false;
        }

        private static string ResolveAssetPath(BuildContext context, GUID guid)
        {
            var s = guid.ToString();
            if (context.GuidToPath != null && context.GuidToPath.TryGetValue(s, out var path) && !string.IsNullOrEmpty(path))
                return path;
            path = AssetDatabase.GUIDToAssetPath(s);
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }
}
