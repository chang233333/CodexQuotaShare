# CodexQuotaShare 开发任务书

## 1. 项目名称

项目暂定名：

**CodexQuotaShare**

项目定位：

一个面向 ChatGPT Plus / Pro 用户的开源 Windows 工具，用于在同一 OpenAI 账号登录的多台个人电脑之间：

1. 查看 OpenAI Codex 当前真实周额度；
2. 查看每台电脑估算的周额度消耗；
3. 为不同设备设置独立的周使用上限；
4. 在设备达到额度时进行本地限制；
5. 在所有设备之间同步额度状态；
6. 在任意设备达到限额时通知所有在线设备；
7. OpenAI 官方额度重置后自动开启新的统计周期；
8. 不要求主电脑持续在线；
9. 不要求用户自行部署数据库、服务器、Docker、Python、Node.js 等环境；
10. 最终以 GitHub Release 中的 Windows ZIP / EXE 形式发布，普通用户下载、解压即可运行。

本项目目标是成为类似 CodexBar、QuotaScope 一类的轻量开源桌面工具，而不是企业级设备管理平台。

---

# 2. 核心用户场景

典型用户：

* ChatGPT Plus 用户；
* ChatGPT Pro 用户；
* 同一个 OpenAI 账号登录 2 至 4 台自己的 Windows 电脑；
* 希望限制其中部分设备的 Codex 周使用量；
* 不希望某一台次要电脑过度消耗整个账号的周额度。

示例：

用户有：

* MAIN-PC
* LAB-PC
* LAPTOP
* OFFICE-PC

设置：

```text
MAIN-PC        Unlimited
LAB-PC         30%
LAPTOP         30%
OFFICE-PC      20%
```

这里的 30% 是：

**OpenAI 当前实际 Codex weekly quota 的 30%**

而不是自行假定某个固定 token 数量或者固定消息数量。

如果 OpenAI 修改 Plus / Pro 的额度，本软件不应要求修改硬编码参数。

---

# 3. 产品设计原则

整个项目必须遵循以下原则。

## 3.1 开箱即用

最终用户不应需要：

* 安装 Python；
* 安装 Node.js；
* 安装数据库；
* 配置 Redis；
* 配置 NATS；
* 安装 Docker；
* 配置 VPS；
* 配置端口；
* 手动生成 API Key；
* 手动配置 OpenAI Token；
* 修改配置文件。

理想用户流程：

```text
GitHub Release
    ↓
下载 CodexQuotaShare-win-x64.zip
    ↓
解压
    ↓
运行 CodexQuotaShare.exe
    ↓
Create Group / Join Group
    ↓
完成
```

---

## 3.2 主电脑不是服务器

不要设计：

```text
MAIN-PC
   ↓
LAB-PC
   ↓
LAPTOP
```

也不要让其他电脑连接 MAIN-PC 的 IP 或端口。

正确架构：

```text
MAIN-PC ─────┐
LAB-PC ──────┤
LAPTOP ──────┼──── Cloud Relay
OFFICE-PC ───┘
```

所有设备只连接统一的云端 Relay。

因此：

```text
MAIN-PC OFF
```

不能导致其他设备停止运行。

产品层面可以存在：

```text
Owner Device
Member Device
```

但网络层面不应该存在：

```text
Server PC
Client PC
```

---

# 4. 推荐技术架构

整个项目分为两个主要组件。

```text
/
├── client/
│   └── Windows Desktop Client
│
├── relay/
│   └── Cloudflare Worker + Durable Object
│
├── docs/
│
├── tests/
│
└── README.md
```

---

# 5. Windows Client

建议：

```text
C#
.NET
WinUI 3
```

优先参考和复用 QuotaScope 的 Windows UI 和 Codex App Server 交互方式。

最终发布必须：

```text
self-contained
win-x64
portable
```

普通用户不能被要求额外安装 .NET Runtime。

---

# 6. 云端 Relay

使用：

```text
Cloudflare Workers
+
Durable Objects
+
WebSocket
```

第一版不要引入：

```text
PostgreSQL
Redis
NATS
Kafka
Supabase
Firebase
Docker
```

除非确实发现 Cloudflare Durable Object 无法满足需求，否则禁止自行扩大基础设施复杂度。

一个 Device Group 对应一个 Durable Object。

例如：

```text
GROUP-XK792Q
```

对应：

```text
QuotaGroup:XK792Q
```

Durable Object 保存：

```text
group
devices
policies
weekly epoch
usage ledger
current OpenAI quota snapshot
notifications
```

---

# 7. 开源项目参考

优先研究以下项目。

## 7.1 QuotaScope

用途：

作为 Windows 客户端的主要基础。

重点研究：

* Windows tray；
* Codex App Server 生命周期；
* JSON-RPC；
* account/rateLimits/read；
* account/rateLimits/updated；
* Windows notification；
* self-contained build；
* settings；
* startup；
* quota UI。

如果 License 允许，可以 Fork。

如果不适合直接 Fork，则重新实现相关功能。

---

## 7.2 srmdn/codex-meter

主要用于研究：

```text
~/.codex/sessions/
```

如何解析：

* sessions；
* token usage；
* activity；
* local usage statistics。

本项目需要使用本机 Codex activity 来辅助判断：

```text
某段时间是哪几台设备产生了 OpenAI weekly quota 消耗
```

不要简单地把 token 数直接当作 OpenAI weekly quota。

---

## 7.3 Waveshare CodexMeter

仅作为以下设计参考：

* device ID；
* device secret；
* HMAC；
* nonce；
* timestamp；
* replay protection；
* pairing。

不要使用它的：

```text
Device → Windows Host
```

直连网络架构。

---

## 7.4 Cloudflare Durable Object Examples

研究：

* WebSocket；
* Hibernation；
* Durable Object；
* 多客户端状态同步；
* 广播；
* reconnect；
* persistent state。

---

# 8. License 要求

开发开始前必须检查所有参考项目 License。

原则：

1. 只有 License 允许时才复制代码；
2. 保留必要 attribution；
3. 在 THIRD_PARTY_NOTICES.md 中记录：

   * 项目；
   * URL；
   * License；
   * 使用了哪些代码；
4. 不要复制 License 不兼容的代码。

项目自身优先使用：

```text
MIT License
```

---

# 9. OpenAI Quota Reader

这是最核心模块之一。

组件：

```text
CodexQuotaReader
```

职责：

通过本地：

```text
codex app-server
```

调用 OpenAI Codex App Server。

主要读取：

```text
account/rateLimits/read
```

并监听：

```text
account/rateLimits/updated
```

至少提取：

```text
usedPercent
windowDurationMins
resetsAt
planType
```

必须识别：

```text
windowDurationMins = 10080
```

对应 weekly window。

不要自己假定：

```text
Plus = X messages/week
Pro = Y messages/week
```

---

# 10. Quota Snapshot

客户端内部统一使用：

```text
QuotaSnapshot
```

概念数据结构：

```text
QuotaSnapshot

accountIdHash
planType
weeklyUsedPercent
weeklyRemainingPercent
weeklyResetAt
observedAt
source
```

其中：

```text
weeklyUsedPercent
```

来自 OpenAI。

这是账号级权威数据。

客户端不允许自行修改。

---

# 11. 不上传 OpenAI 凭据

严格要求：

Cloud Relay 永远不能接收到：

* OpenAI password；
* ChatGPT Cookie；
* refresh token；
* access token；
* auth.json；
* conversation；
* prompt；
* source code；
* Codex response；
* 用户文件。

允许上传的只有类似：

```text
weeklyUsedPercent
resetAt
deviceActivity
deviceId
timestamp
sequence
```

如果某项数据不是实现额度同步所必需，则不要上传。

---

# 12. Device Group

每个用户创建一个：

```text
Device Group
```

默认支持：

```text
2～4 devices
```

代码内部不要把逻辑写死为三台设备。

推荐：

```text
MAX_DEVICES = 4
```

但核心数据结构必须使用：

```text
List<Device>
```

而不是：

```text
Device1
Device2
Device3
```

未来应容易扩展到：

```text
8
16
```

设备。

---

# 13. Create Group

Owner 第一次点击：

```text
Create Group
```

服务器生成：

```text
groupId
ownerSecret
joinCode
createdAt
```

例如：

```text
Group ID:
XK792Q
```

客户端显示：

```text
Invite Code: XK792Q
```

如果实现成本不高，可以同时提供二维码。

---

# 14. Join Group

Member Device 输入：

```text
Join Code
```

服务器生成：

```text
deviceId
deviceSecret
```

设备本地保存：

```text
Device ID
Device Secret
```

Device Secret 使用：

```text
Windows DPAPI
```

保护。

禁止明文写入普通 JSON 配置。

---

# 15. Owner 权限

Owner Device 可以：

```text
Rename Device

Set Limit

Remove Device

Change Owner

View Group

Reset local display cache
```

其中：

```text
Set Limit
```

示例：

```text
Unlimited
10%
20%
30%
40%
50%
Custom
```

Custom 范围：

```text
1% ～ 100%
```

---

# 16. Member 权限

Member Device：

可以：

```text
查看所有设备使用量
查看账号总 weekly quota
查看 reset 时间
收到通知
查看自己的 limit
```

不能：

```text
修改自己的 limit
修改其他设备 limit
删除其他设备
修改 server ledger
伪造 reset
```

---

# 17. 设备状态

Device 模型至少包括：

```text
deviceId
displayName
role
limitPercent
estimatedUsagePercent
status
lastSeen
joinedAt
```

status：

```text
ONLINE
OFFLINE
WARNING
LIMIT_REACHED
BLOCKED
```

---

# 18. 本机 Activity Reader

建立模块：

```text
CodexActivityReader
```

读取本机：

```text
~/.codex/sessions/
```

尽可能获得：

```text
token delta
session activity
last activity timestamp
active session count
```

这里只作为：

```text
quota attribution signal
```

不能作为最终 quota。

---

# 19. 设备额度归因算法

这是项目最重要的算法模块。

OpenAI 提供的是：

```text
Account Weekly Quota
```

不是：

```text
Per-device Weekly Quota
```

因此必须进行估算。

定义：

```text
Q(t)
```

为 OpenAI 官方 `weeklyUsedPercent`。

在时间窗口：

```text
[t0, t1]
```

得到：

```text
DeltaQ = Q(t1) - Q(t0)
```

例如：

```text
42.0%
→
44.0%

DeltaQ = 2.0%
```

同期收集所有在线设备的 local activity。

例如：

```text
PC1 tokens delta = 0
PC2 tokens delta = 8M
PC3 tokens delta = 2M
```

则第一版可以按照：

```text
activity weight
```

分配：

```text
PC2 += 1.6%
PC3 += 0.4%
```

---

# 20. 单设备活动特殊情况

如果 DeltaQ > 0：

```text
DeltaQ = 2%
```

而统计窗口内只有：

```text
PC2 active
```

则直接：

```text
PC2 += 2%
```

这是高置信度归因。

---

# 21. Unattributed Usage

必须引入：

```text
Unattributed Usage
```

如果：

```text
OpenAI DeltaQ > 0
```

但没有任何设备报告 activity：

不要强行分配。

例如：

```text
OpenAI
50% → 52%

Devices
no activity
```

则：

```text
unattributed += 2%
```

UI 必须显示：

```text
Other / Unknown
2.0%
```

原因可能包括：

* Codex Web；
* 其他没有加入组的设备；
* activity 统计缺失；
* OpenAI 计量延迟；
* session 文件延迟。

软件不能伪装成拥有不存在的精度。

---

# 22. Attribution Confidence

建议为归因结果增加：

```text
confidence
```

例如：

```text
HIGH
MEDIUM
LOW
```

示例：

只有一台设备 active：

```text
HIGH
```

多个设备 active：

```text
MEDIUM
```

无 activity：

```text
UNATTRIBUTED
```

UI 第一版可以不展示 confidence，但数据层建议保留。

---

# 23. 周期 Epoch

不要仅按照本机日期判断周重置。

建立：

```text
WeeklyEpoch
```

字段：

```text
epochId
resetAt
startedAt
lastQuota
```

判定 OpenAI reset 时至少参考：

```text
resetsAt changed
```

以及：

```text
weeklyUsedPercent sharply decreased
```

必须防止因为：

```text
本机时间修改
```

导致错误 reset。

---

# 24. OpenAI Reset

检测到新周期后：

服务器执行：

```text
create new WeeklyEpoch
```

然后：

```text
device estimated usage = 0
unattributed = 0
device blocked = false
```

所有在线客户端收到：

```text
WEEKLY_RESET
```

通知。

离线电脑再次连接后通过 snapshot 自动更新。

---

# 25. Cloud Relay 数据模型

一个 Durable Object 保存：

```text
GroupState
```

建议逻辑结构：

```text
GroupState
{
    groupId,
    ownerDeviceId,
    devices[],
    weeklyEpoch,
    officialQuota,
    unattributedUsage,
    version,
    updatedAt
}
```

---

# 26. Server 是额度账本权威方

客户端不能上传：

```text
estimatedUsagePercent = 0
```

然后要求服务器接受。

客户端只能上传：

```text
activity report
official quota observation
heartbeat
```

Server 根据这些数据更新：

```text
Ledger
```

因此：

```text
estimatedUsagePercent
```

只能由 Server 修改。

---

# 27. 多设备观察 OpenAI Quota

因为每台电脑都可以通过自己的：

```text
codex app-server
```

看到同一个 OpenAI 账号 quota，

所以多个设备都会上传：

```text
QuotaObservation
```

服务器应：

1. 比较时间戳；
2. 检查值是否合理；
3. 合并相同观察；
4. 不因为单台设备异常立即 reset；
5. 优先采用较新的有效数据。

不要要求 Owner 在线才能获取 OpenAI quota。

---

# 28. Cloud Relay 通信

推荐：

```text
WebSocket
```

启动：

```text
connect
authenticate
receive snapshot
```

随后保持连接。

发送事件：

```text
HEARTBEAT

ACTIVITY_UPDATE

QUOTA_OBSERVATION

POLICY_UPDATE
```

接收：

```text
GROUP_SNAPSHOT

DEVICE_UPDATED

QUOTA_UPDATED

DEVICE_LIMIT_REACHED

WEEKLY_RESET

DEVICE_REMOVED
```

---

# 29. 重连

网络断开后：

使用：

```text
exponential backoff
```

例如：

```text
1s
2s
5s
10s
30s
60s
```

最大：

```text
60s
```

重新连接成功后：

不要依赖旧增量事件。

必须首先：

```text
GET FULL SNAPSHOT
```

然后恢复状态。

---

# 30. Server State Version

GroupState 增加：

```text
version
```

每次 authoritative state 更新：

```text
version++
```

客户端只接受：

```text
newVersion > currentVersion
```

避免旧消息覆盖新状态。

---

# 31. 基础防伪装

本项目：

```text
防小人不防君子
```

不追求企业级反篡改。

每个 Device 保存：

```text
deviceId
deviceSecret
```

请求包含：

```text
deviceId
timestamp
nonce
sequence
payload
signature
```

signature：

```text
HMAC-SHA256
```

Server 检查：

```text
signature
timestamp
nonce
sequence
```

防止最简单的：

* 请求伪造；
* replay；
* 随意修改 Device ID。

---

# 32. 安全边界

不要声称：

```text
Administrator cannot bypass it
```

拥有 Windows Administrator 权限的用户理论上可以：

* 终止进程；
* 删除程序；
* 修改防火墙；
* 修改文件；
* 重装程序。

本项目只要求：

普通用户不能通过简单修改 UI 或 JSON：

```text
usage = 0
```

就绕过 Server ledger。

---

# 33. Local Enforcement

实现两个级别。

## Level 1

默认：

```text
Soft Enforcement
```

设备达到：

```text
estimatedUsagePercent >= limitPercent
```

后：

```text
status = LIMIT_REACHED
```

客户端：

1. Windows Toast；
2. UI 红色警告；
3. 检测 Codex Desktop / CLI 使用；
4. 阻止新的本机 Codex 使用，或者关闭相应程序。

实现方式选择稳定、侵入性最低的一种。

---

# 34. Level 2

可选：

```text
Enhanced Enforcement
```

第一次启用：

请求 Windows Administrator / UAC。

可以通过：

```text
Windows Firewall
```

进行更强限制。

要求：

达到额度：

```text
BLOCK
```

新 weekly epoch：

```text
UNBLOCK
```

此功能不能默认强制开启。

---

# 35. 限额判断

例如：

```text
LAB-PC
limit = 30%
usage = 29.4%
```

继续允许。

更新：

```text
usage = 30.1%
```

则：

```text
LIMIT_REACHED
```

不要试图保证：

```text
usage <= 30.000000%
```

因为 OpenAI quota 更新和任务执行存在粒度问题。

软件文档必须说明：

这是：

```text
approximately 30%
```

的设备额度管理。

---

# 36. 达到限额后的广播

假设：

```text
LAB-PC
30%
```

Server 产生：

```text
DEVICE_LIMIT_REACHED
```

所有在线电脑收到：

```text
LAB-PC reached its weekly limit.

30.0 / 30.0%

Reset:
2026-XX-XX XX:XX
```

LAB-PC 本机额外执行 Enforcement。

其他电脑仅显示通知。

---

# 37. Windows Tray

程序启动后默认最小化到系统托盘。

Tray 显示：

```text
CodexQuotaShare
```

点击显示主面板。

右键：

```text
Open
Sync Now
Pause Notifications
Settings
Exit
```

Owner 额外：

```text
Manage Devices
```

---

# 38. 主界面

建议主界面：

```text
CodexQuotaShare
────────────────────────

OpenAI Account

Weekly Used
████████████░░░░░░ 46%

Reset
3d 14h

────────────────────────

Devices

MAIN-PC
Owner
17.4%
Unlimited

LAB-PC
██████████████░░░░
26.7 / 30%

LAPTOP
██████░░░░░░░░░░░░
11.2 / 30%

OFFICE-PC
████████████████████
30.0 / 30%
LIMIT REACHED

────────────────────────

Other / Unknown
2.0%

Last sync: 8 sec ago
```

---

# 39. 离线设备

例如：

```text
LAB-PC
Offline
Last seen 2h ago
Usage 20.4 / 30%
```

不要删除历史状态。

达到新 epoch 时：

服务器依然将其 usage reset。

下次上线直接获取新状态。

---

# 40. Settings

至少包括：

```text
Launch at startup

Desktop notifications

Soft enforcement

Enhanced enforcement

Device name

Group information

Leave group

Debug logging
```

Owner：

```text
Manage limits

Remove devices

Transfer ownership
```

---

# 41. Privacy 页面

UI 中增加简单 Privacy 页面。

明确：

```text
Never uploaded:

OpenAI credentials
ChatGPT cookies
prompts
responses
source code
conversation contents
files
```

上传：

```text
device identifier
quota percentage
reset time
local activity counters
timestamps
```

---

# 42. Logging

本机日志禁止包含：

```text
OpenAI credentials
Device Secret
ChatGPT content
prompt
response
```

允许：

```text
quota observation
sync state
group event
error
```

提供：

```text
Debug Logging
```

开关。

---

# 43. Relay 日志

云端默认不记录完整请求 payload。

仅保留必要的：

```text
errors
rate limiting
group state metadata
```

避免建立没有意义的用户活动数据库。

---

# 44. API Version

所有协议增加：

```text
protocolVersion
```

例如：

```text
1
```

未来客户端和 Relay 更新时保持兼容。

---

# 45. Server API

第一版尽量少。

例如：

```text
POST /v1/groups
POST /v1/groups/join
GET /v1/groups/:id
WS   /v1/groups/:id/ws
```

所有实时功能优先通过：

```text
WebSocket
```

完成。

---

# 46. Rate Limit

Relay 必须实现简单 rate limit。

例如每个 device：

```text
activity update
<= 1 / 5 sec
```

正常情况下不需要这么频繁。

客户端应该在：

```text
数据变化
```

时上传，而不是毫无意义地高频轮询。

---

# 47. Activity 汇总频率

建议：

本机 activity 可以：

```text
10～30 秒
```

聚合一次。

OpenAI quota：

优先监听：

```text
rateLimits/updated
```

其次定期：

```text
rateLimits/read
```

作为 fallback。

避免过度轮询。

---

# 48. Cloudflare 成本目标

设计目标：

典型：

```text
4 devices
24/7
```

在 Cloudflare Free Tier 下应该能够正常运行。

如果当前实现明显产生过量 Worker / DO 请求：

优先优化通信方案，而不是要求普通用户付费。

---

# 49. Self-host

第一版可以不做完整 GUI self-host 功能。

但 repo 必须允许：

```text
cd relay

npm install

wrangler deploy
```

部署自己的 Relay。

README 提供：

```text
Deploy to Cloudflare
```

说明。

未来可以增加：

```text
Deploy to Cloudflare
```

一键按钮。

---

# 50. 默认公共 Relay

代码结构需要支持：

```text
DEFAULT_RELAY_URL
```

例如：

```text
https://relay.example.com
```

同时 Advanced Settings 可以：

```text
Custom Relay URL
```

方便社区自行部署。

---

# 51. 更新机制

第一版可以只实现：

检查 GitHub Releases。

不要做自动静默更新。

UI 可以提示：

```text
New version available
v1.1.0
```

点击打开 Release。

---

# 52. GitHub Release

Release 至少提供：

```text
CodexQuotaShare-win-x64.zip

SHA256SUMS.txt
```

不要要求普通用户：

```text
git clone
dotnet build
npm install
```

---

# 53. CI/CD

GitHub Actions：

至少实现：

```text
build client
test client
build relay
test relay
package portable ZIP
calculate SHA256
create release artifact
```

---

# 54. Tests

必须建立自动测试。

## Client

测试：

```text
quota parsing
weekly window selection
reset detection
activity parsing
HMAC signing
state version
limit detection
```

---

# 55. Relay Tests

至少：

```text
create group
join device
max device limit
owner authorization
member authorization
quota observation
activity update
usage attribution
unattributed usage
weekly reset
limit reached
device removal
reconnect
replay rejection
invalid signature
```

---

# 56. Attribution Tests

特别测试：

### Case A

```text
DeltaQ = 2%

PC2 activity = 100
PC3 activity = 0
```

结果：

```text
PC2 += 2%
```

### Case B

```text
DeltaQ = 2%

PC2 activity = 80
PC3 activity = 20
```

结果：

```text
PC2 += 1.6
PC3 += 0.4
```

### Case C

```text
DeltaQ = 2%

all activity = 0
```

结果：

```text
unattributed += 2
```

---

# 57. Reset Tests

例如：

```text
epoch A

used = 83%
resetAt = T1
```

随后：

```text
used = 1%
resetAt = T2
T2 != T1
```

结果：

```text
new epoch
all device usage = 0
unattributed = 0
all blocked state cleared
```

---

# 58. Restart Tests

Client restart：

必须恢复：

```text
device identity
group membership
settings
last snapshot
```

Server restart：

Durable Object state：

必须保留：

```text
devices
limits
ledger
epoch
```

---

# 59. Offline Tests

模拟：

```text
PC2 offline
```

PC3继续消耗 quota。

PC2重新上线。

必须：

```text
receive latest full snapshot
```

不能依赖缺失的 WebSocket event。

---

# 60. Owner Offline Test

这是硬性验收项。

场景：

```text
Owner PC OFF
Member A ON
Member B ON
```

要求：

* quota monitoring 正常；
* activity sync 正常；
* weekly reset 正常；
* notification 正常；
* enforcement 正常。

Owner 离线不能造成系统停止。

---

# 61. Privacy Tests

确认网络层绝对不会发送：

```text
auth.json
ChatGPT cookie
prompt text
response text
Codex source file
```

建议加入测试确保序列化模型中不存在这些字段。

---

# 62. 项目开发阶段

不要一次完成所有功能。

按照以下顺序。

---

## Phase 0：Repository Investigation

先研究：

```text
QuotaScope
srmdn/codex-meter
Waveshare CodexMeter
Cloudflare DO websocket examples
```

输出：

```text
docs/research.md
```

内容：

* License；
* 可复用组件；
* Codex app-server 调用方式；
* session 数据格式；
* Cloudflare 设计；
* 风险。

完成后再正式编码。

---

# 63. Phase 1：Local Quota MVP

只做：

```text
Windows app
+
OpenAI weekly quota
```

要求：

```text
显示 weekly used
显示 remaining
显示 resetAt
监听 quota update
tray icon
```

还不要做 cloud。

---

# 64. Phase 2：Local Activity

增加：

```text
CodexActivityReader
```

显示：

```text
Today activity
Current session
Token delta
```

验证 session parsing 稳定性。

---

# 65. Phase 3：Relay

实现：

```text
Cloudflare Worker
Durable Object
Group
Device
WebSocket
```

验证：

```text
2 clients
4 clients
```

均可实时同步。

---

# 66. Phase 4：Pairing

实现：

```text
Create Group
Join Group
Owner
Member
Device Secret
HMAC
```

---

# 67. Phase 5：Global Dashboard

所有设备显示：

```text
OpenAI weekly
all devices
device limits
device usage
unattributed
```

---

# 68. Phase 6：Attribution

实现：

```text
DeltaQuota
+
ActivityWeight
```

归因算法。

必须写单元测试。

---

# 69. Phase 7：Reset

实现：

```text
WeeklyEpoch
```

自动 reset。

重点测试：

Owner offline。

---

# 70. Phase 8：Notifications

实现：

```text
DEVICE_LIMIT_REACHED
WEEKLY_RESET
```

Windows Toast。

---

# 71. Phase 9：Soft Enforcement

设备达到 quota：

```text
Soft Enforcement
```

开始工作。

---

# 72. Phase 10：Enhanced Enforcement

作为可选功能。

不要让这一步阻塞 MVP Release。

---

# 73. Phase 11：Packaging

完成：

```text
portable ZIP
GitHub Actions
README
Privacy
License
Screenshots
```

---

# 74. 第一版 MVP 范围

v0.1 必须包含：

```text
Windows x64
Plus / Pro
2～4 devices
Create Group
Join Group
Owner / Member
OpenAI weekly quota
resetAt
local activity
device usage estimation
device limits
cross-device dashboard
cross-device notification
weekly automatic reset
soft enforcement
portable ZIP
```

---

# 75. v0.1 明确不做

不要做：

```text
macOS
Linux GUI
Android
iOS
企业账户管理
组织级权限
多 OpenAI 账号
网页管理后台
复杂数据库
用户注册系统
付款系统
高级反作弊
HTTPS MITM
ChatGPT conversation inspection
```

防止 scope creep。

---

# 76. UI 风格

整体目标：

```text
简单
原生
轻量
信息密度适中
```

参考：

```text
CodexBar
QuotaScope
Windows 11 Settings
```

不要制作：

```text
复杂 Dashboard
大量动画
Web-style admin panel
```

---

# 77. README 首屏

最终 README 应快速说明：

```text
CodexQuotaShare

Share and limit Codex weekly quota
across your own computers.

✓ 2–4 Windows PCs
✓ ChatGPT Plus / Pro
✓ Per-device weekly limits
✓ Automatic OpenAI reset detection
✓ Cross-device quota dashboard
✓ Cross-device notifications
✓ No OpenAI credentials uploaded
✓ Portable
✓ Open source
```

然后：

```text
Download
```

直接指向 GitHub Releases。

---

# 78. README 必须解释精度问题

明确说明：

OpenAI 当前公开 quota 是：

```text
account-level
```

不是：

```text
device-level
```

所以设备使用量通过：

```text
OpenAI account quota delta
+
local Codex activity
```

进行估算。

不要宣传为：

```text
100% exact per-device OpenAI usage
```

应描述为：

```text
estimated per-device allocation
```

---

# 79. 软件内部同样区分两个指标

绝对不能把两者混为一谈：

```text
Official Account Usage
```

来自 OpenAI。

和：

```text
Estimated Device Usage
```

来自本项目的归因算法。

UI 和代码命名都必须体现这个区别。

---

# 80. 错误处理

如果：

```text
codex app-server unavailable
```

UI：

```text
Unable to read OpenAI quota
```

但不要崩溃。

如果：

```text
Cloud Relay unavailable
```

继续保留：

```text
local quota display
local cache
```

UI：

```text
Sync offline
```

恢复后自动重新同步。

---

# 81. Fail-safe

如果云端暂时离线：

不要重置任何：

```text
device usage
```

不要自动解除：

```text
LIMIT_REACHED
```

等待权威 snapshot。

---

# 82. 数据最小化

服务器只存实现产品必需的数据。

尽量不要建立：

```text
每分钟历史 usage 数据库
```

第一版只需要：

```text
current epoch
current ledger
lastSeen
必要 event
```

---

# 83. Performance

Windows Client：

目标：

Idle CPU：

```text
接近 0%
```

Idle memory：

尽可能：

```text
< 150 MB
```

如果 WinUI 3 本身造成一定额外内存可接受，但禁止后台高频轮询造成持续 CPU 消耗。

---

# 84. Repository Quality

项目必须保持：

```text
clear structure
nullable enabled
async cancellation
structured logging
unit tests
minimal dependencies
```

禁止随意添加大量第三方依赖。

---

# 85. 开发过程要求

Codex 开发过程中：

1. 每个 Phase 先检查现有代码；
2. 不要重复实现已有功能；
3. 每个主要功能完成后运行测试；
4. 不要为了修复一个功能破坏现有功能；
5. 不要随意改变已经确定的总体架构；
6. 如果发现需求技术上不可实现，明确记录原因；
7. 优先实现稳定 MVP，而不是扩大范围；
8. 不要为了“更专业”擅自增加复杂基础设施。

---

# 86. 重要架构禁令

除非存在明确技术阻塞，否则禁止改成：

```text
Main PC server
LAN direct connection
central VPS application server
PostgreSQL
Redis
NATS
Docker requirement
OpenAI proxy
MITM
browser extension
API key based quota estimation
```

当前确定方案就是：

```text
Windows Client
        ↓
Cloudflare Worker
        ↓
Durable Object
```

---

# 87. 第一阶段最终验收场景

准备 4 台模拟客户端：

```text
MAIN-PC
LAB-PC
LAPTOP
OFFICE-PC
```

限制：

```text
MAIN-PC       Unlimited
LAB-PC        30%
LAPTOP        30%
OFFICE-PC     20%
```

要求全部通过：

### Test 1

四台设备都能看到：

```text
OpenAI weekly quota
```

### Test 2

四台都能看到其他设备。

### Test 3

关闭 MAIN-PC。

其他三台继续正常运行。

### Test 4

LAB-PC产生 Codex activity。

其 estimated usage 增长。

### Test 5

LAB-PC达到 30%。

LAB-PC：

```text
LIMIT_REACHED
```

所有在线电脑弹通知。

### Test 6

重新启动 LAB-PC。

仍然：

```text
LIMIT_REACHED
```

不能因为重启清零。

### Test 7

OpenAI weekly quota reset。

所有设备 usage：

```text
→ 0
```

LAB-PC：

```text
unblocked
```

### Test 8

关闭 Cloud Relay 网络。

客户端不崩溃。

恢复网络后重新同步。

### Test 9

Member 尝试修改：

```text
limit
ledger
```

Server 拒绝。

### Test 10

修改本机缓存：

```text
usage = 0
```

重新连接 Server 后：

恢复 Server authoritative value。

---

# 88. 最终产品目标

用户只需要理解三个概念：

```text
Account quota
Device limit
Device usage
```

不能要求普通用户理解：

```text
Durable Object
WebSocket
HMAC
JSON-RPC
App Server
```

这些全部属于内部实现细节。

最终用户体验应接近：

```text
Download
Run
Create Group
Join Group
Set 30%
Done
```

这就是整个项目最重要的产品目标。

---

# 89. Codex 开始任务时的执行要求

开始后不要立刻大量写代码。

首先完成：

```text
Phase 0
```

并检查当前 repository。

然后输出：

```text
1. 当前目录结构
2. 参考项目调查结果
3. License 情况
4. 推荐的最终 repo architecture
5. MVP implementation plan
6. 当前发现的技术风险
```

确认项目基础没有明显问题后再开始 Phase 1。

如果 repository 当前为空，则创建符合本文档要求的 monorepo。

不要偏离本文档中已经确定的核心产品架构。
