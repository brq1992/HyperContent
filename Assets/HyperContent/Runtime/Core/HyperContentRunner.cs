using System;
using System.Collections.Generic;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Hidden MonoBehaviour singleton that provides a per-frame tick and delayed-callback
    /// facility for subsystems that need to drive state machines without relying on
    /// <c>async/await</c> (e.g. HttpBundleTransport retry backoff, download progress polling).
    ///
    /// Mirrors the role of Addressables' <c>MonoBehaviourCallbackHooks</c>. The GameObject is
    /// <c>HideFlags.HideAndDontSave</c> and marked <c>DontDestroyOnLoad</c>. Created lazily on
    /// first access from any thread; callers remain responsible for subscribing/unsubscribing.
    /// </summary>
    internal sealed class HyperContentRunner : MonoBehaviour
    {
        private static HyperContentRunner s_instance;

        private event Action _onUpdate;
        private readonly List<ScheduledCallback> _scheduled = new List<ScheduledCallback>();
        private readonly List<ScheduledCallback> _dueScratch = new List<ScheduledCallback>();

        private struct ScheduledCallback
        {
            public float dueTime;
            public Action callback;
            public int token;
        }

        private int _nextToken;

        internal static HyperContentRunner Instance
        {
            get
            {
                if (s_instance == null)
                    CreateInstance();
                return s_instance;
            }
        }

        private static void CreateInstance()
        {
            if (s_instance != null) return;

            var go = new GameObject("[HyperContentRunner]")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<HyperContentRunner>();
        }

        /// <summary>Subscribe to a per-frame Update tick. Safe to call from any user code.</summary>
        public void AddUpdate(Action callback)
        {
            if (callback != null) _onUpdate += callback;
        }

        public void RemoveUpdate(Action callback)
        {
            if (callback != null) _onUpdate -= callback;
        }

        /// <summary>
        /// Schedule <paramref name="callback"/> to fire after <paramref name="delaySeconds"/>
        /// (real time, unaffected by <c>Time.timeScale</c>). Returns an opaque token that can
        /// be passed to <see cref="CancelSchedule"/> to prevent the callback from firing.
        /// </summary>
        public int Schedule(float delaySeconds, Action callback)
        {
            if (callback == null) return 0;
            int token = ++_nextToken;
            _scheduled.Add(new ScheduledCallback
            {
                dueTime = Time.realtimeSinceStartup + Mathf.Max(0f, delaySeconds),
                callback = callback,
                token = token
            });
            return token;
        }

        public void CancelSchedule(int token)
        {
            if (token == 0) return;
            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                if (_scheduled[i].token == token)
                {
                    _scheduled.RemoveAt(i);
                    return;
                }
            }
        }

        private void Update()
        {
            var update = _onUpdate;
            if (update != null)
            {
                try { update(); }
                catch (Exception e) { HCLogger.LogError($"[HyperContentRunner] update ex: {e.Message}"); }
            }

            if (_scheduled.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            _dueScratch.Clear();

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                if (now >= _scheduled[i].dueTime)
                {
                    _dueScratch.Add(_scheduled[i]);
                    _scheduled.RemoveAt(i);
                }
            }

            for (int i = 0; i < _dueScratch.Count; i++)
            {
                try { _dueScratch[i].callback?.Invoke(); }
                catch (Exception e) { HCLogger.LogError($"[HyperContentRunner] scheduled ex: {e.Message}"); }
            }
            _dueScratch.Clear();
        }

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }
    }
}
