using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChillWithYou.KugouMusic.Installer.Models;
using ChillWithYou.KugouMusic.Installer.Services;
using Microsoft.Win32;

namespace ChillWithYou.KugouMusic.Installer
{
    public partial class MainWindow : Window
    {
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private GameInstallInfo _currentInfo = new GameInstallInfo();
        private List<GameInstallInfo> _candidates = new List<GameInstallInfo>();
        private GitHubReleaseInfo? _latestRelease;
        private bool _suppressPathTextEvent;
        private bool _isBusy;
        private bool _noticeAccepted;

        public MainWindow()
        {
            InitializeComponent();
            TxtHeaderVersion.Text = "v" + InstallerConstants.InstallerVersion;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!PackageInstallService.HasEmbeddedPayload())
            {
                RbSourceOffline.IsEnabled = false;
                RbSourceGitHub.IsChecked = true;
                AppendLog("[提示] 当前安装器未内嵌离线包，已自动切换为从 GitHub 拉取模式。");
            }

            if (!string.IsNullOrEmpty(InstallerConstants.DefaultGitHubRepo))
            {
                TxtGitHubRepo.Text = InstallerConstants.DefaultGitHubRepo;
            }

            RunGameScan();
            UpdateSourceControls();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                _lifetimeCts.Cancel();
                _lifetimeCts.Dispose();
            }
            catch
            {
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnShowNotice_Click(object sender, RoutedEventArgs e)
        {
            PanelPreInstallNotice.Visibility = Visibility.Visible;
        }

        private void BtnAcceptNotice_Click(object sender, RoutedEventArgs e)
        {
            _noticeAccepted = true;
            PanelPreInstallNotice.Visibility = Visibility.Collapsed;
            UpdateActionButtonsEnabled();
        }

        private void BtnDeclineNotice_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RunGameScan()
        {
            _candidates = GameLocator.ScanAllCandidates();
            if (_candidates.Count > 1)
            {
                PanelMultiCandidates.Visibility = Visibility.Visible;
                CmbCandidates.Items.Clear();
                foreach (GameInstallInfo c in _candidates)
                {
                    CmbCandidates.Items.Add($"{c.RootPath}  [{c.ScanSource}]");
                }

                CmbCandidates.SelectedIndex = 0;
            }
            else
            {
                PanelMultiCandidates.Visibility = Visibility.Collapsed;
            }

            if (_candidates.Count > 0)
            {
                GameInstallInfo picked = _candidates[0];
                _suppressPathTextEvent = true;
                TxtGameDir.Text = picked.RootPath;
                _suppressPathTextEvent = false;
                ApplyInspectedInfo(picked);
                AppendLog($"自动扫描找到游戏目录: {picked.RootPath}（来源：{picked.ScanSource}）");
            }
            else
            {
                ApplyInspectedInfo(new GameInstallInfo());
                TxtScanSource.Text = "未自动检测到目录，请点击「浏览…」手动选择";
                AppendLog("未自动扫描到《Chill with You》目录，请点击右上角「浏览…」手动指定。");
            }
        }

        private void ApplyInspectedInfo(GameInstallInfo info)
        {
            _currentInfo = info;

            if (info.IsValidGame)
            {
                TxtScanSource.Text = info.ScanSource;
                SetChip(ChipGame, TxtChipGame, "游戏本体: 已就绪", ChipTone.Ok);
                SetChip(ChipBepInEx, TxtChipBepInEx, info.HasBepInEx ? "BepInEx 5: 已安装" : "BepInEx 5: 待安装", info.HasBepInEx ? ChipTone.Ok : ChipTone.Info);
                SetChip(
                    ChipPlugin,
                    TxtChipPlugin,
                    info.PluginInstalled ? $"酷狗插件: v{info.InstalledPluginVersion}" : "酷狗插件: 未安装",
                    info.PluginInstalled ? ChipTone.Ok : ChipTone.Info);
                SetChip(ChipGateway, TxtChipGateway, info.HasGatewayApi ? "本地网关: 已就绪" : "本地网关: 待安装", info.HasGatewayApi ? ChipTone.Ok : ChipTone.Info);
                SetChip(
                    ChipNode,
                    TxtChipNode,
                    info.HasNodeJs ? "Node.js: 已检测到" : "Node.js: 待补充",
                    info.HasNodeJs ? ChipTone.Ok : ChipTone.Warn);

                PanelRunningWarn.Visibility = info.IsGameRunning ? Visibility.Visible : Visibility.Collapsed;

                if (string.IsNullOrWhiteSpace(TxtGitHubRepo.Text) && !string.IsNullOrWhiteSpace(info.SavedGitHubRepo))
                {
                    TxtGitHubRepo.Text = info.SavedGitHubRepo;
                }

                if (RbSourceGitHub.IsChecked == true)
                {
                    BtnInstall.Content = info.PluginInstalled ? "从 GitHub 下载并更新" : "从 GitHub 下载并安装";
                }
                else
                {
                    BtnInstall.Content = info.PluginInstalled ? "更新 / 修复插件" : "一键安装全部依赖";
                }
            }
            else
            {
                SetChip(ChipGame, TxtChipGame, "游戏本体: 未检测到", ChipTone.Err);
                SetChip(ChipBepInEx, TxtChipBepInEx, "BepInEx 5: —", ChipTone.Muted);
                SetChip(ChipPlugin, TxtChipPlugin, "酷狗插件: —", ChipTone.Muted);
                SetChip(ChipGateway, TxtChipGateway, "本地网关: —", ChipTone.Muted);
                SetChip(ChipNode, TxtChipNode, "Node.js: —", ChipTone.Muted);
                PanelRunningWarn.Visibility = Visibility.Collapsed;
                BtnInstall.Content = "一键安装";
            }

            UpdateActionButtonsEnabled();
        }

        private enum ChipTone
        {
            Muted,
            Info,
            Ok,
            Warn,
            Err,
        }

        private static void SetChip(Border border, TextBlock label, string text, ChipTone tone)
        {
            label.Text = text;
            switch (tone)
            {
                case ChipTone.Ok:
                    border.Background = BrushFrom("#1A4CE0B3");
                    border.BorderBrush = BrushFrom("#554CE0B3");
                    label.Foreground = BrushFrom("#4CE0B3");
                    break;
                case ChipTone.Info:
                    border.Background = BrushFrom("#1A57C7FF");
                    border.BorderBrush = BrushFrom("#5057C7FF");
                    label.Foreground = BrushFrom("#8CD9FF");
                    break;
                case ChipTone.Warn:
                    border.Background = BrushFrom("#1EFFB86C");
                    border.BorderBrush = BrushFrom("#58FFB86C");
                    label.Foreground = BrushFrom("#FFB86C");
                    break;
                case ChipTone.Err:
                    border.Background = BrushFrom("#1FFF6B7A");
                    border.BorderBrush = BrushFrom("#55FF6B7A");
                    label.Foreground = BrushFrom("#FF8B98");
                    break;
                default:
                    border.Background = BrushFrom("#14192E");
                    border.BorderBrush = BrushFrom("#24CDDEFF");
                    label.Foreground = BrushFrom("#7D88A8");
                    break;
            }
        }

        private static SolidColorBrush BrushFrom(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        }

        private void TxtGameDir_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPathTextEvent || !IsLoaded)
            {
                return;
            }

            string path = TxtGameDir.Text.Trim();
            GameInstallInfo info = GameLocator.InspectDirectory(path, "手动路径");
            ApplyInspectedInfo(info);
        }

        private void CmbCandidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = CmbCandidates.SelectedIndex;
            if (idx >= 0 && idx < _candidates.Count)
            {
                _suppressPathTextEvent = true;
                TxtGameDir.Text = _candidates[idx].RootPath;
                _suppressPathTextEvent = false;
                ApplyInspectedInfo(_candidates[idx]);
            }
        }

        private void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            RunGameScan();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "请选择 Chill with You 游戏根目录（包含 Chill With You.exe）",
                Multiselect = false,
            };

            if (!string.IsNullOrWhiteSpace(TxtGameDir.Text) && Directory.Exists(TxtGameDir.Text))
            {
                dlg.InitialDirectory = TxtGameDir.Text;
            }

            if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
            {
                TxtGameDir.Text = dlg.FolderName;
                GameInstallInfo info = GameLocator.InspectDirectory(dlg.FolderName, "手动选择");
                ApplyInspectedInfo(info);
                AppendLog(info.IsValidGame
                    ? $"已选定游戏目录: {info.RootPath}"
                    : $"所选目录未找到 {InstallerConstants.GameExeName}: {dlg.FolderName}");
            }
        }

        private void BtnKillGame_Click(object sender, RoutedEventArgs e)
        {
            GameLocator.TerminateGameProcesses(_currentInfo.RootPath);
            ApplyInspectedInfo(GameLocator.InspectDirectory(TxtGameDir.Text, _currentInfo.ScanSource));
            AppendLog("已关闭正在运行的 Chill With You.exe 进程。");
        }

        private void InstallSource_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateSourceControls();
            ApplyInspectedInfo(_currentInfo);
        }

        private void UpdateSourceControls()
        {
            bool useGh = RbSourceGitHub.IsChecked == true;
            if (useGh && _latestRelease == null)
            {
                TxtProgressStatus.Text = "提示：请先填写 GitHub 仓库（如 owner/repo），点击安装或先检查更新。";
            }
        }

        private void TxtGitHubRepo_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtRepoPlaceholder.Visibility = string.IsNullOrEmpty(TxtGitHubRepo.Text) ? Visibility.Visible : Visibility.Collapsed;
            _latestRelease = null;
            PanelGitHubStatus.Visibility = Visibility.Collapsed;
        }

        private async void BtnCheckGitHub_Click(object sender, RoutedEventArgs e)
        {
            await CheckGitHubAsync();
        }

        private async Task<bool> CheckGitHubAsync()
        {
            if (_isBusy)
            {
                return false;
            }

            string rawRepo = TxtGitHubRepo.Text.Trim();
            if (string.IsNullOrEmpty(rawRepo))
            {
                TxtProgressStatus.Text = "请先在下方填写 GitHub 仓库（格式如 owner/repo 或完整链接）。";
                AppendLog("尚未填写 GitHub 仓库地址。");
                return false;
            }

            SetBusy(true, "正在连接 GitHub Releases...");
            ProgressBarMain.IsIndeterminate = true;
            try
            {
                GitHubReleaseInfo info = await GitHubClient.FetchLatestReleaseAsync(rawRepo, _lifetimeCts.Token);
                _latestRelease = info;

                bool isNewer = _currentInfo.PluginInstalled
                    ? GitHubClient.IsNewerVersion(info.VersionText, _currentInfo.InstalledPluginVersion)
                    : true;

                string assetPart = info.PreferredPluginAsset != null
                    ? $"{info.PreferredPluginAsset.Name} ({PackageInstallService.FormatBytes(info.PreferredPluginAsset.Size)})"
                    : "无可用附件";

                string tagState = _currentInfo.PluginInstalled
                    ? (isNewer ? $"发现更新 v{info.VersionText}（本地 v{_currentInfo.InstalledPluginVersion}）" : $"已是最新 v{info.VersionText}")
                    : $"最新版本 v{info.VersionText}";

                TxtReleaseHeadline.Text = $"{tagState}  ·  {assetPart}";
                TxtReleaseNotes.Text = string.IsNullOrWhiteSpace(info.Body)
                    ? "该版本未附带更新日志。"
                    : info.Body.Replace("\r", " ").Replace("\n", " · ").Trim();
                PanelGitHubStatus.Visibility = Visibility.Visible;

                TxtProgressStatus.Text = $"GitHub 检查完成：{tagState}";
                AppendLog($"GitHub 最新 Release: {info.TagName} | 附件: {assetPart}");
                return true;
            }
            catch (Exception ex)
            {
                PanelGitHubStatus.Visibility = Visibility.Collapsed;
                TxtProgressStatus.Text = "检查 GitHub 更新失败：" + ex.Message;
                AppendLog("检查 GitHub 失败: " + ex.Message);
                return false;
            }
            finally
            {
                ProgressBarMain.IsIndeterminate = false;
                SetBusy(false, TxtProgressStatus.Text);
            }
        }

        private void BtnOpenRelease_Click(object sender, RoutedEventArgs e)
        {
            string? url = _latestRelease?.HtmlUrl;
            if (string.IsNullOrEmpty(url))
            {
                string slug = GitHubClient.NormalizeRepoSlug(TxtGitHubRepo.Text);
                if (!string.IsNullOrEmpty(slug))
                {
                    url = $"https://github.com/{slug}/releases";
                }
            }

            if (!string.IsNullOrEmpty(url))
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            if (!_noticeAccepted)
            {
                PanelPreInstallNotice.Visibility = Visibility.Visible;
                return;
            }

            GameInstallInfo inspected = GameLocator.InspectDirectory(TxtGameDir.Text, _currentInfo.ScanSource);
            ApplyInspectedInfo(inspected);

            if (!inspected.IsValidGame)
            {
                TxtProgressStatus.Text = "当前目录无效，请选择包含 Chill With You.exe 的游戏根目录。";
                return;
            }

            if (inspected.IsGameRunning)
            {
                TxtProgressStatus.Text = "游戏正在运行中，请先点击上方的「关闭游戏进程」后再安装。";
                return;
            }

            bool useGitHub = RbSourceGitHub.IsChecked == true;
            if (useGitHub && _latestRelease == null)
            {
                bool ok = await CheckGitHubAsync();
                if (!ok || _latestRelease == null)
                {
                    return;
                }
            }

            PanelUninstallConfirm.Visibility = Visibility.Collapsed;
            SetBusy(true, "准备开始安装...");

            var options = new InstallOptions
            {
                UseGitHubRelease = useGitHub,
                GitHubRepo = TxtGitHubRepo.Text.Trim(),
                TargetRelease = _latestRelease,
                InstallGatewayApi = ChkInstallGateway.IsChecked == true,
                AutoDownloadPortableNode = ChkAutoNode.IsChecked == true,
                PreserveUserConfig = ChkPreserveConfig.IsChecked == true,
                KeepInstallerInGameDir = ChkKeepInstaller.IsChecked == true,
                LaunchGameAfterInstall = ChkLaunchAfter.IsChecked == true,
            };

            var progress = new Progress<(double ratio, string message)>(p =>
            {
                ProgressBarMain.Value = Math.Clamp(p.ratio * 100.0, 0.0, 100.0);
                TxtProgressStatus.Text = p.message;
                TxtProgressPercent.Text = $"{Math.Round(p.ratio * 100)}%";
            });

            try
            {
                await PackageInstallService.InstallAsync(
                    inspected,
                    options,
                    progress,
                    msg => Dispatcher.Invoke(() => AppendLog(msg)),
                    _lifetimeCts.Token);

                GameInstallInfo refreshed = GameLocator.InspectDirectory(inspected.RootPath, inspected.ScanSource);
                ApplyInspectedInfo(refreshed);

                if (options.LaunchGameAfterInstall)
                {
                    AppendLog("正在启动游戏...");
                    GameLocator.LaunchGame(refreshed);
                }
            }
            catch (OperationCanceledException)
            {
                TxtProgressStatus.Text = "操作已取消。";
                AppendLog("安装被取消。");
            }
            catch (Exception ex)
            {
                TxtProgressStatus.Text = "安装失败：" + ex.Message;
                AppendLog("安装出错: " + ex.Message);
            }
            finally
            {
                SetBusy(false, TxtProgressStatus.Text);
            }
        }

        private void BtnUninstallToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_currentInfo.IsValidGame)
            {
                return;
            }

            PanelUninstallConfirm.Visibility = PanelUninstallConfirm.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnCancelUninstall_Click(object sender, RoutedEventArgs e)
        {
            PanelUninstallConfirm.Visibility = Visibility.Collapsed;
        }

        private async void BtnConfirmUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            GameInstallInfo inspected = GameLocator.InspectDirectory(TxtGameDir.Text, _currentInfo.ScanSource);
            if (!inspected.IsValidGame)
            {
                return;
            }

            if (inspected.IsGameRunning)
            {
                TxtProgressStatus.Text = "请先关闭游戏进程后再执行卸载。";
                return;
            }

            PanelUninstallConfirm.Visibility = Visibility.Collapsed;
            SetBusy(true, "正在卸载插件...");
            ProgressBarMain.IsIndeterminate = true;

            try
            {
                var uOpt = new UninstallOptions
                {
                    RemoveGatewayApi = ChkUninstGateway.IsChecked == true,
                    RemoveBepInExCore = ChkUninstBepInEx.IsChecked == true,
                    RemoveUserDataAndConfig = ChkUninstData.IsChecked == true,
                };

                await PackageInstallService.UninstallAsync(inspected, uOpt, msg => Dispatcher.Invoke(() => AppendLog(msg)));
                GameInstallInfo refreshed = GameLocator.InspectDirectory(inspected.RootPath, inspected.ScanSource);
                ApplyInspectedInfo(refreshed);
                ProgressBarMain.Value = 100;
                TxtProgressStatus.Text = "卸载完成。";
            }
            catch (Exception ex)
            {
                TxtProgressStatus.Text = "卸载失败：" + ex.Message;
                AppendLog("卸载出错: " + ex.Message);
            }
            finally
            {
                ProgressBarMain.IsIndeterminate = false;
                SetBusy(false, TxtProgressStatus.Text);
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInfo.IsValidGame && Directory.Exists(_currentInfo.RootPath))
            {
                Process.Start(new ProcessStartInfo { FileName = _currentInfo.RootPath, UseShellExecute = true });
            }
        }

        private void BtnLaunchGame_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInfo.IsValidGame)
            {
                GameLocator.LaunchGame(_currentInfo);
                AppendLog("已触发启动游戏。");
            }
        }

        private void SetBusy(bool busy, string status)
        {
            _isBusy = busy;
            TxtProgressStatus.Text = status;
            if (!busy)
            {
                TxtProgressPercent.Text = ProgressBarMain.Value > 0 ? $"{Math.Round(ProgressBarMain.Value)}%" : string.Empty;
            }

            UpdateActionButtonsEnabled();
        }

        private void UpdateActionButtonsEnabled()
        {
            bool valid = _currentInfo.IsValidGame;
            BtnInstall.IsEnabled = !_isBusy && valid && (RbSourceOffline.IsChecked == true || !string.IsNullOrWhiteSpace(TxtGitHubRepo.Text));
            BtnCheckGitHub.IsEnabled = !_isBusy;
            BtnLaunchGame.IsEnabled = !_isBusy && valid;
            BtnOpenFolder.IsEnabled = !_isBusy && valid;
            BtnUninstallToggle.IsEnabled = !_isBusy && valid && (_currentInfo.PluginInstalled || _currentInfo.HasBepInEx || _currentInfo.HasGatewayApi);
            TxtGameDir.IsEnabled = !_isBusy;
        }

        private void AppendLog(string line)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            if (!string.IsNullOrEmpty(TxtLog.Text))
            {
                TxtLog.AppendText(Environment.NewLine);
            }

            TxtLog.AppendText($"[{stamp}] {line}");
            TxtLog.ScrollToEnd();
        }
    }
}
