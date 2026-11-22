using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using ChillWithYou.KugouMusic.Installer.Models;
using Microsoft.Win32;

namespace ChillWithYou.KugouMusic.Installer.Services
{
    [SupportedOSPlatform("windows")]
    public static class GameLocator
    {
        private static readonly Regex VdfPathRegex = new Regex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AcfInstallDirRegex = new Regex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<GameInstallInfo> ScanAllCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<GameInstallInfo>();

            void TryAdd(string? rawPath, string source)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    return;
                }

                string normalized;
                try
                {
                    normalized = Path.GetFullPath(rawPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    return;
                }

                if (!seen.Add(normalized))
                {
                    return;
                }

                GameInstallInfo info = InspectDirectory(normalized, source);
                if (info.IsValidGame)
                {
                    results.Add(info);
                }
            }

            // 1. 当前安装器所在目录及其父目录（例如直接放在游戏目录里运行）
            string exeDir = AppContext.BaseDirectory;
            TryAdd(exeDir, "当前目录");
            DirectoryInfo? exeParent = Directory.GetParent(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (exeParent != null)
            {
                TryAdd(exeParent.FullName, "上级目录");
            }

            TryAdd(Environment.CurrentDirectory, "工作目录");

            // 2. 正在运行的游戏进程
            foreach (string procDir in FindRunningGameDirectories())
            {
                TryAdd(procDir, "运行中的游戏进程");
            }

            // 3. Steam 注册表 + libraryfolders.vdf + appmanifest_3548580.acf
            foreach (string steamRoot in FindSteamRootDirectories())
            {
                foreach (string libraryRoot in ParseSteamLibraries(steamRoot))
                {
                    string acf = Path.Combine(libraryRoot, "steamapps", "appmanifest_" + InstallerConstants.SteamAppId + ".acf");
                    if (File.Exists(acf))
                    {
                        string? installDir = TryReadAcfInstallDir(acf);
                        if (!string.IsNullOrEmpty(installDir))
                        {
                            TryAdd(Path.Combine(libraryRoot, "steamapps", "common", installDir), "Steam 库清单 (" + InstallerConstants.SteamAppId + ")");
                        }
                    }

                    TryAdd(Path.Combine(libraryRoot, "steamapps", "common", InstallerConstants.SteamFolderName), "Steam 库目录");
                }
            }

            // 4. 全盘常见 Steam / 游戏目录兜底扫描
            string[] commonSubPaths =
            {
                @"SteamLibrary\steamapps\common\" + InstallerConstants.SteamFolderName,
                @"Steam\steamapps\common\" + InstallerConstants.SteamFolderName,
                @"Program Files (x86)\Steam\steamapps\common\" + InstallerConstants.SteamFolderName,
                @"Program Files\Steam\steamapps\common\" + InstallerConstants.SteamFolderName,
                @"Games\" + InstallerConstants.SteamFolderName,
                @"Games\Steam\steamapps\common\" + InstallerConstants.SteamFolderName,
                InstallerConstants.SteamFolderName,
            };

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable))
                {
                    continue;
                }

                foreach (string sub in commonSubPaths)
                {
                    TryAdd(Path.Combine(drive.RootDirectory.FullName, sub), "磁盘自动扫描 (" + drive.Name.TrimEnd('\\') + ")");
                }
            }

            return results;
        }

        public static GameInstallInfo InspectDirectory(string? dirPath, string source = "手动选择")
        {
            var info = new GameInstallInfo
            {
                RootPath = dirPath?.Trim() ?? string.Empty,
                ScanSource = source,
            };

            if (string.IsNullOrWhiteSpace(info.RootPath) || !Directory.Exists(info.RootPath))
            {
                return info;
            }

            string gameExe = Path.Combine(info.RootPath, InstallerConstants.GameExeName);
            string gameData = Path.Combine(info.RootPath, InstallerConstants.GameDataDirName);
            info.IsValidGame = File.Exists(gameExe) && Directory.Exists(gameData);
            if (!info.IsValidGame)
            {
                return info;
            }

            info.IsSteamInstall = File.Exists(Path.Combine(gameData, "Plugins", "x86_64", "steam_api64.dll")) ||
                                  IsSteamManifestPresent(info.RootPath);

            string bepDll = Path.Combine(info.RootPath, "BepInEx", "core", "BepInEx.dll");
            string winhttp = Path.Combine(info.RootPath, "winhttp.dll");
            string doorstopIni = Path.Combine(info.RootPath, "doorstop_config.ini");
            info.HasBepInEx = File.Exists(bepDll) && File.Exists(winhttp) && File.Exists(doorstopIni);

            string pluginDll = Path.Combine(info.RootPath, "BepInEx", "plugins", InstallerConstants.PluginDllName);
            info.PluginInstalled = File.Exists(pluginDll);
            if (info.PluginInstalled)
            {
                info.InstalledPluginVersion = ReadPluginVersion(pluginDll);
            }

            info.HasGatewayApi = File.Exists(Path.Combine(info.RootPath, "KuGouMusicApi", "app.js")) &&
                                 Directory.Exists(Path.Combine(info.RootPath, "KuGouMusicApi", "node_modules"));

            info.NodeExecutablePath = FindNodeExecutable(info.RootPath);
            info.IsGameRunning = IsGameProcessRunningIn(info.RootPath);
            info.SavedGitHubRepo = ReadSavedGitHubRepo(info.RootPath);

            return info;
        }

        public static string FindNodeExecutable(string? gameRoot = null)
        {
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                string[] bundled =
                {
                    Path.Combine(gameRoot, "KuGouMusicApi", "node.exe"),
                    Path.Combine(gameRoot, "runtime", "node.exe"),
                    Path.Combine(gameRoot, "node", "node.exe"),
                    Path.Combine(gameRoot, "node.exe"),
                };
                foreach (string b in bundled)
                {
                    if (File.Exists(b))
                    {
                        return b;
                    }
                }
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string raw in pathEnv.Split(Path.PathSeparator))
            {
                string dir = raw.Trim().Trim('"');
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                string candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string[] fallbacks =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
            };

            foreach (string f in fallbacks)
            {
                if (!string.IsNullOrEmpty(f) && File.Exists(f))
                {
                    return f;
                }
            }

            return string.Empty;
        }

        public static bool IsGameProcessRunningIn(string? gameRoot)
        {
            foreach (Process p in Process.GetProcessesByName("Chill With You"))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(gameRoot))
                    {
                        return true;
                    }

                    string? exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe) &&
                        string.Equals(Path.GetDirectoryName(exe)?.TrimEnd('\\'), gameRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
                finally
                {
                    p.Dispose();
                }
            }

            return false;
        }

        public static void TerminateGameProcesses(string? gameRoot)
        {
            foreach (Process p in Process.GetProcessesByName("Chill With You"))
            {
                try
                {
                    string? exe = null;
                    try { exe = p.MainModule?.FileName; } catch { }
                    if (string.IsNullOrWhiteSpace(gameRoot) || string.IsNullOrEmpty(exe) ||
                        string.Equals(Path.GetDirectoryName(exe)?.TrimEnd('\\'), gameRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(2500))
                        {
                            p.Kill(true);
                            p.WaitForExit(3000);
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        public static void LaunchGame(GameInstallInfo info)
        {
            if (info == null || !info.IsValidGame)
            {
                return;
            }

            if (info.IsSteamInstall && IsSteamClientAvailable())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "steam://rungameid/" + InstallerConstants.SteamAppId,
                        UseShellExecute = true,
                    });
                    return;
                }
                catch
                {
                }
            }

            string exe = Path.Combine(info.RootPath, InstallerConstants.GameExeName);
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = info.RootPath,
                    UseShellExecute = true,
                });
            }
        }

        private static bool IsSteamManifestPresent(string gameRoot)
        {
            try
            {
                DirectoryInfo? common = Directory.GetParent(gameRoot.TrimEnd('\\'));
                DirectoryInfo? steamapps = common?.Parent;
                if (steamapps == null)
                {
                    return false;
                }

                return File.Exists(Path.Combine(steamapps.FullName, "appmanifest_" + InstallerConstants.SteamAppId + ".acf"));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSteamClientAvailable()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? exe = key?.GetValue("SteamExe") as string;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe.Replace('/', '\\')))
                {
                    return true;
                }
            }
            catch
            {
            }

            return Process.GetProcessesByName("steam").Length > 0;
        }

        private static string ReadPluginVersion(string dllPath)
        {
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(dllPath);
                string? ver = !string.IsNullOrWhiteSpace(fvi.FileVersion) ? fvi.FileVersion : fvi.ProductVersion;
                if (!string.IsNullOrWhiteSpace(ver))
                {
                    int plus = ver.IndexOf('+');
                    if (plus > 0)
                    {
                        ver = ver.Substring(0, plus);
                    }

                    return ver.Trim();
                }
            }
            catch
            {
            }

            return "未知版本";
        }

        private static string ReadSavedGitHubRepo(string gameRoot)
        {
            try
            {
                string repoFile = Path.Combine(gameRoot, "BepInEx", "config", "KugouMusic", "github_repo.txt");
                if (File.Exists(repoFile))
                {
                    string v = File.ReadAllText(repoFile, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(v))
                    {
                        return v;
                    }
                }

                string cfgFile = Path.Combine(gameRoot, "BepInEx", "config", "chillwithyou.kugoumusic.cfg");
                if (File.Exists(cfgFile))
                {
                    foreach (string line in File.ReadLines(cfgFile, Encoding.UTF8))
                    {
                        if (line.StartsWith("GitHubRepo", StringComparison.OrdinalIgnoreCase))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0)
                            {
                                string v = line.Substring(eq + 1).Trim();
                                if (!string.IsNullOrEmpty(v))
                                {
                                    return v;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return InstallerConstants.DefaultGitHubRepo;
        }

        private static IEnumerable<string> FindRunningGameDirectories()
        {
            var list = new List<string>();
            foreach (Process p in Process.GetProcessesByName("Chill With You"))
            {
                try
                {
                    string? exe = p.MainModule?.FileName;
                    string? dir = !string.IsNullOrEmpty(exe) ? Path.GetDirectoryName(exe) : null;
                    if (!string.IsNullOrEmpty(dir))
                    {
                        list.Add(dir);
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }

            return list;
        }

        private static IEnumerable<string> FindSteamRootDirectories()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryRegDir(RegistryKey hive, string subKey, string valueName, bool isFile = false)
            {
                try
                {
                    using RegistryKey? key = hive.OpenSubKey(subKey);
                    string? raw = key?.GetValue(valueName) as string;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        string p = raw.Replace('/', '\\').Trim();
                        if (isFile)
                        {
                            p = Path.GetDirectoryName(p) ?? string.Empty;
                        }

                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                        {
                            roots.Add(p);
                        }
                    }
                }
                catch
                {
                }
            }

            TryRegDir(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            TryRegDir(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe", isFile: true);
            TryRegDir(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            TryRegDir(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

            string defaultPf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            if (Directory.Exists(defaultPf))
            {
                roots.Add(defaultPf);
            }

            return roots;
        }

        private static IEnumerable<string> ParseSteamLibraries(string steamRoot)
        {
            var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };

            string[] vdfCandidates =
            {
                Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
            };

            foreach (string vdf in vdfCandidates)
            {
                if (!File.Exists(vdf))
                {
                    continue;
                }

                try
                {
                    string text = File.ReadAllText(vdf, Encoding.UTF8);
                    foreach (Match m in VdfPathRegex.Matches(text))
                    {
                        string path = m.Groups[1].Value.Replace(@"\\", @"\").Trim();
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            libs.Add(path);
                        }
                    }
                }
                catch
                {
                }
            }

            return libs;
        }

        private static string? TryReadAcfInstallDir(string acfPath)
        {
            try
            {
                string text = File.ReadAllText(acfPath, Encoding.UTF8);
                Match m = AcfInstallDirRegex.Match(text);
                if (m.Success)
                {
                    return m.Groups[1].Value.Trim();
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
