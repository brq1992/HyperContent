using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Scripting;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Cross-platform runtime IMGUI debugging panel for HyperContent. Companion to
    /// <c>HyperContentLiveInspectorWindow</c> (Editor only) — same data, same "Referenced By"
    /// pivot, but works on Android device builds where Editor windows are unavailable.
    ///
    /// Wiring:
    ///   HyperContentRuntimeInspector.EnsureInstance();   // call once at startup, e.g. from a debug bootstrap
    ///   HyperContentRuntimeInspector.Toggle();           // hook to a debug menu button
    ///   // Or: 3-finger 1-second hold on touch devices (configurable on the component).
    ///
    /// Capture is user-driven by default — the panel does NOT auto-poll. Press [Capture]
    /// (or enable [Auto]) to refresh. Rendering and snapshot collection are skipped entirely
    /// while the panel is hidden, so a permanent component instance has effectively zero cost.
    ///
    /// Bundle row → "Referenced By" tree pivots on <see cref="BundleSummary.OpLocationHash"/>
    /// to enumerate the asset ops loaded out of that bundle, then drills into handles
    /// (with acquisition stack when <c>HYPERCONTENT_TRACK_HANDLES</c> is defined) and instances.
    /// That's the "which call site is keeping this bundle alive" diagnostic chain in one
    /// click.
    ///
    /// Define <c>HYPERCONTENT_TRACK_HANDLES</c> in PlayerSettings → Scripting Define Symbols
    /// to capture per-handle acquisition stack traces. Without it, you still see exactly
    /// which handleId is unreleased (handle granularity is preserved by the cache layer);
    /// you just won't get the call-site stack — only the count.
    /// </summary>
    [AddComponentMenu("HyperContent/Runtime Inspector")]
    public sealed class HyperContentRuntimeInspector : MonoBehaviour
    {
        // ── Inspector-tunable configuration ──────────────────────────────────────

        [SerializeField] private bool startVisible = false;
        [SerializeField] private bool autoRefresh = false;
        [SerializeField, Range(0.1f, 2f)] private float refreshInterval = 0.5f;
        [SerializeField, Range(1f, 3f)] private float guiScale = 1.5f;
        [SerializeField] private bool enableMultiTouchGesture = true;
        [SerializeField, Range(2, 5)] private int gestureFingerCount = 3;
        [SerializeField, Range(0.3f, 3f)] private float gestureHoldSeconds = 1f;
        [SerializeField, Range(2, 16)] private int treeMaxDepth = 8;
        [Tooltip("Editor / PC keyboard shortcut to toggle visibility. Set to None to disable.")]
        [SerializeField] private KeyCode toggleKey = KeyCode.F12;

        // ── Runtime state ────────────────────────────────────────────────────────

        public static HyperContentRuntimeInspector Instance { get; private set; }

        private DiagnosticsSnapshot _snapshot;
        private DiagnosticsSnapshot _baseline;
                [SerializeField]
        private bool _visible;
        private float _nextRefreshAt;

        private int _tab; // 0=Operations 1=Bundles 2=Instances 3=Handles 4=Deps
        private string _filter = string.Empty;
        private bool _onlyOrphans;
        private bool _onlyDiff;

        // _selectedOpHash drives the Referenced By tree (always op-hash-rooted).
        // Per-tab id selections coexist so switching tabs preserves each tab's own row highlight.
        private int _selectedOpHash;
        private int _selectedHandleId;
        private int _selectedInstanceId;
        private string _selectedBundleName;

        private float _gestureStartTime = -1f;
        private Vector2 _listScroll;
        private Vector2 _treeScroll;
        private readonly HashSet<int> _expandedHandleStacks = new HashSet<int>();
        private readonly HashSet<int> _treeWalkSeen = new HashSet<int>();

        // Baseline diff sets — rebuilt when a baseline snapshot is captured/cleared.
        private readonly HashSet<int> _baselineOpHashes = new HashSet<int>();
        private readonly HashSet<string> _baselineBundleNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<int> _baselineInstanceIds = new HashSet<int>();
        private readonly HashSet<int> _baselineHandleIds = new HashSet<int>();

        // GUI styles. Lazily initialized inside OnGUI; touching GUI.skin from Awake throws.
        private GUIStyle _styleHeader;
        private GUIStyle _styleRowNormal;
        private GUIStyle _styleRowSelected;
        private GUIStyle _styleRowOrphan;
        private GUIStyle _styleRowOrphanSelected;
        private GUIStyle _stylePaneTitle;
        private GUIStyle _styleStack;
        private GUIStyle _styleEmpty;
        private bool _stylesInitialized;
        private Texture2D _texSelected;
        private Texture2D _texPanel;

        private static readonly string[] TabLabels = new[] { "Operations", "Bundles", "Instances", "Handles", "Deps" };

        // "Deps" tab state: query an address → asset-level dependency bundle set (mode-aware).
        private string _depQueryInput = string.Empty;
        private DependencyBundleQuery _depQueryResult;
        private Vector2 _depScroll;

        // ── Static API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Lazily create a persistent <see cref="DontDestroyOnLoad"/> singleton instance.
        /// Idempotent — repeat calls return the same instance.
        /// </summary>
        [Preserve]
        public static HyperContentRuntimeInspector EnsureInstance()
        {
            if (Instance == null)
            {
                var go = new GameObject(nameof(HyperContentRuntimeInspector));
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<HyperContentRuntimeInspector>();
            }
            return Instance;
        }

        [Preserve] public static void Show()   { EnsureInstance()._visible = true; }
        [Preserve] public static void Hide()   { if (Instance != null) Instance._visible = false; }
        [Preserve] public static void Toggle() { var inst = EnsureInstance(); inst._visible = !inst._visible; }

        /// <summary>
        /// Capture a fresh snapshot, format the full diagnostics report, and write it to
        /// <c>Application.persistentDataPath/{fileName}</c>. Returns the absolute path on
        /// success, null on failure. The path is also <c>Debug.Log</c>-ged so it's easy to
        /// spot in <c>logcat</c> for <c>adb pull</c>.
        /// </summary>
        [Preserve]
        public static string ExportSnapshotToFile(string pFileName = "hc_diagnostics.txt")
        {
            string safeName = string.IsNullOrEmpty(pFileName) ? "hc_diagnostics.txt" : pFileName;
            DiagnosticsSnapshot reuse = Instance != null ? Instance._snapshot : null;
            var snap = HyperContentDiagnostics.GetSnapshot(reuse);
            if (Instance != null) Instance._snapshot = snap;
            string text = HyperContentDiagnostics.FormatReport(snap);
            string path = Path.Combine(Application.persistentDataPath, safeName);
            try
            {
                File.WriteAllText(path, text);
                Debug.Log($"[HC] Diagnostics exported: {path}");
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HC] Diagnostics export failed: {ex.Message}");
                return null;
            }
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _visible = startVisible;
            _snapshot = new DiagnosticsSnapshot();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DestroyTexture(ref _texSelected);
            DestroyTexture(ref _texPanel);
        }

        private void Update()
        {
            if (enableMultiTouchGesture)
                DetectGesture();
            if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
                _visible = !_visible;
        }

        /// <summary>
        /// N-finger touch hold gesture. Once the configured finger count is held for the
        /// configured duration, visibility is toggled and the gesture is "armed" (set to
        /// +Infinity) so it doesn't re-fire every frame; releasing all fingers re-arms it.
        /// </summary>
        private void DetectGesture()
        {
            int n = Input.touchCount;
            if (n == gestureFingerCount)
            {
                if (_gestureStartTime < 0f)
                {
                    _gestureStartTime = Time.unscaledTime;
                }
                else if (!float.IsPositiveInfinity(_gestureStartTime)
                         && Time.unscaledTime - _gestureStartTime >= gestureHoldSeconds)
                {
                    _visible = !_visible;
                    _gestureStartTime = float.PositiveInfinity;
                }
            }
            else if (n == 0)
            {
                _gestureStartTime = -1f;
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            if (autoRefresh && Time.unscaledTime >= _nextRefreshAt)
            {
                CaptureNow();
                _nextRefreshAt = Time.unscaledTime + Mathf.Max(0.1f, refreshInterval);
            }

            // GUI scaling for high-DPI mobile screens. Save/restore the matrix so other OnGUI
            // listeners in the project are unaffected.
            var prevMatrix = GUI.matrix;
            float scale = Mathf.Max(1f, guiScale);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;
            float h = Screen.height / scale;
            var area = new Rect(0, 0, w, h);

            // Translucent black backdrop swallows touches so they don't pass through to game UI.
            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = prevColor;

            GUILayout.BeginArea(area);
            DrawHeader();
            DrawTabs();
            // Approximate header+tabs height. ListPane and TreePane share the remainder 50/50.
            const float reservedTop = 200f;
            float panelH = Mathf.Max(80f, (h - reservedTop) * 0.5f);
            DrawListPane(panelH);
            DrawTreePane(panelH);
            GUILayout.EndArea();

            GUI.matrix = prevMatrix;
        }

        // ── Header / Tabs ────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            GUILayout.BeginVertical(_styleHeader);

            DrawStatusRow();
            DrawActionRow();
            DrawBaselineRow();
            DrawFilterRow();

            GUILayout.EndVertical();
        }

        private void DrawStatusRow()
        {
            string text;
            if (_snapshot == null || !_snapshot.IsInitialized)
            {
                text = "HyperContent: not initialized";
            }
            else
            {
                string baselineSuffix = _baseline != null
                    ? $"  | baseline frame={_baseline.FrameCount}"
                    : string.Empty;
                text =
                    $"Mode={_snapshot.Mode}  " +
                    $"ops={_snapshot.Operations.Count}  " +
                    $"bundles={_snapshot.Bundles.Count}  " +
                    $"orphans={_snapshot.OrphanedBundleCount}  " +
                    $"instances={_snapshot.Instances.Count}  " +
                    $"handles={_snapshot.Handles.Count}" +
                    baselineSuffix;
            }
            GUILayout.Label(text, _stylePaneTitle);
        }

        private void DrawActionRow()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture", GUILayout.MinWidth(90), GUILayout.MinHeight(28)))
                CaptureNow();
            autoRefresh = GUILayout.Toggle(autoRefresh, $"Auto ({refreshInterval:F1}s)",
                "Button", GUILayout.MinHeight(28), GUILayout.MinWidth(110));
            if (GUILayout.Button("Log Report", GUILayout.MinHeight(28)))
                HyperContentDiagnostics.LogReport();
            if (GUILayout.Button("Export", GUILayout.MinHeight(28)))
                ExportSnapshotToFile();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.MinWidth(70), GUILayout.MinHeight(28)))
                _visible = false;
            GUILayout.EndHorizontal();
        }

        private void DrawBaselineRow()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture Baseline", GUILayout.MinHeight(24)))
                CaptureBaseline();
            if (GUILayout.Button("Clear Baseline", GUILayout.MinHeight(24)))
                ClearBaseline();
            _onlyDiff = GUILayout.Toggle(_onlyDiff, "Only Diff vs Baseline",
                "Button", GUILayout.MinHeight(24), GUILayout.MinWidth(170));
            if (_onlyDiff && _baseline == null)
                GUILayout.Label("(no baseline yet)", _styleEmpty);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawFilterRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(50));
            _filter = GUILayout.TextField(_filter ?? string.Empty, GUILayout.MinWidth(220));
            if (GUILayout.Button("X", GUILayout.Width(28), GUILayout.MinHeight(22)))
                _filter = string.Empty;
            _onlyOrphans = GUILayout.Toggle(_onlyOrphans, "Only Orphans",
                "Button", GUILayout.MinHeight(22), GUILayout.MinWidth(110));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawTabs()
        {
            int newTab = GUILayout.Toolbar(_tab, TabLabels, GUILayout.MinHeight(26));
            if (newTab != _tab)
                _tab = newTab;
        }

        // ── List pane ────────────────────────────────────────────────────────────

        private void DrawListPane(float pHeight)
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(pHeight));
            GUILayout.Label(TabLabels[_tab], _stylePaneTitle);

            // Deps tab is snapshot-independent (queries the live catalog directly).
            if (_tab == 4)
            {
                DrawDepsView();
                GUILayout.EndVertical();
                return;
            }

            if (_snapshot == null || !_snapshot.IsInitialized)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    _snapshot == null
                        ? "No snapshot yet — press Capture"
                        : "HyperContent not initialized",
                    _styleEmpty);
                GUILayout.FlexibleSpace();
                GUILayout.EndVertical();
                return;
            }

            _listScroll = GUILayout.BeginScrollView(_listScroll);
            switch (_tab)
            {
                case 0: DrawOperationsList(); break;
                case 1: DrawBundlesList(); break;
                case 2: DrawInstancesList(); break;
                case 3: DrawHandlesList(); break;
            }
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private void DrawOperationsList()
        {
            var ops = _snapshot.Operations;
            int shown = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                if (!MatchesFilter(op.Address, op.InternalId)) continue;
                if (_onlyDiff && _baselineOpHashes.Contains(op.LocationHash)) continue;
                shown++;
                bool selected = op.LocationHash == _selectedOpHash;
                GUIStyle st = selected ? _styleRowSelected : _styleRowNormal;
                if (GUILayout.Button(FormatOperationRow(op), st))
                    _selectedOpHash = op.LocationHash;
            }
            if (shown == 0)
                GUILayout.Label("(no rows match filter)", _styleEmpty);
        }

        private void DrawBundlesList()
        {
            if (_snapshot.Mode == DiagnosticsMode.AssetDatabase)
            {
                GUILayout.Label(
                    "AssetDatabase mode — no bundles in play.\n" +
                    "Use Operations / Handles / Instances tabs to find leaks at the asset level.",
                    _styleEmpty);
                return;
            }

            var bundles = _snapshot.Bundles;
            int shown = 0;
            for (int i = 0; i < bundles.Count; i++)
            {
                var b = bundles[i];
                if (_onlyOrphans && b.IsReachableFromCache) continue;
                if (!MatchesFilter(b.BundleName)) continue;
                if (_onlyDiff && _baselineBundleNames.Contains(b.BundleName)) continue;
                shown++;
                bool selected = (b.OpLocationHash != 0 && b.OpLocationHash == _selectedOpHash)
                                || (b.OpLocationHash == 0 && b.BundleName == _selectedBundleName);
                GUIStyle st = !b.IsReachableFromCache
                    ? (selected ? _styleRowOrphanSelected : _styleRowOrphan)
                    : (selected ? _styleRowSelected : _styleRowNormal);
                string label = FormatBundleRow(b);
                if (GUILayout.Button(label, st))
                {
                    _selectedBundleName = b.BundleName;
                    _selectedOpHash = b.OpLocationHash;
                }
            }
            if (shown == 0)
                GUILayout.Label("(no bundles match filter)", _styleEmpty);
        }

        private void DrawInstancesList()
        {
            var instances = _snapshot.Instances;
            int shown = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                var it = instances[i];
                if (!MatchesFilter(it.InstanceName, it.OpAddress)) continue;
                if (_onlyDiff && _baselineInstanceIds.Contains(it.InstanceId)) continue;
                shown++;
                bool selected = it.InstanceId == _selectedInstanceId;
                string label = $"id={it.InstanceId} name=\"{it.InstanceName ?? "?"}\"  " +
                               $"op=[{it.OpLocationHash}] {it.OpAddress ?? "?"}";
                GUIStyle st = selected ? _styleRowSelected : _styleRowNormal;
                if (GUILayout.Button(label, st))
                {
                    _selectedInstanceId = it.InstanceId;
                    _selectedOpHash = it.OpLocationHash;
                }
            }
            if (shown == 0)
                GUILayout.Label("(no instances match filter)", _styleEmpty);
        }

        private void DrawHandlesList()
        {
            var handles = _snapshot.Handles;
            int shown = 0;
            for (int i = 0; i < handles.Count; i++)
            {
                var h = handles[i];
                if (!MatchesFilter(h.Address)) continue;
                if (_onlyDiff && _baselineHandleIds.Contains(h.HandleId)) continue;
                shown++;
                bool selected = h.HandleId == _selectedHandleId;
                string label = $"handleId={h.HandleId} op=[{h.LocationHash}] {h.Address ?? "?"}";
                if (h.Frame > 0)
                    label += $"  frame={h.Frame} t={h.TimeAtAcquire:F2}";
                GUIStyle st = selected ? _styleRowSelected : _styleRowNormal;
                if (GUILayout.Button(label, st))
                {
                    _selectedHandleId = h.HandleId;
                    _selectedOpHash = h.LocationHash;
                }
                if (selected && !string.IsNullOrEmpty(h.AcquisitionStack))
                    GUILayout.Label(h.AcquisitionStack, _styleStack);
            }
            if (handles.Count == 0)
                GUILayout.Label("(define HYPERCONTENT_TRACK_HANDLES to capture handles & stacks)", _styleEmpty);
            else if (shown == 0)
                GUILayout.Label("(no handles match filter)", _styleEmpty);
        }

        // ── Deps tab (address → dependency bundles) ───────────────────────────────

        private void DrawDepsView()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Address:", GUILayout.Width(70));
            _depQueryInput = GUILayout.TextField(_depQueryInput ?? string.Empty, GUILayout.MinWidth(260));
            if (GUILayout.Button("Query", GUILayout.Width(80), GUILayout.MinHeight(24)))
                HyperContentDiagnostics.TryQueryDependencyBundles(_depQueryInput, out _depQueryResult);
            if (GUILayout.Button("X", GUILayout.Width(28), GUILayout.MinHeight(22)))
            {
                _depQueryInput = string.Empty;
                _depQueryResult = null;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label("Enter a GUID or addressable name, then Query. Shows the bundle set this asset " +
                            "would load under the active mode (owning bundle LAST).", _styleEmpty);

            if (_depQueryResult == null)
                return;

            var r = _depQueryResult;
            GUILayout.Label($"address: {r.Address}", _styleRowNormal);
            GUILayout.Label($"mode: {r.Mode}    resolved: {r.Resolved}", _styleRowNormal);
            if (!string.IsNullOrEmpty(r.Note))
                GUILayout.Label($"note: {r.Note}", _styleEmpty);

            int count = r.BundleNames != null ? r.BundleNames.Count : 0;
            GUILayout.Label($"dependency bundles ({count}):", _stylePaneTitle);

            _depScroll = GUILayout.BeginScrollView(_depScroll);
            if (count == 0)
            {
                GUILayout.Label("(none)", _styleEmpty);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    bool isOwning = i == count - 1;
                    string label = isOwning
                        ? $"  [{i}] {r.BundleNames[i]}  (owning)"
                        : $"  [{i}] {r.BundleNames[i]}";
                    GUILayout.Label(label, _styleRowNormal);
                }
            }
            GUILayout.EndScrollView();
        }

        // ── Tree pane (Referenced By) ────────────────────────────────────────────

        private void DrawTreePane(float pHeight)
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(pHeight));
            GUILayout.Label("Referenced By", _stylePaneTitle);

            if (_snapshot == null || !_snapshot.IsInitialized)
            {
                GUILayout.Label("(no snapshot)", _styleEmpty);
                GUILayout.EndVertical();
                return;
            }
            if (_selectedOpHash == 0
                || !_snapshot.OperationByHash.TryGetValue(_selectedOpHash, out int rootIdx))
            {
                GUILayout.Label("Select a row above to see what holds it.", _styleEmpty);
                GUILayout.EndVertical();
                return;
            }

            _treeScroll = GUILayout.BeginScrollView(_treeScroll);
            _treeWalkSeen.Clear();
            DrawReferencedBySubtree(rootIdx, pDepth: 0);
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private void DrawReferencedBySubtree(int pOpIndex, int pDepth)
        {
            if (pDepth > treeMaxDepth) return;

            var op = _snapshot.Operations[pOpIndex];
            if (!_treeWalkSeen.Add(op.LocationHash))
            {
                GUILayout.Label(
                    $"{Indent(pDepth)}cycle to op [{op.LocationHash}] (already shown)",
                    _styleRowNormal);
                return;
            }

            int handleCount = _snapshot.HandlesByOp.TryGetValue(op.LocationHash, out var hs) ? hs.Count : 0;
            int instanceCount = _snapshot.InstancesByOp.TryGetValue(op.LocationHash, out var ins) ? ins.Count : 0;
            int dependentCount = _snapshot.DependentEdges.TryGetValue(op.LocationHash, out var deps) ? deps.Count : 0;

            string nodeLabel = $"{Indent(pDepth)}[{op.LocationHash}] {op.Address ?? "?"}  " +
                               $"RefCount={op.RefCount} (handles={handleCount}, instances={instanceCount}, dependents={dependentCount})";
            GUILayout.Label(nodeLabel, _styleRowNormal);

            if (hs != null)
            {
                for (int i = 0; i < hs.Count; i++)
                    DrawHandleLeaf(hs[i], pDepth + 1);
            }
            if (ins != null)
            {
                for (int i = 0; i < ins.Count; i++)
                {
                    var instance = _snapshot.Instances[ins[i]];
                    GUILayout.Label(
                        $"{Indent(pDepth + 1)}* instance id={instance.InstanceId} name=\"{instance.InstanceName ?? "?"}\"",
                        _styleRowNormal);
                }
            }
            if (deps != null)
            {
                for (int i = 0; i < deps.Count; i++)
                    DrawReferencedBySubtree(deps[i], pDepth + 1);
            }
        }

        private void DrawHandleLeaf(int pHandleIdx, int pDepth)
        {
            var h = _snapshot.Handles[pHandleIdx];
            bool hasStack = !string.IsNullOrEmpty(h.AcquisitionStack);
            bool expanded = _expandedHandleStacks.Contains(h.HandleId);
            string toggle = hasStack ? (expanded ? "[-]" : "[+]") : "[ ]";
            string label = $"{Indent(pDepth)}{toggle} handleId={h.HandleId}";
            if (h.Frame > 0)
                label += $"  frame={h.Frame} t={h.TimeAtAcquire:F2}";
            if (GUILayout.Button(label, _styleRowNormal) && hasStack)
            {
                if (expanded) _expandedHandleStacks.Remove(h.HandleId);
                else _expandedHandleStacks.Add(h.HandleId);
            }
            if (expanded && hasStack)
                GUILayout.Label(h.AcquisitionStack, _styleStack);
        }

        // ── Capture / Baseline ───────────────────────────────────────────────────

        private void CaptureNow()
        {
            _snapshot = HyperContentDiagnostics.GetSnapshot(_snapshot ?? new DiagnosticsSnapshot());
        }

        private void CaptureBaseline()
        {
            _baseline = HyperContentDiagnostics.GetSnapshot(_baseline ?? new DiagnosticsSnapshot());
            RebuildBaselineSets();
        }

        private void ClearBaseline()
        {
            _baseline = null;
            _baselineOpHashes.Clear();
            _baselineBundleNames.Clear();
            _baselineInstanceIds.Clear();
            _baselineHandleIds.Clear();
        }

        private void RebuildBaselineSets()
        {
            _baselineOpHashes.Clear();
            _baselineBundleNames.Clear();
            _baselineInstanceIds.Clear();
            _baselineHandleIds.Clear();
            if (_baseline == null) return;

            var ops = _baseline.Operations;
            for (int i = 0; i < ops.Count; i++) _baselineOpHashes.Add(ops[i].LocationHash);
            var bs = _baseline.Bundles;
            for (int i = 0; i < bs.Count; i++) _baselineBundleNames.Add(bs[i].BundleName);
            var ins = _baseline.Instances;
            for (int i = 0; i < ins.Count; i++) _baselineInstanceIds.Add(ins[i].InstanceId);
            var hs = _baseline.Handles;
            for (int i = 0; i < hs.Count; i++) _baselineHandleIds.Add(hs[i].HandleId);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private bool MatchesFilter(params string[] pCandidates)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            for (int i = 0; i < pCandidates.Length; i++)
            {
                var c = pCandidates[i];
                if (!string.IsNullOrEmpty(c)
                    && c.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private string FormatOperationRow(OperationSummary pOp)
        {
            int handleCount = _snapshot.HandlesByOp.TryGetValue(pOp.LocationHash, out var hs) ? hs.Count : 0;
            int instanceCount = _snapshot.InstancesByOp.TryGetValue(pOp.LocationHash, out var ins) ? ins.Count : 0;
            int dependentCount = _snapshot.DependentEdges.TryGetValue(pOp.LocationHash, out var deps) ? deps.Count : 0;
            return $"[{pOp.LocationHash}] {pOp.Address ?? "?"}  " +
                   $"RefCount={pOp.RefCount} (handles={handleCount}, instances={instanceCount}, dependents={dependentCount})  " +
                   $"status={pOp.Status}";
        }

        private static string FormatBundleRow(BundleSummary pB)
        {
            string status = pB.IsReachableFromCache
                ? $"reachable refCount={pB.RefCount}"
                : "ORPHAN — HC-side leak";
            return $"{pB.BundleName}  [{status}]";
        }

        private static string Indent(int pDepth)
        {
            if (pDepth <= 0) return string.Empty;
            return new string(' ', pDepth * 2);
        }

        // ── Styles ───────────────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _texSelected = MakeTex(2, 2, new Color(0.20f, 0.45f, 0.75f, 1f));
            _texPanel = MakeTex(2, 2, new Color(0.10f, 0.10f, 0.12f, 0.95f));

            _styleHeader = new GUIStyle(GUI.skin.box);
            _styleHeader.normal.background = _texPanel;
            _styleHeader.padding = new RectOffset(6, 6, 6, 6);

            _styleRowNormal = new GUIStyle(GUI.skin.label);
            _styleRowNormal.fontSize = 12;
            _styleRowNormal.padding = new RectOffset(6, 6, 2, 2);
            _styleRowNormal.alignment = TextAnchor.MiddleLeft;
            _styleRowNormal.wordWrap = false;
            _styleRowNormal.stretchWidth = true;
            _styleRowNormal.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 1f);

            _styleRowSelected = new GUIStyle(_styleRowNormal);
            _styleRowSelected.normal.background = _texSelected;
            _styleRowSelected.normal.textColor = Color.white;
            _styleRowSelected.hover.textColor = Color.white;
            _styleRowSelected.active.textColor = Color.white;

            _styleRowOrphan = new GUIStyle(_styleRowNormal);
            _styleRowOrphan.normal.textColor = new Color(1f, 0.35f, 0.35f, 1f);

            _styleRowOrphanSelected = new GUIStyle(_styleRowOrphan);
            _styleRowOrphanSelected.normal.background = _texSelected;
            _styleRowOrphanSelected.normal.textColor = new Color(1f, 0.55f, 0.55f, 1f);

            _stylePaneTitle = new GUIStyle(GUI.skin.label);
            _stylePaneTitle.fontStyle = FontStyle.Bold;
            _stylePaneTitle.fontSize = 14;
            _stylePaneTitle.padding = new RectOffset(6, 6, 4, 4);
            _stylePaneTitle.normal.textColor = new Color(0.95f, 0.85f, 0.55f, 1f);
            _stylePaneTitle.wordWrap = true;
            _stylePaneTitle.stretchWidth = true;

            _styleStack = new GUIStyle(GUI.skin.label);
            _styleStack.fontSize = 11;
            _styleStack.wordWrap = true;
            _styleStack.padding = new RectOffset(20, 6, 2, 4);
            _styleStack.normal.textColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            _styleStack.stretchWidth = true;

            _styleEmpty = new GUIStyle(GUI.skin.label);
            _styleEmpty.alignment = TextAnchor.MiddleCenter;
            _styleEmpty.fontStyle = FontStyle.Italic;
            _styleEmpty.normal.textColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            _styleEmpty.stretchWidth = true;
            _styleEmpty.padding = new RectOffset(6, 6, 8, 8);
        }

        private static Texture2D MakeTex(int pWidth, int pHeight, Color pColor)
        {
            var pix = new Color[pWidth * pHeight];
            for (int i = 0; i < pix.Length; i++) pix[i] = pColor;
            var tex = new Texture2D(pWidth, pHeight);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        private static void DestroyTexture(ref Texture2D pTex)
        {
            if (pTex == null) return;
            if (Application.isPlaying) Destroy(pTex);
            else DestroyImmediate(pTex);
            pTex = null;
        }
    }
}
