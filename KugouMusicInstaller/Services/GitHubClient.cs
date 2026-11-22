using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChillWithYou.KugouMusic.Installer.Models;

namespace ChillWithYou.KugouMusic.Installer.Services
{
    public static class GitHubClient
    {
        private static readonly HttpClient SharedClient = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(4),
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ChillWithYou-KugouMusic-Installer/" + InstallerConstants.InstallerVersion);
            return client;
        }

        public static string NormalizeRepoSlug(string? input)
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
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo) || owner.Contains(' ') || repo.Contains(' '))
            {
                return string.Empty;
            }

            return owner + "/" + repo;
        }

        public static async Task<GitHubReleaseInfo> FetchLatestReleaseAsync(string rawRepo, CancellationToken ct)
        {
            string slug = NormalizeRepoSlug(rawRepo);
            if (string.IsNullOrEmpty(slug))
            {
                throw new ArgumentException("请输入有效的 GitHub 仓库（格式如 owner/repo 或完整链接）");
            }

            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{slug}/releases/latest");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using HttpResponseMessage resp = await SharedClient.SendAsync(req, ct).ConfigureAwait(false);
            string jsonText;

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                using HttpRequestMessage listReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{slug}/releases?per_page=5");
                listReq.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                using HttpResponseMessage listResp = await SharedClient.SendAsync(listReq, ct).ConfigureAwait(false);
                if (listResp.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException($"未找到仓库 {slug}，或该仓库尚未发布任何 GitHub Release。");
                }

                listResp.EnsureSuccessStatusCode();
                string listJson = await listResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument listDoc = JsonDocument.Parse(listJson);
                if (listDoc.RootElement.ValueKind != JsonValueKind.Array || listDoc.RootElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException($"仓库 {slug} 暂未发布任何公开 Release。");
                }

                JsonElement? firstNonDraft = null;
                foreach (JsonElement elem in listDoc.RootElement.EnumerateArray())
                {
                    bool draft = elem.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True;
                    if (!draft)
                    {
                        firstNonDraft = elem.Clone();
                        break;
                    }
                }

                if (firstNonDraft == null)
                {
                    throw new InvalidOperationException($"仓库 {slug} 暂无非草稿 Release。");
                }

                return ParseReleaseElement(firstNonDraft.Value);
            }

            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"GitHub API 返回 HTTP {(int)resp.StatusCode}: {errBody}");
            }

            jsonText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            return ParseReleaseElement(doc.RootElement);
        }

        public static async Task DownloadFileAsync(string url, string destinationPath, IProgress<(long received, long total)>? progress, CancellationToken ct)
        {
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using HttpResponseMessage resp = await SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? 0L;
            using Stream remote = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var local = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await remote.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await local.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report((received, total));
            }
        }

        public static bool IsNewerVersion(string? candidate, string? current)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                return true;
            }

            Version vc = ParseLooseVersion(candidate);
            Version vcur = ParseLooseVersion(current);
            return vc > vcur;
        }

        public static string CleanVersion(string? tag)
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

        private static Version ParseLooseVersion(string text)
        {
            string clean = CleanVersion(text);
            string[] parts = clean.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int single))
            {
                return new Version(single, 0, 0, 0);
            }

            if (Version.TryParse(clean, out Version? v) && v != null)
            {
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
            }

            return new Version(0, 0, 0, 0);
        }

        private static GitHubReleaseInfo ParseReleaseElement(JsonElement root)
        {
            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty;
            var info = new GitHubReleaseInfo
            {
                TagName = tag,
                VersionText = CleanVersion(tag),
                Name = root.TryGetProperty("name", out JsonElement n) ? (n.GetString() ?? tag) : tag,
                Body = root.TryGetProperty("body", out JsonElement b) ? (b.GetString() ?? string.Empty).Trim() : string.Empty,
                HtmlUrl = root.TryGetProperty("html_url", out JsonElement h) ? (h.GetString() ?? string.Empty) : string.Empty,
                PublishedAt = root.TryGetProperty("published_at", out JsonElement p) ? (p.GetString() ?? string.Empty) : string.Empty,
            };

            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                GitHubAssetInfo? preferredZip = null;
                GitHubAssetInfo? fallbackZip = null;
                GitHubAssetInfo? dllAsset = null;

                foreach (JsonElement a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out JsonElement an) ? (an.GetString() ?? string.Empty) : string.Empty;
                    string url = a.TryGetProperty("browser_download_url", out JsonElement au) ? (au.GetString() ?? string.Empty) : string.Empty;
                    long size = a.TryGetProperty("size", out JsonElement asz) ? asz.GetInt64() : 0L;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    var item = new GitHubAssetInfo { Name = name, DownloadUrl = url, Size = size };
                    info.Assets.Add(item);

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        info.InstallerAsset ??= item;
                    }
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        bool looksInstaller = name.IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool looksPlugin = name.IndexOf("kugou", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           name.IndexOf("plugin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           name.IndexOf("chill", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (!looksInstaller && looksPlugin && preferredZip == null)
                        {
                            preferredZip = item;
                        }
                        else if (!looksInstaller && fallbackZip == null)
                        {
                            fallbackZip = item;
                        }
                    }
                    else if (string.Equals(name, InstallerConstants.PluginDllName, StringComparison.OrdinalIgnoreCase) ||
                             (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && dllAsset == null))
                    {
                        dllAsset = item;
                    }
                }

                info.PreferredPluginAsset = preferredZip ?? fallbackZip ?? dllAsset;
            }

            return info;
        }
    }
}
