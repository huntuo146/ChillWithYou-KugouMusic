using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using BepInEx;
using ChillWithYou.KugouMusic.Models;
using Cysharp.Threading.Tasks;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>本地网关运行状态。</summary>
    internal enum GatewayState
    {
        Remote = 0,
        Stopped = 1,
        Starting = 2,
        RunningOwned = 3,
        RunningExternal = 4,
    }

    /// <summary>
    /// 托管本地 KuGouMusicApi（Node.js）网关进程：
    /// - 游戏启动或切到 ExternalGateway 时自动检查端口并在后台静默拉起 `node app.js`；
    /// - 若端口已被手动启动的 `启动酷狗网关.bat` 占用，则直接复用且退出时不关闭外部窗口；
    /// - 仅关闭由插件自己启动的网关进程，避免残留后台进程。
    /// </summary>
    internal sealed class GatewayProcessService
    {
        private readonly object _gate = new object();
        private Process _process;
        private bool _ownedByPlugin;
        private int _targetPort = 3000;
        private DateTime _startedUtc = DateTime.MinValue;
        private bool _warnedMissing;
        private DateTime _lastStatusProbeUtc = DateTime.MinValue;
        private bool _cachedListening;

        public bool IsOwnedRunning
        {
            get
            {
                lock (_gate)
                {
                    return _ownedByPlugin && _process != null && !_process.HasExited;
                }
            }
        }

        public bool EnsureStarted(bool force = false)
        {
            if (!force && (Plugin.AutoStartGateway == null || !Plugin.AutoStartGateway.Value))
            {
                return false;
            }

            if (Plugin.GetBackendMode() != KugouBackendMode.ExternalGateway)
            {
                return false;
            }

            string baseUrl = Plugin.ExternalApiBaseUrl != null ? Plugin.ExternalApiBaseUrl.Value : "http://127.0.0.1:3000";
            string host;
            int port;
            if (!TryParseLocalEndpoint(baseUrl, out host, out port))
            {
                return false;
            }

            lock (_gate)
            {
                _targetPort = port;

                if (_process != null && !_process.HasExited)
                {
                    return true;
                }

                DisposeProcessLocked();

                if (IsPortListening(port))
                {
                    _ownedByPlugin = false;
                    LogInfo("[Gateway] 检测到本地端口 " + port + " 已在运行（复用现有网关）");
                    return true;
                }

                string apiDir = ResolveApiDirectory();
                if (string.IsNullOrEmpty(apiDir))
                {
                    if (!_warnedMissing)
                    {
                        _warnedMissing = true;
                        LogWarn("[Gateway] 未找到 KuGouMusicApi\\app.js，跳过自动启动");
                    }

                    return false;
                }

                string nodePath = ResolveNodeExecutable();
                if (string.IsNullOrEmpty(nodePath))
                {
                    if (!_warnedMissing)
                    {
                        _warnedMissing = true;
                        LogWarn("[Gateway] 未找到 node.exe，请确认已安装 Node.js");
                    }

                    return false;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = nodePath,
                        Arguments = "app.js",
                        WorkingDirectory = apiDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };

                    psi.EnvironmentVariables["PORT"] = port.ToString();
                    psi.EnvironmentVariables["HOST"] = host;

                    var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    proc.OutputDataReceived += OnStdout;
                    proc.ErrorDataReceived += OnStderr;

                    if (!proc.Start())
                    {
                        proc.Dispose();
                        LogWarn("[Gateway] 无法启动 KuGouMusicApi 进程");
                        return false;
                    }

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    _process = proc;
                    _ownedByPlugin = true;
                    _startedUtc = DateTime.UtcNow;
                    _warnedMissing = false;

                    LogInfo("[Gateway] 已随游戏启动本地 KuGouMusicApi 网关（PID=" + proc.Id + ", http://" + host + ":" + port + "）");
                    return true;
                }
                catch (Exception ex)
                {
                    LogWarn("[Gateway] 启动本地网关失败: " + ex.Message);
                    return false;
                }
            }
        }

        public bool Restart()
        {
            StopIfOwned();
            return EnsureStarted(force: true);
        }

        public void StopIfOwned()
        {
            lock (_gate)
            {
                if (!_ownedByPlugin || _process == null)
                {
                    return;
                }

                try
                {
                    if (!_process.HasExited)
                    {
                        int pid = _process.Id;
                        _process.Kill();
                        _process.WaitForExit(1500);
                        LogInfo("[Gateway] 已关闭随游戏启动的本地网关（PID=" + pid + "）");
                    }
                }
                catch (Exception ex)
                {
                    LogWarn("[Gateway] 关闭本地网关失败: " + ex.Message);
                }
                finally
                {
                    DisposeProcessLocked();
                    _ownedByPlugin = false;
                }
            }
        }

        /// <summary>
        /// 游戏刚启动的几秒内，如果插件刚拉起 node 还未完成监听绑定，稍作等待再发 HTTP 请求，避免首帧并发抢跑失败。
        /// </summary>
        public async UniTask WaitUntilReadyAsync(string requestUrl, CancellationToken ct)
        {
            string host;
            int port;
            if (!TryParseLocalEndpoint(requestUrl, out host, out port))
            {
                return;
            }

            if (IsPortListening(port))
            {
                return;
            }

            bool ownedStarting;
            lock (_gate)
            {
                ownedStarting = _ownedByPlugin &&
                                _process != null &&
                                !_process.HasExited &&
                                (DateTime.UtcNow - _startedUtc).TotalSeconds < 12.0;
            }

            if (!ownedStarting)
            {
                return;
            }

            for (int i = 0; i < 75; i++)
            {
                if (ct.IsCancellationRequested || IsPortListening(port))
                {
                    return;
                }

                lock (_gate)
                {
                    if (_process == null || _process.HasExited)
                    {
                        return;
                    }
                }

                await UniTask.Delay(120, ignoreTimeScale: true, cancellationToken: ct);
            }
        }

        public GatewayState GetState()
        {
            string baseUrl = Plugin.ExternalApiBaseUrl != null ? Plugin.ExternalApiBaseUrl.Value : "http://127.0.0.1:3000";
            string host;
            int port;
            if (!TryParseLocalEndpoint(baseUrl, out host, out port))
            {
                return GatewayState.Remote;
            }

            DateTime now = DateTime.UtcNow;
            if ((now - _lastStatusProbeUtc).TotalMilliseconds >= 500.0)
            {
                _cachedListening = IsPortListening(port);
                _lastStatusProbeUtc = now;
            }

            if (IsOwnedRunning)
            {
                return _cachedListening ? GatewayState.RunningOwned : GatewayState.Starting;
            }

            return _cachedListening ? GatewayState.RunningExternal : GatewayState.Stopped;
        }

        public string GetStatusText()
        {
            string baseUrl = Plugin.ExternalApiBaseUrl != null ? Plugin.ExternalApiBaseUrl.Value : "http://127.0.0.1:3000";
            string host;
            int port;
            if (!TryParseLocalEndpoint(baseUrl, out host, out port))
            {
                return "远程网关（不托管本地进程）";
            }

            switch (GetState())
            {
                case GatewayState.RunningOwned:
                    return "运行中（随游戏启动，端口 " + port + "）";
                case GatewayState.Starting:
                    return "启动中（端口 " + port + "）";
                case GatewayState.RunningExternal:
                    return "运行中（外部启动，端口 " + port + "）";
                default:
                    return "未运行（端口 " + port + "）";
            }
        }

        public static bool IsPortListening(int port)
        {
            try
            {
                IPEndPoint[] endpoints = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                for (int i = 0; i < endpoints.Length; i++)
                {
                    if (endpoints[i].Port == port)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略网络信息枚举异常
            }

            return false;
        }

        public static bool TryParseLocalEndpoint(string url, out string host, out int port)
        {
            host = "127.0.0.1";
            port = 3000;
            if (string.IsNullOrEmpty(url))
            {
                return true;
            }

            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string h = uri.Host;
            if (!string.Equals(h, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            host = string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : h;
            port = uri.Port > 0 ? uri.Port : 3000;
            return true;
        }

        private static string ResolveApiDirectory()
        {
            string gameRoot = !string.IsNullOrEmpty(Paths.GameRootPath)
                ? Paths.GameRootPath
                : Path.GetDirectoryName(Paths.BepInExRootPath);

            if (string.IsNullOrEmpty(gameRoot))
            {
                return null;
            }

            string apiDir = Path.Combine(gameRoot, "KuGouMusicApi");
            return File.Exists(Path.Combine(apiDir, "app.js")) ? apiDir : null;
        }

        private static string ResolveNodeExecutable()
        {
            string apiDir = ResolveApiDirectory();
            if (!string.IsNullOrEmpty(apiDir))
            {
                string bundledInApi = Path.Combine(apiDir, "node.exe");
                if (File.Exists(bundledInApi))
                {
                    return bundledInApi;
                }

                string gameRoot = Path.GetDirectoryName(apiDir);
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    string[] bundled =
                    {
                        Path.Combine(gameRoot, "runtime", "node.exe"),
                        Path.Combine(gameRoot, "node", "node.exe"),
                        Path.Combine(gameRoot, "node.exe"),
                    };
                    for (int i = 0; i < bundled.Length; i++)
                    {
                        if (File.Exists(bundled[i]))
                        {
                            return bundled[i];
                        }
                    }
                }
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] dirs = pathEnv.Split(Path.PathSeparator);
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i].Trim().Trim('"');
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

            for (int i = 0; i < fallbacks.Length; i++)
            {
                if (!string.IsNullOrEmpty(fallbacks[i]) && File.Exists(fallbacks[i]))
                {
                    return fallbacks[i];
                }
            }

            return null;
        }

        private void DisposeProcessLocked()
        {
            if (_process == null)
            {
                return;
            }

            try
            {
                _process.OutputDataReceived -= OnStdout;
                _process.ErrorDataReceived -= OnStderr;
                _process.Dispose();
            }
            catch
            {
                // 清理忽略
            }

            _process = null;
        }

        private static void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            string line = e.Data.Trim();
            if (line.Length == 0)
            {
                return;
            }

            bool verbose = Plugin.EnableVerboseLog != null && Plugin.EnableVerboseLog.Value;
            if (line.IndexOf("server running", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("[ERR]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                verbose)
            {
                LogInfo("[Gateway] " + line);
            }
        }

        private static void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            LogWarn("[Gateway][stderr] " + e.Data.Trim());
        }

        private static void LogInfo(string msg)
        {
            if (Plugin.Log != null)
            {
                Plugin.Log.LogInfo(msg);
            }
        }

        private static void LogWarn(string msg)
        {
            if (Plugin.Log != null)
            {
                Plugin.Log.LogWarning(msg);
            }
        }
    }
}
