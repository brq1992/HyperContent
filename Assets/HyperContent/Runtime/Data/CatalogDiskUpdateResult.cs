namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// High-level outcome of <see cref="com.igg.hypercontent.HyperContent.TryUpdateCachedCatalogOnDiskAsync"/>.
    /// Disk-only: does not reload <see cref="LocalContentCatalog"/> in memory; call <see cref="com.igg.hypercontent.HyperContent.ReloadRuntimeCatalogAsync"/> when appropriate.
    /// </summary>
    public enum CatalogDiskUpdateKind
    {
        /// <summary>Remote hash matches cache; no catalog file written.</summary>
        NoChange = 0,

        /// <summary>New catalog and hash written under the cache root.</summary>
        Applied = 1,

        /// <summary>Network, HTTP, disk, cancellation, or invalid configuration after a remote update was attempted.</summary>
        Failed = 2,

        /// <summary>No remote catalog configured in settings (<c>HasRemoteCatalog</c> false); no network and no disk writes — not a failure.</summary>
        SkippedNoRemote = 3
    }

    /// <summary>
    /// Structured result for catalog-on-disk hot update (facade). Maps to <see cref="com.igg.hypercontent.shared.ErrorCode"/>
    /// 1006–1009 for disk outcomes; other codes (e.g. settings load) may appear on other paths.
    /// </summary>
    public readonly struct CatalogDiskUpdateResult
    {
        public CatalogDiskUpdateKind Kind { get; }

        /// <summary>
        /// Typical: 1006/1007 (success), 1009 (<see cref="CatalogDiskUpdateKind.SkippedNoRemote"/>), 1008 or other (failure).
        /// </summary>
        public int ErrorCode { get; }

        /// <summary>Human-readable detail; null when not needed (including <see cref="CatalogDiskUpdateKind.SkippedNoRemote"/>).</summary>
        public string Message { get; }

        public CatalogDiskUpdateResult(CatalogDiskUpdateKind pKind, int pErrorCode, string pMessage)
        {
            Kind = pKind;
            ErrorCode = pErrorCode;
            Message = pMessage;
        }
    }
}
