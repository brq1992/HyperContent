using System.Collections.Generic;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Collects missing remote bundles for a <see cref="ResourceLocation"/> tree (catalog + local store rules
    /// aligned with <see cref="BundleDownloadManager"/>). Used for Prompt-before-download and QueryOnly-style queries.
    /// </summary>
    internal static class LoadNetworkPrecheck
    {
        internal static List<BundleDownloadInfo> CollectMissingRemoteForLocation(
            ICatalog pCatalog,
            IBundleStore pBundleStore,
            ResourceLocation pRoot)
        {
            var list = new List<BundleDownloadInfo>();
            if (pCatalog == null || pBundleStore == null || pRoot == null)
                return list;

            var bundleSet = new HashSet<string>();
            BundleDownloadManager.CollectBundleNamesRecursive(pRoot, bundleSet);

            foreach (var bundleName in bundleSet)
            {
                if (!pCatalog.TryGetBundleInfo(bundleName, out var info))
                    continue;
                if (info.Location != ContentLocation.Remote)
                    continue;
                if (!NeedsDownload(pBundleStore, bundleName, info))
                    continue;

                list.Add(new BundleDownloadInfo
                {
                    bundleName = bundleName,
                    sizeBytes = info.Size,
                    remoteUrl = info.RemoteRelativePath
                });
            }

            return list;
        }

        internal static bool NeedsDownload(IBundleStore pStore, string pBundleName, BundleInfo pInfo)
        {
            if (!pStore.Exists(pBundleName))
                return true;
            if (!string.IsNullOrEmpty(pInfo.Hash) &&
                !pStore.VerifyHash(pBundleName, pInfo.Hash))
                return true;
            return false;
        }

        internal static MissingBundlePromptInfo ToMissingSummary(List<BundleDownloadInfo> pList)
        {
            if (pList == null || pList.Count == 0)
                return new MissingBundlePromptInfo(0, 0, null);

            long totalBytes = 0;
            foreach (var b in pList)
                totalBytes += b.sizeBytes;

            var names = new List<string>(pList.Count);
            foreach (var b in pList)
                names.Add(b.bundleName);

            return new MissingBundlePromptInfo(pList.Count, totalBytes, names);
        }
    }
}
