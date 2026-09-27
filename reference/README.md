# 维护者参考（reference/）

这个目录里的东西**不是程序源码**，也不面向使用者。它是读代码之前先读得懂的那层背景：
数据是怎么组织起来的、为什么这么分、实测数字是多少。

程序源码在仓库根的 `App.xaml` / `MainWindow.xaml` / `Models/` / `Services/` /
`ViewModels/` / `Views/` / `Controls/` / `Styles/` / `Assets/`。那之外的分层是：

| 位置 | 是什么 | 进版本库 |
| --- | --- | --- |
| `reference/` | 数据模型的逆向分析结论，读代码前的前置知识 | 是 |
| `docs/`（不存在）| —— 分析件已从 `docs/` 迁出，避免与「用户文档」混在一个目录 | — |
| `tools/` | 可复用的构建、启动、发布、冒烟核对脚本 | 是 |
| `tools/oneshot/` | 一次性量测脚本，带写死的窗口标题和参数 | 否（`.gitignore`） |
| `.workbuddy/` | 外部 agent 工具的工位：逐页审计、实施契约、事故恢复脚本、它的日记 | 否（`.gitignore`） |
| `screenshots/` | 逐轮 UI 验收截图 | 否（`.gitignore`） |

## 为什么这么切

判据来自姊妹仓 `sekaisync`（CLI 与数据层主仓）的同类决定：它把 `docs/`、`HANDOFF.md`、
`work/`、`.workbuddy/` 全部排除在版本库之外，只保留 7 个根级跟踪文件
（`.gitignore` `LICENSE` `README.md` `README.zh-CN.md` `pyproject.toml` `sekaisync.png`
`settings.json`）。理由是「开发过程不进发布树」。

本仓照这个方向切，但有一处不同：**数据模型的结论留在库里**。
理由很实际——这些数字（`web_pages` 752,364 行、`entities` 66,435 行、三张表不同层级）
是对着真实 SQLite 库逐条量出来的，重做一遍要一套完整 store；而审计与实施契约不同，
它们是**某一轮改动的过程记录**，做完就过期，且带维护机的绝对路径。

## 本目录的文件

- `data-taxonomy.md` — 按游戏资产逻辑重切 `entities` / `web_pages` / `meta`：三张表性质不同，
  不该在界面里平级陈列。
- `data-provenance.md` — 爬虫管线分层，以及两个资料站的数据怎么区分。

两份都是**特定时间点对着特定库的实测**。数字会随同步过期，结论的分层不会。
引用它们里的数字时先复核，别当常量用。
