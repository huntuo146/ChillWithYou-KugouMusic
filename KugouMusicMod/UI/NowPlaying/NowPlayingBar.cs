using System;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.NowPlaying
{
    /// <summary>
    /// 底部一体化 Now Playing Dock Bar 与 380×76 Mini Player 胶囊模式绘制。
    /// </summary>
    internal sealed class NowPlayingBar
    {
        public void DrawFullBar(Rect r, NowPlayingModel model, CoverCache coverCache)
        {
            if (model == null)
            {
                return;
            }

            float vinylAngle = KugouAnim.StepVinylAngle(model.IsPlaying && model.HasTrack);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(r, KugouTheme.Colors.NowPlayingBg, 0f);
                KugouPainter.Divider(new Rect(r.x, r.y, r.width, 1f), 0f);
            }

            // 左侧：封面/黑胶 + 曲名 + 歌手 + 律动条
            float leftPad = 16f;
            float coverSize = 44f;
            var coverRect = new Rect(r.x + leftPad, r.y + (r.height - coverSize) * 0.5f, coverSize, coverSize);
            bool useVinyl = !model.IsKugouTrack || (model.CurrentTrack == null && string.IsNullOrEmpty(model.CoverUrl));
            KugouWidgets.DrawCover(
                coverRect,
                coverCache,
                model.CoverUrl,
                useVinyl ? coverSize * 0.5f : 10f,
                model.Title,
                null,
                useVinylFallback: true,
                vinylAngle: vinylAngle,
                trackForAutoResolve: model.CurrentTrack,
                coverSize: 240);

            float infoX = coverRect.xMax + 12f;
            float titleW = 236f;
            float singerW = 190f;
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                Color titleCol = model.ServiceReady
                    ? (model.IsPlaying ? KugouTheme.Colors.TextPrimary : KugouTheme.Colors.TextSecondary)
                    : KugouTheme.Colors.TextTertiary;
                GUIStyle titleSt = KugouTheme.Style(TypeToken.BodyStrong, titleCol, TextAnchor.MiddleLeft);
                float eqSpace = model.HasTrack ? 22f : 0f;
                titleSt.Draw(
                    new Rect(infoX, r.y + 13f, titleW - eqSpace, 20f),
                    KugouTheme.Content(model.Title),
                    false,
                    false,
                    false,
                    false);

                if (model.HasTrack)
                {
                    KugouPainter.EqualizerBars(new Rect(infoX + titleW - 18f, r.y + 16f, 14f, 13f), model.IsPlaying);
                }

                GUIStyle subSt = KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft);
                subSt.Draw(
                    new Rect(infoX, r.y + 34f, singerW, 18f),
                    KugouTheme.Content(model.Singer),
                    false,
                    false,
                    false,
                    false);
            }

            // 中央：传输按钮 + 细进度条
            float centerX = r.x + r.width * 0.5f;
            bool controlsEnabled = model.ServiceReady;

            var playRect = new Rect(centerX - 18f, r.y + 8f, 36f, 36f);
            var prevRect = new Rect(playRect.x - 36f, r.y + 12f, 28f, 28f);
            var nextRect = new Rect(playRect.xMax + 8f, r.y + 12f, 28f, 28f);

            if (KugouWidgets.IconButton(prevRect, IconCode.Prev, false, controlsEnabled, 13))
            {
                model.PrevTrack();
            }

            if (DrawMainPlayButton(playRect, model.IsPlaying, controlsEnabled))
            {
                model.TogglePlayPause();
            }

            if (KugouWidgets.IconButton(nextRect, IconCode.Next, false, controlsEnabled, 13))
            {
                model.NextTrack();
            }

            // 细进度条与时间
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float barTotalW = 240f;
                float barX = centerX - barTotalW * 0.5f;
                float barY = r.y + 52f;
                GUIStyle monoLeft = KugouTheme.TextStyle(11, FontStyle.Normal, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight, false, true);
                GUIStyle monoRight = KugouTheme.TextStyle(11, FontStyle.Normal, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft, false, true);
                monoLeft.Draw(new Rect(barX - 4f, barY - 6f, 34f, 15f), KugouTheme.Content(model.PlayedText), false, false, false, false);
                KugouWidgets.ProgressBar(new Rect(barX + 36f, barY, barTotalW - 72f, 3f), model.Progress, false, false);
                monoRight.Draw(new Rect(barX + barTotalW - 30f, barY - 6f, 36f, 15f), KugouTheme.Content(model.DurationText), false, false, false, false);
            }

            // 右侧：音源音质芯片 + 连续失败保护提醒
            float rightPad = 16f;
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float chipW = 104f;
                var chipRect = new Rect(r.xMax - rightPad - chipW, r.y + 17f, chipW, 20f);
                if (model.IsKugouSource)
                {
                    KugouWidgets.Badge(
                        chipRect,
                        model.SourceQualityLabel,
                        KugouTheme.Colors.BadgeQualityText,
                        KugouTheme.Colors.BadgeQualityBg,
                        KugouTheme.Colors.BadgeQualityBorder);
                }
                else
                {
                    KugouWidgets.Badge(
                        chipRect,
                        model.SourceQualityLabel,
                        KugouTheme.Colors.TextSecondary,
                        KugouTheme.Colors.Card,
                        KugouTheme.Colors.StrokeHairline);
                }

                if (model.ConsecutiveFailures > 0)
                {
                    var warnRect = new Rect(r.xMax - rightPad - 96f, r.y + 40f, 96f, 16f);
                    KugouWidgets.StatusDot(new Rect(warnRect.x, warnRect.y + 5f, 6f, 6f), KugouTheme.Colors.Warning, true);
                    GUIStyle warnSt = KugouTheme.Style(TypeToken.Micro, KugouTheme.Colors.Warning, TextAnchor.MiddleRight);
                    warnSt.Draw(warnRect, KugouTheme.Content("连续失败 " + model.ConsecutiveFailures + "/5"), false, false, false, false);
                }
            }
        }

        public void DrawMiniPlayer(
            Rect cardRect,
            NowPlayingModel model,
            CoverCache coverCache,
            Action onExpand,
            out Rect dragArea)
        {
            float vinylAngle = KugouAnim.StepVinylAngle(model.IsPlaying && model.HasTrack);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.GlassPanel(cardRect, 38f, true, 0.92f, 0.58f);
            }

            float coverSize = 52f;
            var coverRect = new Rect(cardRect.x + 12f, cardRect.y + (cardRect.height - coverSize) * 0.5f, coverSize, coverSize);
            KugouWidgets.DrawCover(
                coverRect,
                coverCache,
                model.CoverUrl,
                coverSize * 0.5f,
                model.Title,
                null,
                useVinylFallback: true,
                vinylAngle: vinylAngle,
                trackForAutoResolve: model.CurrentTrack,
                coverSize: 240);

            var expandRect = new Rect(cardRect.xMax - 38f, cardRect.y + 24f, 28f, 28f);
            var nextRect = new Rect(expandRect.x - 32f, cardRect.y + 24f, 28f, 28f);
            var playRect = new Rect(nextRect.x - 38f, cardRect.y + 20f, 34f, 34f);
            var prevRect = new Rect(playRect.x - 32f, cardRect.y + 24f, 28f, 28f);

            float textX = coverRect.xMax + 12f;
            float textW = Mathf.Max(40f, prevRect.x - textX - 8f);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                GUIStyle tSt = KugouTheme.Style(TypeToken.BodyStrong, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft);
                tSt.Draw(new Rect(textX, cardRect.y + 18f, textW, 20f), KugouTheme.Content(model.Title), false, false, false, false);

                GUIStyle sSt = KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft);
                sSt.Draw(new Rect(textX, cardRect.y + 39f, textW, 18f), KugouTheme.Content(model.Singer), false, false, false, false);

                if (model.HasTrack)
                {
                    var lineRect = new Rect(cardRect.x + 36f, cardRect.yMax - 4f, cardRect.width - 72f, 2f);
                    KugouWidgets.ProgressBar(lineRect, model.Progress, false, false);
                }
            }

            bool controlsEnabled = model.ServiceReady;
            if (KugouWidgets.IconButton(prevRect, IconCode.Prev, false, controlsEnabled, 12))
            {
                model.PrevTrack();
            }

            if (DrawMainPlayButton(playRect, model.IsPlaying, controlsEnabled))
            {
                model.TogglePlayPause();
            }

            if (KugouWidgets.IconButton(nextRect, IconCode.Next, false, controlsEnabled, 12))
            {
                model.NextTrack();
            }

            if (KugouWidgets.IconButton(expandRect, IconCode.Expand, false, true, 13))
            {
                if (onExpand != null)
                {
                    onExpand();
                }
            }

            dragArea = new Rect(cardRect.x, cardRect.y, prevRect.x - cardRect.x - 4f, cardRect.height);
        }

        private static bool DrawMainPlayButton(Rect r, bool isPlaying, bool enabled)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            bool hover = enabled && KugouWidgets.IsHovered(r);
            float h = KugouAnim.Hover(id, hover);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float rad = r.height * 0.5f;
                if (enabled)
                {
                    if (h > 0.01f)
                    {
                        KugouPainter.Shadow(r, rad, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.36f * h), 8, 1f);
                    }

                    KugouPainter.GradientRect(r, KugouTextures.BrandGradient, rad, Color.white);
                    if (h > 0.01f)
                    {
                        KugouPainter.RoundRect(r, new Color(1f, 1f, 1f, 0.12f * h), rad);
                    }
                }
                else
                {
                    KugouPainter.RoundRect(r, KugouTheme.Colors.DisabledFill, rad);
                }

                IconCode icon = isPlaying ? IconCode.Pause : IconCode.Play;
                Color fg = enabled ? Color.white : KugouTheme.Colors.TextDisabled;
                var iconRect = isPlaying ? r : new Rect(r.x + 1f, r.y, r.width, r.height);
                KugouPainter.Icon(iconRect, icon, 14, fg);
            }

            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }
    }
}
