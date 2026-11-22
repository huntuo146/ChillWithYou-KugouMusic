using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI
{
    /// <summary>
    /// 专辑/歌单/单曲封面异步加载与缓存（内存 LRU 64 张 + 磁盘封面目录缓存）：
    /// - 直接通过 Plugin.Http（HttpClient）抓取字节流再主线程 LoadImage，绕过 Unity Release 包禁用明文 HTTP 的限制；
    /// - 优先尝试 HTTPS，失败自动回退原始 HTTP；
    /// - 若 KugouTrack 缺少 CoverUrl，自动调用 IKugouClient.ResolveTrackCoverUrlAsync 补全封面并回写内存缓存。
    /// </summary>
    internal sealed class CoverCache
    {
        private const int MaxEntries = 64;
        private const float FailureCooldownSeconds = 300f;
        private const int MaxConcurrentDownloads = 6;

        private sealed class CacheEntry
        {
            public string Key;
            public Texture2D Texture;
            public LinkedListNode<string> LruNode;
        }

        private readonly Dictionary<string, CacheEntry> _entries =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly HashSet<string> _inFlightUrls = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _inFlightHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _failedUntil = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _resolveFailedUntil = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private int _activeDownloads;
        private int _loggedFailures;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        public Texture2D GetOrRequestTrackCover(KugouTrack track, int targetSize, out bool isLoading)
        {
            isLoading = false;
            if (_disposed || track == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(track.CoverUrl))
            {
                return GetOrRequest(track.CoverUrl, targetSize, out isLoading);
            }

            if (string.IsNullOrEmpty(track.Hash))
            {
                return null;
            }

            float failUntil;
            if (_resolveFailedUntil.TryGetValue(track.Hash, out failUntil))
            {
                if (Time.unscaledTime < failUntil)
                {
                    return null;
                }

                _resolveFailedUntil.Remove(track.Hash);
            }

            if (_inFlightHashes.Contains(track.Hash))
            {
                isLoading = true;
                return null;
            }

            if (_inFlightHashes.Count >= MaxConcurrentDownloads)
            {
                return null;
            }

            _inFlightHashes.Add(track.Hash);
            isLoading = true;
            ResolveAndFetchAsync(track, targetSize, _cts.Token).Forget();
            return null;
        }

        public Texture2D GetOrRequest(string rawUrl, int targetSize, out bool isLoading)
        {
            isLoading = false;
            if (_disposed || string.IsNullOrEmpty(rawUrl))
            {
                return null;
            }

            string sizedUrl = ResolveSizedUrl(rawUrl, targetSize);
            if (string.IsNullOrEmpty(sizedUrl))
            {
                return null;
            }

            CacheEntry entry;
            if (_entries.TryGetValue(sizedUrl, out entry) && entry != null && entry.Texture != null)
            {
                Touch(entry);
                return entry.Texture;
            }

            float failUntil;
            if (_failedUntil.TryGetValue(sizedUrl, out failUntil))
            {
                if (Time.unscaledTime < failUntil)
                {
                    return null;
                }

                _failedUntil.Remove(sizedUrl);
            }

            if (_inFlightUrls.Contains(sizedUrl))
            {
                isLoading = true;
                return null;
            }

            if (_activeDownloads >= MaxConcurrentDownloads)
            {
                return null;
            }

            _inFlightUrls.Add(sizedUrl);
            _activeDownloads++;
            isLoading = true;
            FetchImageAsync(sizedUrl, _cts.Token).Forget();
            return null;
        }

        public static string ResolveSizedUrl(string rawUrl, int size)
        {
            if (string.IsNullOrEmpty(rawUrl))
            {
                return null;
            }

            string trimmed = rawUrl.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                trimmed = "https:" + trimmed;
            }

            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed
                    .Replace("{size}", size.ToString())
                    .Replace("{SIZE}", size.ToString());
            }

            return null;
        }

        private async UniTaskVoid ResolveAndFetchAsync(KugouTrack track, int targetSize, CancellationToken ct)
        {
            string hash = track.Hash;
            try
            {
                IKugouClient client = Plugin.Client;
                if (client == null)
                {
                    _resolveFailedUntil[hash] = Time.unscaledTime + 30f;
                    return;
                }

                string resolvedUrl = await client.ResolveTrackCoverUrlAsync(track, ct);
                if (ct.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (string.IsNullOrEmpty(resolvedUrl))
                {
                    await UniTask.SwitchToMainThread(CancellationToken.None);
                    _resolveFailedUntil[hash] = Time.unscaledTime + FailureCooldownSeconds;
                    return;
                }

                await UniTask.SwitchToMainThread(ct);
                if (ct.IsCancellationRequested || _disposed)
                {
                    return;
                }

                track.CoverUrl = resolvedUrl;
                if (Plugin.Store != null)
                {
                    Plugin.Store.UpdateTrackCover(hash, resolvedUrl);
                }

                string sized = ResolveSizedUrl(resolvedUrl, targetSize);
                if (!string.IsNullOrEmpty(sized) && !_entries.ContainsKey(sized) && !_inFlightUrls.Contains(sized))
                {
                    _inFlightUrls.Add(sized);
                    _activeDownloads++;
                    FetchImageAsync(sized, ct).Forget();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                _resolveFailedUntil[hash] = Time.unscaledTime + FailureCooldownSeconds;
            }
            finally
            {
                _inFlightHashes.Remove(hash);
            }
        }

        private async UniTaskVoid FetchImageAsync(string originalSizedUrl, CancellationToken ct)
        {
            try
            {
                string cacheFile = GetDiskCachePath(originalSizedUrl);
                byte[] bytes = await Task.Run(() => TryReadDiskBytes(cacheFile));

                if (bytes == null || bytes.Length < 64)
                {
                    bytes = await DownloadCoverBytesAsync(originalSizedUrl, ct);
                    if (bytes != null && bytes.Length >= 64)
                    {
                        byte[] copy = bytes;
                        _ = Task.Run(() => TryWriteDiskBytes(cacheFile, copy));
                    }
                }

                if (ct.IsCancellationRequested || _disposed)
                {
                    return;
                }

                await UniTask.SwitchToMainThread(ct);
                if (ct.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (bytes == null || bytes.Length < 64)
                {
                    _failedUntil[originalSizedUrl] = Time.unscaledTime + FailureCooldownSeconds;
                    return;
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;

                if (!ImageConversion.LoadImage(tex, bytes, true))
                {
                    UnityEngine.Object.Destroy(tex);
                    _failedUntil[originalSizedUrl] = Time.unscaledTime + FailureCooldownSeconds;
                    return;
                }

                Insert(originalSizedUrl, tex);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_loggedFailures < 3 && Plugin.Log != null)
                {
                    _loggedFailures++;
                    Plugin.Log.LogWarning("[Cover] 封面加载异常: " + ex.Message);
                }

                _failedUntil[originalSizedUrl] = Time.unscaledTime + FailureCooldownSeconds;
            }
            finally
            {
                _inFlightUrls.Remove(originalSizedUrl);
                _activeDownloads = Mathf.Max(0, _activeDownloads - 1);
            }
        }

        private static async UniTask<byte[]> DownloadCoverBytesAsync(string url, CancellationToken ct)
        {
            IHttpTransport http = Plugin.Http;
            if (http == null)
            {
                return null;
            }

            var headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0" },
                { "Referer", "https://www.kugou.com/" },
            };

            string httpsUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + url.Substring("http://".Length)
                : url;

            HttpResponseData resp = await http.GetAsync(httpsUrl, headers, ct);
            if (resp != null && resp.Ok && resp.Bytes != null && resp.Bytes.Length >= 64)
            {
                return resp.Bytes;
            }

            if (!string.Equals(httpsUrl, url, StringComparison.Ordinal))
            {
                HttpResponseData httpResp = await http.GetAsync(url, headers, ct);
                if (httpResp != null && httpResp.Ok && httpResp.Bytes != null && httpResp.Bytes.Length >= 64)
                {
                    return httpResp.Bytes;
                }
            }

            return null;
        }

        private static string GetDiskCachePath(string url)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }

                return Path.Combine(KugouPaths.CoversDir, sb.ToString() + ".img");
            }
        }

        private static byte[] TryReadDiskBytes(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    return bytes != null && bytes.Length >= 64 ? bytes : null;
                }
            }
            catch
            {
            }

            return null;
        }

        private static void TryWriteDiskBytes(string path, byte[] bytes)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tmp, path);
            }
            catch
            {
            }
        }

        private void Touch(CacheEntry entry)
        {
            if (entry.LruNode != null && !ReferenceEquals(entry.LruNode, _lru.First))
            {
                _lru.Remove(entry.LruNode);
                _lru.AddFirst(entry.LruNode);
            }
        }

        private void Insert(string key, Texture2D tex)
        {
            CacheEntry existing;
            if (_entries.TryGetValue(key, out existing))
            {
                if (existing.Texture != null && !ReferenceEquals(existing.Texture, tex))
                {
                    UnityEngine.Object.Destroy(existing.Texture);
                }

                existing.Texture = tex;
                Touch(existing);
                return;
            }

            while (_entries.Count >= MaxEntries && _lru.Last != null)
            {
                string oldestKey = _lru.Last.Value;
                _lru.RemoveLast();
                CacheEntry victim;
                if (_entries.TryGetValue(oldestKey, out victim))
                {
                    _entries.Remove(oldestKey);
                    if (victim.Texture != null)
                    {
                        UnityEngine.Object.Destroy(victim.Texture);
                    }
                }
            }

            var node = _lru.AddFirst(key);
            _entries[key] = new CacheEntry
            {
                Key = key,
                Texture = tex,
                LruNode = node,
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }

            foreach (var kv in _entries)
            {
                if (kv.Value != null && kv.Value.Texture != null)
                {
                    UnityEngine.Object.Destroy(kv.Value.Texture);
                }
            }

            _entries.Clear();
            _lru.Clear();
            _inFlightUrls.Clear();
            _inFlightHashes.Clear();
            _failedUntil.Clear();
            _resolveFailedUntil.Clear();
        }
    }
}
