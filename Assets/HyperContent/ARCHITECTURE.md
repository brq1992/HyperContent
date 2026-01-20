# HyperContent 架构设计文档

## 设计原则

1. **接口驱动**: 所有核心功能通过接口定义，便于测试和替换实现
2. **模块化**: 清晰的模块划分，低耦合高内聚
3. **可扩展**: 支持不同的Catalog源、存储后端、传输协议
4. **类型安全**: 强类型定义，减少运行时错误

## 模块划分

### 1. Core Interfaces (核心接口层)

定义所有核心功能的接口，位于 `Runtime/Core/`:

- **IContentCatalog**: Catalog管理
  - 职责: 提供资产键到Bundle的映射，Bundle元信息查询
  - 实现: LocalContentCatalog (POC), RemoteCatalog (未来)
  
- **IBundleStore**: Bundle存储
  - 职责: 本地缓存管理，Bundle持久化
  - 实现: LocalBundleStore (POC), EncryptedBundleStore (未来)
  
- **IBundleTransport**: Bundle传输
  - 职责: 远程Bundle下载/上传
  - 实现: StubTransport (POC), HttpTransport (未来)
  
- **IBundleLoader**: Bundle加载
  - 职责: Unity AssetBundle的加载/卸载
  - 实现: UnityBundleLoader (POC)
  
- **IResourceProvider**: 资源提供
  - 职责: 对外API，提供LoadAsset接口
  - 实现: ResourceProvider (POC)

### 2. Data Structures (数据结构层)

运行期数据结构，位于 `Runtime/Data/` 和 `Shared/`:

- **ContentLocation**: 内容位置枚举
- **BundleInfo**: Bundle元信息
- **Handle**: 异步操作句柄基类
- **FetchResult**: 获取操作结果

### 3. Implementations (实现层)

各接口的具体实现，位于对应的子目录:

- `Runtime/Catalog/`: Catalog实现
- `Runtime/Bundle/`: Bundle相关实现
- `Runtime/Resource/`: 资源提供实现

### 4. Shared (共享层)

跨程序集共享的定义，位于 `Shared/`:

- **Constants**: 错误码、日志字段、命名规则
- **ContentLocation**: 位置枚举

## 依赖关系

```
Editor (未来)
  ↓
Runtime
  ├─ Core (接口定义)
  ├─ Data (数据结构)
  ├─ Catalog (Catalog实现)
  ├─ Bundle (Bundle实现)
  └─ Resource (资源提供)
  ↓
Shared (共享定义)
```

## 数据流

### LoadAsset流程

```
User Code
  ↓
IResourceProvider.LoadAsset<T>(key)
  ↓
IContentCatalog.TryGetBundleName(key) → bundleName
  ↓
IContentCatalog.TryGetBundleInfo(bundleName) → bundleInfo
  ↓
根据bundleInfo.Location:
  - Local/StreamingAssets → IBundleStore.GetLocalPath() → IBundleLoader.LoadFromFile()
  - Remote → IBundleTransport.Download() → IBundleStore.Save() → IBundleLoader.LoadFromMemory()
  ↓
AssetBundle.LoadAsset<T>()
  ↓
返回Asset
```

## 扩展点

### 1. 自定义Catalog源

实现 `IContentCatalog` 接口:
- 从数据库读取
- 从远程API获取
- 从加密文件读取

### 2. 自定义存储后端

实现 `IBundleStore` 接口:
- 加密存储
- 压缩存储
- 云存储同步

### 3. 自定义传输协议

实现 `IBundleTransport` 接口:
- HTTP/HTTPS
- 自定义协议
- P2P传输

## 线程模型

- **主线程**: Unity API调用（AssetBundle.Load等）
- **后台线程**: 文件IO、网络下载、哈希计算

## 内存管理

- **RefCount机制**: 每个资产维护引用计数
- **Bundle卸载**: 当Bundle内所有资产RefCount归零时卸载
- **缓存策略**: BundleStore管理本地缓存大小

## 错误处理

- **错误码**: 统一的错误码定义（见Constants.cs）
- **日志**: 结构化日志字段（见LogFields）
- **异常**: 接口方法返回bool或使用Result模式，避免抛出异常

## 性能考虑

- **异步加载**: 所有IO操作异步化
- **预加载**: 支持Bundle预加载
- **依赖加载**: 自动加载依赖Bundle
- **缓存**: 本地缓存减少重复下载
