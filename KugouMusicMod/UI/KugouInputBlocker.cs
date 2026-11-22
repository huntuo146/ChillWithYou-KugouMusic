using UnityEngine;
using UnityEngine.UI;

namespace ChillWithYou.KugouMusic.UI
{
    /// <summary>
    /// 全透明 UGUI 射线拦截层（Overlay Canvas, sortingOrder = 32000）：
    /// - 解决 IMGUI 窗口不拦截 UGUI 点击导致的“点插件窗口却穿透触发游戏按钮”问题；
    /// - 不包含任何 Text / TMP 组件，彻底规避 Base_SC 字库缺失警告；
    /// - 歌词框在锁定状态下关闭拦截，实现完全鼠标穿透（仅在悬停解锁角落时临时拦截解锁点击）。
    /// </summary>
    internal sealed class KugouInputBlocker
    {
        public enum Slot
        {
            Main = 0,
            Lyric = 1,
        }

        private GameObject _root;
        private Canvas _canvas;
        private RectTransform _mainRect;
        private Image _mainImage;
        private RectTransform _lyricRect;
        private Image _lyricImage;
        private bool _disposed;

        public void SetSlot(Slot slot, Rect logicalRect, float uiScale, bool active)
        {
            if (_disposed)
            {
                return;
            }

            if (!active || logicalRect.width <= 1f || logicalRect.height <= 1f)
            {
                if (_root == null)
                {
                    return;
                }

                Image target = slot == Slot.Main ? _mainImage : _lyricImage;
                if (target != null && target.gameObject.activeSelf)
                {
                    target.gameObject.SetActive(false);
                }

                UpdateCanvasActiveState();
                return;
            }

            EnsureCreated();
            if (_root == null)
            {
                return;
            }

            RectTransform rt = slot == Slot.Main ? _mainRect : _lyricRect;
            Image img = slot == Slot.Main ? _mainImage : _lyricImage;
            if (rt == null || img == null)
            {
                return;
            }

            if (!_canvas.gameObject.activeSelf)
            {
                _canvas.gameObject.SetActive(true);
            }

            if (!img.gameObject.activeSelf)
            {
                img.gameObject.SetActive(true);
            }

            float scale = Mathf.Max(0.5f, uiScale);
            rt.anchoredPosition = new Vector2(logicalRect.x * scale, -logicalRect.y * scale);
            rt.sizeDelta = new Vector2(logicalRect.width * scale, logicalRect.height * scale);
        }

        private void EnsureCreated()
        {
            if (_root != null)
            {
                return;
            }

            try
            {
                var go = new GameObject("KugouMod_InputBlocker");
                go.hideFlags |= HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);

                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32000;
                canvas.pixelPerfect = false;

                go.AddComponent<GraphicRaycaster>();

                _mainImage = CreateBlockerImage(go.transform, "MainBlocker", out _mainRect);
                _lyricImage = CreateBlockerImage(go.transform, "LyricBlocker", out _lyricRect);

                _root = go;
                _canvas = canvas;
            }
            catch (System.Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[UI] 创建输入遮挡层失败（不影响插件功能）: " + ex.Message);
                }
            }
        }

        private static Image CreateBlockerImage(Transform parent, string name, out RectTransform rt)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            rt = child.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);

            var img = child.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = true;
            child.SetActive(false);
            return img;
        }

        private void UpdateCanvasActiveState()
        {
            if (_root == null)
            {
                return;
            }

            bool mainOn = _mainImage != null && _mainImage.gameObject.activeSelf;
            bool lyricOn = _lyricImage != null && _lyricImage.gameObject.activeSelf;
            if (!mainOn && !lyricOn && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }

            _canvas = null;
            _mainRect = null;
            _mainImage = null;
            _lyricRect = null;
            _lyricImage = null;
        }
    }
}
