using System.Collections.Generic;
using System.IO;
using System.Text;
using com.igg.hypercontent.runtime;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// HCB1（HyperContent Binary v1）序列化。Editor-only。
    ///
    /// 写"未压缩"二进制字节；GZip 压缩由 <see cref="CatalogGenerator.Serialize"/>
    /// 的 dispatcher 层负责（保持 Reader/Writer 与压缩算法解耦，未来可叠加 LZ4 / Zstd 等）。
    ///
    /// 字段顺序与 <see cref="CatalogBinaryReader.Read"/> 严格对称，详见
    /// docs/CATALOG_SCHEMA.md "Serialization Formats" 章节。
    /// </summary>
    internal static class CatalogBinaryWriter
    {
        internal const string MAGIC = "HCB1";
        internal const int BINARY_FORMAT_VERSION = 2;

        /// <summary>
        /// 序列化 <see cref="CatalogSchema"/> 为紧凑二进制字节流（不压缩）。
        /// </summary>
        public static byte[] Write(CatalogSchema pCatalog)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false))
            {
                bw.Write((byte)'H'); bw.Write((byte)'C'); bw.Write((byte)'B'); bw.Write((byte)'1');
                bw.Write(BINARY_FORMAT_VERSION);
                bw.Write(pCatalog.schemaVersion);
                bw.Write(pCatalog.catalogNameIndex);
                WriteString(bw, pCatalog.catalogHash);
                bw.Write(pCatalog.timestamp);

                WriteStringArray(bw, pCatalog.stringTable);

                int assetCount = pCatalog.assetRecords?.Count ?? 0;
                bw.Write(assetCount);
                if (pCatalog.assetRecords != null)
                {
                    for (int i = 0; i < assetCount; i++)
                    {
                        var rec = pCatalog.assetRecords[i];
                        WriteString(bw, rec.guid);
                        bw.Write(rec.bundleIndex);
                        bw.Write(rec.assetPathIndex);
                        WriteIntList(bw, rec.dependencyBundles);
                    }
                }

                int nameCount = pCatalog.nameAliases?.Count ?? 0;
                bw.Write(nameCount);
                if (pCatalog.nameAliases != null)
                {
                    for (int i = 0; i < nameCount; i++)
                    {
                        var alias = pCatalog.nameAliases[i];
                        bw.Write(alias.nameStringIndex);
                        WriteString(bw, alias.nameHash);
                        bw.Write(alias.guidIndex);
                    }
                }

                int bundleCount = pCatalog.bundleRecords?.Count ?? 0;
                bw.Write(bundleCount);
                if (pCatalog.bundleRecords != null)
                {
                    for (int i = 0; i < bundleCount; i++)
                    {
                        var rec = pCatalog.bundleRecords[i];
                        bw.Write(rec.bundleNameIndex);
                        WriteString(bw, rec.bundleHash);
                        bw.Write(rec.size);

                        int depCount = rec.dependencies?.Count ?? 0;
                        bw.Write(depCount);
                        if (rec.dependencies != null)
                        {
                            for (int d = 0; d < depCount; d++)
                                bw.Write(rec.dependencies[d]);
                        }

                        bw.Write(rec.assetCount);
                        bw.Write(rec.contentLocation);
                        bw.Write(rec.bundleTagFlags);
                        bw.Write(rec.remoteRelativePathIndex);
                    }
                }

                bw.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteString(BinaryWriter bw, string s)
        {
            if (s == null)
            {
                bw.Write(-1);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(s);
            bw.Write(bytes.Length);
            if (bytes.Length > 0)
                bw.Write(bytes);
        }

        private static void WriteStringArray(BinaryWriter bw, IList<string> list)
        {
            int count = list?.Count ?? 0;
            bw.Write(count);
            if (list == null) return;
            for (int i = 0; i < count; i++)
                WriteString(bw, list[i]);
        }

        /// <summary>
        /// Writes an int list with a <c>-1</c> sentinel for null (distinguishes "no asset-level deps data"
        /// from "empty list"). Reader mirrors this in <c>CatalogBinaryReader.ReadIntList</c>.
        /// </summary>
        private static void WriteIntList(BinaryWriter bw, IList<int> list)
        {
            if (list == null)
            {
                bw.Write(-1);
                return;
            }
            bw.Write(list.Count);
            for (int i = 0; i < list.Count; i++)
                bw.Write(list[i]);
        }
    }
}
