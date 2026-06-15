using System;
using System.Collections.Generic;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// How a load operation may use the network for missing remote bundles.
    /// When load options are omitted at the facade, the effective mode is <see cref="Silent"/> (see CONVENTIONS.md §1.3).
    /// </summary>
    public enum LoadAssetNetworkMode
    {
        /// <summary>
        /// Missing remote bundles are enqueued for download (High) without user interaction.
        /// </summary>
        Silent = 0,

        /// <summary>
        /// Before any download for this load, invoke <see cref="LoadNetworkOptions.UserConfirmMissingBundleDownload"/>
        /// with aggregated missing count and bytes (catalog snapshot only). If the callback returns false, the load fails.
        /// </summary>
        PromptBeforeDownload = 1,

        /// <summary>
        /// Resolve dependencies and report missing remote bundles only; do not enqueue downloads.
        /// </summary>
        QueryOnly = 2
    }

    /// <summary>
    /// Scope for <c>HasPendingDownloads</c> / <c>GetPendingDownloads</c> style queries (facade; Owner2/Owner3 implement filtering).
    /// </summary>
    public enum PendingBundleQueryScope
    {
        /// <summary>
        /// All remote bundles that are not yet satisfied locally (same broad semantics as the current no-arg APIs).
        /// </summary>
        All = 0,

        /// <summary>
        /// Only remote bundles marked <see cref="BundleTagFlags.Blocking"/> that are not yet satisfied locally.
        /// Must use the same &quot;missing&quot; rules as <c>DownloadAllBlockingBundlesAsync</c>.
        /// </summary>
        BlockingOnly = 1
    }

    /// <summary>
    /// UI-oriented summary for a prompt or for <see cref="LoadAssetNetworkMode.QueryOnly"/> results.
    /// Byte totals use only <see cref="BundleInfo.Size"/> from the current in-memory catalog for the missing remote bundle list; no HTTP HEAD.
    /// Records with unknown size contribute 0. Does not include CDN base, full URLs, or per-file URL lists.
    /// </summary>
    public readonly struct MissingBundlePromptInfo
    {
        /// <summary>Number of distinct remote bundles that are missing locally for the operation.</summary>
        public int MissingBundleCount { get; }

        /// <summary>Sum of <see cref="BundleInfo.Size"/> for those bundles (0 if unknown sizes).</summary>
        public long TotalMissingBytes { get; }

        /// <summary>Optional bundle names for debugging or advanced UI; may be null or empty.</summary>
        public IReadOnlyList<string> MissingBundleNames { get; }

        public MissingBundlePromptInfo(
            int pMissingBundleCount,
            long pTotalMissingBytes,
            IReadOnlyList<string> pMissingBundleNames = null)
        {
            MissingBundleCount = pMissingBundleCount;
            TotalMissingBytes = pTotalMissingBytes;
            MissingBundleNames = pMissingBundleNames;
        }
    }

    /// <summary>
    /// Result of a <see cref="LoadAssetNetworkMode.QueryOnly"/> load attempt: whether the asset can be satisfied locally,
    /// plus the same aggregate missing-bundle info as used for prompts.
    /// </summary>
    public readonly struct LoadAvailabilityResult
    {
        /// <summary>True if no remote bundle download is required for this load given current local cache and catalog.</summary>
        public bool CanCompleteLocally { get; }

        /// <summary>When <see cref="CanCompleteLocally"/> is false, catalog-based counts and sizes for missing remote bundles.</summary>
        public MissingBundlePromptInfo MissingSummary { get; }

        /// <summary>0 on success paths; on failure (e.g. invalid key, catalog error) use a value from <see cref="com.igg.hypercontent.shared.ErrorCode"/>.</summary>
        public int ErrorCode { get; }

        /// <summary>Human-readable detail when <see cref="ErrorCode"/> is non-zero or for logging.</summary>
        public string Message { get; }

        public LoadAvailabilityResult(
            bool pCanCompleteLocally,
            MissingBundlePromptInfo pMissingSummary,
            int pErrorCode = 0,
            string pMessage = null)
        {
            CanCompleteLocally = pCanCompleteLocally;
            MissingSummary = pMissingSummary;
            ErrorCode = pErrorCode;
            Message = pMessage;
        }
    }

    /// <summary>
    /// Options for load entry points that may trigger remote bundle downloads (<c>LoadAsync</c>, <c>InstantiateAsync</c>, <c>LoadSceneAsync</c>, etc.).
    /// Default property values correspond to <see cref="LoadAssetNetworkMode.Silent"/>.
    /// </summary>
    public sealed class LoadNetworkOptions
    {
        /// <summary>Network behavior for missing remote bundles.</summary>
        public LoadAssetNetworkMode Mode { get; set; } = LoadAssetNetworkMode.Silent;

        /// <summary>
        /// When <see cref="Mode"/> is <see cref="LoadAssetNetworkMode.PromptBeforeDownload"/>, must be set by the application.
        /// Return true to enqueue downloads; false to abort the load (facade should fail with <see cref="com.igg.hypercontent.shared.ErrorCode.OPERATION_USER_DECLINED_REMOTE_DOWNLOAD"/>).
        /// Callback thread matches the runtime implementation; marshal to the Unity main thread inside the callback if UI is shown.
        /// </summary>
        public Func<MissingBundlePromptInfo, bool> UserConfirmMissingBundleDownload { get; set; }
    }
}
