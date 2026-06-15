# HyperContent - Directory Structure

> Overview of the HyperContent module layout. Updated to reflect v0.5 architecture (actual file locations).

```
HyperContent/
+-- README.md                                        // Project entry point and quick start
|
+-- docs/                                            // All design and specification documents
|   +-- ARCHITECTURE.md                              // Layered architecture, core class designs
|   +-- CONVENTIONS.md                               // Rules: naming, error codes, RefCount, design principles
|   +-- CATALOG_SCHEMA.md                            // Catalog Layer design, binary schema
|   +-- DIRECTORY_STRUCTURE.md                       // This file
|   +-- OWNERS.md                                    // Owner roles, responsibilities, collaboration rules
|   +-- CHANGELOG.md                                 // Version change log
|   +-- BUILD_LIFECYCLE.md                           // Build pipeline and catalog lifecycle
|   +-- INITIALIZATION_FLOW.md                       // Runtime: init, CatalogLocator, settings.json
|   +-- LOAD_RELEASE_FLOW.md                         // Runtime: DAG load/release, RefCount, edge cases
|   +-- CONTENT_UPDATE_FLOW.md                       // Runtime: content update, catalog hot-update
|   +-- PROVIDER_FLOW.md                             // Runtime: provider execution, PAD
|   +-- TODO.md                                      // Future work: planned APIs, planned docs (CATALOG_LIFECYCLE)
|   // Planned (see TODO.md): CATALOG_LIFECYCLE.md
|
+-- Shared/                                          // Shared code between Editor and Runtime
|   +-- HyperContent.Shared.asmdef                   // Assembly definition (shared)
|   +-- Constants.cs                                 // ErrorCode, LogFields, NamingRules
|   +-- ContentLocation.cs                           // Enum: None / Local / Remote / StreamingAssets / Resources
|   +-- HCLog.cs                                     // HCLogger: macro-based logging with Console double-click navigation
|   +-- NameHashUtil.cs                              // SHA256-based stable name hash (for NameAlias lookup)
|   +-- PlayModeSettings.cs                          // Editor play mode reader (UseAssetDatabase / UseExistingBundle)
|
+-- Runtime/                                         // Runtime code (ships with the game)
|   +-- HyperContent.Runtime.asmdef                  // Assembly definition (runtime)
|   |
|   +-- Core/                                        // -- Core Interfaces & Data Types (Owner0) --
|   |   +-- ContentHandle.cs                         // ContentHandle<T> struct (unified handle)
|   |   +-- SceneInstance.cs                         // Scene load result wrapper
|   |   +-- VoidResult.cs                            // Void placeholder for no-result operations
|   |   +-- IContentProvider.cs                      // Provider interface: ProviderId, Provide, Release
|   |   +-- IBundleStore.cs                          // Local cache contract
|   |   +-- IBundleTransport.cs                      // Remote download contract
|   |   +-- IBundleDownloadQueue.cs                  // Global bundle download queue + progress listener types (Owner0 API)
|   |   +-- IBundleLoader.cs                         // Unity AssetBundle loading contract
|   |   +-- RuntimeSettings.cs                       // Runtime settings (from settings.json)
|   |   +-- HyperContentPaths.cs                     // Path constants, Android StreamingAssets support
|   |
|   +-- Data/                                        // -- Shared data structures --
|   |   +-- BundleInfo.cs                            // Bundle metadata (incl. RemoteRelativePath, TagFlags)
|   |   +-- FetchResult.cs                           // Download result
|   |   +-- CatalogDiskUpdateResult.cs               // Catalog disk hot-update result (Kind + ErrorCode incl. SkippedNoRemote)
|   |   +-- LoadNetworkTypes.cs                      // Load modes, PendingBundleQueryScope, MissingBundlePromptInfo, LoadNetworkOptions
|   |
|   +-- Catalog/                                     // -- Catalog Layer --
|   |   +-- ICatalog.cs                              // Catalog interface: TryGetLocations
|   |   +-- ResourceLocation.cs                      // Location descriptor: Address, InternalId, ProviderId, Dependencies
|   |   +-- CatalogSchema.cs                         // Catalog schema: stringTable + assetRecords + nameAliases + bundleRecords
|   |   +-- LocalContentCatalog.cs                   // Local catalog loader (HyperContentPaths.LoadText for Android)
|   |   +-- CatalogLocator.cs                        // Local resolve + optional remote hash/catalog to disk (CheckAndDownloadCatalogUpdateAsync)
|   |   +-- BundleDownloadManager.cs                 // Bundle download: check pending, check by asset, selective download
|   |   // [DELETED] RemoteContentCatalog.cs         // Superseded by CatalogLocator + LocalContentCatalog
|   |   // [DELETED] ContentUpdateManager.cs         // Superseded by BundleDownloadManager
|   |
|   +-- Operations/                                  // -- Operation Layer + Facade (DAG engine + API entry) --
|   |   +-- HyperContent.cs                          // Static facade (sole public entry point, explicit init)
|   |   +-- HyperContentImpl.cs                      // Internal implementation (pure C#, no MonoBehaviour)
|   |   +-- AsyncOperationBase.cs                    // Operation base class: RefCount, status, DAG deps
|   |   +-- AssetOperation.cs                        // Typed operation for asset loading
|   |   +-- SceneOperation.cs                        // Operation for scene loading
|   |   +-- OperationCache.cs                        // Global cache: GetOrCreate, Release with recursive unload
|   |   +-- ResourceManager.cs                       // DAG scheduler: builds dep tree, topological execution
|   |   +-- OperationStatus.cs                       // Enum: None/Pending/InProgress/Succeeded/Failed/Disposed
|   |   +-- AssetReference.cs                        // Non-generic serializable asset reference (GUID key)
|   |   +-- AssetReference_T.cs                      // Generic AssetReference<T> (typed)
|   |
|   +-- Providers/                                   // -- Provider Layer (pluggable IO) --
|   |   +-- ProvideHandle.cs                         // Bridge: Complete, Fail, UpdateProgress, GetDependencyResult
|   |   +-- ProviderRegistry.cs                      // Provider registration by ProviderId
|   |   +-- BundleFileProvider.cs                    // Load .bundle file (local), produces AssetBundle
|   |   +-- BundleAssetExtractor.cs                  // Extract asset from loaded AssetBundle (no IO)
|   |   +-- RemoteBundleProvider.cs                  // HTTP download + cache, remote bundle loading
|   |   +-- LocalFileProvider.cs                     // Editor mode direct load (no Bundle)
|   |   +-- SceneProvider.cs                         // Async scene loading
|   |   +-- PlayAssetDeliveryBundleProvider.cs       // Android PAD variant of BundleFileProvider
|   |
|   +-- Lifecycle/                                   // -- Instance lifecycle --
|   |   +-- InstanceRegistry.cs                      // Track GameObject instance -> Operation mapping
|   |
|   +-- Bundle/                                      // -- Bundle infrastructure (existing, Owner3) --
|   |   +-- BundleProvider.cs                        // Facade: store + transport + loader orchestration
|   |   +-- LocalBundleStore.cs                      // IBundleStore impl: atomic write, LRU prune, hash verify
|   |   +-- HttpBundleTransport.cs                   // IBundleTransport impl: retry, concurrency, timeout
|   |   +-- UnityBundleLoader.cs                     // IBundleLoader impl: AssetBundle.LoadFromFile/Memory
|   |
|   +-- Examples/                                    // -- Usage examples --
|       +-- HyperContentTest.cs                      // POC test
|
+-- Editor/                                          // Editor-only code (does not ship)
|   +-- HyperContent.Editor.asmdef                   // Assembly definition (editor)
|   +-- HyperContentPlayerBuildProcessor.cs         // Player Build: copy HyperContentBuild/ → StreamingAssets/hc/
|   +-- HyperContentBuildMenu.cs                     // Menu items
|   +-- HyperContentBuildWindow.cs                   // Build config UI
|   +-- HyperContentAsset.cs                         // ScriptableObject marker
|   +-- AssetReferenceDrawer.cs                     // PropertyDrawer: AssetReference Inspector (GUID picker)
|   +-- StubTransport.cs                             // IBundleTransport stub for testing
|   +-- BUILD_SYSTEM.md                              // Build system design doc
|   +-- QUICK_START.md                               // Editor quick-start guide
|   |
|   +-- Build/                                       // -- Build pipeline (Owner1) --
|   |   +-- HyperContentBuilder.cs                   // Build orchestrator
|   |   +-- BuildContext.cs                          // Build session state
|   |   +-- BuildPlan.cs                             // Grouping tool output
|   |   +-- BuildReport.cs                           // Build result data
|   |   +-- BuildReportGenerator.cs                  // Human-readable report generator
|   |   +-- BuildValidator.cs                        // Pre-build validation
|   |   +-- BuildToolFactory.cs                      // Plugin registry
|   |   +-- AssetCollector.cs                        // Scan project for markers
|   |   +-- DependencyAnalyzer.cs                    // Analyze dependencies
|   |   +-- CatalogGenerator.cs                      // Generate catalog JSON
|   |   +-- BundleBuilder.cs                         // Invoke Unity BuildPipeline
|   |   +-- IBundleGroupingTool.cs                   // Grouping tool contract
|   |   +-- IBundleGroupingStrategy.cs               // Grouping strategy contract
|   |   +-- IBuildExecutor.cs                        // Build executor contract
|   |   +-- DefaultGroupingTool.cs                   // Default grouping tool
|   |   +-- DefaultBuildExecutor.cs                  // Default build executor
|   |   +-- BundleGroupingStrategyFactory.cs         // Strategy factory
|   |   +-- AddressableGroupingStrategy.cs           // Group by Addressable membership
|   |   +-- MarkerBasedGroupingStrategy.cs           // Group by marker bundleGroup
|   |
|   +-- Simulation/                                  // -- Editor simulation --
|       +-- AssetDatabaseProvider.cs                 // IContentProvider impl for AssetDatabase loading
|       +-- EditorAssetDatabaseCatalog.cs            // ICatalog impl for AssetDatabase mode
|       +-- EditorCatalogGenerator.cs                // Generate editor catalog from AssetDatabase
|
+-- Test/                                            // -- Integration tests --
    +-- HyperContentLoadTest.cs                      // Load test
```

## Namespace Convention

Namespaces follow `com.igg.<root_folder_name>` per project code-style, with 4 layers:

| Assembly | Namespace | Contents |
|----------|-----------|----------|
| `HyperContent.Shared` | `com.igg.hypercontent.shared` | ErrorCode, LogFields, NamingRules |
| `HyperContent.Runtime` | `com.igg.hypercontent` | Static facade `HyperContent`, `HyperContentImpl` (API layer) |
| `HyperContent.Runtime` | `com.igg.hypercontent.runtime` | All runtime implementation (Operations, Providers, Catalog, etc.) |
| `HyperContent.Editor` | `com.igg.hypercontent.editor` | Build pipeline, editor tools |
| (Test) | `com.igg.hypercontent.test` | Integration tests |

Usage from game code:

```csharp
using com.igg.hypercontent;
var op = HyperContent.LoadAsync<Texture2D>("UI/Avatar");
```

## Assembly Dependencies

```
HyperContent.Shared     (no dependencies)
       ^
HyperContent.Runtime    depends on Shared
       ^
HyperContent.Editor     depends on Runtime + Shared + UnityEditor
```