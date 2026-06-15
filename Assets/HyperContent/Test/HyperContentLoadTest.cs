using System.Collections.Generic;
using UnityEngine;
using com.igg.core;
using com.igg.hypercontent.runtime;

namespace com.igg.hypercontent.test
{
    /// <summary>
    /// 通过 <see cref="AddressableManager"/> 在运行时按用户输入的 key 实例化资源。
    /// 在 ENABLE_HYPERCONTENT 下，启动时先按 <c>Bootstrapper.Loading</c> 的顺序完成
    /// <see cref="AddressableManager.CatalogDiskUpdateLoadingHelper"/> 与
    /// <see cref="AddressableManager.InitLoadingHelper"/>，准备就绪后才允许加载。
    /// 提供一个 OnGUI 面板：输入 key → 点击"加载"（或按回车）→ 已加载资源列表可单独"释放"。
    /// </summary>
    public class HyperContentLoadTest : MonoBehaviour
    {
        /// <summary>默认填入输入框的 key，便于一键测试主工程 UI。</summary>
        public const string MuseumUiAddress = "MuseumUI.prefab";

        /// <summary>默认 PNG 地址（catalog 内完整 asset path），点击「加载PNG」且输入框为空时使用。</summary>
        public const string DefaultPngAddress =
            "Assets/Art/Character/Common/Battle/FX/CommonEffectIcons/Common_FX01.png";

        private const string TAG = "[HyperContentLoadTest]";

        /// <summary>面板左上角起点（缩放前像素，相对屏幕左上角）。</summary>
        [SerializeField] private Vector2 _guiOrigin = new Vector2(10f, 30f);

        /// <summary>面板布局基准宽度（缩放前像素），决定面板内部控件的逻辑宽度。</summary>
        [SerializeField] private float _guiWidth = 520f;

        /// <summary>面板渲染宽度占屏幕宽度的比例，用于按屏幕大小自适应缩放。</summary>
        [SerializeField, Range(0.2f, 1f)] private float _screenWidthRatio = 0.5f;

        /// <summary>自适应缩放下限，防止小屏上字过小。</summary>
        [SerializeField] private float _minGuiScale = 1.5f;

        /// <summary>自适应缩放上限，防止超宽屏上字过大。</summary>
        [SerializeField] private float _maxGuiScale = 6f;

        /// <summary>是否在启动时自动加载默认 key（保留旧行为，默认关闭）。</summary>
        [SerializeField] private bool _autoLoadOnStart = false;

        /// <summary>已实例化的资源条目，用于面板列出与按钮释放。</summary>
        private readonly List<LoadedEntry> _loadedEntries = new List<LoadedEntry>();

        /// <summary>输入框当前内容。</summary>
        private string _inputKey = MuseumUiAddress;

        /// <summary>面板滚动位置。</summary>
        private Vector2 _scrollPos;

        /// <summary>HyperContent 初始化是否完成（非 ENABLE_HYPERCONTENT 下恒为 true）。</summary>
        private bool _isReady;

        /// <summary>初始化阶段的状态提示。</summary>
        private string _statusText = "未初始化";

        // ── 实验：Pump 调度模式对比 + 批量加载 ─────────────────────────────────

        /// <summary>批量加载 key 输入框，一行一个。</summary>
        private string _batchKeysInput = string.Empty;

        /// <summary>"快速填入 N 份当前 Key" 的 N（默认 5）。</summary>
        private int _quickFillCount = 5;

        /// <summary>当前批次未完成 entry 数；==0 时打 BATCH_END。</summary>
        private int _batchPending;

        /// <summary>当前批次发起的 entry 总数（仅用于 BATCH_END 日志）。</summary>
        private int _batchSize;

        /// <summary>当前批次起始帧号，用于算跨帧数。</summary>
        private int _batchStartFrame;

        /// <summary>当前批次起始 wall clock 时间（秒）。</summary>
        private float _batchStartTime;

        /// <summary>最近一次批次完成后的简短统计，显示在 GUI 顶部。</summary>
        private string _batchSummary = "（未运行批次）";

        private void Start()
        {
#if ENABLE_HYPERCONTENT
            _statusText = "CatalogDiskUpdateLoadingHelper 加载中...";
            var catalogHelper = new AddressableManager.CatalogDiskUpdateLoadingHelper();
            catalogHelper.LoadingFinished += () =>
            {
                catalogHelper.Dispose();
                _statusText = "InitLoadingHelper 加载中...";
                var initHelper = new AddressableManager.InitLoadingHelper();
                initHelper.LoadingFinished += () =>
                {
                    initHelper.Dispose();
                    OnReady();
                };
                initHelper.StartLoading();
            };
            catalogHelper.StartLoading();
#else
            OnReady();
#endif
        }

        private void OnReady()
        {
            _isReady = true;
            _statusText = "就绪";
            Debug.Log($"{TAG} 初始化完成，可以开始按 key 加载资源。");

            if (_autoLoadOnStart && !string.IsNullOrEmpty(_inputKey))
                LoadAsset(_inputKey);
        }

        private void Update()
        {
            // 回车键快捷加载，等价于点击"加载"按钮。
            if (_isReady && !string.IsNullOrEmpty(_inputKey) &&
                (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                LoadAsset(_inputKey);
            }
        }

        private void OnGUI()
        {
            // 根据屏幕宽度按比例自适应缩放：目标渲染宽度 = Screen.width * ratio，再除以逻辑宽度得到 scale。
            // 同时限制在 [_minGuiScale, _maxGuiScale]，并保证缩放后面板宽度不超过屏幕可用区。
            float safeWidth = Mathf.Max(1f, _guiWidth);
            float scale = Screen.width * Mathf.Clamp01(_screenWidthRatio) / safeWidth;
            scale = Mathf.Clamp(scale, Mathf.Max(0.1f, _minGuiScale), Mathf.Max(_minGuiScale, _maxGuiScale));

            // 若面板在当前 scale 下仍会超出屏幕（极端窄屏），按可用宽度回退缩放。
            float maxScaleByScreen = Mathf.Max(0.1f, (Screen.width - _guiOrigin.x * 2f) / safeWidth);
            if (scale > maxScaleByScreen) scale = maxScaleByScreen;

            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);

            // BeginArea 的坐标是缩放前的逻辑坐标，所以这里用 Screen.* / scale 换算到逻辑空间。
            float areaWidth = Mathf.Min(_guiWidth, Screen.width / scale - _guiOrigin.x * 2f);
            float areaHeight = Mathf.Max(60f, Screen.height / scale - _guiOrigin.y * 2f);
            GUILayout.BeginArea(new Rect(_guiOrigin.x, _guiOrigin.y, areaWidth, areaHeight), GUI.skin.box);

            GUILayout.Label($"HyperContent 加载测试  状态: {_statusText}");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Key:", GUILayout.Width(40f));
            GUI.SetNextControlName("HyperContentKeyInput");
            _inputKey = GUILayout.TextField(_inputKey ?? string.Empty, GUILayout.Width(250f));
            GUI.enabled = _isReady && !string.IsNullOrEmpty(_inputKey);
            if (GUILayout.Button("加载", GUILayout.Width(80f)))
                LoadAsset(_inputKey);
            if (GUILayout.Button("加载PNG", GUILayout.Width(80f)))
                LoadPng(string.IsNullOrWhiteSpace(_inputKey) ? DefaultPngAddress : _inputKey);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label("提示：prefab 用「加载」；纹理填 .png 路径或留空后点「加载PNG」（默认 Common_FX01.png）。回车等同「加载」。");

            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"已加载: {_loadedEntries.Count}");
            GUILayout.FlexibleSpace();
            GUI.enabled = _loadedEntries.Count > 0;
            if (GUILayout.Button("全部释放", GUILayout.Width(100f)))
                ReleaseAll();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            DrawExperimentPanel();

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);
            for (int i = _loadedEntries.Count - 1; i >= 0; i--)
            {
                var entry = _loadedEntries[i];
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label($"[{entry.state}] {entry.key}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("释放", GUILayout.Width(60f)))
                {
                    ReleaseEntry(entry);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.EndArea();

            GUI.matrix = prevMatrix;
        }

        private void LoadAsset(string pKey)
        {
            if (!_isReady)
            {
                Debug.LogWarning($"{TAG} 尚未就绪，忽略加载请求: {pKey}");
                return;
            }

            if (string.IsNullOrWhiteSpace(pKey))
            {
                Debug.LogWarning($"{TAG} 输入的 key 为空，已忽略。");
                return;
            }

            Debug.Log($"{TAG} AddressableManager.Instantiate: {pKey}");

            // 先创建条目，handle 在回调中再填回去；这样若同步失败也能在面板看到记录。
            var entry = new LoadedEntry { key = pKey, state = "Loading" };
            _loadedEntries.Add(entry);

            entry.handle = AddressableManager.Instantiate(
                pKey,
                parent: transform,
                instantiateInWorldSpace: false,
                pSuccessCallback: (pH) => OnAssetInstantiated(entry, pH),
                pFailedCallback: () => OnAssetFailed(entry));
        }

        private void OnAssetInstantiated(LoadedEntry pEntry, InstantiateHandle pHandle)
        {
            if (pHandle != null)
                pEntry.handle = pHandle;

            if (pEntry.handle == null || !pEntry.handle.IsValid())
            {
                pEntry.state = "Invalid";
                Debug.LogError($"{TAG} Instantiate 完成但 Handle 无效: {pEntry.key}");
                OnBatchItemComplete(pEntry);
                return;
            }

            var instance = pHandle.Result;
            if (instance != null)
                instance.name = pEntry.key;

            pEntry.state = "Loaded";
            Debug.Log($"{TAG} 已实例化: {pEntry.key}");
            OnBatchItemComplete(pEntry);
        }

        private void LoadPng(string pKey)
        {
            if (!_isReady)
            {
                Debug.LogWarning($"{TAG} 尚未就绪，忽略 PNG 加载请求: {pKey}");
                return;
            }

            if (string.IsNullOrWhiteSpace(pKey))
            {
                Debug.LogWarning($"{TAG} 输入的 PNG key 为空，已忽略。");
                return;
            }

            Debug.Log($"{TAG} AddressableManager.LoadAsset<Texture2D>: {pKey}");

            var entry = new LoadedEntry { key = pKey, state = "Loading" };
            _loadedEntries.Add(entry);

            entry.handle = AddressableManager.LoadAsset<Texture2D>(
                pKey,
                pSuccessCallback: (pH) => OnTextureLoaded(entry, pH),
                pFailedCallback: () => OnAssetFailed(entry, "纹理加载"));
        }

        private void OnTextureLoaded(LoadedEntry pEntry, LoadHandle<Texture2D> pHandle)
        {
            if (pHandle != null)
                pEntry.handle = pHandle;

            if (pEntry.handle == null || !pEntry.handle.IsValid())
            {
                pEntry.state = "Invalid";
                Debug.LogError($"{TAG} LoadAsset 完成但 Handle 无效: {pEntry.key}");
                OnBatchItemComplete(pEntry);
                return;
            }

            var tex = pHandle.Result;
            pEntry.state = "Loaded";
            Debug.Log($"{TAG} 已加载纹理: {pEntry.key} ({tex?.width}x{tex?.height})");
            OnBatchItemComplete(pEntry);
        }

        private void OnAssetFailed(LoadedEntry pEntry, string pAction = "实例化")
        {
            pEntry.state = "Failed";
            Debug.LogError($"{TAG} {pAction}失败: {pEntry.key}");
            OnBatchItemComplete(pEntry);
        }

        private void ReleaseEntry(LoadedEntry pEntry)
        {
            if (pEntry == null) return;

            if (pEntry.handle != null)
            {
                AddressableManager.ReleaseByHandle(pEntry.handle);
                pEntry.handle = null;
            }
            _loadedEntries.Remove(pEntry);
            Debug.Log($"{TAG} 已释放: {pEntry.key}");
        }

        private void ReleaseAll()
        {
            for (int i = _loadedEntries.Count - 1; i >= 0; i--)
            {
                var entry = _loadedEntries[i];
                if (entry?.handle != null)
                    AddressableManager.ReleaseByHandle(entry.handle);
            }
            _loadedEntries.Clear();
            Debug.Log($"{TAG} 已释放全部资源。");
        }

        // ── 实验面板 + 批量加载 ────────────────────────────────────────────────
        // 用途：一次性跑出"批量大小 × 调度模式"曲线，定位 inflection point
        //   - DeferAll：当前默认（入队 + 下一帧 Pump 一次性 drain）
        //   - Immediate：跳过队列同步执行（OPT-1 行为）
        //   - Throttled：入队 + Pump 每帧最多 N 个 bundle op，asset op 不限（A1 行为）
        // 测试方法：粘贴 N 个 key 一行一个 → 选模式 → "批量加载" → 看日志里的 BATCH_END 行
        //   BATCH_END 行包含 wallClock + frames + avgFrameMs 三个数，配合 IGGProfiler 单 op
        //   的 HC.Load_* / HC.Load.Stage.Schedule_* 可以横向对比三种模式的曲线形状。
        private void DrawExperimentPanel()
        {
            GUILayout.Space(8f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("─ 实验：Pump 调度模式对比 ─");

            GUILayout.BeginHorizontal();
            GUILayout.Label("模式:", GUILayout.Width(50f));
            int modeIndex = (int)ResourceManagerExperiments.PumpMode;
            int newModeIndex = GUILayout.Toolbar(modeIndex,
                new[] { "DeferAll（默认）", "Immediate（OPT-1）", "Throttled（A1）" });
            if (newModeIndex != modeIndex)
            {
                ResourceManagerExperiments.PumpMode = (PumpMode)newModeIndex;
                Debug.Log($"{TAG} 切换 PumpMode → {ResourceManagerExperiments.PumpMode}");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = ResourceManagerExperiments.PumpMode == PumpMode.Throttled;
            GUILayout.Label($"Throttle bundle ops/帧: {ResourceManagerExperiments.ThrottleBundleOpsPerFrame}",
                GUILayout.Width(220f));
            float n = GUILayout.HorizontalSlider(
                ResourceManagerExperiments.ThrottleBundleOpsPerFrame, 1f, 32f);
            int newN = Mathf.Clamp(Mathf.RoundToInt(n), 1, 32);
            if (newN != ResourceManagerExperiments.ThrottleBundleOpsPerFrame)
            {
                ResourceManagerExperiments.ThrottleBundleOpsPerFrame = newN;
                Debug.Log($"{TAG} 切换 ThrottleBundleOpsPerFrame → {newN}");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label("批量 Key（一行一个，留空时用上方 Key 输入框）:");
            _batchKeysInput = GUILayout.TextArea(_batchKeysInput ?? string.Empty,
                GUILayout.Height(60f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("快速填入当前 Key:", GUILayout.Width(120f));
            GUI.enabled = !string.IsNullOrWhiteSpace(_inputKey);
            if (GUILayout.Button("×5", GUILayout.Width(40f))) FillBatchWithCurrentKey(5);
            if (GUILayout.Button("×10", GUILayout.Width(40f))) FillBatchWithCurrentKey(10);
            if (GUILayout.Button("×30", GUILayout.Width(40f))) FillBatchWithCurrentKey(30);
            if (GUILayout.Button("×100", GUILayout.Width(50f))) FillBatchWithCurrentKey(100);
            GUI.enabled = true;
            if (GUILayout.Button("清空", GUILayout.Width(50f))) _batchKeysInput = string.Empty;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = _isReady && _batchPending == 0;
            if (GUILayout.Button("批量加载 Prefab", GUILayout.Width(160f)))
                RunBatch(pPng: false);
            if (GUILayout.Button("批量加载 PNG", GUILayout.Width(160f)))
                RunBatch(pPng: true);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            string progressTxt = _batchPending > 0
                ? $"进行中 {_batchSize - _batchPending}/{_batchSize}（已耗时 {(Time.realtimeSinceStartup - _batchStartTime) * 1000f:F0}ms / {Time.frameCount - _batchStartFrame} 帧）"
                : _batchSummary;
            GUILayout.Label($"批次状态: {progressTxt}");

            GUILayout.EndVertical();
        }

        private void FillBatchWithCurrentKey(int pCount)
        {
            if (string.IsNullOrWhiteSpace(_inputKey)) return;
            var sb = new System.Text.StringBuilder(_inputKey.Length * pCount + pCount);
            for (int i = 0; i < pCount; i++)
            {
                sb.Append(_inputKey);
                if (i < pCount - 1) sb.Append('\n');
            }
            _batchKeysInput = sb.ToString();
        }

        /// <summary>
        /// 解析 _batchKeysInput 拿到 N 个 key，按当前 PumpMode 同帧依次发起 LoadAsync。
        /// 完成时机由 OnBatchItemComplete 统计；BATCH_START / BATCH_END 日志包含 mode / throttle /
        /// wallClock / frames，配合 IGGProfiler 单 op sample 横向对比三种模式曲线。
        /// </summary>
        private void RunBatch(bool pPng)
        {
            if (!_isReady)
            {
                Debug.LogWarning($"{TAG} 尚未就绪，忽略批量加载请求");
                return;
            }
            if (_batchPending > 0)
            {
                Debug.LogWarning($"{TAG} 上一批 ({_batchPending}/{_batchSize}) 未完成，请等待或全部释放后重试");
                return;
            }

            var keys = ParseBatchKeys();
            if (keys.Count == 0)
            {
                Debug.LogWarning($"{TAG} 批量 key 为空（多行输入框 + 当前 Key 都空）");
                return;
            }

            _batchSize = keys.Count;
            _batchPending = keys.Count;
            _batchStartFrame = Time.frameCount;
            _batchStartTime = Time.realtimeSinceStartup;
            _batchSummary = "进行中...";

            string modeTag = ResourceManagerExperiments.PumpMode.ToString();
            int throttle = ResourceManagerExperiments.ThrottleBundleOpsPerFrame;
            Debug.Log($"{TAG} === BATCH_START size={keys.Count} type={(pPng ? "PNG" : "Prefab")} " +
                $"mode={modeTag} throttle={throttle} frame={_batchStartFrame} ===");

            // 业务侧加载发起本身是 hot loop，全部在同一帧（同一 OnGUI 回调）内串行调用，
            // 此时 ResourceManager 内部按 PumpMode 决定要不要同帧 Execute / 入队 / 限流。
            for (int i = 0; i < keys.Count; i++)
            {
                if (pPng) LoadPng(keys[i]);
                else LoadAsset(keys[i]);
            }
        }

        private List<string> ParseBatchKeys()
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(_batchKeysInput))
            {
                var lines = _batchKeysInput.Split(new[] { '\n', '\r' },
                    System.StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (!string.IsNullOrEmpty(trimmed)) list.Add(trimmed);
                }
            }
            else if (!string.IsNullOrWhiteSpace(_inputKey))
            {
                list.Add(_inputKey.Trim());
            }
            return list;
        }

        /// <summary>
        /// 单条 entry 完成（成功 / 失败 / 无效都算）时回调；==0 时打 BATCH_END 行汇总数据。
        /// _batchPending 由 RunBatch 一次性初始化，加载流程中只会单调递减，无并发风险。
        /// </summary>
        private void OnBatchItemComplete(LoadedEntry pEntry)
        {
            if (_batchPending <= 0) return;
            _batchPending--;
            if (_batchPending > 0) return;

            float wallMs = (Time.realtimeSinceStartup - _batchStartTime) * 1000f;
            int frames = Time.frameCount - _batchStartFrame;
            float avgFrameMs = wallMs / Mathf.Max(1, frames);
            string modeTag = ResourceManagerExperiments.PumpMode.ToString();
            int throttle = ResourceManagerExperiments.ThrottleBundleOpsPerFrame;

            // BATCH_END 行格式：把 mode / throttle / size / wallClock / frames / avgFrameMs 全部塞进单行，
            // 方便 grep "BATCH_END" 后 awk 切列做 CSV 喂给表格。
            string summary = $"size={_batchSize} mode={modeTag} throttle={throttle} " +
                $"wallClock={wallMs:F1}ms frames={frames} avgFrameMs={avgFrameMs:F1}";
            Debug.Log($"{TAG} === BATCH_END {summary} ===");
            _batchSummary = summary;
        }

        // ── 生命周期 ──────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            ReleaseAll();
        }

        /// <summary>面板中一条已加载（或加载中/失败）的资源记录。</summary>
        private class LoadedEntry
        {
            public string key;
            public ILoadHandleBase handle;
            public string state;
        }
    }
}
