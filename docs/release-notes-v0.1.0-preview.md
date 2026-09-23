# v0.1.0 candidate — 2026-09-21

本地候选版，未上线。此次审计修复了生产 Relay 入口、真实 C# WebSocket 握手、活动归因漏算、重置佐证、缓存版本修复、通知遗漏与设备管理缺口；增加默认可配置的本机软限制、Windows Toast、开机启动/通知设置、更新入口和完整 Release CI。

官方额度来自 OpenAI 的账号级周窗口；设备用量仍是估算值，不能宣称精确的设备账单。软限制可能中断已识别 Codex 的当前任务，不防管理员或主动停用。Enhanced enforcement 未实现，未安装防火墙规则。

正式发布依赖可用公共 Relay、目标 GitHub 仓库及干净 Windows 人工验收。当前候选包没有公共地址；详细证据与剩余项目见 task-audit-20260921.md。
