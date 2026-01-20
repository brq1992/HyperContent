using System;
using System.Collections.Generic;
using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Handle for async asset loading operations
    /// Provides status tracking, progress reporting, and result access
    /// </summary>
    public class AssetHandle<T> : Handle where T : UnityEngine.Object
    {
        /// <summary>
        /// Loaded asset result (available when Status == Succeeded)
        /// </summary>
        public T Result { get; private set; }
        
        /// <summary>
        /// Current progress (0.0 to 1.0)
        /// </summary>
        public float Progress { get; private set; }
        
        /// <summary>
        /// Total number of operations needed (dependencies + main bundle)
        /// </summary>
        public int TotalOperations { get; private set; }
        
        /// <summary>
        /// Number of completed operations
        /// </summary>
        public int CompletedOperations { get; private set; }
        
        /// <summary>
        /// Asset key being loaded
        /// </summary>
        public string Key { get; private set; }
        
        /// <summary>
        /// List of bundle names that need to be loaded (including dependencies)
        /// </summary>
        public List<string> RequiredBundles { get; private set; }
        
        /// <summary>
        /// Set required bundles list (internal use)
        /// </summary>
        internal void SetRequiredBundles(List<string> bundles)
        {
            RequiredBundles = bundles ?? new List<string>();
        }
        
        /// <summary>
        /// Callbacks registered for completion
        /// </summary>
        private List<Action<T>> _completionCallbacks = new List<Action<T>>();
        
        /// <summary>
        /// Callbacks registered for progress updates
        /// </summary>
        private List<Action<float>> _progressCallbacks = new List<Action<float>>();
        
        /// <summary>
        /// Create a new asset handle
        /// </summary>
        public AssetHandle(string key)
        {
            Key = key;
            Status = HandleStatus.Invalid;
            Progress = 0.0f;
            RequiredBundles = new List<string>();
            TotalOperations = 0;
            CompletedOperations = 0;
        }
        
        /// <summary>
        /// Set the handle to in-progress state
        /// </summary>
        public void BeginOperation(int totalOperations)
        {
            Status = HandleStatus.InProgress;
            TotalOperations = totalOperations;
            CompletedOperations = 0;
            Progress = 0.0f;
        }
        
        /// <summary>
        /// Update progress
        /// </summary>
        public void UpdateProgress(float progress)
        {
            Progress = Mathf.Clamp01(progress);
            NotifyProgress();
        }
        
        /// <summary>
        /// Complete an operation (increment completed count and update progress)
        /// </summary>
        public void CompleteOperation()
        {
            CompletedOperations++;
            if (TotalOperations > 0)
            {
                Progress = (float)CompletedOperations / TotalOperations;
            }
            else
            {
                Progress = 1.0f;
            }
            NotifyProgress();
        }
        
        /// <summary>
        /// Complete the handle with successful result
        /// </summary>
        public void Complete(T asset)
        {
            if (Status == HandleStatus.InProgress)
            {
                Result = asset;
                Status = HandleStatus.Succeeded;
                Progress = 1.0f;
                NotifyCompletion();
            }
        }
        
        /// <summary>
        /// Fail the handle with error code and message
        /// </summary>
        public void Fail(int errorCode, string errorMessage)
        {
            if (Status == HandleStatus.InProgress)
            {
                Status = HandleStatus.Failed;
                ErrorCode = errorCode;
                ErrorMessage = errorMessage;
                NotifyCompletion();
            }
        }
        
        /// <summary>
        /// Register a completion callback
        /// </summary>
        public void RegisterCallback(Action<T> callback)
        {
            if (callback == null) return;
            
            if (IsDone)
            {
                if (IsValid)
                {
                    callback(Result);
                }
                // Failed handles don't call callbacks with null asset
            }
            else
            {
                _completionCallbacks.Add(callback);
            }
        }
        
        /// <summary>
        /// Register a progress callback
        /// </summary>
        public void RegisterProgressCallback(Action<float> callback)
        {
            if (callback == null) return;
            
            _progressCallbacks.Add(callback);
            // Immediately notify current progress
            callback(Progress);
        }
        
        /// <summary>
        /// Notify all completion callbacks
        /// </summary>
        private void NotifyCompletion()
        {
            foreach (var callback in _completionCallbacks)
            {
                try
                {
                    if (IsValid)
                    {
                        callback(Result);
                    }
                    // Failed handles don't call callbacks
                }
                catch (Exception e)
                {
                    Debug.LogError($"[HyperContent] Exception in completion callback: {e.Message}");
                }
            }
            _completionCallbacks.Clear();
        }
        
        /// <summary>
        /// Notify all progress callbacks
        /// </summary>
        private void NotifyProgress()
        {
            foreach (var callback in _progressCallbacks)
            {
                try
                {
                    callback(Progress);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[HyperContent] Exception in progress callback: {e.Message}");
                }
            }
        }
    }
}
