using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>
    /// 方案二：对接本地开源 MakcRe/KuGouMusicApi HTTP 网关（默认 http://127.0.0.1:3000）。
    /// 该后端承接扫码登录、个人歌单、账号详情、概念版领 VIP 等完整能力。
    /// 已按实际 KuGouMusicApi v1.6.x 响应与缓存行为做兼容适配：
    /// - 自动注册 /register/dev 获取 dfid 并在 Cookie 中透传；
    /// - 动态/轮询/播放地址接口附加 timestamp 绕过网关 apicache；
    /// - 兼容 /login/qr/create(base64) 与 /login/qr/check(data.status)；
    /// - 兼容 /song/url 顶层 url[] 数组与 std_hash_time 毫秒时长；
    /// - 兼容 collection_xxx / listid:xxx / 纯数字 specialid 歌单。
    /// </summary>
    internal sealed class ExternalApiKugouClient : IKugouClient
    {
        private const string MobileHost = "http://mobilecdn.kugou.com";

        private static readonly Regex CollectionIdRegex = new Regex(
            @"collection_\d+_\d+_\d+_\d+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string MobileWebUserAgent =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148";

        private readonly IHttpTransport _http;
        private readonly string _baseUrl;
        private readonly string _token;
        private readonly string _userId;
        private readonly string _cookie;
        private string _dfid;

        public ExternalApiKugouClient(
            IHttpTransport http,
            string baseUrl,
            string token,
            string userId,
            string cookie)
        {
            _http = http;
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _token = token ?? string.Empty;
            _userId = userId ?? string.Empty;
            _cookie = cookie ?? string.Empty;

            string fromConfig = Plugin.Dfid != null ? Plugin.Dfid.Value : string.Empty;
            string fromCookie = ExtractCookieValue(_cookie, "dfid");
            _dfid = !string.IsNullOrEmpty(fromConfig) ? fromConfig.Trim() : fromCookie;
        }

        public KugouBackendMode Mode
        {
            get { return KugouBackendMode.ExternalGateway; }
        }

        public string DisplayName
        {
            get { return "ExternalGateway（" + _baseUrl + "）"; }
        }

        public bool SupportsAccount
        {
            get { return true; }
        }

        public void Dispose()
        {
        }

        // ---------------- 连通性 ----------------

        public async UniTask<KugouConnectionTestResult> TestConnectionAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new KugouConnectionTestResult();

            await EnsureDfidAsync(ct);

            ApiRaw raw = await GetJsonAsync(
                "/search",
                BuildArgs("keywords", "test", "page", "1", "pagesize", "1", "limit", "1"),
                ct,
                noCache: true);

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;

            if (raw == null || raw.Json == null)
            {
                result.Ok = false;
                result.Message = "网关不可达: " + (raw != null ? raw.Error : _baseUrl);
                return result;
            }

            result.Ok = true;
            result.Message = "网关可用（" + result.ElapsedMs + "ms" +
                             (string.IsNullOrEmpty(_dfid) ? string.Empty : "，dfid 已就绪") + "）";
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
            string specialId = ExtractCollectionId(text) ?? NativeKugouClient.ExtractSpecialId(text);

            if (string.IsNullOrEmpty(specialId) && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (IsDigits(text) || text.StartsWith("listid:", StringComparison.OrdinalIgnoreCase))
                {
                    specialId = text;
                }
                else
                {
                    await EnsureDfidAsync(ct);
                    ApiRaw probe = await GetJsonAsync(
                        "/search",
                        BuildArgs("keywords", text, "page", "1", "pagesize", "1", "limit", "1", "type", "special"),
                        ct);

                    JArray probeList = probe != null && probe.Json != null
                        ? KugouJson.Arr(KugouJson.Pick(probe.Json, "data"), "lists", "info", "list")
                        : null;

                    if (probeList != null && probeList.Count > 0)
                    {
                        specialId = ExtractPlaylistId(probeList[0] as JObject);
                    }
                }
            }

            if (string.IsNullOrEmpty(specialId) && text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                KugouPlaylistDetail webResolved = await ResolveShareUrlPlaylistAsync(text, detail, ct);
                if (webResolved != null)
                {
                    return webResolved;
                }
            }

            if (string.IsNullOrEmpty(specialId))
            {
                return Fail(detail, KugouApiStatus.ParseError, "无法从输入中解析出歌单 ID");
            }

            Plugin.Log.LogInfo("[External] 歌单输入解析为 id=" + specialId);
            return await GetPlaylistDetailAsync(specialId, ct);
        }

        public async UniTask<KugouPlaylistDetail> GetPlaylistDetailAsync(string specialId, CancellationToken ct)
        {
            var detail = new KugouPlaylistDetail();
            if (string.IsNullOrEmpty(specialId))
            {
                return Fail(detail, KugouApiStatus.InvalidArgument, "歌单 ID 为空");
            }

            await EnsureDfidAsync(ct);

            string targetId = specialId.Trim();
            detail.Summary.SpecialId = targetId;

            // 1) 个人无公开 collection_id 的歌单（走 /playlist/track/all/new）
            if (targetId.StartsWith("listid:", StringComparison.OrdinalIgnoreCase))
            {
                string listId = targetId.Substring("listid:".Length).Trim();
                detail.Summary.Name = "酷狗歌单 " + listId;
                return await FetchTracksPagedAsync(
                    detail,
                    "/playlist/track/all/new",
                    "listid",
                    listId,
                    ct);
            }

            // 2) 纯数字 specialid：先尝试换取 global_collection_id，失败则直接走移动端接口兜底
            if (IsDigits(targetId))
            {
                string resolvedCollection = await TryResolveCollectionIdFromSpecialIdAsync(targetId, detail, ct);
                if (!string.IsNullOrEmpty(resolvedCollection))
                {
                    targetId = resolvedCollection;
                    detail.Summary.SpecialId = targetId;
                }
                else
                {
                    return await FetchNumericSpecialFallbackAsync(targetId, detail, ct);
                }
            }

            // 3) 标准 KuGouMusicApi 歌单详情（/playlist/detail 要求参数名 ids，data 为数组）
            ApiRaw meta = await GetJsonAsync("/playlist/detail", BuildArgs("ids", targetId), ct);
            if (meta != null && meta.Json != null)
            {
                JObject metaObject = ExtractFirstObject(KugouJson.Pick(meta.Json, "data", "data.info", "info"));
                ApplyPlaylistSummary(detail.Summary, metaObject);
            }

            if (string.IsNullOrEmpty(detail.Summary.Name))
            {
                detail.Summary.Name = "酷狗歌单 " + targetId;
            }

            return await FetchTracksPagedAsync(
                detail,
                "/playlist/track/all",
                "id",
                targetId,
                ct);
        }

        private async UniTask<KugouPlaylistDetail> FetchTracksPagedAsync(
            KugouPlaylistDetail detail,
            string endpoint,
            string idParamName,
            string idValue,
            CancellationToken ct)
        {
            int page = 1;
            const int limit = 300;
            const int maxPages = 40;

            while (page <= maxPages)
            {
                if (ct.IsCancellationRequested)
                {
                    return Fail(detail, KugouApiStatus.Cancelled, "已取消");
                }

                ApiRaw raw = await GetJsonAsync(
                    endpoint,
                    BuildArgs(
                        idParamName, idValue,
                        "page", page.ToString(),
                        "pagesize", limit.ToString(),
                        "limit", limit.ToString()),
                    ct);

                if (raw == null || raw.Json == null)
                {
                    if (page == 1)
                    {
                        return Fail(detail, KugouApiStatus.NetworkError, "歌单曲目获取失败: " + (raw != null ? raw.Error : "网关无响应"));
                    }

                    break;
                }

                JToken data = KugouJson.Pick(raw.Json, "data", "data.songs", "songs");
                JObject listInfo = KugouJson.Pick(raw.Json, "data.list_info", "list_info") as JObject;
                if (listInfo != null &&
                    (string.IsNullOrEmpty(detail.Summary.Name) ||
                     detail.Summary.Name.StartsWith("酷狗歌单 ", StringComparison.Ordinal)))
                {
                    ApplyPlaylistSummary(detail.Summary, listInfo);
                }

                JArray songs = KugouJson.Arr(data, "songs", "info", "list", "lists") ?? data as JArray;
                if (songs == null || songs.Count == 0)
                {
                    break;
                }

                int added = 0;
                for (int i = 0; i < songs.Count; i++)
                {
                    KugouTrack track = ParseGatewayTrack(songs[i]);
                    if (track != null && !string.IsNullOrEmpty(track.Hash))
                    {
                        detail.Tracks.Add(track);
                        added++;
                    }
                }

                page++;
                if (added == 0 || songs.Count < limit)
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
            Plugin.Log.LogInfo("[External] 歌单《" + detail.Summary.Name + "》拉取完成：" + detail.Tracks.Count + " 首");
            return detail;
        }

        private async UniTask<string> TryResolveCollectionIdFromSpecialIdAsync(
            string specialId,
            KugouPlaylistDetail detail,
            CancellationToken ct)
        {
            try
            {
                string infoUrl = MobileHost + "/api/v3/special/info?format=json&specialid=" +
                                 Uri.EscapeDataString(specialId);
                HttpResponseData response = await _http.GetAsync(infoUrl, BuildHeaders(), ct);
                if (response == null || !response.Ok || string.IsNullOrEmpty(response.Text))
                {
                    return null;
                }

                JObject json = JObject.Parse(response.Text);
                JObject data = KugouJson.Token(json, "data") as JObject;
                if (data == null)
                {
                    return null;
                }

                ApplyPlaylistSummary(detail.Summary, data);
                string gid = KugouJson.Str(data, "global_collection_id", "gid", "list_create_gid");
                return !string.IsNullOrEmpty(gid) && gid.StartsWith("collection_", StringComparison.OrdinalIgnoreCase)
                    ? gid
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private async UniTask<KugouPlaylistDetail> FetchNumericSpecialFallbackAsync(
            string specialId,
            KugouPlaylistDetail detail,
            CancellationToken ct)
        {
            var nativeHelper = new NativeKugouClient(_http, _cookie, _dfid, _userId, _token);
            KugouPlaylistDetail fallback = await nativeHelper.GetPlaylistDetailAsync(specialId, ct);
            if (fallback != null && fallback.Status == KugouApiStatus.Ok)
            {
                return fallback;
            }

            return Fail(detail, fallback != null ? fallback.Status : KugouApiStatus.NoSource,
                fallback != null ? fallback.Message : "数字歌单拉取失败");
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

            await EnsureDfidAsync(ct);

            string sizeText = pageSize < 1 ? "30" : pageSize.ToString();
            ApiRaw raw = await GetJsonAsync(
                "/search",
                BuildArgs(
                    "keywords", keyword,
                    "page", page < 1 ? "1" : page.ToString(),
                    "pagesize", sizeText,
                    "limit", sizeText,
                    "type", "song"),
                ct);

            if (raw == null || raw.Json == null)
            {
                result.Status = KugouApiStatus.NetworkError;
                result.Message = "搜索失败: " + (raw != null ? raw.Error : "网关无响应");
                return result;
            }

            JToken data = KugouJson.Pick(raw.Json, "data");
            JArray songs = KugouJson.Arr(data, "lists", "info", "songs", "list");
            result.Total = KugouJson.Int(data, 0, "total");

            if (songs != null)
            {
                for (int i = 0; i < songs.Count; i++)
                {
                    KugouTrack track = ParseGatewayTrack(songs[i]);
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

            await EnsureDfidAsync(ct);

            string sizeText = pageSize < 1 ? "20" : pageSize.ToString();
            ApiRaw raw = await GetJsonAsync(
                "/search",
                BuildArgs(
                    "keywords", keyword,
                    "page", page < 1 ? "1" : page.ToString(),
                    "pagesize", sizeText,
                    "limit", sizeText,
                    "type", "special"),
                ct);

            if (raw == null || raw.Json == null)
            {
                result.Status = KugouApiStatus.NetworkError;
                result.Message = "歌单搜索失败: " + (raw != null ? raw.Error : "网关无响应");
                return result;
            }

            JToken data = KugouJson.Pick(raw.Json, "data");
            JArray list = KugouJson.Arr(data, "lists", "info", "list");
            result.Total = KugouJson.Int(data, 0, "total");

            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    JObject item = list[i] as JObject;
                    if (item == null)
                    {
                        continue;
                    }

                    string specialId = ExtractPlaylistId(item);
                    if (string.IsNullOrEmpty(specialId))
                    {
                        continue;
                    }

                    result.Playlists.Add(new KugouPlaylistSummary
                    {
                        SpecialId = specialId,
                        Name = KugouJson.Str(item, "specialname", "listname", "name", "title") ?? "未命名歌单",
                        Author = KugouJson.Str(item, "nickname", "list_create_username", "author", "username") ?? string.Empty,
                        SongCount = KugouJson.Int(item, 0, "song_count", "songcount", "count", "total"),
                        CoverUrl = KugouJson.Str(item, "img", "cover", "imgurl", "pic") ?? string.Empty,
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

            await EnsureDfidAsync(ct);

            string qualityTag = QualityToGateway(quality);
            ApiRaw raw = await FetchSongUrlRawAsync(track, qualityTag, ct);

            KugouPlayUrlResult parsed;
            if (TryBuildPlayUrlResult(raw, track, out parsed))
            {
                if (!parsed.IsTrial || qualityTag == "128")
                {
                    return parsed;
                }
            }

            // 游客 / 非 VIP 对 320 或 flac 常返回无权空地址或试听片段，自动降级尝试 128k 完整流
            if (qualityTag != "128")
            {
                ApiRaw lowRaw = await FetchSongUrlRawAsync(track, "128", ct);
                KugouPlayUrlResult lowParsed;
                if (TryBuildPlayUrlResult(lowRaw, track, out lowParsed) && (!lowParsed.IsTrial || parsed == null))
                {
                    return lowParsed;
                }
            }

            if (parsed != null)
            {
                return parsed;
            }

            if (raw == null || raw.Json == null)
            {
                string bodyErr = raw != null ? ExtractGatewayError(raw) : "网关无响应";
                return KugouPlayUrlResult.Fail(KugouApiStatus.NetworkError, "直链接口失败: " + bodyErr);
            }

            string msg = KugouJson.Str(raw.Json, "error", "error_msg", "errmsg");
            return KugouPlayUrlResult.Fail(
                KugouApiStatus.NoSource,
                string.IsNullOrEmpty(msg) ? "播放地址为空（可能需要 VIP 或已下架）" : msg);
        }

        private async UniTask<ApiRaw> FetchSongUrlRawAsync(
            KugouTrack track,
            string qualityTag,
            CancellationToken ct)
        {
            ApiRaw raw = await GetJsonAsync("/song/url", BuildSongUrlArgs(track, qualityTag), ct, noCache: true);

            // errcode 20028（本次请求需要验证）通常是 dfid 失效，重新注册设备后再试一次
            if (raw != null && NeedsDeviceVerify(raw))
            {
                Plugin.Log.LogWarning("[External] /song/url 触发设备验证 (20028)，正在刷新 dfid 重试");
                await EnsureDfidAsync(ct, forceRefresh: true);
                raw = await GetJsonAsync("/song/url", BuildSongUrlArgs(track, qualityTag), ct, noCache: true);
            }

            return raw;
        }

        private static bool TryBuildPlayUrlResult(
            ApiRaw raw,
            KugouTrack track,
            out KugouPlayUrlResult result)
        {
            result = null;
            if (raw == null || raw.Json == null)
            {
                return false;
            }

            JObject node = ResolvePlayUrlNode(raw.Json);
            string url = ExtractFirstUrl(node) ?? ExtractFirstUrl(raw.Json);
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            string ext = node != null
                ? (KugouJson.Str(node, "extName", "extname", "ext") ?? InferExtFromUrl(url))
                : InferExtFromUrl(url);

            int timeLen = node != null
                ? KugouJson.Int(node, 0, "std_hash_time", "timeLength", "timelength", "duration")
                : 0;

            if (timeLen > 3600)
            {
                timeLen = timeLen / 1000;
            }

            result = new KugouPlayUrlResult
            {
                Status = KugouApiStatus.Ok,
                Url = url,
                Bitrate = node != null ? KugouJson.Int(node, 0, "bitrate", "bitRate", "br") : 0,
                TimeLengthSeconds = timeLen,
                ExtName = string.IsNullOrEmpty(ext) ? "mp3" : ext,
            };

            result.IsTrial = NativeKugouClient.IsTrialClip(result.TimeLengthSeconds, track.DurationSeconds);
            return true;
        }

        private static Dictionary<string, string> BuildSongUrlArgs(KugouTrack track, string qualityTag)
        {
            var songArgs = new Dictionary<string, string>
            {
                { "id", track.Hash },
                { "hash", track.Hash },
                { "quality", qualityTag },
            };

            if (!string.IsNullOrEmpty(track.AlbumId))
            {
                songArgs["album_id"] = track.AlbumId;
            }

            if (!string.IsNullOrEmpty(track.AlbumAudioId))
            {
                songArgs["album_audio_id"] = track.AlbumAudioId;
            }

            return songArgs;
        }

        // ---------------- 账号 ----------------

        public async UniTask<KugouUserPlaylistResult> GetUserPlaylistsAsync(CancellationToken ct)
        {
            var result = new KugouUserPlaylistResult();
            await EnsureDfidAsync(ct);

            string effectiveToken = !string.IsNullOrEmpty(_token) ? _token : ExtractCookieValue(_cookie, "token");
            if (string.IsNullOrEmpty(effectiveToken))
            {
                result.Status = KugouApiStatus.AuthRequired;
                result.Message = "未检测到有效 token，请重新扫码并在手机点击「确认登录」";
                return result;
            }

            ApiRaw raw = await GetJsonAsync(
                "/user/playlist",
                BuildArgs("page", "1", "pagesize", "100", "limit", "100"),
                ct,
                noCache: true);

            if (raw == null || raw.Json == null)
            {
                result.Status = KugouApiStatus.NetworkError;
                result.Message = "个人歌单获取失败: " + (raw != null ? ExtractGatewayError(raw) : "网关无响应");
                Plugin.Log.LogWarning("[External] /user/playlist 请求失败: " + result.Message);
                return result;
            }

            int apiStatus = KugouJson.Int(raw.Json, 1, "status");
            int errCode = KugouJson.Int(raw.Json, 0, "error_code", "errcode");
            if (apiStatus == 0 || errCode != 0)
            {
                string errMsg = ExtractGatewayError(raw);
                result.Status = errCode == 20010 ? KugouApiStatus.AuthRequired : KugouApiStatus.NetworkError;
                result.Message = "网关返回错误 (" + errCode + "): " + errMsg;
                Plugin.Log.LogWarning("[External] /user/playlist 失败: " + raw.Json.ToString(Newtonsoft.Json.Formatting.None));
                return result;
            }

            JToken data = KugouJson.Pick(raw.Json, "data");
            JArray list = KugouJson.Arr(data, "info", "list", "lists");

            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    JObject item = list[i] as JObject;
                    if (item == null)
                    {
                        continue;
                    }

                    string specialId = ExtractPlaylistId(item);
                    if (string.IsNullOrEmpty(specialId))
                    {
                        string listId = KugouJson.Str(item, "listid", "list_create_listid");
                        if (!string.IsNullOrEmpty(listId) && listId != "0")
                        {
                            specialId = "listid:" + listId;
                        }
                    }

                    if (string.IsNullOrEmpty(specialId))
                    {
                        continue;
                    }

                    result.Playlists.Add(new KugouPlaylistSummary
                    {
                        SpecialId = specialId,
                        Name = KugouJson.Str(item, "name", "listname", "specialname", "title") ?? "未命名歌单",
                        Author = KugouJson.Str(item, "list_create_username", "nickname", "author", "username") ?? string.Empty,
                        SongCount = KugouJson.Int(item, 0, "count", "songcount", "song_count", "total"),
                        CoverUrl = KugouJson.Str(item, "pic", "imgurl", "cover") ?? string.Empty,
                    });
                }
            }

            if (result.Playlists.Count == 0)
            {
                result.Message = "账号下未获取到歌单（请确认与酷狗概念版/手机版平台一致，或先在酷狗 App 创建歌单）";
                Plugin.Log.LogWarning("[External] /user/playlist 返回空列表: " + raw.Json.ToString(Newtonsoft.Json.Formatting.None));
            }

            result.Status = KugouApiStatus.Ok;
            return result;
        }

        public async UniTask<KugouUserDetail> GetUserDetailAsync(CancellationToken ct)
        {
            var detail = new KugouUserDetail();
            await EnsureDfidAsync(ct);

            ApiRaw raw = await GetJsonAsync("/user/detail", null, ct, noCache: true);
            if (raw == null || raw.Json == null)
            {
                detail.Status = KugouApiStatus.NetworkError;
                detail.Message = "账号详情获取失败: " + (raw != null ? ExtractGatewayError(raw) : "网关无响应");
                return detail;
            }

            JToken data = KugouJson.Pick(raw.Json, "data", "data.info");
            if (data is JObject obj)
            {
                detail.UserId = KugouJson.Str(obj, "userid", "userId", "uid") ?? _userId;
                detail.Nickname = KugouJson.Str(obj, "nickname", "name", "username") ?? string.Empty;
                detail.IsVip = KugouJson.Int(obj, 0, "vip_type", "viptype", "is_vip") > 0 ||
                               KugouJson.Bool(obj, false, "isvip", "vip");
            }
            else
            {
                detail.UserId = _userId;
            }

            detail.Status = KugouApiStatus.Ok;
            return detail;
        }

        public async UniTask<KugouQrLoginSession> CreateQrKeyAsync(CancellationToken ct)
        {
            var session = new KugouQrLoginSession();
            await EnsureDfidAsync(ct);

            ApiRaw keyRaw = await GetJsonAsync("/login/qr/key", null, ct, noCache: true);
            if (keyRaw == null || keyRaw.Json == null)
            {
                session.Status = KugouApiStatus.NetworkError;
                session.Message = "二维码 key 获取失败: " + (keyRaw != null ? ExtractGatewayError(keyRaw) : "网关无响应");
                return session;
            }

            JToken keyData = KugouJson.Pick(keyRaw.Json, "data", "data.qrcode");
            JObject keyObject = keyData as JObject;
            string key = keyObject != null
                ? KugouJson.Str(keyObject, "qrcode", "key")
                : (keyData != null ? keyData.ToString() : null);

            if (string.IsNullOrEmpty(key))
            {
                session.Status = KugouApiStatus.ParseError;
                session.Message = "二维码 key 解析失败";
                return session;
            }

            session.Key = key;

            string fallbackKeyImg = keyObject != null
                ? NormalizeBase64(KugouJson.Str(keyObject, "qrcode_img", "qrimg", "base64", "img") ?? string.Empty)
                : string.Empty;

            ApiRaw createRaw = await GetJsonAsync(
                "/login/qr/create",
                BuildArgs("key", key, "qrimg", "true"),
                ct,
                noCache: true);

            if (createRaw != null && createRaw.Json != null)
            {
                JToken createData = KugouJson.Pick(createRaw.Json, "data", "data.qrcode");
                if (createData is JObject createObject)
                {
                    session.QrImageBase64 = NormalizeBase64(
                        KugouJson.Str(createObject, "base64", "qrcode_img", "qrimg", "img") ?? string.Empty);
                    session.QrImageUrl = KugouJson.Str(createObject, "url", "qrcode") ?? string.Empty;

                    if (string.IsNullOrEmpty(session.QrImageBase64))
                    {
                        string rawQr = KugouJson.Str(createObject, "qrcode");
                        if (!string.IsNullOrEmpty(rawQr) && rawQr.Length > 200)
                        {
                            session.QrImageBase64 = NormalizeBase64(rawQr);
                        }
                    }
                }
                else if (createData != null)
                {
                    session.QrImageBase64 = NormalizeBase64(createData.ToString());
                }
            }

            if (string.IsNullOrEmpty(session.QrImageBase64))
            {
                session.QrImageBase64 = fallbackKeyImg;
            }

            if (string.IsNullOrEmpty(session.QrImageBase64))
            {
                string rawText = createRaw != null && createRaw.Response != null
                    ? createRaw.Response.Text
                    : null;
                if (!string.IsNullOrEmpty(rawText) && rawText.Length > 400)
                {
                    session.QrImageBase64 = NormalizeBase64(rawText);
                }
            }

            session.Status = KugouApiStatus.Ok;
            return session;
        }

        public async UniTask<KugouQrCheckResult> CheckQrStatusAsync(string key, CancellationToken ct)
        {
            var result = new KugouQrCheckResult { Key = key };

            if (string.IsNullOrEmpty(key))
            {
                result.State = KugouQrState.Failed;
                result.Message = "二维码 key 为空";
                return result;
            }

            // 必须带 timestamp 绕过 server.js 的 2 分钟 apicache，否则轮询状态永远卡在 1
            ApiRaw raw = await GetJsonAsync("/login/qr/check", BuildArgs("key", key), ct, noCache: true);
            if (raw == null || raw.Json == null)
            {
                result.State = KugouQrState.Waiting;
                result.Message = raw != null ? ExtractGatewayError(raw) : "网关无响应";
                return result;
            }

            JToken data = KugouJson.Pick(raw.Json, "data");
            JObject dataObject = data as JObject;

            // 扫码状态码在 data.status（0=过期 1=等待 2=待确认 4=成功），顶层 status=1 仅代表 HTTP 接口成功
            int scanStatus = dataObject != null
                ? KugouJson.Int(dataObject, -1, "status")
                : -1;

            if (scanStatus < 0)
            {
                scanStatus = KugouJson.Int(raw.Json, 0, "status");
            }

            string token = dataObject != null
                ? (KugouJson.Str(dataObject, "token", "token_v2", "access_token") ?? string.Empty)
                : string.Empty;

            string userId = dataObject != null
                ? (KugouJson.Str(dataObject, "userid", "userId", "uid") ?? string.Empty)
                : string.Empty;

            string nickname = dataObject != null
                ? (KugouJson.Str(dataObject, "nickname", "name", "username") ?? string.Empty)
                : string.Empty;

            string cookie = MergeCredentialCookie(
                raw.Response != null ? raw.Response.GetCookie() : string.Empty,
                token,
                userId,
                _dfid);

            result.Token = token;
            result.UserId = userId;
            result.Nickname = nickname;
            result.Cookie = cookie;

            // 只有 status == 4 或已下发非空 token 才视为授权完成；status == 2 时可能已带 userid 但尚未返回 token
            if (scanStatus == 4 || !string.IsNullOrEmpty(token))
            {
                Plugin.Log.LogInfo("[External] 扫码授权完成: status=" + scanStatus + ", userid=" + userId +
                                   ", hasToken=" + (!string.IsNullOrEmpty(token)));
                result.State = KugouQrState.Confirmed;
                return result;
            }

            if (scanStatus == 0 || scanStatus == 800)
            {
                result.State = KugouQrState.Expired;
                result.Message = "二维码已过期，请重新获取";
            }
            else if (scanStatus == 2 || scanStatus == 801)
            {
                result.State = KugouQrState.Scanned;
                result.Message = "已扫码" + (string.IsNullOrEmpty(nickname) ? string.Empty : "（" + nickname + "）") +
                                 "，请在手机上点「确认登录」";
            }
            else
            {
                result.State = KugouQrState.Waiting;
                result.Message = "等待扫码（status=" + scanStatus + "）";
            }

            return result;
        }

        public async UniTask<KugouApiStatus> ClaimYouthVipAsync(CancellationToken ct)
        {
            await EnsureDfidAsync(ct);

            string today = DateTime.Now.ToString("yyyy-MM-dd");
            ApiRaw dayRaw = await GetJsonAsync(
                "/youth/day/vip",
                BuildArgs("receive_day", today),
                ct,
                noCache: true);

            if (dayRaw != null && dayRaw.Json != null)
            {
                int dayStatus = KugouJson.Int(dayRaw.Json, 0, "status", "code");
                int errCode = KugouJson.Int(dayRaw.Json, -1, "error_code", "errcode");
                Plugin.Log.LogInfo("[External] /youth/day/vip (" + today + ") 响应: " + dayRaw.Json.ToString(Newtonsoft.Json.Formatting.None));

                if (dayStatus == 1 || dayStatus == 200 || errCode == 0)
                {
                    await GetJsonAsync("/youth/day/vip/upgrade", null, ct, noCache: true);
                    return KugouApiStatus.Ok;
                }
            }

            // 兼容旧版网关回落 /youth/vip
            ApiRaw legacyRaw = await GetJsonAsync("/youth/vip", null, ct, noCache: true);
            if (legacyRaw == null || legacyRaw.Json == null)
            {
                return KugouApiStatus.NetworkError;
            }

            Plugin.Log.LogInfo("[External] /youth/vip 响应: " + legacyRaw.Json.ToString(Newtonsoft.Json.Formatting.None));
            int legacyStatus = KugouJson.Int(legacyRaw.Json, 0, "status", "code");
            return legacyStatus == 1 || legacyStatus == 200 ? KugouApiStatus.Ok : KugouApiStatus.NoSource;
        }

        public async UniTask<KugouLyricsResult> GetLyricsAsync(KugouTrack track, CancellationToken ct)
        {
            if (track == null || string.IsNullOrEmpty(track.Name))
            {
                return new KugouLyricsResult { Status = KugouApiStatus.InvalidArgument, Message = "曲目为空" };
            }

            await EnsureDfidAsync(ct);

            string keyword = (string.IsNullOrEmpty(track.Singer) || track.Singer == "未知歌手")
                ? track.Name
                : (track.Singer + " - " + track.Name);
            int durationMs = track.DurationSeconds > 0 ? track.DurationSeconds * 1000 : 0;

            var args = new Dictionary<string, string>
            {
                { "keywords", keyword },
                { "hash", track.Hash ?? string.Empty },
                { "duration", durationMs.ToString() },
                { "man", "no" },
            };

            if (!string.IsNullOrEmpty(track.AlbumAudioId))
            {
                args["album_audio_id"] = track.AlbumAudioId;
            }

            ApiRaw searchRaw = await GetJsonAsync("/search/lyric", args, ct);
            if (searchRaw == null || searchRaw.Json == null)
            {
                return new KugouLyricsResult
                {
                    Status = KugouApiStatus.NetworkError,
                    Message = searchRaw != null ? searchRaw.Error : "歌词搜索无响应",
                };
            }

            JArray candidates = KugouJson.Arr(searchRaw.Json, "candidates");
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

            ApiRaw lyricRaw = await GetJsonAsync(
                "/lyric",
                BuildArgs("id", id, "accesskey", accesskey, "fmt", "lrc", "decode", "true"),
                ct);

            if (lyricRaw == null || lyricRaw.Json == null)
            {
                return new KugouLyricsResult
                {
                    Status = KugouApiStatus.NetworkError,
                    Message = lyricRaw != null ? lyricRaw.Error : "歌词获取无响应",
                };
            }

            string decoded = KugouJson.Str(lyricRaw.Json, "decodeContent");
            string encoded = KugouJson.Str(lyricRaw.Json, "content");
            return LrcParser.BuildResult(decoded, encoded);
        }

        public async UniTask<string> ResolveTrackCoverUrlAsync(KugouTrack track, CancellationToken ct)
        {
            if (track == null || string.IsNullOrEmpty(track.Hash))
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(track.CoverUrl))
            {
                return track.CoverUrl;
            }

            try
            {
                await EnsureDfidAsync(ct);

                var args = new List<string> { "hash", track.Hash, "count", "1" };
                if (!string.IsNullOrEmpty(track.AlbumId))
                {
                    args.Add("album_id");
                    args.Add(track.AlbumId);
                }

                if (!string.IsNullOrEmpty(track.AlbumAudioId))
                {
                    args.Add("album_audio_id");
                    args.Add(track.AlbumAudioId);
                }

                ApiRaw raw = await GetJsonAsync("/images", BuildArgs(args.ToArray()), ct);
                if (raw != null && raw.Json != null)
                {
                    string found = KugouJson.ExtractCoverUrl(raw.Json, true);
                    if (!string.IsNullOrEmpty(found))
                    {
                        return found;
                    }
                }
            }
            catch
            {
                // 走原生兜底接口
            }

            return await NativeKugouClient.FetchTrackCoverFallbackAsync(_http, track, ct);
        }

        // ---------------- 内部 ----------------

        private async UniTask EnsureDfidAsync(CancellationToken ct, bool forceRefresh = false)
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_dfid))
            {
                return;
            }

            try
            {
                ApiRaw raw = await GetJsonAsync("/register/dev", null, ct, noCache: true);
                if (raw == null || raw.Json == null)
                {
                    return;
                }

                string dfid = KugouJson.Str(KugouJson.Pick(raw.Json, "data"), "dfid") ??
                              KugouJson.Str(raw.Json, "dfid");

                if (string.IsNullOrEmpty(dfid))
                {
                    return;
                }

                _dfid = dfid.Trim();
                Plugin.Log.LogInfo("[External] 设备 dfid 已注册: " + _dfid);

                if (Plugin.Dfid != null && !string.Equals(Plugin.Dfid.Value, _dfid, StringComparison.Ordinal))
                {
                    await UniTask.SwitchToMainThread(CancellationToken.None);
                    Plugin.Dfid.Value = _dfid;
                    Plugin.SaveConfig();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[External] /register/dev 异常: " + ex.Message);
            }
        }

        private async UniTask<KugouPlaylistDetail> ResolveShareUrlPlaylistAsync(
            string url,
            KugouPlaylistDetail detail,
            CancellationToken ct)
        {
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", MobileWebUserAgent },
                { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
            };

            HttpResponseData response = await _http.GetAsync(url, headers, ct);
            if (response == null)
            {
                return null;
            }

            string resolvedId = ExtractCollectionId(response.FinalUrl) ??
                                NativeKugouClient.ExtractSpecialId(response.FinalUrl) ??
                                ExtractCollectionId(response.Text) ??
                                NativeKugouClient.ExtractSpecialId(response.Text);

            if (!string.IsNullOrEmpty(resolvedId) && resolvedId != "0")
            {
                Plugin.Log.LogInfo("[External] 分享链接解析为 id=" + resolvedId);
                return await GetPlaylistDetailAsync(resolvedId, ct);
            }

            JObject outputJson = ExtractAssignedJsonObject(response.Text, "window.$output");
            if (outputJson == null)
            {
                return null;
            }

            JObject listInfo = KugouJson.Pick(outputJson, "info.listinfo", "listinfo") as JObject;
            string gid = KugouJson.Str(outputJson, "global_collection_id", "src_gid") ??
                         KugouJson.Str(listInfo, "global_collection_id", "list_create_gid");
            if (!string.IsNullOrEmpty(gid) && gid != "0")
            {
                Plugin.Log.LogInfo("[External] H5 页面解析为 gid=" + gid);
                return await GetPlaylistDetailAsync(gid, ct);
            }

            string sid = KugouJson.Str(listInfo, "specialid");
            if (!string.IsNullOrEmpty(sid) && sid != "0")
            {
                Plugin.Log.LogInfo("[External] H5 页面解析为 specialid=" + sid);
                return await GetPlaylistDetailAsync(sid, ct);
            }

            ApplyPlaylistSummary(detail.Summary, listInfo);
            if (string.IsNullOrEmpty(detail.Summary.Name))
            {
                detail.Summary.Name = "酷狗分享歌单";
            }

            // gcid_ 链接若属于当前已登录账号，自动从 /user/playlist 匹配完整列表 ID（否则 H5 页仅下发前 10 首）
            string effectiveToken = !string.IsNullOrEmpty(_token) ? _token : ExtractCookieValue(_cookie, "token");
            if (!string.IsNullOrEmpty(effectiveToken) && listInfo != null)
            {
                string ownerId = KugouJson.Str(listInfo, "list_create_userid");
                int isDef = KugouJson.Int(listInfo, 0, "is_def");
                if (string.Equals(ownerId, _userId, StringComparison.Ordinal) || !string.IsNullOrEmpty(detail.Summary.Name))
                {
                    KugouUserPlaylistResult mine = await GetUserPlaylistsAsync(ct);
                    if (mine != null && mine.Status == KugouApiStatus.Ok)
                    {
                        for (int i = 0; i < mine.Playlists.Count; i++)
                        {
                            KugouPlaylistSummary item = mine.Playlists[i];
                            bool nameMatch = string.Equals(item.Name, detail.Summary.Name, StringComparison.OrdinalIgnoreCase);
                            bool likeMatch = isDef == 2 && item.Name != null && item.Name.IndexOf("我喜欢", StringComparison.Ordinal) >= 0;
                            if (nameMatch || likeMatch)
                            {
                                Plugin.Log.LogInfo("[External] gcid_ 分享页已自动匹配账号歌单: " + item.Name + " (" + item.SpecialId + ")");
                                return await GetPlaylistDetailAsync(item.SpecialId, ct);
                            }
                        }
                    }
                }
            }

            JArray embeddedSongs = KugouJson.Arr(KugouJson.Pick(outputJson, "info"), "songs", "list") ??
                                   KugouJson.Arr(outputJson, "songs", "list");
            if (embeddedSongs != null && embeddedSongs.Count > 0)
            {
                for (int i = 0; i < embeddedSongs.Count; i++)
                {
                    KugouTrack track = ParseGatewayTrack(embeddedSongs[i]);
                    if (track != null && !string.IsNullOrEmpty(track.Hash))
                    {
                        detail.Tracks.Add(track);
                    }
                }

                if (detail.Tracks.Count > 0)
                {
                    string gcidTag = KugouJson.Str(outputJson, "encode_src_gid", "encode_gic") ?? "gcid";
                    detail.Summary.SpecialId = gcidTag;
                    int declaredTotal = detail.Summary.SongCount;
                    detail.Summary.SongCount = detail.Tracks.Count;
                    detail.Status = KugouApiStatus.Ok;
                    Plugin.Log.LogWarning("[External] gcid_ 分享页服务端未公开全量 ID，已导入页面内嵌 " +
                                          detail.Tracks.Count + " 首" +
                                          (declaredTotal > detail.Tracks.Count ? "（原歌单共 " + declaredTotal + " 首；个人歌单建议登录后点「同步账号个人歌单」）" : string.Empty));
                    return detail;
                }
            }

            return null;
        }

        private static JObject ExtractAssignedJsonObject(string html, string marker)
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(marker))
            {
                return null;
            }

            int pos = html.IndexOf(marker, StringComparison.Ordinal);
            if (pos < 0)
            {
                return null;
            }

            int braceStart = html.IndexOf('{', pos + marker.Length);
            if (braceStart < 0)
            {
                return null;
            }

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = braceStart; i < html.Length; i++)
            {
                char ch = html[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (inString)
                {
                    if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        try
                        {
                            return JObject.Parse(html.Substring(braceStart, i - braceStart + 1));
                        }
                        catch
                        {
                            return null;
                        }
                    }
                }
            }

            return null;
        }

        private static KugouTrack ParseGatewayTrack(JToken item)
        {
            JObject obj = item as JObject;
            if (obj == null)
            {
                return null;
            }

            string hash = KugouJson.Str(obj, "hash", "FileHash", "filehash", "file_hash");
            string hash320 = KugouJson.Str(obj, "320hash", "hash320", "HQFileHash", "hqhash", "sqhash") ?? string.Empty;

            JArray relateGoods = KugouJson.Arr(obj, "relate_goods");
            if (relateGoods != null)
            {
                for (int i = 0; i < relateGoods.Count; i++)
                {
                    JObject good = relateGoods[i] as JObject;
                    if (good == null)
                    {
                        continue;
                    }

                    string gHash = KugouJson.Str(good, "hash");
                    int bitrate = KugouJson.Int(good, 0, "bitrate");
                    if (string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(gHash))
                    {
                        hash = gHash;
                    }

                    if (string.IsNullOrEmpty(hash320) && bitrate >= 320 && bitrate < 500 && !string.IsNullOrEmpty(gHash))
                    {
                        hash320 = gHash;
                    }
                }
            }

            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            string singer = KugouJson.Str(obj, "singername", "SingerName", "singer", "artist", "author");
            if (string.IsNullOrEmpty(singer))
            {
                singer = ExtractSingerNames(KugouJson.Arr(obj, "singerinfo", "ar"));
            }

            int duration = KugouJson.Int(obj, 0, "duration", "timelen", "timelength", "timeLength", "interval");
            // 阈值 3600：超过 1 小时的时长按毫秒处理，避免把 Lo-Fi 短曲误判
            if (duration > 3600)
            {
                duration = duration / 1000;
            }

            string albumId = KugouJson.Str(obj, "album_id", "albumid", "AlbumID") ??
                             KugouJson.Str(KugouJson.Token(obj, "albuminfo"), "id") ??
                             string.Empty;

            string albumName = KugouJson.Str(obj, "album_name", "albumname", "AlbumName") ??
                               KugouJson.Str(KugouJson.Token(obj, "albuminfo"), "name") ??
                               string.Empty;

            int payType = KugouJson.Int(obj, 0, "pay_type", "PayType", "paytype");
            if (payType <= 0)
            {
                JArray downloads = KugouJson.Arr(obj, "download");
                if (downloads != null && downloads.Count > 0)
                {
                    payType = KugouJson.Int(downloads[0], 0, "pay_type", "paytype");
                }
            }

            if (payType <= 0 && KugouJson.Int(obj, 0, "privilege", "Privilege") >= 8)
            {
                payType = 3;
            }

            string rawName = KugouJson.Str(obj, "songname", "SongName", "name", "title", "FileName", "filename") ?? "未知歌曲";

            int separator = rawName.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0)
            {
                string left = rawName.Substring(0, separator).Trim();
                string right = rawName.Substring(separator + 3).Trim();
                if (string.IsNullOrEmpty(singer))
                {
                    singer = left;
                    rawName = string.IsNullOrEmpty(right) ? rawName : right;
                }
                else if (string.Equals(left, singer, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(right))
                {
                    rawName = right;
                }
            }

            return new KugouTrack
            {
                Hash = hash,
                Hash320 = hash320,
                AlbumId = albumId,
                AlbumAudioId = KugouJson.Str(obj, "MixSongID", "mixsongid", "album_audio_id", "add_mixsongid") ?? string.Empty,
                AlbumName = albumName,
                Name = string.IsNullOrEmpty(rawName) ? "未知歌曲" : rawName,
                Singer = string.IsNullOrEmpty(singer) ? "未知歌手" : singer,
                DurationSeconds = duration,
                PayType = payType,
                Price = KugouJson.Int(obj, 0, "price", "Price"),
                ExtName = KugouJson.Str(obj, "extname", "extName", "ExtName", "ext") ?? "mp3",
                CoverUrl = KugouJson.ExtractCoverUrl(obj) ?? string.Empty,
            };
        }

        private async UniTask<ApiRaw> GetJsonAsync(
            string path,
            IDictionary<string, string> args,
            CancellationToken ct,
            bool noCache = false)
        {
            string url = BuildUrl(path, args, noCache);
            if (Plugin.Gateway != null)
            {
                await Plugin.Gateway.WaitUntilReadyAsync(url, ct);
            }

            HttpResponseData response = await _http.GetAsync(url, BuildHeaders(), ct);

            if (response == null)
            {
                return new ApiRaw { Error = "empty response" };
            }

            JObject parsed = null;
            if (!string.IsNullOrEmpty(response.Text))
            {
                try
                {
                    parsed = JObject.Parse(response.Text);
                }
                catch (Exception ex)
                {
                    if (response.Ok)
                    {
                        return new ApiRaw { Response = response, Error = "JSON 解析失败: " + ex.Message };
                    }
                }
            }

            if (!response.Ok)
            {
                return new ApiRaw { Response = response, Json = parsed, Error = response.Error };
            }

            return parsed != null
                ? new ApiRaw { Response = response, Json = parsed }
                : new ApiRaw { Response = response, Error = "响应体为空" };
        }

        private string BuildUrl(string path, IDictionary<string, string> args, bool noCache)
        {
            var builder = new StringBuilder(_baseUrl);
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                builder.Append('/');
            }

            builder.Append(path);

            var query = new List<string>();
            if (!string.IsNullOrEmpty(_token))
            {
                query.Add("token=" + Uri.EscapeDataString(_token));
            }

            if (!string.IsNullOrEmpty(_userId))
            {
                query.Add("userid=" + Uri.EscapeDataString(_userId));
            }

            if (args != null)
            {
                foreach (KeyValuePair<string, string> pair in args)
                {
                    query.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? string.Empty));
                }
            }

            if (noCache && (args == null || !args.ContainsKey("timestamp")))
            {
                query.Add("timestamp=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            if (query.Count > 0)
            {
                builder.Append(builder.ToString().Contains("?") ? "&" : "?");
                builder.Append(string.Join("&", query.ToArray()));
            }

            return builder.ToString();
        }

        private Dictionary<string, string> BuildHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36" },
                { "Accept", "application/json, text/plain, */*" },
            };

            string cookieHeader = MergeCredentialCookie(_cookie, _token, _userId, _dfid);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                headers["Cookie"] = cookieHeader;
            }

            return headers;
        }

        private static string MergeCredentialCookie(string existingCookie, string token, string userId, string dfid)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(existingCookie))
            {
                string[] segments = existingCookie.Split(';');
                for (int i = 0; i < segments.Length; i++)
                {
                    string seg = segments[i].Trim();
                    int eq = seg.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string k = seg.Substring(0, eq).Trim();
                    string v = seg.Substring(eq + 1).Trim();
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    {
                        map[k] = v;
                    }
                }
            }

            if (!string.IsNullOrEmpty(token))
            {
                map["token"] = token;
            }

            if (!string.IsNullOrEmpty(userId))
            {
                map["userid"] = userId;
            }

            if (!string.IsNullOrEmpty(dfid) && !map.ContainsKey("dfid"))
            {
                map["dfid"] = dfid;
            }

            if (map.Count == 0)
            {
                return string.Empty;
            }

            var pairs = new List<string>(map.Count);
            foreach (KeyValuePair<string, string> pair in map)
            {
                pairs.Add(pair.Key + "=" + pair.Value);
            }

            return string.Join("; ", pairs.ToArray());
        }

        private static string ExtractCookieValue(string cookie, string key)
        {
            if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            string[] segments = cookie.Split(';');
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i].Trim();
                int eq = seg.IndexOf('=');
                if (eq > 0 && string.Equals(seg.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return seg.Substring(eq + 1).Trim();
                }
            }

            return string.Empty;
        }

        private static string ExtractCollectionId(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            Match match = CollectionIdRegex.Match(text);
            return match.Success ? match.Value : null;
        }

        private static string ExtractPlaylistId(JObject item)
        {
            if (item == null)
            {
                return null;
            }

            string gid = KugouJson.Str(item, "global_collection_id", "gid", "list_create_gid");
            if (!string.IsNullOrEmpty(gid) && gid != "0")
            {
                return gid;
            }

            string special = KugouJson.Str(item, "specialid", "id");
            if (!string.IsNullOrEmpty(special) && special != "0")
            {
                return special;
            }

            return null;
        }

        private static JObject ExtractFirstObject(JToken token)
        {
            if (token is JObject obj)
            {
                return obj;
            }

            if (token is JArray array && array.Count > 0)
            {
                return array[0] as JObject;
            }

            return null;
        }

        private static void ApplyPlaylistSummary(KugouPlaylistSummary summary, JObject obj)
        {
            if (summary == null || obj == null)
            {
                return;
            }

            string name = KugouJson.Str(obj, "name", "listname", "specialname", "title");
            if (!string.IsNullOrEmpty(name))
            {
                summary.Name = name;
            }

            string author = KugouJson.Str(obj, "list_create_username", "nickname", "author", "username");
            if (!string.IsNullOrEmpty(author))
            {
                summary.Author = author;
            }

            int count = KugouJson.Int(obj, 0, "count", "total", "song_count", "songcount");
            if (count > 0)
            {
                summary.SongCount = count;
            }

            string cover = KugouJson.Str(obj, "pic", "cover", "imgurl", "img");
            if (!string.IsNullOrEmpty(cover))
            {
                summary.CoverUrl = cover;
            }
        }

        private static string ExtractSingerNames(JArray array)
        {
            if (array == null || array.Count == 0)
            {
                return string.Empty;
            }

            var names = new List<string>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                string name = KugouJson.Str(array[i] as JObject, "name", "singername");
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? string.Empty : string.Join("、", names.ToArray());
        }

        private static JObject ResolvePlayUrlNode(JObject root)
        {
            if (root == null)
            {
                return null;
            }

            if (root["url"] != null || root["backupUrl"] != null || root["play_url"] != null)
            {
                return root;
            }

            JToken data = KugouJson.Pick(root, "data", "data.0");
            return ExtractFirstObject(data) ?? root;
        }

        private static string ExtractFirstUrl(JObject obj)
        {
            if (obj == null)
            {
                return null;
            }

            string[] keys = { "url", "backupUrl", "play_url", "src" };
            for (int i = 0; i < keys.Length; i++)
            {
                JToken token = obj.GetValue(keys[i], StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token is JArray array)
                {
                    for (int j = 0; j < array.Count; j++)
                    {
                        string candidate = array[j] != null ? array[j].ToString() : null;
                        if (!string.IsNullOrEmpty(candidate) && candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                    }

                    continue;
                }

                string single = token.ToString();
                if (!string.IsNullOrEmpty(single) && single.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return single;
                }
            }

            return null;
        }

        private static string InferExtFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return "mp3";
            }

            string clean = url.Split('?')[0].ToLowerInvariant();
            if (clean.EndsWith(".m4a", StringComparison.Ordinal))
            {
                return "m4a";
            }

            if (clean.EndsWith(".flac", StringComparison.Ordinal))
            {
                return "flac";
            }

            if (clean.EndsWith(".ogg", StringComparison.Ordinal))
            {
                return "ogg";
            }

            if (clean.EndsWith(".wav", StringComparison.Ordinal))
            {
                return "wav";
            }

            return "mp3";
        }

        private static bool NeedsDeviceVerify(ApiRaw raw)
        {
            if (raw == null || raw.Json == null)
            {
                return false;
            }

            int errcode = KugouJson.Int(raw.Json, 0, "errcode", "error_code");
            return errcode == 20028;
        }

        private static string ExtractGatewayError(ApiRaw raw)
        {
            if (raw == null)
            {
                return "网关无响应";
            }

            if (raw.Json != null)
            {
                string msg = KugouJson.Str(raw.Json, "error", "error_msg", "errmsg", "msg");
                if (!string.IsNullOrEmpty(msg))
                {
                    return msg;
                }
            }

            return string.IsNullOrEmpty(raw.Error) ? "网关无响应" : raw.Error;
        }

        private static IDictionary<string, string> BuildArgs(params string[] pairs)
        {
            var args = new Dictionary<string, string>();
            if (pairs == null)
            {
                return args;
            }

            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                args[pairs[i]] = pairs[i + 1];
            }

            return args;
        }

        private static string QualityToGateway(KugouQuality quality)
        {
            switch (quality)
            {
                case KugouQuality.Mp3128:
                    return "128";
                case KugouQuality.M4A:
                    return "flac";
                default:
                    return "320";
            }
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            string value = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:3000" : baseUrl.Trim();
            return value.TrimEnd('/');
        }

        private static string NormalizeBase64(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            string value = raw.Trim();
            int comma = value.IndexOf(",", StringComparison.Ordinal);
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            {
                value = value.Substring(comma + 1);
            }

            return value;
        }

        private static bool IsDigits(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static KugouPlaylistDetail Fail(KugouPlaylistDetail detail, KugouApiStatus status, string message)
        {
            detail.Status = status;
            detail.Message = message ?? string.Empty;
            Plugin.Log.LogWarning("[External] " + message);
            return detail;
        }

        private sealed class ApiRaw
        {
            public HttpResponseData Response;
            public JObject Json;
            public string Error = string.Empty;
        }
    }
}