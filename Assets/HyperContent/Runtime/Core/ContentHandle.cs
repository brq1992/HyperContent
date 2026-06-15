using System;
using System.Collections;
using System.Runtime.CompilerServices;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Non-generic handle for async operations. Used by Release API and internal tracking.
    /// Wraps an internal AsyncOperationBase without exposing typed result.
    /// Each handle carries a unique HandleId for double-release detection.
    /// </summary>
    public struct ContentHandle : IEquatable<ContentHandle>
    {
        internal readonly AsyncOperationBase Operation;
        internal readonly int HandleId;

        internal ContentHandle(AsyncOperationBase pOperation, int pHandleId)
        {
            Operation = pOperation;
            HandleId = pHandleId;
        }

        public bool IsValid => Operation != null && Operation.Status != OperationStatus.Disposed;

        public bool IsDone => Operation != null &&
            (Operation.Status == OperationStatus.Succeeded || Operation.Status == OperationStatus.Failed);

        public bool IsSuccess => Operation != null && Operation.Status == OperationStatus.Succeeded;

        public float Progress => Operation?.GetProgress() ?? 0f;

        /// <summary>
        /// Error message when the operation failed; null on success.
        /// </summary>
        public string Error => Operation?.Exception?.Message;

        /// <summary>
        /// Internal operation identifier used by the system for cache lookup and release.
        /// </summary>
        public int OperationId => Operation?.LocationHash ?? 0;

        public bool Equals(ContentHandle pOther) => HandleId == pOther.HandleId && Operation == pOther.Operation;
        public override bool Equals(object pObj) => pObj is ContentHandle other && Equals(other);
        public override int GetHashCode() => HandleId;
        public static bool operator ==(ContentHandle pA, ContentHandle pB) => pA.Equals(pB);
        public static bool operator !=(ContentHandle pA, ContentHandle pB) => !pA.Equals(pB);
    }

    /// <summary>
    /// Unified generic handle struct for all HyperContent async operations.
    /// This is the sole contract between caller and system — callers only see
    /// address (string) and handle (ContentHandle&lt;T&gt;).
    ///
    /// Supports three async patterns:
    ///   1. Completed event callback
    ///   2. await (GetAwaiter)
    ///   3. yield return (IEnumerator — MoveNext returns false when IsDone)
    ///
    /// See ARCHITECTURE.md §3 and LOAD_RELEASE_FLOW.md §6.
    /// </summary>
    public struct ContentHandle<T> : IEnumerator, IEquatable<ContentHandle<T>>
    {
        internal readonly AsyncOperationBase Operation;
        internal readonly int HandleId;
        private readonly Func<T> _resultGetter;

        internal ContentHandle(AsyncOperationBase pOperation, int pHandleId, Func<T> pResultGetter)
        {
            Operation = pOperation;
            HandleId = pHandleId;
            _resultGetter = pResultGetter;
        }

        public bool IsValid => Operation != null && Operation.Status != OperationStatus.Disposed;

        public bool IsDone => Operation != null &&
            (Operation.Status == OperationStatus.Succeeded || Operation.Status == OperationStatus.Failed);

        public bool IsSuccess => Operation != null && Operation.Status == OperationStatus.Succeeded;

        public float Progress => Operation?.GetProgress() ?? 0f;

        /// <summary>
        /// Error message when the operation failed; null on success.
        /// </summary>
        public string Error => Operation?.Exception?.Message;

        /// <summary>
        /// The loaded result. Valid only when IsDone &amp;&amp; IsSuccess.
        /// </summary>
        public T Result => IsSuccess && _resultGetter != null ? _resultGetter() : default;

        /// <summary>
        /// Internal operation identifier used by the system for cache lookup and release.
        /// </summary>
        public int OperationId => Operation?.LocationHash ?? 0;

        /// <summary>
        /// Fires when the operation reaches a terminal state (Succeeded or Failed).
        /// Safe to subscribe after completion — the callback fires immediately if already done.
        /// Note: -= is unsupported on struct-based handles; avoid relying on unsubscribe.
        /// </summary>
        public event Action<ContentHandle<T>> Completed
        {
            add
            {
                if (Operation == null) return;
                var captured = this;
                if (IsDone)
                {
                    value?.Invoke(captured);
                    return;
                }
                Operation.OnCompleted += _ => value?.Invoke(captured);
            }
            // Struct handles can't support unsubscribe (each += captures a fresh boxed copy), so this
            // is intentionally a no-op. Warn so `Completed -= handler` misuse surfaces during dev
            // instead of silently leaking the subscription. Stripped in release (HCLogger.LogWarn is
            // [Conditional]); capture a flag inside the callback to opt out instead.
            remove
            {
                HCLogger.LogWarn("[ContentHandle] Completed -= is not supported on struct handles. " +
                    "The handler was NOT removed. Capture a flag in the callback to ignore late invocations instead.");
            }
        }

        /// <summary>
        /// Returns an awaiter for async/await support.
        /// Usage: var result = await HyperContent.LoadAsync&lt;Texture2D&gt;("UI/Icon");
        /// </summary>
        public ContentHandleAwaiter<T> GetAwaiter() => new ContentHandleAwaiter<T>(this);

        /// <summary>
        /// IEnumerator support for coroutine: yield return handle.
        /// MoveNext returns false when IsDone (operation completed or failed).
        /// </summary>
        object IEnumerator.Current => null;
        bool IEnumerator.MoveNext() => !IsDone;
        void IEnumerator.Reset() { }

        /// <summary>
        /// Returns an IEnumerator for coroutine support (alternative to yielding the handle directly).
        /// Usage: yield return handle.AsCoroutine(); or yield return handle;
        /// </summary>
        public IEnumerator AsCoroutine()
        {
            while (!IsDone)
                yield return null;
        }

        /// <summary>
        /// Implicit conversion to non-generic ContentHandle for Release API.
        /// </summary>
        public static implicit operator ContentHandle(ContentHandle<T> pHandle)
            => new ContentHandle(pHandle.Operation, pHandle.HandleId);

        public bool Equals(ContentHandle<T> pOther) => HandleId == pOther.HandleId && Operation == pOther.Operation;
        public override bool Equals(object pObj) => pObj is ContentHandle<T> other && Equals(other);
        public override int GetHashCode() => HandleId;
        public static bool operator ==(ContentHandle<T> pA, ContentHandle<T> pB) => pA.Equals(pB);
        public static bool operator !=(ContentHandle<T> pA, ContentHandle<T> pB) => !pA.Equals(pB);
    }

    /// <summary>
    /// Awaiter for ContentHandle&lt;T&gt;, enabling async/await pattern.
    /// </summary>
    public struct ContentHandleAwaiter<T> : ICriticalNotifyCompletion
    {
        private readonly ContentHandle<T> _handle;

        public ContentHandleAwaiter(ContentHandle<T> pHandle)
        {
            _handle = pHandle;
        }

        public bool IsCompleted => _handle.IsDone || _handle.Operation == null;

        public T GetResult()
        {
            if (!string.IsNullOrEmpty(_handle.Error))
                throw new Exception(_handle.Error);
            return _handle.Result;
        }

        public void OnCompleted(Action pContinuation)
        {
            if (_handle.Operation == null || _handle.IsDone)
            {
                pContinuation?.Invoke();
                return;
            }
            _handle.Operation.OnCompleted += _ => pContinuation?.Invoke();
        }

        public void UnsafeOnCompleted(Action pContinuation) => OnCompleted(pContinuation);
    }
}
