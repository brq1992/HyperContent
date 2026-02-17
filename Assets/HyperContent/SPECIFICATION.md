# HyperContent 规范文档

本文档描述通用规范（命名、错误码、RefCount、目录结构等）。**Catalog Schema** 见 [RESOURCE_LOADING_SYSTEM_SPEC.md](RESOURCE_LOADING_SYSTEM_SPEC.md) 与 `Runtime/Catalog/CatalogSchemaV2.cs`（schemaVersion=2，GUID/Name、stringTable、nameAliases）。

## 1. 目录结构

### Runtime程序集
```
Assets/HyperContent/
├── Runtime/
│   ├── Core/           # 核心接口
│   │   ├── IContentCatalog.cs
│   │   ├── IBundleStore.cs
│   │   ├── IBundleTransport.cs
│   │   ├── IBundleLoader.cs
│   │   ├── IResourceProvider.cs
│   │   └── IAssetLoader.cs
│   ├── Data/            # 数据结构
│   │   ├── BundleInfo.cs
│   │   ├── Handle.cs
│   │   ├── AssetHandle.cs
│   │   └── FetchResult.cs
│   ├── Catalog/         # Catalog 实现
│   │   ├── CatalogSchemaV2.cs
│   │   └── LocalContentCatalogV2.cs
│   ├── Bundle/          # Bundle 相关
│   ├── Resource/        # 资源提供、IAssetLoader 实现
│   └── HyperContentManager.cs
├── Editor/              # 编辑器与构建
└── Shared/              # 共享定义
    ├── Constants.cs
    └── ContentLocation.cs
```

### 程序集划分
- `HyperContent.Runtime`: 运行期核心代码
- `HyperContent.Editor`: 编辑器与构建工具
- `HyperContent.Shared`: 共享的数据结构和常量

### 依赖方向
```
Editor → Runtime → Shared
```

## 2. 命名规则

### 文件命名
- Catalog文件: `{catalog_name}.catalog.json`
- Bundle文件: `{bundle_name}.bundle`
- Hash文件: `{bundle_name}.hash`（可选）

### 标识符命名
- Asset Key: 最大长度256字符，支持路径分隔符（`/`）
- Bundle Name: 最大长度128字符，不支持路径分隔符
- Catalog Name: 最大长度128字符

### 代码命名
- 接口: `I{Name}` (如 `IContentCatalog`)
- 实现类: `{Name}Impl` 或描述性名称 (如 `LocalContentCatalog`)
- 枚举: PascalCase (如 `ContentLocation`)
- 常量: UPPER_SNAKE_CASE (如 `ERROR_CODE`)

## 3. 错误码

### Catalog错误 (1000-1999)
- `1001`: Catalog未找到
- `1002`: Catalog格式无效
- `1003`: Catalog加载失败
- `1004`: Catalog版本不匹配
- `1005`: Catalog条目未找到

### Bundle错误 (2000-2999)
- `2001`: Bundle未找到
- `2002`: Bundle加载失败
- `2003`: Bundle哈希无效
- `2004`: Bundle大小不匹配
- `2005`: Bundle依赖缺失

### Transport错误 (3000-3999)
- `3001`: 网络错误
- `3002`: 超时
- `3003`: 无效URL
- `3004`: 下载失败

### Resource错误 (4000-4999)
- `4001`: 资源未找到
- `4002`: 资源类型不匹配
- `4003`: 资源加载失败
- `4004`: 资源键无效

### 系统错误 (5000-5999)
- `5001`: 系统未初始化
- `5002`: 系统已初始化
- `5003`: 系统状态无效
- `5004`: 内存不足

## 5. 日志字段

结构化日志字段名称（用于日志聚合和分析）:
- `operation`: 操作名称
- `key`: 资产键
- `bundle_name`: Bundle名称
- `error_code`: 错误码
- `error_message`: 错误消息
- `duration_ms`: 操作耗时（毫秒）
- `size_bytes`: 数据大小（字节）
- `ref_count`: 引用计数
- `status`: 状态
- `location`: 位置

## 5. RefCount规则

### 引用计数管理
1. **加载资产时**: RefCount自动+1
2. **调用ReleaseAsset**: RefCount-1
3. **RefCount归零**: 
   - 资产从内存中移除
   - 检查Bundle是否还有其他资产在使用
   - 如果没有，卸载Bundle（但不卸载已加载的对象，除非显式指定）

### 规则
- 同一资产多次LoadAsset，RefCount累加
- 必须调用ReleaseAsset释放引用
- Handle对象也有RefCount，用于管理异步操作的生命周期
- Bundle的卸载策略：当Bundle内所有资产的RefCount都归零时，才卸载Bundle

## 6. 最小可用链路

### 使用流程
1. 按 [Editor/QUICK_START.md](Editor/QUICK_START.md) 构建，得到 Catalog（.v2.catalog.json）与 Bundle
2. 将产物放入 StreamingAssets 或按 Catalog 配置
3. 初始化 HyperContentManager，使用 IAssetLoader.Load&lt;T&gt;(key) 或 IResourceProvider 加载资源

### 示例代码
```csharp
// 初始化
HyperContentManager.Instance.Initialize("test.catalog.json");

// 加载资源
var sprite = HyperContentManager.ResourceProvider.LoadAsset<Sprite>("test_sprite");
```

## 7. 接口变更控制

上述 Schema、接口、数据结构、错误码、日志、RefCount 的变更须经 **Owner0 Code Review**。职责与流程见 [OWNERS.md](OWNERS.md) 与 `.cursor/rules/owner0.md`。
