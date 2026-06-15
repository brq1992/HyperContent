using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Exports BuildPlan to text files at the moment GeneratePlan returns.
    /// Format: per-bundle sections with JSON blocks (entry -> dependencies) for easy expand/collapse.
    /// </summary>
    public static class BuildPlanExporter
    {
        public const string PLAN_COMPARE_SUBFOLDER = "PlanCompare";

        /// <summary>
        /// Export the given BuildPlan to a file. Same format for both full and update build.
        /// Structure: header with AssetCount, then one block per bundle: bundle name + JSON (entry path -> dependency paths).
        /// </summary>
        /// <param name="pPlan">The plan returned by GeneratePlan.</param>
        /// <param name="pOutputDir">Directory to write into (e.g. PlatformOutputDirectory/PlanCompare).</param>
        /// <param name="pFileName">e.g. "full_build_plan.txt" or "update_build_plan.txt".</param>
        /// <returns>Full path of the written file, or null if failed.</returns>
        public static string ExportBuildPlan(BuildPlan pPlan, string pOutputDir, string pFileName)
        {
            if (pPlan == null || string.IsNullOrEmpty(pOutputDir) || string.IsNullOrEmpty(pFileName))
                return null;

            Directory.CreateDirectory(pOutputDir);
            var path = Path.Combine(pOutputDir, pFileName);

            var assetToBundle = pPlan.AssetToBundle;
            var guidToPath = pPlan.GuidToPath;
            var dependencies = pPlan.Dependencies;
            var keyToGuid = pPlan.KeyToGuid;
            var bundleToAssets = pPlan.BundleToAssets;

            var entryGuids = keyToGuid != null ? new HashSet<string>(keyToGuid.Values, System.StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            var totalAssetCount = assetToBundle.Count;

            string ResolvePath(string pGuid)
            {
                if (guidToPath != null && guidToPath.TryGetValue(pGuid, out var p) && !string.IsNullOrEmpty(p))
                    return p;
                var dbPath = AssetDatabase.GUIDToAssetPath(pGuid);
                return string.IsNullOrEmpty(dbPath) ? pGuid : dbPath;
            }

            using (var w = new StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                w.WriteLine("# BuildPlan dump (from GeneratePlan)");
                w.WriteLine("# AssetCount: " + totalAssetCount);
                w.WriteLine();

                var sortedBundleNames = (bundleToAssets != null ? bundleToAssets.Keys : Enumerable.Empty<string>())
                    .OrderBy(b => b, System.StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var bundleName in sortedBundleNames)
                {
                    if (!bundleToAssets.TryGetValue(bundleName, out var assetGuidSet) || assetGuidSet == null)
                        continue;

                    var guidsInBundle = new HashSet<string>(assetGuidSet, System.StringComparer.OrdinalIgnoreCase);
                    var entriesInBundle = guidsInBundle.Where(g => entryGuids.Contains(g)).OrderBy(g => g, System.StringComparer.OrdinalIgnoreCase).ToList();

                    var entryToDeps = new Dictionary<string, List<string>>();
                    foreach (var entryGuid in entriesInBundle)
                    {
                        var depsInThisBundle = new List<string>();
                        if (dependencies != null && dependencies.TryGetValue(entryGuid, out var depSet) && depSet != null)
                        {
                            foreach (var depGuid in depSet)
                            {
                                if (guidsInBundle.Contains(depGuid))
                                    depsInThisBundle.Add(ResolvePath(depGuid));
                            }
                            depsInThisBundle.Sort(System.StringComparer.OrdinalIgnoreCase);
                        }
                        entryToDeps[ResolvePath(entryGuid)] = depsInThisBundle;
                    }

                    w.WriteLine(bundleName);
                    w.WriteLine(ToJson(entryToDeps));
                    w.WriteLine();
                }
            }

            return path;
        }

        private static string ToJson(Dictionary<string, List<string>> pEntryToDeps)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            var first = true;
            foreach (var kvp in pEntryToDeps.OrderBy(x => x.Key, System.StringComparer.OrdinalIgnoreCase))
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\n  ");
                sb.Append(JsonEscape(kvp.Key));
                sb.Append(": [");
                for (var i = 0; i < kvp.Value.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(JsonEscape(kvp.Value[i]));
                }
                sb.Append("]");
            }
            if (pEntryToDeps.Count > 0)
                sb.Append("\n");
            sb.Append("}");
            return sb.ToString();
        }

        private static string JsonEscape(string pStr)
        {
            if (string.IsNullOrEmpty(pStr)) return "\"\"";
            var sb = new StringBuilder(pStr.Length + 4);
            sb.Append('"');
            foreach (var c in pStr)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Returns the PlanCompare directory for the given config (e.g. for opening in Explorer).
        /// </summary>
        public static string GetPlanCompareDirectory(BuildConfig pConfig)
        {
            if (pConfig == null)
                return null;
            return Path.Combine(pConfig.PlatformOutputDirectory, PLAN_COMPARE_SUBFOLDER);
        }
    }
}
