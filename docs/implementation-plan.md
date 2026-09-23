# 分阶段实施与验收

任务书要求按阶段推进，不一次实现全部功能。当前已交付 Phase 0、Phase 1 与 Phase 2 本地 MVP；下列 unchecked 项仍待验证或后续阶段。

## 当前状态

- [x] 检查空项目和四个本地参考快照。
- [x] 核验 MIT / BSD-3-Clause，记录实际来源与文件 SHA256。
- [x] 调查 App Server、session 格式、HMAC、Hibernation 和费用。
- [x] 输出 research.md、架构、实施计划和 monorepo 目录。
- [x] 本地隔离 .NET 10 SDK / WinUI 构建工具链可用。
- [x] Phase 1 实现、36 项自动测试、本机真实额度读取及 GUI smoke。
- [x] Phase 2 本机 Activity 增量读取、历史 baseline、跨日统计及 GUI 展示；60 项自动测试（含 17 项读取器集成测试）、WinUI 构建和扩展 GUI smoke。
- [x] Phase 3 本地 Worker + SQLite-backed QuotaGroup + WebSocket Hibernation；39 项 Relay 测试覆盖 2/4 客户端、持久化、休眠恢复、重启恢复、白名单与活动限流。
- [x] Phase 4 Relay 配对与认证基础；Windows RelayClient、DPAPI identity、单一发送队列、重连、snapshot 缓存和最小配对 UI 已接入；63 项 Core/Activity/RelayAuth/DPAPI/缓存测试、43 项 Relay 测试、Windows Release 构建与 dry-run 验证通过。
- [x] Phase 5–8 Dashboard、官方额度观察、设备归因、WeeklyEpoch 和事件通知已接入；28 项协议测试、23 项 Relay 场景测试、64 项 Core 测试通过。
- [x] Phase 9 Relay 限额策略与 `LIMIT_REACHED` 权威状态已接入；Windows 已增加只作用于本应用受控入口的 fail-safe gate，额度读取不受影响。
- [x] Phase 9 GUI smoke 已覆盖官方额度、设备估算百分比、`unattributed`、`LIMIT_REACHED`、通知和重开生命周期。
- [x] Phase 10 Enhanced Enforcement 边界评估已完成；默认不启用代理、MITM、凭据读取或系统防火墙规则。
- [x] Phase 11 发布准备：CI 客户端/Relay workflow、self-contained ZIP、SHA256、Privacy、许可证打包校验和发布版 GUI smoke 已完成。
- [x] Phase 11 全新解压目录自动审计：SHA256、必需文件、无初始 `data/`、发布版 smoke 和窗口重开已通过。
- [ ] 干净 Windows / Explorer 重启 / 完整人工交互验证。

## Phase 1 — Local Quota MVP

已解决初始 No SDKs were found；当前工具链已构建验证。原实施安排：优先任务专用目录下隔离 SDK、NuGet cache 与临时目录，不修改共享环境。检查 .NET 10 / Windows App SDK 固定版本能否本地构建；不因工具链缺失擅自改成其他 GUI 框架。

实现顺序：Core QuotaSnapshot 与解析器 → 模拟 App Server 协议测试 → 进程生命周期与低频 fallback → WinUI 页面与原生托盘 → self-contained 本地发布验证。

验收：

- [x] primary/secondary 各自承载 weekly；小数、null、缺字段、额外 Spark、异常 reset 时间均正确处理。
- [x] initialize → initialized → read，updated 推送刷新；未知 id、非法行、超时、取消、EOF、重启无悬挂请求。
- [x] 显示 used、remaining、resetAt、数据时间；无登录/无 Codex/无周窗口显示明确 unavailable，保留陈旧值标签。
- [ ] 默认托盘，打开/刷新/退出正常，Explorer 重启恢复；退出清理自己的子进程。
- [x] 自动测试不读真实用户 auth/session；真实只读联调已单独记录授权和版本。
- [x] ZIP 解压运行不依赖机器预装 .NET/WinAppSDK 的自动审计；真实安装的 Codex 原生程序发现和全新 Windows 人工验收仍待外部环境。

Phase 1 不加入 cloud，不实现其他 Phase 的假按钮。

## 后续阶段

| Phase | 实现内容 | 必须达到的阶段出口 |
| --- | --- | --- |
| 2 | 增量 Activity Reader | 已实现 today/current session/token delta；完整行游标、半行隐私、短文件增长、重复累计值、截断恢复、状态迁移、历史/新会话及跨日逻辑已覆盖；真实 session 文件长期运行仍需人工验证 |
| 3 | Worker + QuotaGroup + WebSocket | 2/4 模拟客户端同步；DO 持久化、休眠恢复；先内部测试，认证完成前不公开部署 |
| 4 | 配对、Owner/Member、DPAPI、HMAC | 服务端与 Windows 本地接入完成；保持本地入口关闭 |
| 5 | 跨设备 dashboard | 已完成状态模型与缓存版本修复；GUI dashboard 仍需完整展示 ledger/unattributed |
| 6 | 服务端活动区间归因 | 已完成 2% 单台、80:20、无活动和服务端接收时间窗口测试 |
| 7 | 自动 epoch | 已完成 83/T1→1/T2 确认后清零与异常不清零 |
| 8 | 通知 | 已完成 eventId 去重、Relay snapshot 广播和 Windows 托盘通知 |
| 9 | Soft Enforcement | Relay 限额权威状态、客户端 fail-safe gate 和本应用受控启动入口已完成；外部 Desktop/CLI 进程不拦截 |
| 10 | Enhanced Enforcement，可选 | 已完成安全边界评估；只有用户显式启用并 UAC 后，才可评估仅管理自己的防火墙规则；不阻塞 MVP |
| 11 | 发布 | CI build/test、ZIP/SHA256、许可、Privacy、合成 GUI smoke 证据已完成；干净 Windows 人工验证、GitHub Release 和公共 Relay 仍待外部条件 |

每个阶段先看现有代码，只运行与变化相关的验证；记录真实测试结果。没有运行的测试不标为通过。

## v0.1 完整场景

4 台：MAIN-PC Unlimited、LAB-PC 30%、LAPTOP 30%、OFFICE-PC 20%。

1. 四台显示同一账号官方额度，能看到所有设备。
2. MAIN-PC 关闭后其余设备继续观察、活动同步、限额通知与重置。
3. LAB-PC 活动导致估算增长，达到 30% 触发所有在线设备通知及本地限制。
4. LAB-PC 重启仍受限；Relay 临时不可用不清零，恢复后先收完整 snapshot。
5. 官方 reset 经确认后新 epoch 清零并解除限制，离线设备重连也更新。
6. Member 修改 policy/ledger 被拒绝；异常 reset 观察不立即生效。
7. 本地 cache 改成 usage=0，重连恢复服务器值，包括服务器 version 未变化的情况。
8. 传输白名单测试证明 DTO 中没有 auth、cookie、prompt、response、source、文件路径/内容；未知字段被拒绝。

## 发布前未决事项

- 公共 Relay 的运营账户、真实域名和运维额度；GitHub 发布仓库地址。
- 各支持 Codex 版本的账号身份字段、可执行发现和额度事件覆盖度。
- 所选 .NET / WinUI 的完整构建与 unpackaged Toast 可用性。
- 对实际 Desktop/CLI 的最小侵入限制方式及在执行任务时的用户体验。

上述事项不改变既定 Client → Worker → Durable Object 架构；技术上无法满足的强保证须如实记录，不能靠 UI 文案掩盖。
