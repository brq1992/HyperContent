# 修复记录

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
