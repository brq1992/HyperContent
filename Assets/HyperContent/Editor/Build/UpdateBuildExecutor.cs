using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// IBuildExecutor implementation for Update Build.
    /// Implements full-context build + revert per CONTENT_UPDATE_BUILD_FLOW.md Phase B–D.
    /// </summary>
    public class UpdateBuildExecutor : IBuildExecutor
    {
        public string ExecutorName => "Update Build Executor";

        public string Description => "Builds incremental content updates using full-context build + revert.";

        public BuildResult Execute(BuildPlan pPlan, BuildConfig pConfig)
        {
            var compressionError = BuildPlanCompressionValidator.ValidatePlan(pPlan);
            if (compressionError != null)
            {
                var failContext = new BuildContext
                {
                    Config = pConfig,
                    Errors = { new BuildError(compressionError) }
                };
                return BuildResult.Failure(failContext, compressionError);
            }

            var context = new BuildContext
            {
                Config = pConfig,
                AssetMarkers = pPlan.AssetMarkers,
                KeyToGuid = pPlan.KeyToGuid,
                GuidToPath = pPlan.GuidToPath,
                BundleToAssets = pPlan.BundleToAssets,
                AssetToBundle = pPlan.AssetToBundle,
                Dependencies = pPlan.Dependencies,
                BundleDependencies = pPlan.BundleDependencies,
                BundleCompression = new Dictionary<string, BundleCompressionType>(pPlan.BundleCompression, StringComparer.OrdinalIgnoreCase),
                BundleTagFlagsFromPlan = BuildContext.CloneBundleTagFlagsFromPlan(pPlan.BundleTagFlagsFromPlan),
                Errors = pPlan.Errors,
                Warnings = pPlan.Warnings,
                Report = pConfig.generateReport ? new BuildReport() : null
            };

            try
            {
                // Phase A: Change Detection
                Debug.Log("[HyperContent] Update Build — Phase A: Change Detection");
                var changeResult = ContentChangeDetector.DetectChanges(pConfig, pPlan, context.Errors);
                if (changeResult == null)
                    return BuildResult.Failure(context, "Change detection failed (manifest not found)");

                if (!changeResult.HasChanges)
                {
                    Debug.Log("[HyperContent] No changes detected. Update Build skipped.");
                    return BuildResult.Success(context);
                }

                // Phase B1: Assign changed assets to update bundles
                Debug.Log("[HyperContent] Update Build — Phase B1: Assign Update Bundles");
                var buildVersion = pConfig.ResolvedBuildVersion;
                var updateMapping = UpdateBundleAssigner.AssignUpdateBundles(
                    changeResult.expandedChangedAssetList,
                    buildVersion,
                    pConfig.updateBundleGroupingStrategy,
                    pConfig.customUpdateBundleGroupingStrategy);

                if (updateMapping.Count == 0)
                    return BuildResult.Failure(context, "No update bundles assigned");

                LogUpdateMapping(updateMapping);

                IReadOnlyDictionary<string, BundleCompressionType> customCompressionMap = null;
                if (pConfig.updateBundleGroupingStrategy == UpdateBundleGroupingStrategyType.Custom)
                {
                    if (pConfig.customUpdateBundleGroupingStrategy == null)
                    {
                        context.Errors.Add(new BuildError(
                            "updateBundleGroupingStrategy is Custom but customUpdateBundleGroupingStrategy is null."));
                        return BuildResult.Failure(context, "Custom update strategy not configured");
                    }

                    if (!pConfig.customUpdateBundleGroupingStrategy.TryGetUpdateBundleCompressionMap(
                            updateMapping, pPlan, pConfig, out customCompressionMap, out var customCompressionError))
                    {
                        context.Errors.Add(new BuildError(customCompressionError ?? "TryGetUpdateBundleCompressionMap failed."));
                        return BuildResult.Failure(context, "Custom update bundle compression map failed");
                    }

                    if (!ValidateCustomCompressionMatchesUpdateMapping(updateMapping, customCompressionMap, out var keyError))
                    {
                        context.Errors.Add(new BuildError(keyError));
                        return BuildResult.Failure(context, keyError);
                    }
                }

                // Phase B2–B3: Create update layout and full-context build
                Debug.Log("[HyperContent] Update Build — Phase B2-B3: Full-Context Build");
                var updateBundleResult = ExecuteFullContextBuild(context, pPlan, updateMapping, customCompressionMap);
                if (!updateBundleResult)
                    return BuildResult.Failure(context, "Full-context build failed");

                // Phase B4–B5: Revert unchanged + store update bundle info is implicit
                // (we only copy update bundles to ServerData; unchanged bundles stay in StreamingAssets)

                // Load manifest for Phase C baseline (provides catalog buildVersion from last Full Build)
                var manifest = BuildManifestManager.Load(pConfig, context.Errors);
                if (manifest == null)
                    return BuildResult.Failure(context, "Failed to load manifest for catalog generation");

                // Remote catalog uses the same version as the last Full Build so the shipped APK's
                // settings.json (pointing to HyperCatalog_{manifest.buildVersion}.bin) still resolves.
                string catalogBuildVersion = manifest.buildVersion;
                if (string.IsNullOrEmpty(catalogBuildVersion))
                {
                    context.Errors.Add(new BuildError("Build manifest has no buildVersion. Re-run Full Build to regenerate manifest."));
                    return BuildResult.Failure(context, "Manifest buildVersion is missing");
                }

                Debug.Log($"[HyperContent][UpdateBuild] Version source check: " +
                    $"manifest.buildVersion='{catalogBuildVersion}', " +
                    $"config.ResolvedBuildVersion(this run)='{pConfig.ResolvedBuildVersion}'. " +
                    $"Remote catalog filename WILL use manifest.buildVersion → " +
                    $"HyperCatalog_{catalogBuildVersion}.bin (per CONTENT_UPDATE_BUILD_FLOW.md L589).");

                // Phase C: Mixed Catalog Generation
                Debug.Log("[HyperContent] Update Build — Phase C: Mixed Catalog Generation");
                if (!GenerateMixedCatalog(context, manifest, changeResult, updateMapping, buildVersion))
                    return BuildResult.Failure(context, "Mixed catalog generation failed");

                // Phase D: Output — copy update bundles next to remote catalog (.bin/.hash) when enabled
                Debug.Log("[HyperContent] Update Build — Phase D: Output");
                if (!CopyUpdateBundlesToServerData(context, updateMapping))
                    return BuildResult.Failure(context, "Failed to copy update bundles to output folder");

                // Generate settings.json and remote catalog files using last Full Build version
                if (!GenerateUpdateSettings(context, catalogBuildVersion))
                    return BuildResult.Failure(context, "Settings generation failed");

                if (!BuildValidator.ValidateExportedSettingsJson(context))
                    return BuildResult.Failure(context, "settings.json validation failed");

                if (context.Errors.Count > 0)
                    return BuildResult.Failure(context, "Update Build completed with errors");

                if (pConfig.onAfterUpdateBuildSucceeded != null)
                {
                    try
                    {
                        pConfig.onAfterUpdateBuildSucceeded.Invoke(updateMapping, pConfig);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[HyperContent] onAfterUpdateBuildSucceeded failed: {ex.Message}\n{ex.StackTrace}");
                        context.Warnings.Add(new BuildWarning($"Post-update callback failed: {ex.Message}"));
                    }
                }

                Debug.Log("[HyperContent] Update Build completed successfully!");
                return BuildResult.Success(context);
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Update Build exception: {e.Message}"));
                return BuildResult.Failure(context, $"Update Build exception: {e.Message}");
            }
        }

        public List<string> Validate(BuildPlan pPlan, BuildConfig pConfig)
        {
            var errorList = new List<string>();

            if (string.IsNullOrEmpty(pConfig.buildOutputRoot))
                errorList.Add("Build output root cannot be empty");
            if (pPlan.BundleToAssets.Count == 0)
                errorList.Add("Build plan has no bundles");
            if (!File.Exists(pConfig.BuildManifestPath))
                errorList.Add($"Build manifest not found at {pConfig.BuildManifestPath}. Run Full Build first.");

            BuildPlanCompressionValidator.AppendValidationErrors(pPlan, errorList);

            return errorList;
        }

        /// <summary>
        /// Phase B2–B3: Build with full current layout including update groups.
        /// Unity/SBP sees the full bundle graph so unchanged dependencies have known ownership.
        /// </summary>
        private static bool ValidateCustomCompressionMatchesUpdateMapping(
            IReadOnlyDictionary<string, List<ChangedAssetInfo>> pUpdateMapping,
            IReadOnlyDictionary<string, BundleCompressionType> pCompression,
            out string pError)
        {
            pError = null;
            if (pCompression == null)
            {
                pError = "Custom compression map is null.";
                return false;
            }

            foreach (var k in pUpdateMapping.Keys)
            {
                if (!pCompression.ContainsKey(k))
                {
                    pError = $"Custom compression map missing update bundle '{k}'.";
                    return false;
                }
            }

            foreach (var k in pCompression.Keys)
            {
                if (!pUpdateMapping.ContainsKey(k))
                {
                    pError = $"Custom compression map has unexpected key '{k}' not in update mapping.";
                    return false;
                }
            }

            return true;
        }

        private bool ExecuteFullContextBuild(
            BuildContext pContext,
            BuildPlan pPlan,
            Dictionary<string, List<ChangedAssetInfo>> pUpdateMapping,
            IReadOnlyDictionary<string, BundleCompressionType> pCustomCompressionMap)
        {
            var bundleDir = pContext.Config.BundleOutputDirectory;
            if (!Directory.Exists(bundleDir))
                Directory.CreateDirectory(bundleDir);

            // Build the full current layout: original bundles + update bundles.
            // allBundleBuilds carries physical ".bundle" names for SBP; logicalBundleNames
            // carries extensionless names for TryMerge / BundleCompression lookups.
            var allBundleBuilds = new List<AssetBundleBuild>();
            var logicalBundleNames = new List<string>();

            // Collect GUIDs that are moved to update bundles
            var movedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in pUpdateMapping)
            {
                foreach (var asset in kvp.Value)
                    movedGuids.Add(asset.guid);
            }

            // Original bundles (with changed assets removed)
            foreach (var kvp in pPlan.BundleToAssets)
            {
                var bundleName = kvp.Key;
                var assetGuids = kvp.Value;

                var remainingPaths = new List<string>();
                var remainingNames = new List<string>();

                foreach (var guid in assetGuids)
                {
                    if (movedGuids.Contains(guid)) continue;
                    if (!pPlan.GuidToPath.TryGetValue(guid, out var assetPath)) continue;

                    remainingPaths.Add(assetPath);
                    remainingNames.Add(BundleAssetInternalId.FromAssetPath(assetPath));
                }

                if (remainingPaths.Count > 0)
                {
                    // ".bundle" suffix → APK packer skips deflate (see DefaultBuildExecutor).
                    allBundleBuilds.Add(new AssetBundleBuild
                    {
                        assetBundleName = bundleName + NamingRules.BUNDLE_FILE_EXTENSION,
                        assetNames = remainingPaths.ToArray(),
                        addressableNames = remainingNames.ToArray()
                    });
                    logicalBundleNames.Add(bundleName);
                }
            }

            // Update bundles (with changed assets)
            foreach (var kvp in pUpdateMapping)
            {
                var updateBundleName = kvp.Key;
                var changedAssetList = kvp.Value;

                var pathList = new List<string>();
                var nameList = new List<string>();

                foreach (var asset in changedAssetList)
                {
                    var assetPath = asset.assetPath;
                    if (string.IsNullOrEmpty(assetPath))
                        assetPath = AssetDatabase.GUIDToAssetPath(asset.guid);
                    if (string.IsNullOrEmpty(assetPath)) continue;

                    pathList.Add(assetPath);
                    nameList.Add(BundleAssetInternalId.FromAssetPath(assetPath));
                }

                if (pathList.Count > 0)
                {
                    // ".bundle" suffix → APK packer skips deflate (see DefaultBuildExecutor).
                    allBundleBuilds.Add(new AssetBundleBuild
                    {
                        assetBundleName = updateBundleName + NamingRules.BUNDLE_FILE_EXTENSION,
                        assetNames = pathList.ToArray(),
                        addressableNames = nameList.ToArray()
                    });
                    logicalBundleNames.Add(updateBundleName);
                }
            }

            if (allBundleBuilds.Count == 0)
            {
                pContext.Errors.Add(new BuildError("No bundles to build in full-context build"));
                return false;
            }

            if (!UpdateBuildFullContextCompression.TryMerge(
                    pPlan,
                    pContext.Config,
                    pContext.Config.updateBundleGroupingStrategy,
                    pCustomCompressionMap,
                    logicalBundleNames,
                    pUpdateMapping,
                    out var mergedCompression,
                    out var mergeError))
            {
                pContext.Errors.Add(new BuildError(mergeError));
                return false;
            }

            // SBP-generated synthetic bundles (monoscripts / unitybuiltinassets) need entries in
            // the compression map and BundleToAssets so the same catalog/report flow used by
            // Full Build also works here. See DefaultBuildExecutor.EnsureSpecialBundleRegistered.
            DefaultBuildExecutor.EnsureSpecialBundleRegistered(pContext, DefaultBuildExecutor.MONOSCRIPTS_BUNDLE_NAME);
            DefaultBuildExecutor.EnsureSpecialBundleRegistered(pContext, DefaultBuildExecutor.BUILTIN_ASSETS_BUNDLE_NAME);
            if (!mergedCompression.ContainsKey(DefaultBuildExecutor.MONOSCRIPTS_BUNDLE_NAME))
                mergedCompression[DefaultBuildExecutor.MONOSCRIPTS_BUNDLE_NAME] = BundleCompressionType.Lz4;
            if (!mergedCompression.ContainsKey(DefaultBuildExecutor.BUILTIN_ASSETS_BUNDLE_NAME))
                mergedCompression[DefaultBuildExecutor.BUILTIN_ASSETS_BUNDLE_NAME] = BundleCompressionType.Lz4;

            // Full-context build via SBP ContentPipeline
            var buildTarget = pContext.Config.buildTarget;
            var buildGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var buildParams = new HyperContentBundleBuildParameters(
                buildTarget,
                buildGroup,
                bundleDir,
                mergedCompression,
                true,
                pContext.Config.stripUnityVersionFromBundleHeaders);

            var buildContent = new BundleBuildContent(allBundleBuilds.ToArray());
            var buildTasks = DefaultBuildExecutor.CreateBuildTaskListForUpdate();
            var writeDataCapture = new HyperContentBundleWriteDataCapture();

            IBundleBuildResults results;
            var exitCode = ContentPipeline.BuildAssetBundles(buildParams, buildContent, out results, buildTasks, writeDataCapture);

            if (exitCode < ReturnCode.Success)
            {
                pContext.Errors.Add(new BuildError($"SBP ContentPipeline build failed: {exitCode}"));
                return false;
            }

            // Move SBP build log out of bundle directory to {buildOutputRoot}/{Platform}
            DefaultBuildExecutor.MoveSbpBuildLogToPlatformOutput(pContext);

            // Store actual bundle names from SBP results
            var builtBundles = results.BundleInfos.Keys.ToArray();
            foreach (var built in builtBundles)
            {
                pContext.ExpectedToActualBundleName[built] = built;
                var noExt = built.EndsWith(".bundle")
                    ? built.Substring(0, built.Length - ".bundle".Length)
                    : built;
                pContext.ExpectedToActualBundleName[noExt] = built;
            }

            // Rebuild BundleDependencies from SBP results (transitive + direct)
            DefaultBuildExecutor.RebuildBundleDependenciesFromSbpResults(pContext, results, writeDataCapture.WriteData);

            // Asset-level dependency bundles for CHANGED/NEW assets built this round (owning LAST).
            // Unchanged assets are restored from the manifest in GenerateMixedCatalog instead.
            DefaultBuildExecutor.BuildAssetDependencyBundlesFromSbp(pContext, writeDataCapture.WriteData, writeDataCapture.DependencyData);

            // Diagnostic-only subset check over this round's changed/new assets (does not block the build).
            DefaultBuildExecutor.ValidateAssetDepsSubsetOfBundleClosure(pContext);

            BundlePackedAssetsFromSbp.TryPopulatePackedAssetPaths(pContext, writeDataCapture.WriteData);

            Debug.Log($"[HyperContent] Full-context build completed: {builtBundles.Length} bundles built");
            return true;
        }

        /// <summary>
        /// Phase C: Generate mixed catalog from original Full Build baseline.
        /// Unchanged → StreamingAssets (3), changed/new → Remote (2).
        /// Rebuilt from scratch each Update Build (no patching previous catalog).
        /// </summary>
        private bool GenerateMixedCatalog(
            BuildContext pContext,
            BuildManifest pManifest,
            ChangeDetectionResult pChangeResult,
            Dictionary<string, List<ChangedAssetInfo>> pUpdateMapping,
            string pBuildVersion)
        {
            try
            {
                var bundleDir = pContext.Config.BundleOutputDirectory;
                var catalogDir = pContext.Config.CatalogOutputDirectory;
                if (!Directory.Exists(catalogDir))
                    Directory.CreateDirectory(catalogDir);

                var stringList = new List<string>();
                var stringToIndex = new Dictionary<string, int>();

                int GetOrAddString(string s)
                {
                    if (string.IsNullOrEmpty(s)) return -1;
                    if (stringToIndex.TryGetValue(s, out var idx)) return idx;
                    idx = stringList.Count;
                    stringList.Add(s);
                    stringToIndex[s] = idx;
                    return idx;
                }

                // Build set of changed/new GUIDs for quick lookup
                var changedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in pChangeResult.modifiedAssetList) changedGuids.Add(m.guid);
                foreach (var n in pChangeResult.newAssetList) changedGuids.Add(n.guid);
                // Also include expanded set
                foreach (var e in pChangeResult.expandedChangedAssetList) changedGuids.Add(e.guid);

                // Build GUID → update bundle name mapping
                var guidToUpdateBundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in pUpdateMapping)
                {
                    foreach (var asset in kvp.Value)
                        guidToUpdateBundle[asset.guid] = kvp.Key;
                }

                // Build manifest GUID → CachedAssetState lookup
                var manifestGuidToAsset = new Dictionary<string, CachedAssetState>(StringComparer.OrdinalIgnoreCase);
                foreach (var cachedAsset in pManifest.cachedAssets)
                    manifestGuidToAsset[cachedAsset.guid] = cachedAsset;

                // Build manifest bundle name → CachedBundleState lookup
                var manifestBundleLookup = new Dictionary<string, CachedBundleState>(StringComparer.OrdinalIgnoreCase);
                foreach (var cachedBundle in pManifest.cachedBundles)
                    manifestBundleLookup[cachedBundle.bundleName] = cachedBundle;

                // Collect all bundle names (original + update)
                var allBundleNames = new List<string>();
                var bundleNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Original bundles from manifest
                foreach (var cb in pManifest.cachedBundles)
                {
                    if (!bundleNameToIndex.ContainsKey(cb.bundleName))
                    {
                        bundleNameToIndex[cb.bundleName] = allBundleNames.Count;
                        allBundleNames.Add(cb.bundleName);
                        GetOrAddString(cb.bundleName);
                    }
                }

                // Update bundles
                foreach (var updateBundleName in pUpdateMapping.Keys)
                {
                    if (!bundleNameToIndex.ContainsKey(updateBundleName))
                    {
                        bundleNameToIndex[updateBundleName] = allBundleNames.Count;
                        allBundleNames.Add(updateBundleName);
                        GetOrAddString(updateBundleName);
                    }
                }

                // Build guid→key for nameAlias generation
                var guidToKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in pContext.KeyToGuid)
                    guidToKey[kvp.Value.ToLowerInvariant()] = kvp.Key;

                // Also add keys from manifest for assets not in current plan
                foreach (var ca in pManifest.cachedAssets)
                {
                    if (!guidToKey.ContainsKey(ca.guid))
                        guidToKey[ca.guid] = ca.internalId;
                }

                // Build asset records: entry-level only (KeyToGuid), same rule as Full Build CatalogGenerator.
                // Catalog stays minimal and only exposes addressable resources.
                var assetList = new List<CatalogSchema.AssetRecordEntry>();
                foreach (var kvp in pContext.KeyToGuid)
                {
                    var guidStr = kvp.Value;
                    if (!IsValid32HexGuid(guidStr))
                        continue;
                    var guid = guidStr.ToLowerInvariant();

                    string bundleName;
                    if (guidToUpdateBundle.TryGetValue(guid, out var updateBundle))
                        bundleName = updateBundle;
                    else if (manifestGuidToAsset.TryGetValue(guid, out var cached))
                        bundleName = cached.bundleName; // Unchanged → original bundle
                    else if (!pContext.AssetToBundle.TryGetValue(guidStr, out bundleName))
                        continue; // New entry must be in current plan

                    if (!bundleNameToIndex.TryGetValue(bundleName, out var bundleIndex))
                        continue;

                    string internalId;
                    if (pContext.GuidToPath.TryGetValue(guidStr, out var assetPath))
                        internalId = BundleAssetInternalId.FromAssetPath(assetPath);
                    else if (manifestGuidToAsset.TryGetValue(guid, out var c))
                        internalId = c.internalId;
                    else
                        continue;

                    var assetPathIndex = GetOrAddString(internalId);

                    // Asset-level dependency bundles (post-order, owning LAST).
                    // Changed/new → this build's SBP write-data (pContext.AssetDependencyBundles).
                    // Unchanged   → manifest snapshot (cached.dependencyBundleNames), since its SBP
                    //               write-data is not regenerated this round.
                    List<string> depBundleNames = null;
                    if (pContext.AssetDependencyBundles != null
                        && pContext.AssetDependencyBundles.TryGetValue(guid, out var ctxDepNames)
                        && ctxDepNames != null && ctxDepNames.Count > 0)
                    {
                        depBundleNames = ctxDepNames;
                    }
                    else if (manifestGuidToAsset.TryGetValue(guid, out var cachedForDeps)
                             && cachedForDeps.dependencyBundleNames != null
                             && cachedForDeps.dependencyBundleNames.Count > 0)
                    {
                        depBundleNames = cachedForDeps.dependencyBundleNames;
                    }

                    var depBundleIndices = ResolveDepBundleIndices(depBundleNames, bundleNameToIndex, guid);

                    assetList.Add(new CatalogSchema.AssetRecordEntry
                    {
                        guid = guid,
                        bundleIndex = bundleIndex,
                        assetPathIndex = assetPathIndex,
                        dependencyBundles = depBundleIndices
                    });
                }
                // Removed entries are simply not added.

                assetList.Sort((a, b) => string.CompareOrdinal(a.guid, b.guid));

                // Name aliases
                var nameAliasList = new List<CatalogSchema.NameAliasEntry>();
                for (int i = 0; i < assetList.Count; i++)
                {
                    if (!guidToKey.TryGetValue(assetList[i].guid, out var name))
                        continue;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsValid32HexGuid(name)) continue;

                    var nameHash = NameHashUtil.Compute(name);
                    if (string.IsNullOrEmpty(nameHash)) continue;

                    nameAliasList.Add(new CatalogSchema.NameAliasEntry
                    {
                        nameStringIndex = GetOrAddString(name),
                        nameHash = nameHash,
                        guidIndex = i
                    });
                }
                nameAliasList.Sort((a, b) => string.CompareOrdinal(a.nameHash, b.nameHash));

                var bundleTagByName = CatalogGenerator.BuildBundleTagFlagsByBundleName(pContext, allBundleNames, pManifest);

                // Bundle records
                var bundleRecordList = new List<CatalogSchema.BundleRecordEntry>();
                for (int bi = 0; bi < allBundleNames.Count; bi++)
                {
                    var bn = allBundleNames[bi];
                    var bundleNameIndex = GetOrAddString(bn);
                    bool isUpdateBundle = pUpdateMapping.ContainsKey(bn);

                    string bundleHash;
                    long fileSize;
                    int assetCount;
                    var depIndices = new List<int>();
                    string diskFileName = null;

                    if (isUpdateBundle)
                    {
                        // Update bundle: read from build output
                        diskFileName = ResolveBundleFileName(pContext, bn);
                        var filePath = Path.Combine(bundleDir, diskFileName).Replace("\\", "/");
                        bundleHash = File.Exists(filePath) ? ComputeSHA256(filePath) : "";
                        fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
                        assetCount = pUpdateMapping[bn].Count;

                        // Bundle dependencies from current build context
                        if (pContext.BundleDependencies.TryGetValue(bn, out var deps))
                        {
                            foreach (var dep in deps)
                            {
                                if (bundleNameToIndex.TryGetValue(dep, out var depIdx))
                                    depIndices.Add(depIdx);
                            }
                        }
                    }
                    else
                    {
                        // Original bundle from manifest
                        if (manifestBundleLookup.TryGetValue(bn, out var cachedBundle))
                        {
                            bundleHash = cachedBundle.bundleHash;
                            fileSize = cachedBundle.size;
                            assetCount = cachedBundle.assetGuids.Count;
                        }
                        else
                        {
                            bundleHash = "";
                            fileSize = 0;
                            assetCount = 0;
                        }

                        if (pContext.BundleDependencies.TryGetValue(bn, out var deps))
                        {
                            foreach (var dep in deps)
                            {
                                if (bundleNameToIndex.TryGetValue(dep, out var depIdx))
                                    depIndices.Add(depIdx);
                            }
                        }
                    }

                    // Remote rows: catalog string is extensionless; physical file on CDN is {name}.bundle.
                    int remoteRelativePathIndex = isUpdateBundle && !string.IsNullOrEmpty(diskFileName)
                        ? GetOrAddString(CatalogRemoteRelativePathFromUpdateDiskFileName(diskFileName))
                        : -1;

                    bundleRecordList.Add(new CatalogSchema.BundleRecordEntry
                    {
                        bundleNameIndex = bundleNameIndex,
                        bundleHash = bundleHash,
                        size = fileSize,
                        dependencies = depIndices,
                        assetCount = assetCount,
                        contentLocation = isUpdateBundle ? 2 : 3, // Remote : StreamingAssets
                        bundleTagFlags = (int)bundleTagByName[bn],
                        remoteRelativePathIndex = remoteRelativePathIndex
                    });
                }

                var catalogNameIndex = GetOrAddString(HyperContentPaths.LOCAL_CATALOG_NAME);

                var catalog = new CatalogSchema
                {
                    schemaVersion = CatalogSchema.CurrentSchemaVersion,
                    catalogNameIndex = catalogNameIndex,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    stringTable = stringList.ToArray(),
                    assetRecords = assetList,
                    nameAliases = nameAliasList,
                    bundleRecords = bundleRecordList
                };

                // catalogHash 是对 schema 内容的指纹，与外层封装格式无关：
                // 两轮 Serialize 都按同一 format（来自 BuildConfig）走，hot-update hash 比对稳定。
                var format = pContext.Config.catalogFormat;
                var bytes = CatalogGenerator.Serialize(catalog, format);
                catalog.catalogHash = ComputeCatalogHash(bytes);
                bytes = CatalogGenerator.Serialize(catalog, format);

                // Write catalog (fixed name for package/runtime)
                var catalogPath = Path.Combine(catalogDir, HyperContentPaths.LOCAL_CATALOG_FILENAME);
                File.WriteAllBytes(catalogPath, bytes);

                Debug.Log($"[HyperContent] Mixed catalog generated (format={format}): {catalogPath} — " +
                    $"{assetList.Count} assets, {nameAliasList.Count} nameAliases, " +
                    $"{bundleRecordList.Count} bundles " +
                    $"({bundleRecordList.Count(r => r.contentLocation == 2)} Remote, " +
                    $"{bundleRecordList.Count(r => r.contentLocation == 3)} StreamingAssets), " +
                    $"size={bytes.Length} bytes");

                return true;
            }
            catch (Exception e)
            {
                pContext.Errors.Add(new BuildError($"Mixed catalog generation failed: {e.Message}"));
                Debug.LogError($"[HyperContent] Mixed catalog generation failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Copy update bundles from build output for CDN/upload.
        /// When <see cref="BuildConfig.buildRemoteCatalog"/> is true: same directory as remote catalog
        /// (<c>HyperCatalog_*.bin</c> / <c>.hash</c>) via <see cref="BuildConfig.GetResolvedRemoteCatalogBuildFolder"/> — one output folder for upload.
        /// Otherwise: legacy <see cref="BuildConfig.ServerDataOutputDirectory"/> (<c>ServerData/{Platform}/Bundles</c>).
        /// </summary>
        private bool CopyUpdateBundlesToServerData(
            BuildContext pContext,
            Dictionary<string, List<ChangedAssetInfo>> pUpdateMapping)
        {
            try
            {
                var config = pContext.Config;
                // Same folder as GenerateUpdateSettings remote .bin/.hash when enabled; else legacy Bundles path.
                string serverDir = config.buildRemoteCatalog
                    ? BuildConfig.GetResolvedRemoteCatalogBuildFolder(config.remoteCatalogBuildFolder, config.buildTarget)
                    : config.ServerDataOutputDirectory;
                if (!Directory.Exists(serverDir))
                    Directory.CreateDirectory(serverDir);

                var bundleDir = pContext.Config.BundleOutputDirectory;

                foreach (var updateBundleName in pUpdateMapping.Keys)
                {
                    var fileName = ResolveBundleFileName(pContext, updateBundleName);
                    var sourcePath = Path.Combine(bundleDir, fileName).Replace("\\", "/");
                    var destPath = Path.Combine(serverDir, fileName).Replace("\\", "/");

                    if (File.Exists(sourcePath))
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        Debug.Log($"[HyperContent] Update bundle copied: {sourcePath} → {destPath}");

                        // Remove update bundle from StreamingAssets — it should only exist on CDN/server
                        try
                        {
                            File.Delete(sourcePath);
                            var metaPath = sourcePath + ".meta";
                            if (File.Exists(metaPath))
                                File.Delete(metaPath);
                            Debug.Log($"[HyperContent] Removed update bundle from StreamingAssets: {sourcePath}");
                        }
                        catch (Exception deleteEx)
                        {
                            Debug.LogWarning($"[HyperContent] Failed to remove update bundle from StreamingAssets: {deleteEx.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[HyperContent] Update bundle not found: {sourcePath}");
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                pContext.Errors.Add(new BuildError($"Failed to copy update bundles: {e.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Generate settings.json with remote catalog URLs for the Update Build.
        /// </summary>
        private bool GenerateUpdateSettings(BuildContext pContext, string pBuildVersion)
        {
            try
            {
                var config = pContext.Config;
                var catalogDir = config.CatalogOutputDirectory;
                var catalogPath = Path.Combine(catalogDir, HyperContentPaths.LOCAL_CATALOG_FILENAME);

                Debug.Log($"[HyperContent][UpdateBuild] GenerateUpdateSettings: " +
                    $"settings.buildVersion='{pBuildVersion}' (from manifest), " +
                    $"target settings.json='{Path.Combine(catalogDir, HyperContentPaths.SETTINGS_FILENAME)}'");

                var settings = new RuntimeSettings
                {
                    buildVersion = pBuildVersion,
                    buildTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    localCatalogPath = HyperContentPaths.LOCAL_CATALOG_FILENAME,
                    catalogRequestTimeout = config.catalogRequestTimeout,
                    catalogFormat = (int)config.catalogFormat,
                    dependencyLoadMode = (int)config.dependencyLoadMode
                };

                // CDN base is runtime-only (SetRemoteBundleBaseUrl); settings.json stays relative-path-only.

                // Remote catalog
                if (config.buildRemoteCatalog)
                {
                    string remoteBinName = $"HyperCatalog_{pBuildVersion}.bin";
                    string remoteHashName = $"HyperCatalog_{pBuildVersion}.hash";

                    // Resolve remote folder to project root: ServerData/Production/{Platform}
                    string remoteBuildFolder = BuildConfig.GetResolvedRemoteCatalogBuildFolder(config.remoteCatalogBuildFolder, config.buildTarget);
                    if (!Directory.Exists(remoteBuildFolder))
                        Directory.CreateDirectory(remoteBuildFolder);

                    string remoteBinPath = Path.Combine(remoteBuildFolder, remoteBinName);
                    File.Copy(catalogPath, remoteBinPath, overwrite: true);

                    byte[] catalogBytes = File.ReadAllBytes(catalogPath);
                    string catalogHash = ComputeCatalogHash(catalogBytes);
                    string remoteHashPath = Path.Combine(remoteBuildFolder, remoteHashName);
                    File.WriteAllText(remoteHashPath, catalogHash);

                    settings.remoteCatalogRelativePath = remoteBinName;
                    settings.remoteCatalogHashRelativePath = remoteHashName;
                    settings.cachedCatalogPath = remoteBinName;
                    settings.cachedCatalogHashPath = remoteHashName;

                    Debug.Log($"[HyperContent][UpdateBuild] Remote catalog written: " +
                        $"{Path.GetFullPath(remoteBinPath)} (hash={catalogHash}). " +
                        $"Filename derived from manifest.buildVersion → must match shipped APK's settings.json.");
                }

                string settingsJson = JsonUtility.ToJson(settings, prettyPrint: true);
                string settingsPath = Path.Combine(catalogDir, HyperContentPaths.SETTINGS_FILENAME);
                File.WriteAllText(settingsPath, settingsJson);
                Debug.Log($"[HyperContent] settings.json written: {settingsPath}");

                return true;
            }
            catch (Exception e)
            {
                pContext.Errors.Add(new BuildError($"Settings generation failed: {e.Message}"));
                return false;
            }
        }

        private static string ResolveBundleFileName(BuildContext pContext, string pBundleName)
        {
            if (pContext.ExpectedToActualBundleName != null &&
                pContext.ExpectedToActualBundleName.TryGetValue(pBundleName, out var actual))
                return actual;
            return pBundleName.EndsWith(".bundle") ? pBundleName : pBundleName + ".bundle";
        }

        private static string CatalogRemoteRelativePathFromUpdateDiskFileName(string diskFileName)
        {
            if (string.IsNullOrEmpty(diskFileName))
                throw new InvalidOperationException("Update bundle disk file name is empty.");
            if (!diskFileName.EndsWith(NamingRules.BUNDLE_FILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "HyperContent Update Build: bundle output file name must end with '" +
                    NamingRules.BUNDLE_FILE_EXTENSION + "'. Got: '" + diskFileName + "'");
            return diskFileName.Substring(0, diskFileName.Length - NamingRules.BUNDLE_FILE_EXTENSION.Length);
        }

        private static void LogUpdateMapping(Dictionary<string, List<ChangedAssetInfo>> pMapping)
        {
            Debug.Log($"[HyperContent] Update bundle assignment: {pMapping.Count} bundles");
            foreach (var kvp in pMapping)
            {
                Debug.Log($"  {kvp.Key}: {kvp.Value.Count} assets");
                foreach (var asset in kvp.Value)
                {
                    Debug.Log($"    - {asset.guid} ({asset.assetPath})");
                }
            }
        }

        /// <summary>
        /// Maps an ordered list of dependency bundle NAMES (post-order, owning LAST) to catalog bundle
        /// indices via <paramref name="pBundleNameToIndex"/>. Preserves order. An unmapped name is dropped
        /// with a warning; if nothing maps, returns <c>null</c> (the runtime then fails the load in
        /// AssetLevel mode rather than loading the wrong closure).
        /// </summary>
        private static List<int> ResolveDepBundleIndices(
            List<string> pNames,
            Dictionary<string, int> pBundleNameToIndex,
            string pGuidForLog)
        {
            if (pNames == null || pNames.Count == 0)
                return null;

            var indices = new List<int>(pNames.Count);
            foreach (var name in pNames)
            {
                if (string.IsNullOrEmpty(name))
                    continue;
                if (pBundleNameToIndex.TryGetValue(name, out int idx))
                    indices.Add(idx);
                else
                    Debug.LogWarning($"[HyperContent] UpdateBuild: asset '{pGuidForLog}' dependency bundle " +
                                     $"'{name}' not found in catalog bundle index — dropped from asset-level deps");
            }
            return indices.Count > 0 ? indices : null;
        }

        private static bool IsValid32HexGuid(string pStr)
        {
            if (string.IsNullOrEmpty(pStr) || pStr.Length != 32) return false;
            for (int i = 0; i < 32; i++)
            {
                char c = pStr[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
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

        private static string ComputeCatalogHash(byte[] pData)
        {
            if (pData == null || pData.Length == 0) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(pData);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
