namespace HyperContent.Shared
{
    /// <summary>
    /// Represents the location where content can be found
    /// </summary>
    public enum ContentLocation
    {
        /// <summary>
        /// Content is not available
        /// </summary>
        None = 0,
        
        /// <summary>
        /// Content is in local cache
        /// </summary>
        Local = 1,
        
        /// <summary>
        /// Content needs to be downloaded from remote
        /// </summary>
        Remote = 2,
        
        /// <summary>
        /// Content is in streaming assets
        /// </summary>
        StreamingAssets = 3,
        
        /// <summary>
        /// Content is in resources folder (legacy)
        /// </summary>
        Resources = 4
    }
}
