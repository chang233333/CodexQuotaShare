# Windows client

WinUI 3 / .NET 10 self-contained win-x64。Core 包含官方额度解析、Activity 统计、HMAC、RelayClient、持久待发汇总、快照恢复和限额判断；Windows 项目提供托盘、Toast、DPAPI、设置、启动项、设备管理和本机进程限制。

本轮完整状态见 [任务审计](../docs/task-audit-20260921.md)，用户使用见 [说明](../docs/phase1-user-guide.md)。公共 Relay 和 GitHub Release 尚未上线。可用 --demo 或 smoke-test 验证合成数据；这些模式不读取真实账号、不执行进程限制、不修改启动项。
