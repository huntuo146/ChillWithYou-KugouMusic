using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChillWithYou.KugouMusic.Installer.Models;

namespace ChillWithYou.KugouMusic.Installer.Services
{
    [SupportedOSPlatform("windows")]
    public static class PackageInstallService
    {
        private const string EmbeddedPayloadResource = "ChillWithYou.KugouMusic.Installer.Assets.Payload.zip";

        private static readonly string[] PortableNodeMirrors =
        {
            "https://nodejs.org/dist/v22.14.0/win-x64/node.exe",
            "https://npmmirror.com/mirrors/node/v22.14.0/win-x64/node.exe",
        };

        public static bool HasEmbeddedPayload()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            return asm.GetManifestResourceInfo(EmbeddedPayloadResource) != null;
        }

        public static async Task InstallAsync(
            GameInstallInfo game,
            InstallOptions options,
            IProgress<(double ratio, string message)> progress,
            Action<string> log,
            CancellationToken ct)
        {
            if (game == null || !game.IsValidGame)
            {
                throw new InvalidOperationException("请先选择有效的《Chill with You》游戏根目录。");
            }

            if (GameLocator.IsGameProcessRunningIn(game.RootPath))
            {
                throw new InvalidOperationException("检测到游戏正在运行，请先退出游戏后再执行安装或更新。");
            }

            string tempWork = Path.Combine(Path.GetTempPath(), "ChillWithYou_KugouInstall_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempWork);

            try
            {
                string? downloadedAssetPath = null;
                bool downloadedIsDllOnly = false;

                if (options.UseGitHubRelease)
                {
                    GitHubReleaseInfo? release = options.TargetRelease;
                    if (release == null || release.PreferredPluginAsset == null)
                    {
                        throw new InvalidOperationException("所选 GitHub Release 不包含可下载的插件包（.zip 或 .dll）。");
                    }

                    GitHubAssetInfo asset = release.PreferredPluginAsset;
                    downloadedAssetPath = Path.Combine(tempWork, asset.Name);
                    downloadedIsDllOnly = asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

                    log($"正在从 GitHub 下载 {asset.Name} ({FormatBytes(asset.Size)})...");
                    await GitHubClient.DownloadFileAsync(
                        asset.DownloadUrl,
                        downloadedAssetPath,
                        new Progress<(long received, long total)>(p =>
                        {
                            double r = p.total > 0 ? Math.Clamp((double)p.received / p.total, 0.0, 1.0) : 0.3;
                            progress.Report((0.05 + r * 0.45, $"正在下载 {asset.Name} ({Math.Round(r * 100)}%)..."));
                        }),
                        ct).ConfigureAwait(false);
                }

                // Step 1: 释放内嵌基础依赖（离线安装时直接作为完整包；GitHub 仅提供 .dll 时也先补全 BepInEx + KuGouMusicApi）
                bool extractEmbeddedBaseline = !options.UseGitHubRelease || downloadedIsDllOnly || !game.HasBepInEx || (options.InstallGatewayApi && !game.HasGatewayApi);
                if (extractEmbeddedBaseline)
                {
                    if (!HasEmbeddedPayload())
                    {
                        if (!options.UseGitHubRelease)
                        {
                            throw new InvalidOperationException("安装器未内嵌本地依赖包，请切换到「从 GitHub 拉取」安装。");
                        }
                    }
                    else
                    {
                        log(options.UseGitHubRelease ? "正在补全 BepInEx 与本地基础依赖..." : "正在解压本地完整插件包与全部依赖...");
                        progress.Report((0.52, "正在部署 BepInEx 与运行依赖..."));
                        using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPayloadResource);
                        if (resStream != null)
                        {
                            await Task.Run(() => ExtractZipToGame(
                                resStream,
                                game.RootPath,
                                options.InstallGatewayApi,
                                options.PreserveUserConfig,
                                skipPluginDll: downloadedIsDllOnly,
                                log: log), ct).ConfigureAwait(false);
                        }
                    }
                }

                // Step 2: 应用 GitHub 下载包
                if (!string.IsNullOrEmpty(downloadedAssetPath) && File.Exists(downloadedAssetPath))
                {
                    if (downloadedIsDllOnly)
                    {
                        progress.Report((0.72, "正在校验并安装新版插件 DLL..."));
                        VerifyPeHeader(downloadedAssetPath);
                        string pluginsDir = Path.Combine(game.RootPath, "BepInEx", "plugins");
                        Directory.CreateDirectory(pluginsDir);
                        File.Copy(downloadedAssetPath, Path.Combine(pluginsDir, InstallerConstants.PluginDllName), overwrite: true);
                        log($"已安装新版 {InstallerConstants.PluginDllName}");
                    }
                    else
                    {
                        progress.Report((0.68, "正在解包并校验 GitHub 更新包..."));
                        using FileStream fs = File.OpenRead(downloadedAssetPath);
                        await Task.Run(() => ExtractZipToGame(
                            fs,
                            game.RootPath,
                            options.InstallGatewayApi,
                            options.PreserveUserConfig,
                            skipPluginDll: false,
                            log: log), ct).ConfigureAwait(false);
                        log("已完成 GitHub 完整包解包部署");
                    }
                }

                // Step 3: 生成缺失的默认配置与独立设备 .env（绝不覆盖现有用户数据）
                progress.Report((0.80, "正在检查运行配置与环境..."));
                EnsureCleanDefaultBepInExCfg(game.RootPath, log);
                if (options.InstallGatewayApi)
                {
                    EnsureCleanGatewayEnv(game.RootPath, log);
                }

                // 同步 GitHub 仓库设置供游戏内 UI 一键检更使用
                string slug = GitHubClient.NormalizeRepoSlug(options.GitHubRepo);
                if (!string.IsNullOrEmpty(slug))
                {
                    string cfgDir = Path.Combine(game.RootPath, "BepInEx", "config", "KugouMusic");
                    Directory.CreateDirectory(cfgDir);
                    File.WriteAllText(Path.Combine(cfgDir, "github_repo.txt"), slug, Encoding.UTF8);
                    log($"已同步 GitHub 更新源: {slug}");
                }

                // Step 4: 按需下载便携 Node.js 运行时
                string currentNode = GameLocator.FindNodeExecutable(game.RootPath);
                if (options.InstallGatewayApi && options.AutoDownloadPortableNode && string.IsNullOrEmpty(currentNode))
                {
                    log("本机未检测到 Node.js，正在下载便携版 node.exe 至网关目录...");
                    string targetNode = Path.Combine(game.RootPath, "KuGouMusicApi", "node.exe");
                    await DownloadPortableNodeAsync(
                        targetNode,
                        new Progress<(double r, string msg)>(p => progress.Report((0.82 + p.r * 0.14, p.msg))),
                        log,
                        ct).ConfigureAwait(false);
                }

                // Step 5: 可选复制安装器到游戏目录（供游戏内重启直接调起更新或日后一键管理）
                if (options.KeepInstallerInGameDir)
                {
                    string? selfPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath))
                    {
                        string destInstaller = Path.Combine(game.RootPath, "ChillWithYou-KugouMusic-Installer.exe");
                        if (!string.Equals(Path.GetFullPath(selfPath), Path.GetFullPath(destInstaller), StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Copy(selfPath, destInstaller, overwrite: true);
                                log("已在游戏根目录保存管理程序 ChillWithYou-KugouMusic-Installer.exe");
                            }
                            catch (Exception ex)
                            {
                                log("保存安装器副本已跳过: " + ex.Message);
                            }
                        }
                    }
                }

                progress.Report((1.0, "安装完成！启动游戏即可体验酷狗音乐联动"));
                log("全部文件安装与校验完毕。");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempWork))
                    {
                        Directory.Delete(tempWork, true);
                    }
                }
                catch
                {
                }
            }
        }

        public static async Task DownloadPortableNodeAsync(
            string targetNodeExe,
            IProgress<(double ratio, string message)> progress,
            Action<string> log,
            CancellationToken ct)
        {
            string tmp = targetNodeExe + ".downloading";
            Directory.CreateDirectory(Path.GetDirectoryName(targetNodeExe)!);

            Exception? lastError = null;
            foreach (string mirrorUrl in PortableNodeMirrors)
            {
                try
                {
                    log($"尝试下载 Node.js: {mirrorUrl}");
                    await GitHubClient.DownloadFileAsync(
                        mirrorUrl,
                        tmp,
                        new Progress<(long received, long total)>(p =>
                        {
                            double r = p.total > 0 ? Math.Clamp((double)p.received / p.total, 0.0, 1.0) : 0.2;
                            string mb = (p.received / 1048576.0).ToString("0.0");
                            string tot = p.total > 0 ? (" / " + (p.total / 1048576.0).ToString("0.0") + " MB") : " MB";
                            progress.Report((r, $"正在下载便携 Node.js ({mb}{tot})..."));
                        }),
                        ct).ConfigureAwait(false);

                    VerifyPeHeader(tmp, minBytes: 10 * 1024 * 1024);
                    if (File.Exists(targetNodeExe))
                    {
                        File.Delete(targetNodeExe);
                    }

                    File.Move(tmp, targetNodeExe);
                    log("便携版 Node.js 下载并校验完成！");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    log("该镜像下载失败，切换下一节点: " + ex.Message);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }

            throw new InvalidOperationException("便携版 Node.js 下载失败，网关仍可手动安装 Node.js v18+ 后使用。最后错误：" + lastError?.Message);
        }

        public static Task UninstallAsync(GameInstallInfo game, UninstallOptions options, Action<string> log)
        {
            if (game == null || !game.IsValidGame)
            {
                throw new InvalidOperationException("请先选择有效的游戏根目录。");
            }

            if (GameLocator.IsGameProcessRunningIn(game.RootPath))
            {
                throw new InvalidOperationException("检测到游戏正在运行，请先退出游戏后再执行卸载。");
            }

            return Task.Run(() =>
            {
                string pluginDll = Path.Combine(game.RootPath, "BepInEx", "plugins", InstallerConstants.PluginDllName);
                if (File.Exists(pluginDll))
                {
                    File.Delete(pluginDll);
                    log($"已移除插件 {InstallerConstants.PluginDllName}");
                }

                if (options.RemoveUserDataAndConfig)
                {
                    TryDeleteDir(Path.Combine(game.RootPath, "BepInEx", "cache", "kugou"), log, "酷狗音频与封面缓存");
                    TryDeleteDir(Path.Combine(game.RootPath, "BepInEx", "config", "KugouMusic"), log, "酷狗歌单与账号存储");
                    string cfg = Path.Combine(game.RootPath, "BepInEx", "config", "chillwithyou.kugoumusic.cfg");
                    if (File.Exists(cfg))
                    {
                        File.Delete(cfg);
                        log("已移除插件配置文件 chillwithyou.kugoumusic.cfg");
                    }
                }

                if (options.RemoveGatewayApi)
                {
                    TryDeleteDir(Path.Combine(game.RootPath, "KuGouMusicApi"), log, "本地网关 KuGouMusicApi");
                    string bat = Path.Combine(game.RootPath, "启动酷狗网关.bat");
                    if (File.Exists(bat))
                    {
                        File.Delete(bat);
                        log("已移除 启动酷狗网关.bat");
                    }
                }

                if (options.RemoveBepInExCore)
                {
                    foreach (string rootFile in new[] { "winhttp.dll", ".doorstop_version", "doorstop_config.ini", "changelog.txt" })
                    {
                        string p = Path.Combine(game.RootPath, rootFile);
                        if (File.Exists(p))
                        {
                            File.Delete(p);
                            log($"已移除 {rootFile}");
                        }
                    }

                    TryDeleteDir(Path.Combine(game.RootPath, "BepInEx"), log, "BepInEx 框架目录");
                }

                log("卸载完成。");
            });
        }

        /// <summary>
        /// 无界面静默热替换入口：供游戏内点「立即重启并应用更新」拉起，等待游戏进程释放锁后覆盖文件并重启游戏。
        /// </summary>
        public static int ApplyStagedUpdateHeadless(int waitPid, string stageDir, string gameDir, bool relaunch)
        {
            try
            {
                if (waitPid > 0)
                {
                    try
                    {
                        using Process proc = Process.GetProcessById(waitPid);
                        proc.WaitForExit(35000);
                    }
                    catch
                    {
                    }

                    Thread.Sleep(500);
                }

                if (!Directory.Exists(stageDir) || !Directory.Exists(gameDir))
                {
                    return 2;
                }

                string selfFull = string.Empty;
                try { selfFull = Path.GetFullPath(Environment.ProcessPath ?? string.Empty); } catch { }

                foreach (string srcFile in Directory.GetFiles(stageDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(stageDir, srcFile);
                    string destFile = Path.Combine(gameDir, rel);

                    if (string.Equals(rel, Path.Combine("KuGouMusicApi", ".env"), StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(destFile))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(selfFull) &&
                        string.Equals(Path.GetFullPath(destFile), selfFull, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        try
                        {
                            File.Copy(srcFile, destFile, overwrite: true);
                            break;
                        }
                        catch when (attempt < 19)
                        {
                            Thread.Sleep(300);
                        }
                    }
                }

                try
                {
                    Directory.Delete(stageDir, recursive: true);
                }
                catch
                {
                }

                if (relaunch)
                {
                    GameInstallInfo info = GameLocator.InspectDirectory(gameDir, "自动更新重启");
                    if (info.IsValidGame)
                    {
                        GameLocator.LaunchGame(info);
                    }
                }

                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static void ExtractZipToGame(
            Stream zipStream,
            string gameRoot,
            bool installGateway,
            bool preserveUserConfig,
            bool skipPluginDll,
            Action<string> log)
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            string stripPrefix = DetectWrapperRoot(zip);
            string gameRootFull = Path.GetFullPath(gameRoot);
            int copied = 0;

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

                if (string.Equals(rel, InstallerConstants.PluginDllName, StringComparison.OrdinalIgnoreCase))
                {
                    rel = Path.Combine("BepInEx", "plugins", InstallerConstants.PluginDllName);
                }

                if (skipPluginDll && rel.EndsWith(InstallerConstants.PluginDllName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!installGateway &&
                    (rel.StartsWith("KuGouMusicApi" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rel, "启动酷狗网关.bat", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (rel.StartsWith(Path.Combine("BepInEx", "cache") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    rel.EndsWith("LogOutput.log", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (preserveUserConfig &&
                    rel.StartsWith(Path.Combine("BepInEx", "config") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string dest = Path.GetFullPath(Path.Combine(gameRootFull, rel));
                if (!dest.StartsWith(gameRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (preserveUserConfig &&
                    string.Equals(rel, Path.Combine("KuGouMusicApi", ".env"), StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(dest))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using Stream src = entry.Open();
                using FileStream dst = File.Create(dest);
                src.CopyTo(dst);
                copied++;
            }

            log($"已写入 {copied} 个文件到游戏目录");
        }

        private static string DetectWrapperRoot(ZipArchive zip)
        {
            string? commonTop = null;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string raw = entry.FullName.Replace('\\', '/').TrimStart('/');
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
                string.Equals(commonTop, "KuGouMusicApi", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return commonTop + Path.DirectorySeparatorChar;
        }

        private static void EnsureCleanDefaultBepInExCfg(string gameRoot, Action<string> log)
        {
            string cfgDir = Path.Combine(gameRoot, "BepInEx", "config");
            Directory.CreateDirectory(cfgDir);
            string bepCfg = Path.Combine(cfgDir, "BepInEx.cfg");
            if (File.Exists(bepCfg))
            {
                return;
            }

            const string defaultCfg = """
                [Caching]
                EnableAssemblyCache = true

                [Chainloader]
                HideManagerGameObject = true

                [Logging]
                UnityLogListening = true
                LogConsoleToUnityLog = false

                [Logging.Console]
                Enabled = false

                [Logging.Disk]
                WriteUnityLog = false
                AppendLog = false
                Enabled = true
                LogLevels = Fatal, Error, Warning, Message, Info

                [Preloader]
                ApplyRuntimePatches = true
                HarmonyBackend = auto
                DumpAssemblies = false
                """;
            File.WriteAllText(bepCfg, defaultCfg, Encoding.UTF8);
            log("已生成初始 BepInEx.cfg（默认隐藏调试控制台并启用 HideManagerGameObject）");
        }

        private static void EnsureCleanGatewayEnv(string gameRoot, Action<string> log)
        {
            string apiDir = Path.Combine(gameRoot, "KuGouMusicApi");
            if (!Directory.Exists(apiDir))
            {
                return;
            }

            string envPath = Path.Combine(apiDir, ".env");
            if (File.Exists(envPath))
            {
                return;
            }

            string guid = Guid.NewGuid().ToString("D");
            string dev = "CWY" + Guid.NewGuid().ToString("N").Substring(0, 7).ToUpperInvariant();
            string content =
                "platform=lite\n" +
                "HOST=127.0.0.1\n" +
                "PORT=3000\n" +
                $"KUGOU_API_GUID={guid}\n" +
                $"KUGOU_API_DEV={dev}\n" +
                "KUGOU_API_MAC=02:00:00:00:00:00\n";
            File.WriteAllText(envPath, content, new UTF8Encoding(false));
            log("已为本地 KuGouMusicApi 生成独立设备配置 .env");
        }

        private static void VerifyPeHeader(string path, long minBytes = 4096)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < minBytes)
            {
                throw new InvalidDataException($"下载文件体积异常 ({fi.Length} bytes)");
            }

            byte[] hdr = new byte[2];
            using FileStream fs = File.OpenRead(path);
            if (fs.Read(hdr, 0, 2) != 2 || hdr[0] != 0x4D || hdr[1] != 0x5A)
            {
                throw new InvalidDataException("文件头校验失败，不是有效的 Windows 可执行/DLL 文件。");
            }
        }

        private static void TryDeleteDir(string path, Action<string> log, string label)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                log($"已清理 {label}");
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "未知大小";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }
    }
}
