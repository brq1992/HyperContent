using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using com.igg.hypercontent.runtime;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Copies HyperContent catalog + settings to StreamingAssets/hc/ during Player Build.
    /// Bundles are already in Assets/StreamingAssets/{Platform}/Bundles/ and need no mapping.
    /// Path definitions: see CONVENTIONS.md §3 (canonical source).
    /// </summary>
    public class HyperContentPlayerBuildProcessor : BuildPlayerProcessor
    {
        public override int callbackOrder => 2;

        public override void PrepareForBuild(BuildPlayerContext pBuildPlayerContext)
        {
            Debug.Log("[HyperContent] ========== PlayerBuildProcessor.PrepareForBuild START ==========");
            Debug.Log($"[HyperContent] BuildTarget: {EditorUserBuildSettings.activeBuildTarget}");

            if (pBuildPlayerContext == null)
            {
                Debug.LogError("[HyperContent] PlayerBuild: BuildPlayerContext is null!");
                return;
            }

            string catalogBuildPath = HyperContentPaths.BuildCatalogPath;
            Debug.Log($"[HyperContent] Catalog build path: {catalogBuildPath}");
            Debug.Log($"[HyperContent] Catalog dir exists: {Directory.Exists(catalogBuildPath)}");

            if (Directory.Exists(catalogBuildPath))
            {
                pBuildPlayerContext.AddAdditionalPathToStreamingAssets(
                    catalogBuildPath, HyperContentPaths.STREAMING_ASSETS_SUBFOLDER);
                Debug.Log($"[HyperContent] Mapping: {catalogBuildPath} → StreamingAssets/{HyperContentPaths.STREAMING_ASSETS_SUBFOLDER}");

                ValidateCriticalFiles(catalogBuildPath);
            }
            else
            {
                Debug.LogError($"[HyperContent] Catalog build directory does NOT exist: {catalogBuildPath}");
                Debug.LogError("[HyperContent] Please run HyperContent Build first (Menu: HyperContent/Build)");
            }

            string bundlePath = HyperContentPaths.BuildBundlePath;
            Debug.Log($"[HyperContent] Bundle path: {bundlePath}");
            Debug.Log($"[HyperContent] Bundle dir exists: {Directory.Exists(bundlePath)}");

            if (!Directory.Exists(bundlePath))
            {
                Debug.LogWarning($"[HyperContent] Bundle directory not found: {bundlePath}");
            }
            else
            {
                var bundleFileList = Directory.GetFiles(bundlePath, "*.bundle");
                Debug.Log($"[HyperContent] Bundles found: {bundleFileList.Length}");
            }

            Debug.Log("[HyperContent] ========== PlayerBuildProcessor.PrepareForBuild END ==========");
        }

        private static void ValidateCriticalFiles(string pCatalogBuildPath)
        {
            string settingsFile = Path.Combine(pCatalogBuildPath, HyperContentPaths.SETTINGS_FILENAME);
            string catalogFile = Path.Combine(pCatalogBuildPath, HyperContentPaths.LOCAL_CATALOG_FILENAME);

            if (!File.Exists(settingsFile))
                Debug.LogError($"[HyperContent] CRITICAL: {HyperContentPaths.SETTINGS_FILENAME} NOT FOUND at {settingsFile}");
            else
                Debug.Log($"[HyperContent] OK {HyperContentPaths.SETTINGS_FILENAME} found ({new FileInfo(settingsFile).Length} bytes)");

            if (!File.Exists(catalogFile))
                Debug.LogError($"[HyperContent] CRITICAL: {HyperContentPaths.LOCAL_CATALOG_FILENAME} NOT FOUND at {catalogFile}");
            else
                Debug.Log($"[HyperContent] OK {HyperContentPaths.LOCAL_CATALOG_FILENAME} found ({new FileInfo(catalogFile).Length} bytes)");
        }
    }
}
