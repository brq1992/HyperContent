using System;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Typed operation for asset loading. Holds the loaded result of type T.
    /// See ARCHITECTURE.md section 5.2.
    /// </summary>
    public sealed class AssetOperation<T> : AsyncOperationBase where T : UnityEngine.Object
    {
        public T Result { get; internal set; }

        internal AssetOperation(ResourceLocation location)
        {
            Location = location;
            LocationHash = location.LocationHash;
            Status = OperationStatus.None;
        }

        /// <summary>
        /// Synthetic, already-failed operation for entry-level load failures (invalid address,
        /// catalog miss, declined remote download). Carries no <see cref="AsyncOperationBase.Location"/>
        /// and is never inserted into <see cref="OperationCache"/> (see
        /// <see cref="AsyncOperationBase.IsSynthetic"/>). Created directly in the Failed terminal state
        /// so that the wrapping ContentHandle reports IsDone and fires its Completed callback,
        /// instead of being a default handle that silently never completes.
        /// </summary>
        internal AssetOperation(Exception failure)
        {
            IsSynthetic = true;
            SetFailed(failure);
        }

        internal override bool TrySetResult(UnityEngine.Object result)
        {
            if (result is T typed)
            {
                Result = typed;
                HCLogger.LogVerbose($"[AssetOp<{typeof(T).Name}>] TrySetResult OK [{LogFields.LOCATION_HASH}={LocationHash}] " +
                    $"asset={typed?.name}");
                return true;
            }
            HCLogger.LogWarn($"[AssetOp<{typeof(T).Name}>] TrySetResult type mismatch [{LogFields.LOCATION_HASH}={LocationHash}] " +
                $"got={result?.GetType().Name}");
            return false;
        }

        internal override void Execute(IContentProvider provider, ProvideHandle handle)
        {
            HCLogger.LogVerbose($"[AssetOp<{typeof(T).Name}>] Execute [{LogFields.PROVIDER_ID}={provider.ProviderId}] " +
                $"[{LogFields.LOCATION_HASH}={LocationHash}]");
            provider.Provide(handle);
        }

        internal override void Dispose()
        {
            HCLogger.LogVerbose($"[AssetOp<{typeof(T).Name}>] Dispose [{LogFields.LOCATION_HASH}={LocationHash}] " +
                $"asset={Result?.name}");
            Result = null;
            base.Dispose();
        }
    }
}
