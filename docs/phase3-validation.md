# Phase 3 验证记录

验证日期：2026-09-19。验证范围是本地合成数据；P3 Relay 尚未接收真实账号额度、凭据或生产流量。

## 结论

P3 的本地出口已通过：Worker 路由到 SQLite-backed Durable Object，一个 group 对应一个 DO；WebSocket 使用 Hibernation API；2 台和 4 台模拟客户端都能收到一致的完整快照；Owner 离线不阻断 Member 活动同步；DO 休眠驱逐和整个 Miniflare runtime 重启后，成员、活动、sequence 高水位和限流状态都能从持久化存储恢复。

生产入口仍保持关闭：`LOCAL_DEV=false`、workers.dev/preview URL 关闭，未实现认证、配对、HMAC、额度观察、归因、限额、通知和公开部署。这些属于 P4 及后续阶段，当前测试端点不能连接真实客户端或公共网络。

## 自动验证

在项目 `relay/` 目录执行：

~~~powershell
npm.cmd run typecheck
npm.cmd test
npm.cmd run build -- --outdir E:\codexdefault\CodexQuotaShare\artifacts\phase3\relay
~~~

结果：

- TypeScript 类型检查通过。
- 2 个测试文件、39 项测试通过：协议白名单 25 项，Durable Object/Worker 集成 14 项。
- Wrangler `deploy --dry-run` 通过；本次没有执行生产部署。

## 覆盖的关键场景

- 创建 Owner、加入 Member，2 台客户端实时收到同一快照。
- 4 台客户端同步，Owner 断开后其余 3 台继续活动上报和广播。
- 第 5 台加入被拒绝；并发加入仍保持最多 4 台。
- 同设备新连接替代旧连接，旧连接关闭不会把新连接标记为离线。
- sequence 重放、活动区间重叠、未知消息、额外字段、二进制帧和超大帧被拒绝。
- 每台设备活动上报服务端限流为不超过 1 次/5 秒；限流时间写入 DO 状态，runtime 重启后仍生效。
- 12 秒无业务事件后触发 workerd/Miniflare DO 驱逐，Hibernation socket 恢复后仍能读取并更新持久状态。
- 完整 runtime 重启后成员、最近活动、sequence 高水位、离线状态和活动限流状态保留。
- `REQUEST_SNAPSHOT` 支持同版本完整快照修复；HEARTBEAT 在无变化时只返回 ACK，不递增版本。
- body/消息采用 16 KiB 上限；协议和公开 snapshot 使用显式白名单，不序列化 session 原文、本机路径、凭据、prompt、response、内部 schema 或 sequence。
- 非 loopback、浏览器 Origin、缺少显式本地测试头和 `LOCAL_DEV=false` 的请求均被拒绝。

## 仍需后续验证

- 干净 Windows 环境中的真实 Cloudflare 部署、SQLite Durable Object 生产迁移和多实例网络验证。
- P4 设备配对、邀请过期/轮换、DPAPI、HMAC、权限和跨语言 golden vectors。
- 真实客户端 RelayClient 的重连、完整 snapshot 恢复和断网 fail-safe。
- 后续阶段的官方额度观察、归因、周期重置、通知与本地限制。
