using System;
using System.IO;
using System.Text;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// HCB1（HyperContent Binary v1）反序列化。处理"未压缩"二进制字节，
    /// 调用方负责在传入前完成解压（如 BinaryGzip → GZipStream.Decompress → Read）。
    ///
    /// 与 <c>CatalogBinaryWriter</c> 严格对称，字段顺序见
    /// <see cref="Read"/> 实现注释 / docs/CATALOG_SCHEMA.md。
    ///
    /// 解析失败（magic 缺失、binaryFormatVersion 不符、长度越界等）→ 返回 null，
    /// 调用方按 <see cref="ErrorCode.CATALOG_INVALID_FORMAT"/> 失败。
    ///
    /// 标记 <c>public</c>：HyperContent.Editor 程序集（<c>CatalogGenerator.Deserialize</c>
    /// round-trip 验证 / Editor-only 的诊断工具）需要跨程序集访问；运行时直接使用。
    /// </summary>
    public static class CatalogBinaryReader
    {
        public const string MAGIC = "HCB1";
        public const int BINARY_FORMAT_VERSION = 2;

        /// <summary>
        /// 完整反序列化为 <see cref="CatalogSchema"/>。失败返回 null。
        /// </summary>
        public static CatalogSchema Read(byte[] data)
        {
            if (data == null || data.Length < 12) return null;

            try
            {
                using (var ms = new MemoryStream(data, writable: false))
                using (var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false))
                {
                    if (!ReadAndValidateHeader(br, out int schemaVersion))
                        return null;

                    var schema = new CatalogSchema
                    {
                        schemaVersion = schemaVersion,
                        catalogNameIndex = br.ReadInt32(),
                        catalogHash = ReadString(br),
                        timestamp = br.ReadInt64(),
                        stringTable = ReadStringArray(br),
                    };

                    int assetCount = br.ReadInt32();
                    schema.assetRecords = new System.Collections.Generic.List<CatalogSchema.AssetRecordEntry>(assetCount);
                    for (int i = 0; i < assetCount; i++)
                    {
                        schema.assetRecords.Add(new CatalogSchema.AssetRecordEntry
                        {
                            guid = ReadString(br),
                            bundleIndex = br.ReadInt32(),
                            assetPathIndex = br.ReadInt32(),
                            dependencyBundles = ReadIntList(br),
                        });
                    }

                    int nameCount = br.ReadInt32();
                    schema.nameAliases = new System.Collections.Generic.List<CatalogSchema.NameAliasEntry>(nameCount);
                    for (int i = 0; i < nameCount; i++)
                    {
                        schema.nameAliases.Add(new CatalogSchema.NameAliasEntry
                        {
                            nameStringIndex = br.ReadInt32(),
                            nameHash = ReadString(br),
                            guidIndex = br.ReadInt32(),
                        });
                    }

                    int bundleCount = br.ReadInt32();
                    schema.bundleRecords = new System.Collections.Generic.List<CatalogSchema.BundleRecordEntry>(bundleCount);
                    for (int i = 0; i < bundleCount; i++)
                    {
                        var rec = new CatalogSchema.BundleRecordEntry
                        {
                            bundleNameIndex = br.ReadInt32(),
                            bundleHash = ReadString(br),
                            size = br.ReadInt64(),
                        };
                        int depCount = br.ReadInt32();
                        rec.dependencies = new System.Collections.Generic.List<int>(depCount);
                        for (int d = 0; d < depCount; d++)
                            rec.dependencies.Add(br.ReadInt32());
                        rec.assetCount = br.ReadInt32();
                        rec.contentLocation = br.ReadInt32();
                        rec.bundleTagFlags = br.ReadInt32();
                        rec.remoteRelativePathIndex = br.ReadInt32();
                        schema.bundleRecords.Add(rec);
                    }

                    return schema;
                }
            }
            catch (EndOfStreamException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// 仅读取到 catalogHash 字段就返回，不展开 stringTable / records。
        /// 用于 hot-update hash 比较时只需 hash 不需完整 schema 的场景。
        /// </summary>
        public static string PeekCatalogHash(byte[] data)
        {
            if (data == null || data.Length < 12) return null;

            try
            {
                using (var ms = new MemoryStream(data, writable: false))
                using (var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false))
                {
                    if (!ReadAndValidateHeader(br, out _))
                        return null;

                    br.ReadInt32(); // catalogNameIndex
                    return ReadString(br);
                }
            }
            catch (EndOfStreamException) { return null; }
            catch (IOException) { return null; }
        }

        private static bool ReadAndValidateHeader(BinaryReader br, out int schemaVersion)
        {
            schemaVersion = 0;
            var magicBytes = br.ReadBytes(4);
            if (magicBytes.Length != 4) return false;
            if (magicBytes[0] != (byte)'H' || magicBytes[1] != (byte)'C'
                || magicBytes[2] != (byte)'B' || magicBytes[3] != (byte)'1')
                return false;

            int binaryFormatVersion = br.ReadInt32();
            if (binaryFormatVersion != BINARY_FORMAT_VERSION) return false;

            schemaVersion = br.ReadInt32();
            return true;
        }

        private static string ReadString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            if (len == 0) return string.Empty;
            var bytes = br.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private static string[] ReadStringArray(BinaryReader br)
        {
            int count = br.ReadInt32();
            var arr = new string[count];
            for (int i = 0; i < count; i++)
                arr[i] = ReadString(br);
            return arr;
        }

        /// <summary>
        /// Reads an int list written by <c>CatalogBinaryWriter.WriteIntList</c>. A <c>-1</c> count means null
        /// (no asset-level deps data); <c>0</c> means an empty list.
        /// </summary>
        private static System.Collections.Generic.List<int> ReadIntList(BinaryReader br)
        {
            int count = br.ReadInt32();
            if (count < 0) return null;
            var list = new System.Collections.Generic.List<int>(count);
            for (int i = 0; i < count; i++)
                list.Add(br.ReadInt32());
            return list;
        }
    }
}
