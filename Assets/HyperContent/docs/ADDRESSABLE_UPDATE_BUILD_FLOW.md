# Addressables Update Build — Full Flow (First Half + Second Half)

This document describes the **complete** Addressables "Update a Previous Build" flow as implemented in code: the **pre-build (first half)** and the **build + revert + output (second half)**. No modifications to existing docs; reference only.

---

## Overview

| Phase | Purpose | When / Where |
|-------|---------|--------------|
| **First half** | Resolve .bin path; optionally find modified/new in **static** groups; optionally show restrictions window; user may **Apply** → create new remote group and move entries. Result: **current group layout** (possibly updated). | Before the data builder runs. `AddressablesBuildMenuUpdateAPreviousBuild.OnUpdateBuild` in `AddressableAssetsSettingsGroupEditorBuildMenu.cs`. |
| **Second half** | Run **full build** with current layout; **revert** unchanged assets to previous bundle using .bin; write catalog; do **not** update .bin; only **changed** bundles end up on disk (SBP cache + revert). | `ContentUpdateScript.BuildContentUpdate` → `BuildData` (e.g. `BuildScriptPackedMode`) + `RevertUnchangedAssetsToPreviousAssetState`. |

---

## LLM Summary

**One-sentence mental model**

Addressables Update Build is **not** "build modified assets only"; it is "optionally rearrange group layout in pre-build, then run a full build with `PreviousContentState`, and finally revert unchanged assets back to their previous bundle references."

### Non-negotiable facts

1. **`addressables_content_state.bin` is created during Full Build, not during Update Build.**
2. **`GatherModifiedEntriesWithDependencies()` is a pre-build analysis step for restrictions / preview / moving entries. Its output is not the direct bundle-build input.**
3. **The actual builder still processes all groups in the current layout and calls `ContentPipeline.BuildAssetBundles(...)` with the full bundle set.**
4. **`PreviousContentState` is what enables `RevertUnchangedAssetsToPreviousAssetState` after the build.**
5. **The effective Update Build output is "new catalog + bundles that still matter after revert", not "Unity only built changed bundles from the start".**
6. **Update Build does not regenerate `addressables_content_state.bin`; later updates continue using the same previous state until the next Full Build.**

### Reading order for AI

If you need to answer "how does Addressables Update Build really work?", read it in this order:

1. Pre-build: how `.bin` is resolved and how modified/static entries are discovered.
2. Apply step: how `CreateContentUpdateGroup + MoveEntries` changes the current layout.
3. Build step: how the builder still processes **all** groups and performs a full build.
4. Revert step: how unchanged assets are redirected back to previous bundles.
5. Persistence rule: why `.bin` is not rewritten during Update Build.

---

## First Half — Pre-build (Before BuildData)

**Entry:** User selects "Update a Previous Build". The build menu’s **OnPrebuild** runs first (`OnUpdateBuild`).

### 1. Resolve content state path

- Resolve path to `addressables_content_state.bin` (e.g. from settings, or cache under `Library/com.unity.addressables/...`).
- If no .bin found: may prompt user to select file or cancel. If user continues without .bin, `continueWithoutPreviousState = true` and restrictions check is skipped.

### 2. Check for content update restrictions (depends on option)

Behavior depends on **CheckForContentUpdateRestrictionsOption**:

| Option | Behavior |
|--------|----------|
| **Disabled** | Do nothing. Proceed to set `input.PreviousContentState` from .bin and run build. |
| **FailBuild** | Call `ContentUpdateScript.GatherModifiedEntriesWithDependencies(settings, path)`. If the result is non-empty (modified/new entries in **static** groups), **fail** the build (log error, set `doContentUpdate = false`). No build runs. No new group created. |
| **ListUpdatedAssetsWithRestrictions** (default) | Call `GatherModifiedEntriesWithDependencies(settings, path)`. If non-empty: **pause** build (`doContentUpdate = false`), open **ContentUpdatePreviewWindow** with that map and a **build callback**. When user clicks **"Apply and Continue"**: `ContentUpdatePreviewWindow` first calls `ContentUpdateScript.CreateContentUpdateGroup(settings, enabledEntries, groupName)` (which internally calls `settings.MoveEntries(items, contentGroup)`), **then** invokes the build callback. The build callback sets `input.PreviousContentState = cacheData` and runs `BuildPlayerContent`. So the build runs **after** the group layout has been mutated. If the user clicks **"Continue without changes"**: only the build callback is invoked (no `CreateContentUpdateGroup`); the build runs with the **current** (unchanged) layout. If user clicks **"Cancel build"**: window closes, no build runs. |

### 3. What GatherModifiedEntriesWithDependencies does (first half detail)

- **Load** `addressables_content_state.bin` → `AddressablesContentState` (cachedInfos, cachedBundles, etc.).
- **GatherExplicitModifiedEntries(settings, ref modifiedData, cacheData):**
  - `settings.GetAllAssets(allEntries, false, groupFilter)`: only groups with **BundledAssetGroupSchema** and **ContentUpdateGroupSchema** with **StaticContent == true** ("Cannot Change Post Release"). So only **static** groups are considered.
  - For each entry in those groups: if not in cache or `HasAssetOrDependencyChanged(cachedInfo)` (current hash vs .bin hash), treat as modified/new; add to list and set `FlaggedDuringContentUpdateRestriction = true`.
  - After collecting explicit modified entries: **`AddAllDependentScenesFromModifiedEntries(modifiedEntries)`** — any scenes that depend on the modified entries are also added to the modified list (ensures scene bundles are updated when their referenced assets change).
- **GetStaticContentDependenciesForEntries(settings, ref dependencyMap, groupGuidToCacheBundleName)** — for each explicitly modified entry (key in `dependencyMap`), call `AssetDatabase.GetDependencies(entry.AssetPath)` to find **what the modified entry depends on** (向下查依赖). For each addressable dependency found (`settings.FindAssetEntry(guid, true)`), apply two filters before adding it to the map:
  1. **`GetGroupGuidsWithUnchangedBundleName` filter**: before the per-entry loop, compute which groups' bundle names are unchanged. This is done by simulating `PrepGroupBundlePacking` for every group with a `BundledAssetGroupSchema`, **excluding** the already-modified entries (`entryFilter = entry => !entryGuidToDeps.ContainsKey(entry.guid)`), and comparing the resulting bundle name against the cached bundle name from `.bin` (`groupGuidToCacheBundleName[group.Guid]`). If they match, that group's GUID is added to `groupGuidsWithUnchangedBundleName`. **During the per-entry loop, if a dependency belongs to a group in this set, it is skipped** — its bundle content hasn't changed, so there is no restriction to report.
  2. **Static group check**: the dependency's `parentGroup` must have `ContentUpdateGroupSchema.StaticContent == true`. Non-static group dependencies are not flagged.
  3. **Not already a key**: the dependency must not already be a key in `dependencyMap` (i.e. not already an explicitly modified entry).
  4. If all checks pass: `dependencyMap[modifiedEntry].Add(depEntry)` and `depEntry.FlaggedDuringContentUpdateRestriction = true`.
  - **Implication**: "向下查依赖" means the function finds what the modified entry **references**, not who references it. If entry A (texture) is modified, and entry B (material) references A, this function does **not** find B — because A does not depend on B. Conversely, if entry A (material) is modified and depends on texture C (in a static group whose bundle name changed), then C would be found and added.

- **GetEntriesDependentOnModifiedEntries(settings, ref dependencyMap)** — the reverse direction (向上查依赖). Scans all entries in **static groups** (via `GetStaticGroups`: groups with both `ContentUpdateGroupSchema.StaticContent == true` and `BundledAssetGroupSchema`), builds a map of every static entry → its `AssetDatabase.GetDependencies`. Then collects all known modified entries (all keys + all values currently in `dependencyMap`). For each modified entry M, iterates all static entries: if static entry X's dependencies contain M's `AssetPath`, and M is a key in `dependencyMap`, then X is added to `dependencyMap[M]`. This captures entries that **reference** modified assets and would be affected by the modified assets being moved.
  - **Note**: this function does **not** apply the `groupGuidsWithUnchangedBundleName` filter (unlike `GetStaticContentDependenciesForEntries`). It only requires the dependent entry to be in a static group.

- **Return** a dictionary: modified entry → list of dependency entries. This is used only for **restrictions** (fail or show window); it is **not** passed into the build as "the bundle build input".

### 4. CreateContentUpdateGroup (only when user clicks Apply)

- Creates a new group with a unique name (e.g. "Content Update").
- Adds **BundledAssetGroupSchema** with **Remote** build/load path, **PackTogether**.
- Adds **ContentUpdateGroupSchema** with **StaticContent = false**.
- **MoveEntries(items, contentGroup)**: moves the selected entries from their current groups into this new group.

**Output of first half:** The **current group layout** (all groups, with entries as they are now — possibly including a new remote group and moved entries if the user applied). This layout is what the **second half** will use. No explicit "list of modified" is passed to the builder; the builder consumes the current layout plus `PreviousContentState`.

---

## Second Half — Build + Revert + Output

**Entry:** After pre-build, `BuildContentUpdate(settings, contentStateDataPath)` is called (or the data builder is invoked with `context.PreviousContentState` set).

### 1. BuildContentUpdate (thin wrapper)

- `LoadContentState(contentStateDataPath)` → `AddressablesContentState` (the .bin).
- `IsCacheDataValid(settings, cacheData)`: validate remote catalog path matches, warn on editor version mismatch, verify `BuildRemoteCatalog` is enabled.
- `context = new AddressablesDataBuilderInput(settings, cacheData.playerVersion)`; `context.IsContentUpdateBuild = true`; `context.PreviousContentState = cacheData`.
- Optional cleanup (e.g. clear build dir if no StreamingAssets).
- `SceneManagerState.Record()` — save the current editor scene state before the build.
- `settings.ActivePlayerDataBuilder.BuildData<AddressablesPlayerBuildResult>(context)`.
- `SceneManagerState.Restore()` — restore editor scene state after build completes.

So the **builder** receives the same **current group layout** (from settings) and **PreviousContentState** (the .bin). It does **not** receive a list of modified/new; it never calls `GatherModifiedEntries` or `CreateContentUpdateGroup`.

### 2. Full build with current layout (BuildScriptPackedMode)

- **ProcessAllGroups(aaContext):** iterates over **every** group. No filter for `IsContentUpdateBuild`.
- For each group: **ProcessBundledAssetSchema** → **PrepGroupBundlePacking(assetGroup, bundleInputDefs, schema)** with **entryFilter = null** → all entries in the group go into `bundleInputDefs`; then **m_AllBundleInputDefs.AddRange(bundleInputDefs)**.
- **DoBuild:**  
  - `new BundleBuildContent(m_AllBundleInputDefs)` — full set of bundles (all Local + Remote groups).  
  - **ContentPipeline.BuildAssetBundles(buildParams, buildContent, ...)** — single full build. Dependencies are resolved **across the entire build**; no asset is "not in any known bundle", so no incorrect pull into another bundle.

Bundles may be written to disk or skipped by **SBP Build Cache** (unchanged content → cache hit → no write). This is why "effective output is only changed bundles" should not be read as "the builder only received changed bundles as input".

### 3. Revert unchanged assets to previous bundle

When **builderInput.PreviousContentState != null**, after the build:

- **ContentUpdateContext** is built: **GuidToPreviousAssetStateMap** from `.bin` cachedInfos, **WriteData** from the build, **IdToCatalogDataEntryMap**, **BundleToInternalBundleIdMap**, **ContentState** = .bin, **Registry**, **PreviousAssetStateCarryOver** (list of `CachedAssetState` entries from previously reverted dependencies, used to detect if a bundle was already reverted in a prior revert pass).
- **RevertUnchangedAssetsToPreviousAssetState.Run(aaContext, contentUpdateContext):**
  - Iterates all groups with `BundledAssetGroupSchema`. For each non-folder entry that participated in the build (i.e. `WriteData.AssetToFiles` contains the entry's GUID):
    - First, **always** sets `entry.BundleFileId = catalogBundleEntry.InternalId` for the current build. This ensures new entries added post-initial-build have their `BundleFileId` set correctly for a future `SaveContentState` call.
    - **Skip** if entry is **not** in `GuidToPreviousAssetStateMap` (new entry added post-initial-build; no previous state to revert to).
    - **Skip** if `entry.parentGroup.Guid != previousAssetState.groupGuid` (entry has been moved to a different group, e.g. the new Content Update group created by Apply — do not revert it).
    - Check `hashChanged = AssetDatabase.GetAssetDependencyHash(entry.AssetPath) != previousAssetState.asset.hash`.
    - **Skip** if `hashChanged && !groupIsStaticContentGroup` (hash changed in a non-static group — keep the new build for this entry).
    - **Skip** if `!hashChanged && catalogBundleEntry.InternalId == previousAssetState.bundleFileId` (hash unchanged and bundle path already matches previous state — no-op, nothing to revert).
    - Otherwise: **add revert operation**.
    - **Revert:** if `Registry.ReplaceBundleEntry(...)` succeeds, then **File.Delete(operation.CurrentBuildPath)** (delete newly built bundle), and set `catalogBundleEntry.InternalId = entry.BundleFileId = previousAssetState.bundleFileId`; copy bundle metadata from `ContentState.cachedBundles` into `catalogBundleEntry.Data`.
  - **Built-in shader & monoscript bundles:** if the Default group has `ContentUpdateGroupSchema.StaticContent == true`, also revert the built-in shader bundle (`BuiltInBundleBaseName`) and monoscript bundle (`_monoscripts`) via `RevertBundleByNameContains()`. These special bundles cannot be individually diffed, so they are assumed unchanged when the default group is static.
  - **Key distinction:** entries in **static** groups may be reverted even when their hash has changed (because static groups are "Cannot Change Post Release" — the assets should stay with the original bundle). Entries in **non-static** groups are only reverted when their hash is unchanged.

Result: **Catalog** points unchanged assets to the **previous** bundle (e.g. Local or earlier remote); changed assets point to the newly built bundles. **On disk**, only bundles that still have at least one non-reverted asset (or that were written due to cache miss) remain.

### 4. Post-build catalog update and output pipeline (detail)

After `ContentPipeline.BuildAssetBundles` returns successfully, `DoBuild` executes these steps **in order**. Full Build and Update Build share the same code path; the only branching is inside `ProcessCatalogEntriesForBuild`.

#### 4a. PostProcessBundles (per group)

Iterates every group that produced bundles. For each bundle:

- Reads build result info (CRC, Hash, file size) from `IBundleBuildResults`.
- Populates `dataEntry.Data` = `AssetBundleRequestOptions` (CRC, Hash, BundleSize, BundleName, etc.) on the catalog location entry.
- Renames the bundle file: constructs an output name from the group schema + hash, updates `dataEntry.InternalId` to the final load path (local or remote URL), populates `m_BundleToInternalId[builtBundleName] = dataEntry.InternalId`.
- Moves the built bundle from SBP's temp build path to the group schema's `BuildPath` (local or remote build folder).
- Registers post-catalog-update callbacks for later execution.

After this step, `aaContext.locations` contains catalog entries with **final** `InternalId` and `Data` for every bundle and asset built. `m_BundleToInternalId` maps SBP internal bundle names to final load paths.

#### 4b. ProcessCatalogEntriesForBuild — the key divergence point

```
ProcessCatalogEntriesForBuild(aaContext, groups, builderInput, writeData,
    contentUpdateContext, bundleToInternalId, locationIdToCatalogEntryMap)
```

| | Full Build (`PreviousContentState == null`) | Update Build (`PreviousContentState != null`) |
|---|---|---|
| **Action** | `SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId` | `RevertUnchangedAssetsToPreviousAssetState.Run` |
| **Purpose** | For each entry, look up its bundle via `WriteData.AssetToFiles → FileToBundle → BundleToInternalId → locationIdToCatalogEntry`, set `entry.BundleFileId = catalogEntry.InternalId`. This ensures every entry has its final bundle ID stored so `SaveContentState` can persist it into `.bin`. | Revert unchanged entries' catalog locations to previous bundle IDs (see Section 3 above). Changed/new entries keep their new bundle IDs. `aaContext.locations` is mutated in-place. |
| **Effect on `aaContext.locations`** | No mutation — catalog entries already have correct `InternalId` from PostProcessBundles. | **Mutated**: unchanged entries' `InternalId` reverted to `previousAssetState.bundleFileId`; `Data` replaced with previous `CachedBundleState.data`. |
| **Effect on disk** | None. | `File.Delete(currentBuildPath)` for reverted bundles — newly built files that are no longer referenced are deleted. |

After this step, `aaContext.locations` is the **final source of truth** for the catalog. In Update Build, it is a **mix** of previous bundle references (for unchanged assets) and new bundle references (for changed/new assets).

Post-catalog-update callbacks are then invoked (e.g. registering files).

#### 4c. Generate catalog

`contentCatalog = new ContentCatalogData(...)` is created from `aaContext.locations` (sorted by InternalId):

- **Binary catalog** (default): `contentCatalog.SetData(aaContext.locations.OrderBy(f => f.InternalId).ToList())` → `SerializeToByteArray()` → compute hash.
- **JSON catalog** (if `ENABLE_JSON_CATALOG`): `contentCatalog.SetData(...)` → `JsonUtility.ToJson(contentCatalog)` → compute hash.

Both Full Build and Update Build generate the catalog from the same `aaContext.locations` — the difference is that Update Build's locations have been mutated by the Revert step.

#### 4d. CreateCatalogFiles (local + optional remote)

- **Local catalog**: written to `{Addressables.BuildPath}/catalog.bin` (or `.json`). Also writes a `.hash` file.
- **Remote catalog** (if `BuildRemoteCatalog` enabled): `CreateRemoteCatalog(data, ...)`:
  - Writes versioned file: `{RemoteCatalogBuildPath}/catalog_{playerVersion}.bin` + `.hash`.
  - Adds three dependency hash locations to `runtimeData.CatalogLocations`: Remote hash URL, Cache hash path (`{persistentDataPath}/com.unity.addressables/catalog_{version}.hash`), Local hash path (`{RuntimePath}/catalog.hash`).
  - These locations enable the runtime `ContentCatalogProvider` to check remote → cache → local for catalog freshness.

Both Full Build and Update Build execute the same catalog file generation. In Update Build, the catalog content reflects the post-revert mixed state.

#### 4e. Generate RuntimeSettings

`aaContext.runtimeData` (containing `CatalogLocations`, `InitializationObjects`, etc.) is serialized to `{Addressables.BuildPath}/settings.json`. Identical for both build types.

#### 4f. SaveContentState (.bin) — Full Build only

```csharp
if (extractData.BuildCache != null && builderInput.PreviousContentState == null)
{
    // Full Build only
    ContentUpdateScript.SaveContentState(aaContext.locations, ..., allEntries, dependencyData, playerBuildVersion, ...);
}
```

- Only when `PreviousContentState == null` (Full Build).
- Collects entries from **static groups only** (filtered by `ContentUpdateScript.GroupFilterFunc`).
- For each entry: calls `GetCachedAssetStateForData(guid, entry.BundleFileId, entry.parentGroup.Guid, data, dependencies)` → produces `CachedAssetState` with asset hash, dependency hashes, groupGuid, and bundleFileId.
- For each bundle catalog location: produces `CachedBundleState` with `bundleFileId` (= `InternalId`) and `data` (= `AssetBundleRequestOptions`).
- Appends `carryOverCacheState` (for previously reverted dependencies in chained updates — empty on first Full Build).
- Serializes `AddressablesContentState` via `BinaryFormatter` to `addressables_content_state.bin`.
- Copies from temp path to the configured content state build path.

**Update Build skips this entirely.** The `.bin` from the original Full Build is preserved as-is.

#### 4g. Output summary

| Output | Full Build | Update Build |
|--------|-----------|-------------|
| **Local catalog** | `{BuildPath}/catalog.bin` + `.hash` | Same (but contains mixed bundle references) |
| **Remote catalog** | `{RemoteBuildPath}/catalog_{version}.bin` + `.hash` (if enabled) | Same (mixed bundle references) |
| **Bundles on disk** | All bundles in group `BuildPath` | Only bundles not deleted by Revert |
| **`settings.json`** | `{BuildPath}/settings.json` | Same |
| **`.bin` state file** | **Written** → `addressables_content_state.bin` | **NOT written** — original preserved |
| **`addrResult.IsUpdateContentBuild`** | `false` | `true` |

### 5. Output

- **Registry** and on-disk files: only the bundles that were not fully reverted (and that SBP wrote) remain.
- So the effective **output** of an Update Build is: new/updated catalog + only **changed** bundles.
- Important wording: this describes the **final result after full build + cache + revert**, not a special "build changed bundles only" mode.

---

## Summary Diagram

```
First half (pre-build)
├── Resolve .bin path
├── If CheckForContentUpdateRestrictions != Disabled:
│   ├── GatherModifiedEntriesWithDependencies (static groups only)
│   │   ├── GatherExplicitModifiedEntries (asset + dep hash comparison)
│   │   ├── AddAllDependentScenesFromModifiedEntries
│   │   ├── GetStaticContentDependenciesForEntries
│   │   └── GetEntriesDependentOnModifiedEntries (static groups only)
│   ├── If modified in static groups:
│   │   ├── FailBuild → abort
│   │   └── ListUpdatedAssetsWithRestrictions → show ContentUpdatePreviewWindow
│   │       ├── User "Apply and Continue" → CreateContentUpdateGroup + MoveEntries → invoke build callback
│   │       ├── User "Continue without changes" → invoke build callback (no group mutation)
│   │       └── User "Cancel build" → close window, no build
│   └── Else → proceed to build with current layout
└── Set context.PreviousContentState = cacheData

Second half (build)
├── BuildData(context): ProcessAllGroups (no filter) → m_AllBundleInputDefs → ContentPipeline.BuildAssetBundles(full set)
├── PostProcessBundles: rename bundles, generate catalog entries
├── RevertUnchangedAssetsToPreviousAssetState:
│   ├── Per group: DetermineRequiredAssetEntryUpdates (hash + group + static checks)
│   ├── ApplyAssetEntryUpdates (Registry.ReplaceBundleEntry + File.Delete + InternalId revert)
│   └── Revert built-in shader + monoscript bundles (if Default group is static)
├── Write catalog (from reverted locations); do not SaveContentState (PreviousContentState != null)
└── Output: catalog + only changed bundles (SBP cache + revert)
```

---

## Full Build vs Update Build — Step-by-Step Comparison

Both build types share the same `BuildScriptPackedMode.DoBuild<TResult>()` code path. This table shows **every step** of `DoBuild` and whether each build type differs.

| Step | Method | Full Build | Update Build | Shared? |
|------|--------|-----------|-------------|---------|
| 1 | `ProcessAllGroups` | Iterates all groups, populates `m_AllBundleInputDefs` | **Identical** — no filter for `IsContentUpdateBuild`. The current group layout includes any groups created by the pre-build Apply step. | ✅ Same |
| 2 | `ContentPipeline.BuildAssetBundles` | `BundleBuildContent(m_AllBundleInputDefs)` — full set | **Identical** — SBP sees the full bundle graph | ✅ Same |
| 3 | `PostProcessBundles` (per group) | Rename bundles, populate `AssetBundleRequestOptions` in catalog entries, move bundles to group `BuildPath`, populate `m_BundleToInternalId` | **Identical** | ✅ Same |
| 4 | `ProcessCatalogEntriesForBuild` | **`SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId`**: set `entry.BundleFileId` from catalog entry for `.bin` persistence. No mutation of `aaContext.locations`. | **`RevertUnchangedAssetsToPreviousAssetState.Run`**: revert unchanged entries' `InternalId` + `Data` in `aaContext.locations` to previous bundle; delete reverted bundle files. | ❌ **Different** |
| 5 | Post-catalog callbacks | Execute registered callbacks | **Identical** | ✅ Same |
| 6 | Generate `ContentCatalogData` | `SetData(aaContext.locations)` → serialize → compute hash | **Identical code** — but `aaContext.locations` is **mixed** (some entries point to old bundles) | ⚠️ Same code, different data |
| 7 | `CreateCatalogFiles` | Write local + remote catalog files | **Identical** | ✅ Same |
| 8 | Generate `settings.json` | Write RuntimeData | **Identical** | ✅ Same |
| 9 | `SaveContentState` | **Executed**: serialize entries + bundles to `.bin` via `BinaryFormatter` | **Skipped**: `PreviousContentState != null` → condition fails | ❌ **Different** |
| 10 | Build Layout (optional) | Generate build layout report | **Identical** | ✅ Same |

**Key insight**: the only two places where Full Build and Update Build **diverge** are steps 4 and 9. Everything else — including the SBP build, bundle post-processing, catalog serialization, and file output — is shared code operating on the same (but differently mutated) in-memory state.

### What this document is explicitly saying "no" to

- **No**: "Update Build passes only modified assets to the builder."
- **No**: "GatherModifiedEntriesWithDependencies is the direct input of `ContentPipeline.BuildAssetBundles`."
- **No**: "Update Build regenerates `addressables_content_state.bin`."
- **No**: "Only changed bundles participate in dependency resolution during build."

---

## Code References (Addressables package)

| Step | File / location |
|------|------------------|
| Pre-build, restrictions | `Editor/GUI/AddressableAssetsSettingsGroupEditorBuildMenu.cs` — `AddressablesBuildMenuUpdateAPreviousBuild.OnPrebuild` → `OnUpdateBuild`, switch on `CheckForContentUpdateRestrictionsOption` |
| GatherModifiedEntriesWithDependencies | `Editor/Build/ContentUpdateScript.cs` — `GatherModifiedEntriesWithDependencies`, `GatherExplicitModifiedEntries` (+ `AddAllDependentScenesFromModifiedEntries`), `GetStaticContentDependenciesForEntries`, `GetEntriesDependentOnModifiedEntries` |
| ContentUpdatePreviewWindow | `Editor/GUI/ContentUpdatePreviewWindow.cs` — `ShowUpdatePreviewWindow`, Apply button calls `CreateContentUpdateGroup` then invokes callback |
| CreateContentUpdateGroup | `Editor/Build/ContentUpdateScript.cs` — `CreateContentUpdateGroup`; called from `Editor/GUI/ContentUpdatePreviewWindow.cs` on Apply |
| BuildContentUpdate | `Editor/Build/ContentUpdateScript.cs` — `BuildContentUpdate` (programmatic API path; menu path sets `input.PreviousContentState` and calls `BuildPlayerContent` via callback) |
| Full build | `Editor/Build/DataBuilders/BuildScriptPackedMode.cs` — `BuildDataImplementation` → `ProcessAllGroups` → `DoBuild` → `BundleBuildContent(m_AllBundleInputDefs)`, `ContentPipeline.BuildAssetBundles` |
| PostProcessBundles | `Editor/Build/DataBuilders/BuildScriptPackedMode.cs` — `PostProcessBundles`: per group, read `IBundleBuildResults`, populate `AssetBundleRequestOptions`, rename + move bundles, build `m_BundleToInternalId` |
| ProcessCatalogEntriesForBuild | `Editor/Build/DataBuilders/BuildScriptPackedMode.cs` — Full Build: `SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId`; Update Build: `RevertUnchangedAssetsToPreviousAssetState.Run` |
| Revert | `Editor/Build/RevertUnchangedAssetsToPreviousAssetState.cs` — `Run` (per-group `DetermineRequiredAssetEntryUpdates` + `ApplyAssetEntryUpdates` + `RevertBundleByNameContains` for builtins); invoked from `ProcessCatalogEntriesForBuild` when `PreviousContentState != null` |
| Generate catalog | `Editor/Build/DataBuilders/BuildScriptPackedMode.cs` — `ContentCatalogData.SetData(aaContext.locations)` → `SerializeToByteArray()` or `JsonUtility.ToJson()` |
| CreateCatalogFiles | `Editor/Build/DataBuilders/BuildScriptPackedMode.cs` — `CreateCatalogFiles`: write local catalog + hash; `CreateRemoteCatalog`: write versioned remote catalog + hash + add dependency hash locations |
| SaveContentState (.bin) | `Editor/Build/DataBuilders/BuildScriptPackedMode.cs` — condition `extractData.BuildCache != null && builderInput.PreviousContentState == null`; `Editor/Build/ContentUpdateScript.cs` — `SaveContentState`, `GetCachedAssetStates`, `GetCachedAssetStateForData` |
