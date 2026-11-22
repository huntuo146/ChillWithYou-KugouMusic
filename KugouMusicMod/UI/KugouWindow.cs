using System;
using System.Collections.Generic;
using System.Threading;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.NowPlaying;
using ChillWithYou.KugouMusic.UI.Pages;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI
{
    /// <summary>
    /// 「Midnight Desk Lo-Fi」主悬浮窗外壳（780×560 磨砂玻璃窗 + 380×76 Mini 胶囊模式）：
    /// - Header（Logo / 网关+登录状态 / 官方|酷狗胶囊 / 歌词开关 / Mini / 关闭）
    /// - 左侧导航栏（歌单 / 搜索 / 账号 / 设置 + 底部账号小卡）
    /// - 4 个模块化页面（带切页淡入滑动过渡）
    /// - 底部一体化 Now Playing Bar 与居中浮动 Toast
    /// - 通过 KugouInputBlocker 阻断鼠标穿透到游戏 UI，同时托管原生面板双按钮注入。
    /// </summary>
    internal sealed class KugouWindow
    {
        private const int WindowId = 0x4B4755;

        private static readonly string[] SourceToggleLabels = { "官方", "酷狗" };

        private Rect _windowRect;
        private bool _visible;
        private bool _miniMode;
        private int _tab;
        private float _tabChangedAt = -10f;

        private float _lastPosX = float.NaN;
        private float _lastPosY = float.NaN;
        private bool _positionDirty;

        // ---- Toast ----
        private string _toastText = string.Empty;
        private bool _toastError;
        private float _toastExpire;
        private float _toastShownAt;
        private string _toastActionLabel = string.Empty;
        private Action _toastActionCallback;

        // ---- 数据与子模块 ----
        private KugouPlaylistRecord[] _savedPlaylists = new KugouPlaylistRecord[0];
        private IKugouPage[] _pages;
        private PlaylistPage _playlistPage;
        private SearchPage _searchPage;
        private AccountPage _accountPage;
        private SettingsPage _settingsPage;

        private CoverCache _covers;
        private NowPlayingModel _nowPlayingModel;
        private NowPlayingBar _nowPlayingBar;
        private NativeButtonInjector _nativeInjector;
        private KugouInputBlocker _inputBlocker;

        public bool IsVisible
        {
            get { return _visible; }
        }

        public KugouInputBlocker InputBlocker
        {
            get { return _inputBlocker; }
        }

        public void Initialize()
        {
            float scale = KugouTheme.ResolveUiScale();
            float maxX = Mathf.Max(80f, Screen.width / scale - 100f);
            float maxY = Mathf.Max(80f, Screen.height / scale - 100f);

            float vx = Plugin.WindowPosX != null ? Plugin.WindowPosX.Value : 120f;
            float vy = Plugin.WindowPosY != null ? Plugin.WindowPosY.Value : 90f;
            if (vx < 0f || vx > maxX) vx = 120f;
            if (vy < 0f || vy > maxY) vy = 90f;

            _miniMode = Plugin.WindowMiniMode != null && Plugin.WindowMiniMode.Value;
            float pad = KugouTheme.Sizes.ShadowPad;
            float w = _miniMode ? KugouTheme.Sizes.MiniWidth : KugouTheme.Sizes.WindowWidth;
            float h = _miniMode ? KugouTheme.Sizes.MiniHeight : KugouTheme.Sizes.WindowHeight;
            _windowRect = new Rect(vx - pad, vy - pad, w + pad * 2f, h + pad * 2f);
            _lastPosX = vx;
            _lastPosY = vy;

            _covers = new CoverCache();
            _nowPlayingModel = new NowPlayingModel();
            _nowPlayingBar = new NowPlayingBar();
            _nativeInjector = new NativeButtonInjector();
            _inputBlocker = new KugouInputBlocker();

            var ctx = new KugouUiContext(
                _covers,
                SetStatus,
                SetToastWithAction,
                ToggleSource,
                GoToPage,
                ImportPlaylistAsync,
                SwitchPlaylist,
                () => _savedPlaylists,
                RefreshSavedPlaylists);

            _playlistPage = new PlaylistPage(ctx);
            _searchPage = new SearchPage(ctx);
            _accountPage = new AccountPage(ctx);
            _settingsPage = new SettingsPage(ctx);
            _pages = new IKugouPage[] { _playlistPage, _searchPage, _accountPage, _settingsPage };

            RefreshSavedPlaylists();
        }

        public void Toggle()
        {
            _visible = !_visible;
            if (_visible)
            {
                OnWindowOpened();
            }
            else
            {
                SyncInputBlocker(KugouTheme.ResolveUiScale());
            }
        }

        public void Show()
        {
            _visible = true;
            OnWindowOpened();
        }

        public void Hide()
        {
            _visible = false;
            SyncInputBlocker(KugouTheme.ResolveUiScale());
        }

        public void Tick()
        {
            if (_toastExpire < 0f)
            {
                _toastExpire = Time.unscaledTime + (_toastError ? 6f : 3.5f);
                _toastShownAt = Time.unscaledTime;
            }
            else if (!string.IsNullOrEmpty(_toastText) && Time.unscaledTime > _toastExpire)
            {
                _toastText = string.Empty;
                _toastActionLabel = string.Empty;
                _toastActionCallback = null;
            }

            if (_nativeInjector != null)
            {
                _nativeInjector.TickRefresh();
            }

            if (_nowPlayingModel != null && _visible)
            {
                _nowPlayingModel.Tick();
            }

            if (_visible && _pages != null)
            {
                if (_tab >= 0 && _tab < _pages.Length)
                {
                    _pages[_tab].Tick();
                }

                // 二维码轮询即使切到别页也继续后台检查，直至确认或停止
                if (_tab != 2 && _accountPage != null)
                {
                    _accountPage.Tick();
                }
            }

            SyncInputBlocker(KugouTheme.ResolveUiScale());
        }

        public void SyncInputBlocker(float uiScale)
        {
            if (_inputBlocker == null)
            {
                return;
            }

            if (!_visible)
            {
                _inputBlocker.SetSlot(KugouInputBlocker.Slot.Main, Rect.zero, uiScale, false);
                return;
            }

            float pad = KugouTheme.Sizes.ShadowPad;
            float vw = _miniMode ? KugouTheme.Sizes.MiniWidth : KugouTheme.Sizes.WindowWidth;
            float vh = _miniMode ? KugouTheme.Sizes.MiniHeight : KugouTheme.Sizes.WindowHeight;
            var visualScreenRect = new Rect(_windowRect.x + pad, _windowRect.y + pad, vw, vh);
            _inputBlocker.SetSlot(KugouInputBlocker.Slot.Main, visualScreenRect, uiScale, true);
        }

        public void Draw()
        {
            if (!_visible)
            {
                return;
            }

            float scale = KugouTheme.ResolveUiScale();
            float pad = KugouTheme.Sizes.ShadowPad;
            float vw = _miniMode ? KugouTheme.Sizes.MiniWidth : KugouTheme.Sizes.WindowWidth;
            float vh = _miniMode ? KugouTheme.Sizes.MiniHeight : KugouTheme.Sizes.WindowHeight;

            _windowRect.width = vw + pad * 2f;
            _windowRect.height = vh + pad * 2f;

            float maxVisX = Mathf.Max(0f, (Screen.width / scale) - vw);
            float maxVisY = Mathf.Max(0f, (Screen.height / scale) - vh);
            float visX = Mathf.Clamp(_windowRect.x + pad, 0f, maxVisX);
            float visY = Mathf.Clamp(_windowRect.y + pad, 0f, maxVisY);
            _windowRect.x = visX - pad;
            _windowRect.y = visY - pad;

            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindowContents, string.Empty, GUIStyle.none);
            HandlePositionPersistence();
            SyncInputBlocker(scale);
        }

        public void OnFacilityReady(FacilityMusic facility, MusicPlayListTabUI tab)
        {
            if (_nativeInjector != null)
            {
                _nativeInjector.OnFacilityReady(tab, ToggleSource, Show);
            }
        }

        public void RefreshSavedPlaylists()
        {
            try
            {
                if (Plugin.Store != null)
                {
                    List<KugouPlaylistRecord> list = Plugin.Store.GetPlaylists();
                    _savedPlaylists = list != null ? list.ToArray() : new KugouPlaylistRecord[0];
                }
                else
                {
                    _savedPlaylists = new KugouPlaylistRecord[0];
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[UI] 读取本地歌单失败: " + ex.Message);
                }
            }
        }

        public void SetStatus(string text, bool error = false)
        {
            SetToastInternal(text, error, string.Empty, null);
        }

        public void SetToastWithAction(string text, string actionLabel, Action action)
        {
            SetToastInternal(text, false, actionLabel, action);
        }

        public void Dispose()
        {
            if (_pages != null)
            {
                for (int i = 0; i < _pages.Length; i++)
                {
                    _pages[i].Dispose();
                }
            }

            if (_covers != null)
            {
                _covers.Dispose();
                _covers = null;
            }

            if (_inputBlocker != null)
            {
                _inputBlocker.Dispose();
                _inputBlocker = null;
            }
        }

        // ================= 内部绘制 =================

        private void DrawWindowContents(int id)
        {
            float pad = KugouTheme.Sizes.ShadowPad;
            if (_miniMode)
            {
                var miniRect = new Rect(pad, pad, KugouTheme.Sizes.MiniWidth, KugouTheme.Sizes.MiniHeight);
                Rect dragArea;
                _nowPlayingBar.DrawMiniPlayer(miniRect, _nowPlayingModel, _covers, () => SetMiniMode(false), out dragArea);
                GUI.DragWindow(dragArea);
                return;
            }

            var winRect = new Rect(pad, pad, KugouTheme.Sizes.WindowWidth, KugouTheme.Sizes.WindowHeight);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.GlassPanel(winRect, KugouTheme.Sizes.RadiusWindow, true, 0.90f, 0.56f);
            }

            // 全窗圆角裁剪 Group 保证子背景不溢出窗口边界
            GUI.BeginGroup(winRect);
            var localWin = new Rect(0f, 0f, winRect.width, winRect.height);

            float headerH = KugouTheme.Sizes.HeaderHeight;
            float nowPlayingH = KugouTheme.Sizes.NowPlayingHeight;
            float railW = KugouTheme.Sizes.RailWidth;
            float bodyH = localWin.height - headerH - nowPlayingH;

            DrawHeader(new Rect(0f, 0f, localWin.width, headerH));
            DrawNavRail(new Rect(0f, headerH, railW, bodyH));

            var contentOuter = new Rect(railW, headerH, localWin.width - railW, bodyH);
            var contentInner = new Rect(
                contentOuter.x + KugouTheme.Sizes.ContentPadding,
                contentOuter.y + 16f,
                contentOuter.width - KugouTheme.Sizes.ContentPadding * 2f,
                contentOuter.height - 24f);

            DrawPageWithTransition(contentInner);
            DrawToastOverlay(new Rect(contentOuter.x, contentOuter.yMax - 46f, contentOuter.width, 36f));

            _nowPlayingBar.DrawFullBar(new Rect(0f, localWin.height - nowPlayingH, localWin.width, nowPlayingH), _nowPlayingModel, _covers);

            GUI.EndGroup();

            // 标题栏可拖拽区域（避开右半侧交互按钮）
            GUI.DragWindow(new Rect(pad, pad, winRect.width - 280f, headerH));
        }

        private void DrawHeader(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(r, KugouTheme.Colors.HeaderWash, 0f);
                KugouPainter.Divider(new Rect(r.x, r.yMax - 1f, r.width, 1f), 0f);

                // Logo 渐变圆徽
                var logoRect = new Rect(r.x + 16f, r.y + 13f, 30f, 30f);
                KugouPainter.GradientRect(logoRect, KugouTextures.BrandGradient, 15f, Color.white);
                KugouPainter.Icon(logoRect, IconCode.Music, 14, Color.white);

                // 品牌标题
                KugouTheme.Style(TypeToken.Brand, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(logoRect.xMax + 10f, r.y + 8f, 220f, 20f), KugouTheme.Content("酷狗音乐 · Chill With You"), false, false, false, false);

                // 状态副行：网关状态 + 登录态
                string statusText;
                Color dotColor;
                BuildHeaderStatusChip(out statusText, out dotColor);
                KugouWidgets.StatusDot(new Rect(logoRect.xMax + 11f, r.y + 34f, 6f, 6f), dotColor, dotColor == KugouTheme.Colors.Success);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(logoRect.xMax + 22f, r.y + 28f, 260f, 18f), KugouTheme.Content(statusText), false, false, false, false);
            }

            // 右侧控制区：关闭 / Mini / 歌词 / 音源切换分段胶囊
            float rightCursor = r.xMax - 14f;

            rightCursor -= 28f;
            if (KugouWidgets.IconButton(new Rect(rightCursor, r.y + 14f, 28f, 28f), IconCode.Close, false, true, 12))
            {
                Hide();
            }

            rightCursor -= 34f;
            if (KugouWidgets.IconButton(new Rect(rightCursor, r.y + 14f, 28f, 28f), IconCode.Minimize, false, true, 12))
            {
                SetMiniMode(true);
            }

            rightCursor -= 60f;
            bool lyricsOn = Plugin.EnableLyricsWindow != null && Plugin.EnableLyricsWindow.Value;
            if (KugouWidgets.Button(new Rect(rightCursor, r.y + 14f, 52f, 28f), "歌词", lyricsOn ? ButtonKind.Secondary : ButtonKind.Ghost))
            {
                if (Plugin.EnableLyricsWindow != null)
                {
                    Plugin.EnableLyricsWindow.Value = !lyricsOn;
                    Plugin.SaveConfig();
                    SetStatus(Plugin.EnableLyricsWindow.Value ? "桌面悬浮歌词已开启" : "桌面悬浮歌词已关闭");
                }
            }

            if (lyricsOn && Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.Circle(new Rect(rightCursor + 6f, r.y + 25f, 5f, 5f), KugouTheme.Colors.AccentSky);
            }

            rightCursor -= 142f;
            bool switching = Plugin.Switch != null && Plugin.Switch.IsSwitching;
            int curSrc = (Plugin.Switch != null && Plugin.Switch.IsKugouMode) ? 1 : 0;
            int nextSrc = KugouWidgets.Segmented(
                new Rect(rightCursor, r.y + 13f, 132f, 30f),
                curSrc,
                SourceToggleLabels,
                "hdr_src_seg".GetHashCode(),
                switching,
                switching);
            if (nextSrc != curSrc && !switching)
            {
                ToggleSource();
            }
        }

        private void DrawNavRail(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(r, KugouTheme.Colors.RailBg, 0f);
                KugouPainter.RoundRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), KugouTheme.Colors.Divider, 0f);
            }

            if (_pages != null)
            {
                float itemY = r.y + 14f;
                for (int i = 0; i < _pages.Length; i++)
                {
                    var itemRect = new Rect(r.x + 10f, itemY, r.width - 20f, 38f);
                    bool selected = i == _tab;
                    bool hover = KugouWidgets.IsHovered(itemRect);
                    float h = KugouAnim.Hover(("nav_item_" + i).GetHashCode(), hover);

                    if (Event.current != null && Event.current.type == EventType.Repaint)
                    {
                        if (selected)
                        {
                            KugouPainter.RoundRect(itemRect, KugouTheme.Colors.NavActiveBg, 10f);
                            KugouPainter.GradientRect(
                                new Rect(itemRect.x, itemRect.y + 10f, 3f, 18f),
                                KugouTextures.BrandGradient,
                                1.5f,
                                Color.white);
                        }
                        else if (h > 0.01f)
                        {
                            KugouPainter.RoundRect(itemRect, KugouTheme.WithAlpha(Color.white, 0.045f * h), 10f);
                        }

                        Color fg = selected
                            ? KugouTheme.Colors.TextPrimary
                            : Color.Lerp(KugouTheme.Colors.TextSecondary, KugouTheme.Colors.TextPrimary, h);
                        KugouPainter.Icon(
                            new Rect(itemRect.x + 12f, itemRect.y + 9f, 18f, 20f),
                            _pages[i].Icon,
                            15,
                            selected ? KugouTheme.Colors.AccentSky : fg);
                        KugouTheme.Style(selected ? TypeToken.BodyStrong : TypeToken.Body, fg, TextAnchor.MiddleLeft)
                                  .Draw(new Rect(itemRect.x + 36f, itemRect.y, itemRect.width - 42f, itemRect.height),
                                        KugouTheme.Content(_pages[i].Title),
                                        false, false, false, false);
                    }

                    if (GUI.Button(itemRect, GUIContent.none, GUIStyle.none))
                    {
                        GoToPage(i);
                    }

                    itemY += 44f;
                }
            }

            // 底部账号小卡
            var miniAcc = new Rect(r.x + 10f, r.yMax - 62f, r.width - 20f, 50f);
            bool accHover = KugouWidgets.IsHovered(miniAcc);
            bool loggedIn = Plugin.IsLoggedIn();
            string nick = _accountPage != null && !string.IsNullOrEmpty(_accountPage.CachedNickname)
                ? _accountPage.CachedNickname
                : (loggedIn ? "已登录" : "游客模式");
            bool isVip = _accountPage != null && _accountPage.CachedIsVip;

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(
                    miniAcc,
                    accHover ? KugouTheme.Colors.CardHover : KugouTheme.WithAlpha(KugouTheme.Colors.Card, 0.55f),
                    10f);
                KugouPainter.Stroke(miniAcc, KugouTheme.Colors.Divider, 10f, 1f);

                var avRect = new Rect(miniAcc.x + 8f, miniAcc.y + 10f, 30f, 30f);
                if (loggedIn)
                {
                    KugouPainter.GradientRect(avRect, KugouTextures.BrandGradient, 15f, Color.white);
                    KugouTheme.Style(TypeToken.BodyStrong, Color.white, TextAnchor.MiddleCenter)
                              .Draw(avRect, KugouTheme.Content(KugouWidgets.FirstMonogram(nick)), false, false, false, false);
                }
                else
                {
                    KugouPainter.Circle(avRect, KugouTheme.Colors.DisabledFill);
                    KugouPainter.Icon(avRect, IconCode.Account, 14, KugouTheme.Colors.TextTertiary);
                }

                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(avRect.xMax + 8f, miniAcc.y + 7f, miniAcc.width - 48f, 18f),
                                KugouTheme.Content(nick), false, false, false, false);

                string tag = isVip ? "VIP 会员" : (loggedIn ? "在线" : "点击登录");
                Color tagCol = isVip ? KugouTheme.Colors.BadgeVipText : (loggedIn ? KugouTheme.Colors.Success : KugouTheme.Colors.TextTertiary);
                KugouTheme.Style(TypeToken.Micro, tagCol, TextAnchor.MiddleLeft)
                          .Draw(new Rect(avRect.xMax + 8f, miniAcc.y + 25f, miniAcc.width - 48f, 16f),
                                KugouTheme.Content(tag), false, false, false, false);
            }

            if (GUI.Button(miniAcc, GUIContent.none, GUIStyle.none))
            {
                GoToPage(2);
            }
        }

        private void DrawPageWithTransition(Rect contentInner)
        {
            if (_pages == null || _tab < 0 || _tab >= _pages.Length)
            {
                return;
            }

            float alpha = 1f;
            float offsetX = 0f;
            if (!KugouAnim.ReduceMotion)
            {
                float elapsed = Time.unscaledTime - _tabChangedAt;
                if (elapsed >= 0f && elapsed < 0.18f)
                {
                    float e = KugouAnim.EaseOutCubic(elapsed / 0.18f);
                    alpha = e;
                    offsetX = (1f - e) * 8f;
                }
            }

            Color prevCol = GUI.color;
            GUI.color = new Color(prevCol.r, prevCol.g, prevCol.b, prevCol.a * alpha);
            GUI.BeginGroup(new Rect(contentInner.x + offsetX, contentInner.y, contentInner.width, contentInner.height));
            _pages[_tab].Draw(new Rect(0f, 0f, contentInner.width, contentInner.height));
            GUI.EndGroup();
            GUI.color = prevCol;
        }

        private void DrawToastOverlay(Rect area)
        {
            if (string.IsNullOrEmpty(_toastText))
            {
                return;
            }

            float enter = KugouAnim.ReduceMotion ? 1f : KugouAnim.EaseOutCubic(Mathf.Clamp01((Time.unscaledTime - _toastShownAt) / 0.20f));
            float remain = _toastExpire > 0f ? _toastExpire - Time.unscaledTime : 3f;
            float exit = remain < 0.25f ? Mathf.Clamp01(remain / 0.25f) : 1f;
            float alpha = enter * exit;
            if (alpha <= 0.01f)
            {
                return;
            }

            GUIStyle st = KugouTheme.Style(TypeToken.Body, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft);
            float textW = Mathf.Min(360f, st.CalcSize(KugouTheme.Content(_toastText)).x);
            bool hasAction = !string.IsNullOrEmpty(_toastActionLabel) && _toastActionCallback != null;
            float actionW = hasAction ? 74f : 0f;
            float pillW = Mathf.Clamp(36f + textW + actionW + (hasAction ? 10f : 0f), 140f, area.width - 24f);
            float yOffset = (1f - enter) * 8f;
            var pill = new Rect(area.center.x - pillW * 0.5f, area.y + yOffset, pillW, 30f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.Shadow(pill, 15f, KugouTheme.WithAlpha(KugouTheme.Colors.Shadow, 0.55f * alpha), 10, 3f);
                KugouPainter.RoundRect(pill, KugouTheme.WithAlpha(KugouTheme.Colors.Elevated, 0.96f * alpha), 15f);
                KugouPainter.Stroke(pill, KugouTheme.WithAlpha(_toastError ? KugouTheme.Colors.Error : KugouTheme.Colors.StrokeStrong, 0.45f * alpha), 15f, 1f);

                Color dotCol = _toastError ? KugouTheme.Colors.Error : KugouTheme.Colors.Success;
                KugouWidgets.StatusDot(new Rect(pill.x + 12f, pill.y + 11.5f, 7f, 7f), dotCol, false);

                float maxTextW = pillW - 34f - (hasAction ? actionW + 8f : 0f);
                st.Draw(new Rect(pill.x + 24f, pill.y, maxTextW, pill.height), KugouTheme.Content(_toastText), false, false, false, false);
            }

            if (hasAction)
            {
                var actionRect = new Rect(pill.xMax - actionW - 6f, pill.y + 4f, actionW, 22f);
                if (KugouWidgets.Button(actionRect, _toastActionLabel, ButtonKind.Primary))
                {
                    Action cb = _toastActionCallback;
                    _toastText = string.Empty;
                    _toastActionCallback = null;
                    cb?.Invoke();
                }
            }

            GUI.color = prev;
        }

        // ================= 协调与业务转发 =================

        private void OnWindowOpened()
        {
            RefreshSavedPlaylists();
            if (_pages != null && _tab >= 0 && _tab < _pages.Length)
            {
                _pages[_tab].OnShow();
            }

            if (_nowPlayingModel != null)
            {
                _nowPlayingModel.Tick();
            }

            SyncInputBlocker(KugouTheme.ResolveUiScale());
        }

        private void GoToPage(int index)
        {
            if (_pages == null || index < 0 || index >= _pages.Length)
            {
                return;
            }

            if (_tab == index)
            {
                _pages[index].OnShow();
                return;
            }

            _tab = index;
            _tabChangedAt = Time.unscaledTime;
            _pages[index].OnShow();
        }

        private void SetMiniMode(bool mini)
        {
            if (_miniMode == mini)
            {
                return;
            }

            float pad = KugouTheme.Sizes.ShadowPad;
            float visX = _windowRect.x + pad;
            float visY = _windowRect.y + pad;

            _miniMode = mini;
            if (Plugin.WindowMiniMode != null)
            {
                Plugin.WindowMiniMode.Value = mini;
                Plugin.SaveConfig();
            }

            float nextW = mini ? KugouTheme.Sizes.MiniWidth : KugouTheme.Sizes.WindowWidth;
            float nextH = mini ? KugouTheme.Sizes.MiniHeight : KugouTheme.Sizes.WindowHeight;
            _windowRect = new Rect(visX - pad, visY - pad, nextW + pad * 2f, nextH + pad * 2f);
            SyncInputBlocker(KugouTheme.ResolveUiScale());
        }

        private void SetToastInternal(string text, bool error, string actionLabel, Action action)
        {
            _toastText = text ?? string.Empty;
            _toastError = error;
            _toastActionLabel = actionLabel ?? string.Empty;
            _toastActionCallback = action;

            try
            {
                float duration = error ? 6f : 3.5f;
                _toastExpire = Time.unscaledTime + duration;
                _toastShownAt = Time.unscaledTime;
            }
            catch
            {
                _toastExpire = -1f;
                _toastShownAt = 0f;
            }
        }

        private void BuildHeaderStatusChip(out string text, out Color dotColor)
        {
            string auth = Plugin.IsLoggedIn() ? "已登录" : "游客";
            if (Plugin.GetBackendMode() == KugouBackendMode.NativeDirect)
            {
                text = "直连模式 · " + auth;
                dotColor = Plugin.IsLoggedIn() ? KugouTheme.Colors.Success : KugouTheme.Colors.AccentSky;
                return;
            }

            GatewayState gw = Plugin.Gateway != null ? Plugin.Gateway.GetState() : GatewayState.Stopped;
            switch (gw)
            {
                case GatewayState.RunningOwned:
                case GatewayState.RunningExternal:
                    text = "本地网关 运行中 · " + auth;
                    dotColor = KugouTheme.Colors.Success;
                    break;
                case GatewayState.Starting:
                    text = "本地网关 启动中 · " + auth;
                    dotColor = KugouTheme.Colors.Warning;
                    break;
                case GatewayState.Remote:
                    text = "远程网关 · " + auth;
                    dotColor = KugouTheme.Colors.AccentSky;
                    break;
                default:
                    text = "本地网关 未运行 · " + auth;
                    dotColor = KugouTheme.Colors.TextTertiary;
                    break;
            }
        }

        private void ToggleSource()
        {
            try
            {
                Plugin.EnsureClientCurrent();
                Plugin.Switch.ToggleSource();
                if (_nativeInjector != null)
                {
                    _nativeInjector.ForceRefresh();
                }
            }
            catch (Exception ex)
            {
                SetStatus("切换音源失败: " + ex.Message, true);
            }
        }

        private async UniTask ImportPlaylistAsync(string input, bool switchAfter)
        {
            IKugouClient client = Plugin.Client;
            if (client == null)
            {
                SetStatus("接口客户端未初始化", true);
                return;
            }

            SetStatus("正在解析歌单…", false);
            try
            {
                KugouPlaylistDetail detail = await client.ParsePlaylistInputAsync(input, CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);

                if (detail == null || detail.Status != KugouApiStatus.Ok || detail.Tracks.Count == 0)
                {
                    SetStatus("导入失败: " + (detail != null ? detail.Message : "无响应"), true);
                    return;
                }

                string specialId = string.IsNullOrEmpty(detail.Summary.SpecialId) ? input : detail.Summary.SpecialId;
                string name = string.IsNullOrEmpty(detail.Summary.Name) ? "未命名歌单" : detail.Summary.Name;

                if (Plugin.Store != null)
                {
                    Plugin.Store.SavePlaylist(new KugouPlaylistRecord
                    {
                        SpecialId = specialId,
                        Name = name,
                        Author = detail.Summary.Author,
                        UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Tracks = new List<KugouTrack>(detail.Tracks),
                    });
                }

                if (Plugin.ActivePlaylistId != null)
                {
                    Plugin.ActivePlaylistId.Value = specialId;
                    Plugin.SaveConfig();
                }

                RefreshSavedPlaylists();

                if (Plugin.Switch != null)
                {
                    Plugin.Switch.SetKugouTracks(detail.Tracks);
                    if (switchAfter)
                    {
                        Plugin.Switch.SwitchToKugou();
                    }
                }

                SetStatus("已导入《" + name + "》" + detail.Tracks.Count + " 首", false);
            }
            catch (Exception ex)
            {
                SetStatus("导入异常: " + ex.Message, true);
            }
        }

        private void SwitchPlaylist(string specialId)
        {
            try
            {
                KugouPlaylistRecord record = Plugin.Store != null ? Plugin.Store.GetPlaylist(specialId) : null;
                if (record == null || record.Tracks == null || record.Tracks.Count == 0)
                {
                    SetStatus("本地没有该歌单曲目，请点击刷新图标重试", true);
                    return;
                }

                if (Plugin.ActivePlaylistId != null)
                {
                    Plugin.ActivePlaylistId.Value = specialId;
                    Plugin.SaveConfig();
                }

                Plugin.Switch.SetKugouTracks(record.Tracks);
                Plugin.Switch.SwitchToKugou();
                SetStatus("已切换到《" + (string.IsNullOrEmpty(record.Name) ? specialId : record.Name) + "》", false);
            }
            catch (Exception ex)
            {
                SetStatus("切换歌单失败: " + ex.Message, true);
            }
        }

        private void HandlePositionPersistence()
        {
            float pad = KugouTheme.Sizes.ShadowPad;
            float visX = _windowRect.x + pad;
            float visY = _windowRect.y + pad;

            if (float.IsNaN(_lastPosX) || float.IsNaN(_lastPosY))
            {
                _lastPosX = visX;
                _lastPosY = visY;
                return;
            }

            if (Mathf.Abs(visX - _lastPosX) > 0.5f || Mathf.Abs(visY - _lastPosY) > 0.5f)
            {
                _lastPosX = visX;
                _lastPosY = visY;
                _positionDirty = true;
            }

            if (_positionDirty && Event.current != null && Event.current.type == EventType.MouseUp)
            {
                _positionDirty = false;
                try
                {
                    if (Plugin.WindowPosX != null) Plugin.WindowPosX.Value = Mathf.RoundToInt(visX);
                    if (Plugin.WindowPosY != null) Plugin.WindowPosY.Value = Mathf.RoundToInt(visY);
                    Plugin.SaveConfig();
                }
                catch (Exception ex)
                {
                    if (Plugin.Log != null)
                    {
                        Plugin.Log.LogWarning("[UI] 保存窗口位置失败: " + ex.Message);
                    }
                }
            }
        }
    }
}
