using System;
using System.Net;
using BepInEx;
using Bulbul;
using BepInEx.Configuration;
using BepInEx.Logging;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Patches;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI;
using ChillWithYou.KugouMusic.UI.Theme;
using HarmonyLib;
using UnityEngine;

namespace ChillWithYou.KugouMusic
{
    /// <summary>
    /// 酷狗音乐双音源插件的 BepInEx 入口。
    /// P0：骨架 + 核心补丁 + 日志验证；网络与 UI 在 P1 / P2 接入。
    /// </summary>
    [BepInPlugin(KugouIds.PluginGuid, KugouIds.PluginName, KugouIds.PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        // ---------------- 全局实例 ----------------

        internal static Plugin Instance { get; private set; }

        internal static ManualLogSource Log { get; private set; }

        internal static KugouTrackRegistry Registry { get; private set; }

        internal static KugouStorage Store { get; private set; }

        internal static SourceSwitchService Switch { get; private set; }

        internal static IHttpTransport Http { get; private set; }

        /// <summary>当前生效的酷狗接口客户端（NativeDirect / ExternalGateway）。</summary>
        internal static IKugouClient Client { get; private set; }

        /// <summary>音频缓存（下载 / LRU / 预加载）。</summary>
        internal static AudioCacheService AudioCache { get; private set; }

        /// <summary>本地 KuGouMusicApi 网关进程托管（随游戏自动启停）。</summary>
        internal static GatewayProcessService Gateway { get; private set; }

        /// <summary>P2 悬浮窗（IMGUI）+ 原生按钮注入。</summary>
        internal static KugouWindow Window { get; private set; }

        /// <summary>实时歌词跟踪与缓存服务。</summary>
        internal static LyricService Lyrics { get; private set; }

        /// <summary>可自由拖动的桌面悬浮歌词框。</summary>
        internal static LyricWindow LyricsUi { get; private set; }

        /// <summary>GitHub Releases 在线检查与更新服务。</summary>
        internal static GitHubUpdateService Updater { get; private set; }

        /// <summary>原生音乐 Tab UI（由 FacilityMusic.Setup 补丁缓存，用于控制导入按钮）。</summary>
        internal static MusicPlayListTabUI MusicTabUi { get; set; }

        /// <summary>音乐设施实例（用于切源后同步标题/歌手等界面状态）。</summary>
        internal static FacilityMusic FacilityMusicInstance { get; set; }

        internal static bool IsShuttingDown { get; private set; }

        private static string _clientSignature = string.Empty;

        // ---------------- 配置项 ----------------

        // [General]
        internal static ConfigEntry<string> ActiveSource;
        internal static ConfigEntry<string> ActivePlaylistId;
        internal static ConfigEntry<KeyCode> ToggleHotkey;
        internal static ConfigEntry<bool> AutoResumeLastTrack;

        // [KugouApi]
        internal static ConfigEntry<string> BackendMode;
        internal static ConfigEntry<string> ExternalApiBaseUrl;
        internal static ConfigEntry<bool> AutoStartGateway;
        internal static ConfigEntry<string> PreferredQuality;
        internal static ConfigEntry<bool> SkipTrialTracks;

        // [Auth]
        internal static ConfigEntry<string> UserId;
        internal static ConfigEntry<string> Token;
        internal static ConfigEntry<string> Cookie;
        internal static ConfigEntry<string> Dfid;

        // [Cache]
        internal static ConfigEntry<int> MaxCacheSizeMB;
        internal static ConfigEntry<bool> PreloadNextTrack;

        // [UI]
        internal static ConfigEntry<int> WindowPosX;
        internal static ConfigEntry<int> WindowPosY;
        internal static ConfigEntry<bool> WindowMiniMode;
        internal static ConfigEntry<float> UiScale;
        internal static ConfigEntry<bool> ReduceMotion;
        internal static ConfigEntry<bool> EnableLyricsWindow;
        internal static ConfigEntry<int> LyricsPosX;
        internal static ConfigEntry<int> LyricsPosY;
        internal static ConfigEntry<int> LyricsWidth;
        internal static ConfigEntry<int> LyricsFontSize;
        internal static ConfigEntry<float> LyricsBackgroundAlpha;
        internal static ConfigEntry<bool> LyricsLocked;
        internal static ConfigEntry<float> LyricsOffsetSeconds;

        // [Update]
        internal static ConfigEntry<string> GitHubRepo;
        internal static ConfigEntry<bool> AutoCheckUpdates;

        // [Debug]（P0 专用开关）
        internal static ConfigEntry<bool> EnableVerboseLog;
        internal static ConfigEntry<bool> EnableTagFilterPatch;
        internal static ConfigEntry<bool> EnableUnloadGuard;
        internal static ConfigEntry<bool> UseUnityWebRequestTransport;
        internal static ConfigEntry<string> P0TestAudioPath;

        private Harmony _harmony;

        // ---------------- 生命周期 ----------------

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            gameObject.hideFlags |= HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);
            Registry = new KugouTrackRegistry();

            Log.LogInfo("==================================================");
            Log.LogInfo(" " + KugouIds.PluginName + " v" + KugouIds.PluginVersion + " 正在初始化");
            Log.LogInfo(" Unity=" + Application.unityVersion + " 后台=" + Application.platform);
            Log.LogInfo("==================================================");

            BindConfig();

            Gateway = new GatewayProcessService();
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Gateway.EnsureStarted();

            InitializeTls();

            try
            {
                KugouPaths.EnsureDirectories();
                Store = KugouStorage.Create();
                Log.LogInfo("[Init] 本地存储: " + KugouPaths.StoreFilePath);
                Log.LogInfo("[Init] 缓存目录: " + KugouPaths.CacheDir);
            }
            catch (Exception ex)
            {
                Log.LogError("[Init] 本地存储初始化失败: " + ex);
                Store = new KugouStorage();
            }

            try
            {
                Http = HttpTransportFactory.Create();
            }
            catch (Exception ex)
            {
                Log.LogError("[Init] HTTP 传输层初始化失败，降级为 HttpClient: " + ex.Message);
                Http = new HttpClientTransport();
            }

            AudioCache = new AudioCacheService();
            Switch = new SourceSwitchService();
            RebuildClient("初始化");

            Window = new KugouWindow();
            Window.Initialize();

            Lyrics = new LyricService();
            LyricsUi = new LyricWindow();
            LyricsUi.Initialize();

            Updater = new GitHubUpdateService();

            try
            {
                _harmony = new Harmony(KugouIds.PluginGuid);
                MusicPatches.Install(_harmony);
                Log.LogInfo("[Init] Harmony 补丁装配完成");
            }
            catch (Exception ex)
            {
                Log.LogError("[Init] Harmony 装配失败: " + ex);
            }

            Log.LogInfo("[Init] 完成。按 " + (ToggleHotkey != null ? ToggleHotkey.Value.ToString() : "F8") +
                        " 显示/隐藏酷狗面板（音源切换在面板顶部或原生按钮）");
        }

        private void Update()
        {
            try
            {
                if (Window != null)
                {
                    Window.Tick();
                }

                bool lyricsEnabled = EnableLyricsWindow != null && EnableLyricsWindow.Value;
                if (Lyrics != null && lyricsEnabled)
                {
                    Lyrics.Tick();
                }

                if (LyricsUi != null)
                {
                    LyricsUi.SyncInputBlocker(KugouTheme.ResolveUiScale());
                }

                if (ToggleHotkey == null || ToggleHotkey.Value == KeyCode.None)
                {
                    return;
                }

                if (Input.GetKeyDown(ToggleHotkey.Value))
                {
                    EnsureClientCurrent();

                    if (Window != null)
                    {
                        Window.Toggle();
                        Log.LogInfo("[UI] 热键 " + ToggleHotkey.Value + " 触发，悬浮窗状态: " +
                                    (Window.IsVisible ? "显示" : "隐藏"));
                    }

                    LogStatus();
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[Update] 热键处理异常: " + ex);
            }
        }

        private void OnGUI()
        {
            try
            {
                bool showMain = Window != null && Window.IsVisible;
                bool showLyrics = LyricsUi != null && EnableLyricsWindow != null && EnableLyricsWindow.Value;
                if (!showMain && !showLyrics)
                {
                    return;
                }

                float scale = KugouTheme.ResolveUiScale();
                using (KugouTheme.Scope(scale))
                {
                    if (showMain)
                    {
                        Window.Draw();
                    }

                    if (showLyrics)
                    {
                        LyricsUi.Draw();
                    }
                }
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogError("[UI] OnGUI 异常: " + ex);
                }
            }
        }

        private void OnApplicationQuit()
        {
            Shutdown("OnApplicationQuit");
        }

        private void OnDestroy()
        {
            Shutdown("OnDestroy");
        }

        private void Shutdown(string reason)
        {
            if (IsShuttingDown)
            {
                return;
            }

            IsShuttingDown = true;
            Log.LogInfo("[Shutdown] " + reason + "，暂停播放并保存本地数据");

            EnsurePausedAndClearAutoStart(
                Switch != null ? Switch.Service : null,
                "[Shutdown] 已暂停歌曲并关闭下次启动自启播放");

            try
            {
                if (Switch != null)
                {
                    Switch.Detach();
                }
            }
            catch
            {
                // 退出阶段忽略
            }

            try
            {
                KugouPlayback.CancelPending();
            }
            catch
            {
                // 退出阶段忽略
            }

            try
            {
                if (Store != null)
                {
                    Store.Save();
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Shutdown] 保存失败: " + ex.Message);
            }

            try
            {
                if (Window != null)
                {
                    Window.Dispose();
                }

                if (LyricsUi != null)
                {
                    LyricsUi.Dispose();
                }

                if (Lyrics != null)
                {
                    Lyrics.Dispose();
                }

                KugouPainter.Dispose();
                KugouTheme.Dispose();
                KugouTextures.DisposeAll();
                KugouAnim.Clear();
            }
            catch
            {
                // 退出阶段忽略
            }

            try
            {
                if (Client != null)
                {
                    Client.Dispose();
                    Client = null;
                }

                if (Updater != null)
                {
                    Updater.Dispose();
                    Updater = null;
                }
            }
            catch
            {
                // 退出阶段忽略
            }

            try
            {
                if (Http != null)
                {
                    Http.Dispose();
                }
            }
            catch
            {
                // 退出阶段忽略
            }

            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchSelf();
                }
            }
            catch
            {
                // 退出阶段忽略
            }

            try
            {
                if (Gateway != null)
                {
                    Gateway.StopIfOwned();
                }
            }
            catch
            {
                // 退出阶段忽略
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            if (Gateway != null)
            {
                Gateway.StopIfOwned();
            }
        }

        // ---------------- 配置 ----------------

        private void BindConfig()
        {
            ActiveSource = Config.Bind(
                "General",
                "ActiveSource",
                "Official",
                "当前音源：Official 或 Kugou（切换时自动保存）");

            ActivePlaylistId = Config.Bind(
                "General",
                "ActivePlaylistId",
                string.Empty,
                "上次使用的酷狗歌单 ID");

            ToggleHotkey = Config.Bind(
                "General",
                "ToggleHotkey",
                KeyCode.F8,
                "开关酷狗面板 / 切换音源的热键");

            AutoResumeLastTrack = Config.Bind(
                "General",
                "AutoResumeLastTrack",
                true,
                "进入房间时自动恢复上次音源与曲目");

            BackendMode = Config.Bind(
                "KugouApi",
                "BackendMode",
                "NativeDirect",
                "接口后端：NativeDirect（直连酷狗）或 ExternalGateway（本地 KuGouMusicApi 网关）");

            ExternalApiBaseUrl = Config.Bind(
                "KugouApi",
                "ExternalApiBaseUrl",
                "http://127.0.0.1:3000",
                "ExternalGateway 模式下的本地网关地址");

            AutoStartGateway = Config.Bind(
                "KugouApi",
                "AutoStartGateway",
                true,
                "随游戏自动启动与关闭本地 KuGouMusicApi 网关（仅 ExternalGateway 本机地址生效）");

            PreferredQuality = Config.Bind(
                "KugouApi",
                "PreferredQuality",
                "320",
                "目标音质：128 / 320 / m4a");

            SkipTrialTracks = Config.Bind(
                "KugouApi",
                "SkipTrialTracks",
                true,
                "遇到试听片段或无版权曲目时自动跳过");

            UserId = Config.Bind("Auth", "UserId", string.Empty, "酷狗账号 userid");
            Token = Config.Bind("Auth", "Token", string.Empty, "酷狗账号 token");
            Cookie = Config.Bind("Auth", "Cookie", string.Empty, "酷狗账号 Cookie（手动导入）");
            Dfid = Config.Bind("Auth", "Dfid", string.Empty, "设备 dfid（可选）");

            MaxCacheSizeMB = Config.Bind("Cache", "MaxCacheSizeMB", 500, "音频缓存上限（MB）");
            PreloadNextTrack = Config.Bind("Cache", "PreloadNextTrack", true, "后台预加载下一首");

            WindowPosX = Config.Bind("UI", "WindowPosX", 100, "悬浮窗 X 坐标");
            WindowPosY = Config.Bind("UI", "WindowPosY", 100, "悬浮窗 Y 坐标");
            WindowMiniMode = Config.Bind("UI", "WindowMiniMode", false, "主悬浮窗是否处于 Mini Player 胶囊模式");
            UiScale = Config.Bind("UI", "UiScale", 0f, "界面缩放倍率（0=按屏幕高度自动适配，手动范围 0.85~1.60）");
            ReduceMotion = Config.Bind("UI", "ReduceMotion", false, "减弱动效（关闭黑胶旋转/律动条/微光动画）");

            EnableLyricsWindow = Config.Bind("UI", "EnableLyricsWindow", false, "是否开启可自由拖动的悬浮歌词框");
            LyricsPosX = Config.Bind("UI", "LyricsPosX", -1, "悬浮歌词框 X 坐标（-1 为首次自动居中）");
            LyricsPosY = Config.Bind("UI", "LyricsPosY", -1, "悬浮歌词框 Y 坐标（-1 为首次自动贴底）");
            LyricsWidth = Config.Bind("UI", "LyricsWidth", 560, "悬浮歌词框宽度（像素，320~960）");
            LyricsFontSize = Config.Bind("UI", "LyricsFontSize", 22, "悬浮歌词框主字号（14~36）");
            LyricsBackgroundAlpha = Config.Bind("UI", "LyricsBackgroundAlpha", 0.45f, "悬浮歌词框背景透明度（0=全透明，1=纯黑底）");
            LyricsLocked = Config.Bind("UI", "LyricsLocked", false, "是否锁定悬浮歌词框位置（false 时可左键任意拖动）");
            LyricsOffsetSeconds = Config.Bind("UI", "LyricsOffsetSeconds", 0f, "歌词时间偏移（秒，正数提前、负数延后）");

            string initialRepo = KugouPaths.ReadRepoOverride();
            if (string.IsNullOrEmpty(initialRepo))
            {
                initialRepo = KugouIds.DefaultGitHubRepo;
            }

            GitHubRepo = Config.Bind(
                "Update",
                "GitHubRepo",
                initialRepo ?? string.Empty,
                "GitHub 更新仓库（格式：owner/repo 或完整仓库链接）");
            AutoCheckUpdates = Config.Bind(
                "Update",
                "AutoCheckUpdates",
                true,
                "配置了 GitHub 仓库后，打开设置/启动时是否允许检测更新");

            EnableVerboseLog = Config.Bind("Debug", "EnableVerboseLog", true, "输出 Hook 命中日志（P0 验证用）");
            EnableTagFilterPatch = Config.Bind("Debug", "EnableTagFilterPatch", true, "放行酷狗曲目的标签页筛选");
            EnableUnloadGuard = Config.Bind("Debug", "EnableUnloadGuard", true, "保护官方曲目不被卸载（保证可切回）");
            UseUnityWebRequestTransport = Config.Bind("Debug", "UseUnityWebRequestTransport", false, "改用 UnityWebRequest 作为 HTTP 传输层");
            P0TestAudioPath = Config.Bind(
                "Debug",
                "P0TestAudioPath",
                string.Empty,
                "P0 验证用：填一个本机音频绝对路径（mp3/m4a/wav），占位曲目将用它验证播放链路");
        }

        // ---------------- 接口客户端 ----------------

        /// <summary>
        /// 按当前配置重建 IKugouClient（运行时无缝切换 NativeDirect / ExternalGateway）。
        /// </summary>
        internal static void RebuildClient(string reason)
        {
            if (Instance == null)
            {
                return;
            }

            try
            {
                string signature = BuildClientSignature();
                if (Client != null && string.Equals(signature, _clientSignature, StringComparison.Ordinal))
                {
                    return;
                }

                if (Client != null)
                {
                    try
                    {
                        Client.Dispose();
                    }
                    catch
                    {
                        // 旧客户端释放失败不影响新客户端
                    }
                }

                KugouBackendMode mode = GetBackendMode();
                if (mode == KugouBackendMode.ExternalGateway)
                {
                    if (Gateway != null)
                    {
                        Gateway.EnsureStarted();
                    }

                    Client = new ExternalApiKugouClient(
                        Http,
                        ExternalApiBaseUrl != null ? ExternalApiBaseUrl.Value : "http://127.0.0.1:3000",
                        Token != null ? Token.Value : string.Empty,
                        UserId != null ? UserId.Value : string.Empty,
                        Cookie != null ? Cookie.Value : string.Empty);
                }
                else
                {
                    Client = new NativeKugouClient(
                        Http,
                        Cookie != null ? Cookie.Value : string.Empty,
                        Dfid != null ? Dfid.Value : string.Empty,
                        UserId != null ? UserId.Value : string.Empty,
                        Token != null ? Token.Value : string.Empty);
                }

                _clientSignature = signature;
                Log.LogInfo("[Client] 已启用 " + Client.DisplayName + "（" + reason + "）");
            }
            catch (Exception ex)
            {
                Log.LogError("[Client] 客户端初始化失败: " + ex);
            }
        }

        /// <summary>当前配置是否期望使用酷狗音源。</summary>
        internal static bool IsKugouDesired()
        {
            return ActiveSource != null &&
                   string.Equals(ActiveSource.Value, "Kugou", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>清除游戏“启动时自动继续播放”存档标志，确保加载期间不开启歌曲。</summary>
        internal static bool EnsureStartupPausedSetting()
        {
            try
            {
                SaveDataManager save = SaveDataManager.Instance;
                if (save == null || save.MusicSetting == null || !save.MusicSetting.IsGameStartPlayMusic)
                {
                    return false;
                }

                save.MusicSetting.IsGameStartPlayMusic = false;
                save.SaveMusicSetting();
                return true;
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning("[Pause] 更新启动播放标记失败: " + ex.Message);
                }

                return false;
            }
        }

        /// <summary>暂停当前播放并清除下次启动自启播放标记（退出程序与启动切源共用）。</summary>
        internal static void EnsurePausedAndClearAutoStart(MusicService service, string logOnPaused = null)
        {
            try
            {
                KugouPlayback.CancelPending();
                MusicPatchTargets.CancelAudioLoad(service);
            }
            catch
            {
                // 退出或切源阶段忽略
            }

            bool paused = false;
            try
            {
                FacilityMusic facility = FacilityMusicInstance;
                if (facility != null)
                {
                    bool autoStart = SaveDataManager.Instance != null &&
                                     SaveDataManager.Instance.MusicSetting != null &&
                                     SaveDataManager.Instance.MusicSetting.IsGameStartPlayMusic;

                    if (!facility.IsPaused || autoStart)
                    {
                        facility.PauseMusic();
                        paused = true;
                    }
                    else if (SourceSwitchService.IsAudioPlayerActive(service))
                    {
                        service.Pause();
                        paused = true;
                    }
                }
                else
                {
                    paused = PauseServiceFallback(service);
                }
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning("[Pause] 暂停处理回退: " + ex.Message);
                }

                paused = PauseServiceFallback(service);
            }

            if (paused && !string.IsNullOrEmpty(logOnPaused) && Log != null)
            {
                Log.LogInfo(logOnPaused);
            }
        }

        private static bool PauseServiceFallback(MusicService service)
        {
            bool paused = false;
            try
            {
                if (service != null && service.PlayingMusic != null)
                {
                    service.Pause();
                    paused = true;
                }
            }
            catch
            {
                // 退出阶段忽略
            }

            if (EnsureStartupPausedSetting())
            {
                paused = true;
            }

            return paused;
        }

        /// <summary>热键 / UI 操作前调用：配置变了就自动重建客户端。</summary>
        internal static void EnsureClientCurrent()
        {
            try
            {
                if (Instance == null)
                {
                    return;
                }

                if (!string.Equals(BuildClientSignature(), _clientSignature, StringComparison.Ordinal))
                {
                    RebuildClient("配置变更");
                }
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning("[Client] 配置检查失败: " + ex.Message);
                }
            }
        }

        private static string BuildClientSignature()
        {
            return string.Concat(
                GetBackendMode().ToString(), "|",
                ExternalApiBaseUrl != null ? ExternalApiBaseUrl.Value : string.Empty, "|",
                PreferredQuality != null ? PreferredQuality.Value : string.Empty, "|",
                UserId != null ? UserId.Value : string.Empty, "|",
                Token != null ? Token.Value : string.Empty, "|",
                Cookie != null ? Cookie.Value : string.Empty, "|",
                Dfid != null ? Dfid.Value : string.Empty);
        }

        /// <summary>扫码登录成功后回写凭证（供 P2 登录页调用）。</summary>
        internal static void ApplyCredentials(string userId, string token, string cookie)
        {
            try
            {
                if (!string.IsNullOrEmpty(userId) && UserId != null)
                {
                    UserId.Value = userId;
                }

                if (!string.IsNullOrEmpty(token) && Token != null)
                {
                    Token.Value = token;
                }

                if (!string.IsNullOrEmpty(cookie) && Cookie != null)
                {
                    Cookie.Value = cookie;
                }

                if (Instance != null)
                {
                    Instance.Config.Save();
                }

                RebuildClient("凭证更新");
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning("[Client] 凭证回写失败: " + ex.Message);
                }
            }
        }

        private void InitializeTls()
        {
            try
            {
                // 酷狗全部接口要求 TLS 1.2；Mono 默认协议可能仍是 TLS 1.0
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Log.LogInfo("[Init] TLS 协议已启用: " + ServicePointManager.SecurityProtocol);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Init] TLS 设置失败（不影响 UnityWebRequest 兜底）: " + ex.Message);
            }
        }

        // ---------------- 状态输出 ----------------

        internal static void LogStatus()
        {
            try
            {
                if (Log == null)
                {
                    return;
                }

                Log.LogInfo("---------------- 状态 ----------------");
                Log.LogInfo(" 音源模式   : " + (Switch != null && Switch.IsKugouMode ? "Kugou" : "Official") +
                            "（配置值 " + (ActiveSource != null ? ActiveSource.Value : "?") + "）");
                Log.LogInfo(" 官方快照   : " + (Switch != null ? Switch.OfficialCount.ToString() : "0") + " 首");
                Log.LogInfo(" 酷狗列表   : " + (Switch != null ? Switch.KugouCount.ToString() : "0") + " 首");
                Log.LogInfo(" 后端模式   : " + (BackendMode != null ? BackendMode.Value : "NativeDirect"));
                Log.LogInfo(" 外部网关   : " + (ExternalApiBaseUrl != null ? ExternalApiBaseUrl.Value : "-"));
                Log.LogInfo(" 目标音质   : " + (PreferredQuality != null ? PreferredQuality.Value : "320"));
                Log.LogInfo(" 传输层     : " + (Http != null ? Http.Name : "未初始化"));
                Log.LogInfo(" 接口客户端 : " + (Client != null ? Client.DisplayName : "未初始化"));
                Log.LogInfo(" 缓存占用   : " + (AudioCache != null ? AudioCache.GetCacheSizeText() : "-"));
                Log.LogInfo(" 连续失败   : " + KugouPlayback.ConsecutiveFailures + "/5");
                Log.LogInfo(" 登录态     : " + (IsLoggedIn() ? "已登录" : "游客"));
                Log.LogInfo(" 测试音频   : " + (string.IsNullOrEmpty(P0TestAudioPath != null ? P0TestAudioPath.Value : null)
                    ? "未配置（Debug/P0TestAudioPath）"
                    : P0TestAudioPath.Value));
                Log.LogInfo("------------------------------------");
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning("[Status] 输出失败: " + ex.Message);
                }
            }
        }

        /// <summary>把配置写回 cfg（UI 修改后调用）。</summary>
        internal static void SaveConfig()
        {
            try
            {
                if (Instance != null && Instance.Config != null)
                {
                    Instance.Config.Save();
                }
            }
            catch (Exception ex)
            {
                if (Log != null)
                {
                    Log.LogWarning("[Config] 保存失败: " + ex.Message);
                }
            }
        }

        internal static bool IsLoggedIn()
        {
            string token = Token != null ? Token.Value : null;
            if (!string.IsNullOrEmpty(token))
            {
                return true;
            }

            string cookie = Cookie != null ? Cookie.Value : null;
            if (string.IsNullOrEmpty(cookie))
            {
                return false;
            }

            // 仅凭 dfid / KUGOU_API_MID 等匿名设备 Cookie 不能算作已登录，必须包含非空 token=
            string[] segments = cookie.Split(';');
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i].Trim();
                if (seg.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
                {
                    string val = seg.Substring("token=".Length).Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static KugouQuality GetPreferredQuality()
        {
            string value = PreferredQuality != null ? PreferredQuality.Value : "320";
            if (string.IsNullOrEmpty(value))
            {
                return KugouQuality.Mp3320;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "128":
                case "128k":
                    return KugouQuality.Mp3128;
                case "m4a":
                case "flac":
                    return KugouQuality.M4A;
                default:
                    return KugouQuality.Mp3320;
            }
        }

        internal static KugouBackendMode GetBackendMode()
        {
            string value = BackendMode != null ? BackendMode.Value : "NativeDirect";
            return string.Equals(value, "ExternalGateway", StringComparison.OrdinalIgnoreCase)
                ? KugouBackendMode.ExternalGateway
                : KugouBackendMode.NativeDirect;
        }
    }
}
