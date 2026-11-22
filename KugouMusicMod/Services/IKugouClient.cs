using System;
using System.Collections.Generic;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>
    /// 酷狗双后端统一契约。
    /// 所有方法都不抛异常：网络/解析/未实现等一律通过 <see cref="KugouApiStatus"/> 表达，
    /// 由调用方决定降级策略。
    /// </summary>
    internal interface IKugouClient : IDisposable
    {
        KugouBackendMode Mode { get; }

        string DisplayName { get; }

        /// <summary>该后端是否支持扫码登录 / 个人歌单 / 领 VIP。</summary>
        bool SupportsAccount { get; }

        UniTask<KugouConnectionTestResult> TestConnectionAsync(CancellationToken ct);

        UniTask<KugouPlaylistDetail> ParsePlaylistInputAsync(string input, CancellationToken ct);

        UniTask<KugouPlaylistDetail> GetPlaylistDetailAsync(string specialId, CancellationToken ct);

        UniTask<KugouSearchResult> SearchTracksAsync(string keyword, int page, int pageSize, CancellationToken ct);

        UniTask<KugouSearchResult> SearchPlaylistsAsync(string keyword, int page, int pageSize, CancellationToken ct);

        UniTask<KugouPlayUrlResult> GetPlayUrlAsync(KugouTrack track, KugouQuality quality, CancellationToken ct);

        UniTask<KugouUserPlaylistResult> GetUserPlaylistsAsync(CancellationToken ct);

        UniTask<KugouUserDetail> GetUserDetailAsync(CancellationToken ct);

        UniTask<KugouQrLoginSession> CreateQrKeyAsync(CancellationToken ct);

        UniTask<KugouQrCheckResult> CheckQrStatusAsync(string key, CancellationToken ct);

        UniTask<KugouApiStatus> ClaimYouthVipAsync(CancellationToken ct);

        UniTask<KugouLyricsResult> GetLyricsAsync(KugouTrack track, CancellationToken ct);

        UniTask<string> ResolveTrackCoverUrlAsync(KugouTrack track, CancellationToken ct);
    }

    /// <summary>
    /// 容错 JSON 读取工具：酷狗与开源网关的字段命名在不同版本间会漂移，
    /// 这里按"候选字段名 + 候选容器"依次探测，读不到就返回默认值而不是抛异常。
    /// </summary>
    internal static class KugouJson
    {
        public static JToken Token(JToken root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            JToken current = root;
            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (current == null)
                {
                    return null;
                }

                JArray indexed = current as JArray;
                if (indexed != null)
                {
                    int index;
                    if (int.TryParse(parts[i], out index) && index >= 0 && index < indexed.Count)
                    {
                        current = indexed[index];
                        continue;
                    }

                    current = indexed.Count > 0 ? indexed[0] : null;
                    if (current == null)
                    {
                        return null;
                    }
                }

                JObject obj = current as JObject;
                if (obj == null)
                {
                    return null;
                }

                current = obj.GetValue(parts[i], StringComparison.OrdinalIgnoreCase);
            }

            return current;
        }

        public static JToken Pick(JToken root, params string[] paths)
        {
            if (root == null || paths == null)
            {
                return null;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                JToken token = Token(root, paths[i]);
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token;
                }
            }

            return null;
        }

        public static string Str(JToken root, params string[] names)
        {
            if (root == null || names == null)
            {
                return null;
            }

            JObject obj = root as JObject;
            if (obj == null)
            {
                return null;
            }

            for (int i = 0; i < names.Length; i++)
            {
                JToken token = obj.GetValue(names[i], StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Array)
                {
                    string joined = JoinArray(token as JArray);
                    if (!string.IsNullOrEmpty(joined))
                    {
                        return joined;
                    }

                    continue;
                }

                string value = token.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return null;
        }

        public static int Int(JToken root, int fallback, params string[] names)
        {
            string raw = Str(root, names);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            int value;
            return int.TryParse(raw, out value) ? value : fallback;
        }

        public static long Long(JToken root, long fallback, params string[] names)
        {
            string raw = Str(root, names);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            long value;
            return long.TryParse(raw, out value) ? value : fallback;
        }

        public static bool Bool(JToken root, bool fallback, params string[] names)
        {
            string raw = Str(root, names);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            raw = raw.Trim().ToLowerInvariant();
            if (raw == "1" || raw == "true" || raw == "yes")
            {
                return true;
            }

            if (raw == "0" || raw == "false" || raw == "no")
            {
                return false;
            }

            return fallback;
        }

        public static JArray Arr(JToken root, params string[] names)
        {
            if (root == null || names == null)
            {
                return null;
            }

            JObject obj = root as JObject;
            if (obj == null)
            {
                return null;
            }

            for (int i = 0; i < names.Length; i++)
            {
                JToken token = obj.GetValue(names[i], StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                JArray array = token as JArray;
                if (array != null)
                {
                    return array;
                }

                // 有些网关把数组包在 data.list 这类结构里
                JObject inner = token as JObject;
                if (inner != null)
                {
                    JArray nested = Arr(inner, "list", "info", "songs", "data", "items");
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        public static JObject Obj(JToken root, params string[] names)
        {
            if (root == null || names == null)
            {
                return null;
            }

            JObject obj = root as JObject;
            if (obj == null)
            {
                return null;
            }

            for (int i = 0; i < names.Length; i++)
            {
                JToken token = obj.GetValue(names[i], StringComparison.OrdinalIgnoreCase);
                JObject inner = token as JObject;
                if (inner != null)
                {
                    return inner;
                }
            }

            return null;
        }

        private static string JoinArray(JArray array)
        {
            if (array == null || array.Count == 0)
            {
                return null;
            }

            var parts = new List<string>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                JToken item = array[i];
                if (item == null || item.Type == JTokenType.Null)
                {
                    continue;
                }

                JObject obj = item as JObject;
                string value = obj != null
                    ? (Str(obj, "name", "singerName", "singername", "title") ?? obj.ToString())
                    : item.ToString();

                if (!string.IsNullOrEmpty(value))
                {
                    parts.Add(value);
                }
            }

            return parts.Count == 0 ? null : string.Join("、", parts);
        }

        /// <summary>从曲目/歌单/图片接口的 JSON 节点提取封面 URL；deepSearch=true 时还会深入子数组（用于 /images 接口）。</summary>
        public static string ExtractCoverUrl(JToken token, bool deepSearch = false, int depth = 0)
        {
            if (token == null || depth > 4)
            {
                return string.Empty;
            }

            if (token is JValue val && val.Value != null)
            {
                string s = val.Value.ToString();
                return LooksLikeUrl(s) ? s.Trim() : string.Empty;
            }

            if (token is JObject obj)
            {
                string direct = Str(
                    obj,
                    "sizable_cover",
                    "union_cover",
                    "imgurl",
                    "img_url",
                    "cover",
                    "pic",
                    "Image",
                    "img",
                    "album_img",
                    "author_image",
                    "avatar");
                if (LooksLikeUrl(direct))
                {
                    return direct.Trim();
                }

                string nested = ExtractCoverUrl(Obj(obj, "trans_param", "albuminfo", "album_info", "info", "data"), deepSearch, depth + 1);
                if (!string.IsNullOrEmpty(nested))
                {
                    return nested;
                }

                if (deepSearch)
                {
                    foreach (JProperty prop in obj.Properties())
                    {
                        if (prop.Value is JArray || prop.Value is JObject)
                        {
                            string inner = ExtractCoverUrl(prop.Value, true, depth + 1);
                            if (!string.IsNullOrEmpty(inner))
                            {
                                return inner;
                            }
                        }
                    }
                }
            }

            if (deepSearch && token is JArray arr)
            {
                for (int i = 0; i < arr.Count && i < 6; i++)
                {
                    string inner = ExtractCoverUrl(arr[i], true, depth + 1);
                    if (!string.IsNullOrEmpty(inner))
                    {
                        return inner;
                    }
                }
            }

            return string.Empty;
        }

        private static bool LooksLikeUrl(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string t = value.Trim();
            return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
