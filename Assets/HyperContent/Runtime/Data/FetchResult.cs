namespace HyperContent
{
    /// <summary>
    /// Result of a fetch operation
    /// </summary>
    public class FetchResult
    {
        /// <summary>
        /// Whether the fetch was successful
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Error code if fetch failed
        /// </summary>
        public int ErrorCode { get; set; }
        
        /// <summary>
        /// Error message if fetch failed
        /// </summary>
        public string ErrorMessage { get; set; }
        
        /// <summary>
        /// Size of the fetched data in bytes
        /// </summary>
        public long SizeBytes { get; set; }
        
        /// <summary>
        /// Duration of the fetch operation in milliseconds
        /// </summary>
        public long DurationMs { get; set; }
        
        /// <summary>
        /// Create a successful result
        /// </summary>
        public static FetchResult CreateSuccess(long sizeBytes, long durationMs)
        {
            return new FetchResult
            {
                Success = true,
                SizeBytes = sizeBytes,
                DurationMs = durationMs
            };
        }
        
        /// <summary>
        /// Create a failed result
        /// </summary>
        public static FetchResult CreateFailure(int errorCode, string errorMessage, long durationMs = 0)
        {
            return new FetchResult
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                DurationMs = durationMs
            };
        }
    }
}
