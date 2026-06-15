# HyperContent Content Update Build Flow

How to build incremental content updates that minimize download size. This is the **build-side** flow; for the **runtime-side** flow (catalog download, bundle download on device), see [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md).

Reference: [ADDRESSABLE_UPDATE_BUILD_FLOW.md](ADDRESSABLE_UPDATE_BUILD_FLOW.md) — Unity Addressables' update-build approach.

---

## Scope

This document covers:
- Build Manifest: structure, generation, and usage
- Change detection: asset-level hash comparison
- Dependency expansion: promote affected assets into the shipping set
- Update Build: complete flow (detect → assign → build → catalog)
- Output files and CDN upload
- SBP integration for build speed
- Comparison with Addressables' approach

---

## LLM Summary

**One-sentence mental model**

Update Build always compares the current project state against the **same original Full Build manifest**, then regenerates the **latest remote state** for all assets that still differ from that Full Build baseline.

### Non-negotiable invariants

1. **`build_manifest.json` is created only during the first Full Build of a release line.**
2. **Update Build must load the existing manifest and must never overwrite or regenerate it.**
3. **Every Update Build diffs against the same original manifest, not against the previous Update Build output.**
4. **The latest catalog is regenerated from the original Full Build baseline plus the current update result; it is not produced by patching the previous update catalog.**
5. **Any asset that still differs from the original Full Build must appear in the latest remote output again, even if it was already shipped in an earlier Update Build.**
6. **Unity/SBP build input must preserve full bundle ownership context for unchanged dependencies. Passing only changed assets as the sole `AssetBundleBuild[]` input is invalid because Unity may duplicate unchanged dependencies into update bundles.**

### Preferred implementation shape

- **Detect changes** by comparing current assets against the original Full Build manifest.
- **Determine shipping set** = all assets that are New or still Modified relative to that original manifest.
- **Build with full ownership context** so Unity knows where unchanged dependencies belong.
- **Generate the latest mixed catalog from scratch**:
  - unchanged relative to Full Build → Local
  - new or modified relative to Full Build → Remote
  - removed from current plan → omitted
- **Publish the latest catalog and remote bundles**. Older remote bundles may remain on CDN temporarily, but the newest catalog should reference only the latest valid remote state.

---

## Key Concepts

| Concept | Description |
|---------|-------------|
| **Build Manifest** | Snapshot of the original Full Build state (per-asset hash + dependency list by GUID). Written once at the first Full Build of a release line, then treated as immutable input for all later Update Builds. |
| **Change Detection** | Current asset set from **IBundleGroupingTool.GeneratePlan()**; comparison with manifest uses **GUID only** (see ContentUpdateScript.HasAssetOrDependencyChanged). Asset + all dependency hashes must match. New assets (in current, not in manifest) are required to be detected. |
| **Update Bundle** | Remote bundle produced by an Update Build for assets that are New or still Modified relative to the original Full Build manifest. A later Update Build may replace earlier remote bundles in the latest catalog. |
| **Mixed Catalog** | Catalog with both Local (StreamingAssets) and Remote (CDN) bundle references. It is regenerated each Update Build from the original Full Build baseline plus the current update result. |
| **Latest Remote State** | The current authoritative remote mapping for all assets that still differ from the original Full Build. It is recomputed every Update Build; it is not inherited by patching the previous catalog. |
| **SBP Build Cache** | `ContentPipeline.BuildAssetBundles` (full SBP) caches unchanged build artifacts, making even full rebuilds fast. Independent of Content Update — purely a build speed optimization. |

---

## 1. Build Manifest Structure

The Build Manifest is the counterpart of Addressables' `addressables_content_state.bin`. It records the state of every asset and bundle at Full Build time.

**Owner**: Owner0 defines the specification; Owner1 implements generation and consumption.

**File**: `{buildOutputRoot}/{Platform}/build_manifest.json`

### AssetState (for comparison)

Used inside CachedAssetState to represent one asset's identity and hash. Reference: Addressables `ContentUpdateScript.AssetState`.

| Field | Type | Description |
|-------|------|-------------|
| `guid` | string | Asset GUID (32-char lowercase hex) |
| `hash` | string | `AssetDatabase.GetAssetDependencyHash(path)` for this asset (Hash128 string). Path is always resolved from GUID via `AssetDatabase.GUIDToAssetPath(guid)`. |

Equality: two AssetStates are equal iff `guid == other.guid && hash == other.hash`.

### CachedAssetState

Comparison uses **GUID only** to resolve asset path and state (see Phase A and ContentUpdateScript). All lookups must be by GUID, not by path.

| Field | Type | Description |
|-------|------|-------------|
| `guid` | string | Asset GUID (32-char lowercase hex) |
| `hash` | string | `AssetDatabase.GetAssetDependencyHash(path)` result for this asset (Hash128 string). Path is resolved via `AssetDatabase.GUIDToAssetPath(guid)`. |
| `bundleName` | string | Bundle this asset was assigned to at Full Build time |
| `internalId` | string | The InternalId (addressableName / short key) used inside the bundle |
| `dependencies` | AssetState[] | Dependency list for comparison. Each entry: `{ guid, hash }`. Enables re-fetching current state by GUID and comparing with `Equals`. |
| `dependencyBundleNames` | string[] | **Asset-level** dependency bundle **names** captured at Full Build time (ordered, **owning bundle LAST**), mirroring `AssetRecordEntry.dependencyBundles`. Lets an Update Build restore the asset-level dependency list for **unchanged** assets — whose SBP write-data is not regenerated this round — so the mixed catalog still carries per-asset deps for them. May be empty for assets that had no asset-level data at Full Build time. See [CATALOG_SCHEMA.md §2.4](CATALOG_SCHEMA.md). |

### CachedBundleState

| Field | Type | Description |
|-------|------|-------------|
| `bundleName` | string | Bundle identifier |
| `bundleHash` | string | SHA256 of bundle file content |
| `size` | long | Bundle file size in bytes |
| `assetGuids` | string[] | GUIDs of all assets in this bundle |

### Top-Level Structure

```
BuildManifest
├── buildVersion: string              // Build version at Full Build time
├── buildTimestamp: long              // UTC Unix timestamp
├── cachedAssets: CachedAssetState[]  // Per-asset snapshot
└── cachedBundles: CachedBundleState[] // Per-bundle snapshot
```

### Example

```json
{
  "buildVersion": "2026.03.05.12.00.00",
  "buildTimestamp": 1772870400,
  "cachedAssets": [
    {
      "guid": "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",
      "hash": "f8e7d6c5b4a39281...",
      "bundleName": "ui_common",
      "internalId": "AvatarWidget",
      "dependencies": [
        { "guid": "b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7", "hash": "a1b2c3d4e5f67890..." }
      ],
      "dependencyBundleNames": ["ui_icons_atlas", "ui_common"]
    }
  ],
  "cachedBundles": [
    {
      "bundleName": "ui_common",
      "bundleHash": "abc123def456...",
      "size": 1048576,
      "assetGuids": ["a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", "..."]
    }
  ]
}
```

---

## 2. Full Build: Manifest Generation

During a **Full Build** (see [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md)), after bundles and catalog are generated, the Build Manifest is saved.

```
Full Build (existing flow)
  │
  ├── Phase 1: Grouping → BuildPlan
  ├── Phase 2: Building → Bundles + Catalog
  │
  └── NEW: Save Build Manifest ← ONLY during Full Build
       │
       ├── For each asset in the build (path resolved by GUID where needed):
       │   ├── Record guid
       │   ├── Record hash = AssetDatabase.GetAssetDependencyHash(path), path = GUIDToAssetPath(guid)
       │   ├── Record bundleName, internalId (addressableName)
       │   ├── Record dependencies: for each dependency GUID, get path and hash → AssetState { guid, hash }[]
       │   └── Record dependencyBundleNames from BuildContext.AssetDependencyBundles (asset-level, owning LAST)
       │
       ├── For each bundle:
       │   ├── Record bundleName
       │   ├── Compute SHA256 of bundle file → bundleHash
       │   ├── Record file size
       │   └── Record list of asset GUIDs
       │
       └── Serialize → {buildOutputRoot}/{Platform}/build_manifest.json

⚠️  THIS FILE MUST BE PRESERVED for all future Update Builds.
    Losing it requires a new Full Build.
```

---

## 3. Update Build: Complete Flow

### Phase A: Change Detection

Current asset set comes from **IBundleGroupingTool** (same as Full Build). Comparison uses **GUID only** to resolve paths and state; see comparison rule below (reference: Addressables `ContentUpdateScript.HasAssetOrDependencyChanged`).

```
Phase A: Change Detection
│
├── A1. Load Build Manifest
│   └── Deserialize build_manifest.json → BuildManifest
│       └── If not found → FAIL: "Build manifest not found, run Full Build first"
│
├── A2. Get Current Asset Set (via IBundleGroupingTool)
│   │
│   │   Run the same IBundleGroupingTool used for Full Build:
│   │   └── IBundleGroupingTool.GeneratePlan(config) → BuildPlan (current)
│   │
│   │   From BuildPlan we have:
│   │   ├── All current asset GUIDs (KeyToGuid / GuidToPath / AssetToBundle)
│   │   ├── Current bundle assignment (AssetToBundle, BundleToAssets)
│   │   └── Asset dependency graph (Dependencies: GUID → dependency GUIDs)
│   │
│   └── Build: Dictionary<guid, CurrentAssetInfo> and set of all current GUIDs
│
├── A3. Compare Current vs Manifest (GUID-based)
│   │
│   │   For each asset in CURRENT (from BuildPlan):
│   │   │
│   │   ├── Look up manifest by GUID (not by path).
│   │   │   └── If not in manifest → NEW (mandatory: must be included in Update Bundles) → add to List<NewAssetInfo>
│   │   │
│   │   └── If in manifest → run HasAssetOrDependencyChanged(cachedInfo):
│   │       │   (See "Comparison rule" below. All resolution by GUID.)
│   │       ├── Get current state for asset: GUID → path = GUIDToAssetPath(guid),
│   │       │   hash = GetAssetDependencyHash(path). If resolution fails → MODIFIED.
│   │       ├── Get current state for each dependency GUID from cachedInfo.dependencies.
│   │       ├── Build newCachedInfo (asset + dependencies with current hashes).
│   │       ├── If cachedInfo.Equals(newCachedInfo) → UNCHANGED.
│   │       └── Else → MODIFIED → add to List<ModifiedAssetInfo>
│   │
│   └── Output: ModifiedAssetInfo { guid, oldBundleName, assetPath }, NewAssetInfo { guid, assetPath }
│
├── A3b. Dependency Expansion (required)
│   │
│   │   Starting from the explicit changed set from A3 (Modified ∪ New only):
│   │
│   │   Fixed-point loop (while new entries are added):
│   │   │
│   │   ├── Loop 1: GetStaticContentDependenciesForEntries (downward)
│   │   │   │   For each entry in expanded, check its Dependencies for other entries.
│   │   │   │   Filter: ShouldSkipStaticDependencyEntry (aligned with Addressables
│   │   │   │   GetGroupGuidsWithUnchangedBundleName):
│   │   │   │   ├── Compute the dep entry's current bundle name (bn)
│   │   │   │   ├── Compare bn vs manifest[depGuid].bundleName (OrdinalIgnoreCase)
│   │   │   │   ├── If bn != manifest → don't skip (bundle assignment changed)
│   │   │   │   ├── If dep not in manifest → don't skip (new asset, conservative)
│   │   │   │   ├── Check remainder = (entries in same bundle) minus (A3 explicit mods)
│   │   │   │   ├── If remainder is empty → don't skip (all entries in bundle are modified)
│   │   │   │   └── If remainder non-empty AND bn == manifest → SKIP (bundle unchanged)
│   │   │   └── Only uses A3 explicit Modified/New as exclude set (never expansion closure)
│   │   │
│   │   ├── Loop 2: GetEntriesDependentOnModifiedEntries (upward)
│   │   │   │   For each entry not yet in expanded, if any of its dependencies are in
│   │   │   │   expanded → add it. NO unchanged-bundle-name filter here: this is an
│   │   │   │   asset correctness requirement (entry references a changed dep).
│   │   │   └── Same as Addressables (no filter on upward pass)
│   │   │
│   │   └── Produce ExpandedChangedAssets
│   │
│   │   Design notes:
│   │   ├── HC uses while-loop (fixed-point) vs Addressables single-pass; intentionally
│   │   │   more conservative to ensure transitive closure regardless of iteration order.
│   │   ├── Phase 1 PredictedBundleName = current AssetToBundle[depGuid] (logical name).
│   │   │   Safe for all HC strategies (bundle names don't depend on entry set composition).
│   │   └── Phase 2 (optional): when InternalBundleIdMode uses entry-set hash, must
│   │       re-simulate bundle name from filtered entry set.
│   │
│   └── Output: ExpandedChangedAssets = explicit modified/new assets + dependency-expanded assets
│
├── A4. Detect Removed Assets
│   │
│   │   For each asset in MANIFEST that is not in CURRENT (from BuildPlan):
│   │   └── REMOVED (no longer in project or no longer included by grouping tool)
│   │       → Catalog will drop these entries in Update Build
│   │
│   └── Output: List<RemovedAssetInfo> { guid }
│
└── A5. Summary
    ├── Modified: N assets
    ├── New: M assets (required to be detected and listed)
    ├── Expanded shipping set: P assets
    ├── Removed: K assets
    └── If no changes (N+M == 0) → skip build, output "No changes detected"
```

#### Comparison rule (GUID-based, reference ContentUpdateScript.cs)

- **Resolve state by GUID only.** Path must be obtained via `AssetDatabase.GUIDToAssetPath(guid)`; do not use path as the primary key. Hash is `AssetDatabase.GetAssetDependencyHash(path)`.
- **HasAssetOrDependencyChanged(cachedInfo):**
  1. Get current asset state: `GetAssetState(cachedInfo.asset.guid)` → path from GUID, then hash from path. If failed (e.g. asset deleted) → return **true** (changed).
  2. Get current state for each dependency: for each `cachedInfo.dependencies[i].guid`, call `GetAssetState(depGuid)`. If any fails → return **true** (changed).
  3. Build **newCachedInfo** with current asset state and current dependency states.
  4. Return **true** if `!cachedInfo.Equals(newCachedInfo)` (asset hash or any dependency hash differs); else **false**.
- **Equals(cachedInfo, newCachedInfo):** asset.guid == other.asset.guid && asset.hash == other.asset.hash && same number of dependencies && for each index i, dependencies[i].guid == other.dependencies[i].guid && dependencies[i].hash == other.dependencies[i].hash.

#### Phase A: Alignment with Addressables

| Aspect | Addressables | HyperContent (this doc) | Implementation note |
|--------|--------------|-------------------------|----------------------|
| **Previous state** | `addressables_content_state.bin` → `CachedAssetState[]` | `build_manifest.json` → `cachedAssets` | Same: GUID + hash + dependencies; lookup by GUID only. |
| **Current asset source** | `settings.GetAllAssets(allEntries, groupFilter)` — only groups with **Prevent Updates (StaticContent)=true** | `IBundleGroupingTool.GeneratePlan()` → BuildPlan (all assets in build) | Different: Addressables only considers "static" groups; HyperContent uses full plan. Restrict to a "static" subset if needed. |
| **New** | Entry in current, GUID not in `.bin` → treat as modified | Asset in CURRENT, GUID not in manifest → NewAssetInfo | Same logic. |
| **Modified** | `HasAssetOrDependencyChanged(cachedInfo)` (asset + deps hash) | Same comparison rule above | Implement like `ContentUpdateScript.HasAssetOrDependencyChanged` + `GetCachedAssetStateForData`; resolve path only via `GUIDToAssetPath(guid)`. |
| **Dependency expansion** | GetStaticContentDependenciesForEntries (filtered by `GetGroupGuidsWithUnchangedBundleName`) + GetEntriesDependentOnModifiedEntries (no filter) | **A3b** (required): same two steps. Downward loop filtered by `ShouldSkipStaticDependencyEntry` (explicit-only exclude set + bundle name comparison). Upward loop: no filter. HC uses fixed-point while-loop (vs Addressables single-pass). | Phase 1: `PredictedBundleName = AssetToBundle[depGuid]` (logical name). Phase 2: re-simulate when entry-set hash is used. |
| **Removed** | Not enumerated; deleted assets simply disappear from new catalog | **A4**: For each GUID in MANIFEST not in CURRENT → RemovedAssetInfo. **Reporting only; not used for packaging.** | Output Removed list when reporting is needed; hot update build does not use it. |

### Phase B: Update Layout + Full-Context Build

**Shipping-set input**: The set of assets to ship as Remote in the latest catalog starts from the explicit Modified + New assets from Phase A, then expands via A3b dependency rules. Each asset has: `guid`, `assetPath`, and **source bundle name** (for Modified: from manifest `cachedInfo.bundleName`; for New: from current BuildPlan `AssetToBundle[guid]`).

**Expanded-shipping-set requirement**: The real input to Phase B must be the **dependency-expanded changed set**, not the explicit Modified + New list alone. If dependency-expansion rules say an asset must follow a changed asset, it must also be included in the current update layout.

**Build input**: The actual Unity/SBP build must see the **full current layout after update groups are created**, so unchanged dependencies still have known bundle ownership.

**Output**: A full-context build result plus the subset of remote bundles and catalog entries that remain valid after the revert step.

---

#### B1. Update Bundle Grouping Strategy

How to partition the dependency-expanded changed-asset set into update bundles. Strategy is **configurable** (e.g. `BuildConfig.updateBundleGroupingStrategy`). Built-in strategies are listed below; users can implement **IUpdateBundleGroupingStrategy** for custom behaviour.

| Strategy | Id | Rule | Update bundle name | Example |
|----------|----|------|--------------------|--------|
| **Group by original bundle** | `GroupByOriginalBundle` | One update bundle per **source bundle name**. All assets that belonged to the same original bundle (in manifest or in current plan) go into one update bundle. For Modified: use bundle name from manifest (already-shipped bundle). For New: use current BuildPlan assignment. | `{originalBundleName}_update_{version}`. `version` = build version or sequence number (e.g. timestamp). No path separators; see CONVENTIONS. | Original `ui_common` → `ui_common_update_202603051200`; original `textures_hero` → `textures_hero_update_202603051200`. |
| **Single update bundle** | `SingleBundle` | All changed assets go into **one** update bundle. | `content_update_{version}` (fixed prefix + version). | All → `content_update_202603051200`. |
| **Re-run grouping on changed set** | `ReRunGrouping` | Run the **same IBundleGroupingStrategy** as Full Build on the changed set only (Collect → Analyze → Assign), then suffix each resulting bundle name. For Modified: result may differ from "original bundle" if asset's marker/Group was changed in project. For New: same as current Plan in practice. | Strategy output bundle name + `_update_{version}`. | If strategy yields `ui_widgets`, `hero_skins` → `ui_widgets_update_202603051200`, `hero_skins_update_202603051200`. |

- **Naming**: Update bundle names must follow CONVENTIONS (bundle name max length, no path separators). Version segment must be unique per Update Build (e.g. `ResolvedBuildVersion` or build timestamp).
- **Default**: `GroupByOriginalBundle`.

**User-defined strategy**

Users can implement **IUpdateBundleGroupingStrategy** and register it (e.g. via `BuildConfig` or a strategy factory). The interface receives the dependency-expanded changed-asset list (each with `guid`, `assetPath`, `sourceBundleName`) and the build `version` string, and returns a mapping from **update bundle name** to **list of asset info** (GUIDs + paths). Update bundle names must follow CONVENTIONS. The build pipeline invokes this implementation when the configured strategy is custom.

- **Input**: `IReadOnlyList<ChangedAssetInfo>` (guid, assetPath, sourceBundleName), `string version`.
- **Output**: `Dictionary<string, List<AssetInfo>>` or equivalent (updateBundleName → assets).
- **Contract**: Each asset in the dependency-expanded changed set must appear in exactly one update bundle; names must be unique and CONVENTIONS-compliant.

#### B2. Update layout rule (critical)

The update-bundle mapping from B1 must be converted into the **current update layout** before calling Unity/SBP.

- **Required**: create update groups that contain the dependency-expanded changed set from B1.
- **Required**: derive the **current full layout** after those update groups are created.
- **Invalid**: build `AssetBundleBuild[]` from changed assets only, with no representation of unchanged bundles/dependencies.
  - Risk: Unity may pull unchanged dependencies into update bundles because it does not know they already belong to other bundles in the shipped build.
- **Constraint**: the update groups/layout created during Update Build are build-scoped data and must not be treated as persistent authoring state.

#### B3. Full-context build + revert rule (critical)

After B2 has produced the current update layout, the remaining flow should align with the **second half of Addressables Update Build**:

- run bundle packing from the **full current layout**
- build with `ContentPipeline.BuildAssetBundles()` (full SBP pipeline with custom task list)
- extract accurate bundle dependencies from `IBundleBuildResults.BundleInfos`
- use the immutable Full Build manifest as previous-state input
- revert unchanged assets/catalog references back to the original Full Build state
- keep the latest remote bundles and catalog entries that remain valid after revert
- **delete update bundles from StreamingAssets** after copying them to ServerData (update bundles should only exist on CDN)

SBP Cache is only a **performance optimization**:

- it may prevent some unchanged bundles from being rewritten
- it does **not** mean the builder is operating in a "changed bundles only" mode
- final publishable output is determined by **full-context build + cache behavior + revert**, not by cache alone

```
Phase B: Update Layout + Full-Context Build
│
├── B1. Assign Changed Assets to Update Bundles
│   │
│   │   Input: ExpandedChangedAssets (dependency-expanded set; each with guid, assetPath, sourceBundleName)
│   │   Strategy: BuildConfig.updateBundleGroupingStrategy (default GroupByOriginalBundle)
│   │   │
│   │   ├── GroupByOriginalBundle: group by sourceBundleName → "{sourceBundleName}_update_{version}"
│   │   ├── SingleBundle: one bundle → "content_update_{version}"
│   │   ├── ReRunGrouping: run IBundleGroupingStrategy on changed set only → names + "_update_{version}"
│   │   └── Custom: invoke user-provided IUpdateBundleGroupingStrategy.GroupChangedAssets(...)
│   │
│   └── Output: Map<updateBundleName, List<AssetInfo>> (asset GUIDs + paths per bundle)
│
├── B2. Create Update Layout
│   │   ├── Create update groups from the B1 mapping
│   │   ├── Move / assign ExpandedChangedAssets into those update groups
│   │   └── Derive the full current layout after the group mutation
│
├── B3. Full-Context Build
│   │   ├── Process all groups from the current update layout
│   │   ├── Build using ContentPipeline.BuildAssetBundles() (full SBP, custom task list)
│   │   │   → Unity/SBP sees the full bundle graph, not only changed assets
│   │   ├── Extract accurate bundle dependencies from IBundleBuildResults.BundleInfos
│   │   └── Produce a full build result with correct dependency ownership
│
├── B4. Revert Unchanged Assets To Full Build State
│   │   ├── Compare build result against the immutable Full Build manifest
│   │   ├── For assets unchanged relative to Full Build:
│   │   │   revert catalog/bundle reference to the original local bundle
│   │   ├── For assets changed or new relative to Full Build:
│   │   │   keep them in update bundles
│   │   └── Delete or ignore build artifacts that are no longer referenced after revert
│
└── B5. Store Update Bundle Info
    ├── Purpose: capture build output metadata (bundleHash, size, assetGuids per update bundle) for Phase C to generate the mixed catalog (BundleRecordEntry and asset→bundle mapping).
    ├── bundleName → bundleHash
    ├── bundleName → size
    └── bundleName → assetGuids[]
```

### Phase C: Catalog Generation

**Catalog scope**: Same rule as Full Build — **entry-level only** (KeyToGuid). The catalog stays minimal and exposes only addressable resources; non-entry dependencies are not written to the catalog.

```
Phase C: Catalog Generation (Mixed Local/Remote)
│
├── C1. Start from the original Full Build baseline
│   ├── Base = original Full Build asset/bundle data from the immutable manifest
│   └── Do NOT start from the previous update catalog
│
├── C2. Apply Post-Revert Latest State (entries only)
│   │
│   │   For each UNCHANGED entry:
│   │   ├── bundleRecord → original Full Build bundle
│   │   ├── contentLocation = StreamingAssets (3)
│   │   └── Determined by the Phase B revert step, not by "not selected into B1" alone
│   │
│   │   For each MODIFIED entry:
│   │   ├── bundleRecord → update bundle kept after Phase B revert
│   │   ├── contentLocation = Remote (2)
│   │   └── If still different from the original Full Build, it must remain Remote in the latest catalog
│   │
│   │   For each NEW entry:
│   │   ├── Add new AssetRecordEntry + NameAliasEntry
│   │   ├── bundleRecord → update bundle kept after Phase B revert
│   │   └── contentLocation = Remote (2)
│   │
│   │   REMOVED entries: not present in current BuildPlan KeyToGuid, so they do not appear in the
│   │   new catalog (no explicit "remove" step needed during packaging).
│   │
│   └── BundleRecordEntry for update bundles: bundleNameIndex, bundleHash, size, contentLocation = 2 (Remote)
│
├── C3. Generate Catalog Files
│   └── Local: `{buildOutputRoot}/{Platform}/hc/HyperCatalog.bin` (fixed name)
│   └── Remote (when `buildRemoteCatalog`): versioned `HyperCatalog_{fullBuildVersion}.bin` + `.hash` in
│       `GetResolvedRemoteCatalogBuildFolder` (typically `{project}/ServerData/Production/{Platform}/`) —
│       same folder layout as Full Build remote catalog output (see CONVENTIONS §3)
│
└── C4. Generate settings.json (same as Full Build with remote catalog URLs)
```

#### Phase C: second-or-later Update Build rule

For Update Build #2, #3, ...:

- The baseline is still the **same original Full Build manifest**.
- The latest catalog is rebuilt from scratch:
  - assets equal to the original Full Build → Local
  - assets still different from the original Full Build → Remote in the **current** update output
  - new assets → Remote in the **current** update output
  - removed assets → absent
- Previously published remote bundles are **not** imported into the new catalog as an extra merge step.
- If an asset was already remote in Update Build #1 and is still different from Full Build during Update Build #2, it must appear again in Update Build #2's remote output so the new catalog remains self-consistent.

### Phase D: Output

```
Phase D: Output
│
├── When buildRemoteCatalog = true (typical CDN publish):
│   └── Resolved remote catalog folder (e.g. {project}/ServerData/Production/{Platform}/)
│       ├── HyperCatalog_{fullBuildVersion}.bin + .hash   ← written in settings generation step
│       └── Latest update *.bundle files                  ← same folder as .bin/.hash (CopyUpdateBundlesToServerData)
├── When buildRemoteCatalog = false:
│   └── Update bundles → legacy ServerData/{Platform}/Bundles/
└── Build Manifest is NOT regenerated (future Update Builds diff against same manifest).
```

Old remote bundles may remain on CDN temporarily for rollback / delayed cleanup, but they are not part of the latest catalog unless explicitly referenced by that catalog.

---

## 4. SBP Integration

HyperContent uses the full SBP `ContentPipeline.BuildAssetBundles` API with a custom task list for all bundle builds.

| Build Type | SBP Benefit |
|------------|------------|
| **Full Build** | Build Cache: subsequent full builds skip unchanged content. First build same speed. Object-level dependency analysis ensures accurate bundle dependencies. |
| **Update Build** | Build still uses the current full layout after update-group mutation. Cache may avoid rewriting unchanged bundles; revert ensures the final publishable output keeps only the needed update bundles. Update bundles are removed from StreamingAssets after being copied to ServerData. |
| **Iterative Dev** | Major benefit: rapid rebuild during development. |

### Build API

```csharp
// Full Build and Update Build both use the full SBP pipeline:
var buildParams = new BundleBuildParameters(buildTarget, targetGroup, outputPath);
buildParams.BundleCompression = GetSbpCompression(config.compression);

var buildContent = new BundleBuildContent(assetBundleBuilds);
var buildTasks = DefaultBuildExecutor.CreateBuildTaskListForUpdate(); // Custom task list

IBundleBuildResults results;
var exitCode = ContentPipeline.BuildAssetBundles(buildParams, buildContent, out results, buildTasks);

// After build: extract accurate bundle dependencies from SBP results
DefaultBuildExecutor.RebuildBundleDependenciesFromSbpResults(context, results);
// BundleInfos[bundleName].Dependencies provides ground-truth bundle→bundle edges
```

### Bundle Dependency Accuracy

The full SBP pipeline provides **object-level dependency analysis** (via `ContentBuildInterface.GetPlayerDependenciesForObjects`), which ensures:

- **Transitive dependencies** (e.g. Prefab → Material → Shader) are fully captured even when intermediate assets are not explicitly marked
- **Bundle-to-bundle dependencies** in `IBundleBuildResults.BundleInfos[x].Dependencies` reflect the actual serialized object references, not just asset-level `AssetDatabase` queries
- The `DependencyAnalyzer.BuildBundleDependencies` method (using `AssetDatabase.GetDependencies(path, true)`) is retained as a **pre-build estimate** for validation and editor tools, but is overwritten by SBP results during actual builds

SBP Build Cache and Content Update are **independent mechanisms** that complement each other:
- SBP Cache = build speed optimization (build-level)
- Content Update = download size minimization (catalog-level)

### Update Build: Asset-level dependency restore (mixed catalog)

The mixed catalog an Update Build emits must carry `AssetRecordEntry.dependencyBundles` for **every** asset, not just the ones rebuilt this round (otherwise unchanged assets would fail at runtime with `CATALOG_ASSET_DEPS_MISSING` under `DependencyLoadMode.AssetLevel`). Because SBP only regenerates write-data for changed/new assets, `UpdateBuildExecutor.GenerateMixedCatalog` sources the per-asset dependency bundles two ways:

- **Changed / new assets** — from `BuildContext.AssetDependencyBundles`, freshly computed this round by `BuildAssetDependencyBundlesFromSbp` (+ AssetDatabase augmentation).
- **Unchanged assets** — restored from `BuildManifest.CachedAssetState.dependencyBundleNames` (the Full Build snapshot).

Bundle **names** are then mapped to catalog bundle indices (`ResolveDepBundleIndices`); a name missing from the current catalog index is dropped with a warning. `ValidateAssetDepsSubsetOfBundleClosure` runs diagnostic-only over this round's changed/new assets.

### Update Build: StreamingAssets Cleanup

During Update Build, the full-context SBP build outputs all bundles (including update bundles) to `StreamingAssets`. After the build, `CopyUpdateBundlesToServerData` copies update bundles to the ServerData directory for CDN upload and then **deletes them from StreamingAssets** — update bundles should only exist on the remote server, not in the local player build.

---

## 5. Comparison with Addressables

| Aspect | Addressable | HyperContent |
|--------|------------|--------------|
| **State file** | `addressables_content_state.bin` (BinaryFormatter) | `build_manifest.json` (JSON, human-readable) |
| **Change detection** | Per-asset `AssetDependencyHash` comparison | Same: `AssetDatabase.GetAssetDependencyHash()` |
| **Build API** | `ContentPipeline.BuildAssetBundles` (full SBP with custom tasks) | Same: `ContentPipeline.BuildAssetBundles` (full SBP with custom task list) |
| **Bundle dependency source** | SBP `IBundleBuildResults.BundleInfos` (object-level) | Same: SBP `IBundleBuildResults.BundleInfos` (object-level, overwrites pre-build estimates) |
| **Asset relocation** | Moves modified assets to new "Content Update" Group, then rebuilds ALL groups | Creates update groups from the dependency-expanded changed set, then rebuilds the current full layout |
| **Catalog revert** | `RevertUnchangedAssetsToPreviousAssetState` — reverts unchanged assets' catalog entries to old bundle paths after rebuilding all groups | Same principle is required: revert unchanged assets/catalog references to the original Full Build state after full-context build |
| **Build scope** | Rebuilds ALL groups (SBP Cache handles speed) | Also rebuilds from the current full layout; final publishable output is reduced by cache + revert |
| **Update bundle cleanup** | N/A (Addressables manages output locations internally) | Update bundles are deleted from StreamingAssets after copying to ServerData |
| **State regeneration** | `.bin` is NOT regenerated during update builds | `build_manifest.json` is NOT regenerated during update builds |

### Key Simplification

Addressables rebuilds with the full current layout, then reverts unchanged catalog/bundle references by using previous state. HyperContent should follow the same build-time principle: create the current update layout, build with full context, then revert unchanged assets back to the immutable Full Build state. APK bundles remain immutable in StreamingAssets; the simplification is about state ownership and publish output, not about skipping full-context dependency resolution.

---

## 6. Important Rules

1. **Build Manifest must be preserved** from the original Full Build. Losing it requires a new Full Build + new APK release.
2. **Each Update Build diffs against the SAME original manifest.** The manifest is never regenerated during Update Builds.
3. **APK bundles are immutable.** They ship with the player build and are never modified post-release.
4. **Multiple Update Builds do not patch the previous update catalog in place.** Each build regenerates the latest catalog from the original Full Build baseline plus the current update result.
5. **New Full Build invalidates all previous updates.** A new APK = new Full Build = new manifest. Old remote bundles become obsolete.
6. **Update bundle naming must avoid collisions.** Include build version or sequence number in update bundle names.

---

## 7. End-to-End Example

### Scenario

Full Build (v1.0) shipped with APK:
- Bundle `ui_common`: AssetA, AssetB, AssetC (Local)
- Bundle `textures_hero`: AssetD, AssetE (Local)

After release, AssetB (texture) and AssetD (material) are modified.

### Update Build #1

```
1. Load build_manifest.json (from v1.0 Full Build)

2. Change Detection:
   AssetA: hash unchanged ✓
   AssetB: hash CHANGED ✗ (texture modified)
   AssetC: hash unchanged ✓
   AssetD: hash CHANGED ✗ (material modified)
   AssetE: hash unchanged ✓

3. Bundle Assignment:
   AssetB (was in ui_common) → ui_common_update_v2.bundle
   AssetD (was in textures_hero) → textures_hero_update_v2.bundle

4. Run full-context build from the current update layout,
   then revert unchanged assets back to Full Build state

5. Final publishable remote output:
   - ui_common_update_v2.bundle
   - textures_hero_update_v2.bundle

6. Generate Mixed Catalog:
   AssetA → ui_common (Local, StreamingAssets)
   AssetB → ui_common_update_v2 (Remote, CDN)
   AssetC → ui_common (Local, StreamingAssets)
   AssetD → textures_hero_update_v2 (Remote, CDN)
   AssetE → textures_hero (Local, StreamingAssets)

7. Upload to CDN — when `buildRemoteCatalog`, put **bundles + catalog + hash in the same resolved remote folder**
   (e.g. `ServerData/Production/{Platform}/`; see CONVENTIONS §3):
   - ui_common_update_v2.bundle (contains only AssetB)
   - textures_hero_update_v2.bundle (contains only AssetD)
   - HyperCatalog_v2.bin + .hash (`v2` here = shipped Full Build `buildVersion` from manifest, not the update run’s timestamp)
```

### Update Build #2

Assume no new Full Build was shipped. AssetB is still different from v1.0, and AssetE is now also modified.

```
1. Load the SAME build_manifest.json from v1.0

2. Change Detection (against v1.0 baseline):
   AssetA: unchanged vs v1.0
   AssetB: still CHANGED vs v1.0
   AssetC: unchanged vs v1.0
   AssetD: unchanged vs v1.0
   AssetE: CHANGED vs v1.0

3. Bundle Assignment:
   AssetB → ui_common_update_v3.bundle
   AssetE → textures_hero_update_v3.bundle

4. Run full-context build from the current update layout,
   then revert unchanged assets back to Full Build state

5. Generate latest mixed catalog from the original Full Build baseline:
   AssetA → ui_common (Local)
   AssetB → ui_common_update_v3 (Remote)
   AssetC → ui_common (Local)
   AssetD → textures_hero (Local)
   AssetE → textures_hero_update_v3 (Remote)

6. Publish HyperCatalog_v3.bin + referenced bundles
   Older remote bundles from v2 may still exist on CDN, but v3 catalog should not depend on them unless it explicitly references them.
```

### Runtime (on device)

```
1. App starts → CatalogLocator detects new catalog hash
2. Downloads new catalog (HyperCatalog_v2.bin)
3. BundleDownloadManager finds 2 Remote bundles pending
4. Downloads ui_common_update_v2.bundle + textures_hero_update_v2.bundle
5. Ready:
   - AssetA loads from APK (ui_common, StreamingAssets)
   - AssetB loads from cache (ui_common_update_v2, downloaded)
   - AssetC loads from APK (ui_common, StreamingAssets)
   - AssetD loads from cache (textures_hero_update_v2, downloaded)
   - AssetE loads from APK (textures_hero, StreamingAssets)
```

User downloads: 2 small bundles + catalog ≈ minimal transfer.

---

## 8. Key Classes (To Be Implemented)

| Class | File | Owner | Purpose |
|-------|------|-------|---------|
| `BuildManifest` | `Editor/Build/BuildManifest.cs` | Owner1 | Data structure + serialize/deserialize |
| `BuildManifestManager` | `Editor/Build/BuildManifestManager.cs` | Owner1 | Save manifest during Full Build, load during Update Build |
| `ContentChangeDetector` | `Editor/Build/ContentChangeDetector.cs` | Owner1 | Compare current vs manifest (GUID-based); output modified/new/removed. **Must implement GetStaticContentDependenciesForEntries and GetEntriesDependentOnModifiedEntries** for dependency expansion (Phase A3b). Removed list is for reporting only. |
| `UpdateBuildExecutor` | `Editor/Build/UpdateBuildExecutor.cs` | Owner1 | IBuildExecutor implementation for Update Build |
| `IUpdateBundleGroupingStrategy` | `Editor/Build/IUpdateBundleGroupingStrategy.cs` | Owner1 | Interface for update-bundle grouping. Implement for custom strategy; built-in: GroupByOriginalBundle, SingleBundle, ReRunGrouping. Input: dependency-expanded changed assets + version; output: updateBundleName → list of AssetInfo. |
| `UpdateBundleAssigner` | `Editor/Build/UpdateBundleAssigner.cs` | Owner1 | Resolves strategy (id or custom instance), invokes built-in or IUpdateBundleGroupingStrategy.GroupChangedAssets(), returns mapping from updateBundleName to list of AssetInfo for the dependency-expanded changed set. |

---

## Related Docs

- [ADDRESSABLE_UPDATE_BUILD_FLOW.md](ADDRESSABLE_UPDATE_BUILD_FLOW.md) — Reference: Unity Addressables' update-build flow
- [BUILD_LIFECYCLE.md](BUILD_LIFECYCLE.md) — Full Build flow (where Build Manifest is generated)
- [CONTENT_UPDATE_FLOW.md](CONTENT_UPDATE_FLOW.md) — Runtime update flow (catalog download, bundle download on device)
- [CATALOG_SCHEMA.md](CATALOG_SCHEMA.md) — Catalog format with `contentLocation` field
- [HOT_UPDATE_TODO.md](HOT_UPDATE_TODO.md) — Task list for content update implementation
