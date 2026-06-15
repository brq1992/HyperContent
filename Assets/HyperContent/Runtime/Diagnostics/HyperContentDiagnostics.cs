using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Runtime introspection for HyperContent. Lets you answer the two leak questions:
    ///
    ///   (1) "Did the business code forget to Release?" — look at <see cref="DiagnosticsSnapshot.Operations"/>
    ///       for unexpected RefCount &gt; 0; under <c>HYPERCONTENT_TRACK_HANDLES</c> each handle in
    ///       <see cref="DiagnosticsSnapshot.Handles"/> carries the acquisition stack trace that pins
    ///       the op alive — that is the missing Release call site.
    ///
    ///   (2) "Did HyperContent forget to Unload?" — look at <see cref="BundleSummary.IsReachableFromCache"/>:
    ///       a loaded bundle that has NO matching cached operation is an orphan, i.e. the cache released
    ///       its op but the bundle was not unloaded. That is a HyperContent-side bug, not a caller leak.
    ///
    /// Layer 1 (snapshot + orphan detection) is always compiled — calling it has no cost when not invoked,
    /// and on call it just walks live dictionaries (no GC alloc beyond the returned snapshot lists).
    /// Layer 2 (per-handle stack traces) is gated by <c>HYPERCONTENT_TRACK_HANDLES</c>; when undefined,
    /// <see cref="DiagnosticsSnapshot.Handles"/> is empty and the per-handle <c>StackTrace</c> field is null.
    /// Editor and device builds are both supported — define the symbol in PlayerSettings → Scripting
    /// Define Symbols (or per-platform) when you need leak source tracking.
    ///
    /// Usage:
    ///   HyperContentDiagnostics.LogReport();          // dump full report to Debug.Log
    ///   string text = HyperContentDiagnostics.GetReport();
    ///   var snap = HyperContentDiagnostics.GetSnapshot();
    ///   if (snap.OrphanedBundleCount &gt; 0) { /* HC-side leak */ }
    /// </summary>
    public static class HyperContentDiagnostics
    {
        /// <summary>
        /// Build a structured snapshot suitable for programmatic checks. Reuses the supplied
        /// <see cref="DiagnosticsSnapshot"/> when non-null to avoid allocations on hot paths
        /// (e.g. periodic asserts in tests). Pass <c>null</c> for a fresh instance.
        /// </summary>
        public static DiagnosticsSnapshot GetSnapshot(DiagnosticsSnapshot pReuse = null)
        {
            var snap = pReuse ?? new DiagnosticsSnapshot();
            snap.Reset();
            snap.FrameCount = Time.frameCount;
            snap.RealtimeSinceStartup = Time.realtimeSinceStartup;

            var impl = HyperContentImpl.Current;
            if (impl == null || !impl.IsInitialized)
            {
                snap.IsInitialized = false;
                return snap;
            }
            snap.IsInitialized = true;
            snap.Mode = DetectMode();

            CollectOperations(impl, snap);
            CollectInstances(impl, snap);
            CollectBundles(impl, snap);
            CollectHandles(impl, snap);

            return snap;
        }

        /// <summary>
        /// Resolve an address to the dependency bundle set that loading it would pull in, under the active
        /// catalog's current <see cref="DependencyLoadMode"/>. Backs the inspectors' "deps by address" view.
        /// Returns <c>false</c> when HyperContent is not initialized, the active catalog is not a
        /// <see cref="LocalContentCatalog"/> (e.g. AssetDatabase play mode), or the address is unresolved;
        /// <paramref name="pResult"/>'s <c>Note</c> explains why.
        /// </summary>
        public static bool TryQueryDependencyBundles(string pAddress, out DependencyBundleQuery pResult)
        {
            pResult = new DependencyBundleQuery { Address = pAddress };

            var impl = HyperContentImpl.Current;
            if (impl == null || !impl.IsInitialized)
            {
                pResult.Note = "HyperContent not initialized";
                return false;
            }

            if (!(impl.Catalog is LocalContentCatalog local))
            {
                pResult.Note = "active catalog is not LocalContentCatalog " +
                               "(AssetDatabase play mode loads assets directly — no bundles)";
                return false;
            }

            pResult.Mode = local.LoadMode;
            bool resolved = local.TryGetDependencyBundleNamesForDiagnostics(pAddress, out var names, out var note);
            pResult.BundleNames = names;
            pResult.Note = note;
            pResult.Resolved = resolved;
            return resolved;
        }

        private static DiagnosticsMode DetectMode()
        {
#if UNITY_EDITOR
            return shared.PlayModeSettings.IsAssetDatabaseMode()
                ? DiagnosticsMode.AssetDatabase
                : DiagnosticsMode.Bundle;
#else
            return DiagnosticsMode.Bundle;
#endif
        }

        /// <summary>Pretty-print the current snapshot. Cheap enough to call on demand.</summary>
        public static string GetReport()
        {
            var snap = GetSnapshot();
            return FormatReport(snap);
        }

        /// <summary>
        /// Log the report via <see cref="Debug.Log"/>. Use this from a debug menu / hotkey at any
        /// point where you suspect a leak (e.g. right after a scene transition completes).
        /// </summary>
        public static void LogReport()
        {
            string text = GetReport();
            Debug.Log(text);
        }

        /// <summary>
        /// Format a previously captured snapshot. Useful when comparing two snapshots
        /// (before/after scene transition) — capture both, format the diff yourself,
        /// or just print both with this method.
        /// </summary>
        public static string FormatReport(DiagnosticsSnapshot pSnapshot)
        {
            if (pSnapshot == null) return "[HC.Diagnostics] (null snapshot)";
            if (!pSnapshot.IsInitialized) return "[HC.Diagnostics] HyperContent not initialized.";

            var sb = new StringBuilder(2048);
            sb.Append("=== HyperContent Diagnostics @ frame ").Append(pSnapshot.FrameCount)
              .Append(" t=").AppendFormat("{0:F2}s", pSnapshot.RealtimeSinceStartup)
              .Append(" mode=").Append(pSnapshot.Mode)
              .Append(" ===\n");

            sb.Append("Active operations: ").Append(pSnapshot.Operations.Count).Append('\n');
            for (int i = 0; i < pSnapshot.Operations.Count; i++)
            {
                AppendOperationRow(sb, pSnapshot, i, "  ");
            }

            sb.Append("Active handles: ").Append(pSnapshot.ActiveHandleCount).Append('\n');

            sb.Append("Tracked instances: ").Append(pSnapshot.Instances.Count).Append('\n');
            for (int i = 0; i < pSnapshot.Instances.Count; i++)
            {
                var inst = pSnapshot.Instances[i];
                sb.Append("  id=").Append(inst.InstanceId).Append(' ')
                  .Append("name=\"").Append(inst.InstanceName ?? "?").Append("\" ")
                  .Append("opAddress=\"").Append(inst.OpAddress ?? "?").Append("\" ")
                  .Append("opHash=").Append(inst.OpLocationHash).Append('\n');
            }

            if (pSnapshot.Mode == DiagnosticsMode.AssetDatabase)
            {
                sb.Append("Loaded bundles: n/a (AssetDatabase mode — no bundles in play; use Operations/Handles/Instances above)\n");
            }
            else
            {
                sb.Append("Loaded bundles: ").Append(pSnapshot.Bundles.Count)
                  .Append(" (orphaned: ").Append(pSnapshot.OrphanedBundleCount).Append(")\n");
                for (int i = 0; i < pSnapshot.Bundles.Count; i++)
                {
                    var b = pSnapshot.Bundles[i];
                    sb.Append("  ").Append(b.BundleName).Append("    ");
                    if (b.IsReachableFromCache)
                        sb.Append("[reachable refCount=").Append(b.RefCount).Append(']');
                    else
                        sb.Append("[ORPHAN — HC-side leak]");
                    sb.Append('\n');
                }
            }

            sb.Append("Active handles (with op + acquisition info): ").Append(pSnapshot.Handles.Count).Append('\n');
            bool anyStack = false;
            for (int i = 0; i < pSnapshot.Handles.Count; i++)
            {
                var h = pSnapshot.Handles[i];
                sb.Append("  handleId=").Append(h.HandleId)
                  .Append(" address=\"").Append(h.Address ?? "?").Append("\" ")
                  .Append("opHash=").Append(h.LocationHash);
                if (h.Frame > 0)
                    sb.Append(" frame=").Append(h.Frame).Append(" t=").AppendFormat("{0:F2}s", h.TimeAtAcquire);
                sb.Append('\n');
                if (!string.IsNullOrEmpty(h.AcquisitionStack))
                {
                    anyStack = true;
                    sb.Append("    stack:\n");
                    AppendIndented(sb, h.AcquisitionStack, "      ");
                }
            }
            if (!anyStack && pSnapshot.Handles.Count > 0)
            {
                sb.Append("  (define HYPERCONTENT_TRACK_HANDLES to capture per-handle stack traces)\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Render an op row plus an inline summary of who currently holds it
        /// (dependent ops + handles + instances). For richer tree-walk rendering use the
        /// reverse-edge dictionaries on <see cref="DiagnosticsSnapshot"/> directly.
        /// </summary>
        private static void AppendOperationRow(StringBuilder pSb, DiagnosticsSnapshot pSnap, int pOpIndex, string pIndent)
        {
            var op = pSnap.Operations[pOpIndex];
            pSb.Append(pIndent)
              .Append("[hash=").Append(op.LocationHash).Append("] ")
              .Append("address=\"").Append(op.Address ?? "?").Append("\" ")
              .Append("provider=").Append(op.ProviderId ?? "?").Append(' ')
              .Append("internalId=").Append(op.InternalId ?? "?").Append(' ')
              .Append("status=").Append(op.Status).Append(' ')
              .Append("refCount=").Append(op.RefCount).Append(' ')
              .Append("deps=").Append(op.DependencyCount);

            int dependentCount = pSnap.DependentEdges.TryGetValue(op.LocationHash, out var deps) ? deps.Count : 0;
            int handleCount = pSnap.HandlesByOp.TryGetValue(op.LocationHash, out var handles) ? handles.Count : 0;
            int instanceCount = pSnap.InstancesByOp.TryGetValue(op.LocationHash, out var insts) ? insts.Count : 0;
            if (dependentCount + handleCount + instanceCount > 0)
            {
                pSb.Append("  refsBy(deps=").Append(dependentCount)
                   .Append(", handles=").Append(handleCount)
                   .Append(", instances=").Append(instanceCount).Append(')');
            }
            pSb.Append('\n');
        }

        /// <summary>
        /// Format a Memory-Profiler-style "Referenced By" tree rooted at the given op,
        /// recursing through the reverse <see cref="DiagnosticsSnapshot.DependentEdges"/> graph
        /// up to <paramref name="pMaxDepth"/> levels and listing each leaf op's holding handles
        /// (with stack traces when available) and instance names.
        /// </summary>
        public static string FormatReferencedByTree(
            DiagnosticsSnapshot pSnapshot,
            int pRootOpLocationHash,
            int pMaxDepth = 8)
        {
            var sb = new StringBuilder(1024);
            if (pSnapshot == null || !pSnapshot.IsInitialized)
            {
                sb.Append("[HC.Diagnostics] not initialized");
                return sb.ToString();
            }
            if (!pSnapshot.OperationByHash.TryGetValue(pRootOpLocationHash, out int rootIdx))
            {
                sb.Append("[HC.Diagnostics] op hash ").Append(pRootOpLocationHash).Append(" not in cache");
                return sb.ToString();
            }
            AppendReferencedBySubtree(sb, pSnapshot, rootIdx, depth: 0, pMaxDepth);
            return sb.ToString();
        }

        private static void AppendReferencedBySubtree(
            StringBuilder pSb, DiagnosticsSnapshot pSnap, int pOpIndex, int depth, int pMaxDepth)
        {
            string indent = depth == 0 ? "" : new string(' ', depth * 2) + "└─ ";
            var op = pSnap.Operations[pOpIndex];
            pSb.Append(indent)
              .Append("[hash=").Append(op.LocationHash).Append("] ")
              .Append("address=\"").Append(op.Address ?? "?").Append("\" ")
              .Append("provider=").Append(op.ProviderId ?? "?")
              .Append(" refCount=").Append(op.RefCount).Append('\n');

            string deeper = new string(' ', (depth + 1) * 2);

            if (pSnap.HandlesByOp.TryGetValue(op.LocationHash, out var handleIdxs))
            {
                for (int i = 0; i < handleIdxs.Count; i++)
                {
                    var h = pSnap.Handles[handleIdxs[i]];
                    pSb.Append(deeper).Append("◇ handleId=").Append(h.HandleId);
                    if (h.Frame > 0)
                        pSb.Append(" frame=").Append(h.Frame).Append(" t=").AppendFormat("{0:F2}s", h.TimeAtAcquire);
                    pSb.Append('\n');
                    if (!string.IsNullOrEmpty(h.AcquisitionStack))
                        AppendIndented(pSb, h.AcquisitionStack, deeper + "    ");
                }
            }
            if (pSnap.InstancesByOp.TryGetValue(op.LocationHash, out var instIdxs))
            {
                for (int i = 0; i < instIdxs.Count; i++)
                {
                    var ins = pSnap.Instances[instIdxs[i]];
                    pSb.Append(deeper)
                       .Append("◆ instance id=").Append(ins.InstanceId)
                       .Append(" name=\"").Append(ins.InstanceName ?? "?").Append("\"\n");
                }
            }

            if (depth >= pMaxDepth) return;
            if (!pSnap.DependentEdges.TryGetValue(op.LocationHash, out var depIdxs)) return;
            for (int i = 0; i < depIdxs.Count; i++)
            {
                AppendReferencedBySubtree(pSb, pSnap, depIdxs[i], depth + 1, pMaxDepth);
            }
        }

        /// <summary>
        /// Append <paramref name="pText"/> to <paramref name="pSb"/> with each line indented by
        /// <paramref name="pIndent"/>. Avoids string.Split allocations.
        /// </summary>
        private static void AppendIndented(StringBuilder pSb, string pText, string pIndent)
        {
            int start = 0;
            for (int i = 0; i < pText.Length; i++)
            {
                if (pText[i] == '\n')
                {
                    pSb.Append(pIndent).Append(pText, start, i - start + 1);
                    start = i + 1;
                }
            }
            if (start < pText.Length)
            {
                pSb.Append(pIndent).Append(pText, start, pText.Length - start).Append('\n');
            }
        }

        // ── Collectors ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Two passes over the cache: first builds the flat list + hash index, second walks every
        /// op's Dependencies to populate the reverse-edge map (op -> ops that depend on it).
        /// Single-pass would have to deal with deps that haven't been emitted yet, two-pass keeps
        /// the inner loop trivial and lets <see cref="DiagnosticsSnapshot.OperationByHash"/> stay
        /// authoritative.
        /// </summary>
        private static void CollectOperations(HyperContentImpl pImpl, DiagnosticsSnapshot pSnap)
        {
            pImpl.OperationCache.Snapshot(pSnap.ScratchOps);
            for (int i = 0; i < pSnap.ScratchOps.Count; i++)
            {
                var op = pSnap.ScratchOps[i];
                pSnap.Operations.Add(new OperationSummary
                {
                    LocationHash = op.LocationHash,
                    Address = op.Location?.Address,
                    InternalId = op.Location?.InternalId,
                    ProviderId = op.Location?.ProviderId,
                    Status = op.Status,
                    RefCount = op.RefCount,
                    DependencyCount = op.DependencyCount,
                });
                pSnap.OperationByHash[op.LocationHash] = i;
            }

            for (int i = 0; i < pSnap.ScratchOps.Count; i++)
            {
                var op = pSnap.ScratchOps[i];
                for (int j = 0; j < op.DependencyCount; j++)
                {
                    var dep = op.Dependencies[j];
                    if (dep == null) continue;
                    var list = pSnap.GetOrAddList(pSnap.DependentEdges, dep.LocationHash);
                    list.Add(i);
                }
            }
            pSnap.ActiveHandleCount = pImpl.ActiveHandleCount;
        }

        private static void CollectInstances(HyperContentImpl pImpl, DiagnosticsSnapshot pSnap)
        {
            pImpl.InstanceRegistry.Snapshot(
                pSnap.ScratchInstanceIds, pSnap.ScratchInstanceNames, pSnap.ScratchInstanceOps);
            for (int i = 0; i < pSnap.ScratchInstanceIds.Count; i++)
            {
                var op = pSnap.ScratchInstanceOps[i];
                pSnap.Instances.Add(new InstanceSummary
                {
                    InstanceId = pSnap.ScratchInstanceIds[i],
                    InstanceName = pSnap.ScratchInstanceNames[i],
                    OpLocationHash = op?.LocationHash ?? 0,
                    OpAddress = op?.Location?.Address,
                });
                if (op != null)
                {
                    var list = pSnap.GetOrAddList(pSnap.InstancesByOp, op.LocationHash);
                    list.Add(i);
                }
            }
        }

        /// <summary>
        /// Walks the operation cache to compute the set of bundleNames that ARE referenced
        /// (i.e. some op has providerId == BundleFileProvider/RemoteBundleProvider AND its
        /// InternalId == bundleName). Then asks the bundleLoader for the full set of currently-
        /// loaded bundles and flags any that are not in the reachable set as ORPHANS — those
        /// are the smoking-gun "HC released its op but didn't actually unload" cases.
        /// </summary>
        private static void CollectBundles(HyperContentImpl pImpl, DiagnosticsSnapshot pSnap)
        {
            var bundleLoader = pImpl.BundleLoader;
            if (bundleLoader == null)
                return;

            // bundle-name -> RefCount (latest non-zero wins; same internalId can appear multiple
            // times if duplicates exist, which is itself a bug we want to surface in the dump).
            var reachable = pSnap.ScratchReachableBundleRefs;
            var reachableOpHash = pSnap.ScratchReachableBundleOpHash;

            for (int i = 0; i < pSnap.ScratchOps.Count; i++)
            {
                var op = pSnap.ScratchOps[i];
                var loc = op.Location;
                if (loc == null) continue;
                if (loc.ProviderId == BundleFileProvider.ID || loc.ProviderId == RemoteBundleProvider.ID)
                {
                    if (!string.IsNullOrEmpty(loc.InternalId))
                    {
                        reachable[loc.InternalId] = op.RefCount;
                        reachableOpHash[loc.InternalId] = op.LocationHash;
                    }
                }
            }

            bundleLoader.GetLoadedBundleNames(pSnap.ScratchLoadedBundleNames);
            int orphanCount = 0;
            for (int i = 0; i < pSnap.ScratchLoadedBundleNames.Count; i++)
            {
                string name = pSnap.ScratchLoadedBundleNames[i];
                bool reachableHit = reachable.TryGetValue(name, out int refCount);
                if (!reachableHit) orphanCount++;
                int opHash = 0;
                if (reachableHit)
                    reachableOpHash.TryGetValue(name, out opHash);
                pSnap.Bundles.Add(new BundleSummary
                {
                    BundleName = name,
                    IsReachableFromCache = reachableHit,
                    RefCount = reachableHit ? refCount : 0,
                    OpLocationHash = opHash,
                });
            }
            pSnap.OrphanedBundleCount = orphanCount;
        }

        private static void CollectHandles(HyperContentImpl pImpl, DiagnosticsSnapshot pSnap)
        {
            pImpl.GetActiveHandlesSnapshot(
                pSnap.ScratchHandleIds,
                pSnap.ScratchHandleOps,
                pSnap.ScratchHandleFrames,
                pSnap.ScratchHandleTimes,
                pSnap.ScratchHandleStacks);
            for (int i = 0; i < pSnap.ScratchHandleIds.Count; i++)
            {
                int handleId = pSnap.ScratchHandleIds[i];
                var op = pSnap.ScratchHandleOps[i];
                int locationHash = op?.LocationHash ?? 0;

                pSnap.Handles.Add(new HandleSummary
                {
                    HandleId = handleId,
                    LocationHash = locationHash,
                    Address = op?.Location?.Address,
                    Frame = pSnap.ScratchHandleFrames[i],
                    TimeAtAcquire = pSnap.ScratchHandleTimes[i],
                    AcquisitionStack = pSnap.ScratchHandleStacks[i],
                });
                pSnap.HandleByHandleId[handleId] = i;

                if (op != null)
                {
                    var list = pSnap.GetOrAddList(pSnap.HandlesByOp, locationHash);
                    list.Add(i);
                }
            }
        }
    }

    /// <summary>
    /// Aggregated point-in-time view of HyperContent's runtime state. All lists are
    /// owned by this snapshot and reused across <see cref="HyperContentDiagnostics.GetSnapshot"/>
    /// calls when the same instance is passed back in.
    ///
    /// In addition to the flat lists, three reverse-edge dictionaries (<see cref="DependentEdges"/>,
    /// <see cref="HandlesByOp"/>, <see cref="InstancesByOp"/>) let you answer "who is holding this op?"
    /// in O(1) without rescanning. <see cref="HandleByHandleId"/> and <see cref="OperationByHash"/>
    /// are convenience indexes into the flat lists for tree-walk renderers.
    /// </summary>
    public sealed class DiagnosticsSnapshot
    {
        public bool IsInitialized;
        public int FrameCount;
        public float RealtimeSinceStartup;
        public int ActiveHandleCount;
        public int OrphanedBundleCount;

        /// <summary>
        /// Active runtime mode. <see cref="DiagnosticsMode.AssetDatabase"/> means the editor is
        /// loading assets directly via <c>AssetDatabase.LoadAssetAtPath</c> with no bundles in play
        /// — Bundles list and orphan detection are not meaningful in that mode (still emitted, just
        /// always empty / 0). Use Operations / Handles / Instances to find leaks at the asset level.
        /// </summary>
        public DiagnosticsMode Mode;

        public readonly List<OperationSummary> Operations = new List<OperationSummary>();
        public readonly List<InstanceSummary> Instances = new List<InstanceSummary>();
        public readonly List<BundleSummary> Bundles = new List<BundleSummary>();
        public readonly List<HandleSummary> Handles = new List<HandleSummary>();

        /// <summary>op locationHash -> indexes into <see cref="Operations"/> for ops that depend on it.</summary>
        public readonly Dictionary<int, List<int>> DependentEdges = new Dictionary<int, List<int>>();

        /// <summary>op locationHash -> indexes into <see cref="Handles"/> of handles holding this op.</summary>
        public readonly Dictionary<int, List<int>> HandlesByOp = new Dictionary<int, List<int>>();

        /// <summary>op locationHash -> indexes into <see cref="Instances"/> of instances tracking this op.</summary>
        public readonly Dictionary<int, List<int>> InstancesByOp = new Dictionary<int, List<int>>();

        /// <summary>locationHash -> index into <see cref="Operations"/>.</summary>
        public readonly Dictionary<int, int> OperationByHash = new Dictionary<int, int>();

        /// <summary>handleId -> index into <see cref="Handles"/>.</summary>
        public readonly Dictionary<int, int> HandleByHandleId = new Dictionary<int, int>();

        // Scratch buffers re-used between collectors to keep one snapshot allocation-free
        // after the first call (callers can reuse a long-lived snapshot instance).
        internal readonly List<runtime.AsyncOperationBase> ScratchOps = new List<runtime.AsyncOperationBase>();
        internal readonly List<int> ScratchInstanceIds = new List<int>();
        internal readonly List<string> ScratchInstanceNames = new List<string>();
        internal readonly List<runtime.AsyncOperationBase> ScratchInstanceOps = new List<runtime.AsyncOperationBase>();
        internal readonly List<string> ScratchLoadedBundleNames = new List<string>();
        internal readonly Dictionary<string, int> ScratchReachableBundleRefs =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> ScratchReachableBundleOpHash =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly List<int> ScratchHandleIds = new List<int>();
        internal readonly List<runtime.AsyncOperationBase> ScratchHandleOps = new List<runtime.AsyncOperationBase>();
        internal readonly List<int> ScratchHandleFrames = new List<int>();
        internal readonly List<float> ScratchHandleTimes = new List<float>();
        internal readonly List<string> ScratchHandleStacks = new List<string>();

        // Pool of inner List<int>s reused across snapshots so reverse-edge dictionaries
        // do not re-allocate every time. Cleared+returned in Reset; rented in GetOrAddList.
        private readonly Stack<List<int>> _intListPool = new Stack<List<int>>();

        internal void Reset()
        {
            IsInitialized = false;
            FrameCount = 0;
            RealtimeSinceStartup = 0f;
            ActiveHandleCount = 0;
            OrphanedBundleCount = 0;
            Mode = DiagnosticsMode.Unknown;
            Operations.Clear();
            Instances.Clear();
            Bundles.Clear();
            Handles.Clear();
            ReturnAndClearLists(DependentEdges);
            ReturnAndClearLists(HandlesByOp);
            ReturnAndClearLists(InstancesByOp);
            OperationByHash.Clear();
            HandleByHandleId.Clear();
            ScratchOps.Clear();
            ScratchInstanceIds.Clear();
            ScratchInstanceNames.Clear();
            ScratchInstanceOps.Clear();
            ScratchLoadedBundleNames.Clear();
            ScratchReachableBundleRefs.Clear();
            ScratchReachableBundleOpHash.Clear();
            ScratchHandleIds.Clear();
            ScratchHandleOps.Clear();
            ScratchHandleFrames.Clear();
            ScratchHandleTimes.Clear();
            ScratchHandleStacks.Clear();
        }

        internal List<int> GetOrAddList(Dictionary<int, List<int>> pDict, int pKey)
        {
            if (pDict.TryGetValue(pKey, out var list)) return list;
            list = _intListPool.Count > 0 ? _intListPool.Pop() : new List<int>();
            list.Clear();
            pDict[pKey] = list;
            return list;
        }

        private void ReturnAndClearLists(Dictionary<int, List<int>> pDict)
        {
            foreach (var kv in pDict)
            {
                kv.Value.Clear();
                _intListPool.Push(kv.Value);
            }
            pDict.Clear();
        }
    }

    /// <summary>
    /// Result of <see cref="HyperContentDiagnostics.TryQueryDependencyBundles"/>: the dependency bundle set
    /// an address resolves to under the active <see cref="DependencyLoadMode"/>.
    /// </summary>
    public sealed class DependencyBundleQuery
    {
        /// <summary>Queried address (GUID or name).</summary>
        public string Address;
        /// <summary>Active catalog load mode.</summary>
        public DependencyLoadMode Mode;
        /// <summary>Resolved dependency bundle names (post-order, owning bundle LAST). Empty when unresolved.</summary>
        public List<string> BundleNames = new List<string>();
        /// <summary>Human-readable explanation of the result (mode semantics or why it failed).</summary>
        public string Note;
        /// <summary>True when the address resolved (even if its dep list is empty/missing); false otherwise.</summary>
        public bool Resolved;
    }

    /// <summary>
    /// Active loading mode at snapshot time. Affects which tabs/sections of the report are
    /// meaningful — see <see cref="DiagnosticsSnapshot.Mode"/>.
    /// </summary>
    public enum DiagnosticsMode
    {
        Unknown = 0,
        /// <summary>Editor play-mode is using <c>AssetDatabase.LoadAssetAtPath</c> directly; no bundles loaded.</summary>
        AssetDatabase = 1,
        /// <summary>Bundles are loaded normally (Editor "use existing bundles" or device build).</summary>
        Bundle = 2,
    }

    public struct OperationSummary
    {
        public int LocationHash;
        public string Address;
        public string InternalId;
        public string ProviderId;
        public OperationStatus Status;
        public int RefCount;
        public int DependencyCount;
    }

    public struct InstanceSummary
    {
        public int InstanceId;
        public string InstanceName;
        public int OpLocationHash;
        public string OpAddress;
    }

    public struct BundleSummary
    {
        public string BundleName;

        /// <summary>
        /// True when the operation cache still holds a bundle op with this <c>InternalId</c>.
        /// False = ORPHAN — bundle is loaded in <see cref="UnityBundleLoader"/> but no cached op
        /// references it. That's a HyperContent-side bug (the op was disposed but the AssetBundle
        /// was not unloaded). Investigate the corresponding provider's <c>Release</c> path.
        /// </summary>
        public bool IsReachableFromCache;

        /// <summary>RefCount of the matching cached op; 0 when orphaned.</summary>
        public int RefCount;

        /// <summary>
        /// LocationHash of the cached bundle op (when reachable). Lets diagnostics UI
        /// pivot from a bundle row directly to <see cref="DiagnosticsSnapshot.DependentEdges"/>
        /// to enumerate the asset ops loaded out of this bundle (i.e. the actively-loaded
        /// resources that keep the bundle alive). 0 when orphaned.
        /// </summary>
        public int OpLocationHash;
    }

    /// <summary>
    /// Per-handle acquisition record. <see cref="AcquisitionStack"/> is non-null only when the
    /// build was compiled with <c>HYPERCONTENT_TRACK_HANDLES</c>.
    /// </summary>
    public struct HandleSummary
    {
        public int HandleId;
        public int LocationHash;
        public string Address;
        public int Frame;
        public float TimeAtAcquire;
        public string AcquisitionStack;
    }
}
