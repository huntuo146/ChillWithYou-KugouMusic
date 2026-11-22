using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>
    /// 方案一：纯 C# 直连酷狗移动端/网页端公开接口（零外部依赖）。
    /// P1 覆盖：搜歌 / 搜歌单 / 歌单解析（短链·URL·酷狗码·纯 ID）/ 歌单全量分页 /
    /// trackercdn 直链（免费曲目；付费曲目返回 AuthRequired）/ 手动 Cookie 传入。
    /// 扫码登录、个人歌单、概念版 VIP 属于二期，这里明确返回 NotImplemented。
    /// </summary>
    internal sealed class NativeKugouClient : IKugouClient
    {
        private const string MobileHost = "http://mobilecdn.kugou.com";
        private const string TrackerHost = "http://trackercdn.kugou.com";
        private const string WebHost = "http://m.kugou.com";

        private const int PlaylistPageSize = 100;
        private const int MaxPlaylistPages = 60;

        private readonly IHttpTransport _http;
        private readonly string _cookie;
        private readonly string _dfid;
        private readonly string _userId;
        private readonly string _token;

        private static readonly Regex SpecialIdRegex = new Regex(
            @"(?:specialid|specialId|global_collection_id|collection_id)\s*[=:]\s*""?(\d{3,})|/plist/list/(\d{3,})|/special/single/(\d{3,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public NativeKugouClient(
            IHttpTransport http,
            string cookie,
            string dfid,
            string userId,
            string token)
        {
            _http = http;
            _cookie = cookie ?? string.Empty;
            _dfid = dfid ?? string.Empty;
            _userId = userId ?? string.Empty;
            _token = token ?? string.Empty;
        }

        public KugouBackendMode Mode
        {
            get { return KugouBackendMode.NativeDirect; }
        }

        public string DisplayName
        {
            get { return "NativeDirect（直连酷狗）"; }
        }

        public bool SupportsAccount
        {
            get { return false; }
        }

        public void Dispose()
        {
        }

        // ---------------- 连通性 ----------------

        public async UniTask<KugouConnectionTestResult> TestConnectionAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new KugouConnectionTestResult();

            string url = MobileHost + "/api/v3/search/song?format=json&keyword=test&page=1&pagesize=1&showtype=1";
            HttpResponseData response = await _http.GetAsync(url, BuildHeaders(), ct);
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;

            if (!response.Ok)
            {
                result.Ok = false;
                result.Message = "网络失败: " + response.Error;
                return result;
            }

            JObject json = TryParse(response.Text);
            if (json == null)
            {
                result.Ok = false;
                result.Message = "响应不是合法 JSON";
                return result;
            }

            int status = KugouJson.Int(json, 0, "status", "errcode");
            result.Ok = status == 1;
            result.Message = result.Ok
                ? "酷狗公开接口可用（" + result.ElapsedMs + "ms）"
                : "接口返回 status=" + status;
            return result;
        }

        // ---------------- 歌单 ----------------

        public async UniTask<KugouPlaylistDetail> ParsePlaylistInputAsync(string input, CancellationToken ct)
        {
            var detail = new KugouPlaylistDetail();
            if (string.IsNullOrEmpty(input))
            {
                return Fail(detail, KugouApiStatus.InvalidArgument, "歌单输入为空");
            }

            string text = input.Trim();
            string specialId = ExtractSpecialId(text);
            string responseText = null;

            if (string.IsNullOrEmpty(specialId))
            {
                if (IsHttpUrl(text))
                {
                    // 分享短链 / 网页歌单：跟随重定向后从最终 URL 或页面正文里找 specialid
                    HttpResponseData response = await _http.GetAsync(text, BuildHeaders(), ct);
                    if (!response.Ok && string.IsNullOrEmpty(response.FinalUrl))
                    {
                        return Fail(detail, KugouApiStatus.NetworkError, "短链解析失败: " + response.Error);
                    }

                    specialId = ExtractSpecialId(response.FinalUrl) ?? ExtractSpecialId(response.Text);
                    responseText = response.Text;
                }
                else
                {
                    // 酷狗码 / 纯字母数字码：先按 specialid 直查，再走网页解析
                    string probe = MobileHost + "/api/v3/special/info?format=json&specialid=" +
                                   Uri.EscapeDataString(text);
                    HttpResponseData info = await _http.GetAsync(probe, BuildHeaders(), ct);
                    if (info.Ok)
                    {
                        JObject json = TryParse(info.Text);
                        if (json != null && KugouJson.Int(json, 0, "status") == 1)
                        {
                            specialId = text;
                        }
                    }

                    if (string.IsNullOrEmpty(specialId))
                    {
                        string webUrl = WebHost + "/plist/list/" + Uri.EscapeDataString(text) + "?json=true";
                        HttpResponseData web = await _http.GetAsync(webUrl, BuildHeaders(), ct);
                        specialId = ExtractSpecialId(web.FinalUrl) ?? ExtractSpecialId(web.Text);
                        responseText = web.Text;
                    }
                }
            }

            if (string.IsNullOrEmpty(specialId))
            {
                string message = "无法从输入中解析出歌单 ID";
                if (!string.IsNullOrEmpty(responseText))
                {
                    message += "（返回内容长度 " + responseText.Length + "）";
                }

                return Fail(detail, KugouApiStatus.ParseError, message);
            }

            Plugin.Log.LogInfo("[Native] 歌单输入解析为 specialid=" + specialId);
            return await GetPlaylistDetailAsync(specialId, ct);
        }

        public async UniTask<KugouPlaylistDetail> GetPlaylistDetailAsync(string specialId, CancellationToken ct)
        {
            var detail = new KugouPlaylistDetail();
            if (string.IsNullOrEmpty(specialId))
            {
                return Fail(detail, KugouApiStatus.InvalidArgument, "specialid 为空");
            }

            detail.Summary.SpecialId = specialId;

            // 1) 元信息（失败不阻塞曲目拉取）
            try
            {
                string infoUrl = MobileHost + "/api/v3/special/info?format=json&specialid=" +
                                 Uri.EscapeDataString(specialId);
                HttpResponseData infoResponse = await _http.GetAsync(infoUrl, BuildHeaders(), ct);
                if (infoResponse.Ok)
                {
                    JObject infoJson = TryParse(infoResponse.Text);
                    JToken data = KugouJson.Token(infoJson, "data");
                    if (data != null)
                    {
                        detail.Summary.Name = KugouJson.Str(data, "specialname", "special_name", "name") ?? string.Empty;
                        detail.Summary.Author = KugouJson.Str(data, "nickname", "author", "username") ?? string.Empty;
                        detail.Summary.SongCount = KugouJson.Int(data, 0, "songcount", "song_count", "total");
                        detail.Summary.CoverUrl = KugouJson.Str(data, "imgurl", "img_url", "cover") ?? string.Empty;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Fail(detail, KugouApiStatus.Cancelled, "已取消");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Native] 歌单元信息获取失败: " + ex.Message);
            }

            if (string.IsNullOrEmpty(detail.Summary.Name))
            {
                detail.Summary.Name = "酷狗歌单 " + specialId;
            }

            // 2) 曲目分页
            int page = 1;
            int total = 0;
            bool firstPage = true;

            while (page <= MaxPlaylistPages)
            {
                if (ct.IsCancellationRequested)
                {
                    return Fail(detail, KugouApiStatus.Cancelled, "已取消");
                }

                string url = MobileHost + "/api/v3/special/song?format=json&specialid=" +
                             Uri.EscapeDataString(specialId) + "&page=" + page + "&pagesize=" + PlaylistPageSize;

                HttpResponseData response = await _http.GetAsync(url, BuildHeaders(), ct);
                if (!response.Ok)
                {
                    if (firstPage)
                    {
                        return Fail(detail, KugouApiStatus.NetworkError, "歌单拉取失败: " + response.Error);
                    }

                    break;
                }

                JObject json = TryParse(response.Text);
                if (json == null)
                {
                    if (firstPage)
                    {
                        return Fail(detail, KugouApiStatus.ParseError, "歌单响应不是合法 JSON");
                    }

                    break;
                }

                int status = KugouJson.Int(json, 0, "status", "errcode");
                if (status != 1)
                {
                    if (firstPage)
                    {
                        return Fail(detail, KugouApiStatus.NoSource, "歌单接口返回 status=" + status);
                    }

                    break;
                }

                JToken data = KugouJson.Token(json, "data");
                JArray songs = KugouJson.Arr(data, "info", "songs", "list");
                total = KugouJson.Int(data, total, "total");

                if (songs == null || songs.Count == 0)
                {
                    break;
                }

                int added = 0;
                for (int i = 0; i < songs.Count; i++)
                {
                    KugouTrack track = ParseTrack(songs[i]);
                    if (track == null || string.IsNullOrEmpty(track.Hash))
                    {
                        continue;
                    }

                    detail.Tracks.Add(track);
                    added++;
                }

                firstPage = false;
                page++;

                if (added == 0)
                {
                    break;
                }

                if (total > 0 && detail.Tracks.Count >= total)
                {
                    break;
                }

                if (songs.Count < PlaylistPageSize)
                {
                    break;
                }
            }

            if (detail.Tracks.Count == 0)
            {
                return Fail(detail, KugouApiStatus.NoSource, "歌单内没有可用曲目");
            }

            detail.Summary.SongCount = detail.Tracks.Count;
            detail.Status = KugouApiStatus.Ok;
            Plugin.Log.LogInfo("[Native] 歌单《" + detail.Summary.Name + "》拉取完成：" + detail.Tracks.Count + " 首");
            return detail;
        }

        // ---------------- 搜索 ----------------

        public async UniTask<KugouSearchResult> SearchTracksAsync(
            string keyword,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            var result = new KugouSearchResult();
            if (string.IsNullOrEmpty(keyword))
            {
                result.Status = KugouApiStatus.InvalidArgument;
                result.Message = "关键词为空";
                return result;
            }

            if (page < 1)
            {
                page = 1;
            }

            if (pageSize < 1 || pageSize > 100)
            {
                pageSize = 30;
            }

            string url = MobileHost + "/api/v3/search/song?format=json&showtype=1&keyword=" +
                         Uri.EscapeDataString(keyword) + "&page=" + page + "&pagesize=" + pageSize;

            HttpResponseData response = await _http.GetAsync(url, BuildHeaders(), ct);
            if (!response.Ok)
            {
                result.Status = KugouApiStatus.NetworkError;
                result.Message = "搜索失败: " + response.Error;
                return result;
            }

            JObject json = TryParse(response.Text);
            if (json == null)
            {
                result.Status = KugouApiStatus.ParseError;
                result.Message = "搜索响应不是合法 JSON";
                return result;
            }

            JToken data = KugouJson.Token(json, "data");
            result.Total = KugouJson.Int(data, 0, "total");
            JArray items = KugouJson.Arr(data, "info", "songs", "list");

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    KugouTrack track = ParseTrack(items[i]);
                    if (track != null && !string.IsNullOrEmpty(track.Hash))
                    {
                        result.Tracks.Add(track);
                    }
                }
            }

            result.Status = KugouApiStatus.Ok;
            return result;
        }

        public async UniTask<KugouSearchResult> SearchPlaylistsAsync(
            string keyword,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            var result = new KugouSearchResult();
            if (string.IsNullOrEmpty(keyword))
            {
                result.Status = KugouApiStatus.InvalidArgument;
                result.Message = "关键词为空";
                return result;
            }

            if (pageSize < 1 || pageSize > 100)
            {
                pageSize = 20;
            }

            string url = MobileHost + "/api/v3/search/special?format=json&showtype=1&keyword=" +
                         Uri.EscapeDataString(keyword) + "&page=" + page + "&pagesize=" + pageSize;

            HttpResponseData response = await _http.GetAsync(url, BuildHeaders(), ct);
            if (!response.Ok)
            {
                result.Status = KugouApiStatus.NetworkError;
                result.Message = "歌单搜索失败: " + response.Error;
                return result;
            }

            JObject json = TryParse(response.Text);
            if (json == null)
            {
                result.Status = KugouApiStatus.ParseError;
                result.Message = "歌单搜索响应不是合法 JSON";
                return result;
            }

            JToken data = KugouJson.Token(json, "data");
            result.Total = KugouJson.Int(data, 0, "total");
            JArray items = KugouJson.Arr(data, "info", "lists", "list");

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    JObject item = items[i] as JObject;
                    if (item == null)
                    {
                        continue;
                    }

                    string specialId = KugouJson.Str(item, "specialid", "special_id", "global_collection_id", "id");
                    if (string.IsNullOrEmpty(specialId))
                    {
                        continue;
                    }

                    result.Playlists.Add(new KugouPlaylistSummary
                    {
                        SpecialId = specialId,
                        Name = KugouJson.Str(item, "specialname", "special_name", "name", "title") ?? "未命名歌单",
                        Author = KugouJson.Str(item, "nickname", "author", "username") ?? string.Empty,
                        SongCount = KugouJson.Int(item, 0, "songcount", "song_count", "total"),
                        CoverUrl = KugouJson.Str(item, "imgurl", "cover", "pic") ?? string.Empty,
                    });
                }
            }

            result.Status = KugouApiStatus.Ok;
            return result;
        }

        // ---------------- 直链 ----------------

        public async UniTask<KugouPlayUrlResult> GetPlayUrlAsync(
            KugouTrack track,
            KugouQuality quality,
            CancellationToken ct)
        {
            if (track == null || string.IsNullOrEmpty(track.Hash))
            {
                return KugouPlayUrlResult.Fail(KugouApiStatus.InvalidArgument, "曲目缺少 hash");
            }

            bool wantHigh = quality != KugouQuality.Mp3128;
            string highHash = wantHigh && !string.IsNullOrEmpty(track.Hash320) ? track.Hash320 : track.Hash;

            KugouPlayUrlResult result = await RequestPlayUrlAsync(highHash, wantHigh ? "hq" : "128", track, ct);
            if (result.HasUrl())
            {
                return result;
            }

            // 320 / M4A 拿不到（未登录 / 无版权）时，用原始 hash 以 128 兜底重试一次
            bool retry = wantHigh &&
                         (result.Status == KugouApiStatus.AuthRequired || result.Status == KugouApiStatus.NoSource) &&
                         !string.Equals(highHash, track.Hash, StringComparison.Ordinal);

            if (!retry)
            {
                return result;
            }

            Plugin.Log.LogInfo("[Native] " + quality + " 不可用（" + result.Status + "），回退 128k 重试");
            KugouPlayUrlResult low = await RequestPlayUrlAsync(track.Hash, "128", track, ct);
            return low.HasUrl() ? low : result;
        }

        private async UniTask<KugouPlayUrlResult> RequestPlayUrlAsync(
            string hash,
            string br,
            KugouTrack track,
            CancellationToken ct)
        {
            // 注意：key 必须与 URL 里实际传出的 hash 字符串逐字一致，大小写混用会返回 "Bad key"
            string key = Md5Hex(hash + "kgcloudv2");
            var url = new StringBuilder();
            url.Append(TrackerHost).Append("/i/v2/?key=").Append(key)
               .Append("&hash=").Append(hash)
               .Append("&br=").Append(br)
               .Append("&appid=1005&pid=2&cmd=25&behavior=play");

            if (!string.IsNullOrEmpty(_dfid))
            {
                url.Append("&dfid=").Append(Uri.EscapeDataString(_dfid));
            }

            if (!string.IsNullOrEmpty(_userId))
            {
                url.Append("&userid=").Append(Uri.EscapeDataString(_userId));
            }

            if (!string.IsNullOrEmpty(_token))
            {
                url.Append("&token=").Append(Uri.EscapeDataString(_token));
            }

            HttpResponseData response = await _http.GetAsync(url.ToString(), BuildHeaders(), ct);
            if (!response.Ok)
            {
                return KugouPlayUrlResult.Fail(KugouApiStatus.NetworkError, "直链接口失败: " + response.Error);
            }

            JObject json = TryParse(response.Text);
            if (json == null)
            {
                return KugouPlayUrlResult.Fail(KugouApiStatus.ParseError, "直链响应不是合法 JSON");
            }

            int status = KugouJson.Int(json, 0, "status");
            if (status == 2)
            {
                return KugouPlayUrlResult.Fail(
                    KugouApiStatus.AuthRequired,
                    "该曲目需要登录或购买（status=2）");
            }

            if (status != 1)
            {
                return KugouPlayUrlResult.Fail(KugouApiStatus.NoSource, "直链接口返回 status=" + status);
            }

            string playUrl = FirstUrl(json);
            if (string.IsNullOrEmpty(playUrl))
            {
                return KugouPlayUrlResult.Fail(KugouApiStatus.NoSource, "响应中没有可用 url");
            }

            var parsed = new KugouPlayUrlResult
            {
                Status = KugouApiStatus.Ok,
                Url = playUrl,
                Bitrate = KugouJson.Int(json, 0, "bitRate", "bitrate"),
                TimeLengthSeconds = KugouJson.Int(json, 0, "timeLength", "timelength", "time_length"),
                ExtName = KugouJson.Str(json, "extName", "extname", "ext") ?? "mp3",
            };

            parsed.IsTrial = IsTrialClip(parsed.TimeLengthSeconds, track != null ? track.DurationSeconds : 0);

            if (parsed.IsTrial && track != null)
            {
                Plugin.Log.LogWarning("[Native] 试听片段: " + track.GetDisplayName() +
                                      " 实际 " + parsed.TimeLengthSeconds + "s / 原曲 " +
                                      track.DurationSeconds + "s");
            }

            return parsed;
        }

        // ---------------- 账号（二期） ----------------

        public UniTask<KugouUserPlaylistResult> GetUserPlaylistsAsync(CancellationToken ct)
        {
            var result = new KugouUserPlaylistResult
            {
                Status = KugouApiStatus.NotImplemented,
                Message = "NativeDirect 暂不支持个人歌单同步，请切换到 ExternalGateway",
            };
            return UniTask.FromResult(result);
        }

        public UniTask<KugouUserDetail> GetUserDetailAsync(CancellationToken ct)
        {
            var detail = new KugouUserDetail
            {
                Status = KugouApiStatus.NotImplemented,
                UserId = _userId,
                Message = "NativeDirect 暂不支持账号详情，请切换到 ExternalGateway",
            };
            return UniTask.FromResult(detail);
        }

        public UniTask<KugouQrLoginSession> CreateQrKeyAsync(CancellationToken ct)
        {
            var session = new KugouQrLoginSession
            {
                Status = KugouApiStatus.NotImplemented,
                Message = "NativeDirect 暂不支持扫码登录，请切换到 ExternalGateway",
            };
            return UniTask.FromResult(session);
        }

        public UniTask<KugouQrCheckResult> CheckQrStatusAsync(string key, CancellationToken ct)
        {
            var result = new KugouQrCheckResult
            {
                State = KugouQrState.NotImplemented,
                Key = key,
                Message = "NativeDirect 暂不支持扫码登录，请切换到 ExternalGateway",
            };
            return UniTask.FromResult(result);
        }

        public UniTask<KugouApiStatus> ClaimYouthVipAsync(CancellationToken ct)
        {
            return UniTask.FromResult(KugouApiStatus.NotImplemented);
        }

        public async UniTask<KugouLyricsResult> GetLyricsAsync(KugouTrack track, CancellationToken ct)
        {
            if (track == null || string.IsNullOrEmpty(track.Name))
            {
                return new KugouLyricsResult { Status = KugouApiStatus.InvalidArgument, Message = "曲目为空" };
            }

            string keyword = (string.IsNullOrEmpty(track.Singer) || track.Singer == "未知歌手")
                ? track.Name
                : (track.Singer + " - " + track.Name);
            int durationMs = track.DurationSeconds > 0 ? track.DurationSeconds * 1000 : 0;

            string searchUrl = "http://lyrics.kugou.com/search?ver=1&man=no&client=pc&keyword=" +
                               Uri.EscapeDataString(keyword) +
                               "&duration=" + durationMs +
                               "&hash=" + Uri.EscapeDataString(track.Hash ?? string.Empty);

            HttpResponseData searchResp = await _http.GetAsync(searchUrl, BuildHeaders(), ct);
            if (searchResp == null || !searchResp.Ok)
            {
                return new KugouLyricsResult
                {
                    Status = KugouApiStatus.NetworkError,
                    Message = searchResp != null ? searchResp.Error : "歌词搜索失败",
                };
            }

            JObject searchJson = TryParse(searchResp.Text);
            JArray candidates = KugouJson.Arr(searchJson, "candidates");
            if (candidates == null || candidates.Count == 0)
            {
                return new KugouLyricsResult { Status = KugouApiStatus.NoSource, Message = "纯音乐 / 暂无歌词" };
            }

            JObject first = candidates[0] as JObject;
            string id = KugouJson.Str(first, "id");
            string accesskey = KugouJson.Str(first, "accesskey");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accesskey))
            {
                return new KugouLyricsResult { Status = KugouApiStatus.NoSource, Message = "歌词候选缺少密钥" };
            }

            string downloadUrl = "http://lyrics.kugou.com/download?ver=1&client=pc&id=" +
                                 Uri.EscapeDataString(id) +
                                 "&accesskey=" + Uri.EscapeDataString(accesskey) +
                                 "&fmt=lrc&charset=utf8";

            HttpResponseData dlResp = await _http.GetAsync(downloadUrl, BuildHeaders(), ct);
            if (dlResp == null || !dlResp.Ok)
            {
                return new KugouLyricsResult
                {
                    Status = KugouApiStatus.NetworkError,
                    Message = dlResp != null ? dlResp.Error : "歌词下载失败",
                };
            }

            JObject dlJson = TryParse(dlResp.Text);
            string content = KugouJson.Str(dlJson, "content");
            return LrcParser.BuildResult(null, content);
        }

        public UniTask<string> ResolveTrackCoverUrlAsync(KugouTrack track, CancellationToken ct)
        {
            return FetchTrackCoverFallbackAsync(_http, track, ct);
        }

        internal static async UniTask<string> FetchTrackCoverFallbackAsync(
            IHttpTransport http,
            KugouTrack track,
            CancellationToken ct)
        {
            if (http == null || track == null || string.IsNullOrEmpty(track.Hash))
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(track.CoverUrl))
            {
                return track.CoverUrl;
            }

            try
            {
                string url = "https://wwwapi.kugou.com/yy/index.php?r=play/getdata&hash=" + Uri.EscapeDataString(track.Hash);
                if (!string.IsNullOrEmpty(track.AlbumId))
                {
                    url += "&album_id=" + Uri.EscapeDataString(track.AlbumId);
                }

                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0" },
                    { "Referer", "https://www.kugou.com/" },
                };

                HttpResponseData resp = await http.GetAsync(url, headers, ct);
                if (resp != null && resp.Ok && !string.IsNullOrEmpty(resp.Text))
                {
                    JObject json = TryParse(resp.Text);
                    JToken data = KugouJson.Token(json, "data");
                    string cover = KugouJson.ExtractCoverUrl(data, true);
                    if (!string.IsNullOrEmpty(cover))
                    {
                        return cover;
                    }
                }
            }
            catch
            {
                // 封面拉取失败静默降级为黑胶/首字方块
            }

            return string.Empty;
        }

        // ---------------- 解析工具 ----------------

        internal static KugouTrack ParseTrack(JToken item)
        {
            JObject obj = item as JObject;
            if (obj == null)
            {
                return null;
            }

            string hash = KugouJson.Str(obj, "hash", "filehash", "file_hash", "FileHash");
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            var track = new KugouTrack
            {
                Hash = hash,
                Hash320 = KugouJson.Str(obj, "320hash", "hash320", "hqhash", "filehash_320") ?? string.Empty,
                AlbumId = KugouJson.Str(obj, "album_id", "albumid", "albumId") ?? string.Empty,
                AlbumName = KugouJson.Str(obj, "album_name", "albumname") ?? string.Empty,
                Singer = KugouJson.Str(obj, "singername", "singer", "artist", "author", "ar") ?? string.Empty,
                DurationSeconds = KugouJson.Int(obj, 0, "duration", "timelength", "timeLength"),
                PayType = KugouJson.Int(obj, 0, "pay_type", "paytype"),
                Price = KugouJson.Int(obj, 0, "price"),
                ExtName = KugouJson.Str(obj, "extname", "extName", "ext") ?? "mp3",
                CoverUrl = KugouJson.ExtractCoverUrl(obj) ?? string.Empty,
            };

            string name = KugouJson.Str(obj, "songname", "song_name", "name", "title");
            string fileName = KugouJson.Str(obj, "filename", "file_name");

            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(fileName))
            {
                int separator = fileName.IndexOf(" - ", StringComparison.Ordinal);
                if (separator > 0)
                {
                    name = fileName.Substring(separator + 3).Trim();
                    if (string.IsNullOrEmpty(track.Singer))
                    {
                        track.Singer = fileName.Substring(0, separator).Trim();
                    }
                }
                else
                {
                    name = fileName;
                }
            }

            track.Name = string.IsNullOrEmpty(name) ? "未知歌曲" : name;

            if (string.IsNullOrEmpty(track.Singer))
            {
                track.Singer = "未知歌手";
            }

            // 部分接口给的是毫秒（阈值用 3600：超过 1 小时的时长按毫秒处理，避免误伤短曲）
            if (track.DurationSeconds > 3600)
            {
                track.DurationSeconds = track.DurationSeconds / 1000;
            }

            return track;
        }

        internal static bool IsTrialClip(int actualSeconds, int expectedSeconds)
        {
            if (actualSeconds <= 0)
            {
                return false;
            }

            if (expectedSeconds <= 90)
            {
                return actualSeconds < expectedSeconds && actualSeconds <= 30;
            }

            return actualSeconds <= 65;
        }

        private static string FirstUrl(JToken json)
        {
            if (json == null)
            {
                return null;
            }

            JToken urls = KugouJson.Token(json, "url");
            if (urls == null)
            {
                urls = KugouJson.Token(json, "urls");
            }

            if (urls == null)
            {
                return null;
            }

            if (urls.Type == JTokenType.Array)
            {
                JArray array = (JArray)urls;
                for (int i = 0; i < array.Count; i++)
                {
                    string value = array[i] == null ? null : array[i].ToString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }

                return null;
            }

            string single = urls.ToString();
            return string.IsNullOrEmpty(single) ? null : single;
        }

        internal static string ExtractSpecialId(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            Match match = SpecialIdRegex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            for (int group = 1; group < match.Groups.Count; group++)
            {
                if (match.Groups[group].Success)
                {
                    return match.Groups[group].Value;
                }
            }

            return null;
        }

        private static bool IsHttpUrl(string text)
        {
            return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, string> BuildHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36" },
                { "Accept", "application/json, text/plain, */*" },
                { "Referer", "http://m.kugou.com/" },
            };

            if (!string.IsNullOrEmpty(_cookie))
            {
                headers["Cookie"] = _cookie;
            }

            return headers;
        }

        internal static JObject TryParse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            try
            {
                return JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        internal static string Md5Hex(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                var builder = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static KugouPlaylistDetail Fail(KugouPlaylistDetail detail, KugouApiStatus status, string message)
        {
            detail.Status = status;
            detail.Message = message ?? string.Empty;
            Plugin.Log.LogWarning("[Native] " + message);
            return detail;
        }
    }
}