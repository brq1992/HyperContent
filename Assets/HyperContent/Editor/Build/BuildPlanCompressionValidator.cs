using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Ensures <see cref="BuildPlan.BundleCompression"/> has one entry per bundle in <see cref="BuildPlan.BundleToAssets"/>.
    /// </summary>
    public static class BuildPlanCompressionValidator
    {
        /// <summary>
        /// Returns null if valid; otherwise an error message.
        /// </summary>
        public static string ValidatePlan(BuildPlan pPlan)
        {
            if (pPlan?.BundleToAssets == null || pPlan.BundleToAssets.Count == 0)
                return null;

            if (pPlan.BundleCompression == null)
                return "BuildPlan.BundleCompression is null but BundleToAssets is non-empty.";

            foreach (var bundleName in pPlan.BundleToAssets.Keys)
            {
                if (!pPlan.BundleCompression.TryGetValue(bundleName, out _))
                    return $"BuildPlan.BundleCompression missing entry for bundle '{bundleName}'.";
            }

            return null;
        }

        /// <summary>
        /// Append human-readable errors to <paramref name="pErrors"/> (for executor validation lists).
        /// </summary>
        public static void AppendValidationErrors(BuildPlan pPlan, List<string> pErrors)
        {
            var msg = ValidatePlan(pPlan);
            if (!string.IsNullOrEmpty(msg))
                pErrors?.Add(msg);
        }
    }
}
