<!-- readme-brand:start -->
<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/readme/header-dark.svg">
  <img src=".github/readme/header-light.svg" alt="" width="1280">
</picture>
<!-- readme-brand:end -->

# SekaiSync Desktop for Windows

[English](README.md) | 中文

[![Release](https://img.shields.io/badge/Release-0.4.1--alpha-006F78?style=flat&labelColor=17263B)](SekaiSync.Desktop.csproj) [![Runtime](https://img.shields.io/badge/Runtime-.NET%208-4F6175?style=flat&labelColor=17263B)](SekaiSync.Desktop.csproj) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-4F6175?style=flat&labelColor=17263B)](SekaiSync.Desktop.csproj) [![License](https://img.shields.io/badge/License-MIT-AC246D?style=flat&labelColor=17263B)](LICENSE)

<!-- readme-navigation:start -->
<p>
  <a href="#readme-overview">项目介绍</a> ·
  <a href="#readme-section-02">快速开始</a> ·
  <a href="#readme-section-04">更多说明</a>
</p>
<!-- readme-navigation:end -->

<a id="readme-overview"></a>

本项目是 [SekaiSync](https://github.com/omoinoki/sekaisync) 的 WinUI 3 桌面客户端，用于浏览和管理《世界计划 缤纷舞台！》（Project SEKAI）的本地知识库。知识库由主项目 CLI 同步生成。客户端提供公告、剧情、角色台词、用语术语、游戏实体与底层数据库原始表的浏览功能，支持多语言阅读与对照。

在同一界面中，亦可按需触发同步，或启动本地服务将完整知识库提供给 AI 智能体使用。

![剧情页，浅色主题](media/screenshot-story-light.png)

![同一个页面，深色主题](media/screenshot-story-dark.png)

<a id="readme-section-01"></a>

## 里面有什么

应用包含九个功能页面，按操作性质分为浏览与管理两大部分。浏览页面只读；管理页面提供同步、缓存和服务操作：

**数据浏览**：
- **官方公告**：内联展示官方公告原文。
- **剧情与卡牌**：提供日、英、简中、繁中、韩五服故事文本的并排对照视图。
- **角色台词与用语**：独立页面检索各角色台词及专业术语。
- **游戏实体**：覆盖十二个核心资产域的结构化实体数据。
- **原始数据表**：供直接检索与核对 SQLite 数据库底层存储结构。

**数据管理与服务**：
- 支持同步 Master Data 与官方公告，查看各区服缺失类目。
- 支持按需缓存故事正文。
- 提供本地服务控制台，可通过 MCP 或 HTTP 协议将知识库挂载至外部客户端或智能体。

任务日志仅在同步和接入两页底部的共享区域显示，页面切换不会重置或丢失运行日志。关闭窗口时应用默认常驻系统托盘；重复启动时将激活已有窗口，防止多实例运行冲突。

<a id="readme-section-02"></a>

## 跑起来

```powershell
dotnet build SekaiSync.Desktop.csproj -p:Platform=x64
.\bin\x64\Debug\net8.0-windows10.0.19041.0\SekaiSync.Desktop.exe
```

构建环境依赖 .NET 8 SDK 与 Windows App SDK 1.8（由 NuGet 自动拉取）。普通构建产物已包含 Windows App SDK 相关组件，但在目标机器上仍需预先安装 .NET 8 运行时；部署时请完整复制输出目录。

知识库目录可在应用设置中手动指定；若未设置，程序将自动检索运行目录附近的 `store/` 目录。关于本地知识库的初始同步流程及命令行详细用法，请参考 [主项目文档](https://github.com/omoinoki/sekaisync)。

<a id="readme-section-03"></a>

## 先说一句

客户端交互界面目前仅提供简体中文。所展示的游戏数据则支持日语、英语、简体中文、繁体中文与韩语的五语阅读与对照。

<a id="readme-section-04"></a>

## 许可

采用 [MIT 许可证](LICENSE) 分发。
