using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using ChillWithYou.KugouMusic.Models;
using Newtonsoft.Json;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>插件使用的物理路径。</summary>
    internal static class KugouPaths
    {
        private static string _configDir;
        private static string _cacheDir;

        public static string ConfigDir
        {
            get
            {
                if (string.IsNullOrEmpty(_configDir))
                {
                    _configDir = Path.Combine(Paths.BepInExRootPath, "config", "KugouMusic");
                }

                return _configDir;
            }
        }

        public static string CacheDir
        {
            get
            {
                if (string.IsNullOrEmpty(_cacheDir))
                {
                    _cacheDir = Path.Combine(Paths.BepInExRootPath, "cache", "kugou");
                }

                return _cacheDir;
            }
        }

        public static string StoreFilePath
        {
            get { return Path.Combine(ConfigDir, "store.json"); }
        }

        public static string LyricsDir
        {
            get { return Path.Combine(CacheDir, "lyrics"); }
        }

        public static string CoversDir
        {
            get { return Path.Combine(CacheDir, "covers"); }
        }

        public static string UpdatesDir
        {
            get { return Path.Combine(CacheDir, "updates"); }
        }

        public static string StagedUpdateDir
        {
            get { return Path.Combine(UpdatesDir, "staged"); }
        }

        public static string RepoOverrideFilePath
        {
            get { return Path.Combine(ConfigDir, "github_repo.txt"); }
        }

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(LyricsDir);
            Directory.CreateDirectory(CoversDir);
        }

        public static string ReadRepoOverride()
        {
            try
            {
                string path = RepoOverrideFilePath;
                if (File.Exists(path))
                {
                    return (File.ReadAllText(path, Encoding.UTF8) ?? string.Empty).Trim();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static void WriteRepoOverride(string repo)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(RepoOverrideFilePath, (repo ?? string.Empty).Trim(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        /// <summary>缓存文件路径：{hash}_{quality}.{ext}</summary>
        public static string GetCacheFilePath(string hash, KugouQuality quality)
        {
            string ext;
            switch (quality)
            {
                case KugouQuality.M4A:
                    ext = "m4a";
                    break;
                default:
                    ext = "mp3";
                    break;
            }

            return GetCacheFilePath(hash, quality, ext);
        }

        /// <summary>按实际响应扩展名生成缓存路径（服务器可能返回与请求不同的封装）。</summary>
        public static string GetCacheFilePath(string hash, KugouQuality quality, string ext)
        {
            string safeHash = string.IsNullOrEmpty(hash) ? "empty" : hash.Trim().ToLowerInvariant();

            string tag;
            switch (quality)
            {
                case KugouQuality.M4A:
                    tag = "m4a";
                    break;
                case KugouQuality.Mp3320:
                    tag = "320";
                    break;
                default:
                    tag = "128";
                    break;
            }

            string safeExt = string.IsNullOrEmpty(ext) ? "mp3" : ext.Trim().TrimStart('.').ToLowerInvariant();
            if (safeExt.Length == 0)
            {
                safeExt = "mp3";
            }

            return Path.Combine(CacheDir, safeHash + "_" + tag + "." + safeExt);
        }

        /// <summary>查找某 hash 已存在的任意音质缓存（忽略 .tmp 残留）。</summary>
        public static string FindCachedFile(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            return FindBest(hash.Trim().ToLowerInvariant() + "_*");
        }

        /// <summary>按目标音质查找缓存：{hash}_{128|320|m4a}.* （忽略 .tmp 残留）。</summary>
        public static string FindCachedFile(string hash, KugouQuality quality)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            string tag;
            switch (quality)
            {
                case KugouQuality.M4A:
                    tag = "m4a";
                    break;
                case KugouQuality.Mp3320:
                    tag = "320";
                    break;
                default:
                    tag = "128";
                    break;
            }

            return FindBest(hash.Trim().ToLowerInvariant() + "_" + tag + ".*");
        }

        private static string FindBest(string pattern)
        {
            try
            {
                if (!Directory.Exists(CacheDir))
                {
                    return null;
                }

                string[] candidates = Directory.GetFiles(CacheDir, pattern);
                string best = null;
                long bestLength = 0;

                for (int i = 0; i < candidates.Length; i++)
                {
                    string candidate = candidates[i];
                    if (candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long length;
                    try
                    {
                        length = new FileInfo(candidate).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    if (length > bestLength)
                    {
                        best = candidate;
                        bestLength = length;
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>本地持久化的歌单记录。</summary>
    [Serializable]
    internal sealed class KugouPlaylistRecord
    {
        public string SpecialId = string.Empty;
        public string Name = string.Empty;
        public string Author = string.Empty;
        public long UpdatedAtUnix = 0;
        public List<KugouTrack> Tracks = new List<KugouTrack>();

        public KugouPlaylistSummary ToSummary()
        {
            return new KugouPlaylistSummary
            {
                SpecialId = SpecialId,
                Name = Name,
                Author = Author,
                SongCount = Tracks == null ? 0 : Tracks.Count,
            };
        }
    }

    /// <summary>本地 JSON 存储根对象（绝不写入游戏存档）。</summary>
    [Serializable]
    internal sealed class KugouStoreData
    {
        public int Version = 1;
        public string LastPlaylistId = string.Empty;
        public string LastTrackUuid = string.Empty;
        public List<KugouPlaylistRecord> Playlists = new List<KugouPlaylistRecord>();
        public List<string> FavoriteHashes = new List<string>();
        public List<string> ExcludedHashes = new List<string>();
        public List<string> PinnedHashes = new List<string>();
    }

    /// <summary>
    /// 插件本地偏好与歌单存储。
    /// 所有游戏侧"收藏 / 排除 / 排序"操作在酷狗模式下都改写到这份 JSON，避免污染官方存档。
    /// </summary>
    internal sealed class KugouStorage
    {
        private readonly object _gate = new object();
        private KugouStoreData _data;

        public KugouStoreData Data
        {
            get { return _data; }
        }

        public static KugouStorage Create()
        {
            var storage = new KugouStorage();
            storage.LoadFromDisk();
            return storage;
        }

        public void LoadFromDisk()
        {
            lock (_gate)
            {
                KugouPaths.EnsureDirectories();
                string path = KugouPaths.StoreFilePath;

                if (!File.Exists(path))
                {
                    _data = new KugouStoreData();
                    return;
                }

                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    KugouStoreData loaded = JsonConvert.DeserializeObject<KugouStoreData>(json);
                    _data = loaded ?? new KugouStoreData();
                }
                catch (Exception)
                {
                    // 损坏时保留一份备份，避免用户数据直接丢失
                    try
                    {
                        File.Copy(path, path + ".bak", true);
                    }
                    catch
                    {
                        // 备份失败不阻塞启动
                    }

                    _data = new KugouStoreData();
                }

                if (_data.Playlists == null)
                {
                    _data.Playlists = new List<KugouPlaylistRecord>();
                }

                if (_data.FavoriteHashes == null)
                {
                    _data.FavoriteHashes = new List<string>();
                }

                if (_data.ExcludedHashes == null)
                {
                    _data.ExcludedHashes = new List<string>();
                }

                if (_data.PinnedHashes == null)
                {
                    _data.PinnedHashes = new List<string>();
                }
            }
        }

        public void Save()
        {
            lock (_gate)
            {
                try
                {
                    KugouPaths.EnsureDirectories();
                    string path = KugouPaths.StoreFilePath;
                    string tmp = path + ".tmp";
                    string json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(tmp, path, null);
                        }
                        catch (Exception)
                        {
                            File.Copy(tmp, path, true);
                            File.Delete(tmp);
                        }
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[Store] 保存失败: " + ex.Message);
                }
            }
        }

        // ---------------- 歌单 ----------------

        public KugouPlaylistRecord GetPlaylist(string specialId)
        {
            if (string.IsNullOrEmpty(specialId))
            {
                return null;
            }

            lock (_gate)
            {
                if (_data == null || _data.Playlists == null)
                {
                    return null;
                }

                for (int i = 0; i < _data.Playlists.Count; i++)
                {
                    KugouPlaylistRecord record = _data.Playlists[i];
                    if (record != null && record.SpecialId == specialId)
                    {
                        return record;
                    }
                }

                return null;
            }
        }

        public void UpdateTrackCover(string hash, string coverUrl)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(coverUrl))
            {
                return;
            }

            lock (_gate)
            {
                if (_data == null || _data.Playlists == null)
                {
                    return;
                }

                for (int i = 0; i < _data.Playlists.Count; i++)
                {
                    KugouPlaylistRecord pl = _data.Playlists[i];
                    if (pl == null || pl.Tracks == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < pl.Tracks.Count; j++)
                    {
                        KugouTrack t = pl.Tracks[j];
                        if (t != null && string.IsNullOrEmpty(t.CoverUrl) &&
                            string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            t.CoverUrl = coverUrl;
                        }
                    }
                }
            }
        }

        public List<KugouPlaylistRecord> GetPlaylists()
        {
            lock (_gate)
            {
                return new List<KugouPlaylistRecord>(_data.Playlists);
            }
        }

        public void SavePlaylist(KugouPlaylistRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.SpecialId))
            {
                return;
            }

            lock (_gate)
            {
                KugouPlaylistRecord existing = null;
                for (int i = 0; i < _data.Playlists.Count; i++)
                {
                    if (_data.Playlists[i] != null && _data.Playlists[i].SpecialId == record.SpecialId)
                    {
                        existing = _data.Playlists[i];
                        break;
                    }
                }

                if (existing == null)
                {
                    _data.Playlists.Add(record);
                }
                else
                {
                    existing.Name = record.Name;
                    existing.Author = record.Author;
                    existing.UpdatedAtUnix = record.UpdatedAtUnix;
                    existing.Tracks = record.Tracks;
                }

                _data.LastPlaylistId = record.SpecialId;
            }

            Save();
        }

        public bool RemovePlaylist(string specialId)
        {
            if (string.IsNullOrEmpty(specialId))
            {
                return false;
            }

            bool removed = false;
            lock (_gate)
            {
                for (int i = _data.Playlists.Count - 1; i >= 0; i--)
                {
                    if (_data.Playlists[i] != null && _data.Playlists[i].SpecialId == specialId)
                    {
                        _data.Playlists.RemoveAt(i);
                        removed = true;
                    }
                }
            }

            if (removed)
            {
                Save();
            }

            return removed;
        }

        // ---------------- 收藏 / 排除 ----------------

        public bool IsFavorite(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return false;
            }

            lock (_gate)
            {
                return _data.FavoriteHashes.Contains(hash);
            }
        }

        public bool SetFavorite(string hash, bool favorite)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return false;
            }

            bool changed = false;

            lock (_gate)
            {
                if (favorite)
                {
                    if (!_data.FavoriteHashes.Contains(hash))
                    {
                        _data.FavoriteHashes.Add(hash);
                        changed = true;
                    }
                }
                else
                {
                    if (_data.FavoriteHashes.Contains(hash))
                    {
                        _data.FavoriteHashes.Remove(hash);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                Save();
            }

            return changed;
        }

        public bool IsExcluded(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return false;
            }

            lock (_gate)
            {
                return _data.ExcludedHashes.Contains(hash);
            }
        }

        public void SetExcluded(string hash, bool excluded)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return;
            }

            lock (_gate)
            {
                if (excluded)
                {
                    if (!_data.ExcludedHashes.Contains(hash))
                    {
                        _data.ExcludedHashes.Add(hash);
                    }
                }
                else
                {
                    _data.ExcludedHashes.Remove(hash);
                }
            }

            Save();
        }

        // ---------------- 上次状态 ----------------

        public string LastPlaylistId
        {
            get
            {
                lock (_gate)
                {
                    return _data.LastPlaylistId;
                }
            }
            set
            {
                lock (_gate)
                {
                    _data.LastPlaylistId = value ?? string.Empty;
                }

                Save();
            }
        }

        public string LastTrackUuid
        {
            get
            {
                lock (_gate)
                {
                    return _data.LastTrackUuid;
                }
            }
            set
            {
                lock (_gate)
                {
                    _data.LastTrackUuid = value ?? string.Empty;
                }
            }
        }
    }
}
