using System;
using System.Collections.Generic;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Provider registration by ProviderId. Each built-in or custom provider
    /// registers itself here; ResourceManager looks up providers via this registry.
    /// See ARCHITECTURE.md section 6.3.
    /// </summary>
    internal sealed class ProviderRegistry
    {
        private readonly Dictionary<string, IContentProvider> _map = new Dictionary<string, IContentProvider>();

        public void Register(IContentProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            _map[provider.ProviderId] = provider;
            HCLogger.LogVerbose($"[ProviderRegistry] Registered [{LogFields.PROVIDER_ID}={provider.ProviderId}]");
        }

        public IContentProvider Get(string providerId)
        {
            if (_map.TryGetValue(providerId, out var p))
                return p;
            HCLogger.LogError($"[ProviderRegistry] Provider not found [{LogFields.PROVIDER_ID}={providerId}]");
            throw new InvalidOperationException($"[HyperContent] Provider not found: {providerId}");
        }

        public bool TryGet(string providerId, out IContentProvider provider)
        {
            return _map.TryGetValue(providerId, out provider);
        }

        public void Clear()
        {
            HCLogger.LogVerbose($"[ProviderRegistry] Clear — removing {_map.Count} providers");
            foreach (var kvp in _map)
            {
                if (kvp.Value is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception e)
                    {
                        HCLogger.LogWarn($"[ProviderRegistry] Dispose failed for {kvp.Key}: {e.Message}");
                    }
                }
            }
            _map.Clear();
        }
    }
}
