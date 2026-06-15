using System;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Bridge between Provider and Operation layers.
    /// Providers call Complete/Fail to report results; GetDependencyResult
    /// retrieves loaded dependency data (e.g. AssetBundle from BundleFileProvider).
    /// See ARCHITECTURE.md section 6.2.
    /// </summary>
    public sealed class ProvideHandle
    {
        private readonly AsyncOperationBase _op;
        private readonly ResourceManager _resourceManager;

        internal ProvideHandle(AsyncOperationBase op, ResourceManager resourceManager)
        {
            _op = op;
            _resourceManager = resourceManager;
        }

        public ResourceLocation Location => _op.Location;

        public void Complete<T>(T result) where T : UnityEngine.Object
        {
            HCLogger.LogVerbose($"[ProvideHandle] Complete<{typeof(T).Name}> " +
                $"[{LogFields.LOCATION_HASH}={_op.LocationHash}] asset={result?.name}");
            if (_op is AssetOperation<T> assetOp)
                assetOp.Result = result;
            else
                _op.TrySetResult(result);
            _op.SetSucceeded();
        }

        public void Complete()
        {
            HCLogger.LogVerbose($"[ProvideHandle] Complete(void) [{LogFields.LOCATION_HASH}={_op.LocationHash}]");
            _op.SetSucceeded();
        }

        public void CompleteAsBundle(AssetBundle bundle)
        {
            HCLogger.LogVerbose($"[ProvideHandle] CompleteAsBundle [{LogFields.LOCATION_HASH}={_op.LocationHash}] " +
                $"bundle={bundle?.name}");
            if (_op is AssetOperation<UnityEngine.Object> objOp)
            {
                objOp.Result = bundle;
            }
            _op.SetSucceeded();
        }

        public void Fail(Exception exception)
        {
            HCLogger.LogWarn($"[ProvideHandle] Fail [{LogFields.LOCATION_HASH}={_op.LocationHash}] " +
                $"[{LogFields.ADDRESS}={_op.Location?.Address}] error={exception?.Message}");
            _op.SetFailed(exception);
        }

        public void UpdateProgress(float progress)
        {
            _op.ProgressValue = Mathf.Clamp01(progress);
        }

        /// <summary>
        /// Get the result of a dependency operation by index.
        /// For BundleAssetExtractor, index 0 is typically the primary bundle.
        /// </summary>
        public TDep GetDependencyResult<TDep>(int depIndex) where TDep : class
        {
            if (depIndex < 0 || depIndex >= _op.DependencyCount)
                return null;

            var dep = _op.Dependencies[depIndex];
            if (dep is AssetOperation<UnityEngine.Object> objDep && objDep.Result is TDep cast)
                return cast;

            return null;
        }

        /// <summary>
        /// Get the loaded AssetBundle from the specified dependency.
        /// Provider-specific convenience for bundle asset extraction.
        /// </summary>
        internal AssetBundle GetDependencyBundle(int depIndex)
        {
            if (depIndex < 0 || depIndex >= _op.DependencyCount)
                return null;

            var dep = _op.Dependencies[depIndex];
            if (dep.Location?.Data is AssetBundle bundle)
                return bundle;

            return null;
        }

        internal AsyncOperationBase Operation => _op;
    }
}
