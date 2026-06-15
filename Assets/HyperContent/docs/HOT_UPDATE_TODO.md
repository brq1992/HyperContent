# HyperContent Hot Update Implementation — Task Tracking

Task list for implementing the build-side content update pipeline. Each section lists tasks per Owner with priority and dependencies.

Reference: [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md) for the full design.

---

## Status Legend

| Status | Meaning |
|--------|---------|
| ✅ | Done |
| 🔧 | In Progress |
| ⬚ | Not Started |

---

## Owner0: Core Interfaces & Specifications

Owner0 defines the data structures, error codes, and specifications that other Owners depend on. **O0-1 ~ O0-3 must be completed first** (prerequisite for all other work).

| # | Task | Status | Priority | Details |
|---|------|--------|----------|---------|
| O0-1 | Add `contentLocation` to `CatalogSchema.BundleRecordEntry` | ✅ | P0 | `int contentLocation` field. Maps to ContentLocation enum (3=StreamingAssets, 2=Remote). SCHEMA_VERSION bumped to 3. |
| O0-2 | Update `LocalContentCatalog` to use `contentLocation` | ✅ | P0 | `BundleInfo.Location` now reads from `rec.contentLocation` instead of hardcoded Remote. `LocalPath` populated for StreamingAssets bundles. |
| O0-3 | Define Build Manifest specification | ✅ | P0 | `AssetState`, `CachedAssetState`, `CachedBundleState`, top-level `BuildManifest` structures defined in CONTENT_UPDATE_BUILD_FLOW.md §1. |
| O0-4 | Add Content Update error codes to `Constants.cs` | ✅ | P1 | Added 7001-7006 (BUILD_MANIFEST_NOT_FOUND, BUILD_MANIFEST_INVALID_FORMAT, BUILD_MANIFEST_LOAD_FAILED, CHANGE_DETECTION_FAILED, UPDATE_BUNDLE_BUILD_FAILED, MIXED_CATALOG_GENERATION_FAILED) to `Shared/Constants.cs` ErrorCode class. |
| O0-5 | Update CATALOG_SCHEMA.md | ✅ | P1 | Already documented: `contentLocation` in §2.6 BundleRecord; mixed catalog behavior in §2.8 (Full Build → StreamingAssets, Update Build → mixed); Build Manifest summary in §6. |
| O0-6 | Fix ARCHITECTURE.md §9 + OWNERS.md Update Build description | ✅ | P1 | §9 "Key Simplification" rewritten to "Build Approach vs Addressables": acknowledges full-context build + revert principle; lists actual simplifications as comparison table (state format, baseline, catalog strategy, group config, publish output). OWNERS.md Owner1 "Update Build" steps rewritten: step 4 changed from "Only build new bundles" to "Full-context build + revert (Phase B2–B4)" with correct description. Both now aligned with CONTENT_UPDATE_BUILD_FLOW.md. |
| O0-7 | Verify & complete CONVENTIONS.md | ✅ | P2 | Added §3 "Build Manifest Rules" (manifest immutability, preservation, same-baseline diffing, catalog-from-scratch) and "Update Bundle Version Uniqueness" (version segment must be unique per build). |
| O0-8 | Update OWNERS.md | ✅ | P2 | Added `IUpdateBundleGroupingStrategy.cs` to Owner1 file list. Updated descriptions for ContentChangeDetector, UpdateBundleAssigner, UpdateBuildExecutor. Update Build steps already fixed in O0-6. |

---

## Owner1: Build Pipeline

Owner1 implements the build-side content update pipeline. Depends on Owner0 specifications (O0-1 ~ O0-3).

**Critical design rule**: Update Build uses **full-context build + revert**, NOT "build only changed bundles." See CONTENT_UPDATE_BUILD_FLOW.md Phase B for details. Both `DefaultBuildExecutor` and `UpdateBuildExecutor` now use the full SBP `ContentPipeline.BuildAssetBundles` API with a custom task list. Bundle dependencies are extracted from `IBundleBuildResults.BundleInfos` (object-level accuracy). Update bundles are removed from StreamingAssets after copying to ServerData.

| # | Task | Status | Priority | Depends On | Details |
|---|------|--------|----------|------------|---------|
| O1-1 | Switch to full SBP `ContentPipeline` | ✅ | P0 | — | Replaced `CompatibilityBuildPipeline.BuildAssetBundles` with full SBP `ContentPipeline.BuildAssetBundles` (custom task list) in both `DefaultBuildExecutor.BuildBundles()` and `UpdateBuildExecutor.ExecuteFullContextBuild()`. Bundle dependencies now extracted from `IBundleBuildResults.BundleInfos` (object-level, overwriting pre-build estimates). Update bundles deleted from StreamingAssets after copy to ServerData. |
| O1-2 | Implement `BuildManifest` data structure | ✅ | P0 | O0-3 | `Editor/Build/BuildManifest.cs` — Implement per O0-3 spec (CONTENT_UPDATE_BUILD_FLOW.md §1): `AssetState` (guid, hash), `CachedAssetState` (guid, hash, bundleName, internalId, dependencies:AssetState[]), `CachedBundleState` (bundleName, bundleHash, size, assetGuids), top-level `BuildManifest` (buildVersion, buildTimestamp, cachedAssets, cachedBundles). JSON serialize/deserialize. |
| O1-3 | Implement `BuildManifestManager` | ✅ | P0 | O1-2 | `Editor/Build/BuildManifestManager.cs` — **Save** (Full Build only): for each asset in BuildPlan, record guid, hash=`AssetDatabase.GetAssetDependencyHash(GUIDToAssetPath(guid))`, bundleName, internalId (addressableName), dependencies as AssetState[]; for each bundle, compute SHA256 of bundle file, record size and assetGuids. Serialize to `{buildOutputRoot}/{Platform}/build_manifest.json`. **Load** (Update Build): deserialize from same path. **Fail if file not found** (error code 7001). |
| O1-4 | Integrate manifest save into `DefaultBuildExecutor` | ✅ | P0 | O1-3 | After Full Build Step 5 (report), call `BuildManifestManager.Save(context)`. Manifest is created **ONLY** during Full Build; never regenerated during Update Build. This file must be preserved for all future Update Builds. |
| O1-5 | Implement `ContentChangeDetector` (Phase A) | ✅ | P0 | O1-2 | `Editor/Build/ContentChangeDetector.cs` — Full Phase A implementation: **A1** load manifest via `BuildManifestManager.Load()`; **A2** get current asset set via `IBundleGroupingTool.GeneratePlan()` → BuildPlan (same tool as Full Build); **A3** compare by **GUID only** — for each asset in current plan, look up manifest by GUID: not found → `NewAssetInfo`; found → run `HasAssetOrDependencyChanged()` (resolve path from GUID via `AssetDatabase.GUIDToAssetPath`, compare asset hash + all dependency hashes; see CONTENT_UPDATE_BUILD_FLOW.md comparison rule) → if changed → `ModifiedAssetInfo`; **A3b dependency expansion** (required): implement `GetStaticContentDependenciesForEntries` + `GetEntriesDependentOnModifiedEntries` → produce `ExpandedChangedAssets`; **A4** detect removed assets (reporting only, not used for packaging); **A5** summary output. |
| O1-6a | Define `IUpdateBundleGroupingStrategy` interface | ✅ | P1 | — | `Editor/Build/IUpdateBundleGroupingStrategy.cs` — Input: `IReadOnlyList<ChangedAssetInfo>` (guid, assetPath, sourceBundleName) + `string version`. Output: `Dictionary<string, List<AssetInfo>>` (updateBundleName → assets). Contract: each asset in exactly one update bundle; names follow CONVENTIONS (max 128 chars, no path separators). See CONTENT_UPDATE_BUILD_FLOW.md §B1. |
| O1-6b | Implement `UpdateBundleAssigner` + built-in strategies | ✅ | P1 | O1-5, O1-6a | `Editor/Build/UpdateBundleAssigner.cs` — Resolve strategy from `BuildConfig.updateBundleGroupingStrategy` (new field, see O1-6c). Built-in strategies: **GroupByOriginalBundle** (default): group by sourceBundleName → `{sourceBundleName}_update_{version}`; **SingleBundle**: all → `content_update_{version}`; **ReRunGrouping**: re-run `IBundleGroupingStrategy` on changed set → names + `_update_{version}`. Custom: invoke user-provided `IUpdateBundleGroupingStrategy.GroupChangedAssets()`. |
| O1-6c | Add Update Build fields to `BuildConfig` | ✅ | P1 | — | Add to `BuildConfig`: `updateBundleGroupingStrategy` (enum or string, default `GroupByOriginalBundle`), `remoteBundleLoadUrl` (CDN base URL for bundles). Add `ServerDataOutputDirectory` property for `ServerData/{Platform}/Bundles/`. |
| O1-7 | Implement `UpdateBuildExecutor` (Phase B) | ✅ | P1 | O1-5, O1-6b | `Editor/Build/UpdateBuildExecutor.cs` — `IBuildExecutor` for Update Build. **Must implement full-context build + revert** per CONTENT_UPDATE_BUILD_FLOW.md Phase B: **B2** create update groups from B1 mapping (move ExpandedChangedAssets into update groups), derive **full current layout** (unchanged groups retain original bundle ownership); **B3** build using `ContentPipeline.BuildAssetBundles()` (full SBP, custom task list) with the full current layout → Unity/SBP sees full bundle graph, not only changed assets; extract accurate bundle dependencies from `IBundleBuildResults.BundleInfos`; **B4** revert unchanged assets' catalog/bundle references to original Full Build state (compare build result against immutable manifest; unchanged → local bundle, changed/new → update bundle); **B5** store update bundle metadata (bundleHash, size, assetGuids per update bundle) for Phase C. Update bundles are deleted from StreamingAssets after copying to ServerData. |
| O1-8 | Mixed catalog generation (Phase C) | ✅ | P1 | O0-1, O1-7 | Extend `CatalogGenerator` for mixed catalog per CONTENT_UPDATE_BUILD_FLOW.md Phase C: **C1** start from original Full Build baseline (not from previous update catalog); **C2** for each asset: unchanged → bundleRecord from Full Build, `contentLocation=3` (StreamingAssets); modified → bundleRecord from update bundle (post-revert), `contentLocation=2` (Remote); new → add new AssetRecordEntry + NameAliasEntry, `contentLocation=2`; removed → omit; **C3** generate `ServerData/HyperCatalog_{buildVersion}.bin` + `.hash`; **C4** generate `settings.json` with remote catalog URLs + `remoteBundleBaseUrl`. **Second-or-later Update Build**: same original manifest baseline, catalog rebuilt from scratch each time. |
| O1-9 | Update Build menu / window | ✅ | P2 | O1-7 | Add "Update Build" option to `HyperContentBuildWindow` and `HyperContentBuildMenu`. Include: manifest path selection/display, strategy selection (GroupByOriginalBundle / SingleBundle / ReRunGrouping), change detection preview (show modified/new/removed counts before building). |
| O1-10 | Add `DeterministicAssetBundle` flag | ✅ | P2 | O1-1 | Ensure deterministic builds for reliable hash comparison. SBP default behavior, but verify `BuildAssetBundleOptions.DeterministicAssetBundle` is set. |

---

## Owner2: Runtime Facade & Providers

Owner2's changes are minimal — the runtime already supports Local and Remote providers. Main task is ensuring correct routing based on `contentLocation`.

| # | Task | Status | Priority | Depends On | Details |
|---|------|--------|----------|------------|---------|
| O2-1 | Verify `BundleFileProvider` handles StreamingAssets path | ✅ | P1 | O0-2 | Verified: `ResolveFilePath()` correctly resolves via `_bundleBasePath` (= `BundleBasePath` = `streamingAssetsPath/{Platform}/Bundles/`) and uses `FileExistsOrIsStreamingAssets()` for Android `jar:file://` path compatibility. `AssetBundle.LoadFromFile` handles Android paths internally. |
| O2-2 | Verify `RemoteBundleProvider` handles Remote bundles | ✅ | P1 | O0-2 | Verified: Download + local store cache + memory fallback flow is correct. Hash is now received via `ResourceLocation.Data` (fixed in O2-3). |
| O2-3 | Update `CollectBundlesRecursive` routing in `LocalContentCatalog` | ✅ | P1 | O0-1 | ProviderId routing already correct: `contentLocation==Remote(2)` → `RemoteBundleProvider`, else → `BundleFileProvider`. **Fixed**: `rec.bundleHash` now passed as `ResourceLocation.Data` so `RemoteBundleProvider` can use it for cache verification during `Save()`. |
| O2-4 | Add `remoteBundleBaseUrl` to initialization | ✅ | P1 | O3-4 | Added `remoteBundleBaseUrl` and `HasRemoteBundles` to `RuntimeSettings` (coordinated with O3-4). `InitializeBundleModeAsync()` now calls `catalog.SetBaseUrl()` and creates `LocalBundleStore` + `HttpBundleTransport` when `HasRemoteBundles` is true, enabling `RemoteBundleProvider` registration. |

---

## Owner3: Content Update & Transfer

Owner3's runtime content update flow is already designed. Changes are minor — adapting to the `contentLocation` field in the mixed catalog.

| # | Task | Status | Priority | Depends On | Details |
|---|------|--------|----------|------------|---------|
| O3-1 | Update `BundleDownloadManager` for mixed catalog | ✅ | P1 | O0-1 | `CheckAllPendingDownloads()` only enumerates bundles with `contentLocation == Remote (2)`. Local bundles (`StreamingAssets`, 3) are skipped — they ship with APK. Comment added. |
| O3-2 | Update `BundleProvider` for Local/Remote routing | ✅ | P1 | O0-2 | `BundleProvider` checks `BundleInfo.Location`: `StreamingAssets` → local file load (no download), `Remote` → download/cache then load. Comment added. |
| O3-3 | Verify `LocalBundleStore` handles update bundles | ✅ | P2 | — | Verified: update bundles (versioned names) stored by full bundleName; hash from catalog `BundleRecordEntry.bundleHash` used in Save/VerifyHash. Class comment added. |
| O3-4 | Add `remoteBundleBaseUrl` to `RuntimeSettings` | ✅ | P1 | — | `remoteBundleBaseUrl` field and `HasRemoteBundles` property added to `RuntimeSettings` by Owner2 (O2-4 coordination). `HyperContent.InitializeBundleModeAsync()` now reads from settings and calls `catalog.SetBaseUrl()` + creates `LocalBundleStore`/`HttpBundleTransport`. Owner3 should verify `CatalogLocator` integration is sufficient. |

---

## Cross-Owner Integration Tasks

| # | Task | Status | Priority | Owners | Details |
|---|------|--------|----------|--------|---------|
| X-1 | End-to-end Full Build test | ✅ | P1 | O1, O2 | Full Build → verify: manifest generated at `{buildOutputRoot}/{Platform}/build_manifest.json`; all bundles `contentLocation=3` (StreamingAssets); runtime loads all assets from StreamingAssets correctly. **Verified manually.** |
| X-2 | End-to-end Update Build test | ✅ | P1 | O1, O2, O3 | Modify asset → Update Build → verify: full-context build + revert executed; only changed bundles in `ServerData/`; mixed catalog has correct Local/Remote routing; runtime downloads only changed bundles from CDN. **Verified manually.** |
| X-3 | Second Update Build test | ✅ | P2 | O1, O2, O3 | After Update Build #1, modify different asset → Update Build #2 → verify: catalog rebuilt from **same original manifest** (not from Update #1 output); previously-remote asset re-evaluated against original baseline; catalog is self-consistent and does not import previous update catalog entries. **Verified manually.** |
| X-4 | Offline fallback test | ✅ | P2 | O2, O3 | No CDN available → verify: Local bundles (StreamingAssets) still load correctly; Remote bundles fail gracefully with appropriate error code; user can still use base-build content. **Verified manually.** |

---

## Implementation Order (Recommended)

```
Phase 1: Foundation (Owner0 + Owner1)
  ├── O0-1 ✅  CatalogSchema contentLocation
  ├── O0-2 ✅  LocalContentCatalog uses contentLocation
  ├── O0-3 ✅  Build Manifest spec
  ├── O0-4 ✅  Error codes in Constants.cs
  ├── O0-5 ✅  CATALOG_SCHEMA.md (already documented)
  ├── O0-6 ✅  ARCHITECTURE.md §9 + OWNERS.md Update Build fix
  ├── O0-7 ✅  CONVENTIONS.md (Build Manifest rules + version uniqueness)
  ├── O0-8 ✅  OWNERS.md (new Owner1 files)
  ├── O1-1 ✅  Switch to full SBP ContentPipeline.BuildAssetBundles
  ├── O1-2 ✅  BuildManifest data structure
  └── O1-3 ✅  BuildManifestManager

Phase 2: Change Detection (Owner1)
  ├── O1-4 ✅  Integrate manifest save into Full Build
  └── O1-5 ✅  ContentChangeDetector (Phase A: A1-A5 including dependency expansion)

Phase 3: Update Build (Owner1)
  ├── O1-6a ✅  IUpdateBundleGroupingStrategy interface
  ├── O1-6b ✅  UpdateBundleAssigner + built-in strategies (GroupByOriginalBundle, SingleBundle, ReRunGrouping)
  ├── O1-6c ✅  BuildConfig update fields
  ├── O1-7  ✅  UpdateBuildExecutor (Phase B: full-context build + revert)
  └── O1-8  ✅  Mixed catalog generation (Phase C: from original Full Build baseline)

Phase 4: Runtime Integration (Owner2 + Owner3)
  ├── O2-1 ✅   BundleFileProvider StreamingAssets path (verified)
  ├── O2-2 ✅   RemoteBundleProvider Remote bundles (verified)
  ├── O2-3 ✅   CollectBundlesRecursive routing + bundleHash data
  ├── O2-4 ✅   remoteBundleBaseUrl in initialization + BundleStore/Transport creation
  ├── O3-1 ✅   BundleDownloadManager mixed catalog (Remote only)
  ├── O3-2 ✅   BundleProvider Local/Remote routing
  ├── O3-3 ✅   LocalBundleStore update-bundle verification
  └── O3-4 ✅   RuntimeSettings remoteBundleBaseUrl (done via O2-4 coordination)

Phase 5: Integration Testing
  ├── X-1 ✅    Full Build E2E
  ├── X-2 ✅    Update Build E2E
  ├── X-3 ✅    Second Update Build E2E (same manifest baseline)
  └── X-4 ✅    Offline fallback
```

---

## Notes

- Owner0 tasks (O0-1 ~ O0-3) are **prerequisites** for all other work. These define the data contracts.
- **Owner1 所有任务已完成** (O1-1 ~ O1-10 全部 ✅)。已完成从 `CompatibilityBuildPipeline` 到完整 SBP `ContentPipeline.BuildAssetBundles` 的迁移，Bundle 依赖从 `IBundleBuildResults.BundleInfos` 提取（对象级精度）。Build Manifest、Change Detection、Update Build Executor（full-context build + revert）、Mixed Catalog Generation、UI 菜单/窗口均已实现。Update Build 后 update bundle 会从 StreamingAssets 删除。
- **Owner0 所有任务已完成** (O0-1 ~ O0-8 全部 ✅)。所有规范、错误码、文档已就绪，Owner1 可以开始实现。
- **Owner2 所有任务已完成** (O2-1 ~ O2-4 全部 ✅)。Provider 路由已验证，bundleHash 传递已修复，初始化流程已支持远程 bundle（含 `LocalBundleStore` + `HttpBundleTransport` 自动创建）。同时完成了 O3-4 (`RuntimeSettings.remoteBundleBaseUrl`)。
- **Owner3 所有任务已完成** (O3-1 ~ O3-3 全部 ✅)。BundleDownloadManager 仅枚举 Remote 包、BundleProvider 按 Location 路由、LocalBundleStore 对带版本号的 update bundle 的存储与 hash 校验已确认并补充注释。
- **Phase 5 (X-1 ~ X-4)**：跨 Owner 集成测试（Full Build E2E、Update Build E2E、第二次 Update Build、离线降级）已在项目中通过人工验证，标记为 ✅。
- Each Update Build diffs against the **same original Full Build manifest** — the manifest is never regenerated during Update Builds.
- `DefaultBuildExecutor` and `UpdateBuildExecutor` both use full SBP `ContentPipeline.BuildAssetBundles` with custom task lists. Bundle dependencies are sourced from `IBundleBuildResults.BundleInfos` (object-level accuracy), overwriting the `DependencyAnalyzer` pre-build estimates.
