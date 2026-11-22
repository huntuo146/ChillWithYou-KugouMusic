using System;
using System.Collections.Generic;
using System.Reflection;
using Bulbul;
using ChillWithYou.KugouMusic.UI.Theme;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ChillWithYou.KugouMusic.UI
{
    /// <summary>
    /// 在游戏原生 MusicPlayListTabUI 面板旁注入两个风格一致的按钮（音源切换 / 打开酷狗面板）：
    /// - 严格使用 Base_SC 字库安全汉字与 ASCII（不用 _、|、●、♪、下划线等易警告字符）；
    /// - 酷狗模式下添加 8px 薄荷绿圆形状态点（纯 Image Sprite，无文字）并微染薰衣草字色；
    /// - 自动适配 LayoutGroup 与无布局组时的 AABB 防重叠位置偏移。
    /// </summary>
    internal sealed class NativeButtonInjector
    {
        private const string ToggleObjName = "Kugou_ToggleButton";
        private const string PanelObjName = "Kugou_PanelButton";

        private static readonly Color KugouTextTint = KugouTheme.Hex(0xB9AEFF, 1f);

        private GameObject _toggleObject;
        private GameObject _panelObject;
        private Button _toggleButton;
        private TextMeshProUGUI _toggleLabel;
        private Image _toggleStatusDot;
        private Color _originalLabelColor = Color.white;
        private bool _hasOriginalLabelColor;

        private string _lastStateKey = string.Empty;
        private float _nextRefreshTime;

        public void OnFacilityReady(MusicPlayListTabUI tab, UnityAction onToggleSource, UnityAction onOpenPanel)
        {
            if (tab == null)
            {
                return;
            }

            try
            {
                InjectButtons(tab, onToggleSource, onOpenPanel);
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[UI] 原生按钮注入失败（已降级为 F8 面板）: " + ex.Message);
                }
            }
        }

        public void TickRefresh()
        {
            if (Time.unscaledTime < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.unscaledTime + 0.5f;
            RefreshToggleVisuals(false);
        }

        public void ForceRefresh()
        {
            RefreshToggleVisuals(true);
        }

        private void InjectButtons(MusicPlayListTabUI tab, UnityAction onToggleSource, UnityAction onOpenPanel)
        {
            if (_toggleObject != null && _panelObject != null)
            {
                RefreshToggleVisuals(true);
                return;
            }

            Button template = ResolveTemplateButton(tab);
            if (template == null || template.transform.parent == null)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[UI] 未找到可克隆的原生按钮模板，跳过 UGUI 注入");
                }

                return;
            }

            Transform parent = template.transform.parent;
            bool hasLayoutGroup = parent.GetComponent<HorizontalOrVerticalLayoutGroup>() != null ||
                                  parent.GetComponent<GridLayoutGroup>() != null;

            _toggleObject = FindOrCloneButton(parent, template, ToggleObjName, "官方音源", 1, hasLayoutGroup, onToggleSource);
            _panelObject = FindOrCloneButton(parent, template, PanelObjName, "酷狗面板", 2, hasLayoutGroup, onOpenPanel);

            if (_toggleObject != null)
            {
                _toggleButton = _toggleObject.GetComponent<Button>();
                _toggleLabel = _toggleObject.GetComponentInChildren<TextMeshProUGUI>(true);
                if (_toggleLabel != null && !_hasOriginalLabelColor)
                {
                    _originalLabelColor = _toggleLabel.color;
                    _hasOriginalLabelColor = true;
                }

                _toggleStatusDot = EnsureStatusDot(_toggleObject.transform, _toggleLabel);
            }

            if (hasLayoutGroup && parent is RectTransform parentRect)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);
            }

            RefreshToggleVisuals(true);
            if (Plugin.Log != null)
            {
                Plugin.Log.LogInfo("[UI] 原生按钮注入完成（LayoutGroup=" + hasLayoutGroup + "）");
            }
        }

        private static Button ResolveTemplateButton(MusicPlayListTabUI tab)
        {
            object tagButton = FieldInfoCache.GetValue(typeof(MusicPlayListTabUI), "_tagButton", tab);
            var template = tagButton as Button;
            if (template != null)
            {
                return template;
            }

            object import = FieldInfoCache.GetValue(typeof(MusicPlayListTabUI), "_fileImportButton", tab);
            var importButton = import as MusicImportButton;
            if (importButton != null)
            {
                return FieldInfoCache.GetValue(typeof(MusicImportButton), "_button", importButton) as Button;
            }

            return null;
        }

        private static GameObject FindOrCloneButton(
            Transform parent,
            Button template,
            string name,
            string labelText,
            int siblingOffset,
            bool hasLayoutGroup,
            UnityAction action)
        {
            Transform existing = parent.Find(name);
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = UnityEngine.Object.Instantiate(template.gameObject, parent);
                go.name = name;
                go.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + siblingOffset);

                if (!hasLayoutGroup)
                {
                    PlaceWithoutOverlap(go.GetComponent<RectTransform>(), template.GetComponent<RectTransform>(), parent, siblingOffset);
                }
            }

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick = new Button.ButtonClickedEvent();
                if (action != null)
                {
                    btn.onClick.AddListener(action);
                }
            }

            TextLocalizationBehaviour[] localizations = go.GetComponentsInChildren<TextLocalizationBehaviour>(true);
            for (int i = 0; i < localizations.Length; i++)
            {
                if (localizations[i] != null)
                {
                    localizations[i].enabled = false;
                }
            }

            TextMeshProUGUI label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                label.fontStyle &= ~FontStyles.Underline;
                label.text = labelText;
            }

            return go;
        }

        private static void PlaceWithoutOverlap(RectTransform cloneRect, RectTransform templateRect, Transform parent, int stepIndex)
        {
            if (cloneRect == null || templateRect == null)
            {
                return;
            }

            float stepY = Mathf.Max(30f, templateRect.rect.height + 8f);
            Vector2 basePos = templateRect.anchoredPosition;
            Vector2[] candidates =
            {
                basePos + new Vector2(0f, -stepY * stepIndex),
                basePos + new Vector2(0f, stepY * stepIndex),
                basePos + new Vector2((templateRect.rect.width + 8f) * stepIndex, 0f),
                basePos + new Vector2(-(templateRect.rect.width + 8f) * stepIndex, 0f),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                cloneRect.anchoredPosition = candidates[i];
                if (!OverlapsAnySibling(cloneRect, parent))
                {
                    return;
                }
            }

            cloneRect.anchoredPosition = candidates[0];
        }

        private static bool OverlapsAnySibling(RectTransform self, Transform parent)
        {
            var selfCorners = new Vector3[4];
            var otherCorners = new Vector3[4];
            self.GetWorldCorners(selfCorners);
            Rect selfWorld = CornersToRect(selfCorners);

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null || child == self || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (child.name == ToggleObjName || child.name == PanelObjName)
                {
                    continue;
                }

                var other = child as RectTransform;
                if (other == null)
                {
                    continue;
                }

                other.GetWorldCorners(otherCorners);
                Rect otherWorld = CornersToRect(otherCorners);
                if (selfWorld.Overlaps(otherWorld))
                {
                    return true;
                }
            }

            return false;
        }

        private static Rect CornersToRect(Vector3[] c)
        {
            float minX = Mathf.Min(Mathf.Min(c[0].x, c[1].x), Mathf.Min(c[2].x, c[3].x)) + 2f;
            float maxX = Mathf.Max(Mathf.Max(c[0].x, c[1].x), Mathf.Max(c[2].x, c[3].x)) - 2f;
            float minY = Mathf.Min(Mathf.Min(c[0].y, c[1].y), Mathf.Min(c[2].y, c[3].y)) + 2f;
            float maxY = Mathf.Max(Mathf.Max(c[0].y, c[1].y), Mathf.Max(c[2].y, c[3].y)) - 2f;
            return Rect.MinMaxRect(minX, minY, Mathf.Max(minX, maxX), Mathf.Max(minY, maxY));
        }

        private static Image EnsureStatusDot(Transform buttonTransform, TextMeshProUGUI label)
        {
            if (buttonTransform == null)
            {
                return null;
            }

            Transform existing = buttonTransform.Find("Kugou_StatusDot");
            if (existing != null)
            {
                return existing.GetComponent<Image>();
            }

            var dotGo = new GameObject("Kugou_StatusDot");
            dotGo.transform.SetParent(buttonTransform, false);
            var rt = dotGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(8f, 8f);

            float offsetX = 12f;
            if (label != null)
            {
                var btnRect = buttonTransform as RectTransform;
                float textHalf = label.preferredWidth * 0.5f;
                if (btnRect != null && btnRect.rect.width > textHalf * 2f + 28f)
                {
                    offsetX = Mathf.Max(10f, (btnRect.rect.width * 0.5f) - textHalf - 10f);
                }
            }

            rt.anchoredPosition = new Vector2(offsetX, 0f);

            var img = dotGo.AddComponent<Image>();
            img.sprite = KugouTextures.CreateCircleSprite(16);
            img.raycastTarget = false;
            return img;
        }

        private void RefreshToggleVisuals(bool force)
        {
            if (_toggleLabel == null)
            {
                return;
            }

            bool switching = Plugin.Switch != null && Plugin.Switch.IsSwitching;
            bool kugou = Plugin.Switch != null && Plugin.Switch.IsKugouMode;
            string key = (switching ? "S" : "R") + (kugou ? "K" : "O");
            if (!force && string.Equals(key, _lastStateKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastStateKey = key;

            try
            {
                if (_toggleButton != null)
                {
                    _toggleButton.interactable = !switching;
                }

                _toggleLabel.text = switching ? "切换中" : (kugou ? "酷狗音源" : "官方音源");
                if (_hasOriginalLabelColor)
                {
                    _toggleLabel.color = switching
                        ? KugouTheme.Colors.Warning
                        : (kugou ? KugouTextTint : _originalLabelColor);
                }

                if (_toggleStatusDot != null)
                {
                    _toggleStatusDot.color = switching
                        ? KugouTheme.Colors.Warning
                        : (kugou ? KugouTheme.Colors.Success : KugouTheme.Colors.TextTertiary);
                }
            }
            catch
            {
                // UI 重建瞬态忽略
            }
        }

        private static class FieldInfoCache
        {
            private static readonly Dictionary<string, FieldInfo> Cache =
                new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

            public static FieldInfo TryGetField(Type type, string name)
            {
                string key = type.FullName + "." + name;
                FieldInfo field;
                if (Cache.TryGetValue(key, out field))
                {
                    return field;
                }

                field = AccessTools.Field(type, name);
                Cache[key] = field;
                return field;
            }

            public static object GetValue(Type type, string name, object instance)
            {
                FieldInfo field = TryGetField(type, name);
                if (field == null || instance == null)
                {
                    return null;
                }

                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
