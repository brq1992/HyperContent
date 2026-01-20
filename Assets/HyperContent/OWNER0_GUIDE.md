# Owner0 开发指南

## 已完成工作

### ✅ 1. 程序集结构
- `HyperContent.Runtime`: 运行期核心代码
- `HyperContent.Editor`: 编辑器工具（骨架，v1暂不实现）
- `HyperContent.Shared`: 共享定义

### ✅ 2. 核心接口定义
所有接口位于 `Runtime/Core/`:
- `IContentCatalog`: Catalog管理接口
- `IBundleStore`: Bundle存储接口
- `IBundleTransport`: Bundle传输接口
- `IBundleLoader`: Bundle加载接口
- `IResourceProvider`: 资源提供接口（对外API）

### ✅ 3. 运行期数据结构
- `ContentLocation`: 内容位置枚举（Shared）
- `BundleInfo`: Bundle信息（Runtime/Data）
- `Handle`: 异步操作句柄（Runtime/Data）
- `FetchResult`: 获取结果（Runtime/Data）

### ✅ 4. 规范文档
- **Catalog Schema**: 定义在 `CatalogSchema.cs` 和 `SPECIFICATION.md`
- **命名规则**: 定义在 `Constants.cs` (NamingRules)
- **错误码**: 定义在 `Constants.cs` (ErrorCode)
- **日志字段**: 定义在 `Constants.cs` (LogFields)
- **RefCount规则**: 定义在 `SPECIFICATION.md`

### ✅ 5. 最小可用链路POC
- `LocalContentCatalog`: 本地文件Catalog实现
- `LocalBundleStore`: 本地文件Bundle存储
- `UnityBundleLoader`: Unity AssetBundle加载器
- `ResourceProvider`: 资源提供实现
- `HyperContentManager`: 系统入口点
- `HyperContentTest`: 测试脚本示例

## 接口变更控制

**重要**: 以下内容的任何修改必须经过Owner0 Code Review:

1. **Catalog Schema** (`CatalogSchema.cs`)
   - 字段增减
   - 字段类型变更
   - 格式变更

2. **核心接口** (`Runtime/Core/*.cs`)
   - 方法签名
   - 方法增减
   - 接口继承关系

3. **运行期数据结构** (`Runtime/Data/*.cs`, `Shared/*.cs`)
   - 类/结构体字段
   - 枚举值
   - 序列化格式

4. **错误码** (`Shared/Constants.cs` - ErrorCode)
   - 错误码值
   - 错误码范围分配

5. **日志字段** (`Shared/Constants.cs` - LogFields)
   - 字段名称
   - 字段语义

6. **RefCount规则** (文档和实现)
   - 引用计数策略
   - 卸载时机

## 开发流程

### 对于Owner1/2/3

1. **实现接口**: 实现 `IContentCatalog`, `IBundleStore`, `IBundleTransport` 等接口
2. **遵循规范**: 严格按照已定义的Schema、错误码、日志字段
3. **接口变更**: 如需修改接口，先提交给Owner0 Review
4. **测试**: 使用 `HyperContentTest` 作为参考实现测试

### 对于Owner0

1. **Code Review**: 审查所有接口和Schema变更
2. **规范维护**: 更新规范文档，确保一致性
3. **架构演进**: 根据需求演进架构，但保持向后兼容

## 测试POC

### 前置条件
1. 创建测试Bundle（使用Unity AssetBundle构建）
2. 将Bundle放在 `StreamingAssets/` 目录
3. 创建Catalog JSON文件（参考 `StreamingAssets/test.catalog.json`）

### 使用步骤
1. 在Scene中添加 `HyperContentManager` GameObject
2. 添加 `HyperContentTest` 组件
3. 配置Catalog文件名和测试资产键
4. 运行Scene，查看Console输出

## 后续开发建议

### Owner1: Build Pipeline
- 实现Catalog生成工具
- 扫描项目中的资产
- 生成Bundle并计算哈希
- 输出Catalog JSON文件

### Owner2: Downloader/Cache
- 实现 `IBundleTransport` 完整版本
- HTTP/HTTPS下载
- 断点续传
- 下载队列管理

### Owner3: Editor工具
- Unity Editor面板
- Catalog可视化
- Bundle依赖图
- 调试工具

## 注意事项

1. **不要修改接口**: 除非经过Owner0 Review
2. **遵循命名规则**: 使用已定义的命名规范
3. **错误处理**: 使用统一的错误码
4. **日志格式**: 使用结构化日志字段
5. **RefCount**: 正确管理引用计数，避免内存泄漏

## 文件清单

### 核心文件（Owner0负责）
- `Runtime/Core/*.cs` - 所有接口定义
- `Runtime/Data/*.cs` - 数据结构
- `Shared/*.cs` - 共享定义
- `Runtime/Catalog/CatalogSchema.cs` - Schema定义
- `SPECIFICATION.md` - 规范文档
- `ARCHITECTURE.md` - 架构文档

### 实现文件（Owner1/2/3负责）
- `Runtime/Catalog/LocalContentCatalog.cs` - POC实现
- `Runtime/Bundle/*.cs` - Bundle相关实现
- `Runtime/Resource/ResourceProvider.cs` - 资源提供实现
- `Editor/*.cs` - 编辑器工具
