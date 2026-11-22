using System;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Pages
{
    internal sealed class AccountPage : IKugouPage
    {
        private readonly KugouUiContext _ctx;

        private string _manualCookie = string.Empty;
        private string _manualUserId = string.Empty;
        private string _manualToken = string.Empty;
        private bool _credentialsExpanded;
        private bool _revealCookie;
        private bool _revealUserId;
        private bool _revealToken;

        private Texture2D _qrTexture;
        private string _qrKey = string.Empty;
        private float _qrNextPoll;
        private bool _qrPolling;
        private KugouQrState _qrState;

        private KugouUserDetail _userDetail;
        private float _nextProfileFetchTime;
        private bool _fetchingProfile;

        public string Title { get { return "账号"; } }
        public IconCode Icon { get { return IconCode.Account; } }

        public string CachedNickname
        {
            get { return _userDetail != null ? _userDetail.Nickname : string.Empty; }
        }

        public bool CachedIsVip
        {
            get { return _userDetail != null && _userDetail.IsVip; }
        }

        public AccountPage(KugouUiContext ctx)
        {
            _ctx = ctx;
            SyncFieldsFromConfig();
        }

        public void SyncFieldsFromConfig()
        {
            _manualCookie = Plugin.Cookie != null ? Plugin.Cookie.Value : string.Empty;
            _manualUserId = Plugin.UserId != null ? Plugin.UserId.Value : string.Empty;
            _manualToken = Plugin.Token != null ? Plugin.Token.Value : string.Empty;
        }

        public void OnShow()
        {
            SyncFieldsFromConfig();
            TryFetchProfileIfNeeded();
        }

        public void Tick()
        {
            if (_qrPolling && !string.IsNullOrEmpty(_qrKey) && Time.unscaledTime >= _qrNextPoll)
            {
                _qrNextPoll = Time.unscaledTime + 2f;
                PollQrAsync().Forget();
            }
        }

        public void Draw(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Display, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x, r.y, 180f, 28f), KugouTheme.Content("账号"), false, false, false, false);
            }

            float topY = r.y + 38f;
            float leftW = 240f;
            float gap = 12f;
            float rightX = r.x + leftW + gap;
            float rightW = r.width - leftW - gap;
            float cardH = Mathf.Min(356f, r.yMax - topY - 4f);

            DrawQrCard(new Rect(r.x, topY, leftW, cardH));

            float statusCardH = 148f;
            DrawStatusCard(new Rect(rightX, topY, rightW, statusCardH));

            float credY = topY + statusCardH + 12f;
            float credMaxH = Mathf.Max(48f, r.yMax - credY - 4f);
            DrawCredentialsCard(new Rect(rightX, credY, rightW, _credentialsExpanded ? Mathf.Min(210f, credMaxH) : 46f));
        }

        private void DrawQrCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 16f, r.y + 12f, r.width - 32f, 22f), KugouTheme.Content("扫码登录"), false, false, false, false);
            }

            IKugouClient client = Plugin.Client;
            bool supports = client != null && client.SupportsAccount;
            if (!supports)
            {
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    KugouPainter.Icon(
                        new Rect(r.center.x - 18f, r.y + 92f, 36f, 36f),
                        IconCode.Info,
                        28,
                        KugouTheme.Colors.AccentSky);
                    KugouTheme.Style(TypeToken.Body, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleCenter, true)
                              .Draw(new Rect(r.x + 22f, r.y + 136f, r.width - 44f, 44f), KugouTheme.Content("当前直连模式不支持扫码\n可切换到本地网关或填写下方凭证"), false, false, false, false);
                }

                if (KugouWidgets.Button(new Rect(r.x + 32f, r.y + 196f, r.width - 64f, 32f), "切换到本地网关", ButtonKind.Primary))
                {
                    if (Plugin.BackendMode != null)
                    {
                        Plugin.BackendMode.Value = "ExternalGateway";
                        Plugin.SaveConfig();
                    }

                    Plugin.RebuildClient("账号页一键切换网关");
                    _ctx.SetStatus("已切到 ExternalGateway 本地网关", false);
                }

                return;
            }

            // 二维码展示框（180×180）
            float qrBoxSize = 180f;
            var qrFrame = new Rect(r.center.x - qrBoxSize * 0.5f, r.y + 44f, qrBoxSize, qrBoxSize);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float ringAlpha = _qrPolling && _qrState == KugouQrState.Waiting ? (0.45f + 0.50f * KugouAnim.Pulse(1.8f)) : 0.30f;
                if (_qrTexture != null)
                {
                    var ringRect = new Rect(qrFrame.x - 2f, qrFrame.y - 2f, qrFrame.width + 4f, qrFrame.height + 4f);
                    KugouPainter.RoundRect(ringRect, KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, ringAlpha), KugouTheme.Sizes.RadiusQr + 1f);
                    KugouPainter.RoundRect(qrFrame, Color.white, KugouTheme.Sizes.RadiusQr);

                    var imgRect = new Rect(qrFrame.x + 10f, qrFrame.y + 10f, qrFrame.width - 20f, qrFrame.height - 20f);
                    Color qrTint = _qrState == KugouQrState.Expired ? new Color(1f, 1f, 1f, 0.25f) : Color.white;
                    KugouPainter.Texture(imgRect, _qrTexture, 6f, qrTint);

                    if (_qrState == KugouQrState.Scanned || _qrState == KugouQrState.Confirmed || _qrState == KugouQrState.Expired)
                    {
                        KugouPainter.RoundRect(qrFrame, KugouTheme.WithAlpha(KugouTheme.Colors.WindowBg, 0.78f), KugouTheme.Sizes.RadiusQr);
                        string stateLabel = _qrState == KugouQrState.Scanned
                            ? "已扫码 · 请在手机确认"
                            : (_qrState == KugouQrState.Confirmed ? "登录成功" : "二维码已过期\n点击重新获取");
                        Color stateCol = _qrState == KugouQrState.Expired ? KugouTheme.Colors.Warning : KugouTheme.Colors.Success;
                        KugouPainter.Icon(
                            new Rect(qrFrame.center.x - 16f, qrFrame.center.y - 26f, 32f, 28f),
                            _qrState == KugouQrState.Expired ? IconCode.Refresh : IconCode.Check,
                            24,
                            stateCol);
                        KugouTheme.Style(TypeToken.BodyStrong, stateCol, TextAnchor.MiddleCenter, true)
                                  .Draw(new Rect(qrFrame.x + 12f, qrFrame.center.y + 2f, qrFrame.width - 24f, 40f), KugouTheme.Content(stateLabel), false, false, false, false);
                    }
                }
                else
                {
                    KugouPainter.RoundRect(qrFrame, KugouTheme.Colors.InputBg, KugouTheme.Sizes.RadiusQr);
                    KugouPainter.Stroke(qrFrame, KugouTheme.Colors.StrokeStrong, KugouTheme.Sizes.RadiusQr, 1f);
                    KugouPainter.Icon(new Rect(qrFrame.center.x - 18f, qrFrame.center.y - 30f, 36f, 32f), IconCode.Account, 24, KugouTheme.Colors.TextTertiary);
                    KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleCenter, true)
                              .Draw(new Rect(qrFrame.x + 18f, qrFrame.center.y + 6f, qrFrame.width - 36f, 38f), KugouTheme.Content("使用酷狗 App 扫码\n点此立即获取二维码"), false, false, false, false);
                }
            }

            if ((_qrTexture == null || _qrState == KugouQrState.Expired) && GUI.Button(qrFrame, GUIContent.none, GUIStyle.none))
            {
                RequestQr();
            }

            // 状态提示行
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                Color dotColor = ResolveQrDotColor();
                KugouWidgets.StatusDot(new Rect(r.x + 18f, qrFrame.yMax + 15f, 7f, 7f), dotColor, _qrPolling);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 31f, qrFrame.yMax + 9f, r.width - 44f, 20f), KugouTheme.Content(ResolveQrCaption()), false, false, false, false);
            }

            float btnY = qrFrame.yMax + 38f;
            float halfW = (r.width - 40f) * 0.5f;
            if (KugouWidgets.Button(new Rect(r.x + 16f, btnY, halfW, 30f), _qrTexture == null ? "获取二维码" : "刷新", ButtonKind.Primary))
            {
                RequestQr();
            }

            if (KugouWidgets.Button(new Rect(r.x + 24f + halfW, btnY, halfW, 30f), "停止", ButtonKind.Secondary, _qrPolling))
            {
                _qrPolling = false;
                _ctx.SetStatus("已停止二维码轮询", false);
            }
        }

        private void DrawStatusCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);

                bool loggedIn = Plugin.IsLoggedIn();
                string uid = Plugin.UserId != null ? Plugin.UserId.Value : string.Empty;
                string nick = !string.IsNullOrEmpty(CachedNickname)
                    ? CachedNickname
                    : (loggedIn ? "已登录账号" : "游客模式");

                var avatarRect = new Rect(r.x + 16f, r.y + 16f, 44f, 44f);
                if (loggedIn)
                {
                    KugouPainter.GradientRect(avatarRect, KugouTextures.BrandGradient, 22f, Color.white);
                    KugouTheme.Style(TypeToken.Title, Color.white, TextAnchor.MiddleCenter)
                              .Draw(avatarRect, KugouTheme.Content(KugouWidgets.FirstMonogram(nick)), false, false, false, false);
                }
                else
                {
                    KugouPainter.Circle(avatarRect, KugouTheme.Colors.DisabledFill);
                    KugouPainter.Icon(avatarRect, IconCode.Account, 18, KugouTheme.Colors.TextTertiary);
                }

                float nickW = CachedIsVip ? Mathf.Max(80f, r.width - 212f) : Mathf.Max(100f, r.width - 162f);
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(avatarRect.xMax + 12f, r.y + 16f, nickW, 22f), KugouTheme.Content(nick), false, false, false, false);

                string subUid = loggedIn && !string.IsNullOrEmpty(uid)
                    ? "UID " + KugouWidgets.MaskUserId(uid)
                    : "未绑定酷狗身份凭证";
                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(avatarRect.xMax + 12f, r.y + 38f, Mathf.Max(100f, r.width - 104f), 18f), KugouTheme.Content(subUid), false, false, false, false);

                if (loggedIn)
                {
                    KugouWidgets.Badge(
                        new Rect(r.xMax - 80f, r.y + 18f, 66f, 18f),
                        "已登录",
                        KugouTheme.Colors.BadgeActiveText,
                        KugouTheme.Colors.BadgeActiveBg,
                        KugouTheme.Colors.BadgeActiveBorder,
                        true);
                }
                else
                {
                    KugouWidgets.Badge(
                        new Rect(r.xMax - 68f, r.y + 18f, 54f, 18f),
                        "游客",
                        KugouTheme.Colors.TextTertiary,
                        KugouTheme.Colors.InputBg,
                        KugouTheme.Colors.Divider);
                }

                if (CachedIsVip)
                {
                    KugouWidgets.Badge(
                        new Rect(r.xMax - 126f, r.y + 18f, 40f, 18f),
                        "VIP",
                        KugouTheme.Colors.BadgeVipText,
                        KugouTheme.Colors.BadgeVipBg,
                        KugouTheme.Colors.BadgeVipBorder);
                }

                string backendName = Plugin.Client != null ? Plugin.Client.DisplayName : "未初始化";
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 16f, r.y + 72f, r.width - 32f, 20f), KugouTheme.Content("当前接口：" + backendName), false, false, false, false);
            }

            float btnY = r.yMax - 44f;
            bool canVip = Plugin.Client != null && Plugin.Client.SupportsAccount && Plugin.IsLoggedIn();
            if (KugouWidgets.Button(new Rect(r.x + 16f, btnY, 152f, 30f), "领取概念版 VIP", ButtonKind.VipGold, canVip, IconCode.Sparkle))
            {
                ClaimYouthVip();
            }

            if (KugouWidgets.DangerConfirmButton(new Rect(r.xMax - 102f, btnY, 86f, 30f), "acc_logout".GetHashCode(), "退出登录", "确认退出"))
            {
                Logout();
            }
        }

        private void DrawCredentialsCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
            }

            var header = new Rect(r.x + 14f, r.y + 8f, r.width - 28f, 30f);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.Icon(
                    new Rect(header.x, header.y + 7f, 16f, 16f),
                    _credentialsExpanded ? IconCode.ChevronDown : IconCode.ChevronUp,
                    12,
                    KugouTheme.Colors.TextSecondary);
                KugouTheme.Style(TypeToken.BodyStrong, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(header.x + 20f, header.y, 240f, 30f), KugouTheme.Content("手动凭证（任意后端可用）"), false, false, false, false);
            }

            if (GUI.Button(header, GUIContent.none, GUIStyle.none))
            {
                _credentialsExpanded = !_credentialsExpanded;
            }

            if (!_credentialsExpanded)
            {
                return;
            }

            float y = header.yMax + 6f;
            float labelW = 54f;
            float eyeW = 28f;
            float fieldW = r.width - 28f - labelW - eyeW - 10f;

            DrawMaskedCredRow("Cookie", "cred_ck", ref _manualCookie, ref _revealCookie, r.x + 14f, y, labelW, fieldW);
            y += 38f;
            DrawMaskedCredRow("UserId", "cred_uid", ref _manualUserId, ref _revealUserId, r.x + 14f, y, labelW, fieldW);
            y += 38f;
            DrawMaskedCredRow("Token", "cred_tk", ref _manualToken, ref _revealToken, r.x + 14f, y, labelW, fieldW);
            y += 38f;

            if (KugouWidgets.Button(new Rect(r.xMax - 110f, y, 96f, 30f), "保存凭证", ButtonKind.Primary))
            {
                SaveManualCredentials();
            }
        }

        private static void DrawMaskedCredRow(
            string label,
            string key,
            ref string value,
            ref bool revealed,
            float x,
            float y,
            float labelW,
            float fieldW)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(x, y, labelW, 30f), KugouTheme.Content(label), false, false, false, false);
            }

            bool unused;
            value = KugouWidgets.TextField(
                new Rect(x + labelW, y, fieldW, 30f),
                key,
                value,
                new TextFieldOptions
                {
                    Placeholder = "填入 " + label,
                    AllowClear = true,
                    AllowPaste = true,
                    Masked = true,
                    RevealPlaintext = revealed,
                    Monospace = true,
                },
                out unused);

            if (KugouWidgets.IconButton(new Rect(x + labelW + fieldW + 6f, y + 1f, 28f, 28f), revealed ? IconCode.EyeOpen : IconCode.EyeClose, revealed, true, 13))
            {
                revealed = !revealed;
            }
        }

        private Color ResolveQrDotColor()
        {
            if (_qrTexture == null) return KugouTheme.Colors.TextTertiary;
            switch (_qrState)
            {
                case KugouQrState.Confirmed:
                case KugouQrState.Scanned: return KugouTheme.Colors.Success;
                case KugouQrState.Expired:
                case KugouQrState.Failed: return KugouTheme.Colors.Warning;
                default: return _qrPolling ? KugouTheme.Colors.AccentSky : KugouTheme.Colors.TextTertiary;
            }
        }

        private string ResolveQrCaption()
        {
            if (_qrTexture == null) return "未生成二维码";
            switch (_qrState)
            {
                case KugouQrState.Scanned: return "已扫码，请在手机端确认";
                case KugouQrState.Confirmed: return "已完成授权登录";
                case KugouQrState.Expired: return "二维码已过期，请刷新";
                case KugouQrState.Failed: return "轮询异常，请重试";
                default: return _qrPolling ? "等待扫码 · 每 2s 自动检查" : "已暂停扫码检查";
            }
        }

        private void TryFetchProfileIfNeeded()
        {
            if (_fetchingProfile || !Plugin.IsLoggedIn() || Plugin.Client == null || !Plugin.Client.SupportsAccount)
            {
                return;
            }

            if (Time.unscaledTime < _nextProfileFetchTime)
            {
                return;
            }

            _nextProfileFetchTime = Time.unscaledTime + 60f;
            FetchProfileAsync().Forget();
        }

        private async UniTaskVoid FetchProfileAsync()
        {
            _fetchingProfile = true;
            try
            {
                KugouUserDetail detail = await Plugin.Client.GetUserDetailAsync(CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);
                if (detail != null && detail.Status == KugouApiStatus.Ok)
                {
                    _userDetail = detail;
                }
            }
            catch
            {
                // 非关键元信息失败静默
            }
            finally
            {
                _fetchingProfile = false;
            }
        }

        private void RequestQr()
        {
            if (Plugin.Client == null)
            {
                _ctx.SetStatus("接口客户端未初始化", true);
                return;
            }

            RequestQrAsync().Forget();
        }

        private async UniTaskVoid RequestQrAsync()
        {
            try
            {
                _ctx.SetStatus("正在生成登录二维码…", false);
                KugouQrLoginSession session = await Plugin.Client.CreateQrKeyAsync(CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);

                if (session == null || session.Status != KugouApiStatus.Ok)
                {
                    _ctx.SetStatus("二维码获取失败: " + (session != null ? session.Message : "无响应"), true);
                    return;
                }

                _qrKey = session.Key;
                _qrState = KugouQrState.Waiting;
                SetQrTexture(session.QrImageBase64);
                _qrPolling = true;
                _qrNextPoll = Time.unscaledTime + 1.5f;
                _ctx.SetStatus("请使用酷狗 App 扫码（每 2 秒自动检查）", false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("二维码异常: " + ex.Message, true);
            }
        }

        private async UniTaskVoid PollQrAsync()
        {
            if (!_qrPolling || string.IsNullOrEmpty(_qrKey) || Plugin.Client == null)
            {
                return;
            }

            try
            {
                KugouQrCheckResult result = await Plugin.Client.CheckQrStatusAsync(_qrKey, CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);

                if (result == null)
                {
                    return;
                }

                _qrState = result.State;
                switch (result.State)
                {
                    case KugouQrState.Confirmed:
                        _qrPolling = false;
                        Plugin.ApplyCredentials(result.UserId, result.Token, result.Cookie);
                        SyncFieldsFromConfig();
                        _nextProfileFetchTime = 0f;
                        TryFetchProfileIfNeeded();
                        _ctx.SetStatus("登录成功" + (string.IsNullOrEmpty(result.Nickname) ? string.Empty : "：" + result.Nickname), false);
                        break;

                    case KugouQrState.Expired:
                        _qrPolling = false;
                        _ctx.SetStatus("二维码已过期，请点击刷新", true);
                        break;

                    case KugouQrState.Scanned:
                        _ctx.SetStatus("已扫码，请在手机端确认", false);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[UI] 二维码轮询失败: " + ex.Message);
                }
            }
        }

        private void SetQrTexture(string base64)
        {
            try
            {
                if (_qrTexture != null)
                {
                    UnityEngine.Object.Destroy(_qrTexture);
                    _qrTexture = null;
                }

                if (string.IsNullOrEmpty(base64))
                {
                    return;
                }

                byte[] bytes = Convert.FromBase64String(base64);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                if (tex.LoadImage(bytes))
                {
                    _qrTexture = tex;
                }
                else
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("[UI] 二维码解码失败: " + ex.Message);
                }
            }
        }

        private void SaveManualCredentials()
        {
            try
            {
                Plugin.ApplyCredentials(_manualUserId, _manualToken, _manualCookie);
                _userDetail = null;
                _nextProfileFetchTime = 0f;
                TryFetchProfileIfNeeded();
                _ctx.SetStatus("凭证已保存并生效", false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("保存失败: " + ex.Message, true);
            }
        }

        private void Logout()
        {
            try
            {
                if (Plugin.Cookie != null) Plugin.Cookie.Value = string.Empty;
                if (Plugin.UserId != null) Plugin.UserId.Value = string.Empty;
                if (Plugin.Token != null) Plugin.Token.Value = string.Empty;

                SyncFieldsFromConfig();
                _qrPolling = false;
                _qrKey = string.Empty;
                _userDetail = null;

                Plugin.SaveConfig();
                Plugin.RebuildClient("退出登录");
                _ctx.SetStatus("已退出登录", false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("退出失败: " + ex.Message, true);
            }
        }

        private void ClaimYouthVip()
        {
            ClaimYouthVipAsync().Forget();
        }

        private async UniTaskVoid ClaimYouthVipAsync()
        {
            try
            {
                IKugouClient client = Plugin.Client;
                if (client == null || !client.SupportsAccount)
                {
                    _ctx.SetStatus("当前后端不支持领取概念版 VIP", true);
                    return;
                }

                _ctx.SetStatus("正在提交概念版 VIP 领取…", false);
                KugouApiStatus status = await client.ClaimYouthVipAsync(CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);

                _ctx.SetStatus(
                    status == KugouApiStatus.Ok ? "VIP 领取请求已提交（以酷狗 App 为准）" : "领取失败: " + status,
                    status != KugouApiStatus.Ok);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("领取异常: " + ex.Message, true);
            }
        }

        public void Dispose()
        {
            _qrPolling = false;
            if (_qrTexture != null)
            {
                UnityEngine.Object.Destroy(_qrTexture);
                _qrTexture = null;
            }
        }
    }
}
