# Addressable Build Flow

> **Scope**: This is a **reference document** describing the standard Unity Addressables build pipeline internals (Sections 1–6) and the project's Addressables integration (Section 7). It served as the design reference for HyperContent's custom build system. For HyperContent's own build flow, see [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) and [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md).

This document describes the complete Unity Addressables build pipeline, including full build (New Build), content update build (Update a Previous Build), and the underlying SBP (Scriptable Build Pipeline) caching mechanism.

All analysis is based on the project's actual source code:
- `Packages/com.unity.addressables@38fa2290d5f2/`
- `Library/PackageCache/com.unity.scriptablebuildpipeline@ca3e2d96aa2f/`
- `Assets/Scripts/Editor/BuildTools/BuildScriptManager.cs`

---

## Table of Contents

1. [Key Concepts](#1-key-concepts)
2. [addressables_content_state.bin Structure](#2-addressables_content_statebin-structure)
3. [Full Build Flow (New Build)](#3-full-build-flow-new-build)
4. [Content Update Build Flow (Update a Previous Build)](#4-content-update-build-flow-update-a-previous-build)
5. [SBP BuildCache — The True Incremental Engine](#5-sbp-buildcache--the-true-incremental-engine)
6. [Two-Layer Incremental Architecture](#6-two-layer-incremental-architecture)
7. [Project-Specific Build Pipeline](#7-project-specific-build-pipeline)

---

## 1. Key Concepts

### Static Content (Prevent Updates)

Each Addressable Group has a `ContentUpdateGroupSchema` with a `StaticContent` toggle (displayed as "Prevent Updates" in the Inspector).

| Setting | Meaning |
|---------|---------|
| `StaticContent = true` | Assets are shipped with the player build. Not intended to change post-release. |
| `StaticContent = false` | Assets can be updated remotely after release. |

Source: `AddresaableGroupBuildUtilities.SetSchema()` sets `StaticContent = !isUpdate`.

### Schema Configuration Details

`SetSchema()` configures more than just `StaticContent`. The full set of properties it touches:

| Property | Value |
|----------|-------|
| `BundleNaming` | `NoHash` (always) |
| `BundleMode` | Default `PackSeparately`; overridden to `PackTogether` if the root `AddressableAssetRule.PackModel` says so |
| `UseAssetBundleCrc` | `false` (always) |
| `BuildPath` | `kLocalBuildPath` (non-update) / `kRemoteBuildPath` (update) |
| `LoadPath` | `kLocalLoadPath` (non-update) / `kRemoteLoadPath` (update) |
| `Compression` | `LZ4` (non-update) / `LZMA` (update) |
| `StaticContent` | `true` (non-update) / `false` (update) |
| `IncludeInBuild` | Set to `false` for platform-filtered groups via `AddressabelUtilities.NeedIgnorePlatform(group.name)` |

Source: `AddresaableGroupBuildUtilities.cs` lines 17-57.

### Local vs Remote Path

| Path | Usage |
|------|-------|
| `kLocalBuildPath` / `kLocalLoadPath` | Assets shipped inside the player (StreamingAssets) |
| `kRemoteBuildPath` / `kRemoteLoadPath` | Assets delivered via CDN for hot updates |

### AssetDependencyHash

`AssetDatabase.GetAssetDependencyHash(path)` returns a `Hash128` that changes whenever the asset itself or **any of its dependencies** are modified. This is the primary mechanism for detecting asset changes.

---

## 2. addressables_content_state.bin Structure

This file is the snapshot of the build state, generated during a full build and used for comparison during content update builds.

### Top-Level Structure: `AddressablesContentState`

```
AddressablesContentState
├── playerVersion: string          // Player version at build time
├── editorVersion: string          // Unity editor version
├── remoteCatalogLoadPath: string  // Remote catalog URL (must exist for updates)
├── cachedInfos: CachedAssetState[]   // Per-asset state snapshot
└── cachedBundles: CachedBundleState[] // Per-bundle state snapshot
```

Source: `ContentUpdateScript.cs` lines 164-195.

### Per-Asset Snapshot: `CachedAssetState`

```
CachedAssetState
├── asset: AssetState
│   ├── guid: GUID                // Asset's unique identifier
│   └── hash: Hash128             // AssetDatabase.GetAssetDependencyHash() result
├── dependencies: AssetState[]    // All dependency assets' GUID + Hash
├── groupGuid: string             // GUID of the Group this asset belongs to
├── bundleFileId: string          // InternalId of the Bundle containing this asset
└── data: object                  // Catalog extra data (e.g., AssetBundleRequestOptions)
```

Source: `ContentUpdateScript.cs` lines 95-141.

### Per-Bundle Snapshot: `CachedBundleState`

```
CachedBundleState
├── bundleFileId: string   // Bundle's InternalId (load path)
└── data: object           // AssetBundleRequestOptions (CRC, Hash, BundleSize, BundleName)
```

Source: `ContentUpdateScript.cs` lines 146-158.

### How It's Generated

In `BuildScriptPackedMode.DoBuild()`, **only during a full build** (`PreviousContentState == null`):

1. Collects all assets from groups where `StaticContent = true` and `IncludeInBuild = true`
   (filter: `ContentUpdateScript.GroupFilterFunc`)
2. For each asset, calls `GetCachedAssetStateForData()`:
   - Records asset's `GUID + AssetDependencyHash`
   - Records all dependencies' `GUID + AssetDependencyHash`
   - Records `groupGuid` and `bundleFileId`
3. For each Catalog location of type `IAssetBundleResource`, records as `CachedBundleState`
4. Serializes via `BinaryFormatter` to `.bin` file

Source: `BuildScriptPackedMode.cs` lines 507-536, `ContentUpdateScript.cs` lines 374-428.

### File Location

Default path: `Assets/AddressableAssetsData/{Platform}/addressables_content_state.bin`

---

## 3. Full Build Flow (New Build)

This is the build triggered by `AddressableAssetSettings.CleanPlayerContent()` + `AddressableAssetSettings.BuildPlayerContent()`.

```
Full Build (New Build)
│
├── 1. ProcessAllGroups
│   └── For each Group with BundledAssetGroupSchema:
│       ├── Collect all entries
│       ├── Generate AssetBundleBuild definitions based on BundlePackingMode
│       │   ├── PackTogether: all entries → 1 bundle
│       │   ├── PackSeparately: each entry → 1 bundle
│       │   └── PackTogetherByLabel: entries grouped by label → 1 bundle each
│       └── HandleBundleNames: hash the bundle names for uniqueness
│
├── 2. ContentPipeline.BuildAssetBundles (SBP)
│   ├── CalculateAssetDependencyData (with BuildCache)
│   ├── WriteSerializedFiles (with BuildCache)
│   └── ArchiveAndCompressBundles (with BuildCache)
│       → Outputs bundles to build path
│
├── 3. PostProcessBundles
│   ├── Move bundles from temp to target path (Local or Remote)
│   ├── Set CRC / Hash / BundleSize in Catalog entries
│   └── Apply bundle naming (with hash or NoHash)
│
├── 4. ProcessCatalogEntriesForBuild
│   └── PreviousContentState == null → SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId
│       └── Record each entry's BundleFileId for future content state
│
├── 5. Generate Catalog
│   ├── Serialize catalog → catalog.bin + catalog.hash (local)
│   └── If BuildRemoteCatalog = true:
│       └── catalog_{playerVersion}.bin + .hash (remote)
│
├── 6. SaveContentState ← ONLY during full build
│   ├── Collect StaticContent=true groups' assets
│   ├── Record per-asset: GUID, Hash, dependencies, groupGuid, bundleFileId
│   ├── Record per-bundle: bundleFileId, AssetBundleRequestOptions
│   └── BinaryFormatter.Serialize → addressables_content_state.bin
│
└── Output:
    ├── LocalBuildPath/: bundles for player build
    ├── RemoteBuildPath/: remote bundles + catalog (if enabled)
    └── addressables_content_state.bin  ← MUST BE PRESERVED
```

---

## 4. Content Update Build Flow (Update a Previous Build)

This is the complete flow when clicking "Build > Update a Previous Build" in the Addressables Groups window.

### Phase A: Pre-Build — Detect Changes and Move Assets

```
Phase A: Pre-Build
│
├── A1. Locate addressables_content_state.bin
│   └── GetContentStateDataPath() → find .bin file
│       └── If not found → prompt user to select manually
│
├── A2. Check CheckForContentUpdateRestrictionsOption
│   │   (configured in AddressableAssetSettings)
│   │
│   ├── Disabled → skip restriction check
│   ├── FailBuild → if modified static entries found, fail immediately
│   └── ListUpdatedAssetsWithRestrictions (default) → continue to A3
│
├── A3. GatherModifiedEntriesWithDependencies()
│   │   ┌─ Load .bin → get all CachedAssetState
│   │   │
│   │   ├─ GatherExplicitModifiedEntries():
│   │   │   ├─ Filter: only StaticContent=true && IncludeInBuild=true groups
│   │   │   ├─ For each asset in these groups:
│   │   │   │   ├─ Lookup entry.guid in .bin's CachedAssetState map
│   │   │   │   ├─ Call HasAssetOrDependencyChanged():
│   │   │   │   │   ├─ Compare asset.hash (current vs cached)
│   │   │   │   │   └─ Compare each dependency[].hash
│   │   │   │   └─ If any hash differs → mark as modified
│   │   │   └─ If modified Scene + PackTogether → mark sibling scenes too
│   │   │
│   │   ├─ GetStaticContentDependenciesForEntries():
│   │   │   └─ If modified entry depends on assets in other static groups → add those
│   │   │
│   │   └─ GetEntriesDependentOnModifiedEntries():
│   │       └─ If other static entries depend on modified entries → add those
│   │
│   │   Result: Dict<ModifiedEntry, List<AffectedDependencyEntry>>
│   │
│   └── If no modifications found → proceed directly to build (no preview window)
│
├── A4. ContentUpdatePreviewWindow (user-facing)
│   │
│   │   ┌─────────────────────────────────────────────────────┐
│   │   │  "Assets with update issues"                        │
│   │   │                                                     │
│   │   │  [Info] Modified assets in groups with Prevent       │
│   │   │  Updates enabled have been detected...               │
│   │   │                                                     │
│   │   │  New Group Name: [Content Update              ]     │
│   │   │                                                     │
│   │   │  ☑ Include │ Address     │ Path      │ Group        │
│   │   │  ──────────┼─────────────┼───────────┼──────────    │
│   │   │  ☑         │ asset_a     │ Assets/.. │ StaticGroup  │
│   │   │       ↳    │ dep_of_a    │ Assets/.. │ StaticGroup  │
│   │   │  ☑         │ asset_b     │ Assets/.. │ StaticGroup  │
│   │   │                                                     │
│   │   │  [Cancel build]              [Apply and Continue]   │
│   │   └─────────────────────────────────────────────────────┘
│   │
│   └── User clicks "Apply and Continue"
│
└── A5. CreateContentUpdateGroup()
    ├── Create new group (e.g., "Content Update")
    │   ├── BuildPath = Remote Build Path
    │   ├── LoadPath = Remote Load Path
    │   ├── BundleMode = PackTogether
    │   └── StaticContent = false  ← NOT prevent updates
    └── MoveEntries(): move selected modified assets FROM static groups TO new group

    Group state after this step:
    ├── Original static groups: only unchanged assets remain
    └── New "Content Update" group: only modified assets, remote path, non-static
```

Source: `AddressableAssetsSettingsGroupEditorBuildMenu.cs`, `ContentUpdatePreviewWindow.cs`, `ContentUpdateScript.cs` lines 1056-1065.

### Phase B: Build — Incremental Bundle Building

```
Phase B: Build
│
├── B1. LoadContentState(.bin) → set PreviousContentState on build input
│   └── Cleanup(cleanBuildPath: false) ← preserves old build output
│
├── B2. ProcessAllGroups
│   └── Generate bundle definitions for ALL groups (metadata only, fast)
│
├── B3. ContentPipeline.BuildAssetBundles (SBP with BuildCache)
│   │
│   │   For each build stage (dependency calc, write, archive):
│   │   ├── Generate CacheEntry per asset/bundle (based on content hash)
│   │   ├── Query Library/BuildCache/ for cached results
│   │   ├── Cache HIT (unchanged) → skip processing, reuse cached result
│   │   └── Cache MISS (changed) → perform actual work
│   │
│   │   In practice:
│   │   ├── Static group bundles with unchanged assets → cache hit → skipped
│   │   └── New "Content Update" group bundle → cache miss → actually built
│   │
│   └── Progress bar only shows cache-miss bundles (the changed ones)
│
├── B4. PostProcessBundles
│   └── Move built bundles to target paths, set CRC/Hash/Size in catalog
│
├── B5. ProcessCatalogEntriesForBuild
│   └── PreviousContentState != null → RevertUnchangedAssetsToPreviousAssetState.Run()
│       │
│       ├── For each Group's entries:
│       │   ├── Lookup entry.guid in GuidToPreviousAssetStateMap (from .bin)
│       │   │   └── Not found → new asset, keep new bundle
│       │   ├── Compare groupGuid
│       │   │   └── Different → asset moved groups, keep new bundle
│       │   ├── Compare AssetDependencyHash:
│       │   │   current = AssetDatabase.GetAssetDependencyHash(path)
│       │   │   previous = previousAssetState.asset.hash
│       │   │   ├── Changed && non-static group → keep new bundle ✓
│       │   │   ├── Unchanged && same bundleId → no action needed
│       │   │   └── Unchanged && different bundleId → revert to old bundle
│       │   └── Revert operation:
│       │       ├── CatalogEntry.InternalId = old bundleFileId
│       │       └── CatalogEntry.Data = old AssetBundleRequestOptions
│       │
│       └── Also revert built-in shader bundle and monoscript bundle if default
│           group has StaticContent = true
│
├── B6. Generate Catalog
│   ├── Unchanged assets → catalog points to old bundles (already in player)
│   └── Changed assets → catalog points to new remote bundles
│
├── B7. Content state .bin is NOT regenerated
│   └── (PreviousContentState != null → skip SaveContentState)
│   └── Future updates continue to diff against the same original .bin
│
└── Output:
    ├── Remote path: only new bundles for changed assets
    ├── Remote path: new catalog (catalog_{version}.bin + .hash)
    └── Old bundles from original build remain unchanged
```

Source: `BuildScriptPackedMode.cs` lines 300-561, `RevertUnchangedAssetsToPreviousAssetState.cs`.

---

## 5. SBP BuildCache — The True Incremental Engine

SBP maintains a persistent cache at `Library/BuildCache/` that enables incremental builds across ALL build types (full and update).

### How It Works

```
Library/BuildCache/
└── {guid_prefix}/
    └── {guid}/
        └── {hash}/
            ├── {guid}.info    // Serialized CachedInfo (dependencies, results)
            └── {artifacts}    // Built bundle files
```

Source: `BuildCache.cs` — `const string k_CachePath = "Library/BuildCache"`.

### Cache Entry Generation

Each cacheable item (asset, bundle) produces a `CacheEntry`:

```
CacheEntry
├── Type: Asset | File | Data | ScriptType
├── Guid: derived from asset GUID or content hash
├── Hash: computed from content + version + dependencies
└── Version: task-specific version number
```

### Cached Build Stages

| Build Task | Cache Key | Effect When Cached |
|------------|-----------|-------------------|
| `CalculateAssetDependencyData` | Asset GUID + version | Skip dependency calculation |
| `WriteSerializedFiles` | Content hash of serialized data | Skip serialization, copy from cache |
| `ArchiveAndCompressBundles` | BundleName + ResourceFiles hashes + Compression | Skip archive/compress, copy bundle from cache |

### Cache Validation

`BuildCache.HasAssetOrDependencyChanged(CachedInfo info)`:
1. Check if the asset's own `CacheEntry` has changed
2. Check if any dependency's `CacheEntry` has changed
3. If anything changed → cache miss → rebuild

### Activation

In `AddressableAssetsBundleBuildParameters`: `UseCache = true` (always enabled).

Each build task checks: `input.BuildCache = m_Parameters.UseCache ? m_Cache : null`

---

## 6. Two-Layer Incremental Architecture

Content update builds use two independent incremental mechanisms working at different levels:

```
┌─────────────────────────────────────────────────────────────────┐
│                    Layer 1: SBP BuildCache                       │
│                    (Build-level incremental)                     │
│                                                                 │
│  Location: Library/BuildCache/                                  │
│  Scope: Per-asset and per-bundle                                │
│  Effect: Skips dependency calc, serialization, and archiving    │
│          for unchanged content                                  │
│  Result: Only changed bundles are actually built                │
│  Evidence: Progress bar only shows changed bundles              │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│          Layer 2: RevertUnchangedAssetsToPreviousAssetState      │
│                    (Catalog-level incremental)                   │
│                                                                 │
│  Location: addressables_content_state.bin                       │
│  Scope: Per-asset catalog entry                                 │
│  Effect: Ensures unchanged assets' catalog entries point to     │
│          original bundle paths from the full build              │
│  Result: Client continues loading unchanged assets from local   │
│          bundles; only changed assets load from remote           │
└─────────────────────────────────────────────────────────────────┘
```

### Why Both Layers Are Needed

- **SBP BuildCache alone** is not enough: even if a bundle is cached, the catalog entry might point to a new path. Without Layer 2, the client would try to download bundles it already has locally.

- **Catalog revert alone** is not enough: without SBP caching, every bundle would be rebuilt from scratch even though only a few changed, making builds unnecessarily slow.

---

## 7. Project-Specific Build Pipeline

> **Note**: This section describes the **Addressables-based** build pipeline in `BuildScriptManager.cs`. The project also has a parallel **HyperContent custom build system** (see `Assets/HyperContent/docs/BUILD_LIFECYCLE.md` and `CONTENT_UPDATE_BUILD_FLOW.md`), which uses the full SBP `ContentPipeline.BuildAssetBundles` API (custom task list) instead of Addressables.

### Current Full Build Flow

In `BuildScriptManager.cs`, the project's build steps use a `SessionState`-based state machine. The `BuildStep` enum defines all possible states:

```csharp
public enum BuildStep
{
    None = 0,
    BuildHybridDll,
    UpdateCompileHotfixDll,
    WaitCompileHotfixDllFinished,
    GenerateTypeAssemblyMap,
    CreateAddressableGroup,
    BuildAddressable,
    Build,
    PostBuild,
    Done
}
```

The actual step chain (determined by which step sets the next `SessionState` value):

```
BuildHybridDll                           ← delegates to HybridCLRBuilder
  → GenerateTypeAssemblyMap              ← next = UpdateCompileHotfixDll
    → UpdateCompileHotfixDll             ← next = WaitCompileHotfixDllFinished
      → WaitCompileHotfixDllFinished     ← next = BuildAddressable (NOT CreateAddressableGroup)
        → BuildAddressable               ← Addressable build happens here; next = Build
          → Build (player)               ← next = PostBuild
            → PostBuild                  ← next = Done
              → Done                     ← exit
```

**Note**: `CreateAddressableGroup` exists as a separate entry point (e.g., for manual invocation) and also chains to `BuildAddressable`, but it is **not** in the main automated flow. `WaitCompileHotfixDllFinished` goes directly to `BuildAddressable` (line 105).

Source: `BuildScriptManager.cs` lines 68-146, `BuildStep.cs`.

### The BuildAddressable Step

The `BuildAddressable` step (lines 112-127) performs:

```csharp
AddressableAssetSettings.CleanPlayerContent();  // Clear previous build output
AddressableAssetSettings.BuildPlayerContent();  // Full Addressable rebuild
AssetDatabase.SaveAssets();                      // Persist changes
CompilationPipeline.RequestScriptCompilation(    // Force recompile (clean cache)
    RequestScriptCompilationOptions.CleanBuildCache);
NextBuildIfNoCompile();                          // Continue to next step
```

### Addressable Profile Selection

Before building, `UpdateAddressableSettings()` (lines 191-202) selects the Addressable profile based on `BuildData.UseRemoteAddressablePath`:

| `UseRemoteAddressablePath` | Profile Selected | Effect |
|---------------------------|------------------|--------|
| `true` | `"Production"` | Bundles use remote build/load paths (CDN) |
| `false` | `"Local"` | Bundles use local build/load paths (StreamingAssets) |

Source: `BuildScriptManager.cs` lines 191-202.

### Group Schema Configuration

`AddresaableGroupBuildUtilities.SetSchema()` configures groups based on `isUpdate`. See Section 1 "Schema Configuration Details" for the full property table.

Summary:

| Property | `isUpdate = false` (Local) | `isUpdate = true` (Remote) |
|----------|---------------------------|---------------------------|
| BuildPath | `kLocalBuildPath` | `kRemoteBuildPath` |
| LoadPath | `kLocalLoadPath` | `kRemoteLoadPath` |
| Compression | LZ4 | LZMA |
| StaticContent | `true` | `false` |
| BundleNaming | NoHash | NoHash |
| UseAssetBundleCrc | `false` | `false` |
| BundleMode | `PackSeparately` (default, overridden by Rule) | `PackSeparately` (default, overridden by Rule) |

### BuildData Configuration

`BuildData.IsUpdate` and `BuildData.UseRemoteAddressablePath` from `BuildData.txt` control the build behavior.

When `IsUpdate = true`, `NextBuildIfNoCompile()` returns immediately without calling `Build(buildStep)`, which means the entire state machine stops advancing — no subsequent steps (including `BuildAddressable`, `Build`, etc.) will execute.

```csharp
public static void NextBuildIfNoCompile()
{
    BuildStep buildStep = (BuildStep)SessionState.GetInt(...);
    if (!BuildDataConfig.GetData().IsUpdate)   // ← gate
    {
        Build(buildStep);
    }
}
```

Source: `BuildScriptManager.cs` lines 59-66.

### Content Update Build (Addressables)

The **Addressables-based** pipeline does **not** currently integrate content update builds into its automated flow. Content updates via Addressables can be performed manually via the Addressables Groups window: **Build > Update a Previous Build**.

To integrate into the automated pipeline, the `BuildAddressable` step would need a branch that uses `ContentUpdateScript.BuildContentUpdate()` instead of `CleanPlayerContent() + BuildPlayerContent()`.

### HyperContent Alternative

The project now has a parallel build system — **HyperContent** — which provides its own Full Build and Update Build flows without depending on Unity Addressables Groups or `addressables_content_state.bin`. HyperContent uses:

- `HyperContentBuilder.Build()` with `DefaultBuildExecutor` for Full Build
- `HyperContentBuilder.Build()` with `UpdateBuildExecutor` for Update Build
- `build_manifest.json` instead of `addressables_content_state.bin`
- Full SBP `ContentPipeline.BuildAssetBundles()` (custom task list) instead of `AddressableAssetSettings.BuildPlayerContent()`
- Bundle dependencies extracted from `IBundleBuildResults.BundleInfos` (object-level accuracy)

**SBP alignment (summary)** — HyperContent does not call `AddressableAssetSettings.BuildPlayerContent()`, but `DefaultBuildExecutor.CreateBuildTaskListForUpdate()` mirrors the same **extra** SBP tasks Addressables uses for packed content: `StripUnusedSpriteSources`, `CreateBuiltInBundle`, `CreateMonoScriptBundle`, then `UpdateBundleObjectLayout` immediately after `GenerateBundlePacking`. Shared bundle logical names are `monoscripts` and `unitybuiltinassets` (see [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) §2 “SBP task list”). Disk file names append `.bundle` for Android `aaptOptions.noCompress` compatibility.

For HyperContent-specific documentation, see:
- [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) — HyperContent Full Build flow
- [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md) — HyperContent Update Build flow

---

## References

### Unity Addressables & SBP (Package Sources)

| File | Description |
|------|-------------|
| `Packages/.../Editor/Build/ContentUpdateScript.cs` | Content update logic, state save/load, change detection |
| `Packages/.../Editor/Build/DataBuilders/BuildScriptPackedMode.cs` | Main build script, catalog generation, content state |
| `Packages/.../Editor/Build/RevertUnchangedAssetsToPreviousAssetState.cs` | Catalog-level revert for unchanged assets |
| `Packages/.../Editor/GUI/ContentUpdatePreviewWindow.cs` | Preview window for modified static assets |
| `Packages/.../Editor/GUI/AddressableAssetsSettingsGroupEditorBuildMenu.cs` | "Update a Previous Build" menu entry point |
| `Packages/.../Editor/Build/DataBuilders/AddressableAssetsBundleBuildParameters.cs` | Build parameters, `UseCache = true` |
| `Library/.../Editor/ContentPipeline.cs` | SBP main entry, BuildCache integration |
| `Library/.../Editor/Utilities/BuildCache.cs` | SBP cache implementation |
| `Library/.../Editor/Tasks/ArchiveAndCompressBundles.cs` | Bundle archiving with cache |
| `Library/.../Editor/Tasks/CalculateAssetDependencyData.cs` | Dependency calc with cache |
| `Library/.../Editor/Tasks/WriteSerializedFiles.cs` | Serialization with cache |

### Project-Specific (Addressables Pipeline)

| File | Description |
|------|-------------|
| `Assets/Scripts/Editor/BuildTools/BuildScriptManager.cs` | Project build pipeline state machine |
| `Assets/Scripts/Editor/BuildTools/BuildStep.cs` | BuildStep enum definition |
| `Assets/Scripts/Editor/BuildTools/BuildData.cs` | Build configuration (`IsUpdate`, `UseRemoteAddressablePath`, etc.) |
| `Assets/Scripts/Editor/AddressableGroupHelper/.../AddresaableGroupBuildUtilities.cs` | Group schema configuration |

### HyperContent Build System (Alternative Pipeline)

| File / Doc | Description |
|------------|-------------|
| [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) | HyperContent Full Build flow |
| [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md) | HyperContent Update Build flow |
| `Assets/HyperContent/Editor/Build/HyperContentBuilder.cs` | HyperContent build orchestrator |
| `Assets/HyperContent/Editor/Build/DefaultBuildExecutor.cs` | Full Build executor (uses full SBP `ContentPipeline.BuildAssetBundles`) |
| `Assets/HyperContent/Editor/Build/UpdateBuildExecutor.cs` | Update Build executor (change detection + mixed catalog) |
| `Assets/HyperContent/Editor/Build/BuildManifestManager.cs` | `build_manifest.json` save/load (counterpart of `addressables_content_state.bin`) |
| `Assets/HyperContent/Editor/Build/ContentChangeDetector.cs` | Asset change detection (counterpart of `ContentUpdateScript.GatherModifiedEntries`) |
