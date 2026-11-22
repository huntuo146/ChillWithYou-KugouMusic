using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace ChillWithYou.KugouMusic.Services
{
    /// <summary>统一 HTTP 响应。</summary>
    internal sealed class HttpResponseData
    {
        public bool Ok = false;
        public int StatusCode = 0;
        public string Text = string.Empty;
        public byte[] Bytes = null;
        public string FinalUrl = string.Empty;
        public string Error = string.Empty;

        /// <summary>响应头（大小写不敏感）。</summary>
        public Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 每条 Set-Cookie 单独保存（不同 Cookie 之间可能含逗号，绝不能先合并再切分）。
        /// </summary>
        public List<string> SetCookieHeaders = new List<string>();

        /// <summary>从每条 Set-Cookie 中提取 key=value（丢弃属性）。</summary>
        public string GetCookie()
        {
            var parts = new List<string>();

            if (SetCookieHeaders != null && SetCookieHeaders.Count > 0)
            {
                for (int i = 0; i < SetCookieHeaders.Count; i++)
                {
                    AppendCookiePair(SetCookieHeaders[i], parts);
                }
            }
            else
            {
                string raw;
                if (Headers.TryGetValue("Set-Cookie", out raw))
                {
                    AppendCookiePair(raw, parts);
                }
            }

            return string.Join("; ", parts.ToArray());
        }

        private static void AppendCookiePair(string setCookie, List<string> parts)
        {
            if (string.IsNullOrEmpty(setCookie))
            {
                return;
            }

            string segment = setCookie;
            int semicolon = segment.IndexOf(';');
            if (semicolon >= 0)
            {
                segment = segment.Substring(0, semicolon);
            }

            segment = segment.Trim();
            int equals = segment.IndexOf('=');
            if (equals <= 0)
            {
                return;
            }

            string name = segment.Substring(0, equals).Trim().ToLowerInvariant();
            if (name == "expires" || name == "path" || name == "domain" ||
                name == "max-age" || name == "secure" || name == "httponly" ||
                name == "samesite" || name == "comment")
            {
                return;
            }

            parts.Add(segment);
        }

        public static HttpResponseData FromError(string message)
        {
            return new HttpResponseData { Ok = false, Error = message ?? string.Empty };
        }
    }

    /// <summary>
    /// 网络抽象层。
    /// 主实现为 HttpClient（netstandard2.1 下 GetStringAsync 无 CancellationToken 重载，
    /// 因此统一走 HttpRequestMessage + SendAsync），备用实现为 UnityWebRequest（走 Unity 原生 TLS 栈）。
    /// </summary>
    internal interface IHttpTransport
    {
        string Name { get; }

        UniTask<HttpResponseData> GetAsync(string url, IDictionary<string, string> headers, CancellationToken ct);

        void Dispose();
    }

    internal sealed class HttpClientTransport : IHttpTransport
    {
        private readonly HttpClient _client;

        public string Name
        {
            get { return "HttpClient"; }
        }

        public HttpClientTransport()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8,
            };

            _client = new HttpClient(handler);
            _client.Timeout = TimeSpan.FromSeconds(30);
            _client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }

        public async UniTask<HttpResponseData> GetAsync(
            string url,
            IDictionary<string, string> headers,
            CancellationToken ct)
        {
            var response = new HttpResponseData();

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (headers != null)
                    {
                        foreach (KeyValuePair<string, string> pair in headers)
                        {
                            if (string.IsNullOrEmpty(pair.Key))
                            {
                                continue;
                            }

                            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                        }
                    }

                    using (HttpResponseMessage result = await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false))
                    {
                        response.StatusCode = (int)result.StatusCode;
                        response.Ok = result.IsSuccessStatusCode;

                        foreach (KeyValuePair<string, IEnumerable<string>> header in result.Headers)
                        {
                            response.Headers[header.Key] = string.Join("; ", header.Value);

                            if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (string cookie in header.Value)
                                {
                                    if (!string.IsNullOrEmpty(cookie))
                                    {
                                        response.SetCookieHeaders.Add(cookie);
                                    }
                                }
                            }
                        }

                        foreach (KeyValuePair<string, IEnumerable<string>> header in result.Content.Headers)
                        {
                            response.Headers[header.Key] = string.Join("; ", header.Value);
                        }

                        byte[] bytes = await result.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        response.Bytes = bytes;
                        response.Text = bytes == null ? string.Empty : Encoding.UTF8.GetString(bytes);

                        if (result.RequestMessage != null && result.RequestMessage.RequestUri != null)
                        {
                            response.FinalUrl = result.RequestMessage.RequestUri.ToString();
                        }

                        if (!response.Ok)
                        {
                            response.Error = "HTTP " + response.StatusCode;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                response.Ok = false;
                response.Error = "cancelled";
            }
            catch (Exception ex)
            {
                response.Ok = false;
                response.Error = HttpTransportError.Format(url, ex);
            }

            return response;
        }

        public void Dispose()
        {
            try
            {
                _client.Dispose();
            }
            catch
            {
                // 释放失败不影响退出流程
            }
        }
    }

    internal sealed class UnityWebRequestTransport : IHttpTransport
    {
        public string Name
        {
            get { return "UnityWebRequest"; }
        }

        public async UniTask<HttpResponseData> GetAsync(
            string url,
            IDictionary<string, string> headers,
            CancellationToken ct)
        {
            var response = new HttpResponseData();

            // UnityWebRequest 只能在主线程创建与发送
            await UniTask.SwitchToMainThread(CancellationToken.None);

            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequest.Get(url);
                request.timeout = 30;

                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> pair in headers)
                    {
                        if (string.IsNullOrEmpty(pair.Key))
                        {
                            continue;
                        }

                        request.SetRequestHeader(pair.Key, pair.Value);
                    }
                }

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        request.Abort();
                        response.Ok = false;
                        response.Error = "cancelled";
                        return response;
                    }

                    await UniTask.Yield();
                }

                response.StatusCode = (int)request.responseCode;
                response.FinalUrl = request.url;

                Dictionary<string, string> responseHeaders = request.GetResponseHeaders();
                if (responseHeaders != null)
                {
                    foreach (KeyValuePair<string, string> header in responseHeaders)
                    {
                        response.Headers[header.Key] = header.Value;

                        if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(header.Value))
                        {
                            response.SetCookieHeaders.Add(header.Value);
                        }
                    }
                }

                bool success = request.result == UnityWebRequest.Result.Success;
                response.Ok = success;
                if (success)
                {
                    response.Bytes = request.downloadHandler != null ? request.downloadHandler.data : null;
                    response.Text = request.downloadHandler != null
                        ? request.downloadHandler.text
                        : string.Empty;
                }
                else
                {
                    response.Error = string.IsNullOrEmpty(request.error)
                        ? "UnityWebRequest failed"
                        : request.error;
                }
            }
            catch (Exception ex)
            {
                response.Ok = false;
                response.Error = HttpTransportError.Format(url, ex);
            }
            finally
            {
                if (request != null)
                {
                    request.Dispose();
                }
            }

            return response;
        }

        public void Dispose()
        {
        }
    }

    internal static class HttpTransportFactory
    {
        public static IHttpTransport Create()
        {
            bool useUnity = Plugin.UseUnityWebRequestTransport != null && Plugin.UseUnityWebRequestTransport.Value;
            if (useUnity)
            {
                Plugin.Log.LogInfo("[Http] 使用 UnityWebRequest 传输层（Unity 原生 TLS）");
                return new UnityWebRequestTransport();
            }

            Plugin.Log.LogInfo("[Http] 使用 HttpClient 传输层");
            return new HttpClientTransport();
        }
    }

    internal static class HttpTransportError
    {
        public static string Format(string url, Exception ex)
        {
            if (ex == null)
            {
                return "Unknown error";
            }

            Exception innermost = ex;
            while (innermost.InnerException != null)
            {
                innermost = innermost.InnerException;
            }

            string detail = innermost != ex
                ? ex.GetType().Name + " -> " + innermost.GetType().Name + ": " + innermost.Message
                : ex.GetType().Name + ": " + ex.Message;

            if (!string.IsNullOrEmpty(url) &&
                (url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)))
            {
                return "无法连接本地网关（请先运行「启动酷狗网关.bat」或在设置页切回 NativeDirect）[" + detail + "]";
            }

            return detail;
        }
    }
}
