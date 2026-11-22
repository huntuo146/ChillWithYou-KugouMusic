# 《放松时光：与你共享Lo-Fi故事》酷狗音乐音源插件开发技术规范（DeepSeek 专用）

本文档用于指导生成 BepInEx 5 插件 C# 代码，目标工程已预先配置在当前目录 `ChillWithYou.KugouMusic.csproj` 中。

---

## 1. 运行与编译环境约束（严格遵守）

1. **目标框架**：`netstandard2.1`（Unity `2022.3.62f2` Windows x64 Mono 后端，BepInEx `5.4.23.6`）。
2. **禁止使用高版本 .NET 专有 API**：
   - 严禁使用 `.NET 6/8` 才有的 API（如 `FrozenDictionary`、`Random.Shared`、`PeriodicTimer`、`ArgumentNullException.ThrowIfNull`）。
   - **特别注意 `HttpClient` 在 `netstandard2.1` 下的方法签名**：`HttpClient.GetStringAsync(string)` 和 `GetByteArrayAsync(string)` **没有**带 `CancellationToken` 的重载！需要取消控制时，必须使用 `new HttpRequestMessage(HttpMethod.Get, url)` + `httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)` 或 `httpClient.GetAsync(url, ct)`。
3. **可用库与命名空间**：
   - BepInEx & Harmony：`BepInEx`、`BepInEx.Configuration`、`BepInEx.Logging`、`HarmonyLib`
   - 异步与响应式：`Cysharp.Threading.Tasks`（`UniTask`、`UniTask<T>`）、`R3`、`ObservableCollections`（`ObservableList<T>`）
   - JSON & 网络：`Newtonsoft.Json`、`Newtonsoft.Json.Linq`、`System.Net.Http`
   - Unity 与 UI：`UnityEngine`、`UnityEngine.UI`、`UnityEngine.Networking`（`UnityWebRequestMultimedia`）、`TMPro`（`TMP_Text` / `TextMeshProUGUI`）
4. **文件拆分要求**：请严格按模块分文件输出完整可编译代码，**禁止省略实现或写 `// TODO` 占位**。推荐文件结构：
   - `Plugin.cs`
   - `Models/KugouModels.cs`
   - `Services/KugouConfigAndStore.cs`
   - `Services/IKugouClient.cs`
   - `Services/NativeKugouClient.cs`
   - `Services/ExternalApiKugouClient.cs`
   - `Services/AudioCacheService.cs`
   - `Services/SourceSwitchService.cs`
   - `Patches/MusicPatches.cs`
   - `UI/KugouWindow.cs`

---

## 2. 已逆向核实的游戏核心类与签名速查表

> 所有类名、字段名、方法名与大小写拼写均已在游戏真实 `Assembly-CSharp.dll` 与 `AudioManager.dll` 中核对。**注意原版代码中存在 `PlayArugumentMusic` 的拼写，切勿擅自改成 `Argument`。**

### 2.1 `MusicService`（全局命名空间，无 namespace）
- **关键私有字段**（需通过 `HarmonyLib.AccessTools` 访问）：
  - `private List<GameAudioInfo> _allMusicList`
  - `private List<GameAudioInfo> shuffleList`
  - `private CancellationTokenSource audioLoadCancellation`
  - `private Subject<MusicChangeKind> onChangeMusic`
  - `private Subject<GameAudioInfo> onPlayMusic`
  - `private Subject<GameAudioInfo> onPauseMusic`
- **关键公开属性**：
  - `public IReadOnlyList<GameAudioInfo> AllMusicList { get; }`
  - `public ObservableList<GameAudioInfo> CurrentPlayList { get; }`（注意：`ObservableCollections.ObservableList<GameAudioInfo>` 自带 `Clear()` 和 `AddRange(IEnumerable<GameAudioInfo>)`，修改它会触发集合变更通知让播放列表 UI 刷新）
  - `public GameAudioInfo PlayingMusic { get; }`
  - `public bool IsRepeatOneMusic { get; }`
  - `public bool IsShuffle { get; }`
  - `public bool IsPause;`（public 字段）
- **关键公开方法**：
  - `public void Load(IReadOnlyCollection<GameAudioInfo> musicItems)`
  - `public UniTask<bool> PlayNextMusic(int nextCount, MusicChangeKind changeKind)`
  - `public void PlayArugumentMusic(GameAudioInfo audioInfo, MusicChangeKind changeKind)`（**注意拼写 `Arugument`**）
  - `public bool PlayMusicInPlaylist(int index)`
  - `public UniTask<bool> PlayBackMusic()`
  - `public UniTask<bool> SkipCurrentMusic(MusicChangeKind kind)`
  - `public void Stop()` / `Pause()` / `UnPause()`
  - `public void SetRepeat(bool isrepeat)` / `SetShuffle(bool isShuffle)`
  - `public float GetCurrentMusicProgress()` / `SetMusicProgress(float progress)`
  - `public bool RegisterFavoriteMusic(GameAudioInfo gameAudioInfo)` / `UnregisterFavoriteMusic(GameAudioInfo gameAudioInfo)`
  - `public bool ExcludeFromPlaylist(GameAudioInfo gameAudioInfo)` / `IncludeInPlaylist(GameAudioInfo gameAudioInfo)`
  - `public void RemoveLocalMusicItem(GameAudioInfo music)`
  - `public void SwapAfter(GameAudioInfo target, GameAudioInfo origin)`
  - `public void SetIsAllExcludedMusicFromPlaylist()`
  - `private bool IsMusicVisible(GameAudioInfo music)`
  - `private void AddMusicEntry(GameAudioInfo music)`（订阅 `music.OnAudioClipReplaced`）

### 2.2 `Bulbul.GameAudioInfo`
- **公开字段**：
  - `public bool IsUnlocked;`
  - `public bool IsManualUnlocked;`
  - `public AudioMode PathType;`（枚举：`Normal`, `LocalPc`）
  - `public AudioClip AudioClip;`
  - `public AudioTag Tag;`（`[Flags]` 枚举：`Original = 1, Special = 2, Other = 4, Favorite = 8, Local = 16, All = 31`）
  - `public string Title;`
  - `public string Credit;`
  - `public string LocalPath;`
  - `public string UUID;`
- **关键方法**：
  - `public static UniTask<GameAudioInfo> CreateLocalFileAsync(string filePath, string uuid, CancellationToken ct)`
  - `public UniTask<AudioClip> GetAudioClip(CancellationToken ct)`
  - `public void PrepareAudioClipForPlayback()`
  - `public void UnloadAudioClip()`
  - `public void Dispose()`
  - `private void SetStreamingLocalAudioClip(StreamingLocalAudioClip streamingClip)`（`StreamingLocalAudioClip` 为 internal 类型，可通过 `AccessTools` 转移或通过反射读取字段）

### 2.3 播放器与 UI 与存档
- `KanKikuchi.AudioManager.MusicManager`（继承 `AudioManager<MusicManager>`，`SingletonMonoBehaviour<MusicManager>.Instance`）
  - `public AudioPlayer GetPlayer(AudioClip clip)`
  - `KanKikuchi.AudioManager.AudioPlayer.PlayedTime`（当前播放秒数）
  - 注意：`AudioPlayer.Update()` 根据 `AudioSource.isPlaying` / `time` / `clip.length` 判断是否播完；音频流通过游戏自身 `AudioMixerGroups.MusicGroup` 输出，因此只要走 `MusicService` 播放，游戏原生音乐音量滑块、静音和淡入淡出即自动生效。
- `Bulbul.FacilityMusic`
  - `public void Setup()`（在此 `Postfix` 中安全获取 `MusicService` 实例及 UI 引用）
  - `public MusicService MusicService { get; }`
  - `private IMusicTabUI _musicTabUI;`（实际组件为 `Bulbul.MusicPlayListTabUI`）
  - `private IMusicListUI _musicListUI;`（实际组件为 `MusicUI`）
  - `Bulbul.MusicPlayListTabUI` 私有按钮字段：`private Button _tagButton;`、`private MusicImportButton _fileImportButton;`、`private MusicImportButton _folderImportButton;`；方法 `SetInteractableFileImport(bool)`、`SetInteractableFolderImport(bool)`。
  - `MusicUI`（全局命名空间）私有字段：`private MusicPlayListView _musicPlayListView;`、`private TextMeshProUGUI _musicTitleText;`、`private TextMeshProUGUI _artistNameText;`、`private Button _shuffleButton;`、`private Button _loopButton;`。
- `Bulbul.ScenarioReader`
  - `private UniTaskVoid PlayMusicFromMusicService(GameAudioInfo gameAudioInfo)`：剧情会直接传入官方 `GameAudioInfo` 播放指定配乐。
- `Bulbul.SaveDataManager`
  - `SaveDataManager.Instance.MusicSetting`（`MusicSettingV2`）与 `LocalMusicSetting`：会参与本地和云存档同步（`SaveDataSync`），**严禁写入任何酷狗曲目 UUID 或路径**。

---

## 3. 核心架构与功能需求

### 3.1 酷狗双方案接口层（`IKugouClient`）
插件配置与 UI 需支持在 **两种后端模式** 间随时切换，且均需提供「连通性测试」：
1. **方案一：`NativeKugouClient`（默认可选，纯 C# 零外部依赖直连）**
   - 直接用 `HttpClient` 请求酷狗公开及移动端/网页端 API：
     - **歌单解析**：支持解析酷狗分享短链（跟随重定向取 `specialid` 或 `global_collection_id`）、网页歌单 URL、酷狗码、纯数字歌单 ID，并循环分页拉取歌单全部曲目。
     - **搜歌 / 搜歌单**：关键词搜索单曲（返回 `hash`、`album_id`、歌名、歌手、时长、VIP 标记）和搜索公开歌单。
     - **账号登录**：支持手动导入 Cookie/Token（`userid` + `token` + 可选 `dfid`/`mid`）以及酷狗二维码生成/状态轮询登录，支持游客模式兜底。
     - **原生音源直链解析**：根据 `hash`、`album_id` 与目标音质（`128k` MP3 / `320k` MP3 / `M4A`）请求真实流媒体直链。若响应为试听片段（返回时长显著短于曲目元数据时，如 `<= 65s` 且原曲 `> 90s`），在 `KugouPlayUrlResult.IsTrial` 标记为 `true`。
2. **方案二：`ExternalApiKugouClient`（本地开源 `KuGouMusicApi` HTTP 网关）**
   - 配置项：`ApiBaseUrl`（默认 `http://127.0.0.1:3000`，兼容主流开源 Node 版 `KuGouMusicApi` 风格 REST API）。
   - 支持的功能：
     - 二维码扫码登录（`/login/qr/key`、`/login/qr/create`、`/login/qr/check`）与持久化保存返回的 `token` / `userid` / `cookie`；
     - 账号信息和个人歌单列表同步（`/user/detail`、`/user/playlist`，自动拉取「我喜欢的音乐」、自建歌单与收藏歌单）；
     - 歌单详情与全量曲目（`/playlist/detail` + `/playlist/track/all` 分页）；
     - 搜歌 / 搜歌单（`/search`）；
     - 原生音质播放地址（`/song/url`），支持概念版每日自动领 VIP（可加「手动/自动领概念版 VIP」按钮调用 `/youth/vip`）；
     - 请求统一通过 Header 或 Query 传递持久化认证凭证。

### 3.2 音频下载缓存与解码桥接（`AudioCacheService`）
- **禁止直接将远程 HTTP URL 传给 `UnityWebRequestMultimedia(..., streamAudio: true)`**：必须先下载到本地缓存文件再加载，确保进度条拖动、总时长计算、`AudioPlayer` 播完检测 100% 稳定。
- **缓存目录**：`<BepInExRoot>/cache/kugou/{hash}_{quality}.{ext}`（通过 `.tmp` 临时文件写入成功后原子重命名）。
- **LRU 淘汰与限额**：支持配置 `MaxCacheSizeMB`（默认 `500` MB），每次写入后检查总大小，超出则按最后访问/修改时间清理最旧文件；UI 提供「立即清空缓存」按钮。
- **桥接游戏原生解码**：
  - `.mp3` / `.ogg` / `.wav`：用 `UnityWebRequestMultimedia.GetAudioClip(new Uri(localPath).AbsoluteUri, audioType)`（设 `streamAudio = true`）加载为 `AudioClip`。
  - `.m4a`：调用 `GameAudioInfo.CreateLocalFileAsync(localPath, targetTrack.UUID, ct)` 让游戏原生的 `BulbulLocalAudioDecoder.dll`（Windows Media Foundation）解码，然后把生成的 `AudioClip` 与私有字段 `_streamingLocalAudioClip` 安全迁移到当前播放条目上，并正确处理释放。
- **后台预加载下一首**：当某首酷狗歌曲开始播放时，触发后台低优先级任务提前解析并缓存当前播放列表的下一首（`n+1`），失败静默忽略。

### 3.3 双音源无损切换与防踩坑规则（`SourceSwitchService` + `MusicPatches`）

1. **酷狗曲目识别**：
   - 使用 `ConditionalWeakTable<GameAudioInfo, KugouTrack>` 关联每一个生成的酷狗 `GameAudioInfo` 实例，同时 `UUID` 固定采用 `"kugou:{hash}"` 格式，保证与官方曲目、剧情曲目绝对区分。
   - **字段要求**：构造 `GameAudioInfo` 时 `Title` 和 `Credit` **绝不能为空或 null**（至少给 `"未知歌曲"` / `"未知歌手"`），避免 `FacilityMusic.OnChangeMusic` 输出错误日志；`Tag` 设为 `AudioTag.Original`（**不要**带 `AudioTag.Local`，否则列表会出现游戏自带的移除本地文件按钮）。
2. **异步懒加载播放拦截（核心坑点）**：
   - 游戏列表点击和指定播放调用的是 `MusicService.PlayArugumentMusic(GameAudioInfo, MusicChangeKind)` 以及 `MusicService.PlayMusicInPlaylist(int)`——**这两个原版方法都是同步直接取 `AudioClip`，不会 await `GetAudioClip`**。
   - **Harmony Prefix 处理**：
     - 如果目标 `audioInfo` 属于酷狗曲目且 `audioInfo.AudioClip == null`：返回 `false` 拦截本次同步播放，取消上一次未完成的 `CancellationTokenSource`（防连点串歌），在后台通过 `UniTask` 解析直链 + 缓存 + 创建 `AudioClip`；完成后切回主线程（`await UniTask.SwitchToMainThread()`），将 `audioInfo.AudioClip` 赋值，再触发原始播放逻辑。
     - `GameAudioInfo.GetAudioClip(CancellationToken ct)` 也需加 Prefix：当 `MusicService.PlayNextMusic` / `PlayBackMusic` / `SkipCurrentMusic` 自动下一首走异步流程时，若是酷狗曲目且 `AudioClip == null`，直接接管返回异步构建后的 `UniTask<AudioClip>`。
   - **试听与坏链自动跳过**：
     - 若开启 `SkipTrialTracks`（默认开启），当解析出 `IsTrial == true` 或无法获取直链时，自动跳过并调用 `MusicService.PlayNextMusic(1, MusicChangeKind.Auto)`；必须内置连续失败计数器（例如最多连续跳过 5 首），防止全灰歌单无限死循环请求。
3. **官方 ⇄ 酷狗切换与快照隔离**：
   - `MusicService.Load` Postfix 时保存官方完整曲目快照（`_officialAllMusic`）。
   - 切换音源时：
     - 保存当前音源上次播放曲目 UUID；
     - 停止当前播放并释放旧的酷狗 `AudioClip` 内存；
     - 替换 `MusicService._allMusicList`、`shuffleList` 与 `CurrentPlayList`（注意若处于洗牌模式，重建 `shuffleList`）；
     - 切到酷狗模式时，通过 `MusicPlayListTabUI.SetInteractableFileImport(false)` / `SetInteractableFolderImport(false)` 禁用本地导入按钮；切回官方时恢复。
     - 首次进入房间且配置记住上次音源为 `Kugou` 时，自动切换并加载上次选中的酷狗歌单。
4. **存档彻底隔离**：
   - 当处于酷狗模式或传入曲目为酷狗曲目时，Harmony 拦截：
     - `MusicService.RegisterFavoriteMusic` / `UnregisterFavoriteMusic`：改为更新插件本地 JSON 收藏列表并刷新 `Tag` 的 `AudioTag.Favorite` 位，返回 `true`，不写入 `SaveDataManager`；
     - `MusicService.ExcludeFromPlaylist` / `IncludeInPlaylist`：改为更新插件本地排除列表，返回 `true`；
     - `MusicService.SwapAfter` / `RemoveLocalMusicItem`：拦截，改写插件本地歌单顺序或屏蔽列表；
     - `MusicService.IsMusicVisible`：若传入为酷狗曲目，直接按插件自身排除列表决定可见性，不受游戏官方 `AudioTag` 筛选影响。
5. **剧情兼容**：
   - `ScenarioReader` 调用 `PlayMusicFromMusicService` 传入官方 `GameAudioInfo` 时，因不属于 `KugouTrack`，Patch 直接放行，保证剧情正常插播音乐。

### 3.4 混合 UI 设计
1. **原生 UGUI 轻量按钮注入**：
   - 在 `FacilityMusic.Setup` Postfix 中，尝试从 `MusicPlayListTabUI` 获取 `_tagButton` 或 `_fileImportButton` 作为模板克隆 1～2 个按钮（`[官方/酷狗切换]` 与 `[酷狗面板]`），挂载点击事件。
   - 整个 UGUI 注入必须放在独立的 `try/catch` 保护块中：若未来游戏版本改动导致节点查找失败，仅记录 Warning，不影响快捷键 `F8` 和悬浮窗使用。
2. **IMGUI 悬浮管理窗（`KugouWindow.cs`）**：
   - 快捷键（默认 `F8`，可配置）显隐；支持鼠标拖动窗体并保存坐标到配置；打开时可选拦截鼠标穿透或保持简洁不挡视野。
   - **必须显式初始化中文字体**：`Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14)` 赋值给窗口自定义 `GUIStyle` / `GUI.skin.font`，解决中文歌名与提示变成方框问题。
   - **4 个标签页**：
     1. **歌单**：音源模式切换大按钮（官方 ⇄ 酷狗）、粘贴酷狗链接/酷狗码/ID 导入、已保存歌单列表（切换/删除/刷新）、已登录用户个人歌单同步列表。
     2. **搜歌**：搜索框（单曲 / 歌单切换）、搜索结果列表（双击或点击播放、一键添加到当前自建酷狗列表）。
     3. **登录**：显示当前登录态（昵称 / VIP / 游客）；二维码扫码区（通过 `Texture2D.LoadImage` 显示二维码图片 + 自动轮询登录状态）；手动 Cookie/Token 粘贴框；`ExternalGateway` 模式下提供一键领取概念版 VIP 按钮。
     4. **设置**：接口模式切换（`NativeDirect` ⇄ `ExternalGateway`）、`ApiBaseUrl` 输入与测试连接、目标音质（128k / 320k / M4A）、自动跳过试听/无版权开关、自动预加载下一首开关、缓存容量上限与一键清空按钮、当前状态日志条。

---

## 4. `BepInEx` 持久化配置项（`ConfigEntry` 清单）

- `[General] ActiveSource`: `"Official"` 或 `"Kugou"`（默认 `"Official"`，切歌/退出自动保存）
- `[General] ActivePlaylistId`: 上次使用的酷狗歌单 ID
- `[General] ToggleHotkey`: `KeyCode.F8`
- `[General] AutoResumeLastTrack`: `bool`（默认 `true`）
- `[KugouApi] BackendMode`: `"NativeDirect"` 或 `"ExternalGateway"`（默认 `"NativeDirect"`）
- `[KugouApi] ExternalApiBaseUrl`: `"http://127.0.0.1:3000"`
- `[KugouApi] PreferredQuality`: `"320"` / `"128"` / `"m4a"`（默认 `"320"`）
- `[KugouApi] SkipTrialTracks`: `bool`（默认 `true`）
- `[Auth] UserId` / `[Auth] Token` / `[Auth] Cookie` / `[Auth] Dfid`
- `[Cache] MaxCacheSizeMB`: `int`（默认 `500`）
- `[Cache] PreloadNextTrack`: `bool`（默认 `true`）
- `[UI] WindowPosX` / `[UI] WindowPosY`
