using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using HyperContent.Shared;

namespace HyperContent
{
    /// <summary>
    /// Local catalog implementation for Catalog schema v2 (stringTable + AssetRecord + NameAlias + BundleRecord).
    /// Supports catalogHash/contentHash (bundleHash) for hot-update. Owner3 runtime catalog loader for v2.
    /// </summary>
    public class LocalContentCatalogV2 : IContentCatalog
    {
        private CatalogSchemaV2 _schema;
        private string _baseUrl;
        private Dictionary<string, int> _bundleNameToRecordIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        public bool IsValid => _schema != null;
        public int Version => (int)(_schema?.timestamp ?? 0);

        public bool Initialize(string source)
        {
            try
            {
                string jsonContent;
                if (File.Exists(source))
                {
                    jsonContent = File.ReadAllText(source);
                }
                else
                {
                    string streamingPath = Path.Combine(Application.streamingAssetsPath, source);
                    if (File.Exists(streamingPath))
                        jsonContent = File.ReadAllText(streamingPath);
                    else
                    {
                        Debug.LogError($"[HyperContent] Catalog v2 not found: {source}");
                        return false;
                    }
                }

                _schema = JsonUtility.FromJson<CatalogSchemaV2>(jsonContent);
                if (_schema == null || _schema.bundleRecords == null)
                {
                    Debug.LogError("[HyperContent] Failed to parse catalog v2 JSON");
                    return false;
                }

                _bundleNameToRecordIndex.Clear();
                for (int i = 0; i < _schema.bundleRecords.Count; i++)
                {
                    string name = GetString(_schema.bundleRecords[i].bundleNameIndex);
                    if (!string.IsNullOrEmpty(name))
                        _bundleNameToRecordIndex[name] = i;
                }

                Debug.Log($"[HyperContent] Catalog v2 loaded: {_schema.bundleRecords.Count} bundles");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Catalog v2 initialization failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set base URL for remote bundles (used to build RemoteUrl for update/download).
        /// </summary>
        public void SetBaseUrl(string baseUrl)
        {
            _baseUrl = baseUrl?.TrimEnd('/');
        }

        private string GetString(int index)
        {
            if (_schema?.stringTable == null || index < 0 || index >= _schema.stringTable.Length)
                return null;
            return _schema.stringTable[index];
        }

        public bool TryGetBundleName(string assetKey, out string bundleName)
        {
            bundleName = null;
            if (_schema == null) return false;
            if (string.IsNullOrEmpty(assetKey)) return false;

            int bundleIndex = ResolveAssetKeyToBundleIndex(assetKey);
            if (bundleIndex < 0) return false;

            var rec = _schema.bundleRecords[bundleIndex];
            bundleName = GetString(rec.bundleNameIndex);
            return !string.IsNullOrEmpty(bundleName);
        }

        private int ResolveAssetKeyToBundleIndex(string assetKey)
        {
            if (_schema.assetRecords == null || _schema.bundleRecords == null) return -1;
            if (assetKey.Length == 32 && IsHex(assetKey))
            {
                var guid = ParseGuid32(assetKey);
                for (int i = 0; i < _schema.assetRecords.Count; i++)
                {
                    if (_schema.assetRecords[i].guid == guid)
                        return _schema.assetRecords[i].bundleIndex;
                }
            }
            if (_schema.nameAliases != null)
            {
                string nameHash = NameHashUtil.Compute(assetKey);
                for (int i = 0; i < _schema.nameAliases.Count; i++)
                {
                    if (StringComparer.Ordinal.Equals(_schema.nameAliases[i].nameHash, nameHash))
                    {
                        int assetIdx = _schema.nameAliases[i].assetRecordIndex;
                        if (assetIdx >= 0 && assetIdx < _schema.assetRecords.Count)
                            return _schema.assetRecords[assetIdx].bundleIndex;
                    }
                }
            }
            return -1;
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        private static Guid ParseGuid32(string unityGuid)
        {
            if (string.IsNullOrEmpty(unityGuid) || unityGuid.Length != 32) return default;
            string withDashes = unityGuid.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
            return Guid.TryParse(withDashes, out var g) ? g : default;
        }

        public bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo)
        {
            bundleInfo = null;
            if (_schema == null || string.IsNullOrEmpty(bundleName)) return false;
            if (!_bundleNameToRecordIndex.TryGetValue(bundleName, out int idx)) return false;

            var rec = _schema.bundleRecords[idx];
            string name = GetString(rec.bundleNameIndex);
            string[] deps = null;
            if (rec.dependencies != null && rec.dependencies.Count > 0)
            {
                deps = new string[rec.dependencies.Count];
                for (int i = 0; i < rec.dependencies.Count; i++)
                {
                    int di = rec.dependencies[i];
                    if (di >= 0 && di < _schema.bundleRecords.Count)
                        deps[i] = GetString(_schema.bundleRecords[di].bundleNameIndex);
                }
            }

            bundleInfo = new BundleInfo
            {
                Name = name,
                Size = rec.size,
                Hash = rec.bundleHash,
                Version = 0,
                Location = ContentLocation.Remote,
                RemoteUrl = string.IsNullOrEmpty(_baseUrl) ? null : _baseUrl + "/" + name,
                Dependencies = deps ?? Array.Empty<string>(),
                AssetKeys = Array.Empty<string>()
            };
            return true;
        }

        public IEnumerable<string> GetAllAssetKeys()
        {
            if (_schema?.assetRecords == null) yield break;
            foreach (var ar in _schema.assetRecords)
                yield return ar.guid.ToString("N");
        }

        public IEnumerable<string> GetAllBundleNames()
        {
            return _bundleNameToRecordIndex?.Keys ?? (IEnumerable<string>)Array.Empty<string>();
        }

        public void Release()
        {
            _schema = null;
            _bundleNameToRecordIndex?.Clear();
        }

        /// <summary>
        /// Parse v2 catalog JSON and return catalogHash for hot-update comparison.
        /// Returns null if JSON is not v2 or catalogHash is missing.
        /// </summary>
        public static byte[] GetCatalogHashFromJson(string catalogJson)
        {
            if (string.IsNullOrEmpty(catalogJson)) return null;
            try
            {
                var schema = JsonUtility.FromJson<CatalogSchemaV2>(catalogJson);
                return schema?.catalogHash;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Compare two catalog hashes; returns true if both are null or equal.
        /// </summary>
        public static bool CatalogHashEquals(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
