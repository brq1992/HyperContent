using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Handle for async instantiate operations
    /// </summary>
    public class InstanceHandle : Handle
    {
        /// <summary>
        /// Instantiated GameObject result
        /// </summary>
        public GameObject Result { get; private set; }
        
        /// <summary>
        /// Current progress (0.0 to 1.0)
        /// </summary>
        public float Progress { get; private set; }
        
        /// <summary>
        /// Asset key being instantiated
        /// </summary>
        public string Key { get; private set; }
        
        /// <summary>
        /// Internal asset handle used for loading
        /// </summary>
        public AssetHandle<GameObject> AssetHandle { get; private set; }
        
        /// <summary>
        /// Create a new instance handle
        /// </summary>
        public InstanceHandle(string key, AssetHandle<GameObject> assetHandle)
        {
            Key = key;
            AssetHandle = assetHandle;
            Status = HandleStatus.Invalid;
            Progress = 0.0f;
        }
        
        /// <summary>
        /// Update progress from underlying asset handle
        /// </summary>
        public void UpdateProgress(float progress)
        {
            Progress = progress;
        }
        
        /// <summary>
        /// Complete the handle with instantiated GameObject
        /// </summary>
        public void Complete(GameObject instance)
        {
            if (Status == HandleStatus.Invalid || Status == HandleStatus.InProgress)
            {
                Result = instance;
                Status = HandleStatus.Succeeded;
                Progress = 1.0f;
            }
        }
        
        /// <summary>
        /// Fail the handle with error code and message
        /// </summary>
        public void Fail(int errorCode, string errorMessage)
        {
            if (Status == HandleStatus.Invalid || Status == HandleStatus.InProgress)
            {
                Status = HandleStatus.Failed;
                ErrorCode = errorCode;
                ErrorMessage = errorMessage;
            }
        }
    }
}
