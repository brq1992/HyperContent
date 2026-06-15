using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor.diagnostics
{
    /// <summary>
    /// Plan-time, asset-path-level reverse-reference dump for a single bundle.
    ///
    /// <b>Use this for breadcrumbs, not for counts.</b> The numbers it prints are intentionally
    /// upper bounds — for the post-build truth, read <c>build_report.json</c>:
    /// <list type="bullet">
    ///   <item><description><c>bundleDirectDependencies</c> ↔ Addressables BuildLayout
    ///   <c>Bundle.Dependencies</c> / <c>DependentBundles</c> (one-hop, SBP truth).</description></item>
    ///   <item><description><c>bundleDependencies</c> ↔ Addressables BuildLayout
    ///   <c>Dependencies+ExpandedDependencies</c> (transitive closure shipped in the runtime
    ///   catalog).</description></item>
    /// </list>
    ///
    /// What it actually does: for every non-target explicit asset, walks
    /// <see cref="AssetDatabase.GetDependencies(string, bool)"/> with recursive=true and reports
    /// every hit that lands inside the target bundle's explicit GUID set. That's a path-level
    /// over-approximation of SBP's PPtr-level reference graph, so the numbers tend to come out
    /// somewhere between Addressables' direct count and HC's transitive count. The value here is
    /// the per-referrer-asset breakdown — given a target bundle, which prefab/scene reaches which
    /// asset inside it — which neither <c>build_report.json</c> nor Addressables BuildLayout
    /// expose directly (they stop at the bundle-bundle edge).
    /// </summary>
    public class BundleReverseReferenceDiagnosticWindow : EditorWindow
    {
        private const string EDITOR_PREFS_TARGET = "HyperContent.Diag.RevRef.Target";
        private const string EDITOR_PREFS_MANIFEST_PATH = "HyperContent.Diag.RevRef.Manifest";

        private string _targetBundleName = "townmainscene1";
        private string _manifestPath = "";

        [MenuItem("HyperContent/Diagnostics/Bundle Reverse Reference Dump...")]
        public static void Open()
        {
            var w = GetWindow<BundleReverseReferenceDiagnosticWindow>("HC Bundle Reverse Refs");
            w.minSize = new Vector2(560, 200);
            w.Show();
        }

        private void OnEnable()
        {
            _targetBundleName = EditorPrefs.GetString(EDITOR_PREFS_TARGET, "townmainscene1");
            _manifestPath = EditorPrefs.GetString(EDITOR_PREFS_MANIFEST_PATH, DefaultManifestPath());
        }

        private static string DefaultManifestPath()
        {
            var projRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projRoot, "HyperContentBuild", "Android", "build_manifest.json").Replace("\\", "/");
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Plan-time, asset-path-level over-approximation. For SBP-truth counts use " +
                "build_report.json: bundleDirectDependencies (one-hop, == Addressables BuildLayout) " +
                "or bundleDependencies (transitive closure, == runtime catalog). The value here is " +
                "the per-referrer-asset breakdown (which prefab/scene reaches which asset inside the " +
                "target bundle), which the SBP outputs do not expose directly.",
                MessageType.Info);

            _manifestPath = EditorGUILayout.TextField("Manifest Path", _manifestPath);
            _targetBundleName = EditorGUILayout.TextField("Target Bundle", _targetBundleName);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run Dump", GUILayout.Height(28)))
                {
                    EditorPrefs.SetString(EDITOR_PREFS_TARGET, _targetBundleName);
                    EditorPrefs.SetString(EDITOR_PREFS_MANIFEST_PATH, _manifestPath);
                    Run();
                }
                if (GUILayout.Button("Reset Manifest Path", GUILayout.Width(160)))
                {
                    _manifestPath = DefaultManifestPath();
                }
            }
        }

        private void Run()
        {
            if (string.IsNullOrEmpty(_manifestPath) || !File.Exists(_manifestPath))
            {
                EditorUtility.DisplayDialog("HC Diagnostics", "Manifest not found:\n" + _manifestPath, "OK");
                return;
            }
            if (string.IsNullOrEmpty(_targetBundleName))
            {
                EditorUtility.DisplayDialog("HC Diagnostics", "Target bundle name is empty", "OK");
                return;
            }

            BuildManifest manifest;
            try
            {
                var json = File.ReadAllText(_manifestPath);
                manifest = JsonUtility.FromJson<BuildManifest>(json);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("HC Diagnostics", "Failed to parse manifest: " + e.Message, "OK");
                return;
            }

            var targetGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in manifest.cachedAssets)
            {
                if (string.IsNullOrEmpty(a.guid)) continue;
                if (string.Equals(a.bundleName, _targetBundleName, StringComparison.Ordinal))
                    targetGuids.Add(a.guid);
            }
            if (targetGuids.Count == 0)
            {
                EditorUtility.DisplayDialog("HC Diagnostics",
                    "Target bundle '" + _targetBundleName + "' has no explicit assets in manifest.", "OK");
                return;
            }

            var guidToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in targetGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p))
                    guidToPath[g] = p;
            }

            // referrerBundleName -> referrerPath -> referenced target guids
            var byBundle = new SortedDictionary<string, SortedDictionary<string, List<string>>>(StringComparer.Ordinal);
            var hitGuidRefAssetCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hitGuidRefBundles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            var nonTarget = manifest.cachedAssets
                .Where(a => !string.IsNullOrEmpty(a.guid)
                            && !string.Equals(a.bundleName, _targetBundleName, StringComparison.Ordinal))
                .ToList();

            int totalReferrerAssets = 0;
            int totalReferrerBundles = 0;

            int totalExplicit = nonTarget.Count;
            int processed = 0;
            const int batchSize = 50;

            try
            {
                foreach (var a in nonTarget)
                {
                    if ((processed % batchSize) == 0)
                    {
                        var cancel = EditorUtility.DisplayCancelableProgressBar(
                            "HC Reverse Reference Dump",
                            processed + "/" + totalExplicit + "  (" + a.bundleName + ")",
                            (float)processed / Math.Max(1, totalExplicit));
                        if (cancel)
                            return;
                    }
                    processed++;

                    var referrerPath = AssetDatabase.GUIDToAssetPath(a.guid);
                    if (string.IsNullOrEmpty(referrerPath))
                        continue;

                    var deps = AssetDatabase.GetDependencies(referrerPath, true);
                    if (deps == null || deps.Length == 0) continue;

                    List<string> hits = null;
                    foreach (var depPath in deps)
                    {
                        if (string.IsNullOrEmpty(depPath) || depPath == referrerPath)
                            continue;
                        var depGuid = AssetDatabase.AssetPathToGUID(depPath);
                        if (string.IsNullOrEmpty(depGuid))
                            continue;
                        if (targetGuids.Contains(depGuid))
                        {
                            if (hits == null) hits = new List<string>();
                            hits.Add(depGuid);
                        }
                    }
                    if (hits == null || hits.Count == 0)
                        continue;

                    if (!byBundle.TryGetValue(a.bundleName, out var inner))
                    {
                        inner = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                        byBundle[a.bundleName] = inner;
                        totalReferrerBundles++;
                    }
                    if (!inner.ContainsKey(referrerPath))
                    {
                        inner[referrerPath] = hits;
                        totalReferrerAssets++;
                    }

                    foreach (var hit in hits)
                    {
                        if (!hitGuidRefAssetCount.TryGetValue(hit, out var cnt))
                        {
                            cnt = 0;
                            hitGuidRefBundles[hit] = new HashSet<string>(StringComparer.Ordinal);
                        }
                        hitGuidRefAssetCount[hit] = cnt + 1;
                        hitGuidRefBundles[hit].Add(a.bundleName);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var outputDir = Path.Combine(Path.GetDirectoryName(_manifestPath) ?? "", "Diagnostics");
            Directory.CreateDirectory(outputDir);
            var safeName = _targetBundleName.Replace('/', '_').Replace('\\', '_');
            var outputPath = Path.Combine(outputDir, "reverse_refs_" + safeName + ".txt");

            using (var w = new StreamWriter(outputPath))
            {
                w.WriteLine("=== Reverse references to bundle: " + _targetBundleName + " ===");
                w.WriteLine("manifest         : " + _manifestPath);
                w.WriteLine("target explicit  : " + targetGuids.Count);
                w.WriteLine("referrer bundles : " + totalReferrerBundles);
                w.WriteLine("referrer assets  : " + totalReferrerAssets);
                w.WriteLine();
                w.WriteLine("target GUIDs that are referenced (" + hitGuidRefAssetCount.Count + "/" + targetGuids.Count + "):");
                foreach (var kv in hitGuidRefAssetCount.OrderByDescending(k => k.Value))
                {
                    var p = guidToPath.TryGetValue(kv.Key, out var v) ? v : "?";
                    var bcnt = hitGuidRefBundles[kv.Key].Count;
                    w.WriteLine(string.Format("  refs={0,5} bundles={1,5}  {2}  {3}", kv.Value, bcnt, kv.Key, p));
                }

                w.WriteLine();
                w.WriteLine("target GUIDs NEVER referenced (" + (targetGuids.Count - hitGuidRefAssetCount.Count) + "):");
                foreach (var g in targetGuids)
                {
                    if (hitGuidRefAssetCount.ContainsKey(g))
                        continue;
                    var p = guidToPath.TryGetValue(g, out var v) ? v : "?";
                    w.WriteLine("  " + g + "  " + p);
                }

                w.WriteLine();
                w.WriteLine("=== Per-referrer-bundle breakdown ===");
                foreach (var kv in byBundle)
                {
                    w.WriteLine();
                    w.WriteLine("--- " + kv.Key + " (" + kv.Value.Count + " asset(s)) ---");
                    foreach (var inner in kv.Value)
                    {
                        w.WriteLine("  - " + inner.Key);
                        var distinctHits = inner.Value.Distinct().ToList();
                        foreach (var hit in distinctHits)
                        {
                            var hp = guidToPath.TryGetValue(hit, out var v) ? v : "?";
                            w.WriteLine("      -> " + hit + "  " + hp);
                        }
                    }
                }
            }

            Debug.Log("[HyperContent][RevRef] Dump written: " + outputPath +
                      "  (referrer bundles=" + totalReferrerBundles +
                      ", referrer assets=" + totalReferrerAssets +
                      ", target GUIDs hit=" + hitGuidRefAssetCount.Count +
                      "/" + targetGuids.Count + ")");
            EditorUtility.RevealInFinder(outputPath);
        }
    }
}
