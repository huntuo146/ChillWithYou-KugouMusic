using System;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Pages
{
    internal sealed class SettingsPage : IKugouPage
    {
        private static readonly string[] BackendOptions = { "直连 Native", "本地网关 Gateway" };
        private static readonly string[] QualityOptions = { "128k", "320k", "M4A" };
        private static readonly string[] LyricStyleOptions = { "无", "柔雾", "玻璃" };

        private readonly KugouUiContext _ctx;

        private string _baseUrlEdit = string.Empty;
        private string _repoEdit = string.Empty;
        private bool _autoCheckedOnce;
        private Vector2 _scrollPos;
        private string _cacheInfo = string.Empty;
        private double _cacheMb;
        private bool _hasTestResult;
        private bool _testOk;
        private string _testChipText = string.Empty;
        private bool _testing;

        public string Title { get { return "设置"; } }
        public IconCode Icon { get { return IconCode.Settings; } }

        public SettingsPage(KugouUiContext ctx)
        {
            _ctx = ctx;
            _baseUrlEdit = Plugin.ExternalApiBaseUrl != null ? Plugin.ExternalApiBaseUrl.Value : "http://127.0.0.1:3000";
            _repoEdit = GitHubUpdateService.GetEffectiveRepoSlug();
            UpdateCacheInfo();
        }

        public void OnShow()
        {
            UpdateCacheInfo();
            string latestSlug = GitHubUpdateService.GetEffectiveRepoSlug();
            if (!string.IsNullOrEmpty(latestSlug) && string.IsNullOrEmpty(_repoEdit))
            {
                _repoEdit = latestSlug;
            }

            if (!_autoCheckedOnce &&
                Plugin.AutoCheckUpdates != null &&
                Plugin.AutoCheckUpdates.Value &&
                Plugin.Updater != null &&
                Plugin.Updater.State == UpdateWorkState.Idle &&
                !string.IsNullOrEmpty(latestSlug))
            {
                _autoCheckedOnce = true;
                StartCheckUpdates(true);
            }
        }

        public void Tick() { }

        public void Draw(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Display, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x, r.y, 180f, 28f), KugouTheme.Content("设置"), false, false, false, false);
            }

            var scrollView = new Rect(r.x, r.y + 36f, r.width, Mathf.Max(80f, r.yMax - (r.y + 36f)));
            bool hasChip = _hasTestResult;
            float gwH = 240f + (hasChip ? 24f : 0f);
            float playH = 286f;
            float lyricH = 342f;
            float cacheH = 114f;
            float aboutH = 186f;
            float updateH = ComputeUpdateCardHeight();
            float totalH = gwH + playH + lyricH + cacheH + aboutH + updateH + 60f;

            var contentRect = new Rect(0f, 0f, scrollView.width - 10f, totalH);
            _scrollPos = KugouWidgets.BeginScroll(scrollView, _scrollPos, contentRect);

            float y = 0f;
            float w = contentRect.width;

            y = DrawBackendCard(new Rect(0f, y, w, gwH));
            y += 12f;
            y = DrawPlaybackCard(new Rect(0f, y, w, playH));
            y += 12f;
            y = DrawLyricsCard(new Rect(0f, y, w, lyricH));
            y += 12f;
            y = DrawCacheCard(new Rect(0f, y, w, cacheH));
            y += 12f;
            y = DrawAboutCard(new Rect(0f, y, w, aboutH));
            y += 12f;
            DrawUpdateCard(new Rect(0f, y, w, updateH));

            KugouWidgets.EndScroll();
        }

        private float DrawBackendCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 10f, 180f, 24f), KugouTheme.Content("后端与网关"), false, false, false, false);
            }

            float y = r.y + 42f;
            float rowW = r.width - 28f;

            // 行 1：接口后端
            DrawRowLabel(r.x + 14f, y, "接口后端", "直连官方接口，或用本地网关解锁扫码与个人歌单", 316f);
            int curBackend = Plugin.GetBackendMode() == KugouBackendMode.ExternalGateway ? 1 : 0;
            int nextBackend = KugouWidgets.Segmented(new Rect(r.xMax - 252f, y + 6f, 238f, 30f), curBackend, BackendOptions, "set_backend".GetHashCode());
            if (nextBackend != curBackend)
            {
                SetBackend(nextBackend == 1 ? "ExternalGateway" : "NativeDirect");
            }

            y += 48f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            // 行 2：网关地址 + 保存 + 测试连通
            DrawRowLabel(r.x + 14f, y, "网关地址", "默认 127.0.0.1:3000", 166f);
            bool submitUrl;
            _baseUrlEdit = KugouWidgets.TextField(
                new Rect(r.x + 186f, y + 6f, rowW - 172f - 140f, 30f),
                "settings_baseurl",
                _baseUrlEdit,
                new TextFieldOptions { Placeholder = "http://127.0.0.1:3000", Monospace = true },
                out submitUrl);

            if (KugouWidgets.Button(new Rect(r.xMax - 144f, y + 6f, 52f, 30f), "保存", ButtonKind.Secondary) || submitUrl)
            {
                SaveBaseUrl();
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 86f, y + 6f, 72f, 30f), _testing ? "测试中" : "测试连通", ButtonKind.Secondary, !_testing))
            {
                TestConnection();
            }

            y += 42f;
            if (_hasTestResult)
            {
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    Color c = _testOk ? KugouTheme.Colors.BadgeActiveText : KugouTheme.Colors.Error;
                    Color bg = _testOk ? KugouTheme.Colors.BadgeActiveBg : KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.14f);
                    Color bd = _testOk ? KugouTheme.Colors.BadgeActiveBorder : KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.35f);
                    KugouWidgets.Badge(new Rect(r.x + 186f, y, 250f, 18f), _testChipText, c, bg, bd, true);
                }

                y += 24f;
            }

            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            // 行 3：自动启停
            DrawRowLabel(r.x + 14f, y, "随游戏自动启停网关", "开启后游戏启动时静默启动本地 KuGouMusicApi，退出时自动关闭", 490f);
            bool autoGw = Plugin.AutoStartGateway != null && Plugin.AutoStartGateway.Value;
            bool nextAutoGw = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 4f, 38f, 34f), autoGw, "set_autogw".GetHashCode());
            if (nextAutoGw != autoGw && Plugin.AutoStartGateway != null)
            {
                Plugin.AutoStartGateway.Value = nextAutoGw;
                Plugin.SaveConfig();
                if (nextAutoGw && Plugin.Gateway != null)
                {
                    Plugin.Gateway.EnsureStarted();
                }
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            // 行 4：网关运行状态 + 重启
            GatewayState gwState = Plugin.Gateway != null ? Plugin.Gateway.GetState() : GatewayState.Stopped;
            string gwText = Plugin.Gateway != null ? Plugin.Gateway.GetStatusText() : "未初始化";
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                Color dotCol = ResolveGatewayDotColor(gwState);
                KugouWidgets.StatusDot(new Rect(r.x + 16f, y + 14f, 7f, 7f), dotCol, gwState == GatewayState.RunningOwned || gwState == GatewayState.RunningExternal);
                KugouTheme.Style(TypeToken.Body, curBackend == 1 ? KugouTheme.Colors.TextPrimary : KugouTheme.Colors.TextDisabled, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 30f, y + 6f, rowW - 140f, 24f), KugouTheme.Content("本地网关：" + gwText), false, false, false, false);
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 118f, y + 4f, 104f, 28f), "启动 / 重启", ButtonKind.Secondary, curBackend == 1, IconCode.Refresh))
            {
                RestartLocalGateway();
            }

            return r.yMax;
        }

        private float DrawPlaybackCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 10f, 180f, 24f), KugouTheme.Content("音质与播放偏好"), false, false, false, false);
            }

            float y = r.y + 42f;
            float rowW = r.width - 28f;

            DrawRowLabel(r.x + 14f, y, "目标音质", "高音质需酷狗 VIP，无源时自动降级 128k", 366f);
            string curQ = Plugin.PreferredQuality != null ? Plugin.PreferredQuality.Value : "320";
            int qIdx = curQ == "128" ? 0 : (string.Equals(curQ, "m4a", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            int nextQ = KugouWidgets.Segmented(new Rect(r.xMax - 208f, y + 6f, 194f, 30f), qIdx, QualityOptions, "set_qual".GetHashCode());
            if (nextQ != qIdx)
            {
                SetQuality(nextQ == 0 ? "128" : (nextQ == 2 ? "m4a" : "320"));
            }

            y += 48f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "自动跳过试听 / 无版权", "命中试听片段时自动切至下一首可播歌曲", 490f);
            bool skip = Plugin.SkipTrialTracks != null && Plugin.SkipTrialTracks.Value;
            bool nextSkip = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 4f, 38f, 34f), skip, "set_skip".GetHashCode());
            if (nextSkip != skip && Plugin.SkipTrialTracks != null)
            {
                Plugin.SkipTrialTracks.Value = nextSkip;
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "自动预加载下一首", "当前曲目开始播放后后台静默缓存下一首", 490f);
            bool preload = Plugin.PreloadNextTrack != null && Plugin.PreloadNextTrack.Value;
            bool nextPreload = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 4f, 38f, 34f), preload, "set_pre".GetHashCode());
            if (nextPreload != preload && Plugin.PreloadNextTrack != null)
            {
                Plugin.PreloadNextTrack.Value = nextPreload;
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "手动切源后自动恢复曲目", "仅影响运行中切换音源按钮；游戏启动加载固定保持暂停", 490f);
            bool resume = Plugin.AutoResumeLastTrack != null && Plugin.AutoResumeLastTrack.Value;
            bool nextResume = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 4f, 38f, 34f), resume, "set_res".GetHashCode());
            if (nextResume != resume && Plugin.AutoResumeLastTrack != null)
            {
                Plugin.AutoResumeLastTrack.Value = nextResume;
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, y + 4f, 430f, 22f), KugouTheme.Content("连播熔断保护（连续失败超过 5 首自动停播）"), false, false, false, false);

                int fails = KugouPlayback.ConsecutiveFailures;
                float dotsStart = r.xMax - 120f;
                for (int i = 0; i < 5; i++)
                {
                    Color col = i < fails ? KugouTheme.Colors.AccentCoral : KugouTheme.Colors.TrackEmpty;
                    KugouPainter.Circle(new Rect(dotsStart + i * 14f, y + 11f, 8f, 8f), col);
                }

                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight)
                          .Draw(new Rect(r.xMax - 46f, y + 4f, 32f, 22f), KugouTheme.Content(fails + "/5"), false, false, false, false);
            }

            return r.yMax;
        }

        private float DrawLyricsCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 10f, 180f, 24f), KugouTheme.Content("悬浮歌词框"), false, false, false, false);
            }

            float rowW = r.width - 28f;
            float alpha = Plugin.LyricsBackgroundAlpha != null ? Plugin.LyricsBackgroundAlpha.Value : 0.45f;
            int fontSize = Plugin.LyricsFontSize != null ? Plugin.LyricsFontSize.Value : 22;

            // 实时效果预览条
            var preview = new Rect(r.x + 14f, r.y + 42f, rowW, 44f);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                if (alpha >= 0.65f)
                {
                    KugouPainter.RoundRect(preview, KugouTheme.WithAlpha(KugouTheme.Colors.WindowBg, alpha), 12f);
                    KugouPainter.Stroke(preview, KugouTheme.Colors.StrokeHairline, 12f, 1f);
                }
                else if (alpha >= 0.05f)
                {
                    Color prev = GUI.color;
                    GUI.color = KugouTheme.WithAlpha(KugouTheme.Hex(0x0E1324), alpha);
                    GUI.DrawTexture(preview, KugouTextures.FeatherVeil, ScaleMode.StretchToFill, true);
                    GUI.color = prev;
                }
                else
                {
                    KugouPainter.Stroke(preview, KugouTheme.Colors.Divider, 10f, 1f);
                }

                int sampleSize = Mathf.Clamp(Mathf.RoundToInt(fontSize * 0.72f), 13, 20);
                GUIStyle sampleSt = KugouTheme.TextStyle(sampleSize, FontStyle.Bold, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleCenter);
                KugouPainter.TextShadowed(preview, "♪ 这是一行实时预览歌词 — Chill with You", sampleSt, KugouTheme.Colors.AccentSky, alpha < 0.05f);
            }

            float y = preview.yMax + 10f;

            DrawRowLabel(r.x + 14f, y, "开启悬浮歌词框", "独立展示于游戏界面上方的桌面歌词字幕", 490f);
            bool lyricOn = Plugin.EnableLyricsWindow != null && Plugin.EnableLyricsWindow.Value;
            bool nextLyricOn = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 4f, 38f, 34f), lyricOn, "set_lyon".GetHashCode());
            if (nextLyricOn != lyricOn && Plugin.EnableLyricsWindow != null)
            {
                Plugin.EnableLyricsWindow.Value = nextLyricOn;
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "锁定歌词位置（鼠标穿透）", "锁定后点击歌词不会误触；悬停右上锁图标即可解锁", 490f);
            bool locked = Plugin.LyricsLocked != null && Plugin.LyricsLocked.Value;
            bool nextLocked = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 4f, 38f, 34f), locked, "set_lylock".GetHashCode());
            if (nextLocked != locked && Plugin.LyricsLocked != null)
            {
                Plugin.LyricsLocked.Value = nextLocked;
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "字号与宽度", "调整主歌词字号（14–36）与显示宽度", 314f);
            int nextFont = KugouWidgets.Stepper(new Rect(r.xMax - 252f, y + 6f, 112f, 28f), fontSize, 14, 36, 2, "px");
            if (nextFont != fontSize && Plugin.LyricsFontSize != null)
            {
                Plugin.LyricsFontSize.Value = nextFont;
                Plugin.SaveConfig();
            }

            int width = Plugin.LyricsWidth != null ? Plugin.LyricsWidth.Value : 560;
            int nextWidth = KugouWidgets.Stepper(new Rect(r.xMax - 130f, y + 6f, 116f, 28f), width, 320, 960, 40, string.Empty);
            if (nextWidth != width && Plugin.LyricsWidth != null)
            {
                Plugin.LyricsWidth.Value = nextWidth;
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "底衬风格", "透明描边 / 台灯柔雾 / 磨砂玻璃（向下兼容旧配置）", 356f);
            int bgMode = alpha < 0.05f ? 0 : (alpha < 0.65f ? 1 : 2);
            int nextBgMode = KugouWidgets.Segmented(new Rect(r.xMax - 214f, y + 5f, 200f, 28f), bgMode, LyricStyleOptions, "set_lybg".GetHashCode());
            if (nextBgMode != bgMode && Plugin.LyricsBackgroundAlpha != null)
            {
                Plugin.LyricsBackgroundAlpha.Value = nextBgMode == 0 ? 0f : (nextBgMode == 1 ? 0.45f : 0.78f);
                Plugin.SaveConfig();
            }

            y += 44f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 6f;

            DrawRowLabel(r.x + 14f, y, "时间偏移与定位", "正值提前，负值延后", 148f);
            float offset = Plugin.LyricsOffsetSeconds != null ? Plugin.LyricsOffsetSeconds.Value : 0f;
            float nextOffset = KugouWidgets.FloatStepper(new Rect(r.x + 170f, y + 6f, 124f, 28f), offset, -15f, 15f, 0.5f);
            if (Math.Abs(nextOffset - offset) > 0.001f && Plugin.LyricsOffsetSeconds != null)
            {
                Plugin.LyricsOffsetSeconds.Value = nextOffset;
                Plugin.SaveConfig();
            }

            if (KugouWidgets.Button(new Rect(r.x + 300f, y + 6f, 52f, 28f), "归零", ButtonKind.Ghost) && Plugin.LyricsOffsetSeconds != null)
            {
                Plugin.LyricsOffsetSeconds.Value = 0f;
                Plugin.SaveConfig();
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 166f, y + 6f, 76f, 28f), "重置位置", ButtonKind.Ghost) && Plugin.LyricsUi != null)
            {
                Plugin.LyricsUi.ResetPosition();
                _ctx.SetStatus("已重置歌词框位置", false);
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 86f, y + 6f, 72f, 28f), "重拉歌词", ButtonKind.Secondary) && Plugin.Lyrics != null)
            {
                Plugin.Lyrics.InvalidateCurrent();
                _ctx.SetStatus("已重新拉取当前曲目歌词", false);
            }

            return r.yMax;
        }

        private float DrawCacheCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 10f, 180f, 24f), KugouTheme.Content("本地音频缓存"), false, false, false, false);

                float maxMb = Plugin.MaxCacheSizeMB != null ? Mathf.Max(50f, Plugin.MaxCacheSizeMB.Value) : 500f;
                float ratio = (float)(_cacheMb / maxMb);
                KugouWidgets.ProgressBar(new Rect(r.x + 14f, r.y + 44f, r.width - 28f, 6f), ratio, false, ratio >= 0.85f);

                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 68f, r.width - 210f, 26f), KugouTheme.Content(_cacheInfo), false, false, false, false);
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 186f, r.y + 68f, 76f, 28f), "刷新统计", ButtonKind.Ghost))
            {
                UpdateCacheInfo();
                _ctx.SetStatus("缓存统计已刷新", false);
            }

            if (KugouWidgets.DangerConfirmButton(new Rect(r.xMax - 102f, r.y + 68f, 88f, 28f), "set_clearcache".GetHashCode(), "清空缓存", "确认清空?"))
            {
                ClearCache();
            }

            return r.yMax;
        }

        private float DrawAboutCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 10f, 180f, 24f), KugouTheme.Content("界面与关于"), false, false, false, false);
            }

            float y = r.y + 40f;
            float rowW = r.width - 28f;

            DrawRowLabel(r.x + 14f, y, "界面缩放（UI Scale）", "0 为自动适配；支持 0.85x–1.60x 手动缩放", 376f);
            float scale = KugouTheme.ResolveUiScale();
            bool isAuto = Plugin.UiScale == null || Plugin.UiScale.Value <= 0.1f;

            if (KugouWidgets.Button(new Rect(r.xMax - 186f, y + 6f, 52f, 28f), isAuto ? "● 自动" : "自动", isAuto ? ButtonKind.Secondary : ButtonKind.Ghost)
                && Plugin.UiScale != null)
            {
                Plugin.UiScale.Value = 0f;
                Plugin.SaveConfig();
            }

            float nextScale = KugouWidgets.FloatStepper(new Rect(r.xMax - 130f, y + 6f, 116f, 28f), scale, 0.85f, 1.60f, 0.05f, "0.00", "x");
            if (Math.Abs(nextScale - scale) > 0.005f && Plugin.UiScale != null)
            {
                Plugin.UiScale.Value = nextScale;
                Plugin.SaveConfig();
            }

            y += 42f;
            KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
            y += 4f;

            DrawRowLabel(r.x + 14f, y, "减弱动效（Reduce Motion）", "关闭旋转黑胶、律动条与微光，仅保留淡入淡出", 490f);
            bool reduce = Plugin.ReduceMotion != null && Plugin.ReduceMotion.Value;
            bool nextReduce = KugouWidgets.Switch(new Rect(r.xMax - 52f, y + 2f, 38f, 34f), reduce, "set_reduce".GetHashCode());
            if (nextReduce != reduce && Plugin.ReduceMotion != null)
            {
                Plugin.ReduceMotion.Value = nextReduce;
                Plugin.SaveConfig();
            }

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                string hk = Plugin.ToggleHotkey != null ? Plugin.ToggleHotkey.Value.ToString() : "F8";
                KugouWidgets.Badge(
                    new Rect(r.xMax - 136f, r.y + 12f, 52f, 18f),
                    hk,
                    KugouTheme.Colors.TextPrimary,
                    KugouTheme.Colors.InputBg,
                    KugouTheme.Colors.StrokeStrong);
                KugouWidgets.Badge(
                    new Rect(r.xMax - 76f, r.y + 12f, 62f, 18f),
                    "v" + KugouIds.PluginVersion,
                    KugouTheme.Colors.BadgeQualityText,
                    KugouTheme.Colors.BadgeQualityBg,
                    KugouTheme.Colors.BadgeQualityBorder);

                float footY = y + 40f;
                KugouPainter.Divider(new Rect(r.x + 14f, footY, rowW, 1f));
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(
                              new Rect(r.x + 14f, footY + 7f, rowW, 17f),
                              KugouTheme.Content("基于 BepInEx 5 · UnityDoorstop · HarmonyX · MakcRe/KuGouMusicApi (MIT) 构建"),
                              false, false, false, false);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(
                              new Rect(r.x + 14f, footY + 25f, rowW, 17f),
                              KugouTheme.Content("仅供个人娱乐 · 音乐版权归属酷狗音乐，账号冻结/封禁后果自负，使用即默认同意免责守则"),
                              false, false, false, false);
            }

            return r.yMax;
        }

        private float ComputeUpdateCardHeight()
        {
            GitHubUpdateService updater = Plugin.Updater;
            if (updater == null || updater.State == UpdateWorkState.Idle)
            {
                return 96f;
            }

            if (updater.State == UpdateWorkState.UpdateAvailable ||
                updater.State == UpdateWorkState.ReadyToApply ||
                updater.State == UpdateWorkState.Downloading)
            {
                return 174f;
            }

            return 130f;
        }

        private void DrawUpdateCard(Rect r)
        {
            GitHubUpdateService updater = Plugin.Updater;
            UpdateWorkState state = updater != null ? updater.State : UpdateWorkState.Idle;
            GitHubReleaseInfo release = updater != null ? updater.LatestRelease : null;

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 14f, r.y + 10f, 200f, 24f), KugouTheme.Content("GitHub 在线更新"), false, false, false, false);

                string chipText;
                Color chipFg;
                Color chipBg;
                Color chipBd;
                switch (state)
                {
                    case UpdateWorkState.UpToDate:
                        chipText = "已是最新";
                        chipFg = KugouTheme.Colors.Success;
                        chipBg = KugouTheme.WithAlpha(KugouTheme.Colors.Success, 0.12f);
                        chipBd = KugouTheme.WithAlpha(KugouTheme.Colors.Success, 0.30f);
                        break;
                    case UpdateWorkState.UpdateAvailable:
                        chipText = release != null ? ("发现 v" + release.VersionText) : "有可用更新";
                        chipFg = KugouTheme.Colors.AccentSky;
                        chipBg = KugouTheme.WithAlpha(KugouTheme.Colors.AccentSky, 0.14f);
                        chipBd = KugouTheme.WithAlpha(KugouTheme.Colors.AccentSky, 0.35f);
                        break;
                    case UpdateWorkState.Downloading:
                        chipText = "下载中 " + Mathf.RoundToInt(updater.DownloadProgress * 100f) + "%";
                        chipFg = KugouTheme.Colors.AccentLavender;
                        chipBg = KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.16f);
                        chipBd = KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.36f);
                        break;
                    case UpdateWorkState.ReadyToApply:
                        chipText = "待重启生效";
                        chipFg = KugouTheme.Colors.BadgeActiveText;
                        chipBg = KugouTheme.Colors.BadgeActiveBg;
                        chipBd = KugouTheme.Colors.BadgeActiveBorder;
                        break;
                    case UpdateWorkState.Error:
                        chipText = "检查失败";
                        chipFg = KugouTheme.Colors.Error;
                        chipBg = KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.14f);
                        chipBd = KugouTheme.WithAlpha(KugouTheme.Colors.Error, 0.32f);
                        break;
                    case UpdateWorkState.Checking:
                        chipText = "检查中…";
                        chipFg = KugouTheme.Colors.Warning;
                        chipBg = KugouTheme.WithAlpha(KugouTheme.Colors.Warning, 0.14f);
                        chipBd = KugouTheme.WithAlpha(KugouTheme.Colors.Warning, 0.30f);
                        break;
                    default:
                        chipText = "v" + KugouIds.PluginVersion;
                        chipFg = KugouTheme.Colors.TextTertiary;
                        chipBg = KugouTheme.Colors.InputBg;
                        chipBd = KugouTheme.Colors.Divider;
                        break;
                }

                KugouWidgets.Badge(new Rect(r.xMax - 116f, r.y + 12f, 102f, 18f), chipText, chipFg, chipBg, chipBd, state == UpdateWorkState.UpdateAvailable || state == UpdateWorkState.ReadyToApply);
            }

            float y = r.y + 42f;
            float rowW = r.width - 28f;

            DrawRowLabel(r.x + 14f, y, "GitHub 仓库", "填写 owner/repo 或仓库链接", 148f);
            bool submitRepo;
            _repoEdit = KugouWidgets.TextField(
                new Rect(r.x + 170f, y + 6f, Mathf.Max(80f, rowW - 156f - 146f), 30f),
                "settings_github_repo",
                _repoEdit,
                new TextFieldOptions { Placeholder = "如 username/ChillWithYou-KugouMusic", Monospace = true },
                out submitRepo);

            if (KugouWidgets.Button(new Rect(r.xMax - 146f, y + 6f, 52f, 30f), "保存", ButtonKind.Secondary) || submitRepo)
            {
                SaveGitHubRepo();
            }

            bool busy = updater != null && updater.IsBusy;
            if (KugouWidgets.Button(new Rect(r.xMax - 88f, y + 6f, 74f, 30f), state == UpdateWorkState.Checking ? "检查中" : "检查更新", ButtonKind.Secondary, !busy))
            {
                SaveGitHubRepo(silentToast: true);
                StartCheckUpdates(false);
            }

            if (updater != null && state != UpdateWorkState.Idle)
            {
                y += 46f;
                KugouPainter.Divider(new Rect(r.x + 14f, y, rowW, 1f));
                y += 8f;

                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    Color msgCol = state == UpdateWorkState.Error
                        ? KugouTheme.Colors.Error
                        : (state == UpdateWorkState.UpdateAvailable || state == UpdateWorkState.ReadyToApply
                            ? KugouTheme.Colors.TextPrimary
                            : KugouTheme.Colors.TextSecondary);

                    KugouTheme.Style(TypeToken.Caption, msgCol, TextAnchor.MiddleLeft)
                              .Draw(new Rect(r.x + 14f, y, rowW, 18f), KugouTheme.Content(updater.StatusMessage), false, false, false, false);
                }

                y += 22f;

                if (state == UpdateWorkState.Downloading)
                {
                    if (Event.current != null && Event.current.type == EventType.Repaint)
                    {
                        KugouWidgets.ProgressBar(new Rect(r.x + 14f, y + 8f, rowW - 120f, 6f), updater.DownloadProgress, false, false);
                        string dlText = updater.TotalBytes > 0
                            ? ((updater.DownloadedBytes / 1048576.0).ToString("0.0") + " / " + (updater.TotalBytes / 1048576.0).ToString("0.0") + " MB")
                            : ((updater.DownloadedBytes / 1048576.0).ToString("0.0") + " MB");
                        KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight)
                                  .Draw(new Rect(r.xMax - 122f, y + 1f, 108f, 18f), KugouTheme.Content(dlText), false, false, false, false);
                    }
                }
                else if (state == UpdateWorkState.UpdateAvailable && release != null)
                {
                    if (Event.current != null && Event.current.type == EventType.Repaint && !string.IsNullOrEmpty(release.Body))
                    {
                        string oneLineNotes = release.Body.Replace("\r", " ").Replace("\n", " · ").Trim();
                        KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                                  .Draw(new Rect(r.x + 14f, y, rowW - 224f, 22f), KugouTheme.Content(oneLineNotes), false, false, false, false);
                    }

                    bool hasAsset = !string.IsNullOrEmpty(release.AssetDownloadUrl);
                    if (KugouWidgets.Button(new Rect(r.xMax - 216f, y + 6f, 114f, 28f), "下载并更新", ButtonKind.Primary, hasAsset, IconCode.Sparkle))
                    {
                        StartDownloadUpdate();
                    }

                    if (KugouWidgets.Button(new Rect(r.xMax - 94f, y + 6f, 80f, 28f), "发布页", ButtonKind.Ghost))
                    {
                        updater.OpenReleasePage();
                    }
                }
                else if (state == UpdateWorkState.ReadyToApply)
                {
                    if (KugouWidgets.Button(new Rect(r.x + 14f, y + 6f, 150f, 28f), "立即重启并应用更新", ButtonKind.Primary))
                    {
                        if (!updater.ApplyStagedUpdateAndRestart(true))
                        {
                            _ctx.SetStatus(updater.StatusMessage, true);
                        }
                    }

                    if (KugouWidgets.Button(new Rect(r.x + 174f, y + 6f, 80f, 28f), "打开发布页", ButtonKind.Ghost))
                    {
                        updater.OpenReleasePage();
                    }
                }
            }
        }

        private static void DrawRowLabel(float x, float y, string label, string caption, float maxWidth = 320f)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            KugouTheme.Style(TypeToken.Body, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                      .Draw(new Rect(x, y + 3f, maxWidth, 20f), KugouTheme.Content(label), false, false, false, false);
            KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                      .Draw(new Rect(x, y + 22f, maxWidth, 16f), KugouTheme.Content(caption), false, false, false, false);
        }

        private static Color ResolveGatewayDotColor(GatewayState state)
        {
            switch (state)
            {
                case GatewayState.RunningOwned:
                case GatewayState.RunningExternal: return KugouTheme.Colors.Success;
                case GatewayState.Starting: return KugouTheme.Colors.Warning;
                case GatewayState.Remote: return KugouTheme.Colors.AccentSky;
                default: return KugouTheme.Colors.TextTertiary;
            }
        }

        private void SetBackend(string mode)
        {
            try
            {
                if (Plugin.BackendMode != null)
                {
                    Plugin.BackendMode.Value = mode;
                    Plugin.SaveConfig();
                }

                Plugin.RebuildClient("UI 切换后端");
                _ctx.SetStatus("接口后端已切换: " + mode, false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("切换后端失败: " + ex.Message, true);
            }
        }

        private void SaveBaseUrl()
        {
            try
            {
                if (Plugin.ExternalApiBaseUrl != null)
                {
                    Plugin.ExternalApiBaseUrl.Value = _baseUrlEdit;
                    Plugin.SaveConfig();
                }

                Plugin.RebuildClient("UI 修改网关地址");
                _ctx.SetStatus("网关地址已保存", false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("保存失败: " + ex.Message, true);
            }
        }

        private void SetQuality(string quality)
        {
            try
            {
                if (Plugin.PreferredQuality != null)
                {
                    Plugin.PreferredQuality.Value = quality;
                    Plugin.SaveConfig();
                }

                Plugin.EnsureClientCurrent();
                _ctx.SetStatus("目标音质已设为 " + quality, false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("设置失败: " + ex.Message, true);
            }
        }

        private void RestartLocalGateway()
        {
            try
            {
                if (Plugin.GetBackendMode() != KugouBackendMode.ExternalGateway)
                {
                    _ctx.SetStatus("请先将后端切换到 ExternalGateway", true);
                    return;
                }

                if (Plugin.Gateway == null)
                {
                    _ctx.SetStatus("网关托管模块未初始化", true);
                    return;
                }

                bool ok = Plugin.Gateway.Restart();
                _ctx.SetStatus(ok ? "已触发启动本地网关（" + Plugin.Gateway.GetStatusText() + "）" : "启动失败，请查看日志", !ok);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("启动异常: " + ex.Message, true);
            }
        }

        private void TestConnection()
        {
            if (Plugin.Client == null)
            {
                _ctx.SetStatus("接口客户端未初始化", true);
                return;
            }

            _testing = true;
            TestConnectionAsync().Forget();
        }

        private async UniTaskVoid TestConnectionAsync()
        {
            try
            {
                Plugin.EnsureClientCurrent();
                IKugouClient client = Plugin.Client;
                if (client == null)
                {
                    _testing = false;
                    return;
                }

                KugouConnectionTestResult res = await client.TestConnectionAsync(CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);

                _hasTestResult = true;
                _testOk = res != null && res.Ok;
                _testChipText = _testOk
                    ? ("连通正常 · " + res.ElapsedMs + "ms")
                    : ("连通失败：" + (res != null ? res.Message : "无响应"));
                _ctx.SetStatus(_testChipText, !_testOk);
            }
            catch (Exception ex)
            {
                _hasTestResult = true;
                _testOk = false;
                _testChipText = "测试异常：" + ex.Message;
                _ctx.SetStatus(_testChipText, true);
            }
            finally
            {
                _testing = false;
            }
        }

        private void UpdateCacheInfo()
        {
            try
            {
                if (Plugin.AudioCache != null)
                {
                    long bytes = Plugin.AudioCache.GetCacheSizeBytes();
                    _cacheMb = bytes / (1024.0 * 1024.0);
                    int maxMb = Plugin.MaxCacheSizeMB != null ? Plugin.MaxCacheSizeMB.Value : 500;
                    _cacheInfo = _cacheMb.ToString("0.0") + " MB / " + maxMb + " MB · " + Plugin.AudioCache.GetCacheFileCount() + " 个文件";
                }
                else
                {
                    _cacheMb = 0;
                    _cacheInfo = "缓存服务未初始化";
                }
            }
            catch
            {
                _cacheInfo = "统计失败";
            }
        }

        private void ClearCache()
        {
            try
            {
                if (Plugin.AudioCache != null)
                {
                    Plugin.AudioCache.ClearCache();
                }

                UpdateCacheInfo();
                _ctx.SetStatus("音频缓存已清空", false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("清空失败: " + ex.Message, true);
            }
        }

        private void SaveGitHubRepo(bool silentToast = false)
        {
            string saved = GitHubUpdateService.SaveRepoSetting(_repoEdit);
            _repoEdit = saved;
            if (!silentToast)
            {
                _ctx.SetStatus(
                    string.IsNullOrEmpty(saved) ? "请填写有效的 GitHub 仓库（如 owner/repo）" : ("已保存更新仓库: " + saved),
                    string.IsNullOrEmpty(saved));
            }
        }

        private void StartCheckUpdates(bool silent)
        {
            if (Plugin.Updater == null || Plugin.Updater.IsBusy)
            {
                return;
            }

            CheckUpdatesAsync(silent).Forget();
        }

        private async UniTaskVoid CheckUpdatesAsync(bool silent)
        {
            try
            {
                bool hasNew = await Plugin.Updater.CheckForUpdatesAsync(_repoEdit);
                await UniTask.SwitchToMainThread();
                if (!silent || hasNew)
                {
                    bool isErr = Plugin.Updater.State == UpdateWorkState.Error;
                    _ctx.SetStatus(Plugin.Updater.StatusMessage, isErr);
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    _ctx.SetStatus("检查更新异常: " + ex.Message, true);
                }
            }
        }

        private void StartDownloadUpdate()
        {
            if (Plugin.Updater == null || Plugin.Updater.IsBusy)
            {
                return;
            }

            DownloadUpdateAsync().Forget();
        }

        private async UniTaskVoid DownloadUpdateAsync()
        {
            try
            {
                bool ok = await Plugin.Updater.DownloadAndStageLatestAsync();
                await UniTask.SwitchToMainThread();
                _ctx.SetStatus(Plugin.Updater.StatusMessage, !ok);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("下载更新异常: " + ex.Message, true);
            }
        }

        public void Dispose() { }
    }
}
