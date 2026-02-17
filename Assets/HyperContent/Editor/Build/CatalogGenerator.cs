using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HyperContent;
using HyperContent.Shared;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Generates single catalog per RESOURCE_LOADING_SYSTEM_SPEC: v2 format (stringTable + AssetRecord + NameAlias + BundleRecord).
    /// Output: {catalogName}.catalog.json
    /// </summary>
    public static class CatalogGenerator
    {
        /// <summary>
        /// Generate catalog.json file from build context
        /// </summary>
        public static bool GenerateCatalog(BuildContext context)
        {
            try
            {
                // Create catalog schema following strict v1 format
                var catalog = new CatalogSchema
                {
                    version = 1, // Strictly schemaVersion=1
                    name = context.Config.catalogName,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    assetToBundle = new CatalogSchema.AssetBundleMapping[0],
                    bundles = new CatalogSchema.BundleInfoData[0]
                };
                
                // Build assetToBundle mapping
                var assetToBundleList = new List<CatalogSchema.AssetBundleMapping>();
                foreach (var kvp in context.KeyToGuid)
                {
                    var assetKey = kvp.Key;
                    var assetGuid = kvp.Value;
                    
                    if (context.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                    {
                        assetToBundleList.Add(new CatalogSchema.AssetBundleMapping
                        {
                            key = assetKey,
                            bundle = bundleName
                        });
                    }
                }
                catalog.assetToBundle = assetToBundleList.ToArray();
                
                // Build bundles array
                var bundlesList = new List<CatalogSchema.BundleInfoData>();
                var outputDir = context.Config.outputDirectory;
                
                foreach (var kvp in context.BundleToAssets)
                {
                    var bundleName = kvp.Key;
                    var assetGuids = kvp.Value;
                    
                    // Get actual bundle file name from mapping (if Unity modified it)
                    string bundleFileName;
                    if (context.ExpectedToActualBundleName != null && 
                        context.ExpectedToActualBundleName.TryGetValue(bundleName, out var actualName))
                    {
                        bundleFileName = actualName;
                    }
                    else
                    {
                        // Fallback: Unity BuildPipeline automatically adds .bundle extension
                        bundleFileName = bundleName;
                        if (!bundleFileName.EndsWith(".bundle"))
                        {
                            bundleFileName = bundleName + ".bundle";
                        }
                    }
                    
                    var bundlePath = Path.Combine(outputDir, bundleFileName);
                    bundlePath = bundlePath.Replace("\\", "/"); // Normalize path separators
                    
                    if (!File.Exists(bundlePath))
                    {
                        // Try to find the actual bundle file (Unity might have modified the name)
                        var actualBundlePath = FindActualBundleFile(outputDir, bundleName);
                        if (actualBundlePath != null)
                        {
                            bundlePath = actualBundlePath;
                            bundleFileName = Path.GetFileName(actualBundlePath);
                        }
                        else
                        {
                            context.Warnings.Add(new BuildWarning(
                                $"Bundle file not found: {bundlePath}. " +
                                $"Expected bundle name: {bundleName}. " +
                                $"Please check if the bundle was built successfully. " +
                                $"Looking for files in: {outputDir}",
                                null, 
                                bundleName
                            ));
                            continue;
                        }
                    }
                    
                    // Get bundle file info
                    var fileInfo = new FileInfo(bundlePath);
                    var size = fileInfo.Length;
                    var hash = CalculateFileHash(bundlePath);
                    
                    // Get dependencies
                    var dependencies = new HashSet<string>();
                    if (context.BundleDependencies.TryGetValue(bundleName, out var bundleDeps))
                    {
                        dependencies.UnionWith(bundleDeps);
                    }
                    
                    // Also check AssetBundle manifest for dependencies
                    var manifestFileName = bundleFileName + ".manifest";
                    var manifestPath = Path.Combine(outputDir, manifestFileName);
                    manifestPath = manifestPath.Replace("\\", "/"); // Normalize path separators
                    if (File.Exists(manifestPath))
                    {
                        var manifestDeps = GetDependenciesFromManifest(manifestPath);
                        dependencies.UnionWith(manifestDeps);
                    }
                    
                    // Get asset keys
                    var assetKeys = new List<string>();
                    foreach (var assetGuid in assetGuids)
                    {
                        if (context.AssetMarkers.TryGetValue(assetGuid, out var marker))
                        {
                            assetKeys.Add(marker.assetKey);
                        }
                    }
                    
                    // Determine location (for v1, we'll use StreamingAssets as default)
                    var location = "StreamingAssets";
                    // Use the actual bundle file name (with .bundle extension) for localPath
                    var localPath = bundleFileName; // Relative to StreamingAssets
                    var remoteUrl = "";
                    
                    var bundleInfo = new CatalogSchema.BundleInfoData
                    {
                        name = bundleName,
                        size = size,
                        hash = hash,
                        version = 1,
                        location = location,
                        remoteUrl = remoteUrl,
                        localPath = localPath,
                        dependencies = dependencies.ToArray(),
                        assetKeys = assetKeys.ToArray()
                    };
                    
                    bundlesList.Add(bundleInfo);
                }
                
                catalog.bundles = bundlesList.ToArray();
                
                // Serialize to JSON
                var json = JsonUtility.ToJson(catalog, true);
                
                // Write to file
                var catalogPath = Path.Combine(outputDir, $"{context.Config.catalogName}.catalog.json");
                File.WriteAllText(catalogPath, json);
                
                Debug.Log($"[HyperContent] Catalog generated: {catalogPath}");
                
                return true;
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Failed to generate catalog: {e.Message}"));
                Debug.LogError($"[HyperContent] Catalog generation failed: {e}");
                return false;
            }
        }
        
        /// <summary>
        /// Calculate SHA256 hash of a file
        /// </summary>
        private static string CalculateFileHash(string filePath)
        {
            try
            {
                using (var sha256 = SHA256.Create())
                {
                    using (var stream = File.OpenRead(filePath))
                    {
                        var hash = sha256.ComputeHash(stream);
                        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to calculate hash for {filePath}: {e.Message}");
                return "";
            }
        }
        
        /// <summary>
        /// Parse dependencies from AssetBundle manifest file
        /// </summary>
        private static HashSet<string> GetDependenciesFromManifest(string manifestPath)
        {
            var dependencies = new HashSet<string>();
            
            try
            {
                var lines = File.ReadAllLines(manifestPath);
                bool inDependencies = false;
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    
                    if (trimmed.StartsWith("Dependencies:"))
                    {
                        inDependencies = true;
                        continue;
                    }
                    
                    if (inDependencies)
                    {
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("-"))
                        {
                            break;
                        }
                        
                        // Extract bundle name from dependency line
                        // Format: "- BundleName"
                        if (trimmed.StartsWith("-"))
                        {
                            var depName = trimmed.Substring(1).Trim();
                            if (!string.IsNullOrEmpty(depName))
                            {
                                dependencies.Add(depName);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to parse manifest {manifestPath}: {e.Message}");
            }
            
            return dependencies;
        }
        
        /// <summary>
        /// Find the actual bundle file in the output directory
        /// Unity might modify bundle names or add extensions
        /// </summary>
        private static string FindActualBundleFile(string outputDir, string bundleName)
        {
            if (!Directory.Exists(outputDir))
            {
                return null;
            }
            
            // Try exact match with .bundle extension
            var exactPath = Path.Combine(outputDir, bundleName + ".bundle").Replace("\\", "/");
            if (File.Exists(exactPath))
            {
                return exactPath;
            }
            
            // Try without extension
            var noExtPath = Path.Combine(outputDir, bundleName).Replace("\\", "/");
            if (File.Exists(noExtPath))
            {
                return noExtPath;
            }
            
            // Try to find by searching for files that start with bundle name
            try
            {
                var files = Directory.GetFiles(outputDir, bundleName + "*", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    // Skip manifest files
                    if (fileName.EndsWith(".manifest"))
                    {
                        continue;
                    }
                    // Skip catalog files
                    if (fileName.EndsWith(".catalog.json"))
                    {
                        continue;
                    }
                    // Found a potential bundle file
                    return file.Replace("\\", "/");
                }
            }
            catch
            {
                // Ignore errors
            }
            
            return null;
        }

        /// <summary>
        /// Generate catalog v2: stringTable + AssetRecord + NameAlias + BundleRecord for O(log n) lookup and hot-update (catalogHash).
        /// </summary>
        public static bool GenerateCatalogV2(BuildContext context)
        {
            try
            {
                var outputDir = context.Config.outputDirectory;
                var stringList = new List<string>();
                var stringToIndex = new Dictionary<string, int>();

                int GetOrAddString(string s)
                {
                    if (string.IsNullOrEmpty(s)) return -1;
                    if (stringToIndex.TryGetValue(s, out var idx)) return idx;
                    idx = stringList.Count;
                    stringList.Add(s);
                    stringToIndex[s] = idx;
                    return idx;
                }

                // Bundle order: same as BundleToAssets iteration
                var bundleNames = context.BundleToAssets.Keys.ToList();
                var bundleNameToIndex = new Dictionary<string, int>();
                for (int i = 0; i < bundleNames.Count; i++)
                {
                    GetOrAddString(bundleNames[i]);
                    bundleNameToIndex[bundleNames[i]] = i;
                }

                // Asset records: Guid, bundleIndex, assetPathIndex. Sort by guid for binary search.
                var assetList = new List<CatalogSchemaV2.AssetRecordEntry>();
                foreach (var kvp in context.KeyToGuid)
                {
                    var assetKey = kvp.Key;
                    var guidStr = kvp.Value;
                    if (!context.AssetToBundle.TryGetValue(guidStr, out var bundleName))
                        continue;
                    if (!context.GuidToPath.TryGetValue(guidStr, out var assetPath))
                        continue;
                    if (!bundleNameToIndex.TryGetValue(bundleName, out var bundleIndex))
                        continue;
                    if (!TryParseUnityGuid(guidStr, out var guid))
                        continue;
                    var assetPathIndex = GetOrAddString(assetPath);
                    assetList.Add(new CatalogSchemaV2.AssetRecordEntry
                    {
                        guid = guid,
                        bundleIndex = bundleIndex,
                        assetPathIndex = assetPathIndex
                    });
                }
                assetList.Sort((a, b) => a.guid.CompareTo(b.guid));

                // Name aliases: nameHash -> assetRecordIndex. Sort by nameHash for binary search.
                var nameAliasList = new List<CatalogSchemaV2.NameAliasEntry>();
                for (int i = 0; i < assetList.Count; i++)
                {
                    var guid = assetList[i].guid;
                    string name = null;
                    foreach (var kvp in context.KeyToGuid)
                    {
                        if (TryParseUnityGuid(kvp.Value, out var g) && g == guid) { name = kvp.Key; break; }
                    }
                    if (string.IsNullOrEmpty(name)) continue;
                    var nameHash = NameHashUtil.Compute(name);
                    if (string.IsNullOrEmpty(nameHash)) continue;
                    nameAliasList.Add(new CatalogSchemaV2.NameAliasEntry
                    {
                        nameHash = nameHash,
                        assetRecordIndex = i
                    });
                }
                nameAliasList.Sort((a, b) => string.CompareOrdinal(a.nameHash, b.nameHash));

                // Bundle records: bundleNameIndex, bundleHash, size, dependencies (indices), assetCount
                var bundleRecordsList = new List<CatalogSchemaV2.BundleRecordEntry>();
                for (int bi = 0; bi < bundleNames.Count; bi++)
                {
                    var bundleName = bundleNames[bi];
                    var bundleNameIndex = GetOrAddString(bundleName);
                    string bundleFileName;
                    if (context.ExpectedToActualBundleName != null && context.ExpectedToActualBundleName.TryGetValue(bundleName, out var actualName))
                        bundleFileName = actualName;
                    else
                        bundleFileName = bundleName.EndsWith(".bundle") ? bundleName : bundleName + ".bundle";
                    var bundlePath = Path.Combine(outputDir, bundleFileName).Replace("\\", "/");
                    if (!File.Exists(bundlePath))
                        bundlePath = FindActualBundleFile(outputDir, bundleName) ?? bundlePath;
                    var size = File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : 0L;
                    var bundleHash = File.Exists(bundlePath) ? CalculateFileHash(bundlePath) : "";
                    var depNames = context.BundleDependencies.TryGetValue(bundleName, out var deps) ? deps : new HashSet<string>();
                    var depIndices = new List<int>(depNames.Where(d => bundleNameToIndex.ContainsKey(d)).Select(d => bundleNameToIndex[d]));
                    var assetCount = context.BundleToAssets.TryGetValue(bundleName, out var guids) ? guids.Count : 0;
                    bundleRecordsList.Add(new CatalogSchemaV2.BundleRecordEntry
                    {
                        bundleNameIndex = bundleNameIndex,
                        bundleHash = bundleHash,
                        size = size,
                        dependencies = depIndices,
                        assetCount = assetCount
                    });
                }

                var catalog = new CatalogSchemaV2
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    stringTable = stringList.ToArray(),
                    assetRecords = assetList,
                    nameAliases = nameAliasList,
                    bundleRecords = bundleRecordsList
                };

                // Compute catalogHash from JSON content (exclude hash field for deterministic hash)
                var json = JsonUtility.ToJson(catalog, true);
                catalog.catalogHash = ComputeHashBytes(json);
                json = JsonUtility.ToJson(catalog, true);

                var catalogPath = Path.Combine(outputDir, $"{context.Config.catalogName}.catalog.json");
                File.WriteAllText(catalogPath, json);
                Debug.Log($"[HyperContent] Catalog generated: {catalogPath} (v2, catalogHash length: {catalog.catalogHash?.Length ?? 0} bytes)");
                return true;
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Failed to generate catalog: {e.Message}"));
                Debug.LogError($"[HyperContent] Catalog generation failed: {e}");
                return false;
            }
        }

        private static string ComputeStringHash(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            var bytes = ComputeHashBytes(content);
            return bytes == null ? "" : BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>SHA256 hash as byte[] for catalogHash (performance and compact storage).</summary>
        private static byte[] ComputeHashBytes(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                return sha.ComputeHash(bytes);
            }
        }

        /// <summary>Parse Unity 32-char hex GUID to System.Guid.</summary>
        private static bool TryParseUnityGuid(string unityGuid, out Guid guid)
        {
            guid = default;
            if (string.IsNullOrEmpty(unityGuid) || unityGuid.Length != 32) return false;
            var withDashes = unityGuid.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
            return Guid.TryParse(withDashes, out guid);
        }
    }
}
