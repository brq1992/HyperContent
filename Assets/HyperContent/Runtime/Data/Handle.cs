namespace HyperContent
{
    /// <summary>
    /// Handle status for async operations
    /// </summary>
    public enum HandleStatus
    {
        /// <summary>
        /// Handle is invalid or not initialized
        /// </summary>
        Invalid = 0,
        
        /// <summary>
        /// Operation is in progress
        /// </summary>
        InProgress = 1,
        
        /// <summary>
        /// Operation completed successfully
        /// </summary>
        Succeeded = 2,
        
        /// <summary>
        /// Operation failed
        /// </summary>
        Failed = 3
    }

    /// <summary>
    /// Base handle for async operations
    /// </summary>
    public abstract class Handle
    {
        /// <summary>
        /// Current status of the handle
        /// </summary>
        public HandleStatus Status { get; protected set; } = HandleStatus.Invalid;
        
        /// <summary>
        /// Error code if operation failed
        /// </summary>
        public int ErrorCode { get; protected set; }
        
        /// <summary>
        /// Error message if operation failed
        /// </summary>
        public string ErrorMessage { get; protected set; }
        
        /// <summary>
        /// Whether the operation is done (succeeded or failed)
        /// </summary>
        public bool IsDone => Status == HandleStatus.Succeeded || Status == HandleStatus.Failed;
        
        /// <summary>
        /// Whether the operation completed successfully
        /// </summary>
        public bool IsValid => Status == HandleStatus.Succeeded;
        
        /// <summary>
        /// Reference count for resource management
        /// </summary>
        public int RefCount { get; private set; }
        
        /// <summary>
        /// Increment reference count
        /// </summary>
        public void AddRef()
        {
            RefCount++;
        }
        
        /// <summary>
        /// Decrement reference count
        /// </summary>
        public void Release()
        {
            if (RefCount > 0)
            {
                RefCount--;
            }
        }
        
        /// <summary>
        /// Check if handle can be released (ref count is 0)
        /// </summary>
        public bool CanRelease => RefCount == 0;
    }
}
