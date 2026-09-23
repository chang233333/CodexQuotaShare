# Phase 6–9 验证记录

验证日期：2026-09-19。所有数据均为 Relay 合成数据；`LOCAL_DEV=false` 的生产入口保持关闭。

## 已完成

- Phase 6：服务端保存官方账号额度观察；DeltaQ 由单一活动设备高置信度归因，多设备按 token activity weight 分配，无活动进入 `unattributedUsage`。
- Phase 7：仅在 `resetAt` 改变且使用率明显下降（至少 5 个百分点）时创建新 `WeeklyEpoch`，清零设备账本并解除限额状态；单个异常下降不会 reset。
- Phase 8：`WEEKLY_RESET` 与 `DEVICE_LIMIT_REACHED` 带唯一 `eventId`，通过完整 snapshot 广播；客户端去重后使用 Windows 托盘通知。
- Phase 9：Owner 可设置 `1–100%` 或 `null`（Unlimited）设备限额；客户端通过 HMAC `PATCH /devices/{id}` 修改，服务端账本达到限额时设置 `LIMIT_REACHED` 并广播事件。Member 不能修改策略。

## 验证结果

~~~powershell
cd relay
npm.cmd run typecheck
npm.cmd test -- --run
npm.cmd run build -- --outdir E:\codexdefault\CodexQuotaShare\artifacts\phase6-9-relay

dotnet run --project tests/CodexQuotaShare.Core.Tests/CodexQuotaShare.Core.Tests.csproj -c Release
dotnet build client/CodexQuotaShare.Windows/CodexQuotaShare.Windows.csproj -c Release
~~~

结果：Relay 类型检查通过；28 项协议测试 + 23 项 Relay 场景测试通过；Wrangler dry-run 通过；Core/Activity/RelayAuth/DPAPI/dashboard 64/64；Windows Release 0 warning / 0 error；portable publish 与 GUI smoke 通过（托盘、窗口重开、Activity、退出均通过）。

## 明确未完成边界

`LIMIT_REACHED` 已成为服务端权威状态并在 UI/托盘显示，但当前客户端尚未安装 Codex CLI/Desktop 的系统级拦截器。Windows Administrator 可绕过任何用户态限制；不能把 Relay 状态冒充为已经阻断外部 Codex 新会话。
