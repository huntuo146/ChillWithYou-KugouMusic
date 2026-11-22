using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ChillWithYou.KugouMusic.Services
{
    internal enum UpdateWorkState
    {
        Idle = 0,
        Checking = 1,
        UpToDate = 2,
        UpdateAvailable = 3,
        Downloading = 4,
        ReadyToApply = 5,
        Error = 6,
    }

    internal sealed class GitHubReleaseInfo
    {
        public string TagName = string.Empty;
        public string VersionText = string.Empty;
        public string Title = string.Empty;
        public string Body = string.Empty;
        public string HtmlUrl = string.Empty;
        public string PublishedAt = string.Empty;
        public string AssetName = string.Empty;
        public string AssetDownloadUrl = string.Empty;
        public long AssetSize = 0;
        public bool IsBundleZip = false;
    }

    /// <summary>
    /// 游戏内 GitHub Releases 更新检查、下载暂存与退出自替换服务：
    /// - 支持任意 `owner/repo` 或完整 GitHub URL（可在设置页编辑，亦支持安装器写入 github_repo.txt 共享配置）；
    /// - 下载过程不覆盖用户配置 `BepInEx\config\` 与歌单账号数据 `store.json`；
    /// - 因 Windows 运行中无法直接覆写已加载 DLL，采用暂存目录 + 退出时替换并可选自动重启游戏。
    /// </summary>
    internal sealed class GitHubUpdateService : IDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public UpdateWorkState State { get; private set; } = UpdateWorkState.Idle;
        public string StatusMessage { get; private set; } = string.Empty;
        public float DownloadProgress { get; private set; }
        public long DownloadedBytes { get; private set; }
        public long TotalBytes { get; private set; }
        public GitHubReleaseInfo LatestRelease { get; private set; }
        public string StagedVersionText { get; private set; } = string.Empty;

        public bool IsBusy
        {
            get { return State == UpdateWorkState.Checking || State == UpdateWorkState.Downloading; }
        }

        public static string GetEffectiveRepoSlug()
        {
            string cfg = Plugin.GitHubRepo != null ? Plugin.GitHubRepo.Value : string.Empty;
            string slug = NormalizeRepoSlug(cfg);
            if (!string.IsNullOrEmpty(slug))
            {
                return slug;
            }

            string fileSlug = NormalizeRepoSlug(KugouPaths.ReadRepoOverride());
            if (!string.IsNullOrEmpty(fileSlug))
            {
                return fileSlug;
            }

            return NormalizeRepoSlug(KugouIds.DefaultGitHubRepo);
        }

        public static string SaveRepoSetting(string rawInput)
        {
            string slug = NormalizeRepoSlug(rawInput);
            if (Plugin.GitHubRepo != null)
            {
                Plugin.GitHubRepo.Value = slug;
                Plugin.SaveConfig();
            }

            KugouPaths.WriteRepoOverride(slug);
            return slug;
        }

        public static string NormalizeRepoSlug(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string s = input.Trim();
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - 4);
            }

            int idx = s.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                s = s.Substring(idx + "github.com/".Length);
            }

            s = s.Trim('/');
            string[] parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return string.Empty;
            }

            string owner = parts[0].Trim();
            string repo = parts[1].Trim();
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo) || owner.Contains(" ") || repo.Contains(" "))
            {
                return string.Empty;
            }

            return owner + "/" + repo;
        }

        public async UniTask<bool> CheckForUpdatesAsync(string overrideRepoInput = null)
        {
            if (IsBusy)
            {
                return false;
            }

            string slug = !string.IsNullOrWhiteSpace(overrideRepoInput)
                ? NormalizeRepoSlug(overrideRepoInput)
                : GetEffectiveRepoSlug();

            if (string.IsNullOrEmpty(slug))
            {
                State = UpdateWorkState.Error;
                StatusMessage = "请先填写 GitHub 仓库（格式如 owner/repo）";
                return false;
            }

            State = UpdateWorkState.Checking;
            StatusMessage = "正在查询 GitHub Releases...";
            LatestRelease = null;

            CancellationToken ct = _cts.Token;
            try
            {
                var headers = new Dictionary<string, string>
                {
                    { "Accept", "application/vnd.github+json" },
                    { "User-Agent", "ChillWithYou-KugouMusic/" + KugouIds.PluginVersion },
                };

                string apiUrl = "https://api.github.com/repos/" + slug + "/releases/latest";
                HttpResponseData resp = await Plugin.Http.GetAsync(apiUrl, headers, ct);

                JObject relObj = null;
                if (resp != null && resp.Ok && !string.IsNullOrEmpty(resp.Text))
                {
                    relObj = JObject.Parse(resp.Text);
                }
                else if (resp != null && resp.StatusCode == 404)
                {
                    string listUrl = "https://api.github.com/repos/" + slug + "/releases?per_page=5";
                    HttpResponseData listResp = await Plugin.Http.GetAsync(listUrl, headers, ct);
                    if (listResp != null && listResp.Ok && !string.IsNullOrEmpty(listResp.Text))
                    {
                        JArray arr = JArray.Parse(listResp.Text);
                        for (int i = 0; i < arr.Count; i++)
                        {
                            var cand = arr[i] as JObject;
                            if (cand != null && !cand.Value<bool>("draft"))
                            {
                                relObj = cand;
                                break;
                            }
                        }
                    }
                    else if (listResp != null && listResp.StatusCode == 404)
                    {
                        await UniTask.SwitchToMainThread(ct);
                        State = UpdateWorkState.Error;
                        StatusMessage = "未找到仓库或暂无公开 Release：" + slug;
                        return false;
                    }
                    else
                    {
                        resp = listResp;
                    }
                }

                await UniTask.SwitchToMainThread(ct);

                if (relObj == null)
                {
                    string err = resp != null && !string.IsNullOrEmpty(resp.Error)
                        ? resp.Error
                        : ("HTTP " + (resp != null ? resp.StatusCode : 0));
                    State = UpdateWorkState.Error;
                    StatusMessage = "检查更新失败：" + err;
                    return false;
                }

                GitHubReleaseInfo info = ParseReleaseObject(relObj);
                LatestRelease = info;

                if (IsNewerVersion(info.VersionText, KugouIds.PluginVersion))
                {
                    if (string.IsNullOrEmpty(info.AssetDownloadUrl))
                    {
                        State = UpdateWorkState.UpdateAvailable;
                        StatusMessage = "发现 v" + info.VersionText + "（该 Release 暂无附件，可打开发布页）";
                    }
                    else
                    {
                        State = UpdateWorkState.UpdateAvailable;
                        StatusMessage = "发现新版本 v" + info.VersionText + "（当前 v" + KugouIds.PluginVersion + "）";
                    }

                    return true;
                }

                State = UpdateWorkState.UpToDate;
                StatusMessage = "当前已是最新版本（v" + KugouIds.PluginVersion + "）";
                return false;
            }
            catch (OperationCanceledException)
            {
                State = UpdateWorkState.Idle;
                StatusMessage = "已取消检查";
                return false;
            }
            catch (Exception ex)
            {
                await UniTask.SwitchToMainThread();
                State = UpdateWorkState.Error;
                StatusMessage = "检查更新异常：" + ex.Message;
                return false;
            }
        }

        public async UniTask<bool> DownloadAndStageLatestAsync()
        {
            GitHubReleaseInfo info = LatestRelease;
            if (info == null || string.IsNullOrEmpty(info.AssetDownloadUrl) || IsBusy)
            {
                return false;
            }

            State = UpdateWorkState.Downloading;
            DownloadProgress = 0f;
            DownloadedBytes = 0;
            TotalBytes = info.AssetSize;
            StatusMessage = "正在下载 " + info.AssetName + "...";

            CancellationToken ct = _cts.Token;
            string updatesDir = KugouPaths.UpdatesDir;
            string downloadTmp = Path.Combine(updatesDir, "download.tmp");
            string stagedDir = KugouPaths.StagedUpdateDir;

            try
            {
                Directory.CreateDirectory(updatesDir);
                if (File.Exists(downloadTmp))
                {
                    File.Delete(downloadTmp);
                }

                await DownloadFileWithProgressAsync(info.AssetDownloadUrl, downloadTmp, ct);

                await UniTask.SwitchToMainThread(ct);
                StatusMessage = "正在校验并解包更新...";

                await Task.Run(() => StageDownloadedAsset(downloadTmp, info.AssetName, stagedDir), ct);
                await UniTask.SwitchToMainThread(ct);

                try
                {
                    if (File.Exists(downloadTmp))
                    {
                        File.Delete(downloadTmp);
                    }
                }
                catch
                {
                }

                StagedVersionText = info.VersionText;
                DownloadProgress = 1f;
                State = UpdateWorkState.ReadyToApply;
                StatusMessage = "v" + info.VersionText + " 已就绪，点击「立即重启并更新」生效";
                return true;
            }
            catch (OperationCanceledException)
            {
                await UniTask.SwitchToMainThread();
                State = UpdateWorkState.Idle;
                StatusMessage = "已取消下载";
                return false;
            }
            catch (Exception ex)
            {
                await UniTask.SwitchToMainThread();
                State = UpdateWorkState.Error;
                StatusMessage = "下载/校验失败：" + ex.Message;
                return false;
            }
        }

        public bool ApplyStagedUpdateAndRestart(bool relaunchGame = true)
        {
            string stagedDir = KugouPaths.StagedUpdateDir;
            if (!Directory.Exists(stagedDir))
            {
                State = UpdateWorkState.Error;
                StatusMessage = "未找到已暂存的更新文件";
                return false;
            }

            string gameRoot = ResolveGameRoot();
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
            {
                State = UpdateWorkState.Error;
                StatusMessage = "无法定位游戏根目录";
                return false;
            }

            try
            {
                if (Plugin.Store != null)
                {
                    Plugin.Store.Save();
                }

                Plugin.SaveConfig();

                if (Plugin.Gateway != null)
                {
                    Plugin.Gateway.StopIfOwned();
                }

                int currentPid = Process.GetCurrentProcess().Id;
                string scriptPath = Path.Combine(KugouPaths.UpdatesDir, "apply_update.ps1");
                WriteFallbackApplyScript(scriptPath, currentPid, stagedDir, gameRoot, relaunchGame);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                    WorkingDirectory = gameRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });

                Application.Quit();
                return true;
            }
            catch (Exception ex)
            {
                State = UpdateWorkState.Error;
                StatusMessage = "启动更新器失败：" + ex.Message;
                return false;
            }
        }

        public void OpenReleasePage()
        {
            string url = LatestRelease != null && !string.IsNullOrEmpty(LatestRelease.HtmlUrl)
                ? LatestRelease.HtmlUrl
                : string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                string slug = GetEffectiveRepoSlug();
                if (!string.IsNullOrEmpty(slug))
                {
                    url = "https://github.com/" + slug + "/releases";
                }
            }

            if (!string.IsNullOrEmpty(url))
            {
                Application.OpenURL(url);
            }
        }

        private async UniTask DownloadFileWithProgressAsync(string url, string destinationPath, CancellationToken ct)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
            };

            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromMinutes(3);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ChillWithYou-KugouMusic/" + KugouIds.PluginVersion);

                using (HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long? len = resp.Content.Headers.ContentLength;
                    if (len.HasValue && len.Value > 0)
                    {
                        TotalBytes = len.Value;
                    }

                    using (Stream remote = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var local = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buf = new byte[81920];
                        long received = 0;
                        int read;
                        while ((read = await remote.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await local.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
                            received += read;
                            DownloadedBytes = received;
                            if (TotalBytes > 0)
                            {
                                DownloadProgress = Mathf.Clamp01((float)received / TotalBytes);
                            }
                        }
                    }
                }
            }
        }

        private static void StageDownloadedAsset(string downloadedFile, string assetName, string stagedDir)
        {
            if (Directory.Exists(stagedDir))
            {
                Directory.Delete(stagedDir, true);
            }

            Directory.CreateDirectory(stagedDir);

            bool isDll = assetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (isDll)
            {
                byte[] header = new byte[2];
                using (var fs = File.OpenRead(downloadedFile))
                {
                    if (fs.Length < 4096 || fs.Read(header, 0, 2) != 2 || header[0] != 0x4D || header[1] != 0x5A)
                    {
                        throw new InvalidDataException("下载的 DLL 文件签名无效");
                    }
                }

                string pluginsDir = Path.Combine(stagedDir, "BepInEx", "plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(downloadedFile, Path.Combine(pluginsDir, "ChillWithYou.KugouMusic.dll"), true);
                return;
            }

            using (var fs = File.OpenRead(downloadedFile))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                string stripPrefix = DetectWrapperRootFolder(zip);
                bool foundPluginDll = false;

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        continue;
                    }

                    string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    if (!string.IsNullOrEmpty(stripPrefix) && rel.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = rel.Substring(stripPrefix.Length);
                    }

                    rel = rel.TrimStart(Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(rel) || rel.Contains(".."))
                    {
                        continue;
                    }

                    // 若压缩包根目录下直接放着主 DLL，自动规范化到 BepInEx\plugins\
                    if (string.Equals(rel, "ChillWithYou.KugouMusic.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        rel = Path.Combine("BepInEx", "plugins", "ChillWithYou.KugouMusic.dll");
                    }

                    // 永不覆盖用户配置与缓存数据
                    if (rel.StartsWith("BepInEx" + Path.DirectorySeparatorChar + "config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        rel.StartsWith("BepInEx" + Path.DirectorySeparatorChar + "cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string outPath = Path.GetFullPath(Path.Combine(stagedDir, rel));
                    string stagedRootFull = Path.GetFullPath(stagedDir);
                    if (!outPath.StartsWith(stagedRootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("压缩包路径越界: " + entry.FullName);
                    }

                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using (Stream es = entry.Open())
                    using (FileStream outFs = File.Create(outPath))
                    {
                        es.CopyTo(outFs);
                    }

                    if (rel.EndsWith("ChillWithYou.KugouMusic.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        foundPluginDll = true;
                    }
                }

                if (!foundPluginDll)
                {
                    throw new InvalidDataException("更新包中未包含 ChillWithYou.KugouMusic.dll");
                }
            }
        }

        private static string DetectWrapperRootFolder(ZipArchive zip)
        {
            string commonTop = null;
            for (int i = 0; i < zip.Entries.Count; i++)
            {
                string raw = zip.Entries[i].FullName.Replace('\\', '/').TrimStart('/');
                int slash = raw.IndexOf('/');
                if (slash <= 0)
                {
                    return string.Empty;
                }

                string top = raw.Substring(0, slash);
                if (commonTop == null)
                {
                    commonTop = top;
                }
                else if (!string.Equals(commonTop, top, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }
            }

            if (string.IsNullOrEmpty(commonTop) ||
                string.Equals(commonTop, "BepInEx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commonTop, "KuGouMusicApi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commonTop, "KugouMusicMod", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return commonTop + Path.DirectorySeparatorChar;
        }

        private static GitHubReleaseInfo ParseReleaseObject(JObject obj)
        {
            string tag = obj.Value<string>("tag_name") ?? string.Empty;
            var info = new GitHubReleaseInfo
            {
                TagName = tag,
                VersionText = CleanVersionString(tag),
                Title = obj.Value<string>("name") ?? tag,
                Body = (obj.Value<string>("body") ?? string.Empty).Trim(),
                HtmlUrl = obj.Value<string>("html_url") ?? string.Empty,
                PublishedAt = obj.Value<string>("published_at") ?? string.Empty,
            };

            var assets = obj["assets"] as JArray;
            if (assets != null && assets.Count > 0)
            {
                JObject pickedZip = null;
                JObject pickedPreferredZip = null;
                JObject pickedDll = null;

                for (int i = 0; i < assets.Count; i++)
                {
                    var a = assets[i] as JObject;
                    if (a == null)
                    {
                        continue;
                    }

                    string name = a.Value<string>("name") ?? string.Empty;
                    string url = a.Value<string>("browser_download_url") ?? string.Empty;
                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        bool looksInstaller = name.IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool looksPlugin = name.IndexOf("kugou", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           name.IndexOf("plugin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           name.IndexOf("chill", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!looksInstaller && looksPlugin && pickedPreferredZip == null)
                        {
                            pickedPreferredZip = a;
                        }
                        else if (!looksInstaller && pickedZip == null)
                        {
                            pickedZip = a;
                        }
                    }
                    else if (string.Equals(name, "ChillWithYou.KugouMusic.dll", StringComparison.OrdinalIgnoreCase) ||
                             (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && pickedDll == null))
                    {
                        pickedDll = a;
                    }
                }

                JObject chosen = pickedPreferredZip ?? pickedZip ?? pickedDll;
                if (chosen != null)
                {
                    info.AssetName = chosen.Value<string>("name") ?? string.Empty;
                    info.AssetDownloadUrl = chosen.Value<string>("browser_download_url") ?? string.Empty;
                    info.AssetSize = chosen.Value<long?>("size") ?? 0L;
                    info.IsBundleZip = info.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                }
            }

            return info;
        }

        private static string CleanVersionString(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return "0.0.0";
            }

            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(1);
            }

            int dash = s.IndexOfAny(new[] { '-', '+' });
            if (dash > 0)
            {
                s = s.Substring(0, dash);
            }

            return s.Trim();
        }

        private static bool IsNewerVersion(string candidateText, string currentText)
        {
            Version c;
            Version cur;
            if (!TryParseLooseVersion(candidateText, out c) || !TryParseLooseVersion(currentText, out cur))
            {
                return !string.Equals(candidateText, currentText, StringComparison.OrdinalIgnoreCase);
            }

            return c > cur;
        }

        private static bool TryParseLooseVersion(string text, out Version version)
        {
            string clean = CleanVersionString(text);
            string[] parts = clean.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int single))
            {
                version = new Version(single, 0, 0, 0);
                return true;
            }

            if (Version.TryParse(clean, out Version parsed))
            {
                int b = parsed.Build >= 0 ? parsed.Build : 0;
                int r = parsed.Revision >= 0 ? parsed.Revision : 0;
                version = new Version(parsed.Major, parsed.Minor, b, r);
                return true;
            }

            version = new Version(0, 0, 0, 0);
            return false;
        }

        private static string ResolveGameRoot()
        {
            if (!string.IsNullOrEmpty(Paths.GameRootPath) && Directory.Exists(Paths.GameRootPath))
            {
                return Paths.GameRootPath;
            }

            if (!string.IsNullOrEmpty(Paths.BepInExRootPath))
            {
                DirectoryInfo parent = Directory.GetParent(Paths.BepInExRootPath);
                if (parent != null)
                {
                    return parent.FullName;
                }
            }

            return string.Empty;
        }

        private static void WriteFallbackApplyScript(string scriptPath, int pid, string stagedDir, string gameRoot, bool relaunchGame)
        {
            string steamDataMarker = Path.Combine(gameRoot, "Chill With You_Data", "Plugins", "x86_64", "steam_api64.dll");
            string gameExe = Path.Combine(gameRoot, "Chill With You.exe");
            bool preferSteamUri = File.Exists(steamDataMarker);

            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine("$pidToWait = " + pid);
            sb.AppendLine("$deadline = [DateTime]::UtcNow.AddSeconds(30)");
            sb.AppendLine("while ([DateTime]::UtcNow -lt $deadline) {");
            sb.AppendLine("    $p = Get-Process -Id $pidToWait -ErrorAction SilentlyContinue");
            sb.AppendLine("    if (-not $p) { break }");
            sb.AppendLine("    Start-Sleep -Milliseconds 300");
            sb.AppendLine("}");
            sb.AppendLine("Start-Sleep -Milliseconds 450");
            sb.AppendLine("$stage = '" + EscapePs(stagedDir) + "'");
            sb.AppendLine("$game = '" + EscapePs(gameRoot) + "'");
            sb.AppendLine("$files = Get-ChildItem -LiteralPath $stage -Recurse -File");
            sb.AppendLine("foreach ($f in $files) {");
            sb.AppendLine("    $rel = $f.FullName.Substring($stage.Length).TrimStart('\\', '/')");
            sb.AppendLine("    $dest = Join-Path $game $rel");
            sb.AppendLine("    if ($rel -ieq 'KuGouMusicApi\\.env' -and (Test-Path -LiteralPath $dest)) { continue }");
            sb.AppendLine("    $dir = Split-Path -Parent $dest");
            sb.AppendLine("    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }");
            sb.AppendLine("    for ($i = 0; $i -lt 12; $i++) {");
            sb.AppendLine("        try { Copy-Item -LiteralPath $f.FullName -Destination $dest -Force; break }");
            sb.AppendLine("        catch { Start-Sleep -Milliseconds 300 }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue");
            if (relaunchGame)
            {
                if (preferSteamUri)
                {
                    sb.AppendLine("try { Start-Process 'steam://rungameid/" + KugouIds.SteamAppId + "' } catch { if (Test-Path -LiteralPath '" + EscapePs(gameExe) + "') { Start-Process -FilePath '" + EscapePs(gameExe) + "' -WorkingDirectory $game } }");
                }
                else
                {
                    sb.AppendLine("if (Test-Path -LiteralPath '" + EscapePs(gameExe) + "') { Start-Process -FilePath '" + EscapePs(gameExe) + "' -WorkingDirectory $game }");
                }
            }

            File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(true));
        }

        private static string EscapePs(string s)
        {
            return (s ?? string.Empty).Replace("'", "''");
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }
        }
    }
}
