# 数据出处：爬虫管线分层与两个资料站的区分

配套文档：`data-taxonomy.md`（按游戏资产逻辑分类）。本文处理另外两个维度——
**数据是怎么抓进来的**，以及**两个资料站的数据怎么区分**。

所有数字均实测自 `sekaisync-handoff-2026-08-14/store/kb/sekaisync.db`（752,364 行 web_pages）。

---

## 0. 结论先行

三个会直接导致代码写错的事实：

1. **`source` 不是「站点」，是「实例 + 抓取通道」。** 库里 4 个 source 值，对应 2 个站 + 2 个覆盖层通道。把 `altsource_ms` 和 `altsource_ms_translation` 当成两个平级站点就错了。
2. **两站 `id` 的地域段写法不同。** ms 是 `web:altsource_ms:zh-cn:...`，sv 是 `web:altsource_sv:cn:...`。跨站对齐必须先归一化，否则永远对不上。
3. **抓取时间差两周。** ms 抓于 2026-08-13~08-22，sv 抓于 2026-09-05~09-06。`crawled_at` 是唯一的新鲜度信号，UI 里不显示就等于用户不知道自己在看旧数据。

---

## 1. 爬虫抓取：四层写入，不是一张平表

### 1.1 四个写入通道

| source | 行数 | 抓取日期 | 域名 | 身份 |
|---|---:|---|---|---|
| `altsource_ms` | 374,074 | 2026-08-13 ~ 08-22 | metadata.exmeaning.com (335,849)、pjsk.moe (38,225) | Moesekai 主数据 |
| `altsource_sv` | 373,453 | 2026-09-05 ~ 09-06 | sekai-world.github.io (326,652)、storage.sekai.best (46,801) | Sekai Viewer 主数据 |
| `altsource_ms_translation` | 4,730 | 2026-08-13 ~ 08-22 | translation.exmeaning.com | **Moesekai 译文覆盖层** |
| `altsource_sv_i18n` | 107 | 2026-08-13 | i18n-json.sekai.best | **Sekai Viewer i18n 覆盖层** |

后两个不是第三个站点，是叠加在前两个之上的译文层。它们的 `overlay = 1`，而
`webindex.py:59` 的 `is_auxiliary_page()` 定义是：

```python
def is_auxiliary_page(page: dict[str, Any]) -> bool:
    return bool(page.get("auxiliary", False)) or bool(page.get("overlay", False))
```

**覆盖层行在管线语义里属于「辅助页」**，应该默认排除。任何不加 `overlay = 0` 条件的
剧情列表都会混进 4,837 条格式不同的译文行。

### 1.2 depth 是「抓几种剧情」的旋钮

`crawler.py` 里两站的 depth 分批完全对称：

| depth | 新增内容 |
|---|---|
| ≥ 1 | 活动剧情（ms 同时抓 translation 覆盖层）+ 组合剧情 |
| ≥ 2 | 卡牌剧情 |
| ≥ 3 | 虚拟 Live |
| ≥ 4 | 特别剧情 |

它不是「页面深度」。所以「抓取深度」这个说法在 UI 里会误导，应该显示成「抓取范围」。

### 1.3 CANONICAL_KINDS：管线自己承认的正文白名单

`webindex.py:43` 定义了 10 个「正文 kind」：

```
event_story  unit_story  card_story  special_story  virtual_live
area_talk    self_intro  home_line   mysekai_talk   mysekai_tweet
```

实测：

| 来源 | 行数 |
|---|---:|
| altsource_ms | 67,917 |
| altsource_sv | 109,539 |
| altsource_ms_translation | 4,730 |
| **合计** | **182,186（24.2%）** |

剩下 570,178 行（75.8%）是 master 快照、wordings、覆盖字典。**这个 24% 才是阅读类 UI 应该面对的数据量。**

逐 kind：

| kind | ms | sv | ms_tr |
|---|---:|---:|---:|
| home_line | 29,692 | 29,732 | — |
| mysekai_tweet | 0 | 33,006 | — |
| area_talk | 13,180 | 13,219 | — |
| card_story | 12,176 | 12,244 | — |
| event_story | 7,653 | 7,702 | 4,730 |
| virtual_live | 4,247 | 5,374 | — |
| mysekai_talk | 0 | 7,284 | — |
| unit_story | 600 | 600 | — |
| special_story | 239 | 248 | — |
| self_intro | 130 | 130 | — |

### 1.4 质量标记只对正文 kind 计算

`webindex.py:110` 开头就是 `if kind not in CANONICAL_KINDS: ... content_language_mismatch = False`
并把 `asset_mismatch` 里 `language_mismatch:` 开头的 token 全部清掉。

**推论：** 在 master 快照行上读 `asset_mismatch` / `content_language_mismatch` 是没意义的，
它们被强制清空了。这两个标记只在 24% 的正文行上有值。

### 1.5 trust 的真实含义

实测 `trust` 与 `translation_source` 完全对应：

| trust | 含义 | 行数 |
|---|---|---:|
| B | 官方或原文：`altsource_ms` 374,072、`altsource_sv` 372,727、`official_cn` 覆盖 4,266 | 751,065 |
| C | 非官方译文：`llm` 418 + `jp_pending` 30 + `human` 16（共 464）、`i18n` 107 | 571 |

**trust 区分的是「是不是官方译文」，不是「数据质量高低」。** UI 里不该标成「低质量」，
应该标成「机翻待校对」/「人工待确认」这类具体状态。

### 1.6 source_type 列不可靠

| source | source_type | 行数 |
|---|---|---:|
| altsource_ms | *(空)* | 373,895 |
| altsource_ms | moesekai | 179 |
| altsource_sv | sekai_viewer | 373,453 |
| altsource_ms_translation | *(空)* | 2,996 |
| altsource_ms_translation | moesekai | 1,734 |
| altsource_sv_i18n | *(空)* | 107 |

同一 source 内空值和非空值混排。**别用这列判断站点**，应该从 `settings.json` 的
`backend` 字段取（`moesekai` / `sekai_viewer`），或者维护一张 source → backend 的映射表。

---

## 2. 两个资料站：哪里不同

### 2.1 身份

| | Moesekai mirror | Sekai Viewer |
|---|---|---|
| source id | `altsource_ms` | `altsource_sv` |
| backend | `moesekai` | `sekai_viewer` |
| 主站 | pjsk.moe | sekai-world.github.io |
| 元数据 | metadata.exmeaning.com | sekai-world.github.io/sekai-master-db-{region}-diff |
| 资源 | storage.exmeaning.com | storage.sekai.best |
| 译文通道 | translation.exmeaning.com | i18n-json.sekai.best |
| 地域写法 | `zh-cn` `zh-tw` `ja-jp` `en-us` `ko-kr`（locale） | `cn` `tc` `jp` `en` `kr`（region） |

`settings.json` 里 ms 的 `locale_servers` 就是 locale → server 的映射表
（`zh-cn→cn`、`zh-tw→tw`、`ja-jp→jp`、`en-us→en`、`ko-kr→kr`），
`locale_languages` 是 locale → language 的映射（`zh-cn→zh_hans` 等）。
**这两张表应该直接搬进 WinUI 应用，作为跨站归一化的依据。**

### 2.2 id 结构差异（最容易踩的坑）

```
ms:  web:altsource_ms:zh-cn:actionSets:1
     web:altsource_ms:en-us:event_story:100:1
sv:  web:altsource_sv:cn:areaItems:1
     web:altsource_sv:en:event_story:100:1
```

第 3 段（地域）写法不同。**跨站对齐键的构造步骤：**

1. 去掉 `web:{source}:` 前缀
2. 把剩余第 1 段归一化：ms 的 locale 过 `locale_servers` 转成 region
3. 得到 `{kind}:{asset_id}`，这才是可比的键

`language` 列反而是统一的（两站都用 `ja` / `zh_hans` / `zh_hant` / `en` / `ko`），
这是唯一能直接跨站比较的维度。

### 2.3 kind 覆盖差异

总量几乎相等（ms 374,074 vs sv 373,453，差 0.17%），但**这是巧合**。逐 kind 看：

**仅 ms 有：** `actionSets`、`mysekaiCharacterTalks`、`virtualLives`

**仅 sv 有：** `areaItems`、`birthdayPartyScenarios`、`gameCharacterUnits`、
`gameCharacters`、`liveTalks`、`mysekaiFixtureLabels`、`mysekaiFixtureTags`、
`mysekaiMaterials`、`mysekaiSites`、`mysekai_talk`、`mysekai_tweet`、
`versions`、`virtualLiveGroups`

**数量差异 > 20% 的：**

| kind | ms | sv | 差异 |
|---|---:|---:|---|
| bondsHonorWords | 6,661 | 2,695 | ms 多 2.5 倍 |
| virtual_live | 4,247 | 5,374 | sv 多 27% |

**mysekai 系列是抽取管线不对称的重灾区：**

- ms 有 `mysekaiCharacterTalks`（L1 master JSON，31,625 行）但**没有**抽成 `mysekai_talk`
- sv 有 `mysekai_talk`（L2 可读，7,284 行）但**没有**对应的 L1
- `mysekai_tweet` 只有 sv 有 L2（33,006 行），ms 停在 `mysekaiCharacterTalkTweets`（32,934 行）

也就是说：**想读 mysekai 剧情，只有 sv 能读。** ms 那 6 万多行 mysekai 数据是一坨抓了但没抽的 JSON。
这解释了 sv 的 CANONICAL_KINDS 比 ms 多 41,622 行——其中 40,290 行（97%）就是 mysekai_talk + mysekai_tweet。

### 2.4 语言覆盖差异

剧情类（overlay = 0）逐站逐语言：

| kind | ms | sv |
|---|---|---|
| event_story | ja 1734 / zh_hans 1498 / zh_hant **1474** / ko 1498 / en 1449 | ja 1742 / zh_hans 1498 / zh_hant **1498** / ko 1498 / en 1466 |
| card_story | ja 2744 / zh_hans 2384 / zh_hant **2344** / ko 2384 / en 2320 | ja 2758 / zh_hans 2384 / zh_hant **2384** / ko 2384 / en 2334 |
| unit_story | 各 120 | 各 120 |
| special_story | ja 68 / zh_hans **45** / zh_hant **44** / ko 44 / en 38 | ja 68 / zh_hans **47** / zh_hant **47** / ko 47 / en 39 |
| home_line | ja 6112 / zh_hans 5904 / zh_hant 5904 / ko 5904 / en **5868** | ja 6116 / zh_hans 5904 / zh_hant 5904 / ko 5904 / en **5904** |
| area_talk | ja 2811 / zh_hans 2614 / ko 2598 / en 2559 | ja 2820 / zh_hans 2599 / ko 2599 / en 2585 |
| virtual_live | ja 899 / zh_hans 898 / zh_hant 763 / ko 799 / en 888 | ja 1219 / zh_hans 1090 / zh_hant 1010 / ko 991 / en 1064 |

**sv 的译文覆盖比 ms 略全**（event_story 繁体多 24 话、card_story 繁体多 40 张、home_line 英文多 36 条）。
`untranslated = 1` 的分布也印证这点：ms 只有 2 行，sv 有 726 行（sv 抓的 kind 更多，缺口暴露得也更多）。

注意 virtual_live 的差异不是「缺译」而是**内容量本身就不同**（sv 的虚拟 Live 数据更全）。

### 2.5 覆盖层：两站的「译文」不是一回事

| | altsource_ms_translation | altsource_sv_i18n |
|---|---|---|
| 行数 | 4,730 | 107 |
| 内容 | 整段剧情译文 | 标题/名称字典 |
| kind | `event_story` | `title_overlay`（22 个 namespace × 5 语言） |
| translation_source | official_cn 4,266 / llm 418 / jp_pending 30 / human 16 | i18n 107 |
| trust | B 4,266 + C 464 | C 107 |

两者**不是平行关系**。ms 的是剧情级译文（可以整段替换原文显示），sv 的是词条级字典
（只能覆盖标题）。做「译名」页时它们是两张不同的表，不能合并成一个列表。

### 2.6 id 段与 language 不一致（新发现的陷阱）

`altsource_ms_translation` 的 id 地域段 × language 实测：

| id 地域段 | language | 行数 |
|---|---|---:|
| `ja-jp` | ja | 1,734 |
| `zh-cn` | ja | 1,498 |
| `zh-cn` | zh_hans | 1,498 |

同一批覆盖行里，有一半的 id 写 `zh-cn` 而 `language` 是 `ja`。
**id 地域段 = 抓取时用的 locale，language = 被覆盖行的原语言，两者不是一回事。**
任何「从 id 解析语言」的代码在覆盖层上都会错一半。

（对比：主数据里两站都是严格 1:1，ms 五档各 7~8 万行，sv 五档各 7~9 万行。）

---

## 3. UI 落地

### 3.1 来源作为一等筛选维度

默认来源策略应该是 **「合并，冲突时优先 sv」**（sv 更新、译文覆盖更全、有 mysekai 可读文本）。
筛选器至少提供：`全部 / 仅 Moesekai / 仅 Sekai Viewer / 仅覆盖层`。

### 3.2 来源徽章

每条记录旁边显示三件事：

- **来源**：`MS` / `SV` / `MS·译文` / `SV·i18n`
- **trust**：`B` → 无标记或「官方」；`C` → 按 `translation_source` 显示「机翻待校对」/「待确认」/「社区」
- **抓取时间**：`crawled_at` 的日期

三件事放在一起，用户才知道「这条中文是官方的吗、什么时候抓的、哪个站来的」。

### 3.3 跨站比对视图

对齐键 = `归一化 region + kind + 资产 id`，不能拿 id 原文比。
比对结果分三档：`两站一致` / `仅一站有` / `内容有差异`（hash text 比）。
差异项里最有价值的是「sv 有译文而 ms 没有」——那正是 untranslated 缺口的来源。

### 3.4 数据新鲜度面板

首页需要一张按 source 分组的表：各站行数、`crawled_at` 区间、距今天数。
当前两站差 14 天，这个信息对用户判断「该重新抓了吗」是必要的。

### 3.5 译名页

`title_overlay` 的 22 个 namespace + 覆盖状态，独立成页。
这是整个库里对本地化工作流最有价值的部分，也是现在完全埋在 75 万行里没人看见的部分。

---

## 4. 实现清单

- [ ] `source` → `backend` 映射表写死在应用里（不读 `source_type` 列，它有空值）
- [ ] locale → region 归一化用 `settings.json` 的 `locale_servers`；language 用 `locale_languages`
- [ ] 所有剧情查询默认带 `overlay = 0`；覆盖层只在译名页/对照页出现
- [ ] 覆盖层不解析 id 取语言，只读 `language` 列
- [ ] `trust = C` 的展示文案按 `translation_source` 细分，不要统一叫「低质量」
- [ ] master 快照行上的 `asset_mismatch` / `content_language_mismatch` 恒为空，UI 里对这些 kind 隐藏这两列
- [ ] 来源筛选器 + 来源徽章（来源 / trust / crawled_at）
- [ ] `CANONICAL_KINDS`（10 个）作为「阅读」入口的默认过滤，写进应用常量
- [ ] 跨站比对视图：region 归一化 → 对齐 → 三档差异
- [ ] 首页新鲜度面板：按 source 分组的行数与抓取日期
