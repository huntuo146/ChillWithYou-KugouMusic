using System;
using System.Collections.Generic;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.UI.Theme;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ChillWithYou.KugouMusic.UI.Widgets
{
    internal enum ButtonKind
    {
        Primary = 0,
        Secondary = 1,
        Ghost = 2,
        Danger = 3,
        VipGold = 4,
    }

    internal struct TextFieldOptions
    {
        public string Placeholder;
        public IconCode? LeadingIcon;
        public bool AllowClear;
        public bool AllowPaste;
        public bool Masked;
        public bool RevealPlaintext;
        public bool Monospace;
    }

    /// <summary>
    /// 全套「Midnight Desk Lo-Fi」即时绘制组件库：
    /// 所有交互与绘制严格在逻辑 Rect 内完成，无系统默认灰框。
    /// </summary>
    internal static class KugouWidgets
    {
        private static readonly Dictionary<int, float> DangerConfirmUntil = new Dictionary<int, float>();
        private static readonly RectOffset ZeroOffset = new RectOffset(0, 0, 0, 0);
        private static string _activeInputName = string.Empty;
        private static GUIStyle _fieldNormalStyle;
        private static GUIStyle _fieldMonoStyle;
        private static GUIStyle _fieldMaskStyle;
        private static GUIStyle _fieldMaskMonoStyle;

        public static bool HasFocusedInput
        {
            get
            {
                if (GUIUtility.keyboardControl == 0)
                {
                    return false;
                }

                string focused = GUI.GetNameOfFocusedControl();
                return !string.IsNullOrEmpty(focused) && focused.StartsWith("kg_", StringComparison.Ordinal);
            }
        }

        public static bool IsHovered(Rect r)
        {
            return Event.current != null && r.Contains(Event.current.mousePosition);
        }

        public static bool Button(
            Rect r,
            string label,
            ButtonKind kind = ButtonKind.Secondary,
            bool enabled = true,
            IconCode? icon = null)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            bool hover = enabled && IsHovered(r);
            bool pressed = hover && Event.current != null &&
                           (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag);
            float h = KugouAnim.Hover(id, hover);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float rad = r.height * 0.5f;
                switch (kind)
                {
                    case ButtonKind.Primary:
                        if (enabled && h > 0.01f)
                        {
                            KugouPainter.Shadow(r, rad, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.32f * h), 8, 2f);
                        }

                        if (enabled)
                        {
                            KugouPainter.GradientRect(r, KugouTextures.BrandGradient, rad, Color.white);
                            if (h > 0.01f)
                            {
                                KugouPainter.RoundRect(r, new Color(1f, 1f, 1f, 0.11f * h), rad);
                            }

                            if (pressed)
                            {
                                KugouPainter.RoundRect(r, new Color(0f, 0f, 0f, 0.12f), rad);
                            }
                        }
                        else
                        {
                            KugouPainter.RoundRect(r, KugouTheme.Colors.DisabledFill, rad);
                        }
                        break;

                    case ButtonKind.Secondary:
                        KugouPainter.RoundRect(
                            r,
                            enabled
                                ? Color.Lerp(KugouTheme.Colors.Card, KugouTheme.Colors.CardHover, h)
                                : KugouTheme.WithAlpha(KugouTheme.Colors.DisabledFill, 0.5f),
                            rad);
                        KugouPainter.Stroke(
                            r,
                            enabled
                                ? Color.Lerp(KugouTheme.Colors.StrokeStrong, KugouTheme.Colors.LavenderStroke, h)
                                : KugouTheme.Colors.Divider,
                            rad,
                            1f);
                        break;

                    case ButtonKind.VipGold:
                        KugouPainter.RoundRect(
                            r,
                            Color.Lerp(KugouTheme.Colors.BadgeVipBg, KugouTheme.WithAlpha(KugouTheme.Colors.BadgeVipText, 0.22f), h),
                            rad);
                        KugouPainter.Stroke(
                            r,
                            Color.Lerp(KugouTheme.Colors.BadgeVipBorder, KugouTheme.Colors.BadgeVipText, h * 0.6f),
                            rad,
                            1f);
                        break;

                    case ButtonKind.Danger:
                        KugouPainter.RoundRect(
                            r,
                            KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.12f + 0.10f * h),
                            rad);
                        KugouPainter.Stroke(
                            r,
                            KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.35f + 0.35f * h),
                            rad,
                            1f);
                        break;

                    default: // Ghost
                        if (enabled && h > 0.01f)
                        {
                            KugouPainter.RoundRect(r, KugouTheme.WithAlpha(Color.white, 0.06f * h), rad);
                        }
                        break;
                }

                Color fg = ResolveButtonTextColor(kind, enabled, h);
                if (icon.HasValue)
                {
                    if (string.IsNullOrEmpty(label))
                    {
                        KugouPainter.Icon(r, icon.Value, 13, fg);
                    }
                    else
                    {
                        float iconW = 16f;
                        GUIStyle textSt = KugouTheme.Style(TypeToken.BodyStrong, fg, TextAnchor.MiddleLeft);
                        float textW = Mathf.Min(r.width - iconW - 14f, textSt.CalcSize(KugouTheme.Content(label)).x);
                        float contentW = iconW + 4f + textW;
                        float startX = r.x + Mathf.Max(8f, (r.width - contentW) * 0.5f);
                        KugouPainter.Icon(new Rect(startX, r.y, iconW, r.height), icon.Value, 13, fg);
                        textSt.Draw(
                            new Rect(startX + iconW + 4f, r.y, textW + 2f, r.height),
                            KugouTheme.Content(label),
                            false,
                            false,
                            false,
                            false);
                    }
                }
                else
                {
                    GUIStyle st = KugouTheme.Style(TypeToken.BodyStrong, fg, TextAnchor.MiddleCenter);
                    st.Draw(r, KugouTheme.Content(label), false, false, false, false);
                }
            }

            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        /// <summary>危险操作二次确认按钮：首次点击后 3 秒内变为高亮确认态，再点击才执行。</summary>
        public static bool DangerConfirmButton(
            Rect r,
            int keySeed,
            string label,
            string confirmLabel = "确认?")
        {
            float until;
            bool confirming = DangerConfirmUntil.TryGetValue(keySeed, out until) && Time.unscaledTime <= until;
            if (!confirming && until > 0f)
            {
                DangerConfirmUntil.Remove(keySeed);
            }

            if (confirming)
            {
                int id = GUIUtility.GetControlID(FocusType.Passive, r);
                float h = KugouAnim.Hover(id, IsHovered(r));
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    float rad = r.height * 0.5f;
                    KugouPainter.RoundRect(r, KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.85f + 0.12f * h), rad);
                    GUIStyle st = KugouTheme.Style(TypeToken.Micro, Color.white, TextAnchor.MiddleCenter);
                    st.Draw(r, KugouTheme.Content(confirmLabel), false, false, false, false);
                }

                if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                {
                    DangerConfirmUntil.Remove(keySeed);
                    return true;
                }

                return false;
            }

            if (Button(r, label, ButtonKind.Danger, true))
            {
                DangerConfirmUntil[keySeed] = Time.unscaledTime + 3.0f;
            }

            return false;
        }

        public static bool IconButton(
            Rect r,
            IconCode icon,
            bool active = false,
            bool enabled = true,
            int iconSize = 14,
            Color? activeTint = null)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            bool hover = enabled && IsHovered(r);
            float h = KugouAnim.Hover(id, hover);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float rad = Mathf.Min(r.width, r.height) * 0.5f;
                if (active)
                {
                    KugouPainter.GradientRect(r, KugouTextures.BrandGradientSoft, rad, Color.white, true);
                    KugouPainter.Stroke(r, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.45f), rad, 1f);
                }
                else if (enabled && h > 0.01f)
                {
                    KugouPainter.RoundRect(r, KugouTheme.WithAlpha(Color.white, 0.08f * h), rad);
                }

                Color fg = !enabled
                    ? KugouTheme.Colors.TextDisabled
                    : (active
                        ? (activeTint ?? KugouTheme.Colors.AccentSky)
                        : Color.Lerp(KugouTheme.Colors.TextSecondary, KugouTheme.Colors.TextPrimary, h));
                KugouPainter.Icon(r, icon, iconSize, fg);
            }

            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        public static int Segmented(
            Rect r,
            int selectedIndex,
            string[] labels,
            int animKeySeed,
            bool disabled = false,
            bool shimmer = false)
        {
            if (labels == null || labels.Length == 0)
            {
                return selectedIndex;
            }

            int count = labels.Length;
            int clamped = Mathf.Clamp(selectedIndex, 0, count - 1);
            float thumbAnim = KugouAnim.ValueLerp(animKeySeed, clamped, 18f);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float rad = r.height * 0.5f;
                KugouPainter.RoundRect(r, KugouTheme.Colors.InputBg, rad);
                KugouPainter.Stroke(r, KugouTheme.Colors.StrokeHairline, rad, 1f);

                float slotW = (r.width - 4f) / count;
                var thumbRect = new Rect(r.x + 2f + thumbAnim * slotW, r.y + 2f, slotW, r.height - 4f);
                float thumbRad = thumbRect.height * 0.5f;
                KugouPainter.GradientRect(thumbRect, KugouTextures.BrandGradientSoft, thumbRad, Color.white, true);
                KugouPainter.Stroke(thumbRect, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.48f), thumbRad, 1f);

                if (shimmer && !KugouAnim.ReduceMotion)
                {
                    float wave = Mathf.PingPong(Time.unscaledTime * 1.4f, 1f);
                    float sx = Mathf.Lerp(thumbRect.x, thumbRect.xMax - 18f, wave);
                    KugouPainter.RoundRect(
                        new Rect(sx, thumbRect.y + 2f, 18f, thumbRect.height - 4f),
                        new Color(1f, 1f, 1f, 0.16f),
                        thumbRad);
                }

                for (int i = 0; i < count; i++)
                {
                    var itemRect = new Rect(r.x + 2f + i * slotW, r.y, slotW, r.height);
                    bool selected = i == clamped;
                    Color tc = disabled
                        ? KugouTheme.Colors.TextDisabled
                        : (selected ? KugouTheme.Colors.TextPrimary : KugouTheme.Colors.TextTertiary);
                    GUIStyle st = KugouTheme.Style(selected ? TypeToken.BodyStrong : TypeToken.Caption, tc, TextAnchor.MiddleCenter);
                    st.Draw(itemRect, KugouTheme.Content(labels[i]), false, false, false, false);
                }
            }

            if (disabled)
            {
                return clamped;
            }

            float hitW = (r.width - 4f) / count;
            for (int i = 0; i < count; i++)
            {
                var hit = new Rect(r.x + 2f + i * hitW, r.y, hitW, r.height);
                if (GUI.Button(hit, GUIContent.none, GUIStyle.none))
                {
                    return i;
                }
            }

            return clamped;
        }

        public static bool Switch(Rect r, bool value, int animKeySeed)
        {
            var pill = new Rect(r.x, r.y + (r.height - 20f) * 0.5f, 36f, 20f);
            float t = KugouAnim.ValueLerp(animKeySeed, value ? 1f : 0f, 18f);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                if (t > 0.02f)
                {
                    KugouPainter.RoundRect(pill, KugouTheme.Colors.DisabledFill, 10f);
                    KugouPainter.GradientRect(pill, KugouTextures.BrandGradient, 10f, new Color(1f, 1f, 1f, t));
                }
                else
                {
                    KugouPainter.RoundRect(pill, KugouTheme.Colors.DisabledFill, 10f);
                    KugouPainter.Stroke(pill, KugouTheme.Colors.StrokeHairline, 10f, 1f);
                }

                float knobSize = 14f;
                float knobX = Mathf.Lerp(pill.x + 3f, pill.xMax - knobSize - 3f, t);
                var knob = new Rect(knobX, pill.y + 3f, knobSize, knobSize);
                KugouPainter.Circle(new Rect(knob.x, knob.y + 1f, knobSize, knobSize), new Color(0f, 0f, 0f, 0.25f));
                KugouPainter.Circle(knob, Color.Lerp(KugouTheme.Colors.TextSecondary, Color.white, t));
            }

            if (GUI.Button(pill, GUIContent.none, GUIStyle.none))
            {
                return !value;
            }

            return value;
        }

        public static int Stepper(Rect r, int value, int min, int max, int step, string suffix = "")
        {
            var minusRect = new Rect(r.x, r.y + (r.height - 26f) * 0.5f, 26f, 26f);
            var plusRect = new Rect(r.xMax - 26f, r.y + (r.height - 26f) * 0.5f, 26f, 26f);
            var valRect = new Rect(minusRect.xMax + 4f, r.y, Mathf.Max(20f, plusRect.x - minusRect.xMax - 8f), r.height);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(valRect, KugouTheme.Colors.InputBg, 8f);
                KugouPainter.Stroke(valRect, KugouTheme.Colors.Divider, 8f, 1f);
                GUIStyle st = KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleCenter);
                st.Draw(valRect, KugouTheme.Content(value + suffix), false, false, false, false);
            }

            if (IconButton(minusRect, IconCode.Minimize, false, value > min, 11))
            {
                return Mathf.Clamp(value - step, min, max);
            }

            if (IconButton(plusRect, IconCode.Add, false, value < max, 11))
            {
                return Mathf.Clamp(value + step, min, max);
            }

            return value;
        }

        public static float FloatStepper(Rect r, float value, float min, float max, float step, string format = "+0.0;-0.0;0.0", string suffix = "s")
        {
            var minusRect = new Rect(r.x, r.y + (r.height - 26f) * 0.5f, 26f, 26f);
            var plusRect = new Rect(r.xMax - 26f, r.y + (r.height - 26f) * 0.5f, 26f, 26f);
            var valRect = new Rect(minusRect.xMax + 4f, r.y, Mathf.Max(20f, plusRect.x - minusRect.xMax - 8f), r.height);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(valRect, KugouTheme.Colors.InputBg, 8f);
                KugouPainter.Stroke(valRect, KugouTheme.Colors.Divider, 8f, 1f);
                GUIStyle st = KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.AccentSky, TextAnchor.MiddleCenter);
                st.Draw(valRect, KugouTheme.Content(value.ToString(format) + suffix), false, false, false, false);
            }

            if (IconButton(minusRect, IconCode.Minimize, false, value > min + 0.001f, 11))
            {
                return Mathf.Clamp((float)Math.Round(value - step, 2), min, max);
            }

            if (IconButton(plusRect, IconCode.Add, false, value < max - 0.001f, 11))
            {
                return Mathf.Clamp((float)Math.Round(value + step, 2), min, max);
            }

            return value;
        }

        public static string TextField(
            Rect r,
            string controlName,
            string value,
            TextFieldOptions options,
            out bool submitted)
        {
            submitted = false;
            string current = value ?? string.Empty;
            string fullCtrlName = controlName.StartsWith("kg_", StringComparison.Ordinal) ? controlName : "kg_" + controlName;
            bool focused = string.Equals(GUI.GetNameOfFocusedControl(), fullCtrlName, StringComparison.Ordinal);

            if (focused)
            {
                _activeInputName = fullCtrlName;
            }
            else if (string.Equals(_activeInputName, fullCtrlName, StringComparison.Ordinal))
            {
                _activeInputName = string.Empty;
            }

            bool hasPaste = options.AllowPaste && !options.Masked;
            bool showPaste = hasPaste && !string.IsNullOrEmpty(GUIUtility.systemCopyBuffer);
            bool showClear = options.AllowClear && !string.IsNullOrEmpty(current);

            float rightPad = 8f;
            float actionW = 22f;
            float rightActionsWidth = 0f;
            if (showClear) rightActionsWidth += actionW + 2f;
            if (showPaste) rightActionsWidth += actionW + 2f;

            float leftPad = options.LeadingIcon.HasValue ? 30f : 10f;
            var textRect = new Rect(
                r.x + leftPad,
                r.y + 4f,
                Mathf.Max(20f, r.width - leftPad - rightPad - rightActionsWidth),
                r.height - 8f);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float rad = KugouTheme.Sizes.RadiusInput;
                if (focused)
                {
                    KugouPainter.Stroke(
                        new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f),
                        KugouTheme.Colors.FocusGlow,
                        rad + 1f,
                        2f);
                }

                KugouPainter.RoundRect(r, KugouTheme.Colors.InputBg, rad);
                KugouPainter.Stroke(
                    r,
                    focused ? KugouTheme.Colors.FocusRing : (IsHovered(r) ? KugouTheme.Colors.StrokeStrong : KugouTheme.Colors.StrokeHairline),
                    rad,
                    focused ? 1.3f : 1f);

                if (options.LeadingIcon.HasValue)
                {
                    KugouPainter.Icon(
                        new Rect(r.x + 8f, r.y, 18f, r.height),
                        options.LeadingIcon.Value,
                        13,
                        focused ? KugouTheme.Colors.AccentSky : KugouTheme.Colors.TextTertiary);
                }

                if (string.IsNullOrEmpty(current) && !focused && !string.IsNullOrEmpty(options.Placeholder))
                {
                    GUIStyle ph = KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft);
                    ph.Draw(textRect, KugouTheme.Content(options.Placeholder), false, false, false, false);
                }
            }

            bool maskPreview = options.Masked && !focused && !options.RevealPlaintext && !string.IsNullOrEmpty(current);
            bool imeComposing = !string.IsNullOrEmpty(Input.compositionString);
            if (focused && Event.current != null && Event.current.type == EventType.KeyDown && !imeComposing)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    submitted = true;
                    Event.current.Use();
                }
                else if (Event.current.keyCode == KeyCode.Escape)
                {
                    GUI.FocusControl(null);
                    Event.current.Use();
                }
            }

            if (Event.current != null &&
                Event.current.type == EventType.MouseDown &&
                r.Contains(Event.current.mousePosition))
            {
                ClearUguiSelection();
            }

            // 先渲染并分配 TextField 的 ControlID：否则右侧“清空/粘贴”按钮随文本是否为空动态显隐，
            // 会在输入第 1 个字符后挤压后移 TextField 的 ControlID，导致 keyboardControl 立即脱焦。
            GUIStyle fieldStyle = GetFieldStyle(options.Monospace, maskPreview);
            GUI.SetNextControlName(fullCtrlName);
            string next = GUI.TextField(textRect, current, fieldStyle);

            if (maskPreview && Event.current != null && Event.current.type == EventType.Repaint)
            {
                string maskedText = BuildMaskedPreview(current);
                GUIStyle maskedSt = KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft);
                maskedSt.Draw(textRect, KugouTheme.Content(maskedText), false, false, false, false);
            }

            // 右侧小按钮放在 TextField 之后绘制（textRect 已为 rightActionsWidth 预留宽度，不重叠）
            bool modifiedByAction = false;
            float cursorX = r.xMax - 6f;
            if (showClear)
            {
                cursorX -= actionW;
                if (IconButton(new Rect(cursorX, r.y + (r.height - 22f) * 0.5f, actionW, 22f), IconCode.Close, false, true, 11))
                {
                    next = string.Empty;
                    modifiedByAction = true;
                    GUIUtility.keyboardControl = 0;
                    GUI.FocusControl(fullCtrlName);
                    GUI.changed = true;
                }

                cursorX -= 2f;
            }

            if (showPaste)
            {
                cursorX -= actionW;
                if (IconButton(new Rect(cursorX, r.y + (r.height - 22f) * 0.5f, actionW, 22f), IconCode.Paste, false, true, 11))
                {
                    string clip = GUIUtility.systemCopyBuffer;
                    if (!string.IsNullOrEmpty(clip))
                    {
                        next = clip.Trim();
                        modifiedByAction = true;
                        GUIUtility.keyboardControl = 0;
                        GUI.FocusControl(fullCtrlName);
                        GUI.changed = true;
                    }
                }
            }

            return (maskPreview && !modifiedByAction) ? current : next;
        }

        private static GUIStyle GetFieldStyle(bool monospace, bool hiddenText)
        {
            GUIStyle baseStyle = monospace
                ? KugouTheme.TextStyle(12, FontStyle.Normal, hiddenText ? Color.clear : KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft, false, true)
                : KugouTheme.TextStyle(13, FontStyle.Normal, hiddenText ? Color.clear : KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft, false, false);

            GUIStyle cached = hiddenText
                ? (monospace ? _fieldMaskMonoStyle : _fieldMaskStyle)
                : (monospace ? _fieldMonoStyle : _fieldNormalStyle);

            if (cached == null || cached.font != baseStyle.font)
            {
                cached = new GUIStyle(baseStyle)
                {
                    padding = ZeroOffset,
                    margin = ZeroOffset,
                };
                if (hiddenText)
                {
                    if (monospace) _fieldMaskMonoStyle = cached;
                    else _fieldMaskStyle = cached;
                }
                else
                {
                    if (monospace) _fieldMonoStyle = cached;
                    else _fieldNormalStyle = cached;
                }
            }

            return cached;
        }

        private static void ClearUguiSelection()
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null)
                {
                    es.SetSelectedGameObject(null);
                }
            }
            catch
            {
            }
        }

        public static float Badge(Rect r, string text, Color fg, Color bg, Color border, bool dot = false)
        {
            GUIStyle st = KugouTheme.Style(TypeToken.Micro, fg, TextAnchor.MiddleCenter);
            float tw = st.CalcSize(KugouTheme.Content(text)).x;
            float w = Mathf.Min(r.width, tw + (dot ? 22f : 14f));
            var br = new Rect(r.x, r.y + (r.height - 18f) * 0.5f, w, 18f);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(br, bg, KugouTheme.Sizes.RadiusBadge);
                if (border.a > 0.01f)
                {
                    KugouPainter.Stroke(br, border, KugouTheme.Sizes.RadiusBadge, 1f);
                }

                if (dot)
                {
                    StatusDot(new Rect(br.x + 6f, br.y + 6f, 6f, 6f), fg, true);
                    var tr = new Rect(br.x + 14f, br.y, br.width - 18f, br.height);
                    KugouTheme.Style(TypeToken.Micro, fg, TextAnchor.MiddleLeft)
                              .Draw(tr, KugouTheme.Content(text), false, false, false, false);
                }
                else
                {
                    st.Draw(br, KugouTheme.Content(text), false, false, false, false);
                }
            }

            return w;
        }

        public static void StatusDot(Rect r, Color color, bool pulsing = false)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (pulsing && !KugouAnim.ReduceMotion)
            {
                float p = KugouAnim.Pulse(1.6f);
                float pad = 2f + p * 1.5f;
                KugouPainter.Circle(
                    new Rect(r.x - pad, r.y - pad, r.width + pad * 2f, r.height + pad * 2f),
                    KugouTheme.WithAlpha(color, 0.14f + 0.14f * p));
            }

            KugouPainter.Circle(r, color);
        }

        public static void CardFrame(Rect r, bool hovered = false, bool active = false)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            float rad = KugouTheme.Sizes.RadiusCard;
            if (active)
            {
                KugouPainter.GradientBorderCard(r, rad, KugouTheme.Colors.CardActive, 1.3f);
            }
            else
            {
                KugouPainter.RoundRect(r, hovered ? KugouTheme.Colors.CardHover : KugouTheme.Colors.Card, rad);
                KugouPainter.Stroke(r, hovered ? KugouTheme.Colors.StrokeStrong : KugouTheme.Colors.StrokeHairline, rad, 1f);
            }

            // 顶部 1px 柔和内高光
            KugouPainter.Stroke(
                new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 2f),
                KugouTheme.WithAlpha(Color.white, 0.05f),
                0f,
                1f);
        }

        public static void ProgressBar(Rect r, float ratio, bool hovered = false, bool warning = false)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            float p = Mathf.Clamp01(ratio);
            float rad = r.height * 0.5f;
            KugouPainter.RoundRect(r, KugouTheme.Colors.TrackEmpty, rad);

            if (p > 0.005f)
            {
                float fillW = Mathf.Max(r.height, r.width * p);
                var fr = new Rect(r.x, r.y, fillW, r.height);
                if (warning)
                {
                    KugouPainter.RoundRect(fr, KugouTheme.Colors.Warning, rad);
                }
                else
                {
                    KugouPainter.GradientRect(fr, KugouTextures.BrandGradient, rad, Color.white);
                }

                if (hovered && !warning)
                {
                    var knob = new Rect(fr.xMax - 4f, r.y + r.height * 0.5f - 4f, 8f, 8f);
                    KugouPainter.Circle(knob, Color.white);
                }
            }
        }

        public static void DrawCover(
            Rect r,
            CoverCache cache,
            string coverUrl,
            float radius,
            string seedName,
            string monogramChar,
            bool useVinylFallback = false,
            float vinylAngle = 0f,
            KugouTrack trackForAutoResolve = null,
            int coverSize = 150)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            bool isLoading = false;
            Texture2D tex = null;
            if (cache != null)
            {
                if (trackForAutoResolve != null)
                {
                    tex = cache.GetOrRequestTrackCover(trackForAutoResolve, coverSize, out isLoading);
                }
                else if (!string.IsNullOrEmpty(coverUrl))
                {
                    tex = cache.GetOrRequest(coverUrl, coverSize, out isLoading);
                }
            }

            if (tex != null)
            {
                KugouPainter.Texture(r, tex, radius, Color.white);
                KugouPainter.Stroke(r, KugouTheme.WithAlpha(Color.white, 0.16f), radius, 1f);
                return;
            }

            if (useVinylFallback)
            {
                KugouPainter.RotatedTexture(r, KugouTextures.VinylDisc, vinylAngle, Color.white);
                return;
            }

            Texture2D backdrop = KugouTextures.GetMonogramBackdrop(seedName);
            KugouPainter.Texture(r, backdrop, radius, Color.white);

            if (isLoading && !KugouAnim.ReduceMotion)
            {
                float sweep = Mathf.PingPong(Time.unscaledTime * 1.1f, 1f);
                float alpha = 0.05f + 0.08f * sweep;
                KugouPainter.RoundRect(r, new Color(1f, 1f, 1f, alpha), radius);
            }

            if (!string.IsNullOrEmpty(monogramChar))
            {
                int fontSz = r.height >= 48f ? 18 : 14;
                GUIStyle st = KugouTheme.TextStyle(fontSz, FontStyle.Bold, new Color(1f, 1f, 1f, 0.92f), TextAnchor.MiddleCenter);
                st.Draw(r, KugouTheme.Content(monogramChar), false, false, false, false);
            }
            else
            {
                KugouPainter.Icon(r, IconCode.Music, 16, new Color(1f, 1f, 1f, 0.88f));
            }

            KugouPainter.Stroke(r, KugouTheme.WithAlpha(Color.white, 0.16f), radius, 1f);
        }

        public static void SkeletonRow(Rect r, float timeSeed)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            float pulse = KugouAnim.ReduceMotion ? 0.5f : (0.5f + 0.5f * Mathf.Sin((Time.unscaledTime + timeSeed) * 3.2f));
            Color col = new Color(0.80f, 0.87f, 1f, Mathf.Lerp(0.05f, 0.11f, pulse));
            KugouPainter.RoundRect(new Rect(r.x, r.y + 8f, 36f, 36f), col, 8f);
            KugouPainter.RoundRect(new Rect(r.x + 48f, r.y + 11f, r.width * 0.42f, 13f), col, 6f);
            KugouPainter.RoundRect(new Rect(r.x + 48f, r.y + 30f, r.width * 0.26f, 10f), col, 5f);
        }

        public static Vector2 BeginScroll(Rect viewRect, Vector2 scroll, Rect contentRect)
        {
            return GUI.BeginScrollView(viewRect, scroll, contentRect, false, false);
        }

        public static void EndScroll()
        {
            GUI.EndScrollView();
        }

        public static string MaskUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return string.Empty;
            }

            string s = userId.Trim();
            if (s.Length <= 4)
            {
                return new string('•', s.Length);
            }

            return s.Substring(0, 2) + "••••" + s.Substring(s.Length - 2);
        }

        public static string FirstMonogram(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "K";
            }

            string trimmed = name.Trim();
            return trimmed.Length > 0 ? trimmed.Substring(0, 1).ToUpperInvariant() : "K";
        }

        private static string BuildMaskedPreview(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Length <= 16)
            {
                return new string('•', Mathf.Max(4, value.Length - 4)) +
                       (value.Length > 4 ? value.Substring(value.Length - 4) : string.Empty);
            }

            return "•••••••••••• (" + value.Length + " 字符)";
        }

        private static Color ResolveButtonTextColor(ButtonKind kind, bool enabled, float hover)
        {
            if (!enabled)
            {
                return KugouTheme.Colors.TextDisabled;
            }

            switch (kind)
            {
                case ButtonKind.Primary:
                    return KugouTheme.Colors.TextOnAccent;
                case ButtonKind.VipGold:
                    return KugouTheme.Colors.BadgeVipText;
                case ButtonKind.Danger:
                    return KugouTheme.Colors.Error;
                case ButtonKind.Ghost:
                    return Color.Lerp(KugouTheme.Colors.TextSecondary, KugouTheme.Colors.TextPrimary, hover);
                default:
                    return Color.Lerp(KugouTheme.Colors.TextSecondary, KugouTheme.Colors.TextPrimary, hover);
            }
        }
    }
}
