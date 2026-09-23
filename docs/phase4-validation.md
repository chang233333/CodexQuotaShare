# Phase 4 验证记录

验证日期：2026-09-19。范围为本地合成 Relay、跨语言 HMAC 向量和 Windows DPAPI 存储；没有连接公共 Relay、真实账号或真实凭据。

## 已完成

- Worker 创建组时生成 owner device secret 和 15 分钟 joinCode；加入时生成独立 member device secret。
- joinCode 可由 Owner 轮换，旧 code 立即失效；组仍限制最多 4 台设备。
- HTTP GET、Owner 邀请轮换、设备重命名/移除均要求 HMAC-SHA256。
- WebSocket upgrade 和每条 WebSocket 业务消息都要求 HMAC；签名覆盖方法/路径、时间戳、nonce、认证 sequence 和 canonical JSON payload。
- 服务端检查 5 分钟时钟窗口、单调认证 sequence、nonce 历史和设备身份匹配；设备秘密、joinCode 和认证状态不进入公开 snapshot。
- Owner-only mutation 已验证；Member 不能轮换邀请、转移 Owner、重命名或移除设备。
- C# `RelayAuth` 与 TypeScript 使用同一 golden vector；Windows `DeviceSecretStore` 使用 DPAPI CurrentUser，普通 JSON 只保存加密后的 secret bytes。

## 验证命令与结果

Relay：

~~~powershell
cd relay
npm.cmd run typecheck
npm.cmd test
npm.cmd run build -- --outdir E:\codexdefault\CodexQuotaShare\artifacts\phase4\relay
~~~

结果：TypeScript 类型检查通过；2 个测试文件、43 项测试通过；Wrangler dry-run 构建通过。没有生产部署。

Windows/Core：

~~~powershell
scripts\dev.ps1 -Action Test
scripts\dev.ps1 -Action Build
~~~

结果：62 项 Core/Activity/RelayAuth/DPAPI 测试通过；Windows Release 构建 0 warning / 0 error。

关键覆盖包括：无认证拒绝、签名篡改拒绝、重复 nonce/认证 sequence 拒绝、WebSocket 独立消息签名、旧邀请失效、Owner/Member 越权、设备移除、HMAC golden vector、DPAPI 文件不含明文 secret。

## 边界与未完成项

当前仍是本地测试入口：`LOCAL_DEV=false`、workers.dev/preview URL 关闭。官方额度上传、归因、周期、通知和限制属于后续 Phase 5–9。schemaVersion=2 仍是开发存储格式，不是生产迁移承诺。

## P4 Windows 接入补充（2026-09-19）

- Core 增加 Relay DTO、canonical JSON、快照缓存和 `RelayClient`；WebSocket 只有一个发送循环，活动消息在断线期间保留在有界队列中。
- RelayClient 认证 sequence、activity sequence 和连续 activity window end 写入独立的非秘密状态文件；DPAPI 仍只保存 device secret，群组 id 单独保存。
- 重连间隔为 1/2/5/10/30/60 秒；重连后先发送完整 snapshot 请求。离线时保留最后权威快照，不解除任何限制。
- Windows UI 增加 Relay 地址、创建群组、加入群组、邀请码状态和设备快照；配对响应中的 device secret 不进入普通设置或日志。
- Windows Release 构建：0 warning / 0 error；Core/Activity/RelayAuth/DPAPI/快照缓存测试：63/63；Relay TypeScript：43/43；Wrangler dry-run 通过。

仍未完成：Phase 5 官方账号额度与估算设备用量分离的跨设备 dashboard，以及真实 Relay 公共部署。当前不会连接真实账号或公共 Relay。
