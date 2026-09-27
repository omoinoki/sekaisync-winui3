# 数据分类方案：按游戏资产逻辑重切 entities / web_pages / meta

本文基于真实库实测（`sekaisync-handoff-2026-08-14/store/kb/sekaisync.db`，schema_version 1）。所有行数为实测值。

## 0. 三张表的性质完全不同

先把一个前提说清楚：**这三张表不是同一层的东西，不该在 UI 里平级陈列。**

| 表 | 行数 | 本质 | 面向 |
| --- | --- | --- | --- |
| `entities` | 66,435 | 去重后的资产主数据，一条资产一条记录，五语名合并在 `names_json` 里 | 玩家查资料 |
| `web_pages` | 752,364 | 混合体：master 原始快照 + 抽取后的正文 + 译名/译文覆盖层 | 分三类对待，见第 2 节 |
| `meta` | 5 | 管线状态标记，不是资产 | 首页健康检查 |

`entities` 和 `web_pages` 存在明确的上下游关系，这一点后面会反复用到：

- `web_pages` 里的 camelCase kind（如 `honors`、`cards`、`stamps`）是**按「资产 × 语言 × 来源」展开的原始快照**。实测每个这类 kind 都刚好覆盖 10 个 `source × language` 组合（2 来源 × 5 语言）。
- `entities` 里对应的 type（如 `honor`、`card`、`stamp`）是**去重后的规范记录**，把五语名压进一个 `names_json`。

举例：`honors` 73,193 行 ÷ 10 组合 ≈ 每语言 7,300 条；`entities.type='honor'` 只有 9,132 条（其中 jp 8,556）。同一个称号，在 web_pages 里是 10 行 JSON，在 entities 里是 1 行带五语的记录。

**UI 的默认路径应该走 entities，只在需要结构化字段（发布时间、稀有度、数值）时才回查 web_pages 的快照。**

---

## 1. entities：30 个 type 归到 12 个资产域

`type` 字段本身就是 master 表名，已经是分类了。问题是有 30 个，且其中一部分对玩家没有意义。按玩家心智归并：

| 资产域 | 包含 type | 行数 | 玩家会查吗 |
| --- | --- | ---: | --- |
| 角色 | `character` `character_profile` `character_rank` `character_unit` | 4,658 | 是（前两个） |
| 乐团 | `unit` | 6 | 是 |
| 卡牌 | `card` `card_episode` | 4,173 | 是 |
| 音乐 | `song` `music_vocal` `music_difficulty` | 6,555 | 是（`song`） |
| 活动 | `event` `event_item` `event_mission` `event_story` | 4,686 | 是（`event`） |
| 剧情 | `unit_story` `special_story` | 76 | 是 |
| 称号 | `honor` `honor_group` `bonds_honor` `honor_mission` | 12,001 | 是 |
| 任务 | `live_mission` `character_mission` `normal_mission` `story_mission` | 29,613 | **基本不会** |
| 抽卡 | `gacha` | 1,184 | 是 |
| 演唱会 | `virtual_live` | 488 | 是 |
| 表情 | `stamp` | 1,365 | 是 |
| 我的世界 | `mysekai_fixture` `area` `area_item` | 1,630 | 是 |

合计 66,435，与总数吻合。

### 两个必须处理的现实

**一、任务类占了 44%，但几乎没人查。** `live_mission` 17,987 + `character_mission` 11,362 + `normal_mission` 64 + `story_mission` 200 = 29,613 行，名称长这样：

```
Live mission 269 (period 3, req 25)
```

这是程序化生成的占位名，没有译名价值。UI 必须默认折叠这一域，否则任何「全部实体」视图都会被它淹没。

去掉任务类和纯数值类（`music_difficulty`、`music_vocal`、`character_rank`）之后，**真正有译名、玩家会去查的实体约 26,481 行，只占 40%**。

**二、有些 type 天然兼属两个域。** `card_episode` 既是卡牌的一部分也是剧情；`event_story` 既是活动的一部分也是剧情。上表给的是主域（用于计数不重复），UI 里应该允许兼属标签，`event_story` 同时出现在「活动」和「剧情」下。

### names_json 的 key 不统一

这是渲染层最容易踩的坑。实测各种 type 的取名 key：

| key | 出现在 |
| --- | --- |
| `name` | `area` `area_item` `bonds_honor` `honor` `honor_group` `mysekai_fixture` `stamp` `event_item` `virtual_live` `gacha` `card_prefix` … |
| `title` | `song` `card_episode` `special_story` |
| `full` | `character` `character_rank` |
| `unitName` / `unitProfileName` | `unit` |
| 只有 `ja` / `en` / … 没有主名 | `character_mission` `event_mission` `live_mission` `honor_mission` |
| 空 dict | `card` `character_profile` `character_unit` `music_difficulty` `music_vocal` `unit_story` |

`character` 还有 `firstName` / `givenName` / `firstNameEnglish` / `givenNameEnglish` 这类拆分字段，`event_story` 的 key 是 `episode1_ja` … `episode8_ja`（按话存标题，不是单一名字）。

**取显示名的回退链**：`name` → `title` → `full` → `unitName` → `zh_hans` → `ja` → 第一个非空值。取不到就退回用 `id`。

### facts_json 的多语字段有规律

`facts_json` 里的多语字段一律是 `{key}_{lang}` 模式，lang ∈ `ja` `en` `zh_hans` `zh_hant` `ko`：

- `outline_*` — `event`、`event_story`
- `sentence_*` — `character_mission`、`event_mission`、`honor_mission`、`normal_mission`
- `condition_*` — `honor`、`bonds_honor`
- `description_*` — （web_pages 的 `stamp`）
- `flavor_text_*` — `event_item`、`mysekai_fixture`
- `profileSentence_*` — `unit`

单语字段则是业务数据：`startAt` `endAt` `publishedAt` `releaseAt`（毫秒时间戳）、`unit`、`height`、`birthday`、`school`、`lyricist`、`composer`、`arranger`、`character_id`、`rank`、`power1BonusRate`、`honor_rarity`、`levels`、`requirement`。

UI 可以按 `{key}_{lang}` 规则自动把 facts 渲染成五语对照 + 数值表，不用为每个 type 写死。

---

## 2. web_pages：50 个 kind 分三层，外加一个正交标记

### 命名约定就是分层信号

这是最关键的一条发现。**kind 的命名风格直接对应数据形态：**

- **camelCase（39 种，570,071 行）** = master 原始快照，`text` 列是完整 JSON 对象
- **snake_case（11 种，182,293 行）** = 抽取后的内容，`text` 列是人类可读文本

只有三个例外需要单独记：`systemLive2ds` 是 camelCase 但只有 40% 是 JSON（同一个 kind 里混存了 master 记录和抽取后的台词）；`virtual_live` 是 snake_case 但 5% 是 JSON；`title_overlay` 是 snake_case 但内容是 `id\t译名` 的映射表，不是正文。

#### L1 master 快照（camelCase，JSON）

数量最大的几类：

| kind | 行数 | 平均长度 | 内容 |
| --- | ---: | ---: | --- |
| `characterArchiveVoices` | 119,617 | 256 | 角色语音，含 `displayPhrase` `assetName` `characterArchiveVoiceType` |
| `honors` | 73,193 | 328 | 称号，含 `honorRarity` `levels[]` `description` |
| `mysekaiCharacterTalkTweets` | 65,940 | 163 | MySEKAI 推文 |
| `systemLive2ds` | 61,125 | 172 | Live2D 台词，含 `serif` |
| `wordings` | 42,684 | 80 | UI 文案，含 `wordingKey` `value` |
| `musicDifficulties` | 32,906 | 107 | 谱面难度 |
| `mysekaiCharacterTalks` | 31,625 | 344 | MySEKAI 对话 |
| `cheerfulCarnivalPartyNames` | 27,441 | 164 | 团队名 |
| `cardEpisodes` | 24,456 | 461 | 卡牌剧情结构 |
| `cards` | 12,804 | 10,747 | 卡牌全量数据 |
| `gachas` | 4,347 | 39,705 | 卡池，单条最大 192KB |
| `events` | 1,877 | 8,634 | 活动全量数据 |

这一层的价值是**结构化字段**（时间、稀有度、数值、关联 id），不是给人通读的。它应该被 entities 视图按需回查，不应该作为独立浏览入口。

#### L2 抽取内容（snake_case，可读文本）

| kind | 行数 | 平均长度 | 说明 |
| --- | ---: | ---: | --- |
| `home_line` | 59,424 | 14 | 主页单句语音，五语 |
| `mysekai_tweet` | 33,006 | 18 | MySEKAI 推文，五语 |
| `area_talk` | 26,399 | 322 | 区域对话，`角色名：台词` 结构 |
| `card_story` | 24,420 | 1,970 | 卡牌剧情 |
| `event_story` | 20,085 | 2,796 | 活动剧情 |
| `unit_story` | 1,200 | 2,197 | 乐团剧情 |
| `special_story` | 487 | 775 | 特别剧情 |
| `self_intro` | 260 | 836 | 自我介绍 |
| `mysekai_talk` | 7,284 | 83 | **仅日语，无译文** |
| `virtual_live` | 9,621 | 560 | 演唱会台词 |

真正算「长篇叙事」的只有 `card_story` + `event_story` + `unit_story` + `special_story` = **46,192 行，占全表 6.1%**。剩下的是短台词和语音碎片。

#### L1 ↔ L2 的配对关系

L2 大部分是从 L1 抽出来的，成对出现：

| master（L1） | 抽取（L2） | 备注 |
| --- | --- | --- |
| `cardEpisodes` 24,456 | `card_story` 24,420 | 数量接近 1:1 |
| `eventStories` 1,871 | `event_story` 20,085 | L2 按话拆开，所以多很多 |
| `unitStories` 60 | `unit_story` 1,200 | 同上 |
| `specialStories` 495 | `special_story` 487 | 1:1 |
| `characterArchiveVoices` 119,617 | `home_line` 59,424 | 一条语音一行，五语 |
| `mysekaiCharacterTalkTweets` 65,940 | `mysekai_tweet` 33,006 | |
| `mysekaiCharacterTalks` 31,625 | `mysekai_talk` 7,284 | L2 仅日语 |
| `systemLive2ds` 61,125 | — | 混存在同一 kind 内 |
| `actionSets` 13,329 | `area_talk` 26,399 | 是否对应待确认，数量不吻合 |
| — | `self_intro` 260 | 未见对应 L1 |

这条配对关系的实用价值：L2 提供正文，L1 提供上下文（角色 id、时间、来源 asset）。做一个剧情阅读页时，正文取 L2，元信息回查 L1。

#### L3 覆盖层

**`title_overlay`**（107 行）= 官方译名映射表，22 个 namespace：

`area_name` `area_subname` `card_episode_title` `card_gacha_phrase` `card_prefix` `card_skill_name` `character_name` `character_profile` `comic_title` `event_name` `event_story_episode_title` `gacha_name` `honorGroup_name` `honor_name` `music_titles` `music_vocal` `stamp_name` `unit` `unit_profile` `unit_story_chapter_title` `unit_story_episode_title` `virtualLive_name`

内容是 `id\t译名` 逐行排列，例如 `area_name`：

```
1	全向十字路口
2	中心大街
3	购物广场
```

这是整个库里对本地化最有价值的一块资产，107 行撑起 22 类官方译名，现在的 UI 完全没暴露它。

### 正交标记：overlay / translation_source

`overlay = 1` 的行共 4,837 条（`auxiliary` 与 `aux_flag` 同值），它们是**译文覆盖行**，不是独立资产：

| translation_source | 行数 | 挂在哪个 kind |
| --- | ---: | --- |
| `official_cn` | 4,266 | `event_story`（日 2,844 + 简 1,422） |
| `llm` | 418 | `event_story`（日 342 + 简 76） |
| `i18n` | 107 | `title_overlay`（五语各 ~21） |
| `jp_pending` | 30 | `event_story` |
| `human` | 16 | `event_story` |

覆盖行的 id 在末尾追加了目标语言后缀，例如同一话 `event_story:100:1` 在库里共 13 行：

```
web:altsource_ms:ja-jp:event_story:100:1                          日语原文
web:altsource_ms:zh-cn:event_story:100:1                          简体（来自上游）
...
web:altsource_ms_translation:ja-jp:event_story:100:1:ja           覆盖：用 official_cn 覆盖日语行
web:altsource_ms_translation:zh-cn:event_story:100:1:zh_hans      覆盖：用 official_cn 覆盖简行
```

**两个陷阱必须记住：**

1. **覆盖行的 `language` 列语义变了。** 它指的是「被覆盖的那一行原本是什么语言」，不是内容语言。`web:altsource_ms_translation:ja-jp:...:ja` 的 `language` 是 `ja`，但 `text` 里是中文。任何按 `language` 做筛选或并排展示的代码都会在这里出错。

2. **覆盖行的正文格式不同。** 原文是 `真冬：（——现代语文的作业全部做完了……）`，覆盖行是：

```
（——现代语文的作业全部做完了，接下来就剩数学了……）
真冬
（这里只要套公式就行了……）
```

说话人单独成行、跟在台词**之后**，且没有冒号和全角括号。解析对白需要两套规则，按 `overlay` 判断走哪套。

另外 `derived` / `derived_flag` 全表为 0，`instance` 全表为空，这两列当前没在用。质量标记的实际命中量：`untranslated=1` 728 行，`content_language_mismatch=1` 8 行，`asset_mismatch` 非空 8 行，`scenario_id_mismatch` 非空 424 行。`trust` 只有 B（751,793）和 C（571）两档。

---

## 3. meta：只有 5 个 key，是管线状态不是资产

```
schema_version     = 1
imported_registry  = 1
imported_glossary  = 1
imported_terms     = 1
imported_pages     = 1
```

把它做成可浏览的表格没有意义。它应该变成首页顶部的一条健康状态：四个导入标记是否齐全 + 各表行数 + 最近 `crawled_at` + 覆盖层统计（official_cn 4,266 条）。

如果后续要记录更多状态，建议扩成 `meta` 之外的独立表，或者约定 key 命名前缀（如 `stat_rows_web_pages`、`stat_last_crawl`），避免把统计值和开关混在一起。

---

## 4. 落到 UI：导航怎么切

按上面的分类，五页导航改成：

| 页 | 数据源 | 默认视图 |
| --- | --- | --- |
| 概览 | `meta` + 各表 count + `crawled_at` | 导入状态、规模、覆盖度 |
| 剧情 | `web_pages` L2 的 4 种 story kind（46k 行） | 按 kind / 语言 / 来源筛选，详情渲染对白 |
| 角色 | `entities` 角色 + 乐团域 | 按 unit 分组，展示 profile、生日、身高 |
| 卡牌 | `entities.card` + `card_episode`，回查 `web_pages.cards` | 按角色/稀有度分组 |
| 资产 | `entities` 其余域（音乐 / 活动 / 称号 / 表情 / 抽卡 / 演唱会 / 我的世界），任务域默认折叠 | 域切换 + 五语名检索 |
| 用语 | `glossary_terms` + `terms` + `term_evidence` | 五语对照 + 句级证据 |
| 译名 | `web_pages.title_overlay` + overlay 行 | 22 个 namespace 的译名表 + 译文覆盖状态 |
| 数据库 | 全部（保留） | 调试视图，从主导航移到底部 |

「译名」这一页是新增的，也是这个库最有差异化价值的部分 —— 它直接服务于本地化工作流，而现在完全埋在 75 万行里。

## 5. 实现要点清单

- 显示名回退链：`name → title → full → unitName → zh_hans → ja → id`
- facts 多语字段按 `{key}_{lang}` 自动分组渲染
- `language` 在 `overlay=1` 行上语义不同，所有筛选和对照逻辑要先判 `overlay`
- 对白解析两套规则，按 `overlay` 分支（覆盖行是「台词在上、说话人单独一行」）
- 五语对齐键不是 `canonical_key`（它把语言编进去了，形如 `event_story:zh_hans:100:1`），要用 `id` 去掉 `web:{source}:{locale}:` 前缀后的部分，如 `event_story:100:1`
- 同一话同语言有两行（`altsource_ms` 与 `altsource_sv`），对照视图要先选一侧的 source
- 优先走 `kind` + `language` 等值筛选（有 `idx_pages_kind` / `idx_pages_lang` 索引，取页 0.18s），避免 `text LIKE '%x%'`（每页约 2s）
- `entities` 的任务类（29,613 行）默认折叠
- `mysekai_talk` 只有日语，做对照时要显式标注缺译，不要渲染成空列
