using UnityEngine.SceneManagement;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Operation for async scene loading.
    /// See ARCHITECTURE.md section 5.2.
    /// </summary>
    internal sealed class SceneOperation : AsyncOperationBase
    {
        internal Scene ResultScene;
        internal LoadSceneMode Mode;

        internal SceneOperation(ResourceLocation location, LoadSceneMode mode = LoadSceneMode.Single)
        {
            Location = location;
            LocationHash = location.LocationHash;
            Mode = mode;
            Status = OperationStatus.None;
        }

        /// <summary>
        /// Synthetic, already-failed scene operation for entry-level failures (invalid address,
        /// catalog miss, declined remote download). Never cache-managed (see
        /// <see cref="AsyncOperationBase.IsSynthetic"/>); created in the Failed terminal state so the
        /// wrapping ContentHandle reports IsDone and fires its Completed callback instead of being a
        /// default handle that silently never completes.
        /// </summary>
        internal SceneOperation(System.Exception failure)
        {
            IsSynthetic = true;
            SetFailed(failure);
        }

        internal override void Execute(IContentProvider provider, ProvideHandle handle)
        {
            HCLogger.LogVerbose($"[SceneOp] Execute [{LogFields.PROVIDER_ID}={provider.ProviderId}] " +
                $"scene={Location?.InternalId} mode={Mode}");
            provider.Provide(handle);
        }

        internal override void Dispose()
        {
            HCLogger.LogVerbose($"[SceneOp] Dispose [{LogFields.LOCATION_HASH}={LocationHash}] " +
                $"scene={Location?.InternalId} valid={ResultScene.IsValid()} loaded={ResultScene.isLoaded}");
            if (ResultScene.IsValid() && ResultScene.isLoaded)
            {
                HCLogger.LogInfo($"[SceneOp] Unloading scene: {ResultScene.name}");
                SceneManager.UnloadSceneAsync(ResultScene);
            }
            base.Dispose();
        }
    }
}
