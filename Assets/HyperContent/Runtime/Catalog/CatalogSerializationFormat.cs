namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Catalog 序列化格式（写入端按 <see cref="BuildConfig"/>.catalogFormat 选择；
    /// 读取端按 <see cref="RuntimeSettings"/>.catalogFormat 选择）。
    ///
    /// 设计原则：写入与读取严格对称，**不做格式探测、不做 fallback**。
    /// 读到的 catalog 文件类型必须与 settings.json 中的 catalogFormat 一致，否则按
    /// <c>CATALOG_INVALID_FORMAT</c> 失败（settings.json 出包后不可热更，确保强一致）。
    ///
    /// 扩展点：未来加入 LZ4 / Zstd 等只需追加新枚举值 + 在两端 dispatcher 增加 case，
    /// <see cref="CatalogBinaryReader"/> / Editor 侧 <c>CatalogBinaryWriter</c> 主体无需变动。
    /// </summary>
    public enum CatalogSerializationFormat
    {
        /// <summary>JsonUtility 文本格式，肉眼可读、便于排查。</summary>
        Json = 0,

        /// <summary>手写紧凑二进制（HCB1），无压缩，解析最快。</summary>
        Binary = 1,

        /// <summary>HCB1 之上再过 GZipStream 压缩，文件最小（hot-update 流量友好）。</summary>
        BinaryGzip = 2,

        // Future:
        // BinaryLz4 = 3,
        // BinaryZstd = 4,
    }
}
