using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Theme
{
    /// <summary>
    /// 双路径抗锯齿绘图原语：
    /// - 路径 A（首选）：Unity 内置 RoundedRect 着色器（分辨率无关、原生渐变胶囊/圆形封面）；
    /// - 路径 B（兜底）：SDF 9-Slice 蒙版 + GUI.color 着色，自动探测并在首次异常时无感切换。
    /// 仅在 Event.current.type == EventType.Repaint 时真正执行绘制。
    /// </summary>
    internal static class KugouPainter
    {
        private static bool _probed;
        private static bool _useShaderPath = true;
        private static readonly Dictionary<Texture2D, GUIStyle> NineSliceFillStyles = new Dictionary<Texture2D, GUIStyle>();
        private static readonly Dictionary<Texture2D, GUIStyle> NineSliceStrokeStyles = new Dictionary<Texture2D, GUIStyle>();
        private static GUIStyle _shadowStyle;
        private static int _lastShadowKey = -1;

        public static bool UseShaderPath
        {
            get
            {
                EnsureProbed();
                return _useShaderPath;
            }
        }

        private static bool IsRepaint
        {
            get { return Event.current != null && Event.current.type == EventType.Repaint; }
        }

        private static void EnsureProbed()
        {
            if (_probed)
            {
                return;
            }

            _probed = true;
            try
            {
                bool hasFill = Shader.Find("Hidden/Internal-GUIRoundedRect") != null;
                bool hasPerBorder = Shader.Find("Hidden/Internal-GUIRoundedRectWithColorPerBorder") != null;
                _useShaderPath = hasFill && hasPerBorder;
                if (!_useShaderPath && Plugin.Log != null)
                {
                    Plugin.Log.LogInfo("[UI] 内置圆角着色器不可用，启用 SDF 9-Slice 兜底路径 B");
                }
            }
            catch
            {
                _useShaderPath = false;
            }
        }

        private static void FallbackToPathB(Exception ex)
        {
            _useShaderPath = false;
            if (Plugin.Log != null)
            {
                Plugin.Log.LogInfo("[UI] 路径 A 运行时回退到 SDF 9-Slice 路径 B: " + ex.Message);
            }
        }

        public static void RoundRect(Rect r, Color color, float radius)
        {
            if (!IsRepaint || r.width <= 0.5f || r.height <= 0.5f || color.a <= 0.002f)
            {
                return;
            }

            float rad = ClampRadius(r, radius);
            if (rad <= 0.5f)
            {
                FillSolid(r, color);
                return;
            }

            EnsureProbed();
            if (_useShaderPath)
            {
                try
                {
                    GUI.DrawTexture(
                        r,
                        KugouTextures.White,
                        ScaleMode.StretchToFill,
                        true,
                        0f,
                        color,
                        Vector4.zero,
                        new Vector4(rad, rad, rad, rad));
                    return;
                }
                catch (Exception ex)
                {
                    FallbackToPathB(ex);
                }
            }

            int intR = Mathf.Max(1, Mathf.RoundToInt(rad));
            Texture2D mask = KugouTextures.GetRoundedFillMask(intR);
            DrawNineSlice(r, mask, intR + 1, color, NineSliceFillStyles);
        }

        public static void Stroke(Rect r, Color color, float radius, float width = 1f)
        {
            if (!IsRepaint || r.width <= 1f || r.height <= 1f || color.a <= 0.002f)
            {
                return;
            }

            float rad = ClampRadius(r, radius);
            EnsureProbed();
            if (_useShaderPath)
            {
                try
                {
                    GUI.DrawTexture(
                        r,
                        KugouTextures.White,
                        ScaleMode.StretchToFill,
                        true,
                        0f,
                        color,
                        width,
                        rad);
                    return;
                }
                catch (Exception ex)
                {
                    FallbackToPathB(ex);
                }
            }

            int intR = Mathf.Max(1, Mathf.RoundToInt(rad));
            Texture2D mask = KugouTextures.GetRoundedStrokeMask(intR, width);
            DrawNineSlice(r, mask, intR + 1, color, NineSliceStrokeStyles);
        }

        public static void GradientRect(Rect r, Texture2D gradientTex, float radius, Color tint, bool softFallback = false)
        {
            if (!IsRepaint || r.width <= 0.5f || r.height <= 0.5f || gradientTex == null || tint.a <= 0.002f)
            {
                return;
            }

            float rad = ClampRadius(r, radius);
            EnsureProbed();
            if (_useShaderPath)
            {
                try
                {
                    GUI.DrawTexture(
                        r,
                        gradientTex,
                        ScaleMode.StretchToFill,
                        true,
                        0f,
                        tint,
                        Vector4.zero,
                        new Vector4(rad, rad, rad, rad));
                    return;
                }
                catch (Exception ex)
                {
                    FallbackToPathB(ex);
                }
            }

            Texture2D capsule = KugouTextures.GetGradientCapsule(
                Mathf.RoundToInt(r.width),
                Mathf.RoundToInt(r.height),
                Mathf.RoundToInt(rad),
                softFallback);

            Color prev = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(r, capsule, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

        public static void GradientBorderCard(Rect r, float radius, Color fillColor, float borderWidth = 1.25f)
        {
            if (!IsRepaint)
            {
                return;
            }

            float rad = ClampRadius(r, radius);
            RoundRect(r, fillColor, rad);

            EnsureProbed();
            if (_useShaderPath)
            {
                try
                {
                    GUI.DrawTexture(
                        r,
                        KugouTextures.BrandGradient,
                        ScaleMode.StretchToFill,
                        true,
                        0f,
                        new Color(1f, 1f, 1f, 0.85f),
                        new Vector4(borderWidth, borderWidth, borderWidth, borderWidth),
                        new Vector4(rad, rad, rad, rad));
                    return;
                }
                catch (Exception ex)
                {
                    FallbackToPathB(ex);
                }
            }

            Stroke(r, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.72f), rad, borderWidth);
        }

        public static void Shadow(Rect r, float radius, Color shadowColor, int blur = 16, float offsetY = 5f)
        {
            if (!IsRepaint || shadowColor.a <= 0.002f)
            {
                return;
            }

            int intR = Mathf.Clamp(Mathf.RoundToInt(radius), 4, 48);
            int key = (intR << 16) | blur;
            if (_shadowStyle == null || _lastShadowKey != key)
            {
                Texture2D shadowTex = KugouTextures.GetShadowMask(intR, blur);
                int border = intR + blur;
                _shadowStyle = new GUIStyle
                {
                    border = new RectOffset(border, border, border, border),
                };
                _shadowStyle.normal.background = shadowTex;
                _lastShadowKey = key;
            }

            var sr = new Rect(
                r.x - blur,
                r.y - blur + offsetY,
                r.width + blur * 2f,
                r.height + blur * 2f);

            Color prev = GUI.color;
            GUI.color = shadowColor;
            _shadowStyle.Draw(sr, GUIContent.none, false, false, false, false);
            GUI.color = prev;
        }

        /// <summary>
        /// 五层模拟 Midnight Desk Lo-Fi 磨砂玻璃窗：
        /// 1) 柔和投影 -> 2) 深靛蓝玻璃基底 -> 3) 顶部微光泽 -> 4) 暮光径向氛围辉光 -> 5) 细噪点 + 1px 描边 + 顶部内高光。
        /// </summary>
        public static void GlassPanel(Rect r, float radius, bool withAmbientGlow = true, float baseAlpha = 0.90f, float shadowAlpha = 0.55f)
        {
            if (!IsRepaint)
            {
                return;
            }

            float rad = ClampRadius(r, radius);
            if (shadowAlpha > 0.01f)
            {
                Shadow(r, rad, KugouTheme.WithAlpha(KugouTheme.Colors.Shadow, shadowAlpha), 14, 5f);
            }

            RoundRect(r, KugouTheme.WithAlpha(KugouTheme.Colors.WindowBg, baseAlpha), rad);

            // 顶部高光渐变 & 暮光辉光（仅在窗体内留 1px 安全内缩，配合软边界不溢出圆角）
            if (withAmbientGlow)
            {
                var topBand = new Rect(r.x + 2f, r.y + 1f, Mathf.Max(0f, r.width - 4f), Mathf.Min(120f, r.height * 0.45f));
                DrawTex(topBand, KugouTextures.TopHighlight, Color.white);

                float glowSizeA = Mathf.Min(r.width * 0.72f, 360f);
                var glowA = new Rect(r.x - glowSizeA * 0.15f, r.y - glowSizeA * 0.25f, glowSizeA, glowSizeA);
                GUI.BeginGroup(r);
                var localA = new Rect(glowA.x - r.x, glowA.y - r.y, glowA.width, glowA.height);
                DrawTex(localA, KugouTextures.RadialGlow, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.09f));

                float glowSizeB = Mathf.Min(r.width * 0.60f, 280f);
                var localB = new Rect(r.width - glowSizeB * 0.65f, r.height - glowSizeB * 0.65f, glowSizeB, glowSizeB);
                DrawTex(localB, KugouTextures.RadialGlow, KugouTheme.WithAlpha(KugouTheme.Colors.AccentSky, 0.055f));
                GUI.EndGroup();
            }

            // 磨砂颗粒噪点：三段矩形避开圆角溢出
            DrawNoiseSafe(r, rad);

            // 顶部 1px 内高光 + 外框 1px 冷白发丝线
            var innerTop = new Rect(r.x + rad, r.y + 1f, Mathf.Max(0f, r.width - rad * 2f), 1f);
            FillSolid(innerTop, KugouTheme.Colors.StrokeHighlight);
            Stroke(r, KugouTheme.WithAlpha(KugouTheme.Colors.StrokeHairline, 0.19f), rad, 1f);
        }

        public static void Circle(Rect r, Color color)
        {
            if (!IsRepaint || r.width <= 0.5f || r.height <= 0.5f || color.a <= 0.002f)
            {
                return;
            }

            RoundRect(r, color, Mathf.Min(r.width, r.height) * 0.5f);
        }

        public static void Texture(Rect r, Texture tex, float radius, Color tint)
        {
            if (!IsRepaint || tex == null || r.width <= 0.5f || r.height <= 0.5f || tint.a <= 0.002f)
            {
                return;
            }

            float rad = ClampRadius(r, radius);
            if (rad <= 0.5f)
            {
                DrawTex(r, tex, tint);
                return;
            }

            EnsureProbed();
            if (_useShaderPath)
            {
                try
                {
                    GUI.DrawTexture(
                        r,
                        tex,
                        ScaleMode.ScaleAndCrop,
                        true,
                        0f,
                        tint,
                        Vector4.zero,
                        new Vector4(rad, rad, rad, rad));
                    return;
                }
                catch (Exception ex)
                {
                    FallbackToPathB(ex);
                }
            }

            DrawTex(r, tex, tint);
        }

        public static void RotatedTexture(Rect r, Texture tex, float angleDeg, Color tint)
        {
            if (!IsRepaint || tex == null || r.width <= 0.5f || r.height <= 0.5f)
            {
                return;
            }

            Matrix4x4 prevMatrix = GUI.matrix;
            Color prevColor = GUI.color;
            if (Mathf.Abs(angleDeg) > 0.01f)
            {
                GUIUtility.RotateAroundPivot(angleDeg, r.center);
            }

            GUI.color = tint;
            GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit, true);
            GUI.color = prevColor;
            GUI.matrix = prevMatrix;
        }

        public static void Divider(Rect r, float inset = 0f)
        {
            if (!IsRepaint || r.width <= inset * 2f + 1f)
            {
                return;
            }

            FillSolid(new Rect(r.x + inset, r.y, r.width - inset * 2f, 1f), KugouTheme.Colors.Divider);
        }

        public static void TextShadowed(
            Rect r,
            string text,
            GUIStyle style,
            Color color,
            bool eightWayOutline = false,
            float outlineAlpha = 0.35f)
        {
            if (string.IsNullOrEmpty(text) || style == null || !IsRepaint)
            {
                return;
            }

            GUIContent content = KugouTheme.Content(text);
            Color prev = style.normal.textColor;
            try
            {
                if (eightWayOutline)
                {
                    var outlineColor = new Color(0.04f, 0.055f, 0.10f, outlineAlpha * color.a);
                    SetTextColor(style, outlineColor);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            style.Draw(new Rect(r.x + dx, r.y + dy, r.width, r.height), content, false, false, false, false);
                        }
                    }
                }

                SetTextColor(style, new Color(0f, 0f, 0f, (eightWayOutline ? 0.55f : 0.65f) * color.a));
                style.Draw(new Rect(r.x, r.y + 1.5f, r.width, r.height), content, false, false, false, false);

                SetTextColor(style, color);
                style.Draw(r, content, false, false, false, false);
            }
            finally
            {
                SetTextColor(style, prev);
            }
        }

        public static void Icon(
            Rect r,
            IconCode code,
            int size,
            Color color,
            TextAnchor align = TextAnchor.MiddleCenter)
        {
            if (!IsRepaint || color.a <= 0.002f)
            {
                return;
            }

            string glyph;
            bool useIconFont;
            KugouTheme.ResolveIcon(code, out glyph, out useIconFont);
            GUIStyle st = KugouTheme.IconStyle(useIconFont ? size : Mathf.Max(11, size - 2), color, useIconFont, align);
            st.Draw(r, KugouTheme.Content(glyph), false, false, false, false);
        }

        /// <summary>「正在播放」3 根 Lo-Fi 律动条。</summary>
        public static void EqualizerBars(Rect r, bool playing)
        {
            if (!IsRepaint)
            {
                return;
            }

            float t = Time.unscaledTime;
            bool staticBars = !playing || KugouAnim.ReduceMotion;
            float[] omegas = { 5.1f, 6.7f, 4.3f };
            float[] phases = { 0.0f, 1.4f, 2.7f };
            float[] staticH = { 0.42f, 0.72f, 0.36f };

            float barW = 2f;
            float gap = 2f;
            float totalW = barW * 3f + gap * 2f;
            float startX = r.x + (r.width - totalW) * 0.5f;

            for (int i = 0; i < 3; i++)
            {
                float ratio = staticBars
                    ? (playing ? staticH[i] : 0.28f)
                    : 0.30f + 0.70f * Mathf.Abs(Mathf.Sin(t * omegas[i] + phases[i]));
                float h = Mathf.Max(3f, r.height * ratio);
                var br = new Rect(startX + i * (barW + gap), r.yMax - h, barW, h);
                Color col = KugouTextures.SampleBrandGradient(i * 0.5f);
                RoundRect(br, col, 1f);
            }
        }

        public static void Dispose()
        {
            NineSliceFillStyles.Clear();
            NineSliceStrokeStyles.Clear();
            _shadowStyle = null;
            _lastShadowKey = -1;
        }

        private static void DrawNoiseSafe(Rect r, float rad)
        {
            Texture2D noise = KugouTextures.NoiseTile;
            float inset = Mathf.Clamp(rad * 0.75f, 2f, r.width * 0.25f);
            if (r.height > inset * 2f + 2f)
            {
                var mid = new Rect(r.x + 1f, r.y + inset, r.width - 2f, r.height - inset * 2f);
                GUI.DrawTextureWithTexCoords(mid, noise, new Rect(0f, 0f, mid.width / 128f, mid.height / 128f), true);

                var topStrip = new Rect(r.x + inset, r.y + 1f, Mathf.Max(0f, r.width - inset * 2f), inset - 1f);
                if (topStrip.width > 2f && topStrip.height > 1f)
                {
                    GUI.DrawTextureWithTexCoords(topStrip, noise, new Rect(0f, 0f, topStrip.width / 128f, topStrip.height / 128f), true);
                }

                var botStrip = new Rect(r.x + inset, r.yMax - inset, Mathf.Max(0f, r.width - inset * 2f), inset - 1f);
                if (botStrip.width > 2f && botStrip.height > 1f)
                {
                    GUI.DrawTextureWithTexCoords(botStrip, noise, new Rect(0f, 0f, botStrip.width / 128f, botStrip.height / 128f), true);
                }
            }
        }

        private static void FillSolid(Rect r, Color color)
        {
            if (r.width <= 0.2f || r.height <= 0.2f || color.a <= 0.002f)
            {
                return;
            }

            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, KugouTextures.White, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

        private static void DrawTex(Rect r, Texture tex, Color color)
        {
            if (r.width <= 0.2f || r.height <= 0.2f || tex == null || color.a <= 0.002f)
            {
                return;
            }

            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

        private static void DrawNineSlice(
            Rect r,
            Texture2D mask,
            int border,
            Color color,
            Dictionary<Texture2D, GUIStyle> cache)
        {
            GUIStyle st;
            if (!cache.TryGetValue(mask, out st))
            {
                st = new GUIStyle
                {
                    border = new RectOffset(border, border, border, border),
                };
                st.normal.background = mask;
                cache[mask] = st;
            }

            Color prev = GUI.color;
            GUI.color = color;
            st.Draw(r, GUIContent.none, false, false, false, false);
            GUI.color = prev;
        }

        private static float ClampRadius(Rect r, float radius)
        {
            float maxR = Mathf.Min(r.width, r.height) * 0.5f;
            return Mathf.Clamp(radius, 0f, maxR);
        }

        private static void SetTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
        }
    }
}
