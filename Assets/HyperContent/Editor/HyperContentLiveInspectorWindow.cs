using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using com.igg.hypercontent;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Editor-only live view of HyperContent's runtime state. Mirrors
    /// <see cref="HyperContentDiagnostics"/> output but auto-refreshes during play mode
    /// so you can watch RefCount go up / down as you scene-jump and pinpoint the
    /// exact moment a bundle stops being released.
    ///
    /// The right pane provides a Memory-Profiler-style "Referenced By" tree for the
    /// selected op / bundle: it walks the reverse-edge graph (op -> ops that depend on it)
    /// and at each leaf shows the holding handles (with acquisition stack when
    /// HYPERCONTENT_TRACK_HANDLES is defined) and tracked GameObject instances. That tree
    /// answers "who is currently pinning this bundle alive?" without leaving the editor.
    ///
    /// Workflow:
    ///  1. Open HyperContent / Live Inspector before entering play mode.
    ///  2. Click "Capture Baseline" before the action you want to investigate
    ///     (e.g. before "enter battle scene").
    ///  3. After the inverse action ("exit to lobby"), click "Refresh" — bundles
    ///     still loaded that should have been released show up as orphans or as
    ///     ops with non-zero RefCount.
    ///  4. Select the suspicious bundle / op in the left list; the right pane will
    ///     unfold the chain of dependent ops up to the handle / instance that's
    ///     keeping it alive. Toggle the stack to see the business call site.
    /// </summary>
    public sealed class HyperContentLiveInspectorWindow : EditorWindow
    {
        private DiagnosticsSnapshot _current;
        private DiagnosticsSnapshot _baseline;
        private bool _autoRefresh = true;
        private float _refreshInterval = 0.5f;
        private double _nextRefreshTime;

        private Vector2 _leftScroll;
        private Vector2 _rightScroll;
        private int _tab;
        private static readonly string[] TAB_LABELS = { "Operations", "Bundles", "Instances", "Handles", "Deps" };

        // "Deps" tab: resolve an address → asset-level dependency bundle set (mode-aware).
        private string _depQueryInput = "";
        private DependencyBundleQuery _depQueryResult;

        private string _filter = "";
        private bool _onlyOrphans;
        private bool _onlyDiff;

        // Selection in the left pane drives the "Referenced By" tree on the right.
        // We key by location hash for ops and by bundleName for bundles; bundles map
        // back to a cached BundleFileProvider/RemoteBundleProvider op via the snapshot.
        private int _selectedOpHash;

        // Per-handle stack toggle (handleId -> expanded). Sticky across refreshes so
        // an opened stack stays open as RefCount changes.
        private readonly HashSet<int> _expandedHandleStacks = new HashSet<int>();

        // Cycle guard for the recursive tree walk.
        private readonly HashSet<int> _treeWalkSeen = new HashSet<int>();

        private const int MAX_TREE_DEPTH = 12;

        [MenuItem("HyperContent/Live Inspector", false, 10)]
        public static void Open()
        {
            var w = GetWindow<HyperContentLiveInspectorWindow>("HC Live Inspector");
            w.minSize = new Vector2(820, 420);
            w.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (!_autoRefresh) return;
            if (!Application.isPlaying) return;
            if (EditorApplication.timeSinceStartup < _nextRefreshTime) return;
            _nextRefreshTime = EditorApplication.timeSinceStartup + _refreshInterval;
            RefreshSnapshot();
            Repaint();
        }

        private void RefreshSnapshot()
        {
            _current = HyperContentDiagnostics.GetSnapshot(_current);
        }

        private void OnGUI()
        {
            DrawToolbar();

            if (_current == null)
            {
                EditorGUILayout.HelpBox(
                    "No snapshot yet. Click Refresh while Play mode is running with HyperContent initialized.",
                    MessageType.Info);
                return;
            }

            if (!_current.IsInitialized)
            {
                EditorGUILayout.HelpBox(
                    "HyperContent is not initialized. Enter Play mode and call HyperContent.Initialize().",
                    MessageType.Warning);
                return;
            }

            DrawSummary();

            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawLeftPane();
            DrawRightPane();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(64)))
                RefreshSnapshot();

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto", EditorStyles.toolbarButton, GUILayout.Width(48));
            GUILayout.Label("Interval", GUILayout.Width(50));
            _refreshInterval = EditorGUILayout.Slider(_refreshInterval, 0.1f, 2f, GUILayout.Width(120));

            GUILayout.Space(8);
            if (GUILayout.Button("Capture Baseline", EditorStyles.toolbarButton, GUILayout.Width(120)))
                CaptureBaseline();
            if (GUILayout.Button("Clear Baseline", EditorStyles.toolbarButton, GUILayout.Width(100)))
                _baseline = null;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Log Report", EditorStyles.toolbarButton, GUILayout.Width(80)))
                HyperContentDiagnostics.LogReport();
            if (GUILayout.Button("Copy Report", EditorStyles.toolbarButton, GUILayout.Width(90)))
                EditorGUIUtility.systemCopyBuffer = HyperContentDiagnostics.FormatReport(_current);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Filter", GUILayout.Width(40));
            _filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
            _onlyOrphans = GUILayout.Toggle(_onlyOrphans, "Only Orphans", EditorStyles.toolbarButton, GUILayout.Width(96));
            _onlyDiff = GUILayout.Toggle(_onlyDiff, "Only Diff vs Baseline", EditorStyles.toolbarButton, GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();
        }

        private void CaptureBaseline()
        {
            if (_current == null) RefreshSnapshot();
            if (_current == null) return;
            _baseline = CloneSnapshotForBaseline(_current);
        }

        /// <summary>
        /// Make a shallow value copy of the lists we compare against — we cannot reuse the
        /// snapshot object as a baseline because GetSnapshot reuses it.
        /// </summary>
        private static DiagnosticsSnapshot CloneSnapshotForBaseline(DiagnosticsSnapshot pSrc)
        {
            var dst = new DiagnosticsSnapshot
            {
                IsInitialized = pSrc.IsInitialized,
                FrameCount = pSrc.FrameCount,
                RealtimeSinceStartup = pSrc.RealtimeSinceStartup,
                ActiveHandleCount = pSrc.ActiveHandleCount,
                OrphanedBundleCount = pSrc.OrphanedBundleCount,
            };
            dst.Operations.AddRange(pSrc.Operations);
            dst.Instances.AddRange(pSrc.Instances);
            dst.Bundles.AddRange(pSrc.Bundles);
            dst.Handles.AddRange(pSrc.Handles);
            return dst;
        }

        private void DrawSummary()
        {
            var sb = new StringBuilder(160);
            sb.Append("Mode=").Append(_current.Mode)
              .Append("  Frame ").Append(_current.FrameCount)
              .Append("  ops=").Append(_current.Operations.Count)
              .Append("  bundles=").Append(_current.Bundles.Count)
              .Append("  orphans=").Append(_current.OrphanedBundleCount)
              .Append("  instances=").Append(_current.Instances.Count)
              .Append("  activeHandles=").Append(_current.ActiveHandleCount);
            if (_baseline != null)
            {
                sb.Append("   |   baseline frame ").Append(_baseline.FrameCount)
                  .Append(" ops=").Append(_baseline.Operations.Count)
                  .Append(" bundles=").Append(_baseline.Bundles.Count);
            }
            EditorGUILayout.LabelField(sb.ToString(), EditorStyles.boldLabel);

            if (_current.Mode == DiagnosticsMode.AssetDatabase)
            {
                EditorGUILayout.HelpBox(
                    "AssetDatabase mode: assets load through AssetDatabase.LoadAssetAtPath, no bundles are involved. " +
                    "Bundles tab and orphan detection are not meaningful here. Use Operations / Instances / Handles " +
                    "to find unreleased loads — the Referenced By tree still works at the asset level.",
                    MessageType.Info);
            }
            else if (_current.OrphanedBundleCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{_current.OrphanedBundleCount} loaded bundle(s) have no matching cached operation. " +
                    "These are HyperContent-side leaks: a provider released its op without unloading the AssetBundle. " +
                    "See the Bundles tab for names.",
                    MessageType.Warning);
            }
        }

        // ── Left pane: list of ops/bundles/instances/handles ────────────────────────

        private void DrawLeftPane()
        {
            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            _tab = GUILayout.Toolbar(_tab, TAB_LABELS);
            _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
            switch (_tab)
            {
                case 0: DrawOperationsList(); break;
                case 1: DrawBundlesList(); break;
                case 2: DrawInstancesList(); break;
                case 3: DrawHandlesList(); break;
                case 4: DrawDepsView(); break;
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawOperationsList()
        {
            HashSet<int> baselineHashes = _baseline != null && _onlyDiff ? CollectBaselineOpHashes() : null;
            for (int i = 0; i < _current.Operations.Count; i++)
            {
                var op = _current.Operations[i];
                if (!MatchesFilter(op.Address, op.InternalId, op.ProviderId)) continue;
                if (baselineHashes != null && baselineHashes.Contains(op.LocationHash)) continue;
                DrawOperationRow(op);
            }
        }

        private void DrawOperationRow(OperationSummary pOp)
        {
            int dependentCount = _current.DependentEdges.TryGetValue(pOp.LocationHash, out var deps) ? deps.Count : 0;
            int handleCount = _current.HandlesByOp.TryGetValue(pOp.LocationHash, out var handles) ? handles.Count : 0;
            int instanceCount = _current.InstancesByOp.TryGetValue(pOp.LocationHash, out var insts) ? insts.Count : 0;

            EditorGUILayout.BeginHorizontal();
            bool isSelected = _selectedOpHash == pOp.LocationHash;
            var prevColor = GUI.color;
            if (isSelected) GUI.color = new Color(0.7f, 0.9f, 1f);
            if (GUILayout.Button(
                $"{(pOp.Address ?? "?")}    refCount={pOp.RefCount}    refsBy=({dependentCount}/{handleCount}/{instanceCount})",
                isSelected ? EditorStyles.toolbarButton : EditorStyles.label,
                GUILayout.ExpandWidth(true)))
            {
                _selectedOpHash = pOp.LocationHash;
            }
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(
                $"hash={pOp.LocationHash}  status={pOp.Status}  provider={pOp.ProviderId}  internalId={pOp.InternalId}",
                EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        private HashSet<int> CollectBaselineOpHashes()
        {
            var s = new HashSet<int>();
            for (int i = 0; i < _baseline.Operations.Count; i++)
                s.Add(_baseline.Operations[i].LocationHash);
            return s;
        }

        private void DrawBundlesList()
        {
            if (_current.Mode == DiagnosticsMode.AssetDatabase)
            {
                EditorGUILayout.HelpBox(
                    "No bundles to show — editor is in AssetDatabase mode. " +
                    "Switch the Operations tab to find unreleased asset loads, or " +
                    "switch to 'Use Existing Bundles' play mode to exercise the bundle path.",
                    MessageType.Info);
                return;
            }

            HashSet<string> baselineNames = _baseline != null && _onlyDiff ? CollectBaselineBundleNames() : null;
            for (int i = 0; i < _current.Bundles.Count; i++)
            {
                var b = _current.Bundles[i];
                if (_onlyOrphans && b.IsReachableFromCache) continue;
                if (!MatchesFilter(b.BundleName, null, null)) continue;
                if (baselineNames != null && baselineNames.Contains(b.BundleName)) continue;

                int opHash = FindBundleOpHash(b.BundleName);
                bool isSelected = opHash != 0 && _selectedOpHash == opHash;

                var prevColor = GUI.color;
                if (!b.IsReachableFromCache) GUI.color = new Color(1f, 0.6f, 0.6f);
                else if (isSelected) GUI.color = new Color(0.7f, 0.9f, 1f);
                EditorGUILayout.BeginHorizontal();
                string label = b.IsReachableFromCache
                    ? $"{b.BundleName}    [reachable refCount={b.RefCount}]"
                    : $"{b.BundleName}    [ORPHAN]";
                if (GUILayout.Button(label,
                    isSelected ? EditorStyles.toolbarButton : EditorStyles.label,
                    GUILayout.ExpandWidth(true)))
                {
                    _selectedOpHash = opHash;
                }
                EditorGUILayout.EndHorizontal();
                GUI.color = prevColor;
            }
        }

        /// <summary>
        /// Find the OperationCache entry whose providerId is BundleFileProvider/RemoteBundleProvider
        /// and whose internalId matches the given bundle name. Returns 0 when no matching cached
        /// op exists (i.e. the bundle is an orphan).
        /// </summary>
        private int FindBundleOpHash(string pBundleName)
        {
            for (int i = 0; i < _current.Operations.Count; i++)
            {
                var op = _current.Operations[i];
                if (op.InternalId == pBundleName &&
                    (op.ProviderId == "BundleFileProvider" || op.ProviderId == "RemoteBundleProvider"))
                {
                    return op.LocationHash;
                }
            }
            return 0;
        }

        private HashSet<string> CollectBaselineBundleNames()
        {
            var s = new HashSet<string>();
            for (int i = 0; i < _baseline.Bundles.Count; i++)
                s.Add(_baseline.Bundles[i].BundleName);
            return s;
        }

        private void DrawInstancesList()
        {
            HashSet<int> baselineIds = _baseline != null && _onlyDiff ? CollectBaselineInstanceIds() : null;
            for (int i = 0; i < _current.Instances.Count; i++)
            {
                var ins = _current.Instances[i];
                if (!MatchesFilter(ins.InstanceName, ins.OpAddress, null)) continue;
                if (baselineIds != null && baselineIds.Contains(ins.InstanceId)) continue;
                bool isSelected = _selectedOpHash == ins.OpLocationHash;
                var prevColor = GUI.color;
                if (isSelected) GUI.color = new Color(0.7f, 0.9f, 1f);
                if (GUILayout.Button(
                    $"id={ins.InstanceId}  name=\"{ins.InstanceName}\"  opAddress=\"{ins.OpAddress}\"",
                    isSelected ? EditorStyles.toolbarButton : EditorStyles.label,
                    GUILayout.ExpandWidth(true)))
                {
                    _selectedOpHash = ins.OpLocationHash;
                }
                GUI.color = prevColor;
            }
        }

        private HashSet<int> CollectBaselineInstanceIds()
        {
            var s = new HashSet<int>();
            for (int i = 0; i < _baseline.Instances.Count; i++)
                s.Add(_baseline.Instances[i].InstanceId);
            return s;
        }

        private void DrawHandlesList()
        {
            if (_current.Handles.Count == 0)
            {
                EditorGUILayout.HelpBox("No active handles.", MessageType.Info);
                return;
            }

            bool anyStack = false;
            for (int i = 0; i < _current.Handles.Count; i++)
                if (!string.IsNullOrEmpty(_current.Handles[i].AcquisitionStack)) { anyStack = true; break; }
            if (!anyStack)
            {
                EditorGUILayout.HelpBox(
                    "Handle list is populated but no stack traces are captured. " +
                    "Define HYPERCONTENT_TRACK_HANDLES in PlayerSettings → Scripting Define Symbols to record acquisition stacks.",
                    MessageType.Info);
            }

            HashSet<int> baselineHandleIds = _baseline != null && _onlyDiff ? CollectBaselineHandleIds() : null;
            for (int i = 0; i < _current.Handles.Count; i++)
            {
                var h = _current.Handles[i];
                if (!MatchesFilter(h.Address, null, null)) continue;
                if (baselineHandleIds != null && baselineHandleIds.Contains(h.HandleId)) continue;
                bool isSelected = _selectedOpHash == h.LocationHash;
                var prevColor = GUI.color;
                if (isSelected) GUI.color = new Color(0.7f, 0.9f, 1f);
                if (GUILayout.Button(
                    $"handleId={h.HandleId}    address=\"{h.Address}\"    " +
                    (h.Frame > 0 ? $"frame={h.Frame}" : "(no frame)"),
                    EditorStyles.label,
                    GUILayout.ExpandWidth(true)))
                {
                    _selectedOpHash = h.LocationHash;
                }
                GUI.color = prevColor;

                if (!string.IsNullOrEmpty(h.AcquisitionStack))
                {
                    bool expanded = _expandedHandleStacks.Contains(h.HandleId);
                    var rect = EditorGUILayout.GetControlRect();
                    if (EditorGUI.Foldout(rect, expanded, "stack"))
                    {
                        if (!expanded) _expandedHandleStacks.Add(h.HandleId);
                        EditorGUILayout.SelectableLabel(
                            h.AcquisitionStack,
                            EditorStyles.textArea,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight * 8f));
                    }
                    else if (expanded)
                    {
                        _expandedHandleStacks.Remove(h.HandleId);
                    }
                }
            }
        }

        private HashSet<int> CollectBaselineHandleIds()
        {
            var s = new HashSet<int>();
            for (int i = 0; i < _baseline.Handles.Count; i++)
                s.Add(_baseline.Handles[i].HandleId);
            return s;
        }

        // ── Deps tab: address → dependency bundle set ───────────────────────────────

        private void DrawDepsView()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Address", GUILayout.Width(56));
            _depQueryInput = EditorGUILayout.TextField(_depQueryInput);
            if (GUILayout.Button("Query", GUILayout.Width(70)))
                HyperContentDiagnostics.TryQueryDependencyBundles(_depQueryInput, out _depQueryResult);
            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                _depQueryInput = "";
                _depQueryResult = null;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Enter a GUID or addressable name and Query. Shows the bundle set this asset would load " +
                "under the active mode (owning bundle LAST).",
                EditorStyles.miniLabel);

            if (_depQueryResult == null)
                return;

            var r = _depQueryResult;
            DrawSeparator();
            EditorGUILayout.LabelField($"address: {r.Address}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"mode: {r.Mode}    resolved: {r.Resolved}", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(r.Note))
                EditorGUILayout.HelpBox(r.Note, r.Resolved ? MessageType.Info : MessageType.Warning);

            int count = r.BundleNames != null ? r.BundleNames.Count : 0;
            EditorGUILayout.LabelField($"dependency bundles ({count}):", EditorStyles.boldLabel);
            if (count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    bool isOwning = i == count - 1;
                    EditorGUILayout.LabelField(
                        isOwning ? $"  [{i}] {r.BundleNames[i]}   (owning)" : $"  [{i}] {r.BundleNames[i]}",
                        isOwning ? EditorStyles.boldLabel : EditorStyles.label);
                }
            }
        }

        // ── Right pane: Memory-Profiler-style "Referenced By" tree ──────────────────

        private void DrawRightPane()
        {
            EditorGUILayout.BeginVertical(
                GUI.skin.box,
                GUILayout.Width(420f),
                GUILayout.MaxWidth(520f),
                GUILayout.ExpandHeight(true));

            EditorGUILayout.LabelField("Referenced By", EditorStyles.boldLabel);
            if (_selectedOpHash == 0)
            {
                EditorGUILayout.HelpBox(
                    "Select an op / bundle / instance / handle in the left pane. " +
                    "The reverse-edge tree of who is currently holding it will appear here.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (!_current.OperationByHash.TryGetValue(_selectedOpHash, out int rootIdx))
            {
                EditorGUILayout.HelpBox(
                    $"Op hash {_selectedOpHash} not in current cache. " +
                    "It may have been released between snapshots; pick another row.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            DrawSelectedOpHeader(rootIdx);

            _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll);
            _treeWalkSeen.Clear();
            DrawReferencedBySubtree(rootIdx, depth: 0);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawSelectedOpHeader(int pOpIndex)
        {
            var op = _current.Operations[pOpIndex];
            int dependentCount = _current.DependentEdges.TryGetValue(op.LocationHash, out var deps) ? deps.Count : 0;
            int handleCount = _current.HandlesByOp.TryGetValue(op.LocationHash, out var handles) ? handles.Count : 0;
            int instanceCount = _current.InstancesByOp.TryGetValue(op.LocationHash, out var insts) ? insts.Count : 0;
            EditorGUILayout.LabelField(
                $"address: {op.Address}",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"provider: {op.ProviderId}    internalId: {op.InternalId}    refCount: {op.RefCount}    deps: {op.DependencyCount}",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"refsBy → dependent ops: {dependentCount},  handles: {handleCount},  instances: {instanceCount}",
                EditorStyles.miniLabel);
            DrawSeparator();
        }

        /// <summary>
        /// Walk the reverse <see cref="DiagnosticsSnapshot.DependentEdges"/> graph starting at
        /// <paramref name="pOpIndex"/>, draw at each node the holding handles + instances, and
        /// recurse to the parent op until we reach the root (no dependents). Cycle-safe via
        /// <see cref="_treeWalkSeen"/>.
        /// </summary>
        private void DrawReferencedBySubtree(int pOpIndex, int depth)
        {
            if (depth > MAX_TREE_DEPTH) return;

            var op = _current.Operations[pOpIndex];
            if (!_treeWalkSeen.Add(op.LocationHash))
            {
                EditorGUILayout.LabelField($"… (cycle on hash={op.LocationHash})", EditorStyles.miniLabel);
                return;
            }

            using (new EditorGUI.IndentLevelScope(depth == 0 ? 0 : 1))
            {
                if (depth > 0)
                {
                    EditorGUILayout.LabelField(
                        $"↑ depended-on by  [{op.ProviderId}]  \"{op.Address ?? "?"}\"  refCount={op.RefCount}",
                        EditorStyles.boldLabel);
                }

                if (_current.HandlesByOp.TryGetValue(op.LocationHash, out var handleIdxs))
                {
                    for (int i = 0; i < handleIdxs.Count; i++)
                        DrawHandleLeaf(handleIdxs[i]);
                }
                if (_current.InstancesByOp.TryGetValue(op.LocationHash, out var instIdxs))
                {
                    for (int i = 0; i < instIdxs.Count; i++)
                    {
                        var ins = _current.Instances[instIdxs[i]];
                        EditorGUILayout.LabelField(
                            $"◆ instance id={ins.InstanceId}  name=\"{ins.InstanceName}\"");
                    }
                }

                if (_current.DependentEdges.TryGetValue(op.LocationHash, out var depIdxs))
                {
                    for (int i = 0; i < depIdxs.Count; i++)
                        DrawReferencedBySubtree(depIdxs[i], depth + 1);
                }
                else if (depth == 0)
                {
                    int handleCount = handleIdxs?.Count ?? 0;
                    int instanceCount = instIdxs?.Count ?? 0;
                    if (handleCount + instanceCount == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "No reverse edges, no handles, no instances — yet refCount > 0. " +
                            "This is unusual: investigate cache integrity.",
                            MessageType.Warning);
                    }
                }
            }
        }

        private void DrawHandleLeaf(int pHandleIndex)
        {
            var h = _current.Handles[pHandleIndex];
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"◇ handleId={h.HandleId}  " +
                (h.Frame > 0 ? $"acquired @frame {h.Frame}" : "frame:n/a"),
                EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(h.AcquisitionStack))
            {
                bool expanded = _expandedHandleStacks.Contains(h.HandleId);
                var rect = EditorGUILayout.GetControlRect();
                bool nowExpanded = EditorGUI.Foldout(rect, expanded, "acquisition stack");
                if (nowExpanded != expanded)
                {
                    if (nowExpanded) _expandedHandleStacks.Add(h.HandleId);
                    else _expandedHandleStacks.Remove(h.HandleId);
                }
                if (nowExpanded)
                {
                    EditorGUILayout.SelectableLabel(
                        h.AcquisitionStack,
                        EditorStyles.textArea,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight * 8f));
                }
            }
            else
            {
                EditorGUILayout.LabelField(
                    "  (define HYPERCONTENT_TRACK_HANDLES to capture acquisition stack)",
                    EditorStyles.miniLabel);
            }
        }

        // ── Filter helpers ──────────────────────────────────────────────────────────

        private bool MatchesFilter(string a, string b, string c)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            if (a != null && a.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (b != null && b.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (c != null && c.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.4f));
        }
    }
}
