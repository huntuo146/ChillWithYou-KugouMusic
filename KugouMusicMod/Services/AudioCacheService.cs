using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>
    /// 音频下载缓存：
    /// - 先写 .tmp 再原子重命名，避免半截文件被当成可用缓存；
    /// - 默认 500MB（可配置）LRU 淘汰，按最后写入时间清理；
    /// - 当前曲目开播后后台静默预加载列表中的下一首。
    /// </summary>
    internal sealed class AudioCacheService
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _preloading = new HashSet<string>(StringComparer.Ordinal);

        public string CacheDirectory
        {
            get { return KugouPaths.CacheDir; }
        }

        // ---------------- 统计 / 清理 ----------------

        public long GetCacheSizeBytes()
        {
            try
            {
                if (!Directory.Exists(KugouPaths.CacheDir))
                {
                    return 0;
                }

                long total = 0;
                string[] files = Directory.GetFiles(KugouPaths.CacheDir, "*.*");
                for (int i = 0; i < files.Length; i++)
                {
                    if (files[i].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        total += new FileInfo(files[i]).Length;
                    }
                    catch
                    {
                        // 文件被占用时跳过
                    }
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }

        public int GetCacheFileCount()
        {
            try
            {
                if (!Directory.Exists(KugouPaths.CacheDir))
                {
                    return 0;
                }

                int count = 0;
                string[] files = Directory.GetFiles(KugouPaths.CacheDir, "*.*");
                for (int i = 0; i < files.Length; i++)
                {
                    if (!files[i].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        public string GetCacheSizeText()
        {
            double mb = GetCacheSizeBytes() / (1024.0 * 1024.0);
            return mb.ToString("0.0") + " MB / " + GetCacheFileCount() + " 个文件";
        }

        /// <summary>清空缓存（含残留的 .tmp）。</summary>
        public bool ClearCache()
        {
            try
            {
                if (!Directory.Exists(KugouPaths.CacheDir))
                {
                    return true;
                }

                string[] files = Directory.GetFiles(KugouPaths.CacheDir, "*.*");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("[Cache] 删除失败 " + Path.GetFileName(files[i]) + ": " + ex.Message);
                    }
                }

                Plugin.Log.LogInfo("[Cache] 已清空，当前 " + GetCacheSizeText());
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Cache] 清空失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>按容量上限做 LRU 淘汰（按最后写入时间，最旧先删）。</summary>
        public bool EnforceQuota()
        {
            try
            {
                if (!Directory.Exists(KugouPaths.CacheDir))
                {
                    return true;
                }

                int limitMb = Plugin.MaxCacheSizeMB != null ? Plugin.MaxCacheSizeMB.Value : 500;
                if (limitMb <= 0)
                {
                    return true;
                }

                long limitBytes = limitMb * 1024L * 1024L;
                string[] files = Directory.GetFiles(KugouPaths.CacheDir, "*.*");
                var entries = new List<FileInfo>(files.Length);
                long total = 0;

                for (int i = 0; i < files.Length; i++)
                {
                    if (files[i].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(files[i]);
                        entries.Add(info);
                        total += info.Length;
                    }
                    catch
                    {
                        // 忽略不可读文件
                    }
                }

                if (total <= limitBytes)
                {
                    return true;
                }

                entries.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                int removed = 0;
                for (int i = 0; i < entries.Count && total > limitBytes; i++)
                {
                    try
                    {
                        long length = entries[i].Length;
                        string name = entries[i].Name;
                        entries[i].Delete();
                        total -= length;
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("[Cache] 淘汰失败 " + entries[i].Name + ": " + ex.Message);
                    }
                }

                if (removed > 0)
                {
                    Plugin.Log.LogInfo("[Cache] LRU 淘汰 " + removed + " 个文件，当前 " +
                                       (GetCacheSizeBytes() / (1024.0 * 1024.0)).ToString("0.0") + " MB");
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Cache] 配额检查失败: " + ex.Message);
                return false;
            }
        }

        // ---------------- 下载 ----------------

        /// <summary>
        /// 确保曲目已缓存到本地。
        /// 优先命中目标音质；只有其它音质时先尝试拉取目标音质，
        /// 拉取失败（离线 / 无直链）再兜底使用已有音质。
        /// </summary>
        public async UniTask<bool> EnsureCachedAsync(KugouTrack track, CancellationToken ct)
        {
            if (track == null || string.IsNullOrEmpty(track.Hash))
            {
                return false;
            }

            KugouQuality preferred = Plugin.GetPreferredQuality();

            // 1) 目标音质命中
            string exact = KugouPaths.FindCachedFile(track.Hash, preferred);
            if (!string.IsNullOrEmpty(exact))
            {
                Touch(exact);
                return true;
            }

            // 2) 记录已有其它音质，作为拉取失败后的兜底
            string anyQuality = KugouPaths.FindCachedFile(track.Hash);

            // 3) 尝试拉取目标音质
            if (Plugin.Client != null)
            {
                bool downloaded = await TryDownloadAsync(track, preferred, ct);
                if (downloaded)
                {
                    return true;
                }
            }

            // 4) 兜底使用已有音质（离线场景仍可播放）
            if (!string.IsNullOrEmpty(anyQuality))
            {
                Touch(anyQuality);
                Plugin.Log.LogWarning("[Cache] 目标音质不可用，改用已有缓存: " + Path.GetFileName(anyQuality));
                return true;
            }

            return false;
        }

        private async UniTask<bool> TryDownloadAsync(
            KugouTrack track,
            KugouQuality quality,
            CancellationToken ct)
        {
            IKugouClient client = Plugin.Client;
            if (client == null)
            {
                Plugin.Log.LogWarning("[Cache] 接口客户端未初始化");
                return false;
            }

            KugouPlayUrlResult playUrl;
            try
            {
                playUrl = await client.GetPlayUrlAsync(track, quality, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Cache] 直链获取异常: " + ex.Message);
                return false;
            }

            if (playUrl == null || !playUrl.HasUrl())
            {
                string reason = playUrl != null ? playUrl.Message : "未知原因";
                bool paidTrack = track.IsPaid() || track.PayType > 0;
                Plugin.Log.LogWarning("[Cache] 无法获取直链（" + track.GetDisplayName() + "，付费=" +
                                      paidTrack + "）: " + reason);
                return false;
            }

            if (playUrl.IsTrial && Plugin.SkipTrialTracks != null && Plugin.SkipTrialTracks.Value)
            {
                Plugin.Log.LogWarning("[Cache] 试听片段已跳过: " + track.GetDisplayName());
                return false;
            }

            string ext = NormalizeExtension(playUrl.ExtName, quality);

            // 网关可能返回 flac 等本机解码链不支持的封装，降级到 320 mp3 再试一次
            if (!IsSupportedContainer(ext))
            {
                Plugin.Log.LogWarning("[Cache] 封装 " + ext + " 不受支持，降级 320k mp3 重试");
                KugouPlayUrlResult fallback = await client.GetPlayUrlAsync(track, KugouQuality.Mp3320, ct);
                if (fallback == null || !fallback.HasUrl())
                {
                    Plugin.Log.LogWarning("[Cache] 降级后仍无可用直链: " + track.GetDisplayName());
                    return false;
                }

                if (fallback.IsTrial && Plugin.SkipTrialTracks != null && Plugin.SkipTrialTracks.Value)
                {
                    Plugin.Log.LogWarning("[Cache] 降级后仍为试听片段，已跳过: " + track.GetDisplayName());
                    return false;
                }

                playUrl = fallback;
                quality = KugouQuality.Mp3320;
                ext = NormalizeExtension(fallback.ExtName, quality);
            }

            if (!IsSupportedContainer(ext))
            {
                Plugin.Log.LogWarning("[Cache] 无可用封装: " + ext);
                return false;
            }

            string path = KugouPaths.GetCacheFilePath(track.Hash, quality, ext);
            // 预加载与前台播放可能同时命中同一首，使用唯一临时名避免互相覆盖
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            HttpResponseData response;
            try
            {
                response = await Plugin.Http.GetAsync(playUrl.Url, null, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Cache] 下载异常: " + ex.Message);
                return false;
            }

            if (response == null || !response.Ok || response.Bytes == null || response.Bytes.Length == 0)
            {
                Plugin.Log.LogWarning("[Cache] 下载失败: " + (response != null ? response.Error : "空响应"));
                return false;
            }

            byte[] payload = response.Bytes;
            int payloadSize = payload.Length;

            // 写盘与 LRU 计算都放到线程池，避免主线程发生磁盘 IO 微卡顿
            bool written = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(KugouPaths.CacheDir);
                    File.WriteAllBytes(tmp, payload);

                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    File.Move(tmp, path);
                    return true;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[Cache] 写盘失败: " + ex.Message);
                    TryDelete(tmp);
                    return false;
                }
            });

            if (!written)
            {
                return false;
            }

            await System.Threading.Tasks.Task.Run(() => EnforceQuota());

            Plugin.Log.LogInfo("[Cache] 已缓存 " + track.GetDisplayName() + " (" +
                               (payloadSize / 1024.0 / 1024.0).ToString("0.00") + " MB)");
            return true;
        }

        // ---------------- 预加载 ----------------

        /// <summary>当前曲目开播后，后台静默预加载列表中的下一首（失败静默）。</summary>
        public void SchedulePreloadNext(MusicService service, GameAudioInfo current)
        {
            if (Plugin.PreloadNextTrack == null || !Plugin.PreloadNextTrack.Value)
            {
                return;
            }

            if (service == null || current == null)
            {
                return;
            }

            try
            {
                var list = service.CurrentPlayList;
                if (list == null || list.Count <= 1)
                {
                    return;
                }

                int currentIndex = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list[i], current))
                    {
                        currentIndex = i;
                        break;
                    }
                }

                if (currentIndex < 0)
                {
                    return;
                }

                for (int step = 1; step < list.Count; step++)
                {
                    GameAudioInfo next = list[(currentIndex + step) % list.Count];
                    KugouTrack nextTrack;
                    if (!Plugin.Registry.TryGet(next, out nextTrack) || nextTrack == null)
                    {
                        continue;
                    }

                    PreloadAsync(nextTrack).Forget();
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Cache] 预加载调度失败: " + ex.Message);
            }
        }

        private async UniTaskVoid PreloadAsync(KugouTrack track)
        {
            string hash = track != null ? track.Hash : null;
            if (string.IsNullOrEmpty(hash))
            {
                return;
            }

            lock (_gate)
            {
                if (!_preloading.Add(hash))
                {
                    return;
                }
            }

            try
            {
                bool ok = await EnsureCachedAsync(track, CancellationToken.None);
                if (ok && Plugin.EnableVerboseLog != null && Plugin.EnableVerboseLog.Value)
                {
                    Plugin.Log.LogInfo("[Cache] 预加载完成: " + track.GetDisplayName());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Cache] 预加载失败（忽略）: " + ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _preloading.Remove(hash);
                }
            }
        }

        // ---------------- 工具 ----------------

        private static void Touch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // 访问时间更新失败不影响播放
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 残留文件留给下次 LRU 清理
            }
        }

        private static bool IsSupportedContainer(string ext)
        {
            return ext == "mp3" || ext == "m4a" || ext == "ogg" || ext == "wav";
        }

        private static string NormalizeExtension(string ext, KugouQuality quality)
        {
            if (!string.IsNullOrEmpty(ext))
            {
                string value = ext.Trim().TrimStart('.').ToLowerInvariant();
                if (value == "mp3" || value == "m4a" || value == "flac" || value == "ogg" || value == "wav")
                {
                    return value;
                }
            }

            return quality == KugouQuality.M4A ? "m4a" : "mp3";
        }
    }
}