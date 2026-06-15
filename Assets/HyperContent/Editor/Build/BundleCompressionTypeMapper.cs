using System;
using System.Collections.Generic;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using BuildCompression = UnityEngine.BuildCompression;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Maps <see cref="BundleCompressionType"/> to SBP <see cref="BuildCompression"/> (aligned with Addressables BundledAssetGroupSchema).
    /// </summary>
    public static class BundleCompressionTypeMapper
    {
        /// <summary>
        /// Map HyperContent enum to Unity <see cref="BuildCompression"/>.
        /// </summary>
        public static BuildCompression ToBuildCompression(BundleCompressionType pType)
        {
            switch (pType)
            {
                case BundleCompressionType.None:
                    return BuildCompression.Uncompressed;
                case BundleCompressionType.Lz4:
                case BundleCompressionType.Lz4HC:
                    return BuildCompression.LZ4;
                case BundleCompressionType.Lzma:
                    return BuildCompression.LZMA;
                default:
                    return BuildCompression.LZ4;
            }
        }

        /// <summary>
        /// Map Addressables schema compression mode to HyperContent enum.
        /// </summary>
        public static BundleCompressionType FromAddressableSchemaMode(BundledAssetGroupSchema.BundleCompressionMode pMode)
        {
            switch (pMode)
            {
                case BundledAssetGroupSchema.BundleCompressionMode.Uncompressed:
                    return BundleCompressionType.None;
                case BundledAssetGroupSchema.BundleCompressionMode.LZ4:
                    return BundleCompressionType.Lz4;
                case BundledAssetGroupSchema.BundleCompressionMode.LZMA:
                    return BundleCompressionType.Lzma;
                default:
                    return BundleCompressionType.Lz4;
            }
        }

        /// <summary>
        /// Register each bundle name with and without ".bundle" for SBP identifier lookups.
        /// </summary>
        public static Dictionary<string, BuildCompression> BuildIdentifierToCompressionMap(
            IReadOnlyDictionary<string, BundleCompressionType> pPerBundle)
        {
            var map = new Dictionary<string, BuildCompression>(StringComparer.OrdinalIgnoreCase);
            if (pPerBundle == null)
                return map;

            foreach (var kvp in pPerBundle)
            {
                var bc = ToBuildCompression(kvp.Value);
                RegisterBundleNameKeys(map, kvp.Key, bc);
            }

            return map;
        }

        static void RegisterBundleNameKeys(Dictionary<string, BuildCompression> pMap, string pBundleName, BuildCompression pBc)
        {
            if (string.IsNullOrEmpty(pBundleName))
                return;
            pMap[pBundleName] = pBc;
            var withExt = pBundleName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                ? pBundleName
                : pBundleName + ".bundle";
            pMap[withExt] = pBc;
        }
    }
}
