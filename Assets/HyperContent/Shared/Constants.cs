using System;

namespace com.igg.hypercontent.shared
{
    /// <summary>
    /// Error codes for HyperContent system
    /// </summary>
    public static class ErrorCode
    {
        // Catalog errors (1000-1999)
        public const int CATALOG_NOT_FOUND = 1001;
        public const int CATALOG_INVALID_FORMAT = 1002;
        public const int CATALOG_LOAD_FAILED = 1003;
        public const int CATALOG_VERSION_MISMATCH = 1004;
        public const int CATALOG_ENTRY_NOT_FOUND = 1005;

        /// <summary>Catalog disk hot-update: remote matches local; no file written. Maps to CatalogDiskUpdateResult success state "no change".</summary>
        public const int CATALOG_DISK_UPDATE_NO_CHANGE = 1006;

        /// <summary>Catalog disk hot-update: new catalog written to disk. Maps to CatalogDiskUpdateResult success state "applied".</summary>
        public const int CATALOG_DISK_UPDATE_APPLIED = 1007;

        /// <summary>Catalog disk hot-update: failed (network, HTTP, disk, etc.). Maps to CatalogDiskUpdateResult failure; see Message and underlying Transport codes if applicable.</summary>
        public const int CATALOG_DISK_UPDATE_FAILED = 1008;

        /// <summary>Catalog disk hot-update: skipped — no remote catalog paths in settings (<c>HasRemoteCatalog</c> false). Not a failure; no network I/O.</summary>
        public const int CATALOG_DISK_UPDATE_SKIPPED_NO_REMOTE = 1009;

        /// <summary>
        /// Asset-level dependency loading: the asset record carries no asset-level dependency bundle list
        /// (catalog was built without per-asset deps, or the build pipeline dropped them). The load fails
        /// loudly instead of silently falling back to the owning bundle's full bundle-level closure.
        /// </summary>
        public const int CATALOG_ASSET_DEPS_MISSING = 1010;

        // Bundle errors (2000-2999)
        public const int BUNDLE_NOT_FOUND = 2001;
        public const int BUNDLE_LOAD_FAILED = 2002;
        public const int BUNDLE_INVALID_HASH = 2003;
        public const int BUNDLE_SIZE_MISMATCH = 2004;
        public const int BUNDLE_DEPENDENCY_MISSING = 2005;

        // Transport errors (3000-3999)
        public const int TRANSPORT_NETWORK_ERROR = 3001;
        public const int TRANSPORT_TIMEOUT = 3002;
        public const int TRANSPORT_INVALID_URL = 3003;
        public const int TRANSPORT_DOWNLOAD_FAILED = 3004;

        /// <summary>Remote bundle or catalog HTTP was required but CDN/base was not configured (module or settings).</summary>
        public const int TRANSPORT_REMOTE_BASE_NOT_CONFIGURED = 3005;

        // Resource errors (4000-4999)
        public const int RESOURCE_NOT_FOUND = 4001;
        public const int RESOURCE_TYPE_MISMATCH = 4002;
        public const int RESOURCE_LOAD_FAILED = 4003;
        public const int RESOURCE_KEY_INVALID = 4004;

        // Operation errors (5000-5999)
        public const int SYSTEM_NOT_INITIALIZED = 5001;
        public const int SYSTEM_ALREADY_INITIALIZED = 5002;
        public const int SYSTEM_INVALID_STATE = 5003;
        public const int SYSTEM_OUT_OF_MEMORY = 5004;
        public const int OPERATION_TIMED_OUT = 5005;
        public const int OPERATION_DEPENDENCY_FAILED = 5006;

        /// <summary>User declined the pre-download prompt (PromptBeforeDownload load network mode).</summary>
        public const int OPERATION_USER_DECLINED_REMOTE_DOWNLOAD = 5007;

        /// <summary>Query-only load mode: remote bundles are missing locally; load cannot complete without download.</summary>
        public const int OPERATION_LOAD_QUERY_ONLY_INCOMPLETE = 5008;

        /// <summary>Caller cancelled the operation (e.g. batch download <c>CancellationToken</c>); distinct from transport failure.</summary>
        public const int OPERATION_CANCELLED = 5009;

        // Settings errors (6000-6999)
        public const int SETTINGS_NOT_FOUND = 6001;
        public const int SETTINGS_INVALID_FORMAT = 6002;
        public const int SETTINGS_LOAD_FAILED = 6003;

        // Content Update Build errors (7000-7999)
        public const int BUILD_MANIFEST_NOT_FOUND = 7001;
        public const int BUILD_MANIFEST_INVALID_FORMAT = 7002;
        public const int BUILD_MANIFEST_LOAD_FAILED = 7003;
        public const int CHANGE_DETECTION_FAILED = 7004;
        public const int UPDATE_BUNDLE_BUILD_FAILED = 7005;
        public const int MIXED_CATALOG_GENERATION_FAILED = 7006;
    }

    /// <summary>
    /// Log field names for structured logging
    /// </summary>
    public static class LogFields
    {
        public const string OPERATION = "operation";
        public const string KEY = "key";
        public const string BUNDLE_NAME = "bundle_name";
        public const string ERROR_CODE = "error_code";
        public const string ERROR_MESSAGE = "error_message";
        public const string DURATION_MS = "duration_ms";
        public const string SIZE_BYTES = "size_bytes";
        public const string REF_COUNT = "ref_count";
        public const string STATUS = "status";
        public const string LOCATION = "location";
        public const string ADDRESS = "address";
        public const string LOCATION_HASH = "location_hash";
        public const string PROVIDER_ID = "provider_id";
    }

    /// <summary>
    /// Naming conventions
    /// </summary>
    public static class NamingRules
    {
        // Catalog file naming: HyperCatalog.bin (package), HyperCatalog_{buildVersion}.bin (remote)
        public const string CATALOG_FILE_EXTENSION = ".bin";
        
        // Bundle file naming: {bundle_name}.bundle
        public const string BUNDLE_FILE_EXTENSION = ".bundle";

        /// <summary>
        /// Catalog / <see cref="ResourceLocation.InternalId"/> (remote): CDN-relative path after the platform
        /// segment, **without** <see cref="BUNDLE_FILE_EXTENSION"/>. Wrong tooling or old blobs fail fast at load.
        /// </summary>
        /// <param name="pathOrName">Trimmed when non-empty; null/empty skips validation.</param>
        /// <param name="context">Where this value came from (for exception message).</param>
        /// <exception cref="InvalidOperationException">If value ends with <see cref="BUNDLE_FILE_EXTENSION"/>.</exception>
        public static void RequireCatalogBundleRelativePath(string pathOrName, string context)
        {
            if (string.IsNullOrEmpty(pathOrName))
                return;
            string t = pathOrName.Trim();
            if (t.EndsWith(BUNDLE_FILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "HyperContent catalog contract: bundle-relative strings must not include the '" +
                    BUNDLE_FILE_EXTENSION + "' suffix (that is added only for disk/CDN/HTTP). " +
                    $"({context}) Offending value: '{t}'");
        }

        /// <summary>
        /// HTTP path after platform folder: catalog extensionless segment plus <see cref="BUNDLE_FILE_EXTENSION"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">If <paramref name="catalogRelativePath"/> already ends with <see cref="BUNDLE_FILE_EXTENSION"/>.</exception>
        public static string ToTransportRelativeBundlePath(string catalogRelativePath)
        {
            if (string.IsNullOrEmpty(catalogRelativePath))
                return catalogRelativePath;
            catalogRelativePath = catalogRelativePath.Trim();
            if (catalogRelativePath.EndsWith(BUNDLE_FILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "HyperContent: path passed to transport must be catalog form (no '" + BUNDLE_FILE_EXTENSION +
                    "'). Got: '" + catalogRelativePath + "'");
            return catalogRelativePath + BUNDLE_FILE_EXTENSION;
        }
        
        // Hash file naming: {bundle_name}.hash
        public const string HASH_FILE_EXTENSION = ".hash";
        
        // Max key length: 256 characters
        public const int MAX_KEY_LENGTH = 256;
        
        // Max bundle name length: 128 characters
        public const int MAX_BUNDLE_NAME_LENGTH = 128;
    }
}
