using System;
using UnityEngine;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Non-generic serializable asset reference. Stores a 32-char lowercase hex GUID as the
    /// load key. Used in Inspector fields where the concrete asset type is not known at
    /// declaration time (e.g. a shared config field), or as a base for AssetReference&lt;T&gt;.
    ///
    /// This is a pure data class — it holds only the GUID and exposes no load/release logic.
    /// Loading is done through HyperContent.LoadAsync&lt;T&gt;(AssetReference) overloads so that
    /// the user interacts only with the familiar HyperContent static API.
    ///
    /// The GUID must match an entry produced by the HyperContent build pipeline. At runtime
    /// the Catalog resolves it via its internal _guidToAssetIndex O(1) dictionary.
    ///
    /// See CATALOG_SCHEMA.md §2.8 (Key Lookup Algorithms).
    /// </summary>
    [Serializable]
    public class AssetReference
    {
        [SerializeField] private string _assetGuid;

        /// <summary>The 32-char lowercase hex GUID used as the load key.</summary>
        public string AssetGuid => _assetGuid;

        /// <summary>Returns true when the GUID field is non-empty.</summary>
        public bool RuntimeKeyIsValid => !string.IsNullOrEmpty(_assetGuid);

        public AssetReference() { }

        public AssetReference(string pAssetGuid)
        {
            _assetGuid = pAssetGuid;
        }

        public override string ToString() => _assetGuid ?? "(null)";
    }
}
