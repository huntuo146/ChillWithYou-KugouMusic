<div align="center">

# ChillWithYou-KugouMusic

**为桌面伴侣游戏《Chill with You: Lo-Fi Story》打造的酷狗音乐双音源插件与一键安装器**

[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.x-4CE0B3?style=flat-square)](https://github.com/BepInEx/BepInEx)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-57C7FF?style=flat-square)](#)
[![Framework](https://img.shields.io/badge/.NET-netstandard2.1%20%7C%20net8.0-8E7CFF?style=flat-square)](#)
[![License](https://img.shields.io/badge/License-MIT-FFB86C?style=flat-square)](./LICENSE)
[![Release](https://img.shields.io/github/v/release/huntuo146/ChillWithYou-KugouMusic?style=flat-square&color=FF6F9E&label=Release)](https://github.com/huntuo146/ChillWithYou-KugouMusic/releases)

</div>

---

## 插件简介

**ChillWithYou-KugouMusic** 是一款基于 **BepInEx 5 + HarmonyX** 开发的非官方第三方音频扩展插件，围绕《Chill with You: Lo-Fi Story》的夜间书桌陪伴氛围设计（**Midnight Desk Lo-Fi** 视觉体系），支持在游戏原生 Lo-Fi 音乐与酷狗音乐之间无感切换，并提供独立双栏图形化安装器及游戏内 GitHub 在线更新能力。

### 核心功能

- **双音源无感切换**：顶部胶囊开关及游戏原生面板注入入口支持一键切换「官方 / 酷狗」音源；切回官方时不破坏原曲目列表，且退出游戏自动暂停并清理自启播放态。
- **双接入后端**：
  - `NativeDirect`：纯 C# 直连酷狗接口，开箱即用。
  - `ExternalGateway`：联动本地 `MakcRe/KuGouMusicApi` 网关，解锁二维码扫码登录、同步个人收藏/自建歌单及概念版 VIP 权益领取。
- **本地网关随游戏静默启停**：进入游戏时后台自动拉起本地 `KuGouMusicApi` 服务（无需手动打开黑框窗口），退出游戏自动回收进程。
- **歌单深度浏览与单曲点播**：支持通过酷狗分享短链 / 歌单 ID / 账号同步导入歌单；点击歌单卡片可进入详情页查看全部曲目、按歌名/歌手筛选并点击任意一首直接播放。
- **专辑封面自动抓取与本地缓存**：通过 .NET `HttpClient` 拉取曲目与歌单封面并在本地磁盘缓存，歌单中缺少封面 URL 的歌曲会自动补全并回写本地存储。
- **可自由拖动桌面悬浮歌词框**：可选开启独立桌面歌词悬浮窗，支持自由拖拽、位置锁定（鼠标穿透防误触）、字号/宽度与时间偏移微调，以及「无 / 柔雾 / 玻璃」三种底衬。
- **独立 `.exe` 安装中心与游戏内在线更新**：
  - 自动扫描 Steam 注册表（`libraryfolders.vdf` / `AppID 3548580`）定位游戏根目录，一键部署全部依赖并保留用户个人配置与歌单；
  - 本机缺少 Node.js 时支持自动下载便携版 `node.exe` 至网关目录；
  - 插件「设置 → GitHub 在线更新」与安装器均支持从 [GitHub Releases](https://github.com/huntuo146/ChillWithYou-KugouMusic/releases) 拉取最新版本并自动完成退出替换升级。

---

## 快速安装

### 方式一：一键安装器（推荐）

1. 前往 [**Releases 页面**](https://github.com/huntuo146/ChillWithYou-KugouMusic/releases) 下载 `ChillWithYou-KugouMusic-Installer.exe`。
2. 双击运行安装器，阅读并确认「安装前须知 · 开源构建说明与免责声明」。
3. 安装器会自动扫描《Chill with You》游戏根目录并检测缺失环境：
   - 保持默认勾选即可一并安装 BepInEx 5、插件本体、`KuGouMusicApi` 本地网关；若本机未安装 Node.js，将自动下载便携版运行环境。
4. 点击 **「一键安装 / 更新」**，完成后直接启动游戏。

> 运行环境提示：单文件安装器基于 `.NET 8 Desktop Runtime (win-x64)` 运行，若 Windows 提示缺少桌面运行时，按系统弹窗提示安装一次即可。

### 方式二：手动安装

1. 从 Releases 下载完整压缩包 `ChillWithYou-KugouMusic-vX.Y.Z.zip`。
2. 将压缩包内容完整解压至游戏根目录（包含 `Chill With You.exe` 的文件夹）。
3. 若需使用本地网关扫码与个人歌单且本机未安装 Node.js，请安装 Node.js 18+（或将便携版 `node.exe` 放入 `KuGouMusicApi/` 目录）。
4. 启动游戏，按默认热键 **`F8`**（或点击游戏音乐面板上的酷狗按钮）打开插件主界面。

---

## 目录结构

```text
├── KugouMusicMod/              # BepInEx 5 插件本体源码（netstandard2.1）
│   ├── Models/                 # 歌单、曲目、接口状态与版本常量定义
│   ├── Patches/                # Harmony 游戏音乐服务与 UI 注入补丁
│   ├── Services/               # 双端酷狗 API、网关托管、封面/音频缓存、歌词与 GitHub 更新服务
│   ├── UI/                     # Midnight Desk Lo-Fi 主题引擎、播放条、歌词浮窗与 4 大页面
│   ├── Plugin.cs               # BepInEx 插件生命周期入口与配置绑定
│   ├── DEEPSEEK_SPEC.md        # 早期由 Gemini 编写并核验的技术架构规范
│   └── ChillWithYou.KugouMusic.csproj
├── KugouMusicInstaller/        # 独立 WPF 安装与更新中心源码（net8.0-windows）
│   ├── Models/                 # 安装诊断模型与配置项
│   ├── Services/               # Steam 路径扫描器、GitHub Releases 拉取与部署/热替换引擎
│   ├── MainWindow.xaml(.cs)    # 双栏 Midnight Desk 界面与安装前声明浮层
│   └── build-release.ps1       # 一键编译插件、生成 Release ZIP 与发布单文件 EXE 脚本
├── 启动酷狗网关.bat             # 手动调试用本地网关启动批处理
├── LICENSE                     # MIT 开源许可证
└── README.md
```

---

## 从源码构建

**前置环境**：[.NET 8 SDK](https://dotnet.microsoft.com/)、游戏本体目录（含 `Chill With You_Data/Managed/`）及已解压的 `BepInEx/core/`（与可选打包的 `KuGouMusicApi/`）。

```powershell
# 1. 仅编译插件 DLL（Release 模式会自动部署到上级 BepInEx\plugins\）
dotnet build .\KugouMusicMod\ChillWithYou.KugouMusic.csproj -c Release

# 2. 一键构建完整发布包与单文件安装器（输出到 Release\ 目录）
powershell -ExecutionPolicy Bypass -File .\KugouMusicInstaller\build-release.ps1
```

---

## 仓库贡献与 AI 协同分工说明

本项目由 **Google Antigravity** 与 **DeepSeek Harness** 协同工作流驱动完成，各模型在实际研发流程中的分工如下：

| 参与模型 / 工具 | 担任角色 | 具体完成的工作 |
| :--- | :--- | :--- |
| **Gemini** (`Google Antigravity`) | **架构规划、逆向工程、疑难排错与全栈落地** | 1. 逆向分析游戏 `Assembly-CSharp.dll` 与 `AudioManager.dll` 音频链路，搭建 BepInEx 5 + `netstandard2.1` 编译环境并编写 [DEEPSEEK_SPEC.md](KugouMusicMod/DEEPSEEK_SPEC.md)。<br>2. 逐阶段人工审查与验证 DeepSeek 代码产出，排查并修复 TMP 字库 `Base_SC` 缺失告警、面板热键与扫码登录报错、Unity Release 下屏蔽明文 HTTP 封面导致封面空白等关键 Bug。<br>3. 主导开发并落地酷狗账号同步与分享短链导入修复、本地网关随游戏静默启停、可自由拖动悬浮歌词框、退出自动暂停与防启动续播、歌单内全部歌曲详情浏览/点播、封面自动补全缓存，以及基于 GitHub Releases 的热更新系统与 `.exe` 安装器（后期与 `Gemini 4 Neon` 协同完成安装器双栏 WPF 重构与打包验证）。 |
| **DeepSeek** (`deepseek-harness`) | **IL 签名核验与初始功能编码构建** | 1. 基于 `DEEPSEEK_SPEC.md` 对游戏真实程序集执行只读反编译核验（核对 60+ 符号签名），提出 6 项关键 IL 修正（包括 `ObservableList` 原地更新、改走 `MusicService.Load` 与官方条目防卸载保护等）并完成酷狗 4 类接口实测。<br>2. 分三阶段（**P0** 插件骨架与 Harmony 补丁打点 → **P1** 双接口客户端 / 缓存 / 音源切换服务 → **P2** 原生按钮注入与初版 IMGUI 四页界面）完成全套可编译的初版插件代码实现。 |
| **Claude** (`Claude Opus 5.5`) | **主插件 UI/UX 视觉架构设计** | 针对初版控制台样式进行体验升级设计，制定贴合游戏氛围的 **「Midnight Desk Lo-Fi」** 视觉设计语言与架构规范（深空磨砂玻璃层次、`KugouTheme` 色彩字体令牌、程序化圆角与黑胶动效、侧边导航栏、底部 Now Playing 播放栏、Mini 胶囊模式及 `KugouInputBlocker` 防鼠标穿透设计），随后由 Gemini 完成代码实现。 |

---

## 开源依赖与致谢

- [**BepInEx 5**](https://github.com/BepInEx/BepInEx) / [**HarmonyX**](https://github.com/BepInEx/HarmonyX) / [**UnityDoorstop**](https://github.com/NeighTools/UnityDoorstop)：Unity Mono 游戏注入与运行期补丁框架
- [**MakcRe/KuGouMusicApi**](https://github.com/MakcRe/KuGouMusicApi)（MIT License）：酷狗音乐 / 概念版 Node.js 协议网关实现
- **Newtonsoft.Json** / **UniTask**：JSON 解析与 Unity 异步任务调度

---

## 免责与版权声明

1. 本项目为非官方开源扩展工具，与《Chill with You: Lo-Fi Story》开发商及酷狗音乐官方均无任何隶属或授权关系，**仅供个人技术研究与单机休闲娱乐使用**，严禁用于任何商业用途、收费分发或绕过正版版权限制。
2. 插件检索、播放与缓存的所有音乐音频、歌词、封面及相关数据，其**版权与一切合法权益均归属于酷狗音乐（KuGou Music）及原词曲/唱片版权方**。请尊重音乐版权，支持正版平台。
3. 使用扫码登录、Cookie 鉴权、概念版 VIP 权益领取或接口请求均存在触发平台风控策略的可能。**因使用本插件直接或间接导致酷狗音乐账号被限流、冻结或封禁等任何后果，均与本项目作者、贡献者及关联开源项目无关**，由使用者自行承担全部责任。
4. 下载、安装或运行本插件与安装器，即视为您已完整阅读并默认同意遵守以上全部守则。

---

<div align="center">

**Built with AI-Assisted Engineering Harnesses**

[![Google Antigravity](https://img.shields.io/badge/Google-Antigravity-4285F4?style=for-the-badge&logo=google&logoColor=white)](https://github.com/huntuo146/ChillWithYou-KugouMusic)
[![deepseek-harness](https://img.shields.io/badge/deepseek--harness-IL%20Verify%20%26%20Code-4D6BFE?style=for-the-badge)](https://github.com/huntuo146/ChillWithYou-KugouMusic)

[![Gemini](https://img.shields.io/badge/Gemini-Architecture%20%26%20Implementation-57C7FF?style=flat-square&logo=googlegemini&logoColor=white)](#仓库贡献与-ai-协同分工说明)
[![DeepSeek](https://img.shields.io/badge/DeepSeek-IL%20Audit%20%26%20Core%20P0--P2-536DFE?style=flat-square)](#仓库贡献与-ai-协同分工说明)
[![Claude](https://img.shields.io/badge/Claude%20Opus%205.5-Midnight%20Desk%20UI%20Design-8E7CFF?style=flat-square&logo=anthropic&logoColor=white)](#仓库贡献与-ai-协同分工说明)

</div>
