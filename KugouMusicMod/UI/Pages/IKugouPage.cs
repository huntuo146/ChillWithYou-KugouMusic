using System;
using System.Collections.Generic;
using System.Threading;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using ChillWithYou.KugouMusic.UI.Theme;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Pages
{
    internal interface IKugouPage
    {
        string Title { get; }
        IconCode Icon { get; }
        void OnShow();
        void Tick();
        void Draw(Rect contentRect);
        void Dispose();
    }

    internal sealed class KugouUiContext
    {
        public CoverCache Covers { get; private set; }
        public Action<string, bool> SetStatus { get; private set; }
        public Action<string, string, Action> SetToastWithAction { get; private set; }
        public Action ToggleSource { get; private set; }
        public Action<int> GoToPage { get; private set; }
        public Func<string, bool, UniTask> ImportPlaylistAsync { get; private set; }
        public Action<string> SwitchPlaylist { get; private set; }
        public Func<KugouPlaylistRecord[]> GetSavedPlaylists { get; private set; }
        public Action RefreshSavedPlaylists { get; private set; }

        public KugouUiContext(
            CoverCache covers,
            Action<string, bool> setStatus,
            Action<string, string, Action> setToastWithAction,
            Action toggleSource,
            Action<int> goToPage,
            Func<string, bool, UniTask> importPlaylistAsync,
            Action<string> switchPlaylist,
            Func<KugouPlaylistRecord[]> getSavedPlaylists,
            Action refreshSavedPlaylists)
        {
            Covers = covers;
            SetStatus = setStatus;
            SetToastWithAction = setToastWithAction;
            ToggleSource = toggleSource;
            GoToPage = goToPage;
            ImportPlaylistAsync = importPlaylistAsync;
            SwitchPlaylist = switchPlaylist;
            GetSavedPlaylists = getSavedPlaylists;
            RefreshSavedPlaylists = refreshSavedPlaylists;
        }
    }
}
