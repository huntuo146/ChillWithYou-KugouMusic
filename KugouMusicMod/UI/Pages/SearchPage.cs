using System;
using System.Collections.Generic;
using System.Threading;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.NowPlaying;
using ChillWithYou.KugouMusic.UI.Theme;
using ChillWithYou.KugouMusic.UI.Widgets;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Pages
{
    internal sealed class SearchPage : IKugouPage
    {
        private sealed class TrackRowVm
        {
            public KugouTrack Track;
            public string IndexText;
            public string Title;
            public string Subtitle;
            public string DurationText;
            public string Uuid;
            public bool IsPaid;
        }

        private sealed class PlaylistRowVm
        {
            public KugouPlaylistSummary Summary;
            public string Title;
            public string Subtitle;
            public string Monogram;
        }

        private static readonly string[] SearchModeLabels = { "单曲", "歌单" };

        private readonly KugouUiContext _ctx;
        private readonly List<TrackRowVm> _trackRows = new List<TrackRowVm>();
        private readonly List<PlaylistRowVm> _playlistRows = new List<PlaylistRowVm>();
        private readonly HashSet<string> _trialHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _keyword = string.Empty;
        private int _modeIndex;
        private bool _searching;
        private bool _searchFailed;
        private string _resultMeta = string.Empty;
        private Vector2 _scrollPos;

        public string Title { get { return "搜索"; } }
        public IconCode Icon { get { return IconCode.Search; } }

        public SearchPage(KugouUiContext ctx)
        {
            _ctx = ctx;
        }

        public void OnShow() { }
        public void Tick() { }

        public void Draw(Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouTheme.Style(TypeToken.Display, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(r.x, r.y, 180f, 28f), KugouTheme.Content("搜索"), false, false, false, false);

                if (!string.IsNullOrEmpty(_resultMeta))
                {
                    KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleRight)
                              .Draw(new Rect(r.xMax - 260f, r.y + 4f, 260f, 22f), KugouTheme.Content(_resultMeta), false, false, false, false);
                }
            }

            // 顶部搜索行
            float barY = r.y + 36f;
            float btnW = 76f;
            float segW = 112f;
            float fieldW = r.width - btnW - segW - 16f;

            bool submit;
            _keyword = KugouWidgets.TextField(
                new Rect(r.x, barY, fieldW, 34f),
                "search_kw",
                _keyword,
                new TextFieldOptions
                {
                    Placeholder = "搜索歌曲、歌手或歌单…",
                    LeadingIcon = IconCode.Search,
                    AllowClear = true,
                    AllowPaste = true,
                },
                out submit);

            int nextMode = KugouWidgets.Segmented(
                new Rect(r.x + fieldW + 8f, barY + 2f, segW, 30f),
                _modeIndex,
                SearchModeLabels,
                "search_mode_seg".GetHashCode(),
                _searching);
            if (nextMode != _modeIndex && !_searching)
            {
                _modeIndex = nextMode;
                if (!string.IsNullOrEmpty(_keyword))
                {
                    DoSearch();
                }
            }

            bool clickedSearch = KugouWidgets.Button(
                new Rect(r.xMax - btnW, barY + 1f, btnW, 32f),
                _searching ? "搜索中" : "搜索",
                ButtonKind.Primary,
                !_searching);

            if ((submit || clickedSearch) && !_searching)
            {
                DoSearch();
            }

            // 结果区
            var viewRect = new Rect(r.x, barY + 46f, r.width, Mathf.Max(60f, r.yMax - (barY + 46f)));
            if (_searching)
            {
                DrawSkeletonList(viewRect);
                return;
            }

            int totalCount = _modeIndex == 0 ? _trackRows.Count : _playlistRows.Count;
            if (totalCount == 0)
            {
                DrawEmptyOrErrorState(viewRect);
                return;
            }

            float rowH = 54f;
            float totalH = totalCount * rowH;
            var contentRect = new Rect(0f, 0f, viewRect.width - 10f, Mathf.Max(viewRect.height - 2f, totalH));
            _scrollPos = KugouWidgets.BeginScroll(viewRect, _scrollPos, contentRect);

            // 虚拟化：仅绘制可见范围行
            int first = Mathf.Max(0, Mathf.FloorToInt(_scrollPos.y / rowH) - 1);
            int last = Mathf.Min(totalCount - 1, Mathf.CeilToInt((_scrollPos.y + viewRect.height) / rowH) + 1);

            string playingUuid = ResolvePlayingUuid();
            for (int i = first; i <= last; i++)
            {
                var rowRect = new Rect(0f, i * rowH, contentRect.width, rowH - 4f);
                if (_modeIndex == 0)
                {
                    TrackRowVm vm = _trackRows[i];
                    bool isPlaying = !string.IsNullOrEmpty(playingUuid) && string.Equals(playingUuid, vm.Uuid, StringComparison.Ordinal);
                    DrawTrackRow(rowRect, vm, isPlaying);
                }
                else
                {
                    DrawPlaylistRow(rowRect, _playlistRows[i]);
                }
            }

            KugouWidgets.EndScroll();
        }

        private void DrawSkeletonList(Rect viewRect)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            int count = Mathf.Min(7, Mathf.Max(3, Mathf.FloorToInt(viewRect.height / 54f)));
            for (int i = 0; i < count; i++)
            {
                KugouWidgets.SkeletonRow(new Rect(viewRect.x, viewRect.y + i * 54f, viewRect.width, 50f), i * 0.25f);
            }
        }

        private void DrawEmptyOrErrorState(Rect viewRect)
        {
            var card = new Rect(viewRect.x, viewRect.y + 12f, viewRect.width, Mathf.Min(160f, viewRect.height - 24f));
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouWidgets.CardFrame(card, false, false);
                KugouPainter.Icon(
                    new Rect(card.center.x - 16f, card.y + 34f, 32f, 32f),
                    _searchFailed ? IconCode.Warning : IconCode.Search,
                    24,
                    _searchFailed ? KugouTheme.Colors.Warning : KugouTheme.Colors.TextTertiary);

                string main = _searchFailed
                    ? "搜索请求失败"
                    : (string.IsNullOrEmpty(_resultMeta) ? "输入歌名、歌手或关键词开始探索" : "未找到相关结果");
                KugouTheme.Style(TypeToken.BodyStrong, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleCenter)
                          .Draw(new Rect(card.x + 20f, card.y + 76f, card.width - 40f, 24f), KugouTheme.Content(main), false, false, false, false);
            }

            if (_searchFailed && KugouWidgets.Button(new Rect(card.center.x - 46f, card.y + 110f, 92f, 28f), "重试", ButtonKind.Secondary))
            {
                DoSearch();
            }
        }

        private void DrawTrackRow(Rect r, TrackRowVm vm, bool isPlaying)
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

                // 序号或正在播放律动条
                var idxRect = new Rect(r.x + 8f, r.y, 26f, r.height);
                if (isPlaying)
                {
                    KugouPainter.EqualizerBars(new Rect(r.x + 14f, r.y + 18f, 14f, 14f), true);
                }
                else
                {
                    KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleCenter)
                              .Draw(idxRect, KugouTheme.Content(vm.IndexText), false, false, false, false);
                }

                // 封面（支持缺图自动拉取）
                KugouWidgets.DrawCover(
                    new Rect(r.x + 38f, r.y + 7f, 36f, 36f),
                    _ctx.Covers,
                    vm.Track.CoverUrl,
                    7f,
                    vm.Uuid,
                    KugouWidgets.FirstMonogram(vm.Title),
                    false,
                    0f,
                    vm.Track,
                    150);

                // 徽章与时长靠右留空
                float rightBound = r.xMax - 122f;
                KugouTheme.Style(TypeToken.Mono, KugouTheme.Colors.TextSecondary, TextAnchor.MiddleRight)
                          .Draw(new Rect(r.xMax - 118f, r.y, 44f, r.height), KugouTheme.Content(vm.DurationText), false, false, false, false);

                float badgeX = rightBound;
                if (_trialHashes.Contains(vm.Track.Hash ?? string.Empty))
                {
                    badgeX -= 44f;
                    KugouWidgets.Badge(new Rect(badgeX, r.y + 16f, 40f, 18f), "试听", KugouTheme.Colors.BadgeTrialText, KugouTheme.Colors.BadgeTrialBg, KugouTheme.Colors.BadgeTrialBorder);
                }
                else if (vm.IsPaid)
                {
                    badgeX -= 44f;
                    KugouWidgets.Badge(new Rect(badgeX, r.y + 16f, 40f, 18f), "付费", KugouTheme.Colors.BadgePaidText, KugouTheme.Colors.BadgePaidBg, KugouTheme.Colors.BadgePaidBorder);
                }

                float textX = r.x + 84f;
                float textW = Mathf.Max(60f, badgeX - textX - 8f);
                Color titleCol = isPlaying ? KugouTheme.Colors.AccentSky : KugouTheme.Colors.TextPrimary;
                KugouTheme.Style(TypeToken.BodyStrong, titleCol, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 6f, textW, 20f), KugouTheme.Content(vm.Title), false, false, false, false);

                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 26f, textW, 18f), KugouTheme.Content(vm.Subtitle), false, false, false, false);
            }

            // 操作按钮：播放（圆形） + 加入（Ghost）
            if (KugouWidgets.IconButton(new Rect(r.xMax - 64f, r.y + 11f, 28f, 28f), IconCode.Play, isPlaying, true, 12))
            {
                PlayTrack(vm.Track);
            }

            if (KugouWidgets.IconButton(new Rect(r.xMax - 32f, r.y + 11f, 26f, 28f), IconCode.Add, false, true, 12))
            {
                AddTrack(vm.Track);
            }
        }

        private void DrawPlaylistRow(Rect r, PlaylistRowVm vm)
        {
            bool hover = KugouWidgets.IsHovered(r);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                KugouPainter.RoundRect(r, hover ? KugouTheme.Colors.CardHover : KugouTheme.WithAlpha(KugouTheme.Colors.Card, 0.52f), 10f);
                if (hover)
                {
                    KugouPainter.Stroke(r, KugouTheme.Colors.StrokeHairline, 10f, 1f);
                }

                KugouWidgets.DrawCover(
                    new Rect(r.x + 10f, r.y + 5f, 40f, 40f),
                    _ctx.Covers,
                    vm.Summary.CoverUrl,
                    8f,
                    vm.Title + vm.Summary.SpecialId,
                    vm.Monogram);

                float textX = r.x + 60f;
                float textW = Mathf.Max(60f, r.xMax - textX - 110f);
                KugouTheme.Style(TypeToken.BodyStrong, KugouTheme.Colors.TextPrimary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 6f, textW, 20f), KugouTheme.Content(vm.Title), false, false, false, false);
                KugouTheme.Style(TypeToken.Caption, KugouTheme.Colors.TextTertiary, TextAnchor.MiddleLeft)
                          .Draw(new Rect(textX, r.y + 26f, textW, 18f), KugouTheme.Content(vm.Subtitle), false, false, false, false);
            }

            if (KugouWidgets.Button(new Rect(r.xMax - 98f, r.y + 11f, 90f, 28f), "导入并播放", ButtonKind.Secondary))
            {
                _ctx.ImportPlaylistAsync(vm.Summary.SpecialId, true).Forget();
            }
        }

        private void DoSearch()
        {
            if (string.IsNullOrEmpty(_keyword))
            {
                _ctx.SetStatus("请输入搜索关键词", true);
                return;
            }

            _searching = true;
            _searchFailed = false;
            _scrollPos = Vector2.zero;
            SearchAsync(_keyword.Trim(), _modeIndex == 1).Forget();
        }

        private async UniTaskVoid SearchAsync(string keyword, bool playlistMode)
        {
            try
            {
                IKugouClient client = Plugin.Client;
                if (client == null)
                {
                    _searching = false;
                    _searchFailed = true;
                    _ctx.SetStatus("接口客户端未初始化", true);
                    return;
                }

                KugouSearchResult result = playlistMode
                    ? await client.SearchPlaylistsAsync(keyword, 1, 20, CancellationToken.None)
                    : await client.SearchTracksAsync(keyword, 1, 30, CancellationToken.None);

                await UniTask.SwitchToMainThread(CancellationToken.None);
                _searching = false;

                if (result == null || result.Status != KugouApiStatus.Ok)
                {
                    _searchFailed = true;
                    _ctx.SetStatus("搜索失败: " + (result != null ? result.Message : "无响应"), true);
                    return;
                }

                _searchFailed = false;
                RebuildRowViewModels(result, playlistMode, keyword);
                _ctx.SetStatus("搜索完成：" + _resultMeta, false);
            }
            catch (Exception ex)
            {
                _searching = false;
                _searchFailed = true;
                _ctx.SetStatus("搜索异常: " + ex.Message, true);
            }
        }

        private void RebuildRowViewModels(KugouSearchResult result, bool playlistMode, string keyword)
        {
            _trackRows.Clear();
            _playlistRows.Clear();

            if (playlistMode)
            {
                for (int i = 0; i < result.Playlists.Count; i++)
                {
                    KugouPlaylistSummary sum = result.Playlists[i];
                    string title = string.IsNullOrEmpty(sum.Name) ? "未命名歌单" : sum.Name;
                    string author = string.IsNullOrEmpty(sum.Author) ? "未知创建者" : sum.Author;
                    _playlistRows.Add(new PlaylistRowVm
                    {
                        Summary = sum,
                        Title = title,
                        Subtitle = author + " · " + sum.SongCount + " 首",
                        Monogram = KugouWidgets.FirstMonogram(title),
                    });
                }

                _resultMeta = "歌单 " + _playlistRows.Count + " 个 · 「" + keyword + "」";
                return;
            }

            for (int i = 0; i < result.Tracks.Count; i++)
            {
                KugouTrack track = result.Tracks[i];
                string singer = track.GetDisplaySinger();
                string album = string.IsNullOrEmpty(track.AlbumName) ? string.Empty : " · " + track.AlbumName;
                _trackRows.Add(new TrackRowVm
                {
                    Track = track,
                    IndexText = (i + 1).ToString("00"),
                    Title = track.GetDisplayName(),
                    Subtitle = singer + album,
                    DurationText = NowPlayingModel.FormatSeconds(track.DurationSeconds),
                    Uuid = track.GetUuid(),
                    IsPaid = track.IsPaid(),
                });
            }

            _resultMeta = "单曲 " + _trackRows.Count + " 首 · 「" + keyword + "」";
        }

        private void PlayTrack(KugouTrack track)
        {
            try
            {
                if (Plugin.Switch == null)
                {
                    return;
                }

                if (!Plugin.Switch.IsKugouMode)
                {
                    _ctx.SetToastWithAction("当前为官方曲库模式", "切到酷狗", () =>
                    {
                        _ctx.ToggleSource();
                    });
                    return;
                }

                MusicService service = Plugin.Switch.Service;
                if (service == null)
                {
                    _ctx.SetStatus("音乐服务未就绪，请先进入房间场景", true);
                    return;
                }

                if (!Plugin.Switch.AddTrackToCurrentPlaylist(track))
                {
                    _ctx.SetStatus("加入播放列表失败", true);
                    return;
                }

                GameAudioInfo info = Plugin.Switch.FindEntry(track.GetUuid());
                if (info == null)
                {
                    _ctx.SetStatus("找不到刚加入的曲目", true);
                    return;
                }

                service.PlayArugumentMusic(info, MusicChangeKind.Manual);
                _ctx.SetStatus("正在播放: " + track.GetDisplayName(), false);
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("播放失败: " + ex.Message, true);
            }
        }

        private void AddTrack(KugouTrack track)
        {
            try
            {
                if (Plugin.Switch != null && Plugin.Switch.AddTrackToCurrentPlaylist(track))
                {
                    _ctx.SetStatus("已加入当前列表: " + track.GetDisplayName(), false);
                }
                else
                {
                    _ctx.SetToastWithAction("请先切换到酷狗音源再添加曲目", "切到酷狗", () =>
                    {
                        _ctx.ToggleSource();
                    });
                }
            }
            catch (Exception ex)
            {
                _ctx.SetStatus("加入失败: " + ex.Message, true);
            }
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

        public void Dispose()
        {
            _trackRows.Clear();
            _playlistRows.Clear();
            _trialHashes.Clear();
        }
    }
}
