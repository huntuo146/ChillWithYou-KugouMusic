using System;
using System.IO;
using System.Windows;
using ChillWithYou.KugouMusic.Installer.Services;

namespace ChillWithYou.KugouMusic.Installer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (TryRunHeadlessApplyUpdate(e.Args))
            {
                return;
            }

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }

        private bool TryRunHeadlessApplyUpdate(string[] args)
        {
            bool applyMode = false;
            int pid = 0;
            string stageDir = string.Empty;
            string gameDir = string.Empty;
            bool relaunch = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--apply-update", StringComparison.OrdinalIgnoreCase))
                {
                    applyMode = true;
                }
                else if (string.Equals(arg, "--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out pid);
                }
                else if (string.Equals(arg, "--stage", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    stageDir = args[++i];
                }
                else if (string.Equals(arg, "--game", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    gameDir = args[++i];
                }
                else if (string.Equals(arg, "--relaunch", StringComparison.OrdinalIgnoreCase))
                {
                    relaunch = true;
                }
            }

            if (!applyMode || string.IsNullOrWhiteSpace(stageDir) || string.IsNullOrWhiteSpace(gameDir))
            {
                if (applyMode)
                {
                    Shutdown(2);
                    return true;
                }

                return false;
            }

            int code = PackageInstallService.ApplyStagedUpdateHeadless(
                pid,
                Path.GetFullPath(stageDir),
                Path.GetFullPath(gameDir),
                relaunch);

            Shutdown(code);
            return true;
        }
    }
}
