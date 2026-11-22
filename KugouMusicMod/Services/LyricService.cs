using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;
using KanKikuchi.AudioManager;
using UnityEngine;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>LRC 歌词解析与 Base64 解码辅助。</summary>
    internal static class LrcParser
    {
        private static readonly Regex TimeTagRegex = new Regex(
            @"\[(\d{1,3}):(\d{2})(?:\.(\d{1,3}))?\]",
            RegexOptions.Compiled);

        private static readonly Regex OffsetTagRegex = new Regex(
            @"^\s*\[offset\s*:\s*([+-]?\d+)\s*\]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static KugouLyricsResult BuildResult(string decodedText, string base64Content)
        {
            string lrcText = decodedText;
            if (string.IsNullOrEmpty(lrcText) && !string.IsNullOrEmpty(base64Content))
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(base64Content.Trim());
                    lrcText = Encoding.UTF8.GetString(bytes);
                }
                catch (Exception ex)
                {
                    return new KugouLyricsResult
                    {
                        Status = KugouApiStatus.ParseError,
                        Message = "歌词 Base64 解码失败: " + ex.Message,
                    };
                }
            }

            if (string.IsNullOrEmpty(lrcText))
            {
                return new KugouLyricsResult
                {
                    Status = KugouApiStatus.NoSource,
                    Message = "歌词内容为空",
                };
            }

            var result = new KugouLyricsResult
            {
                Status = KugouApiStatus.Ok,
                RawLrc = lrcText,
            };

            ParseInto(lrcText, result.Lines);
            if (result.Lines.Count == 0)
            {
                result.Status = KugouApiStatus.NoSource;
                result.Message = "未解析出带时间轴的歌词行";
            }

            return result;
        }

        public static void ParseInto(string rawLrc, List<KugouLyricLine> output)
        {
            if (output == null)
            {
                return;
            }

            output.Clear();
            if (string.IsNullOrEmpty(rawLrc))
            {
                return;
            }

            float offsetSeconds = 0f;
            var entries = new List<KugouLyricLine>();
            string[] lines = rawLrc.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Match offsetMatch = OffsetTagRegex.Match(line);
                if (offsetMatch.Success)
                {
                    int offsetMs;
                    if (int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out offsetMs))
                    {
                        offsetSeconds = -offsetMs / 1000f;
                    }

                    continue;
                }

                MatchCollection matches = TimeTagRegex.Matches(line);
                if (matches.Count == 0)
                {
                    continue;
                }

                string text = TimeTagRegex.Replace(line, string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                for (int m = 0; m < matches.Count; m++)
                {
                    Match tag = matches[m];
                    int min;
                    int sec;
                    if (!int.TryParse(tag.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out min) ||
                        !int.TryParse(tag.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sec))
                    {
                        continue;
                    }

                    float frac = 0f;
                    if (tag.Groups[3].Success)
                    {
                        string fracStr = tag.Groups[3].Value;
                        int fracVal;
                        if (int.TryParse(fracStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out fracVal))
                        {
                            frac = fracStr.Length == 3
                                ? fracVal / 1000f
                                : (fracStr.Length == 2 ? fracVal / 100f : fracVal / 10f);
                        }
                    }

                    float time = Math.Max(0f, min * 60f + sec + frac);
                    entries.Add(new KugouLyricLine
                    {
                        TimeSeconds = time,
                        Text = text,
                    });
                }
            }

            if (Math.Abs(offsetSeconds) > 0.001f)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].TimeSeconds = Math.Max(0f, entries[i].TimeSeconds + offsetSeconds);
                }
            }

            entries.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            output.AddRange(entries);
        }
    }

    /// <summary>
    /// 实时歌词跟踪服务：
    /// - 监听 MusicService.PlayingMusic 曲目变化并异步拉取/缓存 LRC；
    /// - 每帧读取 AudioPlayer.PlayedTime 计算当前句与下一句歌词；
    /// - 支持本地 .lrc 磁盘缓存（cache/kugou/lyrics）与快切防抖取消。
    /// </summary>
    internal sealed class LyricService
    {
        private readonly Dictionary<string, KugouLyricsResult> _memoryCache =
            new Dictionary<string, KugouLyricsResult>(StringComparer.Ordinal);

        private CancellationTokenSource _fetchCts;
        private string _activeUuid = string.Empty;
        private GameAudioInfo _activeInfo;
        private KugouLyricsResult _activeLyrics;
        private bool _loading;
        private float _lastPlayTime;

        public string TrackTitle { get; private set; } = string.Empty;
        public string TrackSinger { get; private set; } = string.Empty;
        public string CurrentLine { get; private set; } = "等待播放音乐…";
        public string NextLine { get; private set; } = string.Empty;
        public string PreviousLine { get; private set; } = string.Empty;
        public float LineChangedAt { get; private set; } = -10f;
        public float CurrentLineProgress { get; private set; }
        public bool HasLyrics { get; private set; }
        public float CurrentPlaySeconds { get; private set; }

        public void Tick()
        {
            MusicService service = Plugin.Switch != null ? Plugin.Switch.Service : null;
            if (service == null)
            {
                TrackTitle = string.Empty;
                TrackSinger = string.Empty;
                SetLineState("等待进入房间…", string.Empty, false, 0f);
                return;
            }

            GameAudioInfo playing = service.PlayingMusic;
            if (playing == null)
            {
                _activeUuid = string.Empty;
                _activeInfo = null;
                _activeLyrics = null;
                TrackTitle = string.Empty;
                TrackSinger = string.Empty;
                SetLineState("未播放歌曲", string.Empty, false, 0f);
                return;
            }

            string uuid = playing.UUID ?? string.Empty;
            if (!ReferenceEquals(playing, _activeInfo) || !string.Equals(uuid, _activeUuid, StringComparison.Ordinal))
            {
                OnTrackChanged(playing, uuid);
            }

            float playTime = ResolvePlaybackTime(playing, service);
            CurrentPlaySeconds = playTime;
            UpdateDisplayedLines(playTime);
        }

        public void InvalidateCurrent()
        {
            if (!string.IsNullOrEmpty(_activeUuid))
            {
                _memoryCache.Remove(_activeUuid);
            }

            if (_activeInfo != null)
            {
                OnTrackChanged(_activeInfo, _activeUuid);
            }
        }

        private void OnTrackChanged(GameAudioInfo playing, string uuid)
        {
            _activeInfo = playing;
            _activeUuid = uuid;
            _lastPlayTime = 0f;
            CurrentPlaySeconds = 0f;

            KugouTrack track = null;
            bool isKugou = Plugin.Registry != null && Plugin.Registry.TryGet(playing, out track) && track != null;

            TrackTitle = isKugou
                ? track.GetDisplayName()
                : (string.IsNullOrEmpty(playing.Title) ? "未知曲目" : playing.Title);
            TrackSinger = isKugou
                ? track.GetDisplaySinger()
                : (string.IsNullOrEmpty(playing.Credit) ? string.Empty : playing.Credit);

            CancelFetch();

            if (!isKugou)
            {
                _loading = false;
                _activeLyrics = new KugouLyricsResult
                {
                    Status = KugouApiStatus.NoSource,
                    Message = "官方曲目（无歌词）",
                };
                return;
            }

            KugouLyricsResult cached;
            if (_memoryCache.TryGetValue(uuid, out cached))
            {
                _loading = false;
                _activeLyrics = cached;
                return;
            }

            string diskLrc = TryReadDiskLrc(track.Hash);
            if (!string.IsNullOrEmpty(diskLrc))
            {
                KugouLyricsResult parsed = LrcParser.BuildResult(diskLrc, null);
                _memoryCache[uuid] = parsed;
                _loading = false;
                _activeLyrics = parsed;
                return;
            }

            _loading = true;
            _activeLyrics = null;
            _fetchCts = new CancellationTokenSource();
            FetchLyricsAsync(track, uuid, _fetchCts.Token).Forget();
        }

        private async UniTaskVoid FetchLyricsAsync(KugouTrack track, string uuid, CancellationToken ct)
        {
            try
            {
                IKugouClient client = Plugin.Client;
                if (client == null)
                {
                    await UniTask.SwitchToMainThread(ct);
                    if (!ct.IsCancellationRequested && string.Equals(_activeUuid, uuid, StringComparison.Ordinal))
                    {
                        _loading = false;
                        _activeLyrics = new KugouLyricsResult { Status = KugouApiStatus.NoSource, Message = "客户端未就绪" };
                    }

                    return;
                }

                KugouLyricsResult result = await client.GetLyricsAsync(track, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (result != null && result.Status == KugouApiStatus.Ok && !string.IsNullOrEmpty(result.RawLrc))
                {
                    TryWriteDiskLrc(track.Hash, result.RawLrc);
                }

                await UniTask.SwitchToMainThread(ct);
                if (ct.IsCancellationRequested || !string.Equals(_activeUuid, uuid, StringComparison.Ordinal))
                {
                    return;
                }

                _loading = false;
                _activeLyrics = result ?? new KugouLyricsResult { Status = KugouApiStatus.NoSource, Message = "暂无歌词" };
                _memoryCache[uuid] = _activeLyrics;
            }
            catch (OperationCanceledException)
            {
                // 切歌取消
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[Lyric] 获取歌词异常: " + ex.Message);
                }

                await UniTask.SwitchToMainThread(CancellationToken.None);
                if (string.Equals(_activeUuid, uuid, StringComparison.Ordinal))
                {
                    _loading = false;
                    _activeLyrics = new KugouLyricsResult { Status = KugouApiStatus.NetworkError, Message = "歌词获取失败" };
                }
            }
        }

        private float ResolvePlaybackTime(GameAudioInfo playing, MusicService service)
        {
            try
            {
                if (playing != null && playing.AudioClip != null && MusicManager.Instance != null)
                {
                    AudioPlayer player = MusicManager.Instance.GetPlayer(playing.AudioClip);
                    if (player != null && player.AudioSource != null && player.AudioSource.clip != null)
                    {
                        float t = player.PlayedTime;
                        if (t >= 0f)
                        {
                            _lastPlayTime = t;
                            return t;
                        }
                    }
                }
            }
            catch
            {
                // 切换/销毁瞬态忽略
            }

            return service.IsPause ? _lastPlayTime : _lastPlayTime;
        }

        private void SetLineState(string current, string next, bool hasLyrics, float lineProgress)
        {
            string normalized = current ?? string.Empty;
            if (!string.Equals(CurrentLine, normalized, StringComparison.Ordinal))
            {
                PreviousLine = CurrentLine ?? string.Empty;
                CurrentLine = normalized;
                LineChangedAt = Time.unscaledTime;
            }

            NextLine = next ?? string.Empty;
            HasLyrics = hasLyrics;
            CurrentLineProgress = Mathf.Clamp01(lineProgress);
        }

        private void UpdateDisplayedLines(float playTime)
        {
            string headerLine = "♪ " + TrackTitle + (string.IsNullOrEmpty(TrackSinger) ? string.Empty : " - " + TrackSinger);

            if (_loading)
            {
                SetLineState(headerLine, "正在获取歌词…", false, 0f);
                return;
            }

            if (_activeLyrics == null || _activeLyrics.Status != KugouApiStatus.Ok || _activeLyrics.Lines.Count == 0)
            {
                string sub = _activeLyrics != null && !string.IsNullOrEmpty(_activeLyrics.Message)
                    ? _activeLyrics.Message
                    : "纯音乐 / 暂无歌词";
                SetLineState(headerLine, sub, false, 0f);
                return;
            }

            float offset = Plugin.LyricsOffsetSeconds != null ? Plugin.LyricsOffsetSeconds.Value : 0f;
            float effectiveTime = playTime + offset;
            List<KugouLyricLine> lines = _activeLyrics.Lines;

            if (effectiveTime < lines[0].TimeSeconds)
            {
                SetLineState(headerLine, lines[0].Text, true, 0f);
                return;
            }

            int idx = 0;
            int lo = 0;
            int hi = lines.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (lines[mid].TimeSeconds <= effectiveTime)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            float startSec = lines[idx].TimeSeconds;
            float nextSec = idx + 1 < lines.Count ? lines[idx + 1].TimeSeconds : startSec + 5f;
            float span = Mathf.Max(0.4f, nextSec - startSec);
            float lineProgress = (effectiveTime - startSec) / span;

            SetLineState(lines[idx].Text, idx + 1 < lines.Count ? lines[idx + 1].Text : string.Empty, true, lineProgress);
        }

        private void CancelFetch()
        {
            if (_fetchCts != null)
            {
                try
                {
                    _fetchCts.Cancel();
                    _fetchCts.Dispose();
                }
                catch
                {
                    // 已释放忽略
                }

                _fetchCts = null;
            }
        }

        private static string TryReadDiskLrc(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            try
            {
                string path = Path.Combine(KugouPaths.LyricsDir, hash.Trim().ToLowerInvariant() + ".lrc");
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteDiskLrc(string hash, string rawLrc)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(rawLrc))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(KugouPaths.LyricsDir);
                string path = Path.Combine(KugouPaths.LyricsDir, hash.Trim().ToLowerInvariant() + ".lrc");
                File.WriteAllText(path, rawLrc, Encoding.UTF8);
            }
            catch
            {
                // 缓存写入失败不影响显示
            }
        }

        public void Dispose()
        {
            CancelFetch();
            _memoryCache.Clear();
        }
    }
}
