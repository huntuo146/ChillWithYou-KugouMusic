using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Theme
{
    internal enum TypeToken
    {
        Display = 0,     // 18 Bold
        Title = 1,       // 15 Bold
        Body = 2,        // 13 Regular
        BodyStrong = 3,  // 13 Bold
        Caption = 4,     // 12 Regular
        Micro = 5,       // 11 Bold
        Mono = 6,        // 12 Segoe UI
        Brand = 7,       // 14 Bold
    }

    internal enum IconCode
    {
        Play,
        Pause,
        Prev,
        Next,
        Search,
        Settings,
        Playlist,
        Account,
        Music,
        Lock,
        Unlock,
        Close,
        Minimize,
        Expand,
        Add,
        Delete,
        Refresh,
        Paste,
        Link,
        ChevronDown,
        ChevronUp,
        Check,
        Info,
        Warning,
        EyeOpen,
        EyeClose,
        Sparkle,
    }

    /// <summary>
    /// 「Midnight Desk Lo-Fi」设计系统：色板 Tokens、字号层级、动态 OS 字体、自建 GUISkin 与 DPI 缩放作用域。
    /// </summary>
    internal static class KugouTheme
    {
        internal static class Colors
        {
            public static readonly Color WindowBg = Hex(0x121829, 0.90f);
            public static readonly Color RailBg = Hex(0x0D1221, 0.55f);
            public static readonly Color HeaderWash = new Color(1f, 1f, 1f, 0.03f);
            public static readonly Color Card = Hex(0x1B2338, 0.72f);
            public static readonly Color CardHover = Hex(0x222B45, 0.82f);
            public static readonly Color CardActive = Hex(0x262449, 0.88f);
            public static readonly Color InputBg = Hex(0x0C1120, 0.72f);
            public static readonly Color Elevated = Hex(0x1E2640, 0.96f);
            public static readonly Color NowPlayingBg = Hex(0x0F1424, 0.92f);
            public static readonly Color OverlayHover = new Color(1f, 1f, 1f, 0.055f);
            public static readonly Color OverlayPress = new Color(1f, 1f, 1f, 0.095f);
            public static readonly Color Shadow = Hex(0x05070F, 0.55f);

            public static readonly Color StrokeHairline = Hex(0xCDDEFF, 0.16f);
            public static readonly Color StrokeStrong = Hex(0xCDDEFF, 0.28f);
            public static readonly Color StrokeHighlight = new Color(1f, 1f, 1f, 0.08f);
            public static readonly Color Divider = Hex(0xCDDEFF, 0.08f);
            public static readonly Color FocusRing = Hex(0x8E7CFF, 0.70f);
            public static readonly Color FocusGlow = Hex(0x8E7CFF, 0.18f);
            public static readonly Color LavenderStroke = Hex(0x8E7CFF, 0.55f);
            public static readonly Color LavenderGlow = Hex(0x8E7CFF, 0.35f);
            public static readonly Color NavActiveBg = Hex(0x8E7CFF, 0.14f);
            public static readonly Color ScrollThumb = Hex(0xCDDEFF, 0.20f);
            public static readonly Color ScrollThumbActive = Hex(0xCDDEFF, 0.36f);

            public static readonly Color TextPrimary = Hex(0xEEF2FF, 1.00f);
            public static readonly Color TextSecondary = Hex(0xAEB8D4, 1.00f);
            public static readonly Color TextTertiary = Hex(0x7A84A3, 1.00f);
            public static readonly Color TextDisabled = Hex(0x565E78, 1.00f);
            public static readonly Color TextOnAccent = Color.white;

            public static readonly Color AccentSky = Hex(0x57C7FF, 1.00f);
            public static readonly Color AccentLavender = Hex(0x8E7CFF, 1.00f);
            public static readonly Color AccentCoral = Hex(0xFF6F9E, 1.00f);

            public static readonly Color Success = Hex(0x4CE0B3, 1.00f);
            public static readonly Color Warning = Hex(0xFFB86C, 1.00f);
            public static readonly Color Error = Hex(0xFF6B7A, 1.00f);

            public static readonly Color DisabledFill = Hex(0x2A3150, 0.85f);
            public static readonly Color TrackEmpty = Hex(0xCDDEFF, 0.12f);

            public static readonly Color BadgeVipText = Hex(0xFFD27A, 1.00f);
            public static readonly Color BadgeVipBg = Hex(0xFFD27A, 0.14f);
            public static readonly Color BadgeVipBorder = Hex(0xFFD27A, 0.35f);

            public static readonly Color BadgePaidText = Hex(0xFF9DBC, 1.00f);
            public static readonly Color BadgePaidBg = Hex(0xFF6F9E, 0.14f);
            public static readonly Color BadgePaidBorder = Hex(0xFF6F9E, 0.32f);

            public static readonly Color BadgeTrialText = Hex(0xFFC98F, 1.00f);
            public static readonly Color BadgeTrialBg = Hex(0xFFB86C, 0.14f);
            public static readonly Color BadgeTrialBorder = Hex(0xFFB86C, 0.30f);

            public static readonly Color BadgeQualityText = Hex(0x9FDCFF, 1.00f);
            public static readonly Color BadgeQualityBg = Hex(0x57C7FF, 0.12f);
            public static readonly Color BadgeQualityBorder = Hex(0x57C7FF, 0.28f);

            public static readonly Color BadgeActiveText = Hex(0x4CE0B3, 1.00f);
            public static readonly Color BadgeActiveBg = Hex(0x4CE0B3, 0.12f);
            public static readonly Color BadgeActiveBorder = Hex(0x4CE0B3, 0.32f);
        }

        internal static class Sizes
        {
            public const float WindowWidth = 780f;
            public const float WindowHeight = 560f;
            public const float MiniWidth = 380f;
            public const float MiniHeight = 76f;
            public const float ShadowPad = 14f;

            public const float HeaderHeight = 56f;
            public const float RailWidth = 140f;
            public const float NowPlayingHeight = 68f;
            public const float ContentPadding = 20f;
            public const float CardPadding = 14f;
            public const float CardGap = 12f;

            public const float RadiusWindow = 16f;
            public const float RadiusCard = 12f;
            public const float RadiusInput = 10f;
            public const float RadiusBadge = 6f;
            public const float RadiusQr = 14f;
        }

        internal struct ScopeToken : IDisposable
        {
            private readonly GUISkin _prevSkin;
            private readonly Matrix4x4 _prevMatrix;
            private readonly Color _prevColor;
            private readonly float _prevScale;

            internal ScopeToken(float uiScale)
            {
                _prevSkin = GUI.skin;
                _prevMatrix = GUI.matrix;
                _prevColor = GUI.color;
                _prevScale = CurrentUiScale;

                EnsureBuilt();
                CurrentUiScale = uiScale;
                if (_skin != null)
                {
                    GUI.skin = _skin;
                }

                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));
            }

            public void Dispose()
            {
                GUI.skin = _prevSkin;
                GUI.matrix = _prevMatrix;
                GUI.color = _prevColor;
                CurrentUiScale = _prevScale;
            }
        }

        private static GUISkin _skin;
        private static Font _mainFont;
        private static Font _monoFont;
        private static Font _iconFont;
        private static bool _fontsLoaded;

        private static readonly GUIContent ReusableContent = new GUIContent();
        private static readonly Dictionary<long, GUIStyle> StyleCache = new Dictionary<long, GUIStyle>();
        private static readonly Dictionary<IconCode, string> ResolvedIconGlyph = new Dictionary<IconCode, string>();
        private static readonly Dictionary<IconCode, bool> ResolvedUseIconFont = new Dictionary<IconCode, bool>();

        private static Texture2D _transparentTex;
        private static Texture2D _scrollThumbTex;
        private static Texture2D _scrollThumbHoverTex;

        public static float CurrentUiScale { get; private set; } = 1f;
        public static Font MainFont { get { EnsureFonts(); return _mainFont; } }
        public static Font MonoFont { get { EnsureFonts(); return _monoFont != null ? _monoFont : _mainFont; } }
        public static Font IconFont { get { EnsureFonts(); return _iconFont; } }

        public static float ResolveUiScale()
        {
            float cfg = Plugin.UiScale != null ? Plugin.UiScale.Value : 0f;
            if (cfg > 0.1f)
            {
                return Mathf.Clamp(cfg, 0.85f, 1.6f);
            }

            float auto = Screen.height > 0 ? Screen.height / 1080f : 1f;
            return Mathf.Clamp(auto, 0.85f, 1.6f);
        }

        public static ScopeToken Scope(float uiScale)
        {
            return new ScopeToken(uiScale);
        }

        public static GUIContent Content(string text)
        {
            ReusableContent.text = text ?? string.Empty;
            ReusableContent.image = null;
            ReusableContent.tooltip = string.Empty;
            return ReusableContent;
        }

        public static Color Hex(int rgb, float alpha = 1f)
        {
            float r = ((rgb >> 16) & 0xFF) / 255f;
            float g = ((rgb >> 8) & 0xFF) / 255f;
            float b = (rgb & 0xFF) / 255f;
            return new Color(r, g, b, alpha);
        }

        public static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        public static void EnsureBuilt()
        {
            EnsureFonts();
            if (_skin != null)
            {
                return;
            }

            _transparentTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            _transparentTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            _transparentTex.Apply(false, true);
            _transparentTex.hideFlags = HideFlags.HideAndDontSave;

            _scrollThumbTex = TintMaskCopy(KugouTextures.ScrollThumbMask, Colors.ScrollThumb);
            _scrollThumbHoverTex = TintMaskCopy(KugouTextures.ScrollThumbMask, Colors.ScrollThumbActive);

            var skin = ScriptableObject.CreateInstance<GUISkin>();
            skin.hideFlags = HideFlags.HideAndDontSave;
            skin.font = _mainFont;

            skin.settings.cursorColor = Colors.AccentSky;
            skin.settings.cursorFlashSpeed = 1.8f;
            skin.settings.selectionColor = WithAlpha(Colors.AccentLavender, 0.35f);
            skin.settings.doubleClickSelectsWord = true;
            skin.settings.tripleClickSelectsLine = true;

            skin.window = new GUIStyle(GUIStyle.none) { padding = new RectOffset(), margin = new RectOffset() };
            skin.box = new GUIStyle(GUIStyle.none) { padding = new RectOffset(), margin = new RectOffset() };
            skin.scrollView = new GUIStyle(GUIStyle.none) { padding = new RectOffset(), margin = new RectOffset() };

            var label = new GUIStyle
            {
                name = "label",
                font = _mainFont,
                fontSize = 13,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            SetAllTextColors(label, Colors.TextPrimary);
            skin.label = label;

            var textField = new GUIStyle
            {
                name = "textField",
                font = _mainFont,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            SetAllBackgrounds(textField, _transparentTex);
            SetAllTextColors(textField, Colors.TextPrimary);
            skin.textField = textField;
            skin.textArea = new GUIStyle(textField) { name = "textArea", wordWrap = true };

            var button = new GUIStyle
            {
                name = "button",
                font = _mainFont,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            };
            SetAllBackgrounds(button, _transparentTex);
            SetAllTextColors(button, Colors.TextPrimary);
            skin.button = button;

            var vScroll = new GUIStyle
            {
                name = "kg_vscroll",
                fixedWidth = 6f,
                stretchHeight = true,
                padding = new RectOffset(0, 0, 2, 2),
                margin = new RectOffset(2, 0, 0, 0),
            };
            SetAllBackgrounds(vScroll, _transparentTex);

            var vThumb = new GUIStyle
            {
                name = "kg_vscrollthumb",
                fixedWidth = 6f,
                border = new RectOffset(0, 0, 3, 3),
            };
            SetAllBackgrounds(vThumb, _scrollThumbTex);
            vThumb.hover.background = _scrollThumbHoverTex;
            vThumb.active.background = _scrollThumbHoverTex;

            var vArrow = new GUIStyle
            {
                name = "kg_vscrollupbutton",
                fixedWidth = 6f,
                fixedHeight = 0f,
                padding = new RectOffset(),
                margin = new RectOffset(),
            };
            SetAllBackgrounds(vArrow, _transparentTex);
            var vArrowDown = new GUIStyle(vArrow) { name = "kg_vscrolldownbutton" };

            var hHidden = new GUIStyle
            {
                name = "kg_hscroll",
                fixedHeight = 0f,
                fixedWidth = 0f,
            };
            SetAllBackgrounds(hHidden, _transparentTex);
            var hThumb = new GUIStyle(hHidden) { name = "kg_hscrollthumb" };
            var hLeft = new GUIStyle(hHidden) { name = "kg_hscrollleftbutton" };
            var hRight = new GUIStyle(hHidden) { name = "kg_hscrollrightbutton" };

            skin.verticalScrollbar = vScroll;
            skin.verticalScrollbarThumb = vThumb;
            skin.verticalScrollbarUpButton = vArrow;
            skin.verticalScrollbarDownButton = vArrowDown;
            skin.horizontalScrollbar = hHidden;
            skin.horizontalScrollbarThumb = hThumb;
            skin.horizontalScrollbarLeftButton = hLeft;
            skin.horizontalScrollbarRightButton = hRight;

            skin.customStyles = new[]
            {
                vScroll, vThumb, vArrow, vArrowDown,
                hHidden, hThumb, hLeft, hRight,
            };

            _skin = skin;
        }

        public static GUIStyle Style(
            TypeToken token,
            Color color,
            TextAnchor align = TextAnchor.MiddleLeft,
            bool wordWrap = false)
        {
            EnsureFonts();

            int size;
            FontStyle fontStyle;
            Font font = _mainFont;
            ResolveTypeSpec(token, out size, out fontStyle, ref font);

            Color32 c32 = color;
            long key = ((long)token << 52) |
                       ((long)align << 46) |
                       ((wordWrap ? 1L : 0L) << 44) |
                       ((long)size << 36) |
                       ((long)c32.r << 24) |
                       ((long)c32.g << 16) |
                       ((long)c32.b << 8) |
                       c32.a;

            GUIStyle style;
            if (StyleCache.TryGetValue(key, out style))
            {
                return style;
            }

            style = new GUIStyle
            {
                font = font,
                fontSize = size,
                fontStyle = fontStyle,
                alignment = align,
                wordWrap = wordWrap,
                clipping = wordWrap ? TextClipping.Overflow : TextClipping.Clip,
                richText = false,
            };
            SetAllTextColors(style, color);
            StyleCache[key] = style;
            return style;
        }

        public static GUIStyle TextStyle(
            int fontSize,
            FontStyle fontStyle,
            Color color,
            TextAnchor align = TextAnchor.MiddleLeft,
            bool wordWrap = false,
            bool mono = false)
        {
            EnsureFonts();
            Color32 c32 = color;
            int clamped = Mathf.Clamp(fontSize, 9, 44);
            long key = (1L << 60) |
                       ((mono ? 1L : 0L) << 59) |
                       ((wordWrap ? 1L : 0L) << 58) |
                       ((long)fontStyle << 55) |
                       ((long)align << 49) |
                       ((long)clamped << 40) |
                       ((long)c32.r << 24) |
                       ((long)c32.g << 16) |
                       ((long)c32.b << 8) |
                       c32.a;

            GUIStyle style;
            if (StyleCache.TryGetValue(key, out style))
            {
                return style;
            }

            style = new GUIStyle
            {
                font = mono ? MonoFont : _mainFont,
                fontSize = clamped,
                fontStyle = fontStyle,
                alignment = align,
                wordWrap = wordWrap,
                clipping = wordWrap ? TextClipping.Overflow : TextClipping.Clip,
                richText = false,
            };
            SetAllTextColors(style, color);
            StyleCache[key] = style;
            return style;
        }

        public static GUIStyle IconStyle(int size, Color color, bool useIconFont, TextAnchor align = TextAnchor.MiddleCenter)
        {
            EnsureFonts();
            Color32 c32 = color;
            int clamped = Mathf.Clamp(size, 8, 36);
            long key = (2L << 60) |
                       ((useIconFont ? 1L : 0L) << 59) |
                       ((long)align << 52) |
                       ((long)clamped << 40) |
                       ((long)c32.r << 24) |
                       ((long)c32.g << 16) |
                       ((long)c32.b << 8) |
                       c32.a;

            GUIStyle style;
            if (StyleCache.TryGetValue(key, out style))
            {
                return style;
            }

            style = new GUIStyle
            {
                font = useIconFont && _iconFont != null ? _iconFont : _mainFont,
                fontSize = clamped,
                fontStyle = FontStyle.Normal,
                alignment = align,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            SetAllTextColors(style, color);
            StyleCache[key] = style;
            return style;
        }

        public static void ResolveIcon(IconCode code, out string glyph, out bool useIconFont)
        {
            EnsureFonts();
            if (ResolvedIconGlyph.TryGetValue(code, out glyph) &&
                ResolvedUseIconFont.TryGetValue(code, out useIconFont))
            {
                return;
            }

            string primary;
            string fallback;
            GetIconCandidates(code, out primary, out fallback);

            if (_iconFont != null && !string.IsNullOrEmpty(primary) && _iconFont.HasCharacter(primary[0]))
            {
                glyph = primary;
                useIconFont = true;
            }
            else
            {
                glyph = fallback;
                useIconFont = false;
            }

            ResolvedIconGlyph[code] = glyph;
            ResolvedUseIconFont[code] = useIconFont;
        }

        private static void GetIconCandidates(IconCode code, out string mdl2, out string fallback)
        {
            switch (code)
            {
                case IconCode.Play:        mdl2 = "\uE768"; fallback = "▶"; break;
                case IconCode.Pause:       mdl2 = "\uE769"; fallback = "❚❚"; break;
                case IconCode.Prev:        mdl2 = "\uE892"; fallback = "◀◀"; break;
                case IconCode.Next:        mdl2 = "\uE893"; fallback = "▶▶"; break;
                case IconCode.Search:      mdl2 = "\uE721"; fallback = "搜"; break;
                case IconCode.Settings:    mdl2 = "\uE713"; fallback = "设"; break;
                case IconCode.Playlist:    mdl2 = "\uE8FD"; fallback = "≡"; break;
                case IconCode.Account:     mdl2 = "\uE77B"; fallback = "◉"; break;
                case IconCode.Music:       mdl2 = "\uE8D6"; fallback = "♪"; break;
                case IconCode.Lock:        mdl2 = "\uE72E"; fallback = "锁"; break;
                case IconCode.Unlock:      mdl2 = "\uE785"; fallback = "开"; break;
                case IconCode.Close:       mdl2 = "\uE8BB"; fallback = "×"; break;
                case IconCode.Minimize:    mdl2 = "\uE921"; fallback = "—"; break;
                case IconCode.Expand:      mdl2 = "\uE740"; fallback = "⤢"; break;
                case IconCode.Add:         mdl2 = "\uE710"; fallback = "+"; break;
                case IconCode.Delete:      mdl2 = "\uE74D"; fallback = "×"; break;
                case IconCode.Refresh:     mdl2 = "\uE72C"; fallback = "↻"; break;
                case IconCode.Paste:       mdl2 = "\uE77F"; fallback = "粘"; break;
                case IconCode.Link:        mdl2 = "\uE71B"; fallback = "链"; break;
                case IconCode.ChevronDown: mdl2 = "\uE70D"; fallback = "▾"; break;
                case IconCode.ChevronUp:   mdl2 = "\uE70E"; fallback = "▴"; break;
                case IconCode.Check:       mdl2 = "\uE73E"; fallback = "✓"; break;
                case IconCode.Info:        mdl2 = "\uE946"; fallback = "i"; break;
                case IconCode.Warning:     mdl2 = "\uE7BA"; fallback = "!"; break;
                case IconCode.EyeOpen:     mdl2 = "\uE7B3"; fallback = "◎"; break;
                case IconCode.EyeClose:    mdl2 = "\uED1A"; fallback = "●"; break;
                case IconCode.Sparkle:     mdl2 = "\uE734"; fallback = "✦"; break;
                default:                   mdl2 = string.Empty; fallback = "•"; break;
            }
        }

        private static void ResolveTypeSpec(TypeToken token, out int size, out FontStyle fontStyle, ref Font font)
        {
            switch (token)
            {
                case TypeToken.Display:
                    size = 18;
                    fontStyle = FontStyle.Bold;
                    break;
                case TypeToken.Title:
                    size = 15;
                    fontStyle = FontStyle.Bold;
                    break;
                case TypeToken.BodyStrong:
                    size = 13;
                    fontStyle = FontStyle.Bold;
                    break;
                case TypeToken.Caption:
                    size = 12;
                    fontStyle = FontStyle.Normal;
                    break;
                case TypeToken.Micro:
                    size = 11;
                    fontStyle = FontStyle.Bold;
                    break;
                case TypeToken.Mono:
                    size = 12;
                    fontStyle = FontStyle.Normal;
                    font = _monoFont != null ? _monoFont : _mainFont;
                    break;
                case TypeToken.Brand:
                    size = 14;
                    fontStyle = FontStyle.Bold;
                    break;
                default:
                    size = 13;
                    fontStyle = FontStyle.Normal;
                    break;
            }
        }

        private static void EnsureFonts()
        {
            if (_fontsLoaded)
            {
                return;
            }

            _fontsLoaded = true;
            try
            {
                _mainFont = Font.CreateDynamicFontFromOSFont(
                    new[]
                    {
                        "Microsoft YaHei UI",
                        "Microsoft YaHei",
                        "微软雅黑",
                        "PingFang SC",
                        "Noto Sans CJK SC",
                        "SimHei",
                        "Arial Unicode MS",
                        "Arial",
                    },
                    13);
            }
            catch
            {
                _mainFont = null;
            }

            try
            {
                _monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Consolas", "Arial" }, 12);
            }
            catch
            {
                _monoFont = _mainFont;
            }

            try
            {
                _iconFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 16);
                if (_iconFont != null && !_iconFont.HasCharacter('\uE768'))
                {
                    _iconFont = null;
                }
            }
            catch
            {
                _iconFont = null;
            }
        }

        private static Texture2D TintMaskCopy(Texture2D sourceMask, Color tint)
        {
            // 用 GetRoundedFillMask 的像素构造（只用于 9x9 小图）或 4x4 胶囊
            var tex = new Texture2D(6, 8, TextureFormat.RGBA32, false, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.HideAndDontSave;

            var px = new Color32[48];
            const float cx = 2.5f;
            const float cy = 3.5f;
            const float rx = 2.5f;
            const float ry = 3.5f;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 6; x++)
                {
                    float dx = Mathf.Abs(x - cx);
                    float dy = Mathf.Max(0f, Mathf.Abs(y - cy) - (ry - rx));
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - rx;
                    float a = Mathf.Clamp01(0.5f - d);
                    Color col = tint;
                    col.a *= a;
                    px[y * 6 + x] = col;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static void SetAllBackgrounds(GUIStyle style, Texture2D tex)
        {
            style.normal.background = tex;
            style.onNormal.background = tex;
            style.hover.background = tex;
            style.onHover.background = tex;
            style.active.background = tex;
            style.onActive.background = tex;
            style.focused.background = tex;
            style.onFocused.background = tex;
        }

        private static void SetAllTextColors(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.onNormal.textColor = color;
            style.hover.textColor = color;
            style.onHover.textColor = color;
            style.active.textColor = color;
            style.onActive.textColor = color;
            style.focused.textColor = color;
            style.onFocused.textColor = color;
        }

        public static void Dispose()
        {
            StyleCache.Clear();
            ResolvedIconGlyph.Clear();
            ResolvedUseIconFont.Clear();

            if (_skin != null)
            {
                UnityEngine.Object.Destroy(_skin);
                _skin = null;
            }

            if (_transparentTex != null)
            {
                UnityEngine.Object.Destroy(_transparentTex);
                _transparentTex = null;
            }

            if (_scrollThumbTex != null)
            {
                UnityEngine.Object.Destroy(_scrollThumbTex);
                _scrollThumbTex = null;
            }

            if (_scrollThumbHoverTex != null)
            {
                UnityEngine.Object.Destroy(_scrollThumbHoverTex);
                _scrollThumbHoverTex = null;
            }
        }
    }
}
