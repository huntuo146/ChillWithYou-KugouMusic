using System;
using System.Collections.Generic;

namespace ChillWithYou.KugouMusic.Installer.Models
{
    public static class InstallerConstants
    {
        public const string InstallerVersion = "1.0.0";
        public const string DefaultGitHubRepo = "huntuo146/ChillWithYou-KugouMusic";
        public const string SteamAppId = "3548580";
        public const string SteamFolderName = "Chill with You Lo-Fi Story";
        public const string GameExeName = "Chill With You.exe";
        public const string GameDataDirName = "Chill With You_Data";
        public const string PluginDllName = "ChillWithYou.KugouMusic.dll";
    }

    public sealed class GameInstallInfo
    {
        public string RootPath { get; set; } = string.Empty;
        public string ScanSource { get; set; } = string.Empty;
        public bool IsValidGame { get; set; }
        public bool IsSteamInstall { get; set; }
        public bool HasBepInEx { get; set; }
        public bool PluginInstalled { get; set; }
        public string InstalledPluginVersion { get; set; } = string.Empty;
        public bool HasGatewayApi { get; set; }
        public string NodeExecutablePath { get; set; } = string.Empty;
        public bool IsGameRunning { get; set; }
        public string SavedGitHubRepo { get; set; } = string.Empty;

        public bool HasNodeJs => !string.IsNullOrEmpty(NodeExecutablePath);
    }

    public sealed class GitHubAssetInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public sealed class GitHubReleaseInfo
    {
        public string TagName { get; set; } = string.Empty;
        public string VersionText { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string PublishedAt { get; set; } = string.Empty;
        public List<GitHubAssetInfo> Assets { get; } = new List<GitHubAssetInfo>();
        public GitHubAssetInfo? PreferredPluginAsset { get; set; }
        public GitHubAssetInfo? InstallerAsset { get; set; }
    }

    public sealed class InstallOptions
    {
        public bool UseGitHubRelease { get; set; }
        public string GitHubRepo { get; set; } = string.Empty;
        public GitHubReleaseInfo? TargetRelease { get; set; }
        public bool InstallGatewayApi { get; set; } = true;
        public bool AutoDownloadPortableNode { get; set; } = true;
        public bool PreserveUserConfig { get; set; } = true;
        public bool KeepInstallerInGameDir { get; set; } = true;
        public bool LaunchGameAfterInstall { get; set; } = true;
    }

    public sealed class UninstallOptions
    {
        public bool RemoveBepInExCore { get; set; }
        public bool RemoveGatewayApi { get; set; }
        public bool RemoveUserDataAndConfig { get; set; }
    }
}
