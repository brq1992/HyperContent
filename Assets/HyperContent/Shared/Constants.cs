namespace HyperContent.Shared
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

        // Resource errors (4000-4999)
        public const int RESOURCE_NOT_FOUND = 4001;
        public const int RESOURCE_TYPE_MISMATCH = 4002;
        public const int RESOURCE_LOAD_FAILED = 4003;
        public const int RESOURCE_KEY_INVALID = 4004;

        // System errors (5000-5999)
        public const int SYSTEM_NOT_INITIALIZED = 5001;
        public const int SYSTEM_ALREADY_INITIALIZED = 5002;
        public const int SYSTEM_INVALID_STATE = 5003;
        public const int SYSTEM_OUT_OF_MEMORY = 5004;
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
    }

    /// <summary>
    /// Naming conventions
    /// </summary>
    public static class NamingRules
    {
        // Catalog file naming: {catalog_name}.catalog.json
        public const string CATALOG_FILE_EXTENSION = ".catalog.json";
        
        // Bundle file naming: {bundle_name}.bundle
        public const string BUNDLE_FILE_EXTENSION = ".bundle";
        
        // Hash file naming: {bundle_name}.hash
        public const string HASH_FILE_EXTENSION = ".hash";
        
        // Max key length: 256 characters
        public const int MAX_KEY_LENGTH = 256;
        
        // Max bundle name length: 128 characters
        public const int MAX_BUNDLE_NAME_LENGTH = 128;
    }
}
