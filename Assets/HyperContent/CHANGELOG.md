# 变更记录

## 2025-01-29 - 文档整理；移除 Catalog v1

- 重新整理 HyperContent 目录下文档，去除冗余。
- **移除 Catalog v1**：仅保留 Catalog v2（schemaVersion=2）作为当前 Schema；SPECIFICATION 中删除 v1 Schema 章节，Catalog 结构以 RESOURCE_LOADING_SYSTEM_SPEC 与 CatalogSchemaV2.cs 为准。
- **README.md**: 精简为入口索引，仅引用当前 Catalog；文档表与快速开始更新。
- **SPECIFICATION.md**: 删除「Catalog Schema (v1)」整节；目录结构更新为 CatalogSchemaV2、IAssetLoader、LocalContentCatalogV2；章节重新编号。
- **OWNERS.md** / **OWNER0_GUIDE.md**: Schema 仅引用 CatalogSchemaV2.cs；接口变更控制中 Catalog Schema 仅指 v2。
- **ARCHITECTURE.md**: 此前已补充 IAssetLoader；内存/错误处理引用 SPECIFICATION。

---

## 2024-01-XX - 修复编译错误

### 问题
1. Unity JsonUtility不支持Dictionary类型
2. Runtime程序集未引用Shared程序集

### 修复
1. 修改`CatalogSchema.cs`：将Dictionary改为数组结构
   - `assetToBundle`: Dictionary → AssetBundleMapping[]
   - `bundles`: Dictionary → BundleInfoData[]
   
2. 修改`HyperContent.Runtime.asmdef`：添加对Shared程序集的引用

3. 更新示例Catalog JSON：使用数组格式替代字典格式

### 注意事项
- 如果Unity仍显示编译错误，请尝试：
  1. 在Unity中右键点击`Assets/HyperContent`文件夹 → Reimport
  2. 或者关闭并重新打开Unity编辑器
  3. 或者删除`Library/ScriptAssemblies`文件夹（Unity会自动重建）
