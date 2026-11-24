using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bulbul;

namespace ChillWithYou.KugouMusic.Models
{
    /// <summary>插件内部常量。</summary>
    internal static class KugouIds
    {
        public const string PluginGuid = "chillwithyou.kugoumusic";
        public const string PluginName = "Chill with You - Kugou Music Bridge";
        public const string PluginVersion = "1.0.1";
        public const string DefaultGitHubRepo = "huntuo146/ChillWithYou-KugouMusic";
        public const string SteamAppId = "3548580";

        public const string UuidPrefix = "kugou:";

        public static bool IsKugouUuid(string uuid)
        {
            return !string.IsNullOrEmpty(uuid) &&
                   uuid.StartsWith(UuidPrefix, StringComparison.Ordinal);
        }
    }

    /// <summary>接口后端模式。</summary>
    internal enum KugouBackendMode
    {
        NativeDirect = 0,
        ExternalGateway = 1,
    }

    /// <summary>目标音质。</summary>
    internal enum KugouQuality
    {
        Mp3128 = 0,
        Mp3320 = 1,
        M4A = 2,
    }

    /// <summary>登录态种类。</summary>
    internal enum KugouLoginKind
    {
        Guest = 0,
        Cookie = 1,
        QrCode = 2,
    }

    /// <summary>当前音源。</summary>
    internal enum KugouSourceKind
    {
        Official = 0,
        Kugou = 1,
    }

    /// <summary>统一的接口调用状态（不抛异常，由调用方按状态降级）。</summary>
    internal enum KugouApiStatus
    {
        Ok = 0,
        NotImplemented = 1,
        NetworkError = 2,
        AuthRequired = 3,
        TrialOnly = 4,
        NoSource = 5,
        ParseError = 6,
        Cancelled = 7,
        InvalidArgument = 8,
    }

    /// <summary>单曲元数据（酷狗侧）。</summary>
    [Serializable]
    internal sealed class KugouTrack
    {
        public string Hash = string.Empty;
        public string Hash320 = string.Empty;
        public string AlbumId = string.Empty;
        public string AlbumAudioId = string.Empty;
        public string Name = string.Empty;
        public string Singer = string.Empty;
        public string AlbumName = string.Empty;
        public int DurationSeconds = 0;
        public int PayType = 0;
        public int Price = 0;
        public string ExtName = "mp3";
        public string CoverUrl = string.Empty;

        public string GetUuid()
        {
            return KugouIds.UuidPrefix + (Hash ?? string.Empty);
        }

        public string GetDisplayName()
        {
            return string.IsNullOrEmpty(Name) ? "未知歌曲" : Name;
        }

        public string GetDisplaySinger()
        {
            return string.IsNullOrEmpty(Singer) ? "未知歌手" : Singer;
        }

        public bool IsPaid()
        {
            return PayType > 0 || Price > 0;
        }

        public KugouTrack Clone()
        {
            return new KugouTrack
            {
                Hash = Hash,
                Hash320 = Hash320,
                AlbumId = AlbumId,
                AlbumAudioId = AlbumAudioId,
                Name = Name,
                Singer = Singer,
                AlbumName = AlbumName,
                DurationSeconds = DurationSeconds,
                PayType = PayType,
                Price = Price,
                ExtName = ExtName,
                CoverUrl = CoverUrl,
            };
        }
    }

    /// <summary>音源直链解析结果。</summary>
    internal sealed class KugouPlayUrlResult
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string Message = string.Empty;
        public string Url = string.Empty;
        public int Bitrate = 0;
        public int TimeLengthSeconds = 0;
        public string ExtName = "mp3";
        public bool IsTrial = false;

        public bool HasUrl()
        {
            return Status == KugouApiStatus.Ok && !string.IsNullOrEmpty(Url);
        }

        public static KugouPlayUrlResult Fail(KugouApiStatus status, string message)
        {
            return new KugouPlayUrlResult { Status = status, Message = message ?? string.Empty };
        }
    }

    /// <summary>歌单摘要。</summary>
    [Serializable]
    internal sealed class KugouPlaylistSummary
    {
        public string SpecialId = string.Empty;
        public string Name = string.Empty;
        public string Author = string.Empty;
        public int SongCount = 0;
        public string CoverUrl = string.Empty;
    }

    /// <summary>搜索结果（单曲与歌单共用）。</summary>
    internal sealed class KugouSearchResult
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string Message = string.Empty;
        public int Total = 0;
        public readonly List<KugouTrack> Tracks = new List<KugouTrack>();
        public readonly List<KugouPlaylistSummary> Playlists = new List<KugouPlaylistSummary>();
    }

    /// <summary>歌单详情。</summary>
    internal sealed class KugouPlaylistDetail
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string Message = string.Empty;
        public KugouPlaylistSummary Summary = new KugouPlaylistSummary();
        public readonly List<KugouTrack> Tracks = new List<KugouTrack>();
    }

    /// <summary>登录状态。</summary>
    internal sealed class KugouLoginState
    {
        public KugouLoginKind Kind = KugouLoginKind.Guest;
        public string UserId = string.Empty;
        public string Token = string.Empty;
        public string Cookie = string.Empty;
        public string Dfid = string.Empty;
        public string Nickname = string.Empty;
        public bool IsVip = false;

        public bool IsLoggedIn()
        {
            return Kind != KugouLoginKind.Guest &&
                   (!string.IsNullOrEmpty(UserId) || !string.IsNullOrEmpty(Cookie));
        }
    }

    /// <summary>连通性测试结果。</summary>
    internal sealed class KugouConnectionTestResult
    {
        public bool Ok = false;
        public long ElapsedMs = 0;
        public string Message = string.Empty;
    }

    /// <summary>
    /// 酷狗曲目与游戏 <see cref="GameAudioInfo"/> 实例的关联表。
    /// 使用 ConditionalWeakTable，保证曲目对象被游戏回收时不阻止 GC。
    /// </summary>
    internal sealed class KugouTrackRegistry
    {
        private readonly ConditionalWeakTable<GameAudioInfo, KugouTrack> _byInstance =
            new ConditionalWeakTable<GameAudioInfo, KugouTrack>();

        private readonly Dictionary<string, KugouTrack> _byUuid =
            new Dictionary<string, KugouTrack>(StringComparer.Ordinal);

        private readonly object _gate = new object();

        public void Register(GameAudioInfo info, KugouTrack track)
        {
            if (info == null || track == null)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    _byInstance.Remove(info);
                }
                catch
                {
                    // Remove 在未登记时可能抛 KeyNotFound，忽略
                }

                try
                {
                    _byInstance.Add(info, track);
                }
                catch (ArgumentException)
                {
                    // 并发下已存在，忽略
                }

                if (!string.IsNullOrEmpty(track.Hash))
                {
                    _byUuid[track.GetUuid()] = track;
                }
            }
        }

        public bool TryGet(GameAudioInfo info, out KugouTrack track)
        {
            track = null;
            if (info == null)
            {
                return false;
            }

            try
            {
                return _byInstance.TryGetValue(info, out track);
            }
            catch
            {
                track = null;
                return false;
            }
        }

        public bool IsKugou(GameAudioInfo info)
        {
            KugouTrack ignored;
            return TryGet(info, out ignored);
        }

        public KugouTrack GetByUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid))
            {
                return null;
            }

            lock (_gate)
            {
                KugouTrack track;
                return _byUuid.TryGetValue(uuid, out track) ? track : null;
            }
        }

        public void ClearUuidIndex()
        {
            lock (_gate)
            {
                _byUuid.Clear();
            }
        }
    }

    /// <summary>二维码登录状态。</summary>
    internal enum KugouQrState
    {
        Waiting = 0,
        Scanned = 1,
        Confirmed = 2,
        Expired = 3,
        Failed = 4,
        NotImplemented = 5,
    }

    /// <summary>二维码创建结果（key + 图片）。</summary>
    internal sealed class KugouQrLoginSession
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string Key = string.Empty;
        /// <summary>PNG 的 base64（不含 data: 前缀）。</summary>
        public string QrImageBase64 = string.Empty;
        public string QrImageUrl = string.Empty;
        public string Message = string.Empty;

        public bool HasQrImage()
        {
            return !string.IsNullOrEmpty(QrImageBase64);
        }
    }

    /// <summary>二维码轮询结果。</summary>
    internal sealed class KugouQrCheckResult
    {
        public KugouQrState State = KugouQrState.Waiting;
        public string Key = string.Empty;
        public string Token = string.Empty;
        public string UserId = string.Empty;
        public string Cookie = string.Empty;
        public string Nickname = string.Empty;
        public string Message = string.Empty;
    }

    /// <summary>账号详情。</summary>
    internal sealed class KugouUserDetail
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string UserId = string.Empty;
        public string Nickname = string.Empty;
        public bool IsVip = false;
        public string Message = string.Empty;
    }

    /// <summary>个人歌单同步结果。</summary>
    internal sealed class KugouUserPlaylistResult
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string Message = string.Empty;
        public readonly List<KugouPlaylistSummary> Playlists = new List<KugouPlaylistSummary>();
    }

    /// <summary>单行时间戳歌词。</summary>
    internal sealed class KugouLyricLine
    {
        public float TimeSeconds;
        public string Text = string.Empty;
    }

    /// <summary>歌词搜索与下载结果。</summary>
    internal sealed class KugouLyricsResult
    {
        public KugouApiStatus Status = KugouApiStatus.Ok;
        public string Message = string.Empty;
        public string RawLrc = string.Empty;
        public readonly List<KugouLyricLine> Lines = new List<KugouLyricLine>();
    }
}
