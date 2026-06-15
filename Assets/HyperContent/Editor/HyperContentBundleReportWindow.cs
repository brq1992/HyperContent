using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Inspect build report: bundles, contained assets, and bundle-to-bundle refs (to / by).
    /// Primary data source: build_report.json (SBP deps matching runtime catalog).
    /// Legacy fallback: build_manifest.json (asset-level deps).
    /// </summary>
    public sealed class HyperContentBundleReportWindow : EditorWindow
    {
        private const string CONFIG_RELATIVE_PATH = "ProjectSettings/HyperContentBuildConfig.json";

        private string _reportPath = "";
        private HyperContentBundleReportSnapshot _snapshot;
        private string _loadErrorMessage;

        private Vector2 _leftScroll;
        private Vector2 _rightScroll;
        private readonly HashSet<string> _expandedBundleNameSet = new HashSet<string>(StringComparer.Ordinal);
        private string _selectedBundleName;

        private int _refsTab;
        private static readonly string[] REFS_TAB_LABELS = { "References To", "Referenced By" };

        /// <summary>
        /// 0 = direct (one-hop, matches Addressables BuildLayout Dependencies / DependentBundles).
        /// 1 = transitive (closure, matches what the runtime catalog ships and what HC build_report
        ///     bundleDependencies stores). Picked under each refs tab.
        /// </summary>
        private int _refsScope;
        private static readonly string[] REFS_SCOPE_LABELS = { "Direct (1-hop)", "Transitive (catalog)" };

        /// <summary>Fixed width for the right detail pane (bundle info + dependency tabs).</summary>
        private const float RIGHT_PANE_WIDTH = 300f;

        private string _assetSearchFilter = "";

        [MenuItem("HyperContent/Bundle Report", false, 2)]
        public static void Open()
        {
            var window = GetWindow<HyperContentBundleReportWindow>("HyperContent Bundle Report");
            window.minSize = new Vector2(720, 420);
            window.Show();
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_reportPath))
                _reportPath = GetDefaultReportPath();
            TryLoadReport(pSilent: true);
        }

        private static string GetDefaultReportPath()
        {
            var config = LoadBuildConfig();
            var relative = BuildReportGenerator.GetReportPath(config);
            if (string.IsNullOrEmpty(relative))
                return "";
            return relative.Replace("\\", "/");
        }

        private static BuildConfig LoadBuildConfig()
        {
            if (File.Exists(CONFIG_RELATIVE_PATH))
            {
                try
                {
                    var json = File.ReadAllText(CONFIG_RELATIVE_PATH);
                    var config = JsonUtility.FromJson<BuildConfig>(json);
                    if (config != null)
                        return config;
                }
                catch
                {
                    // Fall through to defaults
                }
            }

            return new BuildConfig
            {
                buildTarget = EditorUserBuildSettings.activeBuildTarget
            };
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            DrawToolbar();

            if (!string.IsNullOrEmpty(_loadErrorMessage))
                EditorGUILayout.HelpBox(_loadErrorMessage, MessageType.Warning);

            if (_snapshot == null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Load a build_manifest.json (Full Build output) to inspect bundles.", EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            DrawMetaLine();
            DrawSearchBar();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawLeftPane();
            DrawRightPane();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Report", GUILayout.Width(44));
            _reportPath = EditorGUILayout.TextField(_reportPath);
            if (GUILayout.Button("Browse", EditorStyles.toolbarButton, GUILayout.Width(56)))
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? "";
                var startDir = Directory.Exists(projectRoot) ? projectRoot : "";
                var picked = EditorUtility.OpenFilePanel("Build Report (JSON)", startDir, "json");
                if (!string.IsNullOrEmpty(picked))
                    _reportPath = picked.Replace("\\", "/");
            }
            if (GUILayout.Button("Load", EditorStyles.toolbarButton, GUILayout.Width(44)))
                TryLoadReport(pSilent: false);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawMetaLine()
        {
            EditorGUILayout.BeginHorizontal();
            var ts = _snapshot.BuildTimestampUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(_snapshot.BuildTimestampUnix).ToString("u")
                : "—";
            EditorGUILayout.LabelField($"Version: {_snapshot.BuildVersion}   UTC: {ts}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSearchBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Search assets", GUILayout.Width(88));
            EditorGUI.BeginChangeCheck();
            _assetSearchFilter = EditorGUILayout.TextField(_assetSearchFilter, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
                Repaint();
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(44)))
            {
                _assetSearchFilter = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawLeftPane()
        {
            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            EditorGUILayout.LabelField("Bundles", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Bundle", EditorStyles.miniLabel, GUILayout.MinWidth(120f));
            GUILayout.Label("Assets", EditorStyles.miniLabel, GUILayout.Width(52f));
            GUILayout.Label("Size", EditorStyles.miniLabel, GUILayout.Width(72f));
            // Each refs cell renders "{direct} / {transitive}" so Addressables BuildLayout
            // (direct only) and HC catalog/runtime closure can be eyeballed side by side.
            GUILayout.Label(new GUIContent("Refs To D/T", "Refs To = bundles this bundle depends on. " +
                "D = direct one-hop edges (matches Addressables BuildLayout Dependencies). " +
                "T = transitive closure (what the runtime catalog ships)."),
                EditorStyles.miniLabel, GUILayout.Width(78f));
            GUILayout.Label(new GUIContent("Refs By D/T", "Refs By = bundles that depend on this bundle. " +
                "D = direct one-hop edges (matches Addressables BuildLayout DependentBundles). " +
                "T = transitive closure (every bundle whose runtime load chain pulls this in)."),
                EditorStyles.miniLabel, GUILayout.Width(78f));
            EditorGUILayout.EndHorizontal();

            _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            foreach (var entry in EnumerateVisibleBundleEntries())
            {
                DrawBundleRow(entry);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Bundles visible under current search: bundle name matches or at least one asset path matches.
        /// </summary>
        private IEnumerable<HyperContentBundleReportEntry> EnumerateVisibleBundleEntries()
        {
            var q = _assetSearchFilter?.Trim() ?? "";
            foreach (var entry in _snapshot.Bundles)
            {
                if (string.IsNullOrEmpty(q) || BundleMatchesSearch(entry, q))
                    yield return entry;
            }
        }

        private static bool BundleMatchesSearch(HyperContentBundleReportEntry pEntry, string pQuery)
        {
            if (pEntry == null || string.IsNullOrEmpty(pQuery))
                return true;
            if (ContainsIgnoreCase(pEntry.BundleName, pQuery))
                return true;
            if (pEntry.AssetPaths == null)
                return false;
            foreach (var path in pEntry.AssetPaths)
            {
                if (!string.IsNullOrEmpty(path) && ContainsIgnoreCase(path, pQuery))
                    return true;
            }
            return false;
        }

        private static bool ContainsIgnoreCase(string pHaystack, string pNeedle)
        {
            if (string.IsNullOrEmpty(pHaystack) || string.IsNullOrEmpty(pNeedle))
                return false;
            return pHaystack.IndexOf(pNeedle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawBundleRow(HyperContentBundleReportEntry pEntry)
        {
            var name = pEntry.BundleName;
            var expanded = _expandedBundleNameSet.Contains(name);
            var searchActive = !string.IsNullOrWhiteSpace(_assetSearchFilter);
            var query = searchActive ? _assetSearchFilter.Trim() : "";
            var bundleNameMatches = searchActive && ContainsIgnoreCase(name, query);

            EditorGUILayout.BeginHorizontal();
            var foldRect = GUILayoutUtility.GetRect(18f, 18f, GUILayout.Width(18f));
            if (GUI.Button(foldRect, expanded ? "▼" : "▶", EditorStyles.label))
            {
                if (expanded)
                    _expandedBundleNameSet.Remove(name);
                else
                    _expandedBundleNameSet.Add(name);
            }

            var isSelected = _selectedBundleName == name;
            var style = isSelected ? EditorStyles.boldLabel : EditorStyles.label;
            if (GUILayout.Button(name, style, GUILayout.MinWidth(100f), GUILayout.ExpandWidth(true)))
            {
                _selectedBundleName = name;
                Repaint();
            }

            GUILayout.Label(pEntry.AssetCount.ToString(), GUILayout.Width(52f));
            GUILayout.Label(FormatBytes(pEntry.SizeBytes), GUILayout.Width(72f));
            GUILayout.Label($"{pEntry.RefsToDirectCount} / {pEntry.RefsToCount}", GUILayout.Width(78f));
            GUILayout.Label($"{pEntry.RefsByDirectCount} / {pEntry.RefsByCount}", GUILayout.Width(78f));
            EditorGUILayout.EndHorizontal();

            if (_expandedBundleNameSet.Contains(name))
            {
                EditorGUI.indentLevel += 2;
                foreach (var assetPath in EnumerateAssetPathsForDisplay(pEntry, query, bundleNameMatches))
                {
                    EditorGUILayout.LabelField(assetPath, EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel -= 2;
            }
        }

        /// <summary>
        /// When search is active: if bundle name matched, list all assets; otherwise only asset paths that match.
        /// </summary>
        private static IEnumerable<string> EnumerateAssetPathsForDisplay(
            HyperContentBundleReportEntry pEntry,
            string pTrimmedQuery,
            bool pBundleNameMatches)
        {
            if (pEntry.AssetPaths == null)
                yield break;
            if (string.IsNullOrEmpty(pTrimmedQuery) || pBundleNameMatches)
            {
                foreach (var path in pEntry.AssetPaths)
                    yield return path;
                yield break;
            }
            foreach (var path in pEntry.AssetPaths)
            {
                if (!string.IsNullOrEmpty(path) && ContainsIgnoreCase(path, pTrimmedQuery))
                    yield return path;
            }
        }

        private void DrawRightPane()
        {
            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(RIGHT_PANE_WIDTH), GUILayout.MaxWidth(RIGHT_PANE_WIDTH), GUILayout.ExpandHeight(true));

            if (string.IsNullOrEmpty(_selectedBundleName))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Select a bundle in the list.", EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            var entry = FindEntry(_selectedBundleName);
            if (entry == null)
            {
                EditorGUILayout.LabelField("Bundle not found in snapshot.", EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("Bundle", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(entry.BundleName, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2f));
            EditorGUILayout.LabelField("Bundle file size", FormatBytes(entry.SizeBytes));

            // Root-level dual-scope counts: shows the same numbers Addressables' BuildLayout would
            // report for this bundle (direct/one-hop) right next to the closure HC actually ships
            // in the runtime catalog (transitive). Lets us answer "is HC counting more deps than
            // Addressables?" by reading the row instead of cross-referencing two reports.
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Refs (direct / transitive)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                new GUIContent(
                    $"References To  : {entry.RefsToDirectCount} direct  /  {entry.RefsToCount} transitive",
                    "Direct = bundles this bundle's packed objects directly reference (one-hop, " +
                    "matches Addressables BuildLayout Bundle.Dependencies).\n" +
                    "Transitive = full closure HC ships in the runtime catalog (matches " +
                    "Addressables BuildLayout Bundle.Dependencies+ExpandedDependencies)."));
            EditorGUILayout.LabelField(
                new GUIContent(
                    $"Referenced By  : {entry.RefsByDirectCount} direct  /  {entry.RefsByCount} transitive",
                    "Direct = bundles that directly reference this bundle (matches Addressables " +
                    "BuildLayout Bundle.DependentBundles).\n" +
                    "Transitive = bundles whose runtime load chain ends up pulling this bundle in."));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bundle dependencies", EditorStyles.boldLabel);
            _refsTab = GUILayout.Toolbar(_refsTab, REFS_TAB_LABELS);
            _refsScope = GUILayout.Toolbar(_refsScope, REFS_SCOPE_LABELS);

            _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            IReadOnlyList<string> list;
            string emptyHint;
            if (_refsTab == 0)
            {
                list = _refsScope == 0 ? entry.RefsToDirectBundleNames : entry.RefsToBundleNames;
                emptyHint = _refsScope == 0
                    ? "This bundle has no direct (one-hop) outgoing edges."
                    : "This bundle does not depend on other HyperContent bundles (or no cross-bundle edges were computed).";
            }
            else
            {
                list = _refsScope == 0 ? entry.RefsByDirectBundleNames : entry.RefsByBundleNames;
                emptyHint = _refsScope == 0
                    ? "No bundles directly reference this bundle (closure may still pull it in — switch to Transitive)."
                    : "No other bundles depend on this bundle.";
            }

            if (list == null || list.Count == 0)
                EditorGUILayout.LabelField(emptyHint, EditorStyles.wordWrappedLabel);
            else
            {
                foreach (var other in list)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("• " + other, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Select", GUILayout.Width(56f)))
                    {
                        _selectedBundleName = other;
                        GUIUtility.keyboardControl = 0;
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private HyperContentBundleReportEntry FindEntry(string pBundleName)
        {
            if (_snapshot == null || string.IsNullOrEmpty(pBundleName))
                return null;
            foreach (var e in _snapshot.Bundles)
            {
                if (string.Equals(e.BundleName, pBundleName, StringComparison.Ordinal))
                    return e;
            }
            return null;
        }

        private void TryLoadReport(bool pSilent)
        {
            _loadErrorMessage = null;
            _snapshot = null;

            if (string.IsNullOrWhiteSpace(_reportPath))
            {
                _loadErrorMessage = "Report path is empty.";
                if (!pSilent)
                    EditorUtility.DisplayDialog("Bundle Report", _loadErrorMessage, "OK");
                return;
            }

            try
            {
                var normalized = _reportPath.Replace("\\", "/");
                var fileName = Path.GetFileName(normalized);

                if (string.Equals(fileName, BuildReportGenerator.REPORT_FILENAME, StringComparison.OrdinalIgnoreCase))
                    _snapshot = HyperContentBundleReportSnapshot.FromReportJsonFile(normalized);
                else
                    _snapshot = HyperContentBundleReportSnapshot.FromManifestJsonFile(normalized);

                _expandedBundleNameSet.Clear();
                _selectedBundleName = null;
                _assetSearchFilter = "";
            }
            catch (Exception ex)
            {
                _loadErrorMessage = ex.Message;
                if (!pSilent)
                    EditorUtility.DisplayDialog("Bundle Report", "Failed to load report:\n" + ex.Message, "OK");
            }
        }

        private static string FormatBytes(long pBytes)
        {
            if (pBytes < 0)
                return "—";
            if (pBytes < 1024)
                return $"{pBytes} B";
            var kb = pBytes / 1024.0;
            if (kb < 1024.0)
                return $"{kb:F1} KB";
            var mb = kb / 1024.0;
            if (mb < 1024.0)
                return $"{mb:F2} MB";
            var gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }
    }
}
