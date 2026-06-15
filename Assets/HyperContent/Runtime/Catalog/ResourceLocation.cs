using System;
using System.Collections.Generic;

namespace com.igg.hypercontent.runtime
{
    // Owner: Owner0 (provisional stub created by Owner2 for compilation)
    // See ARCHITECTURE.md section 4.1 for spec.
    // Owner0 should review and take ownership of this file.
    public sealed class ResourceLocation
    {
        public string Address { get; }
        public string InternalId { get; }
        public string ProviderId { get; }
        public Type ResourceType { get; }
        public IReadOnlyList<ResourceLocation> Dependencies { get; }
        public object Data { get; }
        public int LocationHash { get; }

        public ResourceLocation(
            string address,
            string internalId,
            string providerId,
            Type resourceType,
            IReadOnlyList<ResourceLocation> dependencies = null,
            object data = null,
            int locationHash = 0)
        {
            Address = address;
            InternalId = internalId;
            ProviderId = providerId;
            ResourceType = resourceType;
            Dependencies = dependencies ?? Array.Empty<ResourceLocation>();
            Data = data;
            LocationHash = locationHash != 0 ? locationHash : ComputeHash(address, internalId, providerId);
        }

        // Address is included in the hash so two distinct addresses that happen to share
        // the same (internalId, providerId) — e.g. two prefabs named "Foo.prefab" living
        // in different bundles — do NOT collide in OperationCache. Without Address in the
        // mix, the second LoadAsync would receive the first address's already-cached
        // Operation (with its primary bundle pointing at the WRONG bundle) and silently
        // hand back the wrong asset.
        private static int ComputeHash(string address, string internalId, string providerId)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (address?.GetHashCode() ?? 0);
                hash = hash * 31 + (internalId?.GetHashCode() ?? 0);
                hash = hash * 31 + (providerId?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}
