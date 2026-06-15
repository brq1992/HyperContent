using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Saves Build Manifest during Full Build and loads it during Update Build.
    /// The manifest records per-asset hash + bundle assignment for change detection.
    /// </summary>
    public static class BuildManifestManager
    {
        /// <summary>
        /// Save Build Manifest after a Full Build.
        /// Records every asset's GUID, hash, bundle assignment, internalId, and dependency hashes.
        /// Must be called ONLY during Full Build; the manifest is never regenerated during Update Build.
        /// </summary>
        public static bool Save(BuildContext pContext)
        {
            try
            {
                var config = pContext.Config;
                var manifestPath = config.BuildManifestPath;
                var bundleDir = config.BundleOutputDirectory;

                // Audit log: per CONTENT_UPDATE_BUILD_FLOW.md L30-31, this MUST be called only from
                // a Full Build (DefaultBuildExecutor.Step 7). If you see this triggered from an Update
                // Build path, the stack trace below identifies the offending caller.
                Debug.Log($"[HyperContent][ManifestSave] BuildManifestManager.Save() called — " +
                    $"will write {manifestPath} with buildVersion='{config.ResolvedBuildVersion}'.\n" +
                    $"Caller stack:\n{System.Environment.StackTrace}");

                var manifest = new BuildManifest
                {
                    buildVersion = config.ResolvedBuildVersion,
                    buildTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                // Per-asset snapshot: must include every asset in AssetToBundle so manifest matches plan.
                // GuidToPath may miss entries for dependency-only assets (AddDependenciesToBundles adds to AssetToBundle but not GuidToPath).
                foreach (var kvp in pContext.AssetToBundle)
                {
                    var assetGuid = kvp.Key;
                    var bundleName = kvp.Value;

                    if (!pContext.GuidToPath.TryGetValue(assetGuid, out var assetPath))
                        assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        Debug.LogError($"[HyperContent] Manifest skip: no path for guid {assetGuid}");
                        continue;
                    }

                    var hash = AssetDatabase.GetAssetDependencyHash(assetPath);
                    var internalId = BundleAssetInternalId.FromAssetPath(assetPath);

                    var depStateList = new List<AssetState>();
                    if (pContext.Dependencies.TryGetValue(assetGuid, out var depGuids))
                    {
                        foreach (var depGuid in depGuids)
                        {
                            var depPath = AssetDatabase.GUIDToAssetPath(depGuid);
                            if (string.IsNullOrEmpty(depPath)) continue;
                            var depHash = AssetDatabase.GetAssetDependencyHash(depPath);
                            depStateList.Add(new AssetState
                            {
                                guid = depGuid.ToLowerInvariant(),
                                hash = depHash.ToString()
                            });
                        }
                    }

                    // Asset-level dependency bundle names (ordered, owning LAST) for Update Build restore.
                    List<string> depBundleNames = null;
                    if (pContext.AssetDependencyBundles != null
                        && pContext.AssetDependencyBundles.TryGetValue(assetGuid.ToLowerInvariant(), out var adb)
                        && adb != null)
                    {
                        depBundleNames = new List<string>(adb);
                    }

                    manifest.cachedAssets.Add(new CachedAssetState
                    {
                        guid = assetGuid.ToLowerInvariant(),
                        hash = hash.ToString(),
                        bundleName = bundleName,
                        internalId = internalId,
                        dependencies = depStateList,
                        dependencyBundleNames = depBundleNames ?? new List<string>()
                    });
                }

                // Per-bundle snapshot
                foreach (var kvp in pContext.BundleToAssets)
                {
                    var bundleName = kvp.Key;
                    var assetGuids = kvp.Value;

                    var bundleFileName = ResolveBundleFileName(pContext, bundleName);
                    var bundlePath = Path.Combine(bundleDir, bundleFileName).Replace("\\", "/");

                    long fileSize = 0;
                    string bundleHash = "";
                    if (File.Exists(bundlePath))
                    {
                        fileSize = new FileInfo(bundlePath).Length;
                        bundleHash = ComputeSHA256(bundlePath);
                    }
                    else
                    {
                        Debug.LogWarning($"[HyperContent] Bundle file not found for manifest: {bundlePath}");
                    }

                    manifest.cachedBundles.Add(new CachedBundleState
                    {
                        bundleName = bundleName,
                        bundleHash = bundleHash,
                        size = fileSize,
                        assetGuids = assetGuids.Select(g => g.ToLowerInvariant()).ToList()
                    });
                }

                var dir = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonUtility.ToJson(manifest, true);
                File.WriteAllText(manifestPath, json);

                Debug.Log($"[HyperContent] Build manifest saved: {manifestPath} " +
                    $"({manifest.cachedAssets.Count} assets, {manifest.cachedBundles.Count} bundles)");
                return true;
            }
            catch (Exception e)
            {
                pContext.Errors.Add(new BuildError($"Failed to save build manifest: {e.Message}"));
                Debug.LogError($"[HyperContent] Build manifest save failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Load Build Manifest for Update Build.
        /// Returns null and adds error if manifest not found or invalid.
        /// </summary>
        public static BuildManifest Load(BuildConfig pConfig, List<BuildError> pErrorList = null)
        {
            var manifestPath = pConfig.BuildManifestPath;

            if (!File.Exists(manifestPath))
            {
                var msg = $"Build manifest not found at {manifestPath}. Run Full Build first.";
                pErrorList?.Add(new BuildError(msg));
                Debug.LogError($"[HyperContent] {msg}");
                return null;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonUtility.FromJson<BuildManifest>(json);

                if (manifest == null || manifest.cachedAssets == null)
                {
                    var msg = $"Build manifest is invalid or empty: {manifestPath}";
                    pErrorList?.Add(new BuildError(msg));
                    Debug.LogError($"[HyperContent] {msg}");
                    return null;
                }

                Debug.Log($"[HyperContent][ManifestLoad] Build manifest loaded: {manifestPath} " +
                    $"(v{manifest.buildVersion}, {manifest.cachedAssets.Count} assets, " +
                    $"{manifest.cachedBundles.Count} bundles). " +
                    $"Update Build will reuse buildVersion='{manifest.buildVersion}' for remote catalog filename.");
                return manifest;
            }
            catch (Exception e)
            {
                var msg = $"Failed to load build manifest: {e.Message}";
                pErrorList?.Add(new BuildError(msg));
                Debug.LogError($"[HyperContent] {msg}");
                return null;
            }
        }

        private static string ResolveBundleFileName(BuildContext pContext, string pBundleName)
        {
            if (pContext.ExpectedToActualBundleName != null &&
                pContext.ExpectedToActualBundleName.TryGetValue(pBundleName, out var actual))
                return actual;
            return pBundleName.EndsWith(".bundle") ? pBundleName : pBundleName + ".bundle";
        }

        private static string ComputeSHA256(string pFilePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(pFilePath))
            {
                var hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
