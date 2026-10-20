# M2-D（定时 + 指令库 + 时间同步）与 M2-G2（完整编辑页）实施方案

> 状态：**待确认，未改一行代码**。
> 上游依据：`docs/M2-功能对齐方案.md` §5.4 / §5.6 / §6 / §7 / §12；旧版源码逐行对照（**历史依据，非正确性判据**）。
> 本文档只写"要与不要、为什么"，不写代码。
>
> ⚠️ **生效范围（2026-10-07 追加）**：本文档的功能清单来自**旧版**（已废弃的单机桌面程序）的逐项拆解；
> **实现方式以本文档与服务版源码为准**。本项目与旧版**无依赖、无联动**，旧版的实现**不是**正确性判据，
> 其缺陷**不照抄**。术语见 `docs/项目边界与术语.md`。

---

## 0. 本轮范围

| 编号 | 内容 | 依赖 |
|---|---|---|
| M2-D | `TimeSyncService`（SNTP）+ `TimerSchedulerService` + `timer_fires` 表 + 指令库 CRUD | 无 |
| M2-G2 | 图片上传端点 + 细粒度配置端点 + 四个编辑页 + 定时页 + 指令库页 + 设置页扩字段 | M2-D 的定时/指令库端点 |

旧版对照文件（**历史依据，不是正确性判据**）：

```
D:\Project\ControlApp\Services\TimerService.cs              205 行  ← 定时语义
D:\Project\ControlApp\Pages\ButtonEditPage.xaml(.cs)        312/2238 行  ← 按钮编辑页布局
D:\Project\ControlApp\Pages\SubButtonEditPage.xaml(.cs)     215/1079 行
D:\Project\ControlApp\Pages\SubButtonSetEditPage.xaml(.cs)  233/380 行
D:\Project\ControlApp\Pages\CommandLibraryPage.xaml(.cs)    131/298 行
```

---

## 1. ⚠️ 先修一个已查实的缺陷：时间点格式双标准导致定时器**静默失效**

**这是本轮开工前必须堵掉的第一件事，因为它让 M2-D 从第一天起就是错的。**

### 1.1 事实链

`TimerService.cs:94-95`（桌面版，服务版将逐行对齐）：

```csharp
var now = BeijingTime.Now;
var currentTime = now.ToString("HH:mm:ss");      // → "08:00:00"
```

`TimerService.cs:113-114`：

```csharp
var times = button.TimerTimes ?? new();
if (times.Contains(currentTime))                  // 精确字符串匹配
```

→ **判定要求 `TimerTimes` 里的元素必须是 `HH:mm:ss`。**

但产出侧存的是 `HH:mm`：

| 产出点 | 位置 | 值 |
|---|---|---|
| 桌面版模型默认值 | `ButtonConfig.cs:93` | `["08:00"]` |
| 桌面版模型默认值 | `MonthlyPlan.cs:45` | `["08:00"]` |
| 桌面版旧字段默认值 | `ButtonConfig.cs:87` | `"08:00"` |
| **服务版归一化器默认值** | `ControlSettingsNormalizer.cs:39` | `"08:00"` |

### 1.2 为什么桌面版没暴露、服务版一定会暴露

桌面版唯一的**正常**出口是编辑页，而编辑页会归一化：

```
TimeInput.SetTimeString("08:00")  → 拆成 _hour=8,_minute=0,_second=0
TimeInput.GetTimeString()         → $"{_hour:D2}:{_minute:D2}:{_second:D2}" → "08:00:00"
```

所以**只要用户进过编辑页并存过盘，值就变成 `HH:mm:ss`**，桌面版因此长期没暴露。

反例（桌面版同样出错，只是路径罕见）：
- 手工编辑 `control_config.json` 后导入，时间点写成 `"07:45"`；
- 旧格式 `TimerTime = "07:45"` 经迁移进 `TimerTimes`（迁移只做复制，不做格式化）；
- 新按钮开了任务定时但**从未进过编辑页**就直接用了默认值。

**服务版是必然出错**：`ControlSettingsNormalizer` 是新配置的**唯一**默认值生产者，它填 `"08:00"`。
用户在 Web 编辑页勾上「任务定时」而没手动加时间点 → 存在库里的就是 `"08:00"` → **定时器永不触发，且没有任何日志**。

### 1.3 处置

| 项 | 做法 |
|---|---|
| `ControlSettingsNormalizer.DefaultTime` | `"08:00"` → **`"08:00:00"`** |
| 旧格式迁移（`TimerTime→TimerTimes`、`Time→Times`） | 迁移时**顺带格式化**，而不是原样复制 |
| 新增 `NormalizeTimeList` | 把 `H:m` / `HH:mm` / `HH:mm:ss` 统一补成 `HH:mm:ss`；非法值丢弃并写入 `notes` |
| 守门用例 | ① 默认值可被触发；② `"7:45"` → `"07:45:00"`；③ `"07:45"` → `"07:45:00"`；④ `"07:45:00"` 不变；⑤ `"25:00"` / `"abc"` 被丢弃并记 note |

### 1.4 与 §10.7 的关系

§10.7 记录的是桌面版第一处"语义不自洽"（`TimerTime→TimerTimes` 迁移是死代码）。
本条是**第二处**：时间点格式有 `HH:mm` 与 `HH:mm:ss` 两个标准，而判定侧只认后者。

两处的共同点是**桌面版自身不自洽**，因此服务版按"修正"而非"逐字节兼容"处理。
建议把本条追加进 §10.7，作为同一节的第 2 个实例。

> 若你要求逐字节兼容桌面版：把 `DefaultTime` 保持 `"08:00"` 即可 —— 代价是服务版默认情况下定时器完全不工作。**不建议。**

---

## 2. §12 十项决策的拍板情况

| # | 决策点 | 状态 |
|---|---|---|
| 1 | 持久化方案 = C 混合 | ✅ 已在 M2-A 落地（`config_documents` + 乐观锁） |
| 2 | 旧配置导入 = 覆盖 | ✅ 已在 M2-F 落地（导入前不自动备份 → **待补**，见 §6） |
| 3 | 漏触发 = 不补执行 + 告警 | ⏳ **本轮采用推荐值**，见 §3.4 |
| 4 | 触发记录持久化 = 是 | ✅ 本轮做（`timer_fires`） |
| 5 | ANSI 在 Linux = 注册代码页 | ✅ 已在 M2-B 落地（启动自检已输出 `gb2312（CP936）`） |
| 6 | 日志保留 31 天 | ⏳ 属 M2-E；本轮只做 `timer_fires` 的 35 天裁剪 |
| 7 | M2 前端不含拖拽 | ✅ 本轮沿用（点击式排序，拖拽留 M2.5） |
| 8 | 监听端口热重启 | ⚠️ **本轮降级**，见 §4.4 |
| 9 | 里程碑命名 M2-A…M2-G | ✅ 已采用 |
| 10 | 加密 `.dat` 迁移推 M4 | ✅ 已采用 |

---

## 3. M2-D 设计

### 3.1 时间同步（§5.6）—— **已拍板：全应用统一走校正时间**

新增：

- `Core/Services/SntpClient.cs` —— 纯 UDP + 48 字节 SNTP 报文解析，**无平台依赖**，放 Core
- `Core/Services/TimeSyncService.cs` —— 依次尝试服务器列表，成功即停；维护 `Offset` / `LastSyncAt` / `Source` / `LastError`
- `Server/HostedServices/TimeSyncHostedService.cs` —— 启动后**异步**跑首校（超时 2s，不阻塞启动），之后每 30 分钟重校；失败只告警

**决策（用户已拍板）**：按 §5.6 原文执行 —— **定时器与日志时间戳统一走校正后的 `BeijingTime`**。

```csharp
BeijingTime.Now        // 改为：UtcNow + ClockOffset 后转固定 +8。即全应用（日志 / 配置 UpdatedAt / 收发日志 / 认证）统一吃校正
BeijingTime.NowRaw     // 新增：不吃校正（UtcNow + 固定 +8），仅供极少数需要"系统钟"的场景与降级用
BeijingTime.ClockOffset // 默认 TimeSpan.Zero → 未校准时行为与改动前逐字节一致
```

**必须处理的三个落地细节**（否则这条决策会带出新问题）：

| 细节 | 问题 | 处置 |
|---|---|---|
| **Serilog 行内时间戳** | Serilog 的 `{Timestamp}` 来自 `LogEvent.Timestamp`（系统钟），不会自动跟随 `BeijingTime` | ✅ **已实施**：`outputTemplate` 的 `{Timestamp:…}` 换成 `{BeijingTime:…}`，并挂 `BeijingTimeEnricher` 把 `BeijingTime.Now` 写进该属性 |
| **Serilog 滚动文件名** | `RollingInterval.Day` 内部用 `Serilog.Sinks.File` 的 `Clock`（系统钟） | ✅ **已探明：不可注入**。`Serilog.Sinks.File 6.0.0` 的 XML 公开类型清单只有 `FileLifecycleHooks / FileSink / IFileSink / IFlushableFileSink / NullSink / PeriodicFlushToDiskSink / RollingFileSink / SharedFileSink` —— **没有 `Clock`**，它是 internal（DLL 里那个 `SetTestDateTimeNow` 是给上游自己的单测用的，走 `InternalsVisibleTo`）。⇒ **滚动文件名的切分点永远跟随系统钟**，只有文件内容吃校正。已在启动日志里打一条 `【已知局限】` 显式说明 |
| **启动前的时间不连续** | 首校完成前 `ClockOffset = 0`，首校后时间会跳一次 | 首校**异步**进行、不阻塞启动（§13 风险表的既定缓解）。偏移跳变超阈值时 `TimeSyncService` 打 Warning 并写明跳变秒数 |

**降级路径**：SNTP 全部失败 → `ClockOffset` 保持 `Zero` → `BeijingTime.Now == BeijingTime.NowRaw` → 退化为"系统钟 + 固定 +8"，即当前行为。`/api/system/info` 与设置页明确展示"未校准 / 已校准（源 / 偏移 / 上次校准时间）"。

> ⚠️ **诚实项**：这个方案会让日志时间戳**非单调**（校正跳变时）。若 M2-E 的"按日期查历史"要按时间戳排序，需要在排序时容错。这是选择"全应用统一校正"的必然代价，已记录在 §7 风险表。

### 3.2 `TimerSchedulerService` —— 与桌面版逐条对齐

| 语义 | 桌面版 | 服务版 |
|---|---|---|
| 节拍 | `System.Timers.Timer(1000)` | `PeriodicTimer(1s)` |
| 时间源 | `BeijingTime.Now` | `BeijingTime.NowCorrected` |
| 时间点匹配 | `times.Contains(HH:mm:ss)` | 同（格式由 §1 的归一化保证） |
| 任务定时星期 | `TimerMode=="SpecificDays"` 时校验 `TimerDays`；`Daily` 不校验 | 同 |
| 月度计划日期 | `MonthDays` 优先；为空回退 `Months`+`Days` 交叉匹配 | 同 |
| **月度计划执行什么** | **执行 `button.Actions`**（计划只决定"何时"，不携带自己的动作） | **同（关键，易错）** |
| 去重窗口 | 内存 `Dictionary`，2 秒 | **`timer_fires` 唯一约束**（见 3.3） |
| 双触发 | 同按钮同时开任务定时与月度计划、时间点相同 → **各触发一次** | 同（**有意对齐**，见下） |

**关于双触发**：桌面版两个触发键不同（`buttonId:HH:mm:ss` vs `buttonId:planId:HH:mm:ss`），因此同一秒会执行两遍按钮动作。
这是桌面版的既有行为。**建议不改**（改了就是行为偏离），但在 Web 定时页上用文案提示用户"任务定时与月度计划在时间点重合时会各触发一次"。

**配置读取**：每次 tick 调 `ControlSettingsService.GetAsync()` —— 它命中进程内缓存，零 IO、零锁竞争，天然与配置变更一致。
不订阅 `Changed` 事件，避免"改了配置但调度器还没收到通知"的窗口。

**触发链路**：`TimerSchedulerService` → `ActionExecutionService`（复用编码/网络/收发日志/汇总）
→ `TrafficLog` + `LogAdded` + **新增 `TimerFired`** SignalR 事件。

### 3.3 `timer_fires` 表（新迁移 `M2_TimerFires`）

```sql
CREATE TABLE timer_fires (
  ref_id    TEXT NOT NULL,   -- buttonId（任务定时） 或 buttonId:planId（月度计划）
  fire_date TEXT NOT NULL,   -- yyyy-MM-dd（按触发时用的时间源）
  fire_time TEXT NOT NULL,   -- HH:mm:ss
  fired_at  TEXT NOT NULL,   -- ISO 8601，实际触发时刻
  source    TEXT NOT NULL,   -- Daily / SpecificDays / MonthlyPlan / Manual
  PRIMARY KEY (ref_id, fire_date, fire_time)
);
CREATE INDEX ix_timer_fires_date ON timer_fires(fire_date);
```

- Core 侧新增端口 `ITimerFireStore`（`TryClaimAsync(refId, date, time, source)` 返回是否抢到）
- Infrastructure 侧 `EfTimerFireStore` 实现：`INSERT` 撞唯一约束 → `false`
- **写入成功 = 抢到触发权**；失败 = 已触发过，跳过。这一步同时消灭了"同秒多 tick"与"多实例抢触发"
- 裁剪：保留 35 天（略大于日志保留期的 31 天，便于对账）

### 3.4 漏触发（§12 #3 的落法）

**采用推荐值：不补执行 + 告警。**

- 启动后、进入正常 tick 前，扫一次"最近 `MissedScanWindow`（默认 10 分钟，可配）内应当触发、但 `timer_fires` 里没有记录"的触发点
- **只统计与告警，绝不执行**：`"服务停机期间错过 N 个定时触发点（按策略跳过，不补执行）：按钮「开灯」08:00:00 …"`
- 同时发一次 `LogAdded`，Web 端"系统日志"页签可见
- 预留配置项 `Server:Timer:BackfillMissed`（默认 `false`），开启后改为"补执行一次" —— 本轮只留开关与日志提示，不实现补执行逻辑（避免未经使用的复杂分支）

### 3.5 指令库 CRUD

- 端点：`GET /api/commands`、`POST /api/commands`、`PUT /api/commands/{id}`、`DELETE /api/commands/{id}`
- `CommandLibraryService` 现有 `GetAllAsync` / `ResolveAsync` / `SaveAsync`（整份）**保留**；CRUD 走"锁内读改写整份 `commands` 文档"
- `Name` 唯一（桌面版行为）→ 重名返回 `400` 并给出冲突名
- **不参与配置导入导出**（桌面版行为，`ConfigBackupService` 已不动它）
- 执行链已就绪：`ActionExecutionService` 已支持 `DataType = 指令库`（用 `DataContent` 当指令名解析），本轮**不需要动执行链**

### 3.6 新增 / 扩展 API

```
GET  /api/commands                        指令库列表
POST /api/commands                        新增
PUT  /api/commands/{id}                   修改
DELETE /api/commands/{id}                 删除

GET  /api/timers                          当前生效的定时点清单（前端定时页）
POST /api/timers/{refId}/fire             手动触发一次（调试；走同一幂等闸门）

POST /api/system/timesync                 触发一次 SNTP 校准
GET  /api/system/info                     扩展：定时器数 / 时间偏移 / 上次校准时间 / 校准源
```

---

## 4. M2-G2 设计

### 4.1 端点策略 —— 本轮最关键的省力决策

**结论：编辑页整体提交（带乐观锁），列表页快捷操作用细粒度端点（不带乐观锁）。**

| 场景 | 端点 | `expectedVersion` |
|---|---|---|
| 编辑页「保存」 | `PUT /api/config` | **必须带**；冲突 → `409` |
| 列表页 新增 / 复制 / 删除 | `POST /api/buttons`、`POST /api/buttons/{id}/duplicate`、`DELETE /api/buttons/{id}`，子按钮集同构 | `null`（锁内重读，天然合并） |
| 列表页 排序 | `POST /api/items/reorder` | `null` |
| 维护 | `POST /api/maintenance/{clear-buttons,clear-subsets,clear-images,factory-reset}` | `null` |

**判断依据**（`ControlSettingsService.cs:207` / `.Operations.cs:9`）：

`MutateAsync` 已经是「**锁内重读最新 → 改 → 归一化 → 写**」。
因此细粒度端点的语义就是"针对最新配置施加一个局部意图"：

- `expectedVersion = null` → 局部意图，**语义上可合并**，两个人分别删/加不同按钮不会互相冲突 —— 这正是列表页想要的
- `expectedVersion = 带上` → 整份意图，**必须 409** —— 编辑页改的是十几个字段，不能悄悄合并

**为什么不开 §6.1 里全部 14 个细粒度端点**：§6.1 自己已注明"细粒度接口语义上仍是读改写整份配置"。
`PUT /api/buttons/{id}` 相对 `PUT /api/config` 的唯一差别是"前端少传一点"，代价是 14 个 DTO + 14 组端点测试。**收益为负。**

**代价（诚实说明）**：列表页快捷操作是"立即生效、无撤销"，与桌面版 MainPage 右键删除的行为一致。

### 4.2 图片上传 —— 一个必须先补的缺口

现状：`ConfigController` 只有 `GET /api/config/images/{fileName}`，**没有上传端点**。
桌面版是本地 `FilePicker` → 直接写 `images/` 目录；Web 版必须新建。

新增 `POST /api/config/images`（`multipart/form-data`，字段 `file`）：

| 约束 | 做法 |
|---|---|
| 扩展名白名单 | `.png .jpg .jpeg .gif .bmp .webp` |
| 大小上限 | 4 MB（可配） |
| **文件名由服务端生成** | `img{guid:N}{ext}` —— **不接受客户端文件名**，从根上消灭路径穿越，与 `ConfigBackupService` 导出侧命名口径一致 |
| 落盘位置 | `IDataDirectory.ImagesDirectory` |
| 返回 | ~~`{ fileName }`~~ → **`{ fileName, path, bytes }`**：前端必须把 **`path`**（绝对路径）写进 `ButtonImage` / `SubButton.ButtonImage`，理由见 §9.3 |

配套 `DELETE /api/config/images/{fileName}`（复用白名单 + 目录归属双闸）。

**删除按钮时不立即删图**（同一张图可能被多个按钮引用）；回收统一走 maintenance 端点，
但见下面的修正 —— 该端点**尚未存在**。

> ⚠️ **实施时的修正（详见 §9.3）**：本节原写"回收统一走**已有的**
> `POST /api/maintenance/clear-images`"。实做时核查发现该端点**并不存在**
> —— 只有 Core 里的 `ClearCustomImagesAsync`，尚未接端点，它属 G2 的 maintenance 组。
> 更值得注意的：`ClearCustomImagesAsync` 只把配置里的图片字段清空，**不删磁盘文件**，
> 因此它连"回收"这个语义都还没实现。

### 4.3 前端结构

```
web/src/views/
  ConsoleView.vue        控制台（已有）
  TrafficView.vue        收发日志（已有）
  OpsLogView.vue         系统日志（已有）
  SettingsView.vue       设置（已有 → 扩字段）
  TimerView.vue          定时（新：生效定时点清单 + 手动触发 + 校准状态）
  CommandsView.vue       指令库（新：CRUD）

web/src/editor/
  EditorHost.vue         全屏编辑弹层（等价桌面版"导航到独立页"+ 返回）
  ButtonEditor.vue       概况 + 基本设置 + 动作列表 + 定时折叠区
  SubButtonSetEditor.vue 集合设置（含统一 IP/端口）+ 子按钮列表
  SubButtonEditor.vue    基本 + 动作（无定时 / 无月度 / 无 WoL）
  ActionCard.vue         动作卡片：名称 / 协议+IP+端口+延迟+对接码 / 数据类型+数据内容
  PairingCodeField.vue   类型下拉（String|UTF-8|十六进制）+ 输入框
  DataField.vue          数据类型下拉（含「指令库」→ 指令选择区）；协议=WoL 时隐藏
  ColorPicker.vue        6 色快捷 + 自定义 hex
  ImagePicker.vue        上传 + 预览 + 清除
  TimerPlanEditor.vue    任务定时 + 月度计划
  TilePreview.vue        实时瓦片预览
```

关键交互对齐（§7.2）：

- 「对接码类型」下拉**显示名 ↔ 存储值映射**：`String` / `UTF-8` / `十六进制`（存 `Hex`）——与桌面版一致
- 「数据类型」下拉：`String / Hex / ANSI / ASCII / UTF-8 / 指令库`；选「指令库」出现指令选择区
- **协议 = WoL 时隐藏数据类型列**（桌面版行为）
- 字段顺序与桌面版一致：名称 → 协议+IP+端口+延迟+对接码 → 数据类型+数据内容
- 动作卡片：「⤵ 向下复制」把该动作复制一份到正下方
- 页签不引 vue-router，继续 `activeTab` + `v-if`；编辑页走 `EditorHost` 全屏弹层（零路由改造风险）

**三个必须做的体验细节**：

1. **脏检查**：编辑页有未保存改动时关闭 → 二次确认
2. **409 对话框**：展示"服务端版本 vN / 你的基线 vM"，给三个动作 → **重新载入（丢弃我的改动）/ 强制保存（以我的为准，取服务端最新 version 重发）/ 取消**
3. **实时校验**：端口 `1..65535`、MAC 格式、时间 `HH:mm:ss`；Hex 合法性**实时调用已有的 `POST /api/actions/preview`**（M2-B 已就绪），非法就地在字段下标红

### 4.4 设置页扩字段（§7.3 对账）+ 一处降级

| 桌面设置项 | 本轮 |
|---|---|
| 程序名称 / 用户密码 / 指令库入口 / 背景图片 / 默认按钮图片 | ✅ |
| 自由排布开关 | ✅（数值输入，非拖拽） |
| 清除按钮 / 子按钮集 / 图片 / 日志 | ✅ |
| 配置备份 / 恢复 / 恢复出厂 | ✅ |
| 机器码 | ✅ |
| 开机自启动 | ⚠️ 改为**只读展示服务自启状态** + 部署脚本说明（§7.3 原定） |
| 窗口大小 / 锁定窗口 | ❌ 桌面专有 |
| 授权 | ❌ M4 |

**监听端口（§12 #8）—— 已拍板：本轮做热重启**：

设置页改端口后**不重启服务即可生效**。实现路径：

| 步骤 | 做法 |
|---|---|
| 1. 落盘 | 新端口写进配置（`Server:Http:Urls`），走同一套乐观锁 |
| 2. 重建监听 | 通过 `IServerAddressesFeature.Addresses` 改监听地址；Kestrel 不重启进程，但要先 `await server.StopAsync()` 再 `StartAsync()`（`IServer` 实例可复用） |
| 3. 配置同步 | 让 `Server:Http:Urls` 的来源变成**可写的配置文档**（而非只读的 `appsettings.json`），否则下次重启会退回旧端口 —— **这是本项最容易被漏掉的一环** |
| 4. 客户端迁移 | 端口变了，浏览器当前页面必然失联 → 服务端在切换前通过 SignalR 推一条 `ServerAddressChanged(newPort)`，前端倒计时后自动 `location.href = ${protocol}//${hostname}:${newPort}` |
| 5. 兜底 | 新端口绑定失败（被占用 / 权限）→ **回滚到旧端口**并把失败原因返回前端；绝不留下"服务已停但新端口没起来"的状态 |
| 6. 与 §9 安全白名单联动 | `Server:Security:AllowedCidrs` 的校验在端口切换期间不能出现"两个端口都拒绝"的窗口 |

⚠️ **风险提示**：第 4 步的 SignalR 重连在浏览器端有兼容性差异（部分浏览器对跨端口 WebSocket 的失败重试策略不同），需要**真机验证**，不能只靠单测。第 5 步的回滚必须有独立测试用例，否则一个端口占用就能把服务打挂 —— 这比"提示重启"危险得多。

> 因此本项的验收标准比其余各项更严：**必须包含"新端口被占用时服务仍存活在旧端口"的实测**。

---

## 5. 工作分解与验收

| 步 | 内容 | 可独立验证 |
|---|---|---|
| D1 | §1 时间格式缺陷修复 + 归一化器 `NormalizeTimeList` | 单测 5 例 |
| D2 | `SntpClient` + `TimeSyncService` + `BeijingTime` 校正化 + Serilog `{BeijingTime}` 模板 | **先探明 `Serilog.Sinks.File` 滚动钩子是否可注入**；注入假 SNTP 响应；offset 生效；全失败降级 |
| E1 | `TimerFireRecord` + `M2_TimerFires` 迁移 + `ITimerFireStore` | 唯一约束冲突返回 false |
| E2 | `TimerSchedulerService` | **注入可控时钟**，覆盖：Daily / SpecificDays / MonthDays / 旧 Months+Days / 双触发 / 幂等 / 漏触发报告 |
| E3 | 指令库 CRUD 端点 | 集成测试；重名 400 |
| E4 | `/api/timers`、`/api/system/timesync`、`/api/system/info` 扩展 | 集成测试 |
| E5 | 监听端口热重启 | **实测：新端口被占用时服务仍存活在旧端口**；SignalR `ServerAddressChanged`；重启后端口持久 |
| G1 | 图片上传 / 删除端点 | **✅ 已完成**（见 §9）| 集成测试：白名单、大小、服务端命名 |
| G2 | 细粒度端点（buttons / subsets / reorder / maintenance） | 集成测试：`null` 版本无冲突 |
| G3 | 前端构件：`ActionCard` / `PairingCodeField` / `DataField` / `ColorPicker` / `ImagePicker` | 构建 + 手工 |
| G4 | `ButtonEditor` | 打通一条竖线（改名称 → 保存 → 409 → 刷新） |
| G5 | `SubButtonSetEditor` + `SubButtonEditor` | 同上 |
| G6 | `TimerPlanEditor` + `TimerView` + `CommandsView` | 同上 |
| G7 | `SettingsView` 扩字段 + `EditorHost` + 409 对话框 + 脏检查 + 端口热切换 UI | 真机验证自动跳转 |
| G8 | 冒烟脚本扩展 + 打包单文件 exe 复跑 | 冒烟全绿 |

**验收口径**：

- `dotnet build` 0 警告 0 错误
- `dotnet test` 全绿（当前 **251**，预计 **330+**）
- `npm run build` 通过
- 真服务冒烟：定时器部分**用注入时钟**跑（不等真实时间）；另做一次"设成当前时间 +20 秒"的端到端实测，确认真的发出去
- 打包单文件 exe 复跑一遍冒烟（M2-F/G 的做法沿用）

---

## 6. 顺手要补的两件旧账

| 项 | 说明 |
|---|---|
| §12 #2「导入前自动备份旧配置为 `.bak`」 | M2-F 实现了"覆盖"，但**没做导入前的自动备份**。本轮在 `ConfigController.Import` 里补：覆盖前把当前配置写 `config.bak-{yyyyMMddHHmmss}.json` 到数据目录 |
| `Program.cs` LoggerConfiguration | 补 `.MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)`。现在 EF 的 SQL 日志把 `server-*.log` 淹了（启动自检那几行要翻很久才找到） |

---

## 7. 风险登记

| 风险 | 说明 | 缓解 |
|---|---|---|
| **日志时间戳非单调** | 已选"全应用统一校正"，校正跳变时日志时间会跳（严重时钟漂移场景） | 首校在启动早期完成；跳变超阈值记 Warning 并写明跳变秒数；M2-E 按日期查历史时排序需容错 |
| **Serilog 滚动钩子不可注入** | 文件名切分点可能只能用系统钟 | D2 第一步探明；不可注入则启动日志明示，不伪造能力 |
| 校正时钟抖动 | NTP 校正值 ±50ms，理论上跨秒边界可能漏触发 | 1s 轮询 + 秒级精确匹配；D2 增加"时钟回拨/前跳"用例 |
| **端口热重启把服务打挂** | 新端口绑定失败时可能留下"旧端口已停、新端口没起" | 强制回滚到旧端口；E5 必须有"端口被占用仍存活"的独立用例 |
| SignalR 跨端口重连 | 各浏览器对跨端口 WebSocket 失败重试策略不同 | G7 真机验证；推 `ServerAddressChanged` 让前端主动跳转，不依赖自动重连 |
| 双触发 | 任务定时与月度计划时间点重合会各发一次（桌面版行为） | **有意对齐**；UI 文案提示；不改行为 |
| 编辑页体量 | 4 个编辑器约 2000+ 行新前端代码 | 拆组件；G4 先打通一条完整竖线再铺开 G5/G6 |
| EF 迁移 | 新增 `timer_fires` 表需迁移 | 与 `20261007050637_M2_ConfigDocuments` 同法生成；`DatabaseInitializer` 已就绪 |

---

## 8. 决策记录（已拍板）

| # | 事项 | 决定 |
|---|---|---|
| 1 | §12 #3 漏触发 | **不补执行** + 启动时告警报告（采用推荐值；预留 `BackfillMissed` 开关但不实现补执行分支） |
| 2 | §12 #8 监听端口 | **本轮做热重启**（含失败回滚、SignalR 地址迁移、可写配置源；验收含"端口被占用时服务存活"实测） |
| 3 | NTP 校正范围 | **按 §5.6 原文：日志与定时器统一走校正时间**（`BeijingTime.Now` 全应用吃校正，新增 `NowRaw` 降级用；Serilog 模板改用 `{BeijingTime}`） |
| 4 | 编辑页端点策略 | 编辑页 `PUT /api/config` 带乐观锁；列表快捷操作走细粒度端点且 `expectedVersion = null`（详见 §4.1） |
| 5 | 代码改动 | D 组（D1/E1–E5）与 **G1 已完成**，见 §9；G2 起尚未开始 |

---

## 9. M2-G1 实施回填（**已落地**）

> 本轮只做 G1（图片上传 / 删除端点），不含 G2 的细粒度端点。

### 9.1 交付清单

**内核（`ControlServer.Core`）**

| 文件 | 内容 |
|---|---|
| `Services/ImageStorageService.cs`（新） | 上传落盘 / 删除 / **文件名安全解析**（`TryResolveExisting`）；扩展名白名单、**魔数嗅探**、大小上限、`img{GUID:N}{ext}` 命名、原子写、日志注入防护 |
| `Services/ImageReferenceScanner.cs`（新） | 纯函数：找出配置里所有引用某张图的位置（给人看的描述），供删除前判定 |

**宿主（`ControlServer.Server`）**

| 文件 | 内容 |
|---|---|
| `Api/ConfigController.cs`（改） | 新增 `POST /api/config/images`、`DELETE /api/config/images/{fileName}`；`GET` 图片端点改用共享的 `TryResolveExisting`（**删掉了重复的第二道闸实现**） |
| `Options/ServerOptions.cs`（改） | 新增 `ImagesOptions`（`Server:Images:MaxUploadBytes`，默认 4 MB） |
| `Program.cs`（改） | 注册 `ImageStoreOptions` / `ImageStorageService`；把上限喂给 `FormOptions.MultipartBodyLengthLimit`（+1 MB 余量）；`ApiBehaviorOptions.InvalidModelStateResponseFactory` 把"表单超限"翻成 413 + 人话 |

**契约与前端**

| 文件 | 内容 |
|---|---|
| `Contracts/ConfigDtos.cs`（改） | `ConfigImageUploadResultDto`、`ConfigImageInUseDto` |
| `web/src/types.ts` / `api.ts`（改） | 对应 TS 类型 + `uploadImage(file)` / `deleteImage(fileName, force)` |

### 9.2 测试

| 层 | 规模 |
|---|---|
| `ControlServer.Core.Tests/ImageStorageServiceTests.cs`（新） | 42 条：扩展名白名单、六种格式的魔数、大小上限（含"声明说谎"）、命名与绝对路径契约、路径解析的两道闸、引用扫描口径 |
| `ControlServer.Server.IntegrationTests/ConfigImageApiTests.cs`（新） | 21 条：401、端到端上传→读回、客户端文件名不进路径、三道校验、413 的两条路径、删除的 204/404/穿越 404、引用保护 409 + force、**导出契约**（含一条反向守门：只存裸文件名时导出必为 0 张） |
| **合计** | **423 → 487**（Core 344 + 集成 143），0 警告 0 错误，全绿 |
| 真机冒烟 | 29 项全通过（真 Kestrel + 真 PNG + Cookie 会话；含 6 MB 超大文件的真实行为） |

### 9.3 与设计稿的四条**有意偏离**（其中 3 条是查证后的必要修正）

1. **上传接口返回 `{ fileName, path, bytes }`，多了一个 `path`。**
   设计稿只写了 `{ fileName }`，但那样**会静默丢图**：配置里图片字段的契约是
   **服务端绝对路径**，而导出侧（`ConfigBackupService.BuildBackup`）是按
   `File.Exists(字段值)` 收集要内嵌的图片 —— 若前端存的是裸文件名，
   `File.Exists("imgX.png")` 会相对**进程工作目录**求值而返回 false，那张图不会被导出，
   表现为"导出再导入后按钮图没了"，且全程无任何报错。
   集成测试里专门放了一条**反向守门**用例把这个契约钉死。

2. **扩展名白名单之外，加了魔数嗅探。**
   扩展名是**客户端自称**的，只校验它等于允许把任意字节（HTML / 压缩包 / 可执行文件）
   存进图片目录、并以 `image/png` 的 Content-Type 服务出去。魔数是被上传内容**自身**的性质，
   伪造它需要让文件同时是一个合法图片。六种格式（PNG / JPEG / GIF87a / GIF89a / BMP / WEBP）
   逐一覆盖，含"RIFF 开头但不是 WEBP（如 wav）"这类容易误判的边界。

3. **`DELETE` 默认拒绝删除"仍被引用"的图（409 + 引用方清单，`?force=true` 放行）。**
   设计稿未提。不加这条的话，删一张被多处引用的图会让那些按钮**静默**退化成纯色瓦片
   （前端对图片 404 的处理本就是回退，不报错），用户看到的是"我删了一张没用的图，
   怎么别的按钮的图也没了"。匹配口径按**文件名最后一段**、忽略大小写 ——
   必须与前端 `imageUrl()` 及 `GET` 端点**完全一致**，否则"能显示的图"与"被认为被引用的图"会错位。

4. **`FormOptions.MultipartBodyLengthLimit` 收窄到"上限 + 1 MB"（默认 128 MB）。**
   默认值对 4 MB 的图片上传过于宽松：一个恶意请求能逼服务端先缓冲 128 MB 才轮到端点拒绝。

### 9.4 一处**必须知道**的框架行为：超限失败发生在端点代码之前

MVC 在**调用 action 之前**就会为请求构建值提供器并读取表单，因此
"请求体超过 `MultipartBodyLengthLimit`" 这次失败**轮不到端点里的 try/catch**。
实测拿到的是框架默认的 `400` + ProblemDetails，文案是英文的
`Multipart body length limit 5242880 exceeded` —— 既暴露内部数字、又不对用户说人话，
而且 `413` 才是"负载过大"的正确状态码。

处置：在组合根用 `ApiBehaviorOptions.InvalidModelStateResponseFactory` **只针对这一种失败**
改写响应（413 + 中文提示），其余模型状态错误原样交回框架默认实现。
识别只能靠框架给的英文文案（该错误没有错误码，也拿不到强类型异常），
因此**集成测试 `Upload_rejects_a_body_larger_than_the_multipart_buffer_limit` 是这条依赖的哨兵** ——
将来 .NET 若改措辞，表现会**回退**成框架默认的 400，而不是变成错误行为。

### 9.5 查证时发现的三处**方案与实现不符**（需拍板）

| # | 问题 | 影响 |
|---|---|---|
| A | 设计稿称 `POST /api/maintenance/clear-images` 是"已有的"端点 —— 实际**不存在**（只有 Core 的 `ClearCustomImagesAsync`，未接端点） | 不影响 G1；G2 做 maintenance 组时补上即可 |
| B | `ClearCustomImagesAsync` **只清配置里的图片字段，不删磁盘文件** | 「清除图片」并不能回收磁盘空间，孤立文件会永久累积。要么补"删除未被引用的文件"，要么明确文案改成"仅解除引用"。**需拍板** |
| C | 设计稿的 §4.4 步骤 2 写"通过 `IServerAddressesFeature.Addresses` 改监听地址" | **该做法在 E5 已被证伪**（见 `MEMORY.md` §16.1 / `REF-M2-模块细节.md` §16）；实际实现是自定义 `IConnectionListenerFactory`。§4.4 属 G7 的范围，做 G7 时以 E5 的实际实现为准 |

（完 · **M2-G1 已实施**）

