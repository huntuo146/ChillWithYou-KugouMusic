using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Patches;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using KanKikuchi.AudioManager;
using R3;
using UnityEngine;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>
    /// 双音源切换：官方 ⇄ 酷狗。
    /// P0 只负责快照、切换骨架、AudioClip 生命周期守卫与占位歌单，
    /// 真正的网络解析在 P1 接入 IKugouClient。
    /// </summary>
    internal sealed class SourceSwitchService
    {
        private readonly List<GameAudioInfo> _officialList = new List<GameAudioInfo>();
        private readonly Dictionary<GameAudioInfo, AudioClip> _officialClips =
            new Dictionary<GameAudioInfo, AudioClip>();

        private readonly List<KugouTrack> _kugouTracks = new List<KugouTrack>();

        private MusicService _service;
        private bool _officialCaptured;
        private bool _autoSwitchTried;
        private GameAudioInfo _lastOfficialTrack;
        private IDisposable _playMusicSubscription;

        public bool IsKugouMode { get; private set; }

        public bool IsSwitching { get; private set; }

        public MusicService Service
        {
            get { return _service; }
        }

        public int OfficialCount
        {
            get { return _officialList.Count; }
        }

        public int KugouCount
        {
            get { return _kugouTracks.Count; }
        }

        // ---------------- 生命周期 ----------------

        public void AttachService(MusicService service)
        {
            if (service == null)
            {
                return;
            }

            if (_playMusicSubscription == null)
            {
                SubscribePlayMusic(service);
            }

            if (_service == service)
            {
                ApplyImportButtonState(IsKugouMode);
                return;
            }

            _service = service;
            _officialCaptured = false;
            _autoSwitchTried = false;

            Plugin.Log.LogInfo("[Switch] 已绑定 MusicService，开始采集官方曲目快照");
            CaptureOfficialSnapshot();
            ApplyImportButtonState(IsKugouMode);
            TryAutoSwitch();
        }

        /// <summary>
        /// 统一播放事件入口：覆盖 PlayArugumentMusic / PlayMusicInPlaylist / UnPause，
        /// 以及 PlayNextMusic 里"直接调 MusicManager.Play"后单独触发 onPlayMusic 的分支。
        /// </summary>
        private void OnPlayMusic(GameAudioInfo info)
        {
            try
            {
                if (info == null)
                {
                    return;
                }

                KugouTrack track;
                if (!Plugin.Registry.TryGet(info, out track))
                {
                    return;
                }

                if (Plugin.Store != null)
                {
                    // 记忆曲目：切源 / 重启后可恢复
                    Plugin.Store.LastTrackUuid = info.UUID;
                }

                KugouPlayback.NotifySuccess();

                if (Plugin.AudioCache != null)
                {
                    Plugin.AudioCache.SchedulePreloadNext(_service, info);
                }

                if (Plugin.EnableVerboseLog != null && Plugin.EnableVerboseLog.Value)
                {
                    Plugin.Log.LogInfo("[Switch] 正在播放: " + info.Title);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] OnPlayMusic 处理失败: " + ex.Message);
            }
        }

        private void SubscribePlayMusic(MusicService service)
        {
            try
            {
                if (_playMusicSubscription != null)
                {
                    _playMusicSubscription.Dispose();
                    _playMusicSubscription = null;
                }

                _playMusicSubscription = service.OnPlayMusic.Subscribe(OnPlayMusic);
                Plugin.Log.LogInfo("[Switch] 已订阅 OnPlayMusic（预加载 / 记忆曲目）");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 订阅 OnPlayMusic 失败: " + ex.Message);
            }
        }

        /// <summary>插件退出时解除订阅。</summary>
        public void Detach()
        {
            if (_playMusicSubscription == null)
            {
                return;
            }

            try
            {
                _playMusicSubscription.Dispose();
            }
            catch
            {
                // 退出阶段忽略
            }

            _playMusicSubscription = null;
        }

        /// <summary>MusicService.Load 的 Postfix 会调用这里。</summary>
        public void OnMusicLoaded(MusicService service)
        {
            if (service == null)
            {
                return;
            }

            if (_service == null)
            {
                _service = service;
            }

            if (_playMusicSubscription == null)
            {
                SubscribePlayMusic(service);
            }

            if (!_officialCaptured && !IsSwitching && !IsKugouMode)
            {
                CaptureOfficialSnapshot();
            }

            if (_officialCaptured && !_autoSwitchTried && !IsKugouMode && !IsSwitching)
            {
                TryAutoSwitch();
            }
        }

        public void CaptureOfficialSnapshot()
        {
            MusicService service = _service;
            if (service == null || _officialCaptured)
            {
                return;
            }

            try
            {
                IReadOnlyList<GameAudioInfo> source = service.AllMusicList;
                _officialList.Clear();
                _officialClips.Clear();

                if (source != null)
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        GameAudioInfo info = source[i];
                        if (info == null || Plugin.Registry.IsKugou(info))
                        {
                            continue;
                        }

                        _officialList.Add(info);
                        if (info.AudioClip != null)
                        {
                            // 保留强引用，防止官方曲目的 AudioClip 被卸载后无法恢复
                            _officialClips[info] = info.AudioClip;
                        }
                    }
                }

                _officialCaptured = _officialList.Count > 0;
                Plugin.Log.LogInfo("[Switch] 官方快照完成：曲目 " + _officialList.Count +
                                   " 首，clip 引用 " + _officialClips.Count + " 个");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Switch] 官方快照失败: " + ex);
            }
        }

        // ---------------- 切换 ----------------

        public void ToggleSource()
        {
            if (IsKugouMode)
            {
                SwitchToOfficial();
            }
            else
            {
                SwitchToKugou();
            }
        }

        public void SwitchToKugou()
        {
            SwitchToKugou(false, null);
        }

        internal void SwitchToKugou(bool fromStartup, string explicitPlayUuid = null)
        {
            MusicService service = _service;
            if (service == null)
            {
                Plugin.Log.LogWarning("[Switch] 尚未绑定 MusicService（需先进入音乐设施一次）");
                return;
            }

            if (IsSwitching)
            {
                return;
            }

            IsSwitching = true;
            _autoSwitchTried = true;
            SwitchToKugouAsync(fromStartup, explicitPlayUuid).Forget();
        }

        /// <summary>切到指定歌单并立即播放目标曲目（供歌单详情页点歌使用）。</summary>
        public bool SwitchPlaylistAndPlay(string specialId, List<KugouTrack> tracks, KugouTrack targetTrack)
        {
            MusicService service = _service;
            if (service == null || targetTrack == null)
            {
                return false;
            }

            string currentId = Plugin.ActivePlaylistId != null ? Plugin.ActivePlaylistId.Value : string.Empty;
            string uuid = targetTrack.GetUuid();

            if (IsKugouMode && string.Equals(currentId, specialId, StringComparison.Ordinal))
            {
                GameAudioInfo entry = FindEntry(uuid);
                if (entry == null)
                {
                    AddTrackToCurrentPlaylist(targetTrack);
                    entry = FindEntry(uuid);
                }

                if (entry != null)
                {
                    PlayTrackEntry(service, entry);
                    return true;
                }
            }

            if (Plugin.ActivePlaylistId != null && !string.IsNullOrEmpty(specialId))
            {
                Plugin.ActivePlaylistId.Value = specialId;
                Plugin.SaveConfig();
            }

            if (tracks != null && tracks.Count > 0)
            {
                SetKugouTracks(tracks);
            }

            SwitchToKugou(false, uuid);
            return true;
        }

        private async UniTaskVoid SwitchToKugouAsync(bool fromStartup, string explicitPlayUuid)
        {
            try
            {
                CaptureOfficialSnapshot();

                // 首次切换时按需联网导入歌单（已有本地缓存则直接读，不联网）
                await EnsureKugouTracksAsync();

                await UniTask.SwitchToMainThread(CancellationToken.None);

                MusicService service = _service;
                if (service == null)
                {
                    return;
                }

                GameAudioInfo previous = service.PlayingMusic;
                bool wasPlaying = !fromStartup && IsCurrentlyPlaying(service);
                if (previous != null && !Plugin.Registry.IsKugou(previous))
                {
                    _lastOfficialTrack = previous;
                }

                StopPlayback(service);

                // 先摘掉上一轮酷狗曲目的运行时 AudioClip，避免 Load 期间残留
                CleanupKugouClips(service);

                bool wasShuffle = service.IsShuffle;
                List<GameAudioInfo> list = BuildKugouEntries();
                service.Load(list);

                IsKugouMode = true;
                Plugin.ActiveSource.Value = "Kugou";
                SyncShuffleAndLoopAfterLoad(service, wasShuffle);
                RefreshVisiblePlaylist();
                ApplyImportButtonState(true);

                if (!string.IsNullOrEmpty(explicitPlayUuid))
                {
                    GameAudioInfo target = FindEntry(explicitPlayUuid);
                    if (target != null)
                    {
                        PlayTrackEntry(service, target);
                        Plugin.Log.LogInfo("[Switch] 已切歌单并起播: " + target.Title);
                    }
                    else
                    {
                        TryResumePlayback(service, true, true);
                    }
                }
                else if (fromStartup)
                {
                    // 启动加载阶段只切换列表，绝不自动开播，同时确保播放器保持暂停
                    ResetMusicUi();
                    EnsurePausedState(service);
                    Plugin.Log.LogInfo("[Switch] 已切入酷狗音源（启动加载保持暂停），曲目 " + list.Count + " 首");
                }
                else
                {
                    TryResumePlayback(service, wasPlaying, true);
                    Plugin.Log.LogInfo("[Switch] 已切换到酷狗音源，曲目 " + list.Count + " 首");
                }
            }
            catch (OperationCanceledException)
            {
                Plugin.Log.LogWarning("[Switch] 切换酷狗已取消");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Switch] 切换到酷狗失败: " + ex);
            }
            finally
            {
                IsSwitching = false;
            }
        }

        public void SwitchToOfficial()
        {
            MusicService service = _service;
            if (service == null)
            {
                return;
            }

            if (IsSwitching)
            {
                return;
            }

            IsSwitching = true;
            try
            {
                bool wasPlaying = IsCurrentlyPlaying(service);

                StopPlayback(service);
                CleanupKugouClips(service);

                if (_officialList.Count == 0)
                {
                    Plugin.Log.LogWarning("[Switch] 官方快照为空，无法切回；等待下一次 MusicService.Load");
                    return;
                }

                bool wasShuffle = service.IsShuffle;
                service.Load(_officialList);
                IsKugouMode = false;
                Plugin.ActiveSource.Value = "Official";
                RestoreOfficialClips();
                SyncShuffleAndLoopAfterLoad(service, wasShuffle);
                RefreshVisiblePlaylist();
                ApplyImportButtonState(false);
                TryResumePlayback(service, wasPlaying, false);

                Plugin.Log.LogInfo("[Switch] 已切回官方音源，曲目 " + _officialList.Count + " 首");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Switch] 切回官方失败: " + ex);
            }
            finally
            {
                IsSwitching = false;
            }
        }

        private void TryAutoSwitch()
        {
            if (_autoSwitchTried)
            {
                return;
            }

            if (!Plugin.IsKugouDesired())
            {
                _autoSwitchTried = true;
                return;
            }

            // 官方列表尚未就绪时不要切走，否则随后 EntryBehavior 的 Load 会覆盖酷狗列表
            if (!_officialCaptured)
            {
                return;
            }

            _autoSwitchTried = true;
            Plugin.Log.LogInfo("[Switch] 配置要求恢复酷狗音源，启动期间静默切换到酷狗列表");
            SwitchToKugou(true);
        }

        private static bool IsActivePlayerState(AudioPlayer.State state)
        {
            return state == AudioPlayer.State.Playing ||
                   state == AudioPlayer.State.Delay ||
                   state == AudioPlayer.State.Fading;
        }

        private static bool TryGetPlayerState(MusicService service, out AudioPlayer.State state)
        {
            state = AudioPlayer.State.Wait;
            if (service == null || service.PlayingMusic == null)
            {
                return false;
            }

            AudioClip clip = service.PlayingMusic.AudioClip;
            if (clip == null || MusicManager.Instance == null)
            {
                return false;
            }

            AudioPlayer player = MusicManager.Instance.GetPlayer(clip);
            if (player == null)
            {
                return false;
            }

            state = player.CurrentState;
            return true;
        }

        internal static bool IsAudioPlayerActive(MusicService service)
        {
            try
            {
                AudioPlayer.State state;
                return TryGetPlayerState(service, out state) && IsActivePlayerState(state);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCurrentlyPlaying(MusicService service)
        {
            if (service == null || service.PlayingMusic == null)
            {
                return false;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null && facility.IsPaused)
                {
                    return false;
                }

                AudioPlayer.State state;
                if (TryGetPlayerState(service, out state))
                {
                    return IsActivePlayerState(state);
                }
            }
            catch
            {
                // 回退按未暂停判断
            }

            return true;
        }

        private static void EnsurePausedState(MusicService service)
        {
            Plugin.EnsurePausedAndClearAutoStart(service);
        }

        private void StopPlayback(MusicService service)
        {
            KugouPlayback.SafeStop(service);
        }

        /// <summary>切源后没有自动播放时，把原生面板的标题/歌手恢复为空状态。</summary>
        private static void ResetMusicUi()
        {
            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null)
                {
                    facility.ResetAudioInfo();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 刷新界面状态失败: " + ex.Message);
            }
        }

        private static readonly MethodInfo ChangeShuffleUiMethod =
            AccessTools.Method(typeof(FacilityMusic), "ChangeShuffleUI", new[] { typeof(bool) });

        private static readonly MethodInfo ChangeLoopUiMethod =
            AccessTools.Method(typeof(FacilityMusic), "ChangeLoopUI", new[] { typeof(bool) });

        private static void SyncShuffleAndLoopAfterLoad(MusicService service, bool wasShuffle)
        {
            if (service == null)
            {
                return;
            }

            try
            {
                if (wasShuffle && !service.IsShuffle)
                {
                    service.SetShuffle(true);
                }

                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null)
                {
                    ChangeShuffleUiMethod?.Invoke(facility, new object[] { service.IsShuffle });
                    ChangeLoopUiMethod?.Invoke(facility, new object[] { service.IsRepeatOneMusic });
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 同步随机/循环状态失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 统一经由 FacilityMusic 触发点播，保持游戏原生 _mainState 与播放/暂停按钮状态同步，
        /// 避免插件播放控制与游戏内按钮产生冲突。
        /// </summary>
        internal static void PlayTrackEntry(MusicService service, GameAudioInfo entry)
        {
            if (service == null || entry == null)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null)
                {
                    facility.OnClickButtonPlayListPlayMusicButton(entry);
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 经由 FacilityMusic 起播失败，回退直连播放: " + ex.Message);
            }

            service.PlayArugumentMusic(entry, MusicChangeKind.Manual);
        }

        private void CleanupKugouClips(MusicService service)
        {
            try
            {
                // 以 _allMusicList 为准（CurrentPlayList 可能已被标签过滤）。
                // 统一调用 UnloadAudioClip，由补丁完成"释放流式解码器 + Destroy 运行时 clip"，
                // 避免插件内部与补丁各做一遍导致的重复释放或遗漏。
                List<GameAudioInfo> all = MusicPatchTargets.GetAllMusicList(service);
                if (all == null)
                {
                    return;
                }

                for (int i = 0; i < all.Count; i++)
                {
                    GameAudioInfo info = all[i];
                    if (info == null || !Plugin.Registry.IsKugou(info))
                    {
                        continue;
                    }

                    info.UnloadAudioClip();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 清理酷狗 clip 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 按当前标签重新过滤一次 CurrentPlayList。
        /// MusicService.Load 只按 IsUnlocked 过滤，不会应用标签；
        /// 切源后主动刷新，行为与游戏内切换标签页保持一致。
        /// </summary>
        public void RefreshVisiblePlaylist()
        {
            MusicService service = _service;
            if (service == null)
            {
                return;
            }

            try
            {
                List<GameAudioInfo> all = MusicPatchTargets.GetAllMusicList(service);
                List<GameAudioInfo> shuffle = MusicPatchTargets.GetShuffleList(service);

                List<GameAudioInfo> source = service.IsShuffle && shuffle != null ? shuffle : all;
                if (source == null)
                {
                    return;
                }

                AudioTag tag = MusicPatchTargets.GetCurrentAudioTag();
                var playlist = service.CurrentPlayList;
                if (playlist == null)
                {
                    return;
                }

                playlist.Clear();
                for (int i = 0; i < source.Count; i++)
                {
                    GameAudioInfo info = source[i];
                    if (info == null)
                    {
                        continue;
                    }

                    if (MusicPatchTargets.IsVisibleUnderTag(info, tag))
                    {
                        playlist.Add(info);
                    }
                }

                Plugin.Log.LogInfo("[Switch] 已按标签 " + tag + " 刷新列表，可见 " + playlist.Count + " 首");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 刷新列表失败: " + ex.Message);
            }
        }

        private void RestoreOfficialClips()
        {
            foreach (KeyValuePair<GameAudioInfo, AudioClip> pair in _officialClips)
            {
                try
                {
                    GameAudioInfo info = pair.Key;
                    AudioClip clip = pair.Value;

                    if (info == null || clip == null)
                    {
                        continue;
                    }

                    if (info.AudioClip == null)
                    {
                        info.AudioClip = clip;
                        try
                        {
                            clip.LoadAudioData();
                        }
                        catch
                        {
                            // 重新载入失败时保留引用，播放时会再次尝试
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[Switch] 恢复官方 clip 失败: " + ex.Message);
                }
            }
        }

        // ---------------- 酷狗列表构建（P0 占位） ----------------

        private List<GameAudioInfo> BuildKugouEntries()
        {
            var result = new List<GameAudioInfo>(_kugouTracks.Count);
            for (int i = 0; i < _kugouTracks.Count; i++)
            {
                GameAudioInfo info = CreateEntry(_kugouTracks[i]);
                if (info != null)
                {
                    result.Add(info);
                }
            }

            return result;
        }

        /// <summary>为单个酷狗曲目创建游戏侧条目（含本地收藏标记与注册表登记）。</summary>
        public GameAudioInfo CreateEntry(KugouTrack track)
        {
            if (track == null)
            {
                return null;
            }

            AudioTag tag = AudioTag.Original;
            if (Plugin.Store != null && Plugin.Store.IsFavorite(track.Hash))
            {
                tag |= AudioTag.Favorite;
            }

            var info = new GameAudioInfo
            {
                IsUnlocked = true,
                IsManualUnlocked = true,
                PathType = AudioMode.Normal,
                Tag = tag,
                Title = track.GetDisplayName(),
                Credit = track.GetDisplaySinger(),
                UUID = track.GetUuid(),
                LocalPath = null,
                AudioClip = null,
            };

            Plugin.Registry.Register(info, track);
            return info;
        }

        /// <summary>按 UUID 在当前播放列表中查找条目。</summary>
        public GameAudioInfo FindEntry(string uuid)
        {
            if (string.IsNullOrEmpty(uuid))
            {
                return null;
            }

            MusicService service = _service;
            var list = service != null ? service.CurrentPlayList : null;
            if (list == null)
            {
                return null;
            }

            for (int i = 0; i < list.Count; i++)
            {
                GameAudioInfo info = list[i];
                if (info != null && info.UUID == uuid)
                {
                    return info;
                }
            }

            return null;
        }

        /// <summary>
        /// 把单曲追加进当前酷狗列表（走 AddMusicItem 的存档隔离路径）。
        /// 仅酷狗模式下允许，避免把酷狗条目混进官方列表。
        /// </summary>
        public bool AddTrackToCurrentPlaylist(KugouTrack track)
        {
            MusicService service = _service;
            if (service == null || track == null)
            {
                return false;
            }

            if (!IsKugouMode)
            {
                return false;
            }

            if (FindEntry(track.GetUuid()) != null)
            {
                return true;
            }

            GameAudioInfo info = CreateEntry(track);
            if (info == null)
            {
                return false;
            }

            if (!service.AddMusicItem(info))
            {
                return false;
            }

            bool exists = false;
            for (int i = 0; i < _kugouTracks.Count; i++)
            {
                if (_kugouTracks[i] != null && _kugouTracks[i].Hash == track.Hash)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _kugouTracks.Add(track);
            }

            return true;
        }

        /// <summary>
        /// 切源后按配置恢复播放：切源前在播 或 AutoResumeLastTrack 开启时，
        /// 优先播放记住的曲目，否则播放目标列表第一首。
        /// </summary>
        /// <summary>解析切源后恢复播放或空轨起播的目标曲目（先上次记忆曲目，再回退列表首曲）。</summary>
        internal GameAudioInfo ResolveResumeTarget(MusicService service, bool? overrideKugouMode = null)
        {
            if (service == null)
            {
                return null;
            }

            var list = service.CurrentPlayList;
            if (list == null || list.Count == 0)
            {
                return null;
            }

            bool kugouMode = overrideKugouMode.HasValue ? overrideKugouMode.Value : IsKugouMode;
            GameAudioInfo target = null;

            if (kugouMode)
            {
                string lastUuid = Plugin.Store != null ? Plugin.Store.LastTrackUuid : null;
                if (!string.IsNullOrEmpty(lastUuid))
                {
                    target = FindEntry(lastUuid);
                }
            }
            else if (_lastOfficialTrack != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list[i], _lastOfficialTrack))
                    {
                        target = _lastOfficialTrack;
                        break;
                    }
                }
            }

            return target ?? list[0];
        }

        private void TryResumePlayback(MusicService service, bool wasPlaying, bool kugouMode)
        {
            if (service == null)
            {
                return;
            }

            bool autoResume = Plugin.AutoResumeLastTrack != null && Plugin.AutoResumeLastTrack.Value;
            if (!wasPlaying && !autoResume)
            {
                ResetMusicUi();
                EnsurePausedState(service);
                return;
            }

            try
            {
                GameAudioInfo target = ResolveResumeTarget(service, kugouMode);
                if (target == null)
                {
                    ResetMusicUi();
                    EnsurePausedState(service);
                    return;
                }

                PlayTrackEntry(service, target);
                Plugin.Log.LogInfo("[Switch] 已自动恢复播放: " + target.Title);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 恢复播放失败: " + ex.Message);
            }
        }

        /// <summary>同步原生"本机文件/文件夹导入"按钮可用性（酷狗模式禁用）。</summary>
        public void ApplyImportButtonState(bool kugouMode)
        {
            MusicPlayListTabUI tab = Plugin.MusicTabUi;
            if (tab == null)
            {
                return;
            }

            try
            {
                tab.SetInteractableFileImport(!kugouMode);
                tab.SetInteractableFolderImport(!kugouMode);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[UI] 切换导入按钮状态失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 确保酷狗曲目就绪：本地已存歌单优先，其次按 General/ActivePlaylistId 联网导入，
        /// 最后退化为占位曲目（便于无网络时验证切换与补丁）。
        /// </summary>
        private async UniTask EnsureKugouTracksAsync()
        {
            if (_kugouTracks.Count > 0)
            {
                return;
            }

            string playlistId = Plugin.ActivePlaylistId != null ? Plugin.ActivePlaylistId.Value : string.Empty;

            try
            {
                KugouPlaylistRecord record = Plugin.Store != null ? Plugin.Store.GetPlaylist(playlistId) : null;

                if (record != null && record.Tracks != null && record.Tracks.Count > 0)
                {
                    for (int i = 0; i < record.Tracks.Count; i++)
                    {
                        KugouTrack track = record.Tracks[i];
                        if (track == null)
                        {
                            continue;
                        }

                        bool excluded = Plugin.Store != null && Plugin.Store.IsExcluded(track.Hash);
                        if (!excluded)
                        {
                            _kugouTracks.Add(track);
                        }
                    }

                    if (_kugouTracks.Count > 0)
                    {
                        Plugin.Log.LogInfo("[Switch] 载入本地歌单《" + record.Name + "》，曲目 " +
                                           _kugouTracks.Count + " 首");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Switch] 读取本地歌单失败: " + ex.Message);
            }

            if (!string.IsNullOrEmpty(playlistId) && Plugin.Client != null)
            {
                Plugin.Log.LogInfo("[Switch] 正在联网导入歌单: " + playlistId);

                KugouPlaylistDetail detail = null;
                try
                {
                    detail = await Plugin.Client.ParsePlaylistInputAsync(playlistId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[Switch] 歌单导入异常: " + ex.Message);
                }

                if (detail != null && detail.Status == KugouApiStatus.Ok && detail.Tracks.Count > 0)
                {
                    if (Plugin.Store != null)
                    {
                        Plugin.Store.SavePlaylist(new KugouPlaylistRecord
                        {
                            SpecialId = string.IsNullOrEmpty(detail.Summary.SpecialId)
                                ? playlistId
                                : detail.Summary.SpecialId,
                            Name = detail.Summary.Name,
                            Author = detail.Summary.Author,
                            UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Tracks = new List<KugouTrack>(detail.Tracks),
                        });
                    }

                    for (int i = 0; i < detail.Tracks.Count; i++)
                    {
                        KugouTrack track = detail.Tracks[i];
                        if (track == null)
                        {
                            continue;
                        }

                        bool excluded = Plugin.Store != null && Plugin.Store.IsExcluded(track.Hash);
                        if (!excluded)
                        {
                            _kugouTracks.Add(track);
                        }
                    }

                    if (_kugouTracks.Count > 0)
                    {
                        Plugin.Log.LogInfo("[Switch] 歌单《" + detail.Summary.Name + "》导入完成：" +
                                           _kugouTracks.Count + " 首");
                        return;
                    }
                }
                else if (detail != null)
                {
                    Plugin.Log.LogWarning("[Switch] 歌单导入失败: " + detail.Status + " " + detail.Message);
                }
            }

            _kugouTracks.Clear();
            for (int i = 1; i <= 3; i++)
            {
                _kugouTracks.Add(new KugouTrack
                {
                    Hash = "p0demo" + i,
                    Name = "P0 测试曲目 " + i,
                    Singer = "本地文件验证",
                    DurationSeconds = 0,
                });
            }

            Plugin.Log.LogInfo("[Switch] 使用占位曲目 3 首（未配置歌单或导入失败）");
        }

        /// <summary>供 UI 使用：重新按当前配置构建酷狗列表。</summary>
        public void ReloadKugouTracks()
        {
            _kugouTracks.Clear();
            EnsureKugouTracksAsync().Forget();
        }

        public void SetKugouTracks(List<KugouTrack> tracks)
        {
            _kugouTracks.Clear();
            if (tracks == null)
            {
                return;
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                KugouTrack track = tracks[i];
                if (track == null)
                {
                    continue;
                }

                // 与 EnsureKugouTracksAsync 保持一致：被插件排除的曲目不进播放列表
                if (Plugin.Store != null && Plugin.Store.IsExcluded(track.Hash))
                {
                    continue;
                }

                _kugouTracks.Add(track);
            }
        }
    }

    /// <summary>
    /// 酷狗曲目的 AudioClip 装载桥。
    /// P0 只从本地文件装载（P0TestAudioPath 或已存在的缓存文件），
    /// 统一复用游戏自带的 CreateLocalFileAsync，从而自动分派 mp3（UnityWebRequest）
    /// 与 m4a（BulbulLocalAudioDecoder / Windows Media Foundation）。
    /// </summary>
    internal static class KugouAudioLoader
    {
        private const string FieldStreamingClip = "_streamingLocalAudioClip";
        private const string FieldStreamingSub = "_streamingLocalAudioClipSubscription";
        private const string MethodSetStreaming = "SetStreamingLocalAudioClip";

        private static FieldInfo _streamingField;
        private static FieldInfo _subscriptionField;
        private static MethodInfo _setStreamingMethod;
        private static bool _reflectionReady;

        private static void EnsureReflection()
        {
            if (_reflectionReady)
            {
                return;
            }

            _reflectionReady = true;
            Type type = typeof(GameAudioInfo);
            _streamingField = AccessTools.Field(type, FieldStreamingClip);
            _subscriptionField = AccessTools.Field(type, FieldStreamingSub);
            _setStreamingMethod = AccessTools.Method(type, MethodSetStreaming);

            if (_streamingField == null || _setStreamingMethod == null)
            {
                Plugin.Log.LogWarning("[Loader] 未能定位 StreamingLocalAudioClip 相关成员，m4a 桥接将不可用");
            }
        }

        /// <summary>解析酷狗曲目可用的本地文件（P0：测试文件或已有缓存）。</summary>
        public static string ResolveLocalFile(KugouTrack track)
        {
            try
            {
                string testPath = Plugin.P0TestAudioPath != null ? Plugin.P0TestAudioPath.Value : null;
                if (!string.IsNullOrEmpty(testPath) && File.Exists(testPath))
                {
                    return testPath;
                }
            }
            catch
            {
                // 配置读取失败按无测试文件处理
            }

            if (track == null || string.IsNullOrEmpty(track.Hash))
            {
                return null;
            }

            return KugouPaths.FindCachedFile(track.Hash);
        }

        public static async UniTask<AudioClip> LoadAsync(
            GameAudioInfo info,
            KugouTrack track,
            CancellationToken ct)
        {
            if (info == null)
            {
                return null;
            }

            if (info.AudioClip != null)
            {
                return info.AudioClip;
            }

            string path = ResolveLocalFile(track);

            if (string.IsNullOrEmpty(path) && track != null && Plugin.AudioCache != null)
            {
                // P1：本地无缓存时联网解析直链并下载（试听/付费曲由缓存层按配置拒绝）
                bool cached = await Plugin.AudioCache.EnsureCachedAsync(track, ct);
                if (cached)
                {
                    path = KugouPaths.FindCachedFile(track.Hash);
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                Plugin.Log.LogWarning("[Loader] 曲目无可播放音源: " +
                                      (track != null ? track.GetDisplayName() : "null"));
                return null;
            }

            EnsureReflection();

            GameAudioInfo temp = null;
            try
            {
                // CreateLocalFileAsync 内部使用 UnityWebRequest，必须回到主线程
                await UniTask.SwitchToMainThread(ct);

                temp = await GameAudioInfo.CreateLocalFileAsync(path, info.UUID, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Loader] 解码本地文件失败 (" + path + "): " + ex.Message);
                return null;
            }

            if (temp == null)
            {
                return null;
            }

            if (ct.IsCancellationRequested)
            {
                // 连点取消：临时对象已经完成解码，必须立刻卸载，否则 AudioClip / 解码线程泄漏
                ReleaseTempAfterCancel(temp);
                return null;
            }

            await UniTask.SwitchToMainThread(ct);

            if (ct.IsCancellationRequested)
            {
                ReleaseTempAfterCancel(temp);
                return null;
            }

            try
            {
                AudioClip clip = temp.AudioClip;
                object streamingClip = _streamingField != null ? _streamingField.GetValue(temp) : null;

                if (streamingClip != null && _setStreamingMethod != null)
                {
                    // 迁移流式解码器（m4a），让游戏负责 seek / 重采样 / clip 重建
                    _setStreamingMethod.Invoke(info, new[] { streamingClip });
                }

                if (info.AudioClip == null && clip != null)
                {
                    info.AudioClip = clip;
                }

                DetachTemp(temp);

                info.PrepareAudioClipForPlayback();

                Plugin.Log.LogInfo("[Loader] 装载完成: " + info.Title + " -> " +
                                   (info.AudioClip != null ? info.AudioClip.length.ToString("0.0") + "s" : "null"));

                return info.AudioClip;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Loader] 迁移 AudioClip 失败: " + ex);
                return null;
            }
        }

        /// <summary>取消时释放已经解码完成的临时对象（LocalPc 走原逻辑 Destroy clip）。</summary>
        private static void ReleaseTempAfterCancel(GameAudioInfo temp)
        {
            if (temp == null)
            {
                return;
            }

            try
            {
                temp.UnloadAudioClip();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Loader] 取消后释放临时对象失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 切断临时 GameAudioInfo 对解码资源的持有，避免我们迁移后它被 GC 回收时重复释放。
        /// 注意：绝不 Dispose 临时对象（它持有的是我们已经接管的资源）。
        /// </summary>
        private static void DetachTemp(GameAudioInfo temp)
        {
            try
            {
                if (_subscriptionField != null)
                {
                    var subscription = _subscriptionField.GetValue(temp) as IDisposable;
                    if (subscription != null)
                    {
                        subscription.Dispose();
                    }

                    _subscriptionField.SetValue(temp, null);
                }

                if (_streamingField != null)
                {
                    _streamingField.SetValue(temp, null);
                }

                temp.AudioClip = null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Loader] 清理临时对象失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 异步播放调度：拦截同步播放入口后在这里补完 clip 装载，再回调原方法。
    /// </summary>
    internal static class KugouPlayback
    {
        private static readonly object Gate = new object();
        private static CancellationTokenSource _cts;
        private static GameAudioInfo _pendingInfo;

        internal static bool TryGetPendingInfo(out GameAudioInfo info)
        {
            lock (Gate)
            {
                info = _pendingInfo;
                return _cts != null && info != null;
            }
        }

        public static void RequestPlay(MusicService service, GameAudioInfo info, MusicChangeKind kind)
        {
            KugouTrack track;
            if (!Plugin.Registry.TryGet(info, out track))
            {
                track = null;
            }

            CancellationToken token = BeginPending(info);
            PlayAsync(service, info, track, kind, token).Forget();
        }

        public static void RequestPlayIndex(MusicService service, int index)
        {
            GameAudioInfo info = null;
            if (service != null && service.CurrentPlayList != null && index >= 0 && index < service.CurrentPlayList.Count)
            {
                info = service.CurrentPlayList[index];
            }

            CancellationToken token = BeginPending(info);
            PlayIndexAsync(service, index, token).Forget();
        }

        private static CancellationToken BeginPending(GameAudioInfo info)
        {
            lock (Gate)
            {
                if (_cts != null)
                {
                    try
                    {
                        _cts.Cancel();
                        _cts.Dispose();
                    }
                    catch
                    {
                        // 上一个请求已结束
                    }
                }

                _cts = new CancellationTokenSource();
                _pendingInfo = info;
                return _cts.Token;
            }
        }

        private static void ClearPending(CancellationToken ct)
        {
            lock (Gate)
            {
                if (_cts != null && _cts.Token == ct)
                {
                    _pendingInfo = null;
                }
            }
        }

        private static async UniTaskVoid PlayAsync(
            MusicService service,
            GameAudioInfo info,
            KugouTrack track,
            MusicChangeKind kind,
            CancellationToken ct)
        {
            try
            {
                AudioClip clip = await KugouAudioLoader.LoadAsync(info, track, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (clip == null)
                {
                    ClearPending(ct);
                    HandleLoadFailure(service, info);
                    return;
                }

                await UniTask.SwitchToMainThread(ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                ClearPending(ct);
                // 重新进入原方法：此时 AudioClip 已就绪，Prefix 自然放行
                MusicPatchTargets.InvokePlayArugumentMusic(service, info, kind);
                Plugin.Log.LogInfo("[Hook] 延迟播放完成: " + info.Title);

                NotifySuccess();
            }
            catch (OperationCanceledException)
            {
                ClearPending(ct);
                // 连点取消，静默
            }
            catch (Exception ex)
            {
                ClearPending(ct);
                Plugin.Log.LogError("[Hook] 延迟播放失败: " + ex);
            }
        }

        private static async UniTaskVoid PlayIndexAsync(
            MusicService service,
            int index,
            CancellationToken ct)
        {
            try
            {
                var list = service.CurrentPlayList;
                if (list == null || index < 0 || index >= list.Count)
                {
                    ClearPending(ct);
                    return;
                }

                GameAudioInfo info = list[index];
                KugouTrack track;
                if (!Plugin.Registry.TryGet(info, out track))
                {
                    ClearPending(ct);
                    return;
                }

                AudioClip clip = await KugouAudioLoader.LoadAsync(info, track, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (clip == null)
                {
                    ClearPending(ct);
                    HandleLoadFailure(service, info);
                    return;
                }

                await UniTask.SwitchToMainThread(ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                ClearPending(ct);
                MusicPatchTargets.InvokePlayMusicInPlaylist(service, index);
                Plugin.Log.LogInfo("[Hook] 延迟列表播放完成: index=" + index);

                NotifySuccess();
            }
            catch (OperationCanceledException)
            {
                ClearPending(ct);
            }
            catch (Exception ex)
            {
                ClearPending(ct);
                Plugin.Log.LogError("[Hook] 延迟列表播放失败: " + ex);
            }
        }

        // ---------------- 试听 / 坏链自动跳过 ----------------

        /// <summary>连续失败上限：超过即熔断停播，避免全灰歌单无限请求。</summary>
        private const int MaxConsecutiveFailures = 5;

        private static int _consecutiveFailures;
        private static bool _skipInProgress;

        internal static int ConsecutiveFailures
        {
            get { return _consecutiveFailures; }
        }

        /// <summary>曲目成功装载并播放后复位失败计数。</summary>
        internal static void NotifySuccess()
        {
            _consecutiveFailures = 0;
        }

        /// <summary>
        /// GetAudioClip 静默失败时的计数入口。
        /// 注意：MusicService.GetNextGameAudio 自身已经会继续尝试下一首，
        /// 这里**绝不主动跳曲**，只累计失败次数；超过阈值时中止本轮自动连播，避免死循环。
        /// </summary>
        internal static void RecordGetClipFailure(MusicService service)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures <= MaxConsecutiveFailures)
            {
                Plugin.Log.LogWarning("[Hook] 自动连播取音源失败（" + _consecutiveFailures + "/" +
                                      MaxConsecutiveFailures + "），交由游戏继续试下一首");
                return;
            }

            AbortAutoAdvance(service);
        }

        private static void AbortAutoAdvance(MusicService service)
        {
            Plugin.Log.LogError("[Hook] 连续 " + _consecutiveFailures +
                                " 首无法取到音源，已取消本轮自动连播（防止死循环）");

            // 取消 MusicService 内部正在进行的 audioLoadCancellation，让 await 链退出
            try
            {
                MusicPatchTargets.CancelAudioLoad(service);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Hook] 取消 audioLoadCancellation 失败: " + ex.Message);
            }

            // 与切源共用同一套空 clip 防护，避免原版 Stop() 在 GetPlayer(null) 处抛异常
            SafeStop(service);

            _consecutiveFailures = 0;
        }

        internal static void HandleLoadFailure(MusicService service, GameAudioInfo info)
        {
            _consecutiveFailures++;
            string name = info != null ? info.Title : "未知曲目";

            bool skipEnabled = Plugin.SkipTrialTracks == null || Plugin.SkipTrialTracks.Value;
            if (!skipEnabled)
            {
                Plugin.Log.LogWarning("[Hook] 曲目不可播放（自动跳过已关闭）: " + name);
                return;
            }

            if (_consecutiveFailures > MaxConsecutiveFailures)
            {
                Plugin.Log.LogError("[Hook] 连续 " + _consecutiveFailures +
                                    " 首不可播放，已熔断停播（防止死循环）");

                // 与切源/自动连播共用空 clip 防护
                SafeStop(service);

                _consecutiveFailures = 0;
                return;
            }

            Plugin.Log.LogWarning("[Hook] 曲目不可播放，自动跳过（" + _consecutiveFailures + "/" +
                                  MaxConsecutiveFailures + "）: " + name);
            SkipNextAsync(service).Forget();
        }

        /// <summary>
        /// 安全停播：PlayingMusic 的 AudioClip 已卸载时，原版 Stop() 会在 GetPlayer(null) 抛异常，
        /// 先把 PlayingMusic 清空再停，避免残留状态污染下一次播放。
        /// </summary>
        internal static void SafeStop(MusicService service)
        {
            if (service == null)
            {
                return;
            }

            CancelPending();

            try
            {
                GameAudioInfo playing = service.PlayingMusic;
                if (playing != null && playing.AudioClip == null)
                {
                    MusicPatchTargets.ClearPlayingMusic(service);
                    SyncFacilityPausedIfNotSwitching();
                    return;
                }

                service.Stop();
                SyncFacilityPausedIfNotSwitching();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Hook] 停播失败（可忽略）: " + ex.Message);
                MusicPatchTargets.ClearPlayingMusic(service);
                SyncFacilityPausedIfNotSwitching();
            }
        }

        private static void SyncFacilityPausedIfNotSwitching()
        {
            if (Plugin.Switch != null && Plugin.Switch.IsSwitching)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null && !facility.IsPaused)
                {
                    facility.PauseMusic();
                }
            }
            catch
            {
            }
        }

        private static async UniTaskVoid SkipNextAsync(MusicService service)
        {
            if (service == null || _skipInProgress)
            {
                return;
            }

            _skipInProgress = true;
            try
            {
                await UniTask.SwitchToMainThread(CancellationToken.None);
                await service.PlayNextMusic(1, MusicChangeKind.Auto);
            }
            catch (OperationCanceledException)
            {
                // 用户连续点击导致的取消
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] 自动跳曲失败: " + ex.Message);
            }
            finally
            {
                _skipInProgress = false;
            }
        }

        public static void CancelPending()
        {
            lock (Gate)
            {
                _pendingInfo = null;
                if (_cts == null)
                {
                    return;
                }

                try
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }
                catch
                {
                }

                _cts = null;
            }
        }
    }
}