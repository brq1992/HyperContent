using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEngine;
using UnityEngine.Build.Pipeline;
using UnityEngine.U2D;
using UnityEditor.U2D;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Default build executor that uses Unity's AssetBundle system.
    /// Handles bundle building and delegates catalog generation to CatalogGenerator.
    /// </summary>
    public class DefaultBuildExecutor : IBuildExecutor
    {
        /// <summary>
        /// Catalog/runtime bundle name (no extension) for the MonoScript bundle.
        /// Matches Addressables' "*_monoscripts.bundle" concept: all MonoScript stubs are pulled
        /// out of content bundles into this shared bundle, so cross-bundle PPtr&lt;MonoScript&gt;
        /// references resolve against a single loaded bundle instead of each content bundle having
        /// to re-resolve its own inlined MonoScript copies (primary cause of slow LoadAssetAsync
        /// on prefabs heavy with MonoBehaviour components — see the "93s MuseumUI" investigation).
        /// </summary>
        public const string MONOSCRIPTS_BUNDLE_NAME = "monoscripts";

        /// <summary>
        /// Catalog/runtime bundle name (no extension) for the Unity built-in assets bundle
        /// (built-in shaders, default meshes, etc.). Matches Addressables'
        /// "*_unitybuiltinassets.bundle". Without this bundle, built-in assets referenced by
        /// prefabs get duplicated into every content bundle that uses them.
        /// </summary>
        public const string BUILTIN_ASSETS_BUNDLE_NAME = "unitybuiltinassets";

        /// <summary>
        /// Argument passed to <see cref="CreateMonoScriptBundle"/>. MUST end with <c>.bundle</c>
        /// so the file written to StreamingAssets has the <c>.bundle</c> extension; Android's APK
        /// packer skips re-compression for files matching <c>aaptOptions.noCompress = "bundle"</c>,
        /// letting AssetBundle.LoadFromFile* use memory-mapped I/O at runtime instead of paying a
        /// full deflate decompression cost per load (see the "93s MuseumUI" investigation:
        /// unextensioned HC bundles were being deflated inside the APK, making LoadFromFileAsync
        /// ~10× slower and LoadAssetAsync ~100× slower than Addressables).
        /// </summary>
        private const string MONOSCRIPTS_BUNDLE_FILE = MONOSCRIPTS_BUNDLE_NAME + ".bundle";

        /// <summary>
        /// Argument passed to SBP <see cref="CreateBuiltInShadersBundle"/>. See <see cref="MONOSCRIPTS_BUNDLE_FILE"/>
        /// for why we include the <c>.bundle</c> extension.
        /// </summary>
        private const string BUILTIN_ASSETS_BUNDLE_FILE = BUILTIN_ASSETS_BUNDLE_NAME + ".bundle";

        public string ExecutorName => "Default Build Executor";
        
        public string Description => "Builds Unity AssetBundles and generates catalog.";
        
        public BuildResult Execute(BuildPlan plan, BuildConfig config)
        {
            var compressionError = BuildPlanCompressionValidator.ValidatePlan(plan);
            if (compressionError != null)
            {
                var failContext = new BuildContext
                {
                    Config = config,
                    Errors = { new BuildError(compressionError) }
                };
                return BuildResult.Failure(failContext, compressionError);
            }

            var context = new BuildContext
            {
                Config = config,
                AssetMarkers = plan.AssetMarkers,
                KeyToGuid = plan.KeyToGuid,
                GuidToPath = plan.GuidToPath,
                BundleToAssets = plan.BundleToAssets,
                AssetToBundle = plan.AssetToBundle,
                Dependencies = plan.Dependencies,
                BundleDependencies = plan.BundleDependencies,
                BundleCompression = new Dictionary<string, BundleCompressionType>(plan.BundleCompression, StringComparer.OrdinalIgnoreCase),
                BundleTagFlagsFromPlan = BuildContext.CloneBundleTagFlagsFromPlan(plan.BundleTagFlagsFromPlan),
                Errors = plan.Errors,
                Warnings = plan.Warnings,
                Report = config.generateReport ? new BuildReport() : null
            };
            
            try
            {
                // Step 1: Validate before build (don't check bundle files as they don't exist yet)
                if (!BuildValidator.Validate(context, checkBundleFiles: false))
                {
                    return BuildResult.Failure(context, "Build validation failed");
                }
                
                // Step 2: Build bundles
                if (!BuildBundles(context))
                {
                    return BuildResult.Failure(context, "Bundle build failed");
                }
                
                // Step 3: Generate catalog
                if (!CatalogGenerator.GenerateCatalog(context))
                {
                    return BuildResult.Failure(context, "Catalog generation failed");
                }
                
                // Step 4: Final validation
                BuildValidator.Validate(context);
                
                // Step 5: Generate report
                if (config.generateReport)
                {
                    BuildReportGenerator.GenerateReport(context);
                }
                
                // Step 6: Generate settings.json and optional remote catalog
                if (!GenerateSettingsAndRemoteCatalog(context))
                {
                    return BuildResult.Failure(context, "Settings/remote catalog generation failed");
                }

                if (!BuildValidator.ValidateExportedSettingsJson(context))
                {
                    return BuildResult.Failure(context, "settings.json validation failed");
                }
                
                // Step 7: Save Build Manifest (Full Build only — never regenerated during Update Build)
                if (!BuildManifestManager.Save(context))
                {
                    return BuildResult.Failure(context, "Build manifest save failed");
                }
                
                if (context.Errors.Count > 0)
                {
                    return BuildResult.Failure(context, "Build completed with errors");
                }
                
                return BuildResult.Success(context);
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Build exception: {e.Message}"));
                return BuildResult.Failure(context, $"Build exception: {e.Message}");
            }
        }
        
        public List<string> Validate(BuildPlan plan, BuildConfig config)
        {
            var errors = new List<string>();
            
            // Validate build output root
            if (string.IsNullOrEmpty(config.buildOutputRoot))
            {
                errors.Add("Build output root cannot be empty");
            }
            
            // Validate plan has bundles
            if (plan.BundleToAssets.Count == 0)
            {
                errors.Add("Build plan has no bundles to build");
            }

            BuildPlanCompressionValidator.AppendValidationErrors(plan, errors);
            
            return errors;
        }
        
        /// <summary>
        /// Build Unity AssetBundles using full SBP ContentPipeline with custom task list.
        /// Extracts IBundleWriteData to compute accurate bundle dependencies from SBP's
        /// object-level dependency analysis (same approach as Addressables).
        /// </summary>
        private bool BuildBundles(BuildContext context)
        {
            var startTime = DateTime.Now;
            
            var bundleDir = context.Config.BundleOutputDirectory;
            if (!Directory.Exists(bundleDir))
            {
                Directory.CreateDirectory(bundleDir);
            }
            
            // Clear old bundles if force rebuild
            if (context.Config.forceRebuild)
            {
                ClearOldBundles(bundleDir);
            }
            
            // Prepare AssetBundle build map
            var assetBundleBuilds = PrepareAssetBundleBuilds(context);
            
            if (assetBundleBuilds.Count == 0)
            {
                context.Errors.Add(new BuildError("No bundles to build"));
                return false;
            }
            
            // Build via full SBP ContentPipeline (per-bundle compression from BuildPlan / BuildContext)
            var buildTarget = context.Config.buildTarget;
            var buildGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            if (context.BundleCompression == null || context.BundleCompression.Count == 0)
            {
                context.Errors.Add(new BuildError("BuildContext.BundleCompression is empty — grouping tool must fill per-bundle compression."));
                return false;
            }

            // SBP will generate extra bundles via CreateMonoScriptBundle / CreateBuiltInShadersBundle
            // (see CreateBuildTaskListForUpdate). Pre-register them as "known" bundles so the
            // compression map resolves, StoreActualBundleNames maps them, and CatalogGenerator
            // emits BundleRecord entries. They carry no assets themselves.
            EnsureSpecialBundleRegistered(context, MONOSCRIPTS_BUNDLE_NAME);
            EnsureSpecialBundleRegistered(context, BUILTIN_ASSETS_BUNDLE_NAME);

            var buildParams = new HyperContentBundleBuildParameters(
                buildTarget,
                buildGroup,
                bundleDir,
                context.BundleCompression,
                !context.Config.forceRebuild,
                context.Config.stripUnityVersionFromBundleHeaders);

            var buildContent = new BundleBuildContent(assetBundleBuilds.ToArray());
            var buildTasks = CreateBuildTaskListForUpdate();
            var writeDataCapture = new HyperContentBundleWriteDataCapture();

            IBundleBuildResults results;
            var exitCode = ContentPipeline.BuildAssetBundles(buildParams, buildContent, out results, buildTasks, writeDataCapture);

            if (exitCode < ReturnCode.Success)
            {
                context.Errors.Add(new BuildError($"SBP ContentPipeline build failed: {exitCode}"));
                return false;
            }

            // Move SBP build log out of bundle directory to {buildOutputRoot}/{Platform}
            MoveSbpBuildLogToPlatformOutput(context);

            // Store actual bundle names from SBP results
            StoreActualBundleNames(context, results);

            // Rebuild BundleDependencies from SBP WriteData (accurate object-level deps)
            RebuildBundleDependenciesFromSbpResults(context, results, writeDataCapture.WriteData);

            // Compute per-asset dependency bundles for asset-level loading (same SBP source)
            BuildAssetDependencyBundlesFromSbp(context, writeDataCapture.WriteData, writeDataCapture.DependencyData);

            // Diagnostic-only subset check: asset-level deps ⊆ owning bundle's bundle-level closure.
            // Logs violations at Error level but does NOT block the build (see note in the method).
            ValidateAssetDepsSubsetOfBundleClosure(context);

            BundlePackedAssetsFromSbp.TryPopulatePackedAssetPaths(context, writeDataCapture.WriteData);
            
            // Calculate build duration
            var duration = DateTime.Now - startTime;
            if (context.Report != null)
            {
                context.Report.BuildDurationMs = (long)duration.TotalMilliseconds;
                context.Report.BuildTimestamp = DateTime.Now;
            }
            
            return true;
        }

        /// <summary>
        /// Create SBP task list. Mirrors <c>AddressableAssetsBundleCompatible</c> preset plus the
        /// Addressables-specific tasks that extract MonoScripts and Unity built-in assets into
        /// their own shared bundles (see <see cref="CreateMonoScriptBundle"/>,
        /// <see cref="CreateBuiltInShadersBundle"/>; newer SBP may name this <c>CreateBuiltInBundle</c>).
        /// The extraction requires
        /// <see cref="UpdateBundleObjectLayout"/> to run after <see cref="GenerateBundlePacking"/>
        /// so bundle object layouts get re-adjusted after MonoScripts/BuiltIns are pulled out.
        ///
        /// Without these three tasks, every content bundle inlines its own copies of the
        /// referenced MonoScripts, and native <c>AssetBundle.LoadAssetAsync</c> spends tens of
        /// seconds re-resolving them per bundle (confirmed via AssetRipper: HC bundles contained
        /// MonoScript entries that Addressables bundles did not).
        ///
        /// Also used by <see cref="UpdateBuildExecutor"/>.
        /// </summary>
        internal static IList<IBuildTask> CreateBuildTaskListForUpdate()
        {
            var buildTasks = new List<IBuildTask>();

            // Setup
            buildTasks.Add(new SwitchToBuildPlatform());
            buildTasks.Add(new RebuildSpriteAtlasCache());

            // Player Scripts
            buildTasks.Add(new BuildPlayerScripts());
            buildTasks.Add(new PostScriptsCallback());

            // Dependency
            buildTasks.Add(new CalculateSceneDependencyData());
            buildTasks.Add(new CalculateAssetDependencyData());
            buildTasks.Add(new StripUnusedSpriteSources());
            buildTasks.Add(new CreateBuiltInShadersBundle(BUILTIN_ASSETS_BUNDLE_FILE));
            buildTasks.Add(new CreateMonoScriptBundle(MONOSCRIPTS_BUNDLE_FILE));
            buildTasks.Add(new PostDependencyCallback());

            // Packing
            buildTasks.Add(new GenerateBundlePacking());
            buildTasks.Add(new UpdateBundleObjectLayout());
            buildTasks.Add(new GenerateBundleCommands());
            buildTasks.Add(new GenerateSubAssetPathMaps());
            buildTasks.Add(new GenerateBundleMaps());
            buildTasks.Add(new PostPackingCallback());

            // Writing
            buildTasks.Add(new WriteSerializedFiles());
            buildTasks.Add(new ArchiveAndCompressBundles());
            buildTasks.Add(new PostWritingCallback());
            buildTasks.Add(new HyperContentCaptureBundleWriteDataTask());

            return buildTasks;
        }

        /// <summary>
        /// Pre-register an SBP-generated synthetic bundle (monoscripts / unitybuiltinassets) that
        /// does not come from <see cref="BuildPlan"/>. Adds an empty entry in
        /// <see cref="BuildContext.BundleToAssets"/> so catalog generation emits a
        /// <c>BundleRecord</c> for it, and a default compression so
        /// <see cref="HyperContentBundleBuildParameters.GetCompressionForIdentifier"/> resolves.
        /// Idempotent — safe to call from both Full and Update Build paths.
        /// </summary>
        internal static void EnsureSpecialBundleRegistered(BuildContext pContext, string pBundleName)
        {
            if (pContext == null || string.IsNullOrEmpty(pBundleName))
                return;
            if (!pContext.BundleToAssets.ContainsKey(pBundleName))
                pContext.BundleToAssets[pBundleName] = new HashSet<string>();
            if (!pContext.BundleCompression.ContainsKey(pBundleName))
                pContext.BundleCompression[pBundleName] = BundleCompressionType.Lz4;
        }

        /// <summary>
        /// Rebuild bundle-to-bundle dependency maps from SBP results.
        ///
        /// Produces two complementary views, mirroring Addressables'
        /// <c>BuildLayout.Bundle.Dependencies</c> (one-hop) vs <c>ExpandedDependencies</c>
        /// (transitive), so HC's diagnostics can be compared against Addressables on the
        /// same footing without hand-rolling reverse-edge math:
        /// <list type="bullet">
        ///   <item><description><see cref="BuildContext.BundleDependencies"/> — transitive
        ///   closure as produced by SBP <c>BundleDetails.Dependencies</c>
        ///   (<c>ArchiveAndCompressBundles.CalculateBundleDependencies</c> recursively merges
        ///   each direct edge with its callees). Used by catalog generation so the runtime
        ///   preloads every bundle on the chain.</description></item>
        ///   <item><description><see cref="BuildContext.BundleDirectDependencies"/> — one-hop
        ///   only, derived here from SBP <c>IBundleWriteData.AssetToFiles</c>+<c>FileToBundle</c>
        ///   (the exact same input Addressables uses for
        ///   <c>aaContext.bundleToImmediateBundleDependencies</c>). Diagnostics-only; never
        ///   read by the runtime.</description></item>
        /// </list>
        ///
        /// Also used by <see cref="UpdateBuildExecutor"/>.
        /// </summary>
        internal static void RebuildBundleDependenciesFromSbpResults(
            BuildContext pContext,
            IBundleBuildResults pResults,
            IBundleWriteData pWriteData)
        {
            pContext.BundleDependencies.Clear();
            pContext.BundleDirectDependencies.Clear();

            // SBP may return bundle names with ".bundle" extension while BundleToAssets uses
            // bare names. Build a mapping from SBP names to expected names for normalization
            // so that dependencies are not silently dropped.
            var sbpNameToExpected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var expectedName in pContext.BundleToAssets.Keys)
            {
                sbpNameToExpected[expectedName] = expectedName;
                sbpNameToExpected[expectedName + ".bundle"] = expectedName;
            }

            // ── Transitive closure (catalog/runtime) ─────────────────────────────────
            var bundleInfoDict = pResults != null ? pResults.BundleInfos : null;
            if (bundleInfoDict != null)
            {
                foreach (var kvp in bundleInfoDict)
                {
                    var sbpBundleName = kvp.Key;
                    var bundleDetails = kvp.Value;

                    if (bundleDetails.Dependencies == null || bundleDetails.Dependencies.Length == 0)
                        continue;

                    string normalizedName = sbpNameToExpected.TryGetValue(sbpBundleName, out var mapped)
                        ? mapped
                        : sbpBundleName;

                    var depSet = new HashSet<string>();
                    foreach (var dep in bundleDetails.Dependencies)
                    {
                        if (dep == sbpBundleName)
                            continue;
                        if (sbpNameToExpected.TryGetValue(dep, out var mappedDep))
                        {
                            depSet.Add(mappedDep);
                        }
                        else
                        {
                            Debug.LogWarning($"[HyperContent] SBP dependency '{dep}' (from bundle '{sbpBundleName}') " +
                                             "not found in BundleToAssets — skipped (not a known bundle)");
                        }
                    }

                    if (depSet.Count > 0)
                        pContext.BundleDependencies[normalizedName] = depSet;
                }
            }

            // ── Direct (one-hop) edges from WriteData ────────────────────────────────
            // Mirrors Addressables' GenerateLocationListsTask:
            //     bundleEntry.Dependencies.UnionWith(k.Value.Select(x => bundleToEntry[input.FileToBundle[x]]))
            // i.e. for each explicit asset, the bundle owning files[0] gains a direct edge to
            // the bundle owning each remaining file in AssetToFiles[guid].
            if (pWriteData != null && pWriteData.AssetToFiles != null && pWriteData.FileToBundle != null)
            {
                foreach (var kvp in pWriteData.AssetToFiles)
                {
                    var files = kvp.Value;
                    if (files == null || files.Count == 0)
                        continue;

                    var ownerFile = files[0];
                    if (string.IsNullOrEmpty(ownerFile) || !pWriteData.FileToBundle.TryGetValue(ownerFile, out var ownerSbpBundle))
                        continue;
                    if (!sbpNameToExpected.TryGetValue(ownerSbpBundle, out var ownerBundle))
                        continue;

                    HashSet<string> directSet = null;
                    for (int i = 0; i < files.Count; i++)
                    {
                        var depFile = files[i];
                        if (string.IsNullOrEmpty(depFile))
                            continue;
                        if (!pWriteData.FileToBundle.TryGetValue(depFile, out var depSbpBundle))
                            continue;
                        if (!sbpNameToExpected.TryGetValue(depSbpBundle, out var depBundle))
                            continue;
                        if (string.Equals(depBundle, ownerBundle, StringComparison.Ordinal))
                            continue;

                        if (directSet == null && !pContext.BundleDirectDependencies.TryGetValue(ownerBundle, out directSet))
                        {
                            directSet = new HashSet<string>(StringComparer.Ordinal);
                            pContext.BundleDirectDependencies[ownerBundle] = directSet;
                        }
                        directSet.Add(depBundle);
                    }
                }
            }
            else
            {
                Debug.LogWarning("[HyperContent] SBP IBundleWriteData not captured — BundleDirectDependencies " +
                                 "will be empty for this build (BundleDependencies/transitive still populated).");
            }

            int directBundleCount = pContext.BundleDirectDependencies.Count;
            int transitiveBundleCount = pContext.BundleDependencies.Count;
            Debug.Log($"[HyperContent] Bundle dependency maps rebuilt from SBP: " +
                      $"{directBundleCount} bundles have direct (one-hop) deps, " +
                      $"{transitiveBundleCount} bundles have transitive-closure deps " +
                      $"(catalog uses the transitive list).");
        }

        /// <summary>
        /// Compute the per-asset dependency bundle list for asset-level loading, from the same SBP
        /// <c>IBundleWriteData</c> used by <see cref="RebuildBundleDependenciesFromSbpResults"/>, then
        /// transitively expanded through the SBP bundle-level closure (<see cref="BuildContext.BundleDependencies"/>).
        /// <para/>
        /// <c>AssetToFiles[guid]</c> gives only the ONE-HOP set of entry bundles an asset directly touches: it
        /// stops at explicit (entry) asset boundaries, because an explicitly-grouped asset's objects live in its
        /// OWN bundle and are referenced as an external PPtr, not written into this asset's closure. That is why a
        /// chain like <c>prefab → material(entry) → shader(entry)</c> would otherwise drop the shader's bundle: the
        /// material bundle is one-hop, but the shader bundle is the material's dependency, two hops out.
        /// <para/>
        /// We therefore take the one-hop entry bundles, add the SBP-invisible <c>SpriteAtlas</c> edge, then expand
        /// the whole set through <see cref="BuildContext.BundleDependencies"/> (already a transitive closure),
        /// yielding the complete, self-contained bundle set needed to load THIS asset. Implicit deps need no edge —
        /// SBP duplicates them into each referencing entry's bundle, so they ride along whenever that bundle loads.
        /// <para/>
        /// One-hop alone still under-pins one topology: when an entry R references a SIBLING entry Y in the SAME
        /// owning bundle, and Y has cross-bundle deps (e.g. <c>R → Y(sibling entry) → M(other bundle)</c>), Y's
        /// bundle equals the owner, which the transitive expansion excludes — so M is missed. Cross-bundle
        /// intermediates are fine (the expansion catches them); only the in-bundle sibling-entry hop is invisible,
        /// because <c>AssetToFiles</c> stops at the explicit boundary Y. We close this by computing, per asset, an
        /// <i>in-bundle entry frontier</i> (R plus every sibling entry it transitively references WITHOUT leaving
        /// the owning bundle) from SBP's object-level <see cref="IDependencyData"/> reference graph, then taking the
        /// one-hop base set over the WHOLE frontier. Once any frontier edge crosses out of the owner, Step 3's
        /// transitive closure covers all deeper nesting, so only the owner-local layer needs the frontier. The
        /// entry graph comes from SBP (not <c>AssetDatabase.GetDependencies</c>), matching exactly what SBP packed.
        /// <para/>
        /// The owning bundle is emitted LAST so <c>BundleAssetExtractor.FindLoadedBundle</c> can take the last entry
        /// as the primary bundle; the other entries' order is irrelevant to correctness and is sorted only for
        /// reproducible catalogs. Writes <see cref="BuildContext.AssetDependencyBundles"/> keyed by lowercase 32-hex
        /// GUID. Also used by <see cref="UpdateBuildExecutor"/>, whose update build is a full-context SBP run, so
        /// <see cref="BuildContext.BundleDependencies"/> is complete there too.
        /// </summary>
        internal static void BuildAssetDependencyBundlesFromSbp(
            BuildContext pContext,
            IBundleWriteData pWriteData,
            IDependencyData pDependencyData)
        {
            pContext.AssetDependencyBundles.Clear();

            if (pWriteData == null || pWriteData.AssetToFiles == null || pWriteData.FileToBundle == null)
            {
                Debug.LogWarning("[HyperContent] SBP IBundleWriteData not captured — AssetDependencyBundles empty; " +
                                 "catalog will omit asset-level deps and the runtime will FAIL those loads " +
                                 "(no bundle-level fallback).");
                return;
            }

            // Same SBP→expected bundle-name normalization as RebuildBundleDependenciesFromSbpResults.
            var sbpNameToExpected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var expectedName in pContext.BundleToAssets.Keys)
            {
                sbpNameToExpected[expectedName] = expectedName;
                sbpNameToExpected[expectedName + ".bundle"] = expectedName;
            }

            // Source texture GUID → owning SpriteAtlas GUID. The sprite→atlas packing edge is invisible to
            // SBP (the source texture is stripped and repacked into the atlas), so it is the ONE entry edge
            // that must be recovered from the AssetDatabase. Computed once per build.
            var spriteSourceToAtlas = BuildSpriteSourceToAtlasMap();

            // Explicit (entry) assets = keys of AssetToFiles. These are exactly the assets at which SBP's one-hop
            // closure stops, so they are the nodes of the in-bundle entry frontier graph below.
            var entryGuids = new HashSet<GUID>(pWriteData.AssetToFiles.Keys);

            // entry GUID → owning expected bundle (files[0]). Used to keep the frontier walk inside the bundle.
            var entryOwnerBundle = new Dictionary<GUID, string>();
            foreach (var kvp in pWriteData.AssetToFiles)
            {
                var files = kvp.Value;
                if (files == null || files.Count == 0)
                    continue;
                if (!pWriteData.FileToBundle.TryGetValue(files[0], out var ownSbp))
                    continue;
                if (sbpNameToExpected.TryGetValue(ownSbp, out var own))
                    entryOwnerBundle[kvp.Key] = own;
            }

            // entry GUID → directly-referenced sibling entry GUIDs, from SBP's object-level dependency analysis
            // (IDependencyData.referencedObjects). This is the one-hop entry edge AssetToFiles cannot expose.
            var entryDirectEntryRefs = BuildEntryReferenceGraph(pDependencyData, entryGuids);

            // Diagnostics for the subset clamp below: how many bundles were dropped because they fell
            // outside the owning bundle's bundle-level closure (i.e. bundles BundleLevel mode never loads,
            // so a single asset cannot legitimately need them — almost always the SBP-invisible atlas edge).
            int clampedAssets = 0;
            int clampedEdges = 0;
            var clampedSample = new System.Text.StringBuilder();

            foreach (var kvp in pWriteData.AssetToFiles)
            {
                var files = kvp.Value;
                if (files == null || files.Count == 0)
                    continue;

                var ownerFile = files[0];
                if (string.IsNullOrEmpty(ownerFile) || !pWriteData.FileToBundle.TryGetValue(ownerFile, out var ownerSbpBundle))
                    continue;
                if (!sbpNameToExpected.TryGetValue(ownerSbpBundle, out var ownerBundle))
                    continue;

                // Step 1: compute the in-bundle entry frontier (R plus every sibling entry it transitively
                // references WITHOUT leaving the owning bundle), then build the one-hop base set of entry bundles
                // over the WHOLE frontier (excluding owner). This recovers cross-bundle deps that are only
                // reachable through same-bundle sibling entries — the SBP one-hop boundary AssetToFiles cannot
                // cross. Deeper cross-bundle nesting is handled by the transitive expansion in Step 3.
                var frontier = ComputeInBundleEntryFrontier(kvp.Key, ownerBundle, entryDirectEntryRefs, entryOwnerBundle);

                string guid = kvp.Key.ToString().ToLowerInvariant();

                var baseSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in frontier)
                {
                    if (!pWriteData.AssetToFiles.TryGetValue(member, out var memberFiles) || memberFiles == null)
                        continue;
                    for (int i = 0; i < memberFiles.Count; i++)
                    {
                        var depFile = memberFiles[i];
                        if (string.IsNullOrEmpty(depFile))
                            continue;
                        if (!pWriteData.FileToBundle.TryGetValue(depFile, out var depSbpBundle))
                            continue;
                        if (!sbpNameToExpected.TryGetValue(depSbpBundle, out var depBundle))
                            continue;
                        if (!string.Equals(depBundle, ownerBundle, StringComparison.Ordinal))
                            baseSet.Add(depBundle);
                    }
                }

                // Step 2: recover the SBP-invisible SpriteAtlas edge into the base set BEFORE expansion, so the
                // atlas bundle's OWN transitive closure (e.g. the atlas's shader) gets expanded alongside the rest.
                AddPackedSpriteAtlasBundles(pContext, guid, ownerBundle, baseSet, spriteSourceToAtlas);

                // Step 3: transitively expand the base entry bundles through the SBP bundle-level closure.
                // BundleDependencies[b] is already the full transitive closure of b, so a single union per base
                // bundle yields the complete set (this is what recovers multi-level chains like
                // prefab→material→shader without re-introducing the over-pin of recursive GetDependencies).
                var closure = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in baseSet)
                {
                    closure.Add(b);
                    if (pContext.BundleDependencies.TryGetValue(b, out var transitive) && transitive != null)
                        closure.UnionWith(transitive);
                }
                closure.Remove(ownerBundle);

                // Step 3.5: clamp to the owning bundle's bundle-level closure. BundleLevel mode is the
                // proven-correct reference and loads exactly BundleDependencies[ownerBundle] (+owner); a single
                // asset can never legitimately need more than its whole bundle does. Anything outside that
                // closure is over-pin BundleLevel does not load (in practice only the SBP-invisible atlas edge
                // added in Step 2), so drop it to enforce AssetLevel ⊆ BundleLevel. A null closure means the
                // owning bundle has no bundle deps at all → clamp to empty.
                pContext.BundleDependencies.TryGetValue(ownerBundle, out var ownerClosure);

                if (closure.Count > 0)
                {
                    int beforeCount = closure.Count;
                    if (ownerClosure != null && ownerClosure.Count > 0)
                        closure.IntersectWith(ownerClosure);
                    else
                        closure.Clear();

                    int dropped = beforeCount - closure.Count;
                    if (dropped > 0)
                    {
                        clampedAssets++;
                        clampedEdges += dropped;
                        if (clampedAssets <= 50)
                            clampedSample.AppendLine($"  asset {guid} (owning '{ownerBundle}'): dropped {dropped} over-pin bundle(s) " +
                                                     "outside the bundle-level closure (BundleLevel does not load these).");
                    }
                }

                // Step 4: emit. Dependency-bundle order does not affect correctness (all are loaded); sort for
                // reproducible catalogs, then append the owning bundle LAST (FindLoadedBundle picks the last entry).
                var depBundles = new List<string>(closure);
                depBundles.Sort(StringComparer.Ordinal);
                depBundles.Add(ownerBundle);

                pContext.AssetDependencyBundles[guid] = depBundles;
            }

            Debug.Log($"[HyperContent] Asset-level dependency bundles computed (one-hop + SpriteAtlas edge, " +
                      $"transitively expanded via bundle-level closure, clamped to AssetLevel ⊆ BundleLevel): " +
                      $"{pContext.AssetDependencyBundles.Count} assets.");
            if (clampedAssets > 0)
            {
                Debug.LogWarning($"[HyperContent] Asset-level subset clamp dropped over-pin: {clampedAssets} asset(s), " +
                                 $"{clampedEdges} bundle edge(s) fell outside their owning bundle's bundle-level closure " +
                                 $"and were removed (these are bundles BundleLevel mode does not load). Almost always the " +
                                 $"SBP-invisible SpriteAtlas edge from Step 2 — if a sprite goes white in AssetLevel after " +
                                 $"this, that atlas was genuinely needed and must be injected into the bundle-level graph " +
                                 $"instead.\n{clampedSample}");
            }
        }

        /// <summary>
        /// Build the entry→entry reference graph from SBP's object-level dependency analysis
        /// (<see cref="IDependencyData"/>). For each explicit (entry) asset, collects the GUIDs of the OTHER
        /// explicit assets it directly references (via <c>referencedObjects</c>, from both
        /// <see cref="IDependencyData.AssetInfo"/> and <see cref="IDependencyData.SceneInfo"/>).
        /// <para/>
        /// This is the one-hop entry edge that <c>IBundleWriteData.AssetToFiles</c> cannot expose: AssetToFiles is
        /// file/bundle granularity, so same-bundle sibling entries are indistinguishable from the owner. SBP's
        /// <c>referencedObjects</c> is object granularity (each carries the source asset GUID), so filtering it to
        /// explicit entries yields the direct sibling edges needed for the in-bundle frontier walk. Sourcing this
        /// from SBP (rather than <c>AssetDatabase.GetDependencies</c>) keeps it consistent with what SBP packed.
        /// </summary>
        private static Dictionary<GUID, List<GUID>> BuildEntryReferenceGraph(
            IDependencyData pDependencyData,
            HashSet<GUID> pEntryGuids)
        {
            var graph = new Dictionary<GUID, List<GUID>>();
            if (pDependencyData == null)
            {
                Debug.LogWarning("[HyperContent] SBP IDependencyData not captured — in-bundle entry reference graph " +
                                 "unavailable; asset-level deps may under-pin cross-bundle deps reached through " +
                                 "same-bundle sibling entries (e.g. prefab → sibling prefab(entry) → other-bundle material).");
                return graph;
            }

            void Collect(GUID pOwner, IReadOnlyList<ObjectIdentifier> pRefs)
            {
                if (pRefs == null)
                    return;
                List<GUID> list = null;
                HashSet<GUID> seen = null;
                for (int i = 0; i < pRefs.Count; i++)
                {
                    var g = pRefs[i].guid;
                    if (g == pOwner || !pEntryGuids.Contains(g))
                        continue;
                    seen ??= new HashSet<GUID>();
                    if (!seen.Add(g))
                        continue;
                    (list ??= new List<GUID>()).Add(g);
                }
                if (list != null)
                    graph[pOwner] = list;
            }

            if (pDependencyData.AssetInfo != null)
            {
                foreach (var kvp in pDependencyData.AssetInfo)
                {
                    if (kvp.Value != null && pEntryGuids.Contains(kvp.Key))
                        Collect(kvp.Key, kvp.Value.referencedObjects);
                }
            }
            if (pDependencyData.SceneInfo != null)
            {
                foreach (var kvp in pDependencyData.SceneInfo)
                {
                    if (pEntryGuids.Contains(kvp.Key))
                        Collect(kvp.Key, kvp.Value.referencedObjects);
                }
            }
            return graph;
        }

        /// <summary>
        /// Compute the in-bundle entry frontier for <paramref name="pRoot"/>: the root plus every sibling entry it
        /// transitively references via <paramref name="pEntryRefs"/> WITHOUT leaving the owning bundle
        /// (<paramref name="pOwnerBundle"/>). Edges that cross to another bundle are intentionally NOT followed —
        /// once a dependency lands in a non-owner bundle, that bundle's transitive closure
        /// (<see cref="BuildContext.BundleDependencies"/>, expanded in Step 3 of the caller) already covers all
        /// deeper nesting. The frontier therefore only needs to span the owner-local layer that the bundle-level
        /// expansion excludes. The root is always included (index 0).
        /// </summary>
        private static List<GUID> ComputeInBundleEntryFrontier(
            GUID pRoot,
            string pOwnerBundle,
            Dictionary<GUID, List<GUID>> pEntryRefs,
            Dictionary<GUID, string> pEntryOwnerBundle)
        {
            var result = new List<GUID> { pRoot };
            if (pEntryRefs == null || pEntryRefs.Count == 0)
                return result;

            var visited = new HashSet<GUID> { pRoot };
            var queue = new Queue<GUID>();
            queue.Enqueue(pRoot);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!pEntryRefs.TryGetValue(cur, out var refs) || refs == null)
                    continue;
                for (int i = 0; i < refs.Count; i++)
                {
                    var next = refs[i];
                    if (visited.Contains(next))
                        continue;
                    // Stay inside the owning bundle; cross-bundle hops are covered by Step 3's transitive closure.
                    if (!pEntryOwnerBundle.TryGetValue(next, out var nextOwner)
                        || !string.Equals(nextOwner, pOwnerBundle, StringComparison.Ordinal))
                        continue;
                    visited.Add(next);
                    result.Add(next);
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        /// <summary>
        /// Diagnostic subset check for the asset-level dependency data. For every asset, its dependency
        /// bundle set (all entries except the owning bundle, which is LAST) must be a subset of the owning
        /// bundle's bundle-level transitive closure (<see cref="BuildContext.BundleDependencies"/>). A
        /// "superset" element — an asset claiming a dependency outside its owning bundle's closure — usually
        /// signals an SBP data anomaly.
        /// <para/>
        /// This logs every violation at <b>Error</b> level for visibility but intentionally does NOT add to
        /// <see cref="BuildContext.Errors"/>, so it never blocks the build. Rationale: with the current
        /// algorithm the asset-level set is the transitive closure of the asset's one-hop entry bundles (all
        /// within the owning bundle's closure) PLUS the SBP-invisible <c>SpriteAtlas</c> edge added by
        /// <see cref="AddPackedSpriteAtlasBundles"/>. The atlas bundle (and its closure) is therefore the ONLY
        /// expected source of violations here — it legitimately sits outside the pure-SBP bundle-level closure.
        /// Any non-atlas violation indicates an SBP data anomaly worth investigating; treat the errors as a
        /// signal, not a gate.
        /// </summary>
        internal static void ValidateAssetDepsSubsetOfBundleClosure(BuildContext pContext)
        {
            if (pContext?.AssetDependencyBundles == null || pContext.AssetDependencyBundles.Count == 0)
                return;

            int violatingAssets = 0;
            int violatingEdges = 0;
            var sb = new System.Text.StringBuilder();

            foreach (var kvp in pContext.AssetDependencyBundles)
            {
                var deps = kvp.Value;
                if (deps == null || deps.Count == 0)
                    continue;

                // Owning bundle is the LAST entry (post-order invariant).
                var owningBundle = deps[deps.Count - 1];

                HashSet<string> closure = null;
                pContext.BundleDependencies?.TryGetValue(owningBundle, out closure);

                List<string> offenders = null;
                for (int i = 0; i < deps.Count - 1; i++)
                {
                    var dep = deps[i];
                    if (string.IsNullOrEmpty(dep) || string.Equals(dep, owningBundle, StringComparison.Ordinal))
                        continue;
                    bool inClosure = closure != null && closure.Contains(dep);
                    if (!inClosure)
                    {
                        (offenders ??= new List<string>()).Add(dep);
                        violatingEdges++;
                    }
                }

                if (offenders != null)
                {
                    violatingAssets++;
                    if (violatingAssets <= 50) // cap the detail dump
                    {
                        sb.AppendLine($"  asset {kvp.Key} (owning '{owningBundle}') → not in bundle-level closure: " +
                                      string.Join(", ", offenders));
                    }
                }
            }

            if (violatingAssets > 0)
            {
                Debug.LogError(
                    $"[HyperContent] Asset-level subset check: {violatingAssets} asset(s), {violatingEdges} edge(s) " +
                    $"reference dependency bundles outside their owning bundle's SBP bundle-level closure. " +
                    $"With the current algorithm these are expected to be ONLY SpriteAtlas bundles (the one edge " +
                    $"SBP cannot see) — the build is NOT blocked — but any NON-atlas entry here is an SBP data " +
                    $"anomaly worth investigating. " +
                    $"Also note BundleLevel mode would NOT load these bundles (its fallback closure is the SBP " +
                    $"closure), so the affected assets may under-load in BundleLevel mode.\n{sb}");
            }
            else
            {
                Debug.Log("[HyperContent] Asset-level subset check passed: every asset's dependency bundles " +
                          "are within their owning bundle's bundle-level closure.");
            }
        }

        /// <summary>
        /// Add the bundle of any <c>SpriteAtlas</c> that an asset indirectly depends on into
        /// <paramref name="pBaseSet"/> (the one-hop base set, before transitive expansion).
        /// <para/>
        /// A prefab can reference a <c>Sprite</c> whose source texture is <i>packed into</i> a <c>SpriteAtlas</c>.
        /// That texture→atlas edge is a build-time repack, not a serialized PPtr, so it is invisible to SBP's
        /// reference graph and therefore absent from both <c>AssetToFiles</c> and
        /// <see cref="BuildContext.BundleDependencies"/>. Left unrecovered, the atlas bundle is never pinned and the
        /// sprite renders white/missing once that bundle is unloaded. It is the ONLY entry edge SBP cannot see, so
        /// it is the only thing recovered from the AssetDatabase here.
        /// <para/>
        /// Recovered from the asset's <i>full recursive</i> dependency set (<c>GetDependencies(path, true)</c>),
        /// because the source texture may sit behind deep implicit chains, via <paramref name="pSpriteSourceToAtlas"/>
        /// (source texture GUID → owning atlas GUID) resolved through <see cref="BuildContext.AssetToBundle"/>.
        /// Adding to the base set (rather than the final list) lets the caller expand the atlas bundle's own
        /// transitive closure too. The owning bundle is skipped. Note: ordinary explicit multi-level refs
        /// (prefab→material→shader) are NOT handled here — they are recovered by the caller's transitive
        /// expansion over <see cref="BuildContext.BundleDependencies"/>, which is precise and avoids the over-pin
        /// of a recursive AssetDatabase walk.
        /// </summary>
        private static void AddPackedSpriteAtlasBundles(
            BuildContext pContext,
            string pGuidLower,
            string pOwnerBundle,
            HashSet<string> pBaseSet,
            Dictionary<string, string> pSpriteSourceToAtlas)
        {
            if (pContext?.GuidToPath == null || pContext.AssetToBundle == null || pSpriteSourceToAtlas == null)
                return;
            if (!pContext.GuidToPath.TryGetValue(pGuidLower, out var assetPath) || string.IsNullOrEmpty(assetPath))
                return;

            var depPaths = AssetDatabase.GetDependencies(assetPath, true);
            if (depPaths == null)
                return;

            for (int i = 0; i < depPaths.Length; i++)
            {
                var depPath = depPaths[i];
                if (string.IsNullOrEmpty(depPath) || depPath == assetPath)
                    continue;
                if (depPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || depPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    continue;

                var depGuid = AssetDatabase.AssetPathToGUID(depPath);
                if (string.IsNullOrEmpty(depGuid))
                    continue;

                // Dependency is a texture packed into a SpriteAtlas → pin the atlas's bundle.
                if (pSpriteSourceToAtlas.TryGetValue(depGuid, out var atlasGuid)
                    && pContext.AssetToBundle.TryGetValue(atlasGuid, out var atlasBundle)
                    && !string.IsNullOrEmpty(atlasBundle)
                    && !string.Equals(atlasBundle, pOwnerBundle, StringComparison.Ordinal))
                {
                    pBaseSet.Add(atlasBundle);
                }
            }
        }

        /// <summary>
        /// Build a reverse map: source texture GUID → owning <c>SpriteAtlas</c> GUID, by enumerating every
        /// <c>SpriteAtlas</c> in the project and expanding its packables (folders → contained textures,
        /// direct texture/sprite entries → themselves). Used to recover the indirect SpriteAtlas dependency
        /// in <see cref="AddPackedSpriteAtlasBundles"/>. A texture is expected to belong to
        /// at most one atlas; if grouped into several, the last one wins (and a warning is logged).
        /// </summary>
        private static Dictionary<string, string> BuildSpriteSourceToAtlasMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas");
            if (atlasGuids == null || atlasGuids.Length == 0)
                return map;

            foreach (var atlasGuid in atlasGuids)
            {
                var atlasPath = AssetDatabase.GUIDToAssetPath(atlasGuid);
                if (string.IsNullOrEmpty(atlasPath))
                    continue;

                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null)
                    continue;

                var packables = atlas.GetPackables();
                if (packables == null)
                    continue;

                foreach (var packable in packables)
                {
                    if (packable == null)
                        continue;
                    var packablePath = AssetDatabase.GetAssetPath(packable);
                    if (string.IsNullOrEmpty(packablePath))
                        continue;

                    if (AssetDatabase.IsValidFolder(packablePath))
                    {
                        // Folder packable: every texture under it is packed into this atlas.
                        var inFolder = AssetDatabase.FindAssets("t:Texture", new[] { packablePath });
                        if (inFolder == null) continue;
                        foreach (var texGuid in inFolder)
                            AddSpriteSourceToAtlas(map, texGuid, atlasGuid, atlasPath);
                    }
                    else
                    {
                        var texGuid = AssetDatabase.AssetPathToGUID(packablePath);
                        AddSpriteSourceToAtlas(map, texGuid, atlasGuid, atlasPath);
                    }
                }
            }

            Debug.Log($"[HyperContent] SpriteAtlas source map built: {map.Count} source textures across " +
                      $"{atlasGuids.Length} atlas(es).");
            return map;
        }

        private static void AddSpriteSourceToAtlas(
            Dictionary<string, string> pMap, string pTexGuid, string pAtlasGuid, string pAtlasPath)
        {
            if (string.IsNullOrEmpty(pTexGuid))
                return;
            if (pMap.TryGetValue(pTexGuid, out var existing) && !string.Equals(existing, pAtlasGuid, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[HyperContent] Texture {pTexGuid} is packed into multiple SpriteAtlases " +
                                 $"(was {existing}, now {pAtlasPath}). Using the latter for asset-level deps.");
            }
            pMap[pTexGuid] = pAtlasGuid;
        }
        
        /// <summary>
        /// Prepare AssetBundle build configurations
        /// </summary>
        private List<AssetBundleBuild> PrepareAssetBundleBuilds(BuildContext context)
        {
            var builds = new List<AssetBundleBuild>();
            
            foreach (var kvp in context.BundleToAssets)
            {
                var bundleName = kvp.Key;
                var assetGuids = kvp.Value;
                
                var assetPaths = new List<string>();
                var addressableNames = new List<string>();
                
                foreach (var assetGuid in assetGuids)
                {
                    if (!context.GuidToPath.TryGetValue(assetGuid, out var assetPath))
                    {
                        continue;
                    }
                    
                    assetPaths.Add(assetPath);
                    addressableNames.Add(BundleAssetInternalId.FromAssetPath(assetPath));
                }
                
                if (assetPaths.Count > 0)
                {
                    // Append ".bundle" to make the on-disk filename end with .bundle so Android's
                    // APK packer (aaptOptions.noCompress = "bundle") does NOT deflate it, letting
                    // AssetBundle.LoadFromFile* use memory-mapped I/O. BundleToAssets / catalog
                    // keys stay as the extensionless logical bundleName; StoreActualBundleNames
                    // records the "expected → actual" mapping for downstream path resolution.
                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = bundleName + NamingRules.BUNDLE_FILE_EXTENSION,
                        assetNames = assetPaths.ToArray(),
                        addressableNames = addressableNames.ToArray()
                    });
                }
            }
            
            return builds;
        }
        
        /// <summary>
        /// Known filenames for SBP (Scriptable Build Pipeline) build log written into bundle output directory.
        /// Move them to {buildOutputRoot}/{Platform} so they do not ship with bundles.
        /// </summary>
        private static readonly string[] SBP_BUILD_LOG_FILENAMES = { "buildlogtep.json", "AddressablesBuildTEP.json" };
        
        /// <summary>
        /// Move SBP build log from bundle output directory to {buildOutputRoot}/{Platform}.
        /// Called after ContentPipeline.BuildAssetBundles so the log is not included in StreamingAssets.
        /// </summary>
        public static void MoveSbpBuildLogToPlatformOutput(BuildContext context)
        {
            var bundleDir = context.Config.BundleOutputDirectory;
            var destDir = context.Config.PlatformOutputDirectory;
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            foreach (var fileName in SBP_BUILD_LOG_FILENAMES)
            {
                var srcPath = Path.Combine(bundleDir, fileName);
                if (!File.Exists(srcPath))
                    continue;
                var destPath = Path.Combine(destDir, fileName);
                try
                {
                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    File.Move(srcPath, destPath);
                    Debug.Log($"[HyperContent] Moved SBP build log: {srcPath} → {destPath}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[HyperContent] Could not move SBP build log to platform output: {e.Message}");
                }
            }
        }
        
        /// <summary>
        /// Store the mapping from expected bundle names to actual bundle names from SBP results.
        /// </summary>
        private static void StoreActualBundleNames(BuildContext pContext, IBundleBuildResults pResults)
        {
            var allBundles = pResults.BundleInfos.Keys.ToArray();
            var expectedCount = pContext.BundleToAssets.Count;
            var sbpCount = allBundles.Length;

            var allBundleSet = new HashSet<string>(allBundles, StringComparer.OrdinalIgnoreCase);
            var unresolvedExpected = new List<string>();

            foreach (var expectedName in pContext.BundleToAssets.Keys)
            {
                if (allBundleSet.Contains(expectedName))
                {
                    pContext.ExpectedToActualBundleName[expectedName] = expectedName;
                    continue;
                }

                var withExtension = expectedName + ".bundle";
                if (allBundleSet.Contains(withExtension))
                {
                    pContext.ExpectedToActualBundleName[expectedName] = withExtension;
                    continue;
                }

                // Case-insensitive fallback
                var expectedNameLower = expectedName.ToLowerInvariant();
                bool found = false;
                foreach (var actualName in allBundles)
                {
                    var stripped = actualName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                        ? actualName.Substring(0, actualName.Length - ".bundle".Length)
                        : actualName;
                    if (stripped.ToLowerInvariant() == expectedNameLower)
                    {
                        pContext.ExpectedToActualBundleName[expectedName] = actualName;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    unresolvedExpected.Add(expectedName);
                    Debug.LogWarning($"[HyperContent] Could not find actual bundle name for expected name: '{expectedName}'");
                }
            }

            var mappedActuals = new HashSet<string>(pContext.ExpectedToActualBundleName.Values, StringComparer.OrdinalIgnoreCase);
            var extraSbp = allBundles.Where(b => !mappedActuals.Contains(b)).ToList();

            Debug.Log($"[HyperContent] SBP bundle name mapping: built {sbpCount} on disk, {expectedCount} logical bundles, " +
                      $"{pContext.ExpectedToActualBundleName.Count} mapped.");

            if (unresolvedExpected.Count > 0 || extraSbp.Count > 0 || sbpCount != expectedCount)
            {
                if (unresolvedExpected.Count > 0)
                    Debug.LogWarning("[HyperContent] Expected bundle names with no SBP output match:\n  - " +
                                     string.Join("\n  - ", unresolvedExpected));
                if (extraSbp.Count > 0)
                    Debug.LogWarning("[HyperContent] SBP output bundle names not mapped to any expected logical bundle:\n  - " +
                                     string.Join("\n  - ", extraSbp));
                if (sbpCount != expectedCount && unresolvedExpected.Count == 0 && extraSbp.Count == 0)
                    Debug.LogWarning($"[HyperContent] SBP bundle count ({sbpCount}) differs from expected logical count ({expectedCount}) " +
                                     "but one-to-one mapping succeeded — check for duplicate logical names or SBP filtering.");
            }
        }
        
        /// <summary>
        /// Clear old bundle files
        /// </summary>
        private void ClearOldBundles(string outputDir)
        {
            if (!Directory.Exists(outputDir))
            {
                return;
            }
            
            var bundleFiles = Directory.GetFiles(outputDir, "*.bundle", SearchOption.TopDirectoryOnly);
            foreach (var file in bundleFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to delete old bundle: {file}, Error: {e.Message}");
                }
            }
            
            // Also delete manifest files
            var manifestFiles = Directory.GetFiles(outputDir, "*.manifest", SearchOption.TopDirectoryOnly);
            foreach (var file in manifestFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to delete manifest: {file}, Error: {e.Message}");
                }
            }
        }
        
        /// <summary>
        /// Generate settings.json (always) and versioned remote catalog files (if configured).
        /// </summary>
        private bool GenerateSettingsAndRemoteCatalog(BuildContext pContext)
        {
            try
            {
                var config = pContext.Config;
                string buildVersion = config.ResolvedBuildVersion;
                string catalogDir = config.CatalogOutputDirectory;
                string catalogPath = Path.Combine(catalogDir, HyperContentPaths.LOCAL_CATALOG_FILENAME);
                if (!File.Exists(catalogPath))
                {
                    Debug.LogError($"[HyperContent] Catalog not found: {catalogPath}");
                    pContext.Errors.Add(new BuildError($"Catalog not found: {catalogPath}"));
                    return false;
                }

                Debug.Log($"[HyperContent] Generating settings.json — buildVersion={buildVersion}");

                // Build RuntimeSettings
                var settings = new RuntimeSettings
                {
                    buildVersion = buildVersion,
                    buildTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    localCatalogPath = HyperContentPaths.LOCAL_CATALOG_FILENAME,
                    catalogRequestTimeout = config.catalogRequestTimeout,
                    catalogFormat = (int)config.catalogFormat,
                    dependencyLoadMode = (int)config.dependencyLoadMode
                };

                // CDN base is not baked into settings.json (relative paths only; no "://").
                // Runtime sets the module base via SetRemoteBundleBaseUrl. Editor BuildConfig remoteBundleLoadUrl / remoteCatalogLoadUrl remain for tooling reference only.

                // Android builds always ship local bundles inside an install-time PAD pack named
                // BUNDLES_SUBFOLDER ("Bundles"), matching AABBuilder's AssetPackConfig.AddAssetsFolder
                // call (see PlayAssetDeliveryProvider.ASSETBUNDLE_FOLDER_NAME). Hard-code the name
                // here so PAD routing is never silently disabled by an empty settings field; other
                // platforms leave it empty (HasPlayAssetDelivery == false → direct StreamingAssets IO).
                if (config.buildTarget == BuildTarget.Android)
                {
                    settings.playAssetDeliveryPackName = HyperContentPaths.BUNDLES_SUBFOLDER;
                }

                // Generate remote catalog if configured
                if (config.buildRemoteCatalog)
                {
                    string remoteBinName = $"HyperCatalog_{buildVersion}.bin";
                    string remoteHashName = $"HyperCatalog_{buildVersion}.hash";

                    // Resolve remote folder to project root: ServerData/Production/{Platform}
                    string remoteBuildFolder = BuildConfig.GetResolvedRemoteCatalogBuildFolder(config.remoteCatalogBuildFolder, config.buildTarget);
                    if (!Directory.Exists(remoteBuildFolder))
                        Directory.CreateDirectory(remoteBuildFolder);

                    // Copy catalog as versioned remote catalog
                    string remoteBinPath = Path.Combine(remoteBuildFolder, remoteBinName);
                    File.Copy(catalogPath, remoteBinPath, overwrite: true);

                    // Generate hash file (SHA256 of catalog content)
                    byte[] catalogBytes = File.ReadAllBytes(catalogPath);
                    string catalogHash = ComputeSHA256(catalogBytes);
                    string remoteHashPath = Path.Combine(remoteBuildFolder, remoteHashName);
                    File.WriteAllText(remoteHashPath, catalogHash);

                    Debug.Log($"[HyperContent] Remote catalog generated: {Path.GetFullPath(remoteBinPath)}");
                    Debug.Log($"[HyperContent] Remote catalog hash: {Path.GetFullPath(remoteHashPath)} (hash={catalogHash})");

                    // Relative paths after CDN {platform}/ — versioned filenames only (matches ServerData/{Platform}/ upload layout; not StreamingAssets/hc/ on CDN)
                    settings.remoteCatalogRelativePath = remoteBinName;
                    settings.remoteCatalogHashRelativePath = remoteHashName;
                    settings.cachedCatalogPath = remoteBinName;
                    settings.cachedCatalogHashPath = remoteHashName;

                    Debug.Log($"[HyperContent] Remote catalog relative path: {settings.remoteCatalogRelativePath}");
                    Debug.Log($"[HyperContent] Remote hash relative path: {settings.remoteCatalogHashRelativePath}");
                }
                else
                {
                    Debug.Log("[HyperContent] Remote catalog disabled, local-only mode");
                }

                // Write settings.json
                string settingsJson = JsonUtility.ToJson(settings, prettyPrint: true);
                string settingsPath = Path.Combine(catalogDir, HyperContentPaths.SETTINGS_FILENAME);
                File.WriteAllText(settingsPath, settingsJson);

                Debug.Log($"[HyperContent] settings.json written: {settingsPath}");

                return true;
            }
            catch (Exception e)
            {
                pContext.Errors.Add(new BuildError($"Failed to generate settings: {e.Message}"));
                Debug.LogError($"[HyperContent] Settings generation failed: {e}");
                return false;
            }
        }

        private static string ComputeSHA256(byte[] pData)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(pData);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
