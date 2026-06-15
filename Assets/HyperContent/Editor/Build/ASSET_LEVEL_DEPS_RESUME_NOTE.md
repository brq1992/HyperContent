# Asset-Level Dependency Fix — Resume Note (PAUSED)

Temporary note for picking this work back up later. Delete once the open item below is closed.

## What was done (keep)

Fixed an asset-level dependency under-pin: when an entry `R` references a sibling entry `Y`
in the SAME owning bundle and `Y` has cross-bundle deps (e.g. `Boundary -> Survival/BossBoundary/
PositionIndicator -> featurebattle_8/5/7`), SBP's per-asset `AssetToFiles` stops at the explicit
boundary `Y`, and `Y`'s bundle == owner (excluded from the transitive expansion), so those deps
were missed. Bundle-level mode was unaffected (it loads the owner's full closure).

The fix computes, per asset, an **in-bundle entry frontier** (the root plus every sibling entry it
transitively references WITHOUT leaving the owning bundle) from SBP's object-level reference graph,
then takes the one-hop base set over the whole frontier before the existing transitive expansion +
clamp. Cross-bundle hops beyond the owner are already covered by `BundleDependencies` (transitive),
so only the owner-local layer needs the frontier.

Reference graph is sourced from SBP (`IDependencyData.referencedObjects`), NOT
`AssetDatabase.GetDependencies` — consistent with what SBP actually packed. The only remaining
`GetDependencies` use is the SpriteAtlas edge (a genuine SBP blind spot).

### Files changed

- `BundlePackedAssetsFromSbp.cs` — capture `IDependencyData` alongside `IBundleWriteData`.
- `DefaultBuildExecutor.cs` — `BuildEntryReferenceGraph` + `ComputeInBundleEntryFrontier` helpers;
  `BuildAssetDependencyBundlesFromSbp` now builds the base set over the frontier. Method XML docs
  are the up-to-date source of truth for the algorithm.
- `UpdateBuildExecutor.cs` — pass `IDependencyData` into `BuildAssetDependencyBundlesFromSbp`.

Temporary `[DIAG]` logging used during the investigation has been removed.

## Validation status

- In-game behavior looks correct; no obvious functional regression observed.
- Build-time: `Boundary_1001_Ava` correctly pins `featurebattle_8/5/7`. The three sibling refs
  (`groundPrefabList` / `boundaryObject` / `positionIndicatorObject`) are direct `GameObject`
  hard refs that runtime spawns via `GameObject.Instantiate(field)` with no separate load, so their
  bundles must be resident when the referrer loads — the frontier is correct, not over-pin.

## Open item (FIRST thing to investigate on resume)

**Loaded AssetBundle count increased after this change.** Part of the increase is the frontier
correctly pinning previously-under-pinned hard-ref spawn deps, but it needs broader testing to
confirm no genuine over-pin slipped in. Investigation directions:

- Re-attribute each newly-loaded AB to its source asset (the removed `[DIAG]` block did exactly
  this — restore it temporarily, or write a small report tool, keyed on frontier members).
- Watch for the frontier over-expanding along "handle-only" hard refs inside large bundles (refs
  that are held but never actually instantiated).
- Diff the actual resident bundle set between `BundleLevel` and `AssetLevel` to isolate the delta.

## Optional follow-up

If lazy loading of those spawn targets is desired (which would also directly shrink the AB delta
above): convert `boundaryObject` / `positionIndicatorObject` / `groundPrefabList` to `AssetReference`
+ explicit `LoadAsync` before spawn. This is a gameplay-code design choice, not a build-system one;
`AssetReference` (string GUID) is invisible to SBP and drops out of the frontier automatically.

## Not committed

These changes are uncommitted and live alongside other in-progress HyperContent work in the tree.
Commit separately when resuming.
