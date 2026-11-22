using System;
using System.Collections.Generic;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.NowPlaying;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Pages
{
    internal sealed class PlaylistPage : IKugouPage
    {
        private readonly KugouUiContext _ctx;
        private readonly List<KugouPlaylistSummary> _accountPlaylists = new List<KugouPlaylistSummary>();
        private readonly List<int> _detailFilteredIndices = new List<int>();

        private string _importInput = string.Empty;
        private bool _importing;
        private bool _syncingAccount;
        private bool _accountExpanded;
        private Vector2 _scrollPos;
        private float _nextAutoRefreshTime;

        // ---- 歌单详情视图 ----
        private string _viewingPlaylistId = string.Empty;
        private string _detailFilter = string.Empty;
        private string _cachedFilterKey = string.Empty;
        private int _cachedTrackCount = -1;
        private Vector2 _detailScrollPos;

        public string Title { get { return "歌单"; } }
        public IconCode Icon { get { return IconCode.Playlist; } }

        public PlaylistPage(KugouUiContext ctx)
        {
            _ctx = ctx;
        }

        public void OnShow()
        {
            _ctx.RefreshSavedPlaylists();
        }

        public void Tick()
        {
            if (Time.unscaledTime >= _nextAutoRefreshTime)
            {
                _nextAutoRefreshTime = Time.unscaledTime + 3f;
                _ctx.RefreshSavedPlaylists();
            }
        }

        public void Draw(Rect r)
        {
            if (!string.IsNullOrEmpty(_viewingPlaylistId))
            {
                KugouPlaylistRecord target = FindSavedPlaylist(_viewingPlaylistId);
                if (target != null)
                {
                    DrawPlaylistDetail(r, target);
                    return;
                }

                _viewingPlaylistId = string.Empty;
            }

            DrawPlaylistOverview(r);
        }

        // ================= 歌单总览网格 =================

        private void DrawPlaylistOverview(Rect r)
        {
            bool kugou = Plugin.Switch != null && Plugin.Switch.IsKugouMode;
            string countText = CurrentTrackCount();
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                GUIStyle titleSt = KugouTheme.Style(TypeToken.Display, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft);
                titleSt.Draw(new Rect(r.x, r.y, 180f, 28f), KugouTheme.Content("歌单"), false, false, false, false);

                string meta = "当前曲目 " + countText + " · " + (kugou ? "酷狗音源" : "官方曲库");
                GUIStyle metaSt = KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight);
                metaSt.Draw(new Rect(r.xMax - 240f, r.y + 4f, 240f, 22f), KugouTheme.Content(meta), false, false, false, false);
            }

            var importCard = new Rect(r.x, r.y + 36f, r.width, 74f);
            DrawImportCard(importCard);

            var scrollView = new Rect(r.x, importCard.yMax + 14f, r.width, Mathf.Max(80f, r.yMax - importCard.yMax - 14f));
            KugouPlaylistRecord[] saved = _ctx.GetSavedPlaylists();

            float cardW = (scrollView.width - 10f - 12f) * 0.5f;
            float cardH = 74f;
            int rows = saved.Length == 0 ? 1 : (saved.Length + 1) / 2;
            float gridH = saved.Length == 0 ? 96f : rows * cardH + Mathf.Max(0, rows - 1) * 10f;

            float accountHeaderH = 34f;
            float accountListH = _accountExpanded
                ? Mathf.Min(196f, Mathf.Max(46f, _accountPlaylists.Count * 46f + 6f))
                : 0f;

            float totalH = 28f + gridH + 16f + accountHeaderH + accountListH + 12f;
            var contentRect = new Rect(0f, 0f, scrollView.width - 10f, Mathf.Max(scrollView.height - 2f, totalH));

            _scrollPos = KugouWidgets.BeginScroll(scrollView, _scrollPos, contentRect);
            float y = 0f;

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(0f, y, 160f, 22f), KugouTheme.Content("我的歌单 · " + saved.Length), false, false, false, false);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight)
                          .Draw(new Rect(contentRect.width - 220f, y + 2f, 220f, 18f), KugouTheme.Content("点击卡片查看歌单内全部歌曲"), false, false, false, false);
            }

            y += 28f;

            if (saved.Length == 0)
            {
                DrawEmptyPlaylistCard(new Rect(0f, y, contentRect.width, 90f));
                y += 96f;
            }
            else
            {
                string activeId = Plugin.ActivePlaylistId != null ? Plugin.ActivePlaylistId.Value : string.Empty;
                for (int i = 0; i < saved.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    var cr = new Rect(col * (cardW + 12f), y + row * (cardH + 10f), cardW, cardH);
                    DrawPlaylistCard(cr, saved[i], string.Equals(saved[i].SpecialId, activeId, StringComparison.Ordinal), kugou);
                }

                y += gridH;
            }

            y += 14f;
            KugouPainter.Divider(new Rect(0f, y, contentRect.width, 1f), 0f);
            y += 10f;

            DrawAccountSection(new Rect(0f, y, contentRect.width, accountHeaderH + accountListH));

            KugouWidgets.EndScroll();
        }

        private void DrawImportCard(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, false, false);
            }

            float innerPad = 12f;
            float btnW = 104f;
            float fieldW = r.width - innerPad * 2f - btnW - 8f;

            bool submit;
            _importInput = KugouWidgets.TextField(
                new Rect(r.x + innerPad, r.y + 10f, fieldW, 32f),
                "playlist_import",
                _importInput,
                new TextFieldOptions
                {
                    Placeholder = "粘贴分享短链 / 网页链接 / 酷狗码 / 纯数字 ID…",
                    LeadingIcon = IconCode.Link,
                    AllowClear = true,
                    AllowPaste = true,
                },
                out submit);

            string btnText = _importing ? "解析中…" : "导入并播放";
            if ((KugouWidgets.Button(new Rect(r.x + innerPad + fieldW + 8f, r.y + 10f, btnW, 32f), btnText, ButtonKind.Primary, !_importing) || submit)
                && !_importing)
            {
                StartImport(_importInput);
            }

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(
                              new Rect(r.x + innerPad + 2f, r.y + 46f, r.width - innerPad * 2f, 20f),
                              KugouTheme.Content("支持 t1.kugou.com / 酷狗网页链接 / 酷狗码 / 数字 ID · 仅存本地 JSON，绝不修改游戏存档"),
                              false, false, false, false);
            }
        }

        private void DrawPlaylistCard(Rect r, KugouPlaylistRecord record, bool isActiveId, bool isKugouMode)
        {
            bool isActivePlaying = isActiveId && isKugouMode;
            bool hover = KugouWidgets.IsHovered(r);

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(r, hover, isActivePlaying);
            }

            var coverRect = new Rect(r.x + 11f, r.y + 11f, 52f, 52f);
            KugouTrack firstTrack = FindFirstCoverCandidate(record.Tracks);
            string plName = string.IsNullOrEmpty(record.Name) ? "未命名歌单" : record.Name;
            KugouWidgets.DrawCover(
                coverRect,
                _ctx.Covers,
                firstTrack != null ? firstTrack.CoverUrl : string.Empty,
                10f,
                plName + record.SpecialId,
                KugouWidgets.FirstMonogram(plName),
                false,
                0f,
                firstTrack,
                240);

            // 悬停右上操作钮：切换 / 刷新；右下：确认删除
            bool showActions = hover;
            var useRect = new Rect(r.xMax - 82f, r.y + 8f, 24f, 24f);
            var refreshRect = new Rect(r.xMax - 56f, r.y + 8f, 24f, 24f);
            var delHitRect = new Rect(r.xMax - 54f, r.y + 42f, 46f, 22f);

            bool actionClicked = false;
            if (showActions)
            {
                if (KugouWidgets.IconButton(useRect, IconCode.Play, isActivePlaying, true, 11))
                {
                    actionClicked = true;
                    _ctx.SwitchPlaylist(record.SpecialId);
                }

                if (KugouWidgets.IconButton(refreshRect, IconCode.Refresh, false, !_importing, 11))
                {
                    actionClicked = true;
                    StartImport(record.SpecialId);
                }

                int delKey = ("pl_del_" + record.SpecialId).GetHashCode();
                if (KugouWidgets.DangerConfirmButton(delHitRect, delKey, "删除", "确认?"))
                {
                    actionClicked = true;
                    DeletePlaylist(record.SpecialId);
                }

                if (Event.current != null && Event.current.type == EventType.MouseDown &&
                    (useRect.Contains(Event.current.mousePosition) ||
                     refreshRect.Contains(Event.current.mousePosition) ||
                     delHitRect.Contains(Event.current.mousePosition)))
                {
                    actionClicked = true;
                }
            }

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float textX = coverRect.xMax + 11f;
                float topRightReserve = showActions ? 86f : 10f;
                float botRightReserve = showActions ? 60f : 10f;

                KugouTheme.Style(TypeToken.BodyStrong, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 11f, Mathf.Max(40f, r.xMax - textX - topRightReserve), 20f), KugouTheme.Content(plName), false, false, false, false);

                int songCount = record.Tracks != null ? record.Tracks.Count : 0;
                string author = string.IsNullOrEmpty(record.Author) ? "酷狗" : record.Author;
                string sub = songCount + " 首 · " + author + " · " + FormatRelativeTime(record.UpdatedAtUnix);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 32f, Mathf.Max(40f, r.xMax - textX - botRightReserve), 18f), KugouTheme.Content(sub), false, false, false, false);

                if (isActivePlaying)
                {
                    KugouWidgets.Badge(
                        new Rect(textX, r.y + 51f, 80f, 18f),
                        "使用中",
                        KugouTheme.Colors.BadgeActiveText,
                        KugouTheme.Colors.BadgeActiveBg,
                        KugouTheme.Colors.BadgeActiveBorder,
                        dot: true);
                }
                else if (isActiveId)
                {
                    KugouWidgets.Badge(
                        new Rect(textX, r.y + 51f, 80f, 18f),
                        "上次使用",
                        KugouTheme.Colors.TextTertiary,
                        KugouTheme.Colors.InputBg,
                        KugouTheme.Colors.Divider);
                }
            }

            if (!actionClicked && GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                OpenPlaylistDetail(record);
            }
        }

        // ================= 歌单内全部歌曲详情视图 =================

        private void OpenPlaylistDetail(KugouPlaylistRecord record)
        {
            _viewingPlaylistId = record.SpecialId;
            _detailFilter = string.Empty;
            _cachedFilterKey = string.Empty;
            _cachedTrackCount = -1;
            _detailScrollPos = Vector2.zero;
        }

        private void DrawPlaylistDetail(Rect r, KugouPlaylistRecord record)
        {
            string activeId = Plugin.ActivePlaylistId != null ? Plugin.ActivePlaylistId.Value : string.Empty;
            bool isKugou = Plugin.Switch != null && Plugin.Switch.IsKugouMode;
            bool isCurrentPlaylist = isKugou && string.Equals(activeId, record.SpecialId, StringComparison.Ordinal);

            // 顶部返回与操作栏
            if (KugouWidgets.Button(new Rect(r.x, r.y, 108f, 28f), "返回歌单", ButtonKind.Ghost, true, IconCode.Prev))
            {
                _viewingPlaylistId = string.Empty;
                return;
            }

            if (isCurrentPlaylist)
            {
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    KugouWidgets.Badge(
                        new Rect(r.xMax - 170f, r.y + 5f, 74f, 18f),
                        "使用中",
                        KugouTheme.Colors.BadgeActiveText,
                        KugouTheme.Colors.BadgeActiveBg,
                        KugouTheme.Colors.BadgeActiveBorder,
                        dot: true);
                }
            }
            else if (KugouWidgets.Button(new Rect(r.xMax - 206f, r.y, 114f, 28f), "切换到此歌单", ButtonKind.Primary))
            {
                _ctx.SwitchPlaylist(record.SpecialId);
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 84f, r.y, 84f, 28f), _importing ? "刷新中" : "刷新", ButtonKind.Secondary, !_importing, IconCode.Refresh))
            {
                StartImport(record.SpecialId);
            }

            // 歌单头信息卡片 + 歌单内筛选框
            var headerCard = new Rect(r.x, r.y + 36f, r.width, 68f);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(headerCard, false, isCurrentPlaylist);
            }

            var coverRect = new Rect(headerCard.x + 12f, headerCard.y + 10f, 48f, 48f);
            KugouTrack firstTrack = FindFirstCoverCandidate(record.Tracks);
            string plName = string.IsNullOrEmpty(record.Name) ? "未命名歌单" : record.Name;
            KugouWidgets.DrawCover(
                coverRect,
                _ctx.Covers,
                firstTrack != null ? firstTrack.CoverUrl : string.Empty,
                10f,
                plName + record.SpecialId,
                KugouWidgets.FirstMonogram(plName),
                false,
                0f,
                firstTrack,
                240);

            float filterW = 200f;
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                float infoX = coverRect.xMax + 12f;
                float infoMaxW = headerCard.width - 72f - filterW - 24f;
                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(infoX, headerCard.y + 10f, infoMaxW, 24f), KugouTheme.Content(plName), false, false, false, false);

                int totalSongs = record.Tracks != null ? record.Tracks.Count : 0;
                string author = string.IsNullOrEmpty(record.Author) ? "酷狗" : record.Author;
                string meta = "共 " + totalSongs + " 首 · " + author + " · " + FormatRelativeTime(record.UpdatedAtUnix);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(infoX, headerCard.y + 35f, infoMaxW, 20f), KugouTheme.Content(meta), false, false, false, false);
            }

            bool dummySubmit;
            _detailFilter = KugouWidgets.TextField(
                new Rect(headerCard.xMax - filterW - 12f, headerCard.y + 18f, filterW, 32f),
                "pl_detail_filter",
                _detailFilter,
                new TextFieldOptions
                {
                    Placeholder = "筛选歌名 / 歌手…",
                    LeadingIcon = IconCode.Search,
                    AllowClear = true,
                    AllowPaste = false,
                },
                out dummySubmit);

            // 全部歌曲虚拟化滚动列表
            RebuildDetailFilterIfNeeded(record);
            var viewRect = new Rect(r.x, headerCard.yMax + 12f, r.width, Mathf.Max(80f, r.yMax - headerCard.yMax - 12f));

            if (_detailFilteredIndices.Count == 0)
            {
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    KugouWidgets.CardFrame(new Rect(viewRect.x, viewRect.y, viewRect.width, 84f), false, false);
                    KugouTheme.Style(TypeToken.Body, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleCenter)
                              .Draw(new Rect(viewRect.x, viewRect.y, viewRect.width, 84f),
                                    KugouTheme.Content(string.IsNullOrEmpty(_detailFilter) ? "该歌单暂无曲目，请点击右上「刷新」" : "未找到匹配「" + _detailFilter + "」的歌曲"),
                                    false, false, false, false);
                }

                return;
            }

            float rowH = 54f;
            float totalH = _detailFilteredIndices.Count * rowH;
            var contentRect = new Rect(0f, 0f, viewRect.width - 10f, Mathf.Max(viewRect.height - 2f, totalH));
            _detailScrollPos = KugouWidgets.BeginScroll(viewRect, _detailScrollPos, contentRect);

            int first = Mathf.Max(0, Mathf.FloorToInt(_detailScrollPos.y / rowH) - 1);
            int last = Mathf.Min(_detailFilteredIndices.Count - 1, Mathf.CeilToInt((_detailScrollPos.y + viewRect.height) / rowH) + 1);
            string playingUuid = ResolvePlayingUuid();

            for (int vi = first; vi <= last; vi++)
            {
                int trackIdx = _detailFilteredIndices[vi];
                KugouTrack track = record.Tracks[trackIdx];
                if (track == null)
                {
                    continue;
                }

                var rowRect = new Rect(0f, vi * rowH, contentRect.width, rowH - 4f);
                bool isPlaying = !string.IsNullOrEmpty(playingUuid) && string.Equals(playingUuid, track.GetUuid(), StringComparison.Ordinal);
                DrawDetailTrackRow(rowRect, trackIdx, track, isPlaying, record);
            }

            KugouWidgets.EndScroll();
        }

        private void DrawDetailTrackRow(Rect r, int trackIndex, KugouTrack track, bool isPlaying, KugouPlaylistRecord record)
        {
            bool hover = KugouWidgets.IsHovered(r);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                Color bg = isPlaying
                    ? KugouTheme.Colors.CardActive
                    : (hover ? KugouTheme.Colors.CardHover : KugouTheme.WithAlpha(KugouTheme.Colors.Card, 0.48f));
                KugouPainter.RoundRect(r, bg, 10f);
                if (isPlaying || hover)
                {
                    KugouPainter.Stroke(
                        r,
                        isPlaying ? KugouTheme.Colors.LavenderStroke : KugouTheme.Colors.StrokeHairline,
                        10f,
                        1f);
                }

                if (isPlaying)
                {
                    KugouPainter.EqualizerBars(new Rect(r.x + 14f, r.y + 18f, 14f, 14f), true);
                }
                else
                {
                    KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleCenter)
                              .Draw(new Rect(r.x + 6f, r.y, 30f, r.height), KugouTheme.Content((trackIndex + 1).ToString("00")), false, false, false, false);
                }

                // 歌曲封面（自动拉取并显示）
                string title = track.GetDisplayName();
                KugouWidgets.DrawCover(
                    new Rect(r.x + 40f, r.y + 7f, 36f, 36f),
                    _ctx.Covers,
                    track.CoverUrl,
                    7f,
                    track.GetUuid(),
                    KugouWidgets.FirstMonogram(title),
                    false,
                    0f,
                    track,
                    150);

                // 时长 & 付费标记
                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleRight)
                          .Draw(new Rect(r.xMax - 88f, r.y, 44f, r.height), KugouTheme.Content(NowPlayingModel.FormatSeconds(track.DurationSeconds)), false, false, false, false);

                float badgeLeft = r.xMax - 94f;
                if (track.IsPaid())
                {
                    badgeLeft -= 44f;
                    KugouWidgets.Badge(
                        new Rect(badgeLeft, r.y + 16f, 40f, 18f),
                        "付费",
                        KugouTheme.Colors.BadgePaidText,
                        KugouTheme.Colors.BadgePaidBg,
                        KugouTheme.Colors.BadgePaidBorder);
                }

                float textX = r.x + 86f;
                float textW = Mathf.Max(60f, badgeLeft - textX - 8f);
                Color titleCol = isPlaying ? KugouTheme.Colors.AccentSky : KugouTheme.Colors.TextPrimary;
                KugouTheme.Style(TypeToken.BodyStrong, titleCol, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 6f, textW, 20f), KugouTheme.Content(title), false, false, false, false);

                string sub = track.GetDisplaySinger() + (string.IsNullOrEmpty(track.AlbumName) ? string.Empty : " · " + track.AlbumName);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 26f, textW, 18f), KugouTheme.Content(sub), false, false, false, false);
            }

            var playRect = new Rect(r.xMax - 36f, r.y + 11f, 28f, 28f);
            bool clickedPlay = KugouWidgets.IconButton(playRect, IconCode.Play, isPlaying, true, 12);
            bool clickedRow = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (clickedPlay || clickedRow)
            {
                PlayTrackFromDetail(record, track);
            }
        }

        private void PlayTrackFromDetail(KugouPlaylistRecord record, KugouTrack track)
        {
            if (Plugin.Switch == null || record == null || track == null)
            {
                return;
            }

            if (Plugin.Switch.Service == null)
            {
                _ctx.SetStatus("请先进入房间场景后再点播歌曲", true);
                return;
            }

            if (Plugin.Switch.SwitchPlaylistAndPlay(record.SpecialId, record.Tracks, track))
            {
                _ctx.SetStatus("正在播放：" + track.GetDisplayName(), false);
            }
            else
            {
                _ctx.SetStatus("点播失败，请稍后重试", true);
            }
        }

        private void RebuildDetailFilterIfNeeded(KugouPlaylistRecord record)
        {
            int count = record.Tracks != null ? record.Tracks.Count : 0;
            string key = record.SpecialId + "|" + (_detailFilter ?? string.Empty).Trim();
            if (string.Equals(key, _cachedFilterKey, StringComparison.Ordinal) && _cachedTrackCount == count)
            {
                return;
            }

            _cachedFilterKey = key;
            _cachedTrackCount = count;
            _detailFilteredIndices.Clear();

            if (record.Tracks == null)
            {
                return;
            }

            string needle = (_detailFilter ?? string.Empty).Trim();
            for (int i = 0; i < record.Tracks.Count; i++)
            {
                KugouTrack t = record.Tracks[i];
                if (t == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(needle) ||
                    ContainsIgnoreCase(t.Name, needle) ||
                    ContainsIgnoreCase(t.Singer, needle) ||
                    ContainsIgnoreCase(t.AlbumName, needle))
                {
                    _detailFilteredIndices.Add(i);
                }
            }
        }

        // ================= 空态 / 账号歌单 / 工具 =================

        private void DrawEmptyPlaylistCard(Rect r)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            KugouWidgets.CardFrame(r, false, false);
            var discRect = new Rect(r.x + 22f, r.y + 21f, 48f, 48f);
            KugouPainter.RotatedTexture(discRect, KugouTextures.VinylDisc, 0f, new Color(1f, 1f, 1f, 0.78f));

            KugouTheme.Style(TypeToken.BodyStrong, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleLeft)
                      .Draw(new Rect(discRect.xMax + 16f, r.y + 22f, r.width - 100f, 22f), KugouTheme.Content("还没有收藏任何本地酷狗歌单"), false, false, false, false);

            KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                      .Draw(new Rect(discRect.xMax + 16f, r.y + 46f, r.width - 100f, 20f), KugouTheme.Content("在上方粘贴酷狗分享链接或歌单 ID 导入，或从下方同步账号个人歌单"), false, false, false, false);
        }

        private float DrawAccountSection(Rect r)
        {
            bool supportsAccount = Plugin.Client != null && Plugin.Client.SupportsAccount;
            var headerRect = new Rect(r.x, r.y, r.width, 30f);

            var toggleHit = new Rect(headerRect.x, headerRect.y, 175f, headerRect.height);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.Icon(
                    new Rect(headerRect.x, headerRect.y + 7f, 16f, 16f),
                    _accountExpanded ? IconCode.ChevronDown : IconCode.ChevronUp,
                    12,
                    KugouTheme.Colors.TextSecondary);

                KugouTheme.Style(TypeToken.Title, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(
                              new Rect(headerRect.x + 20f, headerRect.y + 2f, 155f, 26f),
                              KugouTheme.Content("账号个人歌单 · " + _accountPlaylists.Count),
                              false, false, false, false);

                if (!supportsAccount)
                {
                    KugouWidgets.Badge(
                        new Rect(headerRect.x + 178f, headerRect.y + 6f, 176f, 18f),
                        "需 ExternalGateway + 登录",
                        KugouTheme.Colors.TextTertiary,
                        KugouTheme.Colors.InputBg,
                        KugouTheme.Colors.Divider);
                }
            }

            if (GUI.Button(toggleHit, GUIContent.none, GUIStyle.none))
            {
                _accountExpanded = !_accountExpanded;
            }

            if (!supportsAccount)
            {
                if (KugouWidgets.Button(new Rect(headerRect.xMax - 76f, headerRect.y + 3f, 76f, 24f), "去设置", ButtonKind.Ghost))
                {
                    _ctx.GoToPage(3);
                }
            }
            else
            {
                string syncLabel = _syncingAccount ? "同步中…" : "同步账号歌单";
                if (KugouWidgets.Button(new Rect(headerRect.xMax - 116f, headerRect.y + 2f, 116f, 26f), syncLabel, ButtonKind.Secondary, !_syncingAccount, IconCode.Refresh))
                {
                    _accountExpanded = true;
                    SyncAccountPlaylists();
                }
            }

            if (!_accountExpanded)
            {
                return headerRect.yMax;
            }

            float listY = headerRect.yMax + 6f;
            if (_accountPlaylists.Count == 0)
            {
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                              .Draw(new Rect(r.x + 20f, listY, r.width - 24f, 30f),
                                    KugouTheme.Content(supportsAccount ? "点击右上「同步账号歌单」拉取你的收藏与自建歌单" : "请先切换至本地网关并扫码登录后同步"),
                                    false, false, false, false);
                }

                return listY + 38f;
            }

            KugouPlaylistRecord[] saved = _ctx.GetSavedPlaylists();
            float rowH = 44f;
            float topY = listY;
            for (int i = 0; i < _accountPlaylists.Count; i++)
            {
                var rowRect = new Rect(r.x, topY + i * rowH, r.width, rowH - 4f);
                DrawAccountPlaylistRow(rowRect, _accountPlaylists[i], FindSavedPlaylist(_accountPlaylists[i].SpecialId, saved) != null);
            }

            return topY + _accountPlaylists.Count * rowH;
        }

        private void DrawAccountPlaylistRow(Rect r, KugouPlaylistSummary summary, bool alreadySaved)
        {
            bool hover = KugouWidgets.IsHovered(r);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(r, hover ? KugouTheme.Colors.CardHover : KugouTheme.Colors.Card, 8f);
                KugouPainter.Stroke(r, KugouTheme.Colors.Divider, 8f, 1f);
                KugouPainter.Icon(new Rect(r.x + 10f, r.y + 10f, 20f, 20f), IconCode.Music, 13, KugouTheme.Colors.AccentSky);

                string name = string.IsNullOrEmpty(summary.Name) ? "未命名歌单" : summary.Name;
                KugouTheme.Style(TypeToken.Body, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x + 36f, r.y, r.width - 190f, r.height), KugouTheme.Content(name), false, false, false, false);

                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight)
                          .Draw(new Rect(r.xMax - 156f, r.y, 70f, r.height), KugouTheme.Content(summary.SongCount + " 首"), false, false, false, false);
            }

            if (alreadySaved)
            {
                if (KugouWidgets.Button(new Rect(r.xMax - 76f, r.y + 7f, 68f, 26f), "查看", ButtonKind.Ghost))
                {
                    KugouPlaylistRecord rec = FindSavedPlaylist(summary.SpecialId);
                    if (rec != null)
                    {
                        OpenPlaylistDetail(rec);
                    }
                }
            }
            else if (KugouWidgets.Button(new Rect(r.xMax - 76f, r.y + 7f, 68f, 26f), "导入", ButtonKind.Secondary, !_importing))
            {
                StartImport(summary.SpecialId);
            }
        }

        private void StartImport(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                _ctx.SetStatus("请先粘贴歌单链接或 ID", true);
                return;
            }

            RunImportAsync(input).Forget();
        }

        private async UniTaskVoid RunImportAsync(string input)
        {
            _importing = true;
            try
            {
                await _ctx.ImportPlaylistAsync(input, true);
                _cachedTrackCount = -1;
            }
            finally
            {
                _importing = false;
            }
        }

        private void DeletePlaylist(string specialId)
        {
            try
            {
                if (Plugin.Store != null && Plugin.Store.RemovePlaylist(specialId))
                {
                    if (string.Equals(_viewingPlaylistId, specialId, StringComparison.Ordinal))
                    {
                        _viewingPlaylistId = string.Empty;
                    }

                    _ctx.RefreshSavedPlaylists();
                    _ctx.SetStatus("已移除本地歌单", false);
                }
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("删除失败: " + ex.Message, true);
            }
        }

        private void SyncAccountPlaylists()
        {
            IKugouClient client = Plugin.Client;
            if (client == null)
            {
                _ctx.SetStatus("接口客户端未初始化", true);
                return;
            }

            if (!client.SupportsAccount)
            {
                _ctx.SetStatus("当前后端不支持个人歌单，请切到 ExternalGateway", true);
                return;
            }

            SyncAccountPlaylistsAsync().Forget();
        }

        private async UniTaskVoid SyncAccountPlaylistsAsync()
        {
            IKugouClient client = Plugin.Client;
            if (client == null)
            {
                return;
            }

            _syncingAccount = true;
            _ctx.SetStatus("正在同步个人歌单…", false);
            try
            {
                var result = await client.GetUserPlaylistsAsync(CancellationToken.None);
                await UniTask.SwitchToMainThread(CancellationToken.None);

                _accountPlaylists.Clear();
                if (result != null && result.Status == KugouApiStatus.Ok && result.Playlists.Count > 0)
                {
                    _accountPlaylists.AddRange(result.Playlists);
                    _ctx.SetStatus("已同步 " + _accountPlaylists.Count + " 个账号歌单", false);
                }
                else
                {
                    _ctx.SetStatus("同步失败: " + (result != null ? result.Message : "无响应"), true);
                }
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("同步异常: " + ex.Message, true);
            }
            finally
            {
                _syncingAccount = false;
            }
        }

        private KugouPlaylistRecord FindSavedPlaylist(string specialId)
        {
            return FindSavedPlaylist(specialId, _ctx.GetSavedPlaylists());
        }

        private static KugouPlaylistRecord FindSavedPlaylist(string specialId, KugouPlaylistRecord[] saved)
        {
            if (saved == null || string.IsNullOrEmpty(specialId))
            {
                return null;
            }

            for (int i = 0; i < saved.Length; i++)
            {
                if (saved[i] != null && string.Equals(saved[i].SpecialId, specialId, StringComparison.Ordinal))
                {
                    return saved[i];
                }
            }

            return null;
        }

        private static KugouTrack FindFirstCoverCandidate(List<KugouTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i] != null && !string.IsNullOrEmpty(tracks[i].CoverUrl))
                {
                    return tracks[i];
                }
            }

            return tracks[0];
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            return !string.IsNullOrEmpty(source) &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolvePlayingUuid()
        {
            try
            {
                var service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                return service != null && service.PlayingMusic != null ? service.PlayingMusic.UUID : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CurrentTrackCount()
        {
            try
            {
                var service = Plugin.Switch != null ? Plugin.Switch.Service : null;
                return service != null && service.CurrentPlayList != null
                    ? service.CurrentPlayList.Count.ToString()
                    : "-";
            }
            catch
            {
                return "-";
            }
        }

        private static string FormatRelativeTime(long unixSeconds)
        {
            if (unixSeconds <= 0)
            {
                return "本地缓存";
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long diff = Math.Max(0, now - unixSeconds);
            if (diff < 120) return "刚刚";
            if (diff < 3600) return (diff / 60) + " 分钟前";
            if (diff < 86400) return (diff / 3600) + " 小时前";
            if (diff < 86400 * 30) return (diff / 86400) + " 天前";
            return (diff / (86400 * 30)) + " 个月前";
        }

        public void Dispose()
        {
            _accountPlaylists.Clear();
            _detailFilteredIndices.Clear();
        }
    }
}
