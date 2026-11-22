using System.Collections.Generic;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Theme
{
    /// <summary>
    /// 基于 unscaledDeltaTime 的轻量状态动画表：
    /// 管理悬停淡入淡出、开关/分段滑块位置、呼吸光晕、黑胶转速，并支持 ReduceMotion。
    /// </summary>
    internal static class KugouAnim
    {
        private struct FloatState
        {
            public float Value;
            public float LastSeenTime;
        }

        private static readonly Dictionary<int, FloatState> States = new Dictionary<int, FloatState>();
        private static readonly List<int> PruneKeys = new List<int>();
        private static float _nextPruneTime;
        private static float _vinylAngle;
        private static float _vinylSpeed;

        public static bool ReduceMotion
        {
            get { return Plugin.ReduceMotion != null && Plugin.ReduceMotion.Value; }
        }

        public static float Hover(int id, bool hovered, float speed = 18f)
        {
            return StepFloat(id, hovered ? 1f : 0f, speed, 0f);
        }

        public static float ValueLerp(int id, float target, float speed = 16f)
        {
            return StepFloat(id, target, speed, target);
        }

        private static float StepFloat(int id, float target, float speed, float initialValue)
        {
            if (ReduceMotion)
            {
                return target;
            }

            float now = Time.unscaledTime;
            FloatState state;
            if (!States.TryGetValue(id, out state))
            {
                state = new FloatState { Value = initialValue, LastSeenTime = now };
            }
            else if (Event.current == null || Event.current.type == EventType.Repaint)
            {
                float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
                float t = 1f - Mathf.Exp(-speed * dt);
                state.Value = Mathf.Lerp(state.Value, target, t);
            }

            state.LastSeenTime = now;
            States[id] = state;
            PruneIfNeeded(now);
            return state.Value;
        }

        public static float EaseOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            float inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        public static float EaseInOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
        }

        public static float Pulse(float periodSeconds)
        {
            if (ReduceMotion || periodSeconds <= 0.05f)
            {
                return 0.8f;
            }

            float phase = (Time.unscaledTime / periodSeconds) * Mathf.PI * 2f;
            return 0.5f + 0.5f * Mathf.Sin(phase);
        }

        public static float StepVinylAngle(bool isPlaying)
        {
            if (ReduceMotion)
            {
                return 0f;
            }

            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
            float targetSpeed = isPlaying ? 72f : 0f; // 柔和 12 rpm，安静不眩晕
            _vinylSpeed = Mathf.Lerp(_vinylSpeed, targetSpeed, 1f - Mathf.Exp(-6f * dt));
            _vinylAngle = (_vinylAngle + _vinylSpeed * dt) % 360f;
            return _vinylAngle;
        }

        private static void PruneIfNeeded(float now)
        {
            if (now < _nextPruneTime || States.Count < 160)
            {
                return;
            }

            _nextPruneTime = now + 10f;
            PruneKeys.Clear();
            foreach (var kv in States)
            {
                if (now - kv.Value.LastSeenTime > 15f)
                {
                    PruneKeys.Add(kv.Key);
                }
            }

            for (int i = 0; i < PruneKeys.Count; i++)
            {
                States.Remove(PruneKeys[i]);
            }

            PruneKeys.Clear();
        }

        public static void Clear()
        {
            States.Clear();
            PruneKeys.Clear();
            _vinylAngle = 0f;
            _vinylSpeed = 0f;
        }
    }
}
