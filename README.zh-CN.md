# SekaiSync Desktop

> [English](README.md) | 简体中文

![构建状态](https://github.com/omoinoki/sekaisync-winui3/actions/workflows/build.yml/badge.svg)
[![许可证：MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

这是 [SekaiSync](https://github.com/omoinoki/sekaisync) 的 WinUI 3 客户端，用来浏览《世界计划 缤纷舞台》的本地资料库。库是主项目那条命令行同步下来的，这个程序管的是你读得动：公告、剧情、台词、术语、实体、原始表，五种语言并排摆开，不用在几个标签页之间来回切。

想跑一次同步，或者把整份库交给 AI 助手用，也都在同一个窗口里办。

![剧情页，浅色主题](media/screenshot-story-light.png)

![同一个页面，深色主题](media/screenshot-story-dark.png)

## 里面有什么

九个页面。侧栏上半截只读，下半截才会动你的数据。

**读**：公告连原文一起摊在右栏；剧情和卡牌故事按五服并排对照；台词、术语、十二个资产域的实体各占一页；想知道库里究竟存了什么，还有原始表可以直接翻。

**做**：同步主数据和公告、看看缺了哪些类目、缓存故事正文，或者起一个本机服务，让客户端通过 MCP 与 HTTP 来问。

任务输出统一落在底部那条共享的停靠区里，只在同步和接入这两页出现，切来切去不会把你正读着的日志冲掉。关掉窗口，程序退回系统托盘候着；再启动一次，它把原来那个窗口带到前面，不会再开一个。

## 跑起来

```powershell
dotnet build SekaiSync.Desktop.csproj -p:Platform=x64
.\bin\x64\Debug\net8.0-windows10.0.19041.0\SekaiSync.Desktop.exe
```

需要 .NET SDK 8 和 Windows App SDK 1.8，后者由 NuGet 带来。产物不打包、自包含，拷到别的机器上不用再装运行时。

库放在哪里，设置页里指一下就行；留空的话它自己在附近找。至于这份库最初怎么建起来、里面有些什么、命令行还能做点什么，主项目里写得比我细。

## 先说一句

界面目前只有简体中文。数据不是：日、英、简中、繁中、韩五语都能读、能对照。

## 许可

MIT，见 [LICENSE](LICENSE)。
