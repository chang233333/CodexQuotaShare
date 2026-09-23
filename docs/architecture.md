# 架构决策草案

状态：Phase 0 设计；以下模块划分尚未表示功能已实现。

## 总体结构

Windows Client（C# / .NET 10 / WinUI 3）通过 HTTPS/WSS 连接 Cloudflare Worker，由每个组独立的 SQLite-backed QuotaGroup Durable Object 管理状态。所有设备是平等网络节点，Owner 仅有策略管理权限。无需主电脑在线，不引入外部数据库、Docker、代理或 MITM。

目标目录：

~~~text
client/
  CodexQuotaShare.Core/       # 额度模型、解析与客户端状态规则
  CodexQuotaShare.Windows/    # WinUI、托盘、App Server、DPAPI、活动读取
relay/
  src/                       # Worker、QuotaGroup、认证、账本、归因
  tests/                     # Relay 单元与真实 DO 运行时集成测试
protocol/
  fixtures/                  # 跨语言合成协议样例与 HMAC golden vectors
tests/
  CodexQuotaShare.Core.Tests/ # C# 单元测试、模拟 App Server 测试
  scenarios/                 # 2/4 客户端完整验收
scripts/                     # 构建、验证、打包
.github/workflows/           # 分阶段引入 CI、ZIP 与 SHA256 artifact
docs/
licenses/
~~~

## 客户端边界

- Core 不引用 WinUI/进程/网络；QuotaSnapshot 明确区分官方额度和 EstimatedDeviceUsage。
- CodexQuotaReader 负责有超时与取消的本地 JSON-RPC，不产生聊天请求。内部原始 JsonElement 不允许穿透到 Relay 序列化层。
- CodexActivityReader 只在本地消费 JSONL，持久化游标并输出区间计数。
- RelayClient 管理认证、单一发送队列、1/2/5/10/30/60 秒带抖动重连和完整 snapshot 恢复。
- StateStore 分离 DPAPI 身份、用户设置、非权威显示 cache；最后有效限制断网不清除。
- NotificationService 按持久 eventId 去重；本设备执行 Enforcement，其他设备只通知。
- UI 默认进入托盘；官方额度、设备估算、Other / Unknown、数据新鲜度明确区分。

## Relay 边界

Worker 提供 POST /v1/groups、POST /v1/groups/join、认证 GET /v1/groups/:id 和 WS /v1/groups/:id/ws。创建/加入走 TLS，并施加请求体大小、尝试次数和速率限制。groupId 是路由标识，不是认证材料。

QuotaGroup 持久化 GroupState、设备验证密钥、policy、epoch、当前账本、待结算区间、重放保护状态和有限的最近通知。只持久化必要的数据，不建立完整活动历史库。每次对外可见权威变更单调增加 version；先提交事务，再广播。

鉴权和业务验证分层：protocolVersion、消息白名单、大小/类型/数值范围、HMAC、timestamp、nonce、sequence、角色、账号匹配、当前 epoch 和 version。Member 只报告活动/官方观察/心跳，不接受其提交的 ledger 或 blocked 值。Owner 操作也不能绕过 server ledger。

HMAC 的密钥要能用于服务器验签，因此 DO 不能仅保存不可逆 hash 并声称可进行普通 HMAC 验证。设备秘密只在配对时经 TLS 返回对应设备；不进入公共 snapshot、日志或通知。server 存储访问只由 Worker/DO 绑定触达。

连接恢复使用 Hibernation attachment 引用身份，再从持久存储确认设备仍有效；角色以当前存储为准，移除设备或转移 Owner 后不能沿用旧连接权限。

## 归因规则

每个有效官方观察保留 observedAt 和服务器 receivedAt。服务器统一验证时钟偏差并按受限时间窗口处理。相同 quota 不反复入账；迟到/乱序记录不回滚已结算账本。

初始化 epoch 时把首次观察当作 baseline，已有账号消耗进入 Other / Unknown，以保证新加入工具不会把历史用量分配给当前设备。后续官方正向增量形成待结算区间，等待有界活动水位：

1. 唯一活跃设备：全部分配，HIGH。
2. 多台活跃设备：按已验证非负增量权重分配，MEDIUM；最后一项吸收舍入余差。
3. 无可用活动：全部记入 unattributed。

已消费活动不能用于下一区间。设备离线、文件延迟、未知设备和 Codex Web 均可能造成不可归因；保留数据覆盖度，不能把“只收到一台报告”夸大为没有任何其他使用来源。

同一 epoch 用高水位避免下降再回升重复记账。账户更正、已移除设备的历史份额、待结算区间与首次 baseline 要有明确去向；移除设备不能抹去已消费份额。

确认新 epoch 时原子清零设备估算、unattributed 和限制状态，基线设为新官方值。该初值作为 epoch baseline 元数据保留，不伪装成设备消耗。因此新周期首次 1% 观察后，UI 官方值可为 1%，设备与 unattributed 都为 0%，符合任务 reset 用例。

## 重置状态机

正常 → 候选 → 确认。触发候选需新的合理 resetAt、显著下降和服务器时间校验；单台异常不能直接确认。优先多个设备相同观察；仅单个 Member 在线时通过间隔复读确认。阈值与确认间隔必须在实现时用合成案例固定并测试。

只到达本机/服务器日历时间并不等于收到 OpenAI reset；离线继续保留原限制。前一 epoch 的 activity/观察不得进入新 epoch。低用量周未出现明显下降、reset credit 或单独窗口漂移先保留为异常候选，待更强证据，不静默造出新周。

该设计防直接篡改与单次异常，不提供恶意客户端真实额度的密码学证明，详见 research.md。

## 发布与运行依赖

发布产物为 CodexQuotaShare-win-x64.zip 和 SHA256SUMS.txt，包含 .NET 与 Windows App SDK 所需运行文件及许可声明；用户不安装开发 SDK/Node/Python。

用户必须已登录 Codex；原生 app-server 的可发现性需验证。生产 DEFAULT_RELAY_URL 在维护者完成部署后注入；未配置时清楚报告同步不可用，不能把 example.com 当可用默认服务。公共 Relay 与 GitHub Release 发布是最终阶段事项，不在 Phase 0 操作。
