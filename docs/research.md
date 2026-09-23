# Phase 0 — Repository Investigation

调查日期：2026-09-18。状态：调研完成；生产功能尚未实现。

## 1. 当前目录与调查边界

任务开始时，CodexQuotaShare 目录中只有 task.md，没有源代码、构建配置或本项目的 Git 元数据。用户随后授权检查同级参考目录；本次仅阅读这些参考项目，没有修改或运行其程序。

| 本地参考快照（相对于用户提供的参考目录） | 正确上游 | 版本识别 |
| --- | --- | --- |
| quota-scope/quota-scope-main | [EvanHexX/quota-scope](https://github.com/EvanHexX/quota-scope) | 解压快照，commit 未知 |
| meter/codex-meter-0.6.0 | [srmdn/codex-meter](https://github.com/srmdn/codex-meter) | package.json 确认 0.6.0 |
| codexmeter2/codex-meter-main | [waveshareteam/codex-meter](https://github.com/waveshareteam/codex-meter) | 解压快照，commit 未知 |
| workerschat/workers-chat-demo-master | [cloudflare/workers-chat-demo](https://github.com/cloudflare/workers-chat-demo) | 解压快照，commit 未知 |

GitHub 上存在同名 QuotaScope 项目；本次依据本地 README 和项目文件确认 Windows 项目为 EvanHexX/quota-scope，不能把同名 Swift 或 Shell 项目当成 Windows 基础。上游元数据在线核验成功，但不把 main 当前版本冒充成本地快照版本。[reference-manifest.json](reference-manifest.json) 记录实际检查文件的 SHA256。

## 2. License 与复用结论

| 项目 | 实际 LICENSE | 可参考/复用内容 | 本次实际引入 |
| --- | --- | --- | --- |
| QuotaScope | MIT，2026 HexX | WinUI 启动、原生托盘、App Server 生命周期、设置与自包含配置 | 仅调查和许可证原文 |
| srmdn/codex-meter | MIT，2026 Said | JSONL session 格式、累计 token、合成测试样例 | 仅调查和许可证原文 |
| Waveshare CodexMeter | MIT，2026 CodexMeter contributors | nonce、HMAC domain separation、常量时间比较 | 仅调查和许可证原文 |
| Cloudflare workers-chat-demo | BSD-3-Clause，2020 Cloudflare | Worker 路由、DO、WebSocket Hibernation、attachment 恢复 | 仅调查和许可证原文 |

四个许可证允许在遵守条款的情况下用于 MIT 项目。复制或改写实质代码时必须保留对应版权和许可；BSD-3-Clause 还禁止借用上游名称背书。完整原文及本次使用范围见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。将来新增 NuGet/npm 依赖、图标、字体或打包 Codex 二进制，需要分别核验，不由本次四份许可证覆盖。

建议创建独立 monorepo，按模块复用经过检查的实现，而不是整体复制 QuotaScope：它包含 Claude、多主题、多窗口布局等超出本项目范围的功能；WinUI 项目还直接编译 legacy app 的共享文件，整包搬入会带入不必要耦合。托盘代码是优先复用候选；App Server 和额度映射需按下文修正后再引入。

## 3. Codex App Server 调查

证据文件：QuotaScope 的 app/Providers/Codex/CodexAppServerClient.cs、RateLimitMapper.cs、CodexCommandResolver.cs，以及两份 codex-meter 的 App Server 实现。

已确认调用顺序：

1. 启动本地 codex app-server，重定向 stdin/stdout/stderr，不打开终端窗口。
2. 使用按行 JSON 请求发送 initialize，包含 clientInfo；按支持版本决定 capabilities。
3. 收到初始化响应后发送 initialized 通知。参考实现明确修复过遗漏该通知导致后续请求超时的问题。
4. 调用 account/rateLimits/read，按 id 匹配响应；监听 account/rateLimits/updated 的 params。
5. 使用低频读取作为通知的补充。单独启动的 App Server 能否实时感知其他 Desktop/CLI 进程消耗，必须实机验证，不能假设通知覆盖全部活动。

### 周窗口选择

旧样例：primary 为 300 分钟，secondary 为 10080 分钟。QuotaScope 另有 primary 为 10080、secondary 为 null、usedPercent 为小数的样例。

- 只在账号级 codex 限额中按 windowDurationMins == 10080 选择周窗口；不能把 secondary 固定当作 weekly。
- 支持 rateLimits 和 rateLimitsByLimitId.codex 的明确路径，不递归选择任意模型窗口。
- Spark 等模型额度不能代替账号额度；不能使用 QuotaScope 的 max(primary, secondary) 作为 weeklyUsedPercent。
- missing、null、非数值、非有限值、越界值或歧义应显示 unavailable；不能补成 0 或通过 clamp 掩盖错误。
- resetsAt 以支持版本协议定义的 Unix 时间为准。参考 mapper 兼容秒/毫秒；若保留兼容，必须有明确样例和范围验证。
- remaining = 100 - used，仅是同一官方观察的派生显示值；不引入固定 token/消息分母。

### 生命周期与日志改进

参考代码展示了 pending request、取消、15 秒超时和子进程销毁，但不能原样视为生产级实现。新模块需要串行化 stdin 写入；在超时、取消、EOF、非法行和进程退出后移除 pending 并完成等待者；每代进程使用独立 reader 和取消令牌；不得把原始 stderr 或 RPC error 直接写入日志，因为可能含敏感信息。退出仅清理本工具创建的 reader 子进程。

### 登录身份与开箱即用

quota 响应不提供可靠的账号唯一标识。account/read 的字段随版本演进，需要独立适配身份读取；不能拿 planType、额度或重置时间散列冒充 accountIdHash。官方 openai/codex 的 App Server README 当前描述了 experimental workspaceRouting.chatgptAccountId，但不能假定所有已安装版本提供此字段。缺少可靠身份时保留本地查看，禁止静默合并不确定账号的云端账本。

优先让 Codex 自身管理登录；本工具不读取 auth.json、不接触浏览器 Cookie。后续选择可用稳定身份后，用组内 salt 产生一致的伪匿名 hash；不得上传邮箱、原始账号 ID 或 account/read 原文。

QuotaScope 通过已安装路径寻找原生 codex.exe，并回退到 PATH 中 codex。其 README 仍要求已安装 Codex CLI/App Server。因此“self-contained .NET”不等于包含 Codex。Phase 1 应验证 Desktop 自带原生程序的发现方式；若必须附带 Codex，则另做二进制许可证、来源、完整性和升级策略审查，不能让普通用户安装 Node.js 来补依赖。

官方网页 https://developers.openai.com/codex/app-server/ 本次返回 HTTP 403；GitHub API 可读取 [官方 App Server README](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)。未进行真实账号调用，接口结论以本地源码/样例和成功取得的官方资料为限。

## 4. Session 数据与活动信号

证据：srmdn/codex-meter 的 src/session-history.ts 和 tests/fixtures/sessions.synthetic。

JSONL 的相关结构：session_meta.payload.id、turn_context、event_msg.payload.type == token_count；累计值在 payload.info.total_token_usage，下含 input_tokens、cached_input_tokens、output_tokens、reasoning_output_tokens、total_tokens。last_token_usage 表示最近用量，不应在重复事件上反复累加。

参考工具做历史汇总、遍历所有文件并保留最新累计值；不能直接当作 10–30 秒增量 reader：

- 按 session/file 维护已读字节位置、未结束行、累计计数基线；重复记录不重复计入。
- 新安装首次历史扫描建立 baseline，不把历史 token 一次性算到当前观察区间。
- 处理追加半行、截断、文件替换、累计计数回退、重启和跨周；持久化游标只留本地。
- 先按事件时间提取区间增量，再汇总 today/activity/active-session count；不以文件修改时间代表所有历史事件。
- cached input 已包含在 input，reasoning 可能包含在 output；不能把这些子项再次加到 total。
- token 数只做权重。没有重叠时间区间或有无法消除的数据缺口，宁可记为 Other / Unknown。
- prompt、response、路径、源代码、session 原文和 session ID 不进入 Relay DTO。测试只用合成数据。

本次没有读取本机真实 ~/.codex/sessions 或 auth.json。

## 5. Cloudflare 架构与费用

参考 src/chat.mjs 已使用 state.acceptWebSocket、getWebSockets、serializeAttachment、deserializeAttachment、webSocketMessage。wrangler.toml 已使用 new_sqlite_classes。可借鉴其连接恢复和广播结构，但不能复制公开聊天室的身份模型、原始错误堆栈返回和聊天历史存储。

设计采用一个组一个 QuotaGroup Durable Object。Worker 只做路由、请求边界与公共入口限流；DO 对组成员、权限、账本、epoch、sequence 和 version 做持久化权威更新。Owner 是角色，不是服务器。

- 使用 SQLite-backed DO，可通过 storage.get/put 存小型状态；这是托管内置存储，不是让用户部署 SQLite/数据库服务。
- 使用 Hibernation API，连接 attachment 只保存恢复连接所需身份引用；账本、nonce/sequence 等不能仅存在内存或 attachment。
- 先持久化状态再广播；重启/休眠后恢复完整快照；同一设备重连替换旧连接。
- WebSocket 控制 ping 或 auto-response 用于保活；不要每 5 秒持久化/广播无变化心跳。lastSeen 与业务观察时间分开处理。
- 分组邀请码不能直接等于公开 groupId。使用含路由信息的高熵邀请码、有效期、撤销与轮换；邀请有效期内 Owner 离线也允许加入。

[Cloudflare pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) 与 [WebSocket 文档](https://developers.cloudflare.com/durable-objects/best-practices/websockets/) 于调查日读取成功。当前 Free 仅支持 SQLite-backed DO，DO requests 100,000/日、duration 13,000 GB-s/日、SQLite 写入 100,000 rows/日、读取 5,000,000 rows/日、存储合计 5 GB。超出免费额度会失败。WebSocket 入站消息有计费 20:1 折算；不能把计费折算当作所有运行时/配额指标的保证。

保守粗算：4 台每 30 秒均有业务变化约 11,520 个入站消息/日，每消息 1–3 行写入约 11,520–34,560 行/日；若每 5 秒发送则 69,120 个消息/日，多行写入可能突破免费额度。实际事务、索引、alarm 和重连会增加开销，必须实测。一组可行不等于公共 Relay 为所有用户永久免费；免费配额按 Cloudflare 账号共享。Hibernation 降低时长成本，但不能忽略所有组的总量。

## 6. 安全与计量的实际边界

### Relay 无法独立证明 OpenAI 观察真实性

不上传 OpenAI 凭据、没有 OpenAI 对额度快照的可验证签名时，Relay 只能校验已认证设备提交的声明。HMAC 防外部伪造和重放，不能阻止持有自己 deviceSecret 的修改版客户端谎报 quota/activity/reset。多设备交叉确认可降低单点异常，却不构成 OpenAI 级证明。

实现将保证 Member 无权直接改 policy、ledger、epoch；重置由服务器状态机判定。不得宣传能绝对阻止恶意 Member 伪造官方观察。任务中的“不能伪造 reset”按直接写入被拒绝与异常观察不立即重置来验收，并明确上述边界。

### 归因与重置

到达顺序并不是观测时间顺序。DeltaQ 先形成待结算区间，留出有界等待窗口收集各设备该区间 activity；每份 activity 仅消耗一次，重复 quota 不反复归因。算法必须输出置信度，遗漏数据落入 unattributed。

同一 epoch 维护不回退的计量高水位，防止 42→41→42 被重复记账，同时保留最新有效官方显示值；差异作为 correction 单独记录，不能伪称账本与官方瞬时值永远相等。

重置候选需 resetAt 向前、使用量明显下降、服务器时间合理且后续观察确认。多个在线设备优先交叉确认；只有一个 Member 在线时允许其间隔复读确认，以满足 Owner offline。单设备恶意报告的边界仍存在。小幅 resetAt 漂移、只降百分比、旧 epoch 消息和本机时钟改动均不能直接清零。低用量周、reset credits 或窗口策略改变需专门样例，不能在证据不足时强行重置。

### 本地限制

纯显示红色警告不满足任务书的 Soft Enforcement。建议跟踪经验证的 Codex 可执行路径/进程身份，在超限时对用户启用管理的 Desktop/CLI 实施最小侵入的关闭/阻止策略；豁免读取 quota 的自建 App Server，避免达到限额后无法观察重置。不能只按模糊进程名批量终止；后台任务丢失风险和管理员可绕过的边界必须在启用界面说明。Phase 0 不执行任何进程终止或防火墙操作。

### 其他需要实际验证的问题

- QuotaScope 托盘的 ShowNotification 使用 Shell_NotifyIcon/NIF_INFO，不能直接等同于任务要求的完整 Windows Toast；Phase 8 应验证 unpackaged Toast 注册与投递。
- 本地 DPAPI 加密状态绑定 Windows 用户/设备，复制 ZIP 不应复制已配对身份；安装文件可 portable，身份不因此跨机便携。
- cache 只做离线显示和保持已有限制，重连先取可信完整 snapshot。相同 version 的完整 snapshot 也需要覆盖被本地篡改的 cache；严格 greater-than 仅适用于已验证在线状态的增量顺序，不能阻断权威修复。
- 持久化递增 sequence，nonce 有界过期去重；C# 与 TypeScript 需统一签名字节并共享 golden vectors。并发鉴权与账本写入必须保证重复请求最多生效一次。
- 默认公共 Relay 域名、运营 Cloudflare 账号和 GitHub Release 仓库均未提供。占位 URL 不能冒充可用服务。

## 7. 本机构建条件和阶段结论

已执行：项目内 rg 文件盘点、四份 LICENSE 核验、相关源码与合成样例检查、上游元数据和官方 Cloudflare 文档核验、dotnet --info、git --version、node --version。

环境结果：Windows x64；Git 2.50.0.windows.1；Node v22.19.0；dotnet host 9.0.5，显示 No SDKs were found。QuotaScope 使用 net10.0-windows10.0.19041.0 / WinUI 3 / Windows App SDK 1.8。没有安装或修改任何共享开发环境。

建议沿用 .NET 10 + WinUI 3，固定经构建验证的包版本，最终同时开启 .NET SelfContained 和 WindowsAppSDKSelfContained，win-x64 portable。缺少 SDK 是本机构建阻塞；下一阶段可先在 E:/codexdefault/CodexQuotaShare/toolchains 隔离准备 SDK 和缓存，不修改全局 PATH 或已有 Python 环境。Windows XAML 编译工具链、真实 Codex 发现和 Toast 仍需实际构建/运行验证。

总体结论：许可证和既定架构可行；应保留 Windows Client → Worker → Durable Object。第一阶段交付是本调研与 monorepo 结构。Phase 1 以解决隔离构建条件、合成协议测试和本地 quota UI 为入口，不提前开发云端或增强限制。

相关设计：[architecture.md](architecture.md)；阶段验收：[implementation-plan.md](implementation-plan.md)。

## 8. Phase 0 交付核验

- monorepo 结构及文档已落盘，原 task.md 和参考项目未修改。
- 检查 18 个文档相对链接，目标均存在且位于本项目内。
- 重新核验 manifest 中 21 个参考文件 SHA256，全部一致；4 份许可证副本与参考原文逐字节一致。
- 未运行产品测试或 C# 编译：本阶段尚未引入产品源码，本机无 .NET SDK。上述结果仅是 Phase 0 文档/来源核验，不代表 MVP 验收通过。

## 后续进展

Phase 1 已使用隔离 SDK 完成本机构建、自动测试、真实只读额度联调及 GUI 验证；本报告此前章节保留 Phase 0 调研时点状态，最新情况见 [phase1-validation.md](phase1-validation.md)。
