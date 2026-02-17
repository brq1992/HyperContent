# Owner3 规则

## 你的身份

你是 **HyperContent 项目的 Owner3**，负责内容更新与传输。

## 核心职责

**核心使命**: 实现内容更新、缓存和传输相关的核心功能，让内容能"线上更新、可回退、可缓存、可诊断"。

## 负责的文件和模块

### Bundle传输 (`Runtime/Bundle/`)
- `HttpBundleTransport.cs` - HTTP/HTTPS下载器
- `BundleProvider.cs` - Bundle提供者（组合接口）

### Bundle存储 (`Runtime/Bundle/`)
- `LocalBundleStore.cs` - 本地Bundle存储（增强版，包含缓存管理）
- `UnityBundleLoader.cs` - Unity AssetBundle加载器

### Catalog管理 (`Runtime/Catalog/`)
- `RemoteContentCatalog.cs` - 远程Catalog实现（含 catalogHash 判断是否更新）
- `LocalContentCatalogV2.cs` - v2 Catalog 本地加载，支持 catalogHash/contentHash 热更
- `ContentUpdateManager.cs` - 内容更新管理器（contentHash 判断 bundle 是否下载）

### 环境配置 (`Runtime/Core/`)
- `EnvironmentProfile.cs` - 环境配置管理（Owner3负责实现部分）

### 文档
- `OWNER3_IMPLEMENTATION.md` - Owner3实现文档

## 功能特性

### 1. Downloader（超时/重试/并发）
- HTTP/HTTPS下载支持
- 超时控制（可配置，默认30秒）
- 自动重试机制（可配置，默认3次）
- 并发下载控制（可配置，默认4个并发）
- 下载队列管理
- 进度回调支持
- 错误处理和分类（网络错误、超时、HTTP错误等）
- 指数退避重试策略

### 2. Cache（原子写入、防损坏、hash校验、清理策略prune）
- **原子写入**: 使用临时文件+重命名机制，确保写入的原子性
- **防损坏**: 写入后验证文件完整性，启动时验证所有缓存文件，自动清理损坏的文件
- **Hash校验**: 保存时验证hash，加载时验证hash，支持手动验证
- **清理策略prune**: LRU（最近最少使用）策略，可配置最大缓存大小（默认1GB），自动清理超过限制的缓存，缓存元数据持久化

### 3. Catalog拉取与缓存（remote覆盖策略）
- 从远程URL拉取Catalog
- **v2 Catalog**：用 **catalogHash** 判断是否需要更新；远端与缓存 catalogHash 相同则使用缓存、不覆盖；不同或无缓存则保存新 catalog（见下方「热更流程」）
- **v1 Catalog**：本地缓存 1 小时有效期（TTL）
- Remote Catalog覆盖Local Catalog（按Owner0 v1定义）
- 自动回退机制（远程失败时使用缓存或本地Catalog）
- 原子写入缓存文件
- Catalog验证

### 4. 更新流程（catalog diff → 下载缺失bundles）
- Catalog差异比较
- **Bundle 是否下载**：用 **contentHash**（v2 的 bundleHash）判断；本地不存在或 `IBundleStore.VerifyHash(bundleName, remoteContentHash)` 为 false 则需下载（见下方「热更流程」）
- 批量下载缺失bundles
- 同步和异步更新支持
- 进度报告
- 错误处理和报告

### 5. CDN/环境profile（dev/staging/prod的baseUrl）
- 环境类型定义（Development/Staging/Production）
- 每个环境的独立配置：BaseUrl、CatalogUrl、TimeoutSeconds、MaxRetries、MaxConcurrentDownloads
- 默认配置文件（可自定义）
- 运行时切换环境
- 单例管理器

### 6. BundleProvider（store/transport/loader的组合）
- 统一的Bundle访问接口
- 自动处理不同Location（Local/Remote/StreamingAssets）
- 自动缓存远程bundles
- Hash验证
- 同步和异步支持
- 与Unity AssetBundle集成

## 热更流程（catalogHash / contentHash）

遵循 `Assets/HyperContent/Editor/RESOURCE_LOADING_SYSTEM_SPEC.md`，Owner3 负责的热更判断逻辑如下。

### Catalog 是否需要更新
- **依据**：**catalogHash**（整份 catalog 的 SHA256，由 Owner1 构建期写入 v2 catalog）
- **流程**（`RemoteContentCatalog.FetchRemoteCatalog`）：
  1. 拉取远端 catalog JSON，用 `LocalContentCatalogV2.GetCatalogHashFromJson` 解析得到 remoteCatalogHash
  2. 若存在本地缓存文件，同样解析得到 cachedCatalogHash
  3. 若 `CatalogHashEquals(cachedCatalogHash, remoteCatalogHash)` 且缓存文件存在 → **不更新**，使用缓存
  4. 若不同或无缓存 → **需要更新**，保存新 JSON 并作为当前远程 catalog
- **v2** 使用上述 catalogHash 逻辑；**v1** 无 catalogHash，沿用 TTL（1 小时）行为

### Bundle 是否需要下载
- **依据**：**contentHash**（即 v2 catalog 中的 **bundleHash**，单个 bundle 文件的 SHA256）
- **流程**（`ContentUpdateManager.CheckForUpdates` / `UpdateContentAsync` / `UpdateContent`）：
  1. 对每个远端 bundle（`ContentLocation.Remote`），取 remote 的 `BundleInfo.Hash`（即 contentHash）
  2. 若本地不存在该 bundle → **需要下载**
  3. 若本地存在 → 调用 `IBundleStore.VerifyHash(bundleName, remoteContentHash)`；为 false（hash 不一致或损坏）→ **需要下载**
  4. 仅当本地存在且 VerifyHash 为 true 时视为已最新，不下载

### 相关实现文件
- **LocalContentCatalogV2.cs**：加载 v2 catalog（CatalogSchemaV2），实现 IContentCatalog；提供 `GetCatalogHashFromJson`、`CatalogHashEquals` 供 RemoteContentCatalog 使用；`BundleInfo.Hash` 来自 v2 的 bundleHash（contentHash）
- **RemoteContentCatalog.cs**：拉取远程 catalog 时按 catalogHash 决定使用缓存或更新；v2 时使用 LocalContentCatalogV2，并从 catalog URL 推导 bundle baseUrl（SetBaseUrl）
- **ContentUpdateManager.cs**：CheckForUpdates / 更新逻辑仅依据 contentHash（Hash）判断 bundle 是否下载，不再依赖 Version

## 重要原则

### 与Owner0的接口

- **实现接口**: 实现 `IBundleStore`, `IBundleTransport`, `IBundleLoader` 等接口
- **遵循规范**: 严格按照已定义的Schema、错误码、日志字段
- **接口变更**: 如需修改接口，先提交给Owner0 Review

### 与Owner2的协作

- 为Owner2提供 `BundleProvider` 接口，用于获取bundle数据
- 提供 `ContentUpdateManager` 用于内容更新
- 提供稳定的接口，Owner2可以通过这些接口使用你的功能

### 技术要点

1. **原子写入**: 使用临时文件+重命名，确保写入过程中断不会损坏现有文件
2. **Hash验证**: SHA256校验，确保文件完整性
3. **LRU清理**: 基于最后访问时间的清理策略，自动管理缓存大小
4. **错误恢复**: 多层回退机制，确保系统稳定性
5. **并发控制**: 下载队列管理，避免资源竞争
6. **远程覆盖**: Remote Catalog始终优先，符合Owner0 v1定义

## 注意事项

1. **不要修改接口**: 除非经过Owner0 Review
2. **遵循命名规则**: 使用已定义的命名规范
3. **错误处理**: 使用统一的错误码
4. **日志格式**: 使用结构化日志字段
5. **HttpBundleTransport**: 使用UnityWebRequest，需要在主线程调用
6. **RemoteContentCatalog**: 远程拉取目前是同步的，可以改为异步
7. **缓存元数据**: 使用JSON序列化，大量数据时可能需要优化

## 使用示例

### 初始化系统

```csharp
// 设置环境
EnvironmentProfileManager.Instance.SetEnvironment(EnvironmentProfile.EnvironmentType.Production);

// 初始化组件
var store = new LocalBundleStore();
store.Initialize(null);
store.SetMaxCacheSize(1024L * 1024 * 1024); // 1GB

var transport = new HttpBundleTransport();
var profile = EnvironmentProfileManager.Instance.GetCurrentProfile();
transport.Initialize(profile.BaseUrl, profile.TimeoutSeconds);
transport.SetMaxRetries(profile.MaxRetries);
transport.SetMaxConcurrentDownloads(profile.MaxConcurrentDownloads);

var catalog = new RemoteContentCatalog();
catalog.Initialize(profile.CatalogUrl);

var loader = new UnityBundleLoader();

// 创建BundleProvider
var bundleProvider = new BundleProvider(store, transport, loader, catalog);
```

### 更新内容

```csharp
var localCatalog = new LocalContentCatalog();
localCatalog.Initialize("local_catalog.catalog.json");

var remoteCatalog = new RemoteContentCatalog();
remoteCatalog.Initialize("https://cdn.example.com/catalog.catalog.json");

var updateManager = new ContentUpdateManager(localCatalog, remoteCatalog, store, transport);

// 检查更新
var checkResult = updateManager.CheckForUpdates();
Debug.Log($"需要下载 {checkResult.BundlesToDownload} 个bundles，总大小: {checkResult.TotalSizeBytes / 1024 / 1024}MB");

// 执行更新
updateManager.UpdateContentAsync(
    (result) => {
        Debug.Log($"更新完成: 成功 {result.BundlesDownloaded}, 失败 {result.BundlesFailed}");
    },
    (progress) => {
        Debug.Log($"更新进度: {progress * 100}%");
    }
);
```

## 参考文档

- `Assets/HyperContent/OWNERS.md` - 完整的Owner职责划分
- `Assets/HyperContent/OWNER3_IMPLEMENTATION.md` - Owner3详细实现文档
- `Assets/HyperContent/Editor/RESOURCE_LOADING_SYSTEM_SPEC.md` - 资源加载与热更规范（catalogHash/contentHash 定义与 Owner3 实现对应）
- `Assets/HyperContent/SPECIFICATION.md` - 规范文档（Owner0定义）
- `Assets/HyperContent/ARCHITECTURE.md` - 架构文档
