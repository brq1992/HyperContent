using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace com.igg.hypercontent.shared
{
    /// <summary>
    /// Centralized logging for HyperContent with macro-based level control.
    ///
    /// Define scripting symbols to control verbosity:
    ///   HYPERCONTENT_LOG_VERBOSE  — all levels (Verbose + Info + Warn + Error)
    ///   HYPERCONTENT_LOG          — Info + Warn + Error
    ///   (neither)                 — Error only (recommended for release builds)
    ///
    /// Uses [Conditional] so disabled calls are stripped at compile time (zero overhead).
    ///
    /// Class name ends with "Logger" and methods start with "Log" — this is an
    /// undocumented Unity convention that makes Console double-click skip these
    /// methods and navigate directly to the actual caller.
    /// </summary>
    public static class HCLogger
    {
        private const string TAG = "[HC]";

        private static string Prefix => $"{TAG} [{Time.frameCount}]";

        // ── Verbose ─────────────────────────────────────────────────────

        [Conditional("HYPERCONTENT_LOG_VERBOSE")]
        public static void LogVerbose(string msg)
        {
            Debug.Log($"{Prefix} {msg}");
        }

        // ── Info ────────────────────────────────────────────────────────

        [Conditional("HYPERCONTENT_LOG")]
        [Conditional("HYPERCONTENT_LOG_VERBOSE")]
        public static void LogInfo(string msg)
        {
            Debug.Log($"{Prefix} {msg}");
        }

        // ── Warn ────────────────────────────────────────────────────────

        [Conditional("HYPERCONTENT_LOG")]
        [Conditional("HYPERCONTENT_LOG_VERBOSE")]
        public static void LogWarn(string msg)
        {
            Debug.LogWarning($"{Prefix} {msg}");
        }

        // ── Error ───────────────────────────────────────────────────────

        public static void LogError(string msg)
        {
            Debug.LogError($"{Prefix} {msg}");
        }

        public static void LogError(int errorCode, string msg)
        {
            Debug.LogError($"{Prefix} [{LogFields.ERROR_CODE}={errorCode}] {msg}");
        }

        // ── Diagnostic（性能元数据）─────────────────────────────────────
        //
        // 与 IGGProfiler 共用 ENABLE_PROFILERLOG 宏：
        //   * 业务侧测试时已习惯单开 ENABLE_PROFILERLOG 跑 D2 性能采集，元数据日志和耗时
        //     sample 总是一起看，复用同一个开关比新增宏对接入方更友好。
        //   * [Conditional("ENABLE_PROFILERLOG")] 关宏后调用点 + 参数表达式被 C# 编译器
        //     完整擦除（A2 已验证），所以 IO 计算（FileInfo.Length）必须包在方法体内部
        //     才能确保零开销 —— 调用前一行的计算 [Conditional] 不擦除。
        //
        // 输出格式与 IGGProfiler 视觉对齐：
        //   IGGProfiler:    [yyyy-MM-dd,HH:mm:ss.fff][性能统计]   HC.X_y: Z ms ...
        //   LogDiagnostic:  [yyyy-MM-dd,HH:mm:ss.fff][性能元数据] HC.X_y: Z unit ...
        // grep "HC\." 能一次性抓到所有 HyperContent 性能诊断输出。
        //
        // 调用方约定：传入的 msg 必须是 "HC.XX_yyy: value unit" 风格，前缀由调用方负责。

        [Conditional("ENABLE_PROFILERLOG")]
        public static void LogDiagnostic(string msg)
        {
            string timeStr = System.DateTime.Now.ToString("[yyyy-MM-dd,HH:mm:ss.fff]");
            Debug.Log($"{timeStr}[性能元数据] {msg}");
        }

        // 给"路径来自磁盘"的 provider 用（BundleFileProvider / PAD store fallback）。
        // jar URI（Android StreamingAssets 内嵌的 jar:file://.../base.apk!/assets/...）
        // 无法用 FileInfo，静默跳过 —— 业务 AAB 测试场景主要走 PAD 主路径或 store 路径，
        // BundleFileProvider StreamingAssets fallback 在 PAD 启用后是异常旁路。
        [Conditional("ENABLE_PROFILERLOG")]
        public static void LogBundleSize(string bundleName, string filePath, string source = "disk")
        {
            if (string.IsNullOrEmpty(filePath)) return;
            if (filePath.IndexOf("://", System.StringComparison.Ordinal) >= 0) return;
            try
            {
                // FileInfo.Length 返回 long、保证非负（文件大小语义），cast 到 ulong 安全；
                // 选 ulong 作 LogBundleSizeBytes 参数类型与 PAD AssetLocation.Size 对齐，
                // 避免 PAD 路径上每个调用点都要 (long) 显式转。
                long bytes = new System.IO.FileInfo(filePath).Length;
                LogBundleSizeBytes(bundleName, (ulong)bytes, source);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HCLogger.LogBundleSize] failed for {bundleName} path={filePath}: {e.Message}");
            }
        }

        // 给"size 已知"的 provider 用（PAD 主路径，assetLocation.Size 直接喂入），无 IO。
        // source 字段用于区分 bundle 来源（pad / store / disk），分析 IO 性能特征时
        // 可用于识别 mmap apk 路径 vs 普通文件路径 vs CDN 缓存路径的差异。
        // 参数类型选 ulong 而非 long：与 PlayAssetDelivery.AssetLocation.Size 类型一致，
        // PAD 调用点无需 cast；FileInfo 路径在 LogBundleSize 内部 cast 一次（安全，非负）。
        [Conditional("ENABLE_PROFILERLOG")]
        public static void LogBundleSizeBytes(string bundleName, ulong bytes, string source = "disk")
        {
            float kb = bytes / 1024f;
            string timeStr = System.DateTime.Now.ToString("[yyyy-MM-dd,HH:mm:ss.fff]");
            Debug.Log($"{timeStr}[性能元数据] HC.BundleSize_{bundleName}: {kb:F1} KB source={source}");
        }
    }
}
