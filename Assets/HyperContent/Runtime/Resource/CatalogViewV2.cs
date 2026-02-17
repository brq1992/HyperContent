using System;
using System.Collections.Generic;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Runtime view over CatalogSchemaV2: O(log n) lookup by GUID or Name,
    /// on-demand string resolution via stringTable to control GC.
    /// Owner2: runtime resource management.
    /// </summary>
    public class CatalogViewV2
    {
        private readonly CatalogSchemaV2 _catalog;
        private readonly List<CatalogSchemaV2.AssetRecordEntry> _assetRecords;
        private readonly List<CatalogSchemaV2.NameAliasEntry> _nameAliases;
        private readonly List<CatalogSchemaV2.BundleRecordEntry> _bundleRecords;

        public CatalogViewV2(CatalogSchemaV2 catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _assetRecords = catalog.assetRecords ?? new List<CatalogSchemaV2.AssetRecordEntry>();
            _nameAliases = catalog.nameAliases ?? new List<CatalogSchemaV2.NameAliasEntry>();
            _bundleRecords = catalog.bundleRecords ?? new List<CatalogSchemaV2.BundleRecordEntry>();
            // Build pipeline guarantees assetRecords sorted by guid, nameAliases by nameHash; no sort here to avoid GC.
        }

        /// <summary>On-demand string from stringTable by index; only resolve when needed to control GC.</summary>
        public string GetString(int index)
        {
            if (_catalog.stringTable == null || index < 0 || index >= _catalog.stringTable.Length)
                return null;
            return _catalog.stringTable[index];
        }

        /// <summary>Parse 32-char hex GUID (no hyphens) to Guid; matches build pipeline format.</summary>
        private static bool TryParseGuid32(string key, out Guid guid)
        {
            guid = default;
            if (string.IsNullOrEmpty(key) || key.Length != 32)
                return false;
            for (int i = 0; i < 32; i++)
            {
                char c = key[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            var withDashes = key.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
            return Guid.TryParse(withDashes, out guid);
        }

        /// <summary>True if key is a 32-char hex GUID (no hyphens).</summary>
        public static bool IsGuidKey(string key)
        {
            return TryParseGuid32(key, out _);
        }

        /// <summary>Binary search by GUID. Returns (bundleIndex, assetPathIndex) or (-1, -1).</summary>
        public (int bundleIndex, int assetPathIndex) FindAssetByGuid(string guidKey)
        {
            if (!TryParseGuid32(guidKey, out var target))
                return (-1, -1);

            int lo = 0;
            int hi = _assetRecords.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                var entry = _assetRecords[mid];
                int cmp = entry.guid.CompareTo(target);
                if (cmp == 0)
                    return (entry.bundleIndex, entry.assetPathIndex);
                if (cmp < 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return (-1, -1);
        }

        /// <summary>Binary search by Name via nameHash. Returns (bundleIndex, assetPathIndex) or (-1, -1).</summary>
        public (int bundleIndex, int assetPathIndex) FindAssetByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return (-1, -1);

            string nameHash = NameHashUtil.Compute(name);
            int lo = 0;
            int hi = _nameAliases.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                var entry = _nameAliases[mid];
                int cmp = string.CompareOrdinal(entry.nameHash, nameHash);
                if (cmp == 0)
                {
                    int recordIndex = entry.assetRecordIndex;
                    if (recordIndex < 0 || recordIndex >= _assetRecords.Count)
                        return (-1, -1);
                    var asset = _assetRecords[recordIndex];
                    return (asset.bundleIndex, asset.assetPathIndex);
                }
                if (cmp < 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return (-1, -1);
        }

        /// <summary>Resolve key (GUID or Name) to (bundleIndex, assetPathIndex). Uses GUID if key is 32-char hex, else Name.</summary>
        public (int bundleIndex, int assetPathIndex) FindAsset(string key)
        {
            if (string.IsNullOrEmpty(key))
                return (-1, -1);
            if (IsGuidKey(key))
                return FindAssetByGuid(key);
            return FindAssetByName(key);
        }

        /// <summary>Bundle display name from stringTable; resolve on-demand when loading.</summary>
        public string GetBundleName(int bundleIndex)
        {
            if (bundleIndex < 0 || bundleIndex >= _bundleRecords.Count)
                return null;
            int nameIndex = _bundleRecords[bundleIndex].bundleNameIndex;
            return GetString(nameIndex);
        }

        /// <summary>Expand dependency chain and return bundle indices in load order (dependencies first).</summary>
        public List<int> GetBundleLoadOrder(int bundleIndex)
        {
            var result = new List<int>();
            var visited = new HashSet<int>();
            VisitBundle(bundleIndex, result, visited);
            return result;
        }

        private void VisitBundle(int index, List<int> result, HashSet<int> visited)
        {
            if (index < 0 || index >= _bundleRecords.Count || visited.Contains(index))
                return;
            var entry = _bundleRecords[index];
            if (entry.dependencies != null)
            {
                foreach (int dep in entry.dependencies)
                {
                    if (!visited.Contains(dep))
                        VisitBundle(dep, result, visited);
                }
            }
            result.Add(index);
            visited.Add(index);
        }

        public CatalogSchemaV2 Catalog => _catalog;
    }
}
