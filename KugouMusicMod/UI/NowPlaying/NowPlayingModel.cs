using System;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using Cysharp.Threading.Tasks;
using KanKikuchi.AudioManager;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.NowPlaying
{
    /// <summary>
    /// 播放条数据快照模型：每帧从 FacilityMusic / MusicService / LyricService 采集状态，避免 OnGUI 反射与字符串分配。
    /// </summary>
    internal sealed class NowPlayingModel
    {
        private int _lastPlayedWholeSec = -1;
        private int _lastDurationWholeSec = -1;
        private string _playedText = "0:00";
        private string _durationText = "0:00";

        public bool ServiceReady { get; private set; }
        public bool HasTrack { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsShuffle { get; private set; }
        public bool IsRepeatOne { get; private set; }
        public bool IsKugouTrack { get; private set; }
        public bool IsKugouSource { get; private set; }
        public KugouTrack CurrentTrack { get; private set; }

        public string Title { get; private set; } = "未播放曲目";
        public string Singer { get; private set; } = "点击歌单或播放键开始聆听";
        public string CoverUrl { get; private set; } = string.Empty;
        public string SourceQualityLabel { get; private set; } = "官方曲库";

        public float PlayedSeconds { get; private set; }
        public float DurationSeconds { get; private set; }
        public float Progress { get; private set; }
        public string PlayedText { get { return _playedText; } }
        public string DurationText { get { return _durationText; } }
        public int ConsecutiveFailures { get; private set; }

        public void Tick()
        {
            IsKugouSource = Plugin.Switch != null && Plugin.Switch.IsKugouMode;
            ConsecutiveFailures = KugouPlayback.ConsecutiveFailures;

            MusicService service = Plugin.Switch != null ? Plugin.Switch.Service : null;
            FacilityMusic facility = Plugin.FacilityMusicInstance;
            ServiceReady = service != null;

            string quality = Plugin.PreferredQuality != null ? Plugin.PreferredQuality.Value : "320";
            string qLabel = string.Equals(quality, "m4a", StringComparison.OrdinalIgnoreCase)
                ? "M4A"
                : (quality + "k");
            SourceQualityLabel = IsKugouSource ? ("酷狗 · " + qLabel) : "官方曲库";

            if (service == null)
            {
                HasTrack = false;
                IsPlaying = false;
                IsShuffle = false;
                IsRepeatOne = false;
                IsKugouTrack = false;
                CurrentTrack = null;
                Title = "等待进入房间";
                Singer = "进入书房场景后即可控制播放";
                CoverUrl = string.Empty;
                UpdateTimeTexts(0f, 0f);
                return;
            }

            IsShuffle = service.IsShuffle;
            IsRepeatOne = service.IsRepeatOneMusic;

            bool paused = facility != null && facility.IsPaused;
            GameAudioInfo pendingInfo = null;
            bool hasPending = !paused &&
                              KugouPlayback.TryGetPendingInfo(out pendingInfo) &&
                              pendingInfo != null;

            GameAudioInfo playing = service.PlayingMusic;
            if (playing == null)
            {
                if (hasPending)
                {
                    ApplyTrackMeta(pendingInfo, out KugouTrack pendingTrack, out bool pendingKugou);
                    HasTrack = true;
                    IsPlaying = true;
                    IsKugouTrack = pendingKugou;
                    CurrentTrack = pendingTrack;
                    Singer = string.IsNullOrEmpty(Singer) ? "正在缓冲音频..." : ("正在缓冲 · " + Singer);
                    float pendingDur = pendingKugou && pendingTrack != null && pendingTrack.DurationSeconds > 0
                        ? pendingTrack.DurationSeconds
                        : 0f;
                    UpdateTimeTexts(0f, pendingDur);
                    return;
                }

                HasTrack = false;
                IsPlaying = false;
                IsKugouTrack = false;
                CurrentTrack = null;
                Title = "当前未选择歌曲";
                Singer = IsKugouSource ? "点击下方播放或从歌单点播" : "官方歌单就绪";
                CoverUrl = string.Empty;
                UpdateTimeTexts(0f, 0f);
                return;
            }

            GameAudioInfo displayInfo = hasPending ? pendingInfo : playing;
            bool switchingPending = hasPending && !ReferenceEquals(displayInfo, playing);

            HasTrack = true;
            ApplyTrackMeta(displayInfo, out KugouTrack track, out bool isKugou);
            IsKugouTrack = isKugou;
            CurrentTrack = track;
            if (switchingPending)
            {
                Singer = string.IsNullOrEmpty(Singer) ? "正在缓冲音频..." : ("正在缓冲 · " + Singer);
            }

            float played = 0f;
            float duration = IsKugouTrack && track != null && track.DurationSeconds > 0 ? track.DurationSeconds : 0f;

            try
            {
                if (!switchingPending && playing.AudioClip != null)
                {
                    if (duration <= 0.1f)
                    {
                        duration = playing.AudioClip.length;
                    }

                    if (MusicManager.Instance != null)
                    {
                        AudioPlayer player = MusicManager.Instance.GetPlayer(playing.AudioClip);
                        if (player != null)
                        {
                            played = Mathf.Max(0f, player.PlayedTime);
                            paused = paused ||
                                     (player.CurrentState != AudioPlayer.State.Playing &&
                                      player.CurrentState != AudioPlayer.State.Delay &&
                                      player.CurrentState != AudioPlayer.State.Fading);
                            if (player.AudioSource != null && player.AudioSource.clip != null && duration <= 0.1f)
                            {
                                duration = player.AudioSource.clip.length;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 销毁或切曲瞬态忽略
            }

            IsPlaying = switchingPending ? true : !paused;
            UpdateTimeTexts(played, duration);
        }

        private void ApplyTrackMeta(GameAudioInfo info, out KugouTrack track, out bool isKugou)
        {
            track = null;
            isKugou = Plugin.Registry != null && Plugin.Registry.TryGet(info, out track) && track != null;
            if (!isKugou)
            {
                track = null;
            }

            Title = isKugou
                ? track.GetDisplayName()
                : (string.IsNullOrEmpty(info.Title) ? "未知曲目" : info.Title);
            Singer = isKugou
                ? track.GetDisplaySinger()
                : (string.IsNullOrEmpty(info.Credit) ? "官方音轨" : info.Credit);
            CoverUrl = isKugou && !string.IsNullOrEmpty(track.CoverUrl) ? track.CoverUrl : string.Empty;
        }

        public void TogglePlayPause()
        {
            if (!ServiceReady)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null)
                {
                    facility.OnClickButtonPlayOrPauseMusic();
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[NowPlaying] 播放/暂停失败: " + ex.Message);
                }
            }
        }

        public void NextTrack()
        {
            if (!ServiceReady)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null)
                {
                    facility.OnClickButtonSkip();
                    return;
                }

                MusicService service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                if (service != null)
                {
                    service.PlayNextMusic(1, MusicChangeKind.Manual).Forget();
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[NowPlaying] 切下一首失败: " + ex.Message);
                }
            }
        }

        public void PrevTrack()
        {
            if (!ServiceReady)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                if (facility != null)
                {
                    facility.OnClickButtonBack();
                    return;
                }

                MusicService service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                if (service != null)
                {
                    service.PlayBackMusic().Forget();
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[NowPlaying] 切上一首失败: " + ex.Message);
                }
            }
        }

        public void ToggleShuffle()
        {
            if (!ServiceReady)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                MusicService service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                if (facility != null)
                {
                    facility.OnClickButtonShuffleChange();
                }
                else if (service != null)
                {
                    service.SetShuffle(!service.IsShuffle);
                }

                bool nowShuffle = service != null && service.IsShuffle;
                IsShuffle = nowShuffle;
                Plugin.Window?.SetStatus(nowShuffle ? "已开启随机播放" : "已切换为顺序播放", false);
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[NowPlaying] 切换随机播放失败: " + ex.Message);
                }
            }
        }

        public void ToggleRepeat()
        {
            if (!ServiceReady)
            {
                return;
            }

            try
            {
                FacilityMusic facility = Plugin.FacilityMusicInstance;
                MusicService service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                if (facility != null)
                {
                    facility.OnClickButtonChangeLoop();
                }
                else if (service != null)
                {
                    service.SetRepeat(!service.IsRepeatOneMusic);
                }

                bool nowRepeat = service != null && service.IsRepeatOneMusic;
                IsRepeatOne = nowRepeat;
                Plugin.Window?.SetStatus(nowRepeat ? "已开启单曲循环" : "已切换为列表循环", false);
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[NowPlaying] 切换循环模式失败: " + ex.Message);
                }
            }
        }

        private void UpdateTimeTexts(float played, float duration)
        {
            DurationSeconds = Mathf.Max(0f, duration);
            PlayedSeconds = DurationSeconds > 0.1f ? Mathf.Clamp(played, 0f, DurationSeconds) : Mathf.Max(0f, played);
            Progress = DurationSeconds > 0.1f ? Mathf.Clamp01(PlayedSeconds / DurationSeconds) : 0f;

            int pSec = Mathf.FloorToInt(PlayedSeconds);
            if (pSec != _lastPlayedWholeSec)
            {
                _lastPlayedWholeSec = pSec;
                _playedText = FormatSeconds(pSec);
            }

            int dSec = Mathf.FloorToInt(DurationSeconds);
            if (dSec != _lastDurationWholeSec)
            {
                _lastDurationWholeSec = dSec;
                _durationText = dSec > 0 ? FormatSeconds(dSec) : "--:--";
            }
        }

        public static string FormatSeconds(int totalSeconds)
        {
            if (totalSeconds <= 0)
            {
                return "0:00";
            }

            int mins = totalSeconds / 60;
            int secs = totalSeconds % 60;
            return mins + ":" + (secs < 10 ? "0" + secs : secs.ToString());
        }
    }
}
