# Phase 5 验证记录

验证日期：2026-09-19。当前完成 Phase 5 的状态模型第一步，仍未接入服务端额度归因或设备限额。

## 已完成

- `RelayDashboardState` 明确分离 `OfficialAccountUsage`（来自 OpenAI quota reader）与 `DeviceActivity`（来自 Relay activity signal）。设备 activity 不提供伪造的官方百分比。
- 设备 activity 仅投影 `tokenDelta`、`activeSessionCount`、`lastSeen` 和来源标记；不包含 prompt、response、Cookie、source 或文件路径。
- 快照版本严格拒绝旧版本；重连收到的完整 authoritative snapshot 允许覆盖同版本缓存，用于修复本地缓存被篡改的情况。
- UI 明确显示“设备 Activity 仅是归因信号，不是 OpenAI 官方额度”。

## 验证命令与结果

~~~powershell
dotnet run --project tests/CodexQuotaShare.Core.Tests/CodexQuotaShare.Core.Tests.csproj -c Release
dotnet build client/CodexQuotaShare.Windows/CodexQuotaShare.Windows.csproj -c Release
~~~

使用项目隔离 .NET 10 SDK 执行：Core/Activity/RelayAuth/DPAPI/Relay cache/dashboard 共 64/64；Windows Release 0 warning / 0 error。

## 下一步

实现服务端 `official quota observation` 与 `estimated device usage` 的权威快照字段及归因前的完整 dashboard；继续保持 `LOCAL_DEV=false`，不连接公共 Relay 或真实账号。
