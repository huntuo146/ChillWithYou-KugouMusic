using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Bulbul;
using ChillWithYou.KugouMusic.Models;
using ChillWithYou.KugouMusic.Services;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace ChillWithYou.KugouMusic.Patches
{
    /// <summary>
    /// 预先解析的原方法 / 私有字段引用，供补丁内部回调原始逻辑使用。
    /// </summary>
    internal static class MusicPatchTargets
    {
        private static MethodInfo _playArugumentMusic;
        private static MethodInfo _playMusicInPlaylist;
        private static MethodInfo _addMusicEntry;
        private static FieldInfo _allMusicListField;
        private static FieldInfo _shuffleListField;
        private static FieldInfo _audioLoadCancellationField;
        private static FieldInfo _playingMusicField;

        public static void Initialize()
        {
            _playArugumentMusic = AccessTools.Method(
                typeof(MusicService),
                "PlayArugumentMusic",
                new[] { typeof(GameAudioInfo), typeof(MusicChangeKind) });

            _playMusicInPlaylist = AccessTools.Method(
                typeof(MusicService),
                "PlayMusicInPlaylist",
                new[] { typeof(int) });

            _addMusicEntry = AccessTools.Method(typeof(MusicService), "AddMusicEntry");
            _allMusicListField = AccessTools.Field(typeof(MusicService), "_allMusicList");
            _shuffleListField = AccessTools.Field(typeof(MusicService), "shuffleList");
            _audioLoadCancellationField = AccessTools.Field(typeof(MusicService), "audioLoadCancellation");
            _playingMusicField = AccessTools.Field(typeof(MusicService), "<PlayingMusic>k__BackingField");

            if (_playArugumentMusic == null)
            {
                Plugin.Log.LogError("[Patch] 未找到 MusicService.PlayArugumentMusic，延迟播放将不可用");
            }

            if (_addMusicEntry == null)
            {
                Plugin.Log.LogWarning("[Patch] 未找到 MusicService.AddMusicEntry，AddMusicItem 隔离将回退为原逻辑");
            }
        }

        public static bool CanInvokeOriginal
        {
            get { return _playArugumentMusic != null; }
        }

        public static void InvokePlayArugumentMusic(
            MusicService service,
            GameAudioInfo info,
            MusicChangeKind kind)
        {
            if (_playArugumentMusic == null)
            {
                return;
            }

            try
            {
                _playArugumentMusic.Invoke(service, new object[] { info, kind });
            }
            catch (TargetInvocationException ex)
            {
                Plugin.Log.LogError("[Patch] 回调 PlayArugumentMusic 失败: " + (ex.InnerException ?? ex));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Patch] 回调 PlayArugumentMusic 失败: " + ex);
            }
        }

        public static void InvokePlayMusicInPlaylist(MusicService service, int index)
        {
            if (_playMusicInPlaylist == null)
            {
                return;
            }

            try
            {
                _playMusicInPlaylist.Invoke(service, new object[] { index });
            }
            catch (TargetInvocationException ex)
            {
                Plugin.Log.LogError("[Patch] 回调 PlayMusicInPlaylist 失败: " + (ex.InnerException ?? ex));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Patch] 回调 PlayMusicInPlaylist 失败: " + ex);
            }
        }

        /// <summary>读取游戏当前标签（只读，绝不写存档）。</summary>
        public static AudioTag GetCurrentAudioTag()
        {
            try
            {
                SaveDataManager manager = SaveDataManager.Instance;
                if (manager == null || manager.MusicSetting == null)
                {
                    return AudioTag.All;
                }

                FieldInfo field = AccessTools.Field(typeof(MusicSettingV2), "CurrentAudioTag");
                if (field == null)
                {
                    return AudioTag.All;
                }

                object reactive = field.GetValue(manager.MusicSetting);
                if (reactive == null)
                {
                    return AudioTag.All;
                }

                PropertyInfo valueProperty = reactive.GetType().GetProperty(
                    "Value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (valueProperty == null || !valueProperty.CanRead)
                {
                    return AudioTag.All;
                }

                object value = valueProperty.GetValue(reactive);
                return value is AudioTag ? (AudioTag)value : AudioTag.All;
            }
            catch
            {
                return AudioTag.All;
            }
        }

        /// <summary>
        /// 统一可见性判定（标签筛选补丁、AddMusicItem 隔离、切源后刷新列表共用）。
        /// 官方曲目沿用游戏规则；酷狗曲目在"全部/原创"始终可见，"收藏"看本地收藏，"本机"隐藏。
        /// </summary>
        public static bool IsVisibleUnderTag(GameAudioInfo info, AudioTag tag)
        {
            if (info == null)
            {
                return false;
            }

            KugouTrack track;
            if (Plugin.Registry.TryGet(info, out track))
            {
                if ((tag & AudioTag.All) == AudioTag.All || (tag & AudioTag.Original) != 0)
                {
                    return true;
                }

                if ((tag & AudioTag.Favorite) != 0)
                {
                    return Plugin.Store != null && track != null && Plugin.Store.IsFavorite(track.Hash);
                }

                return false;
            }

            return (tag & info.Tag) != 0;
        }

        /// <summary>
        /// 手工复刻 MusicService.AddMusicEntry（登记 _allMusicList + 订阅 clip 替换事件）。
        /// 用于 AddMusicItem 的存档隔离路径。
        /// </summary>
        public static bool TryAddMusicEntry(MusicService service, GameAudioInfo info)
        {
            if (service == null || info == null || _addMusicEntry == null)
            {
                return false;
            }

            try
            {
                _addMusicEntry.Invoke(service, new object[] { info });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Patch] AddMusicEntry 调用失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 直接清空 PlayingMusic。
        /// 当上一个条目的 AudioClip 已被卸载（null）时，原版 Stop() 内部的 GetPlayer(null) 可能抛异常，
        /// 导致 PlayingMusic 残留，后续播放会再次踩到 null clip。
        /// </summary>
        public static void ClearPlayingMusic(MusicService service)
        {
            if (service == null || _playingMusicField == null)
            {
                return;
            }

            try
            {
                _playingMusicField.SetValue(service, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Patch] 清空 PlayingMusic 失败: " + ex.Message);
            }
        }

        /// <summary>取消 MusicService 内部的自动连播加载令牌（GetNextGameAudio 每轮会重建它）。</summary>
        public static void CancelAudioLoad(MusicService service)
        {
            if (service == null || _audioLoadCancellationField == null)
            {
                return;
            }

            var cts = _audioLoadCancellationField.GetValue(service) as System.Threading.CancellationTokenSource;
            if (cts != null)
            {
                cts.Cancel();
            }
        }

        public static List<GameAudioInfo> GetAllMusicList(MusicService service)
        {
            if (service == null || _allMusicListField == null)
            {
                return null;
            }

            try
            {
                return _allMusicListField.GetValue(service) as List<GameAudioInfo>;
            }
            catch
            {
                return null;
            }
        }

        public static List<GameAudioInfo> GetShuffleList(MusicService service)
        {
            if (service == null || _shuffleListField == null)
            {
                return null;
            }

            try
            {
                return _shuffleListField.GetValue(service) as List<GameAudioInfo>;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// P0 核心补丁集（全部通过 AccessTools 手工装配，单个目标缺失只跳过不崩溃）。
    /// </summary>
    internal static class MusicPatches
    {
        private static readonly Dictionary<string, int> HitCounts = new Dictionary<string, int>();
        private static readonly object HitGate = new object();

        private static BindingFlags AllStatics =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>FacilityMusic.Setup 正在执行期间为 true，用于拦截启动加载阶段的无意自启播放。</summary>
        internal static bool IsFacilitySetupRunning { get; private set; }

        public static void Install(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            MusicPatchTargets.Initialize();

            // 1. 同步播放拦截（列表点击 / 指定播放）
            Patch(
                harmony,
                typeof(MusicService),
                "PlayArugumentMusic",
                new[] { typeof(GameAudioInfo), typeof(MusicChangeKind) },
                "Prefix_PlayArugumentMusic",
                null,
                "MusicService.PlayArugumentMusic");

            // 2. 列表索引播放拦截
            Patch(
                harmony,
                typeof(MusicService),
                "PlayMusicInPlaylist",
                new[] { typeof(int) },
                "Prefix_PlayMusicInPlaylist",
                null,
                "MusicService.PlayMusicInPlaylist");

            // 3. 异步取 clip（自动下一首 / 上一首 / 剧情）
            Patch(
                harmony,
                typeof(GameAudioInfo),
                "GetAudioClip",
                new[] { typeof(CancellationToken) },
                "Prefix_GetAudioClip",
                null,
                "GameAudioInfo.GetAudioClip");

            // 4. clip 卸载守卫（保护官方曲目 + 销毁酷狗运行时 clip）
            Patch(
                harmony,
                typeof(GameAudioInfo),
                "UnloadAudioClip",
                null,
                "Prefix_UnloadAudioClip",
                "Postfix_UnloadAudioClip",
                "GameAudioInfo.UnloadAudioClip");

            // 5. 设施 Setup（唯一安全的注入点 + 阻止加载阶段开启播放）
            Patch(
                harmony,
                typeof(FacilityMusic),
                "Setup",
                null,
                "Prefix_FacilityMusicSetup",
                "Postfix_FacilityMusicSetup",
                "Finalizer_FacilityMusicSetup",
                "FacilityMusic.Setup");

            // 5.1 启动阶段 PlayNextMusic 拦截（避免 Setup 内对异步酷狗列表触发后台加载/延迟播音）
            Patch(
                harmony,
                typeof(MusicService),
                "PlayNextMusic",
                new[] { typeof(int), typeof(MusicChangeKind) },
                "Prefix_PlayNextMusic",
                null,
                "MusicService.PlayNextMusic");

            // 5.2 暂停时取消在途异步酷狗加载；空轨点播放时自动补播列表首曲/上次记忆曲目
            Patch(harmony, typeof(MusicService), "Pause", null, null, "Postfix_Pause", "MusicService.Pause");
            Patch(harmony, typeof(MusicService), "UnPause", null, "Prefix_UnPause", null, "MusicService.UnPause");

            // 6. 收藏 / 排除 / 排序 / 移除：存档隔离
            Patch(harmony, typeof(MusicService), "RegisterFavoriteMusic", new[] { typeof(GameAudioInfo) }, "Prefix_RegisterFavorite", null, "MusicService.RegisterFavoriteMusic");
            Patch(harmony, typeof(MusicService), "UnregisterFavoriteMusic", new[] { typeof(GameAudioInfo) }, "Prefix_UnregisterFavorite", null, "MusicService.UnregisterFavoriteMusic");
            Patch(harmony, typeof(MusicService), "ExcludeFromPlaylist", new[] { typeof(GameAudioInfo) }, "Prefix_ExcludeFromPlaylist", null, "MusicService.ExcludeFromPlaylist");
            Patch(harmony, typeof(MusicService), "IncludeInPlaylist", new[] { typeof(GameAudioInfo) }, "Prefix_IncludeInPlaylist", null, "MusicService.IncludeInPlaylist");
            Patch(harmony, typeof(MusicService), "SwapAfter", new[] { typeof(GameAudioInfo), typeof(GameAudioInfo) }, "Prefix_SwapAfter", null, "MusicService.SwapAfter");
            Patch(harmony, typeof(MusicService), "RemoveLocalMusicItem", new[] { typeof(GameAudioInfo) }, "Prefix_RemoveLocalMusicItem", null, "MusicService.RemoveLocalMusicItem");

            // 7. AddMusicItem 也会写 PlaylistOrder + SaveMusicSetting，必须隔离
            Patch(harmony, typeof(MusicService), "AddMusicItem", new[] { typeof(GameAudioInfo) }, "Prefix_AddMusicItem", null, "MusicService.AddMusicItem");

            // 8. 官方列表快照
            Patch(
                harmony,
                typeof(MusicService),
                "Load",
                new[] { typeof(IReadOnlyCollection<GameAudioInfo>) },
                null,
                "Postfix_Load",
                "MusicService.Load");

            // 9. 标签筛选放行（compiler-generated lambda）
            PatchTagFilter(harmony);
        }

        private static void PatchTagFilter(Harmony harmony)
        {
            if (Plugin.EnableTagFilterPatch == null || !Plugin.EnableTagFilterPatch.Value)
            {
                Plugin.Log.LogInfo("[Patch] 标签筛选补丁已关闭");
                return;
            }

            try
            {
                Type displayType = AccessTools.Inner(typeof(MusicService), "<>c__DisplayClass62_0");
                if (displayType == null)
                {
                    Plugin.Log.LogWarning("[Patch] 未找到标签筛选闭包类型 <>c__DisplayClass62_0，跳过（酷狗曲目将受原标签过滤）");
                    return;
                }

                MethodInfo target = AccessTools.Method(displayType, "<Setup>b__1");
                if (target == null)
                {
                    Plugin.Log.LogWarning("[Patch] 未找到标签筛选方法 <Setup>b__1，跳过");
                    return;
                }

                MethodInfo prefix = typeof(MusicPatches).GetMethod("Prefix_TagFilter", AllStatics);
                if (prefix == null)
                {
                    Plugin.Log.LogError("[Patch] Prefix_TagFilter 缺失");
                    return;
                }

                harmony.Patch(target, new HarmonyMethod(prefix));
                Plugin.Log.LogInfo("[Patch] OK MusicService.<Setup>b__1 (标签筛选放行)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Patch] 标签筛选补丁装配失败（不影响其它功能）: " + ex.Message);
            }
        }

        private static void Patch(
            Harmony harmony,
            Type type,
            string name,
            Type[] args,
            string prefixName,
            string postfixName,
            string label)
        {
            Patch(harmony, type, name, args, prefixName, postfixName, null, label);
        }

        private static void Patch(
            Harmony harmony,
            Type type,
            string name,
            Type[] args,
            string prefixName,
            string postfixName,
            string finalizerName,
            string label)
        {
            try
            {
                MethodInfo target = args == null
                    ? AccessTools.Method(type, name)
                    : AccessTools.Method(type, name, args);

                if (target == null)
                {
                    Plugin.Log.LogWarning("[Patch] 目标缺失，跳过: " + label);
                    return;
                }

                HarmonyMethod prefix = null;
                HarmonyMethod postfix = null;
                HarmonyMethod finalizer = null;

                if (!string.IsNullOrEmpty(prefixName))
                {
                    MethodInfo mi = typeof(MusicPatches).GetMethod(prefixName, AllStatics);
                    if (mi == null)
                    {
                        Plugin.Log.LogError("[Patch] 补丁方法缺失: " + prefixName);
                        return;
                    }

                    prefix = new HarmonyMethod(mi);
                }

                if (!string.IsNullOrEmpty(postfixName))
                {
                    MethodInfo mi = typeof(MusicPatches).GetMethod(postfixName, AllStatics);
                    if (mi == null)
                    {
                        Plugin.Log.LogError("[Patch] 补丁方法缺失: " + postfixName);
                        return;
                    }

                    postfix = new HarmonyMethod(mi);
                }

                if (!string.IsNullOrEmpty(finalizerName))
                {
                    MethodInfo mi = typeof(MusicPatches).GetMethod(finalizerName, AllStatics);
                    if (mi == null)
                    {
                        Plugin.Log.LogError("[Patch] 补丁方法缺失: " + finalizerName);
                        return;
                    }

                    finalizer = new HarmonyMethod(mi);
                }

                harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
                Plugin.Log.LogInfo("[Patch] OK " + label);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Patch] 装配失败 " + label + ": " + ex);
            }
        }

        // ---------------- 日志 ----------------

        private static void LogHit(string name)
        {
            if (Plugin.EnableVerboseLog == null || !Plugin.EnableVerboseLog.Value)
            {
                return;
            }

            lock (HitGate)
            {
                int count;
                HitCounts.TryGetValue(name, out count);
                count++;
                HitCounts[name] = count;

                if (count <= 5)
                {
                    Plugin.Log.LogInfo("[Hook] " + name + " 命中 #" + count);
                }
            }
        }

        // ---------------- 工具 ----------------

        private static AudioTag GetCurrentAudioTag()
        {
            return MusicPatchTargets.GetCurrentAudioTag();
        }

        // ---------------- 补丁实现 ----------------

        /// <summary>PlayArugumentMusic：酷狗曲目且 clip 未就绪时，拦截并异步补装载。</summary>
        internal static bool Prefix_PlayArugumentMusic(
            MusicService __instance,
            GameAudioInfo __0,
            MusicChangeKind __1)
        {
            try
            {
                if (__0 == null)
                {
                    return true;
                }

                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                if (__0.AudioClip != null)
                {
                    return true;
                }

                LogHit("PlayArugumentMusic(拦截)");
                KugouPlayback.RequestPlay(__instance, __0, __1);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] PlayArugumentMusic Prefix 异常: " + ex);
                return true;
            }
        }

        /// <summary>PlayMusicInPlaylist：同上，按索引拦截。</summary>
        internal static bool Prefix_PlayMusicInPlaylist(
            MusicService __instance,
            int __0,
            ref bool __result)
        {
            try
            {
                var list = __instance.CurrentPlayList;
                if (list == null || __0 < 0 || __0 >= list.Count)
                {
                    return true;
                }

                GameAudioInfo info = list[__0];
                KugouTrack track;
                if (!Plugin.Registry.TryGet(info, out track))
                {
                    return true;
                }

                if (info.AudioClip != null)
                {
                    return true;
                }

                __result = true;
                LogHit("PlayMusicInPlaylist(拦截)");
                KugouPlayback.RequestPlayIndex(__instance, __0);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] PlayMusicInPlaylist Prefix 异常: " + ex);
                return true;
            }
        }

        /// <summary>PlayNextMusic：启动初始化阶段跳过酷狗自动加载播放，避免加载中异步下载并在暂停后突播。</summary>
        internal static bool Prefix_PlayNextMusic(
            MusicService __instance,
            int __0,
            MusicChangeKind __1,
            ref UniTask<bool> __result)
        {
            try
            {
                if (!IsFacilitySetupRunning || __instance == null)
                {
                    return true;
                }

                if (!IsStartupKugouOrUnloaded(__instance))
                {
                    return true;
                }

                __result = UniTask.FromResult(false);
                LogHit("PlayNextMusic(启动跳过)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] PlayNextMusic Prefix 异常: " + ex);
                return true;
            }
        }

        private static bool IsStartupKugouOrUnloaded(MusicService service)
        {
            if (Plugin.IsKugouDesired())
            {
                return true;
            }

            if (Plugin.Switch != null && (Plugin.Switch.IsKugouMode || Plugin.Switch.IsSwitching))
            {
                return true;
            }

            var list = service.CurrentPlayList;
            if (list != null && list.Count > 0 && list[0] != null)
            {
                if (Plugin.Registry != null && Plugin.Registry.IsKugou(list[0]) && list[0].AudioClip == null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Pause：暂停时取消在途的酷狗异步加载，防止点暂停或退出后下载完成才开始播放。</summary>
        internal static void Postfix_Pause(MusicService __instance)
        {
            try
            {
                KugouPlayback.CancelPending();
                MusicPatchTargets.CancelAudioLoad(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Hook] Pause Postfix 异常: " + ex.Message);
            }
        }

        /// <summary>UnPause：启动阶段未加载当前曲目（PlayingMusic==null）时，用户手动点播放按钮直接起播目标曲目。</summary>
        internal static bool Prefix_UnPause(MusicService __instance)
        {
            try
            {
                if (IsFacilitySetupRunning || __instance == null || __instance.PlayingMusic != null || Plugin.Switch == null)
                {
                    return true;
                }

                GameAudioInfo target = Plugin.Switch.ResolveResumeTarget(__instance);
                if (target == null)
                {
                    return true;
                }

                LogHit("UnPause(空轨起播)");
                __instance.PlayArugumentMusic(target, MusicChangeKind.Manual);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] UnPause Prefix 异常: " + ex);
                return true;
            }
        }

        /// <summary>GetAudioClip：自动下一首等异步链路的接管点。</summary>
        internal static bool Prefix_GetAudioClip(
            GameAudioInfo __instance,
            CancellationToken __0,
            ref UniTask<AudioClip> __result)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__instance, out track))
                {
                    return true;
                }

                if (__instance.AudioClip != null)
                {
                    return true;
                }

                var completion = new UniTaskCompletionSource<AudioClip>();
                __result = completion.Task;
                RunGetAudioClip(__instance, track, completion, __0).Forget();
                LogHit("GetAudioClip(接管)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] GetAudioClip Prefix 异常: " + ex);
                return true;
            }
        }

        private static async UniTaskVoid RunGetAudioClip(
            GameAudioInfo info,
            KugouTrack track,
            UniTaskCompletionSource<AudioClip> completion,
            CancellationToken ct)
        {
            try
            {
                AudioClip clip = await KugouAudioLoader.LoadAsync(info, track, ct);

                if (clip == null)
                {
                    // GetNextGameAudio 自带循环会继续试下一首，这里只计数不跳曲；
                    // 连续失败超过阈值时由 KugouPlayback 取消 audioLoadCancellation 并停播。
                    KugouPlayback.RecordGetClipFailure(Plugin.Switch != null ? Plugin.Switch.Service : null);
                }
                else
                {
                    KugouPlayback.NotifySuccess();
                }

                // 失败时返回 null 而不是异常：游戏原逻辑对 null 有容错（只记录日志并跳过），
                // 抛异常反而会把故障传播进 PlayNextMusic 的异步状态机。
                completion.TrySetResult(clip);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] GetAudioClip 装载异常: " + ex.Message);
                completion.TrySetResult(null);
            }
        }

        /// <summary>
        /// UnloadAudioClip：
        /// - 官方曲目：短路，保住 AudioClip（P0 决策 #2）；
        /// - 酷狗曲目：放行原逻辑（释放流式解码），随后由 Postfix 销毁运行时 clip。
        /// </summary>
        internal static bool Prefix_UnloadAudioClip(GameAudioInfo __instance, out AudioClip __state)
        {
            __state = __instance != null ? __instance.AudioClip : null;

            try
            {
                if (Plugin.IsShuttingDown)
                {
                    return true;
                }

                if (Plugin.EnableUnloadGuard == null || !Plugin.EnableUnloadGuard.Value)
                {
                    return true;
                }

                if (__instance == null)
                {
                    return true;
                }

                if (Plugin.Registry.IsKugou(__instance))
                {
                    return true;
                }

                // 只保护官方曲目（PathType=Normal）。玩家通过游戏导入的本机音乐（LocalPc）
                // 仍走原逻辑，由游戏 Destroy 其 AudioClip，避免插件制造内存泄漏。
                if (__instance.PathType != AudioMode.Normal)
                {
                    return true;
                }

                LogHit("UnloadAudioClip(保护官方)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] UnloadAudioClip Prefix 异常: " + ex);
                return true;
            }
        }

        internal static void Postfix_UnloadAudioClip(GameAudioInfo __instance, AudioClip __state)
        {
            try
            {
                if (__instance == null || __state == null)
                {
                    return;
                }

                if (!Plugin.Registry.IsKugou(__instance))
                {
                    return;
                }

                // 酷狗条目 PathType=Normal，游戏只 UnloadAudioData 不会 Destroy，
                // 这里显式销毁运行时创建的 AudioClip，避免内存泄漏。
                UnityEngine.Object.Destroy(__state);
                LogHit("UnloadAudioClip(销毁酷狗 clip)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Hook] UnloadAudioClip Postfix 异常: " + ex.Message);
            }
        }

        private static MusicPlayListTabUI ResolveTabUi(FacilityMusic facility)
        {
            try
            {
                FieldInfo field = AccessTools.Field(typeof(FacilityMusic), "_musicTabUI");
                if (field == null)
                {
                    return null;
                }

                object value = field.GetValue(facility);
                return value as MusicPlayListTabUI;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Hook] 解析 MusicPlayListTabUI 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>FacilityMusic.Setup Prefix：标记 Setup 窗口并清除自启播放存档标志，避免加载结束自动开启歌曲。</summary>
        internal static void Prefix_FacilityMusicSetup()
        {
            IsFacilitySetupRunning = true;
            try
            {
                if (Plugin.EnsureStartupPausedSetting())
                {
                    Plugin.Log.LogInfo("[Startup] 已重置 IsGameStartPlayMusic=false，游戏加载完成后保持暂停");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Hook] FacilityMusic.Setup Prefix 异常: " + ex.Message);
            }
        }

        /// <summary>FacilityMusic.Setup Finalizer：确保 IsFacilitySetupRunning 必定复位。</summary>
        internal static void Finalizer_FacilityMusicSetup()
        {
            IsFacilitySetupRunning = false;
        }

        /// <summary>FacilityMusic.Setup：绑定 MusicService / 缓存音乐 Tab UI / 触发 UGUI 注入。</summary>
        internal static void Postfix_FacilityMusicSetup(FacilityMusic __instance)
        {
            try
            {
                MusicService service = __instance.MusicService;
                if (service == null)
                {
                    Plugin.Log.LogWarning("[Hook] FacilityMusic.Setup 完成但 MusicService 为 null");
                    return;
                }

                LogHit("FacilityMusic.Setup");

                // 先缓存原生 UI 与设施引用，确保后续 AttachService / TryAutoSwitch 能同步导入按钮状态
                Plugin.FacilityMusicInstance = __instance;

                MusicPlayListTabUI tab = ResolveTabUi(__instance);
                if (tab != null)
                {
                    Plugin.MusicTabUi = tab;
                    Plugin.Window?.OnFacilityReady(__instance, tab);
                }
                else
                {
                    Plugin.Log.LogWarning("[Hook] 未找到 MusicPlayListTabUI，UGUI 注入与导入按钮控制将降级");
                }

                Plugin.Switch.AttachService(service);

                if (Plugin.Switch != null &&
                    (Plugin.Switch.IsKugouMode || Plugin.IsKugouDesired()) &&
                    service.PlayingMusic == null)
                {
                    __instance.ResetAudioInfo();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] FacilityMusic.Setup Postfix 异常: " + ex);
            }
        }

        /// <summary>MusicService.Load：采集官方快照。</summary>
        internal static void Postfix_Load(MusicService __instance)
        {
            try
            {
                Plugin.Switch.OnMusicLoaded(__instance);
                LogHit("MusicService.Load");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] Load Postfix 异常: " + ex);
            }
        }

        // ---------------- 存档隔离 ----------------

        internal static bool Prefix_RegisterFavorite(GameAudioInfo __0, ref bool __result)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                bool changed = Plugin.Store.SetFavorite(track.Hash, true);
                __0.Tag = __0.Tag | AudioTag.Favorite;
                __result = true;
                LogHit("RegisterFavorite(本地)");

                if (changed && Plugin.EnableVerboseLog != null && Plugin.EnableVerboseLog.Value)
                {
                    Plugin.Log.LogInfo("[Hook] 本地收藏: " + track.GetDisplayName());
                }

                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] RegisterFavorite Prefix 异常: " + ex);
                return true;
            }
        }

        internal static bool Prefix_UnregisterFavorite(GameAudioInfo __0, ref bool __result)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                Plugin.Store.SetFavorite(track.Hash, false);
                __0.Tag = __0.Tag & ~AudioTag.Favorite;
                __result = true;
                LogHit("UnregisterFavorite(本地)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] UnregisterFavorite Prefix 异常: " + ex);
                return true;
            }
        }

        internal static bool Prefix_ExcludeFromPlaylist(GameAudioInfo __0, ref bool __result)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                Plugin.Store.SetExcluded(track.Hash, true);
                __result = true;
                LogHit("ExcludeFromPlaylist(本地)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] ExcludeFromPlaylist Prefix 异常: " + ex);
                return true;
            }
        }

        internal static bool Prefix_IncludeInPlaylist(GameAudioInfo __0, ref bool __result)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                Plugin.Store.SetExcluded(track.Hash, false);
                __result = true;
                LogHit("IncludeInPlaylist(本地)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] IncludeInPlaylist Prefix 异常: " + ex);
                return true;
            }
        }

        internal static bool Prefix_SwapAfter(GameAudioInfo __0, GameAudioInfo __1)
        {
            try
            {
                bool kugouTarget = Plugin.Registry.IsKugou(__0);
                bool kugouOrigin = Plugin.Registry.IsKugou(__1);

                if (!kugouTarget && !kugouOrigin)
                {
                    return true;
                }

                // P0：仅阻止写入官方存档；排序持久化在 P1 接入
                LogHit("SwapAfter(拦截)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] SwapAfter Prefix 异常: " + ex);
                return true;
            }
        }

        internal static bool Prefix_RemoveLocalMusicItem(GameAudioInfo __0)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                Plugin.Store.SetExcluded(track.Hash, true);
                LogHit("RemoveLocalMusicItem(拦截)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] RemoveLocalMusicItem Prefix 异常: " + ex);
                return true;
            }
        }

        /// <summary>
        /// AddMusicItem 在 Normal 类型下同样会写 MusicSetting.PlaylistOrder 并 SaveMusicSetting()，
        /// 因此酷狗曲目必须走插件自己的登记路径。
        /// </summary>
        internal static bool Prefix_AddMusicItem(MusicService __instance, GameAudioInfo __0, ref bool __result)
        {
            try
            {
                if (__0 == null)
                {
                    return true;
                }

                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                List<GameAudioInfo> all = MusicPatchTargets.GetAllMusicList(__instance);
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i] != null && all[i].UUID == __0.UUID)
                        {
                            Plugin.Log.LogWarning("[Hook] 酷狗曲目重复添加: " + __0.UUID);
                            __result = false;
                            return false;
                        }
                    }
                }

                if (!MusicPatchTargets.TryAddMusicEntry(__instance, __0))
                {
                    return true;
                }

                AudioTag tag = GetCurrentAudioTag();
                if (MusicPatchTargets.IsVisibleUnderTag(__0, tag))
                {
                    List<GameAudioInfo> shuffle = MusicPatchTargets.GetShuffleList(__instance);
                    if (shuffle != null)
                    {
                        shuffle.Add(__0);
                    }

                    __instance.CurrentPlayList.Add(__0);
                }

                __result = true;
                LogHit("AddMusicItem(隔离存档)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] AddMusicItem Prefix 异常: " + ex);
                return true;
            }
        }

        /// <summary>标签筛选回调：酷狗曲目在酷狗模式下按插件规则放行。</summary>
        internal static bool Prefix_TagFilter(object __instance, GameAudioInfo __0, ref bool __result)
        {
            try
            {
                KugouTrack track;
                if (!Plugin.Registry.TryGet(__0, out track))
                {
                    return true;
                }

                AudioTag tag = AudioTag.All;

                if (__instance != null)
                {
                    FieldInfo field = __instance.GetType().GetField(
                        "tag",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (field != null)
                    {
                        object raw = field.GetValue(__instance);
                        if (raw is AudioTag)
                        {
                            tag = (AudioTag)raw;
                        }
                    }
                }

                __result = MusicPatchTargets.IsVisibleUnderTag(__0, tag);
                LogHit("标签筛选(放行酷狗)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Hook] 标签筛选 Prefix 异常: " + ex);
                return true;
            }
        }
    }
}