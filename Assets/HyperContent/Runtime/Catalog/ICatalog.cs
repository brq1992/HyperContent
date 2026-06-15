using System;
using System.Collections.Generic;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Unified Catalog interface: translates addresses (GUID or Name) to ResourceLocation trees,
    /// and provides bundle metadata for content management.
    /// Supports dual-key lookup: 32-char hex GUID or human-readable name (asset name + extension).
    /// Owner: Owner0. See ARCHITECTURE.md section 4.2 and CATALOG_SCHEMA.md.
    /// </summary>
    public interface ICatalog
    {
        /// <summary>
        /// Resolve an address to a list of ResourceLocations with full dependency trees.
        /// Address can be a 32-char hex GUID or a human-readable name (e.g. "Widget/AvatarWidget.prefab").
        /// </summary>
        /// <param name="address">Asset GUID (32-char hex) or name (with extension).</param>
        /// <param name="type">Desired asset type; used to set ResourceLocation.ResourceType.</param>
        /// <param name="locations">Output location list. Each entry contains InternalId, ProviderId, and Dependencies.</param>
        /// <returns>True if address was found in catalog.</returns>
        bool TryGetLocations(string address, Type type, out IList<ResourceLocation> locations);

        /// <summary>Catalog version identifier (typically build timestamp).</summary>
        string Version { get; }

        /// <summary>True after successful Initialize, false after Release or failed init.</summary>
        bool IsValid { get; }

        /// <summary>
        /// Initialize catalog from a source path (local file or StreamingAssets-relative path).
        /// Builds all internal lookup structures (Dictionaries). Must succeed before any query.
        /// </summary>
        /// <returns>True if initialization succeeded.</returns>
        bool Initialize(string source);

        /// <summary>Release all internal data structures and mark catalog as invalid.</summary>
        void Release();

        /// <summary>
        /// Get bundle metadata by bundle name. Used by content update pipeline (Owner3).
        /// Returns cached BundleInfo — no per-call allocation.
        /// </summary>
        bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo);

        /// <summary>Get all bundle names in this catalog. Used for content update diffing.</summary>
        IEnumerable<string> GetAllBundleNames();
    }
}
