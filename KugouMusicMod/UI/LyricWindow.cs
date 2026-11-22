using System;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using KanKikuchi.AudioManager;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI
{
    /// <summary>
    /// 「Midnight Desk Lo-Fi」桌面悬浮歌词框：
    /// - 三种底衬自适应（全透明八向柔描边 / 羽化台灯柔雾 / 磨砂玻璃）；
    /// - 260ms 换行微动效 + 居中像素精确的逐行青蓝染色进度；
    /// - 悬停淡入玻璃胶囊控制条，锁定后自动让出点击穿透（仅右上解锁热区可交互）。
    /// </summary>
    internal sealed class LyricWindow
    {
        private const int WindowId = 0x4B474C; // 'KGL'
        private const float Pad = 12f;
        private const float MinWidth = 320f;
        private const float MaxWidth = 960f;
        private const float TopBarReserve = 34f;

        private Rect _rect;
        private bool _initialized;
        private float _lastVisX = float.NaN;
        private float _lastVisY = float.NaN;
        private bool _positionDirty;

        private float _hoverAlpha;
        private string _lockedHintText = string.Empty;

        public void Initialize()
        {
            float scale = KugouTheme.ResolveUiScale();
            float logicalScreenW = Mathf.Max(400f, Screen.width / scale);
            float logicalScreenH = Mathf.Max(300f, Screen.height / scale);

            float width = ResolveWidth(logicalScreenW);
            float height = ResolveHeight(ResolveFontSize());

            int cfgX = Plugin.LyricsPosX != null ? Plugin.LyricsPosX.Value : -1;
            int cfgY = Plugin.LyricsPosY != null ? Plugin.LyricsPosY.Value : -1;

            float x = cfgX >= 0 ? cfgX : Mathf.Max(20f, (logicalScreenW - width) * 0.5f);
            float y = cfgY >= 0 ? cfgY : Mathf.Max(20f, logicalScreenH - height - 116f);

            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, logicalScreenW - width));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, logicalScreenH - height));

            _rect = new Rect(x - Pad, y - Pad, width + Pad * 2f, height + Pad * 2f);
            _lastVisX = x;
            _lastVisY = y;
            _initialized = true;
        }

        public void ResetPosition()
        {
            float scale = KugouTheme.ResolveUiScale();
            float logicalScreenW = Mathf.Max(400f, Screen.width / scale);
            float logicalScreenH = Mathf.Max(300f, Screen.height / scale);

            float width = ResolveWidth(logicalScreenW);
            float height = ResolveHeight(ResolveFontSize());
            float x = Mathf.Max(20f, (logicalScreenW - width) * 0.5f);
            float y = Mathf.Max(20f, logicalScreenH - height - 116f);

            _rect = new Rect(x - Pad, y - Pad, width + Pad * 2f, height + Pad * 2f);
            _lastVisX = x;
            _lastVisY = y;
            SavePosition(x, y);
        }

        public void Draw()
        {
            if (Plugin.EnableLyricsWindow == null || !Plugin.EnableLyricsWindow.Value)
            {
                return;
            }

            if (!_initialized)
            {
                Initialize();
            }

            float scale = KugouTheme.ResolveUiScale();
            float logicalScreenW = Mathf.Max(400f, Screen.width / scale);
            float logicalScreenH = Mathf.Max(300f, Screen.height / scale);

            int fontSize = ResolveFontSize();
            float vw = ResolveWidth(logicalScreenW);
            float vh = ResolveHeight(fontSize);

            float visX = Mathf.Clamp(_rect.x + Pad, 0f, Mathf.Max(0f, logicalScreenW - vw));
            float visY = Mathf.Clamp(_rect.y + Pad, 0f, Mathf.Max(0f, logicalScreenH - vh));
            _rect = new Rect(visX - Pad, visY - Pad, vw + Pad * 2f, vh + Pad * 2f);

            _rect = GUI.Window(WindowId, _rect, DrawWindowContents, string.Empty, GUIStyle.none);
            HandlePositionPersistence();
            SyncInputBlocker(scale);
        }

        public void SyncInputBlocker(float uiScale)
        {
            KugouInputBlocker blocker = Plugin.Window != null ? Plugin.Window.InputBlocker : null;
            if (blocker == null)
            {
                return;
            }

            bool enabled = Plugin.EnableLyricsWindow != null && Plugin.EnableLyricsWindow.Value;
            if (!enabled || !_initialized)
            {
                blocker.SetSlot(KugouInputBlocker.Slot.Lyric, Rect.zero, uiScale, false);
                return;
            }

            float vw = _rect.width - Pad * 2f;
            float vh = _rect.height - Pad * 2f;
            var visualScreen = new Rect(_rect.x + Pad, _rect.y + Pad, vw, vh);
            bool locked = Plugin.LyricsLocked != null && Plugin.LyricsLocked.Value;

            if (!locked)
            {
                blocker.SetSlot(KugouInputBlocker.Slot.Lyric, visualScreen, uiScale, true);
                return;
            }

            // 锁定时整条穿透，仅当光标落到右上角解锁按钮热区时才局部拦住点击以完成解锁
            var lockScreenRect = new Rect(visualScreen.xMax - 32f, visualScreen.y + 2f, 28f, 26f);
            bool hoverLock = IsMouseInScreenLogicalRect(lockScreenRect, uiScale);
            blocker.SetSlot(KugouInputBlocker.Slot.Lyric, lockScreenRect, uiScale, hoverLock);
        }

        private void DrawWindowContents(int id)
        {
            float vw = _rect.width - Pad * 2f;
            float vh = _rect.height - Pad * 2f;
            var card = new Rect(Pad, Pad, vw, vh);

            bool locked = Plugin.LyricsLocked != null && Plugin.LyricsLocked.Value;
            bool mouseInsideCard = Event.current != null && card.Contains(Event.current.mousePosition);
            var lockHotspot = new Rect(card.xMax - 32f, card.y + 2f, 26f, 26f);
            bool hoverLockCorner = Event.current != null && lockHotspot.Contains(Event.current.mousePosition);

            float targetHover = (!locked && mouseInsideCard) ? 1f : 0f;
            if (Event.current == null || Event.current.type == EventType.Repaint)
            {
                float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
                _hoverAlpha = KugouAnim.ReduceMotion
                    ? targetHover
                    : Mathf.Lerp(_hoverAlpha, targetHover, 1f - Mathf.Exp(-14f * dt));
            }

            float alpha = ResolveAlpha();
            int fontSize = ResolveFontSize();
            bool eightWayOutline = alpha < 0.65f;

            // 底衬绘制
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                if (alpha >= 0.65f)
                {
                    KugouPainter.GlassPanel(card, 14f, false, alpha, 0.36f);
                }
                else if (alpha >= 0.05f)
                {
                    Color prev = GUI.color;
                    GUI.color = KugouTheme.WithAlpha(KugouTheme.Hex(0x0E1324), alpha);
                    GUI.DrawTexture(card, KugouTextures.FeatherVeil, ScaleMode.StretchToFill, true);
                    GUI.color = prev;
                }

                if (!locked && _hoverAlpha > 0.02f)
                {
                    KugouPainter.Stroke(
                        card,
                        KugouTheme.WithAlpha(KugouTheme.Colors.StrokeHairline, 0.24f * _hoverAlpha),
                        14f,
                        1f);
                }
            }

            // 锁定时仅右上角出现轻量解锁按钮；未锁定时悬停显示居中玻璃胶囊控制条
            if (locked)
            {
                if (hoverLockCorner || mouseInsideCard)
                {
                    Color prev = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, hoverLockCorner ? 0.92f : 0.38f);
                    if (KugouWidgets.IconButton(lockHotspot, IconCode.Lock, true, true, 12, KugouTheme.Colors.AccentSky))
                    {
                        ToggleLock(false);
                    }

                    GUI.color = prev;
                }
            }
            else if (_hoverAlpha > 0.03f)
            {
                DrawHoverPillBar(card, _hoverAlpha);
            }

            // 歌词文本区（未悬停时整体略上移居于底衬正中，悬停时下让位给胶囊控制条）
            float topBarOffset = Mathf.Lerp(18f, TopBarReserve, _hoverAlpha);
            DrawLyricsBody(card, fontSize, eightWayOutline, alpha, topBarOffset);

            if (!locked)
            {
                GUI.DragWindow(card);
            }
        }

        private void DrawHoverPillBar(Rect card, float fade)
        {
            string title = Plugin.Lyrics != null ? Plugin.Lyrics.TrackTitle : string.Empty;
            string singer = Plugin.Lyrics != null ? Plugin.Lyrics.TrackSinger : string.Empty;
            string caption = string.IsNullOrEmpty(title)
                ? "拖动调整歌词位置"
                : "♪ " + title + (string.IsNullOrEmpty(singer) ? string.Empty : " - " + singer);

            float pillW = Mathf.Clamp(card.width - 28f, 290f, 480f);
            var pill = new Rect(card.center.x - pillW * 0.5f, card.y + 4f, pillW, 26f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, fade);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.Shadow(pill, 13f, KugouTheme.WithAlpha(KugouTheme.Colors.Shadow, 0.45f * fade), 8, 2f);
                KugouPainter.RoundRect(pill, KugouTheme.WithAlpha(KugouTheme.Colors.Elevated, 0.92f * fade), 13f);
                KugouPainter.Stroke(pill, KugouTheme.WithAlpha(KugouTheme.Colors.StrokeHairline, 0.25f * fade), 13f, 1f);
            }

            float right = pill.xMax - 6f;
            right -= 22f;
            if (KugouWidgets.IconButton(new Rect(right, pill.y + 2f, 22f, 22f), IconCode.Close, false, true, 11))
            {
                if (Plugin.EnableLyricsWindow != null)
                {
                    Plugin.EnableLyricsWindow.Value = false;
                    Plugin.SaveConfig();
                }
            }

            right -= 24f;
            if (KugouWidgets.IconButton(new Rect(right, pill.y + 2f, 22f, 22f), IconCode.Unlock, false, true, 11))
            {
                ToggleLock(true);
            }

            right -= 40f;
            if (KugouWidgets.Button(new Rect(right, pill.y + 2f, 36f, 22f), "+0.5s", ButtonKind.Ghost))
            {
                NudgeOffset(0.5f);
            }

            float offset = Plugin.LyricsOffsetSeconds != null ? Plugin.LyricsOffsetSeconds.Value : 0f;
            right -= 44f;
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.AccentSky, TextAnchor.MiddleCenter)
                          .Draw(new Rect(right, pill.y, 44f, pill.height), KugouTheme.Content(offset.ToString("+0.0;-0.0;0.0") + "s"), false, false, false, false);
            }

            right -= 38f;
            if (KugouWidgets.Button(new Rect(right, pill.y + 2f, 36f, 22f), "-0.5s", ButtonKind.Ghost))
            {
                NudgeOffset(-0.5f);
            }

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float captionW = Mathf.Max(40f, right - pill.x - 16f);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(pill.x + 12f, pill.y, captionW, pill.height), KugouTheme.Content(caption), false, false, false, false);
            }

            GUI.color = prev;
        }

        private static void DrawLyricsBody(Rect card, int fontSize, bool eightWayOutline, float bgAlpha, float topOffset)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            LyricService lyrics = Plugin.Lyrics;
            string currentLine = lyrics != null ? lyrics.CurrentLine : "等待播放…";
            string nextLine = lyrics != null ? lyrics.NextLine : string.Empty;
            string prevLine = lyrics != null ? lyrics.PreviousLine : string.Empty;
            float changedAt = lyrics != null ? lyrics.LineChangedAt : -10f;
            float lineProgress = lyrics != null ? lyrics.CurrentLineProgress : 0f;
            bool hasSynced = lyrics != null && lyrics.HasLyrics;

            float mainH = fontSize * 1.45f + 4f;
            float subSize = Mathf.Max(12, Mathf.RoundToInt(fontSize * 0.72f));
            float subH = subSize * 1.38f + 4f;

            var mainRect = new Rect(card.x + 16f, card.y + topOffset, card.width - 32f, mainH);
            var subRect = new Rect(card.x + 16f, mainRect.yMax + 2f, card.width - 32f, subH);

            GUIStyle mainStyle = KugouTheme.TextStyle(fontSize, FontStyle.Bold, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleCenter, true);
            GUIStyle subStyle = KugouTheme.TextStyle((int)subSize, FontStyle.Normal, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleCenter, true);

            // 换行 260ms 上移淡入过渡
            float slideY = 0f;
            float curAlpha = 1f;
            if (!KugouAnim.ReduceMotion)
            {
                float dt = Time.unscaledTime - changedAt;
                if (dt >= 0f && dt < 0.26f)
                {
                    float e = KugouAnim.EaseOutCubic(dt / 0.26f);
                    curAlpha = e;
                    slideY = (1f - e) * 6f;

                    if (!string.IsNullOrEmpty(prevLine))
                    {
                        var outRect = new Rect(mainRect.x, mainRect.y - e * 6f, mainRect.width, mainRect.height);
                        KugouPainter.TextShadowed(
                            outRect,
                            prevLine,
                            mainStyle,
                            KugouTheme.WithAlpha(KugouTheme.Colors.TextPrimary, 1f - e),
                            eightWayOutline,
                            0.35f);
                    }
                }
            }

            var animatedMainRect = new Rect(mainRect.x, mainRect.y + slideY, mainRect.width, mainRect.height);
            Color baseLineCol = hasSynced ? KugouTheme.Colors.TextPrimary : KugouTheme.Colors.TextSecondary;
            baseLineCol.a *= curAlpha;
            KugouPainter.TextShadowed(animatedMainRect, currentLine, mainStyle, baseLineCol, eightWayOutline, 0.35f);

            // 居中逐行进度染色（仅当单行未换行且存在时间轴进度时）
            if (hasSynced && lineProgress > 0.01f && !string.IsNullOrEmpty(currentLine))
            {
                float measuredW = mainStyle.CalcSize(KugouTheme.Content(currentLine)).x;
                if (measuredW > 8f && measuredW <= animatedMainRect.width - 4f)
                {
                    float startX = animatedMainRect.x + (animatedMainRect.width - measuredW) * 0.5f;
                    float fillW = Mathf.Clamp(measuredW * lineProgress, 0f, measuredW);
                    GUI.BeginGroup(new Rect(startX, animatedMainRect.y, fillW, animatedMainRect.height));
                    var innerRect = new Rect(animatedMainRect.x - startX, 0f, animatedMainRect.width, animatedMainRect.height);
                    Color litColor = KugouTheme.WithAlpha(KugouTheme.Hex(0xA9DEFF), curAlpha);
                    KugouPainter.TextShadowed(innerRect, currentLine, mainStyle, litColor, false, 0f);
                    GUI.EndGroup();
                }
            }

            if (!string.IsNullOrEmpty(nextLine))
            {
                Color nextCol = KugouTheme.WithAlpha(KugouTheme.Hex(0xC9D3EA), 0.72f);
                KugouPainter.TextShadowed(subRect, nextLine, subStyle, nextCol, eightWayOutline, 0.25f);
            }

            // 柔雾 / 玻璃底衬底部 2px 播放进度丝线
            if (bgAlpha >= 0.05f)
            {
                float trackRatio = ResolveTrackRatio();
                if (trackRatio > 0.002f)
                {
                    var lineRect = new Rect(card.x + 28f, card.yMax - 4f, Mathf.Max(20f, card.width - 56f), 2f);
                    Color prev = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, 0.65f);
                    KugouWidgets.ProgressBar(lineRect, trackRatio, false, false);
                    GUI.color = prev;
                }
            }
        }

        private static float ResolveTrackRatio()
        {
            try
            {
                var service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                if (service == null || service.PlayingMusic == null || service.PlayingMusic.AudioClip == null)
                {
                    return 0f;
                }

                float len = service.PlayingMusic.AudioClip.length;
                if (len <= 0.1f)
                {
                    return 0f;
                }

                float cur = Plugin.Lyrics != null ? Plugin.Lyrics.CurrentPlaySeconds : 0f;
                return Mathf.Clamp01(cur / len);
            }
            catch
            {
                return 0f;
            }
        }

        private static bool IsMouseInScreenLogicalRect(Rect logicalRect, float scale)
        {
            Vector2 mp = Input.mousePosition;
            float mx = mp.x / scale;
            float my = (Screen.height - mp.y) / scale;
            return logicalRect.Contains(new Vector2(mx, my));
        }

        private void ToggleLock(bool nextLocked)
        {
            if (Plugin.LyricsLocked == null)
            {
                return;
            }

            Plugin.LyricsLocked.Value = nextLocked;
            Plugin.SaveConfig();
            _lockedHintText = nextLocked ? "已锁定歌词位置（点击可穿透）" : "已解锁歌词位置（左键拖动移动）";
            if (Plugin.Window != null && Plugin.Window.IsVisible)
            {
                Plugin.Window.SetStatus(_lockedHintText, false);
            }
        }

        private static void NudgeOffset(float delta)
        {
            if (Plugin.LyricsOffsetSeconds == null)
            {
                return;
            }

            float next = (float)Math.Round(Plugin.LyricsOffsetSeconds.Value + delta, 2);
            Plugin.LyricsOffsetSeconds.Value = Mathf.Clamp(next, -15f, 15f);
            Plugin.SaveConfig();
        }

        private void HandlePositionPersistence()
        {
            float vx = _rect.x + Pad;
            float vy = _rect.y + Pad;

            if (float.IsNaN(_lastVisX) || float.IsNaN(_lastVisY))
            {
                _lastVisX = vx;
                _lastVisY = vy;
                return;
            }

            if (Mathf.Abs(vx - _lastVisX) > 0.5f || Mathf.Abs(vy - _lastVisY) > 0.5f)
            {
                _lastVisX = vx;
                _lastVisY = vy;
                _positionDirty = true;
            }

            if (_positionDirty && Event.current != null && Event.current.type == EventType.MouseUp)
            {
                _positionDirty = false;
                SavePosition(vx, vy);
            }
        }

        private static void SavePosition(float vx, float vy)
        {
            try
            {
                if (Plugin.LyricsPosX != null) Plugin.LyricsPosX.Value = Mathf.RoundToInt(vx);
                if (Plugin.LyricsPosY != null) Plugin.LyricsPosY.Value = Mathf.RoundToInt(vy);
                Plugin.SaveConfig();
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[LyricUI] 保存歌词框位置失败: " + ex.Message);
                }
            }
        }

        private static float ResolveWidth(float logicalScreenW)
        {
            int w = Plugin.LyricsWidth != null ? Plugin.LyricsWidth.Value : 560;
            return Mathf.Clamp(w, MinWidth, Mathf.Min(MaxWidth, Mathf.Max(MinWidth, logicalScreenW - 20f)));
        }

        private static int ResolveFontSize()
        {
            int size = Plugin.LyricsFontSize != null ? Plugin.LyricsFontSize.Value : 22;
            return Mathf.Clamp(size, 14, 36);
        }

        private static float ResolveAlpha()
        {
            return Plugin.LyricsBackgroundAlpha != null ? Mathf.Clamp01(Plugin.LyricsBackgroundAlpha.Value) : 0.45f;
        }

        private static float ResolveHeight(int fontSize)
        {
            float mainHeight = fontSize * 1.45f + 4f;
            float subHeight = Mathf.Max(12f, fontSize * 0.72f) * 1.38f + 4f;
            return Mathf.Round(TopBarReserve + mainHeight + subHeight + 12f);
        }

        public void Dispose()
        {
            _initialized = false;
        }
    }
}
