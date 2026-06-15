using System;
using UnityEngine;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Typed serializable asset reference. Extends AssetReference with compile-time type
    /// information so that LoadAsync overloads can infer T without the caller specifying it.
    ///
    /// Typical usage in a MonoBehaviour or ScriptableObject:
    /// <code>
    ///   [SerializeField] private AssetReference&lt;Texture2D&gt; _iconRef;
    ///
    ///   async void Start()
    ///   {
    ///       var handle = HyperContent.LoadAsync(_iconRef);
    ///       await handle;
    ///       _image.texture = handle.Result;
    ///   }
    ///
    ///   void OnDestroy() => HyperContent.Release(_handle);
    /// </code>
    ///
    /// This is a pure data class — it holds only the GUID and the type constraint.
    /// All load/release logic lives in the HyperContent static facade.
    ///
    /// The GUID must match an entry produced by the HyperContent build pipeline. At runtime
    /// the Catalog resolves it via its internal _guidToAssetIndex O(1) dictionary.
    ///
    /// See CATALOG_SCHEMA.md §2.8 (Key Lookup Algorithms).
    /// </summary>
    [Serializable]
    public class AssetReference<T> : AssetReference where T : UnityEngine.Object
    {
        public AssetReference() { }

        public AssetReference(string pAssetGuid) : base(pAssetGuid) { }
    }
}
