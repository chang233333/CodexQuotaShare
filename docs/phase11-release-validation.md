本文件为 2026-09-19 历史记录；当前验收状态见 [2026-09-21 审计](task-audit-20260921.md)。

# Phase 11 发布验证

更新：2026-09-19。

## 已验证

- Core 测试：65/65 passed。
- Windows Release：0 warnings / 0 errors。
- self-contained `win-x64` publish 成功。
- GUI smoke：托盘、官方额度、Activity、设备估算、`unattributed`、`LIMIT_REACHED`、Relay 通知、窗口重开和退出均通过。
- 便携包包含 `CodexQuotaShare.exe`、`README.md`、`PRIVACY.md`、`LICENSE`、`THIRD_PARTY_NOTICES.md` 和依赖许可证目录。
- 本机 Phase11 ZIP：`88,615,921` bytes；SHA256：`eb08b2ab4a86acde869065b3feae4445e625e5829b09c0b06ce3a0cde6d8695a`；ZIP 共 566 个条目，未发现 `data/` 条目。
- `scripts/audit-release.ps1` 已在全新目录 `E:\codexdefault\CodexQuotaShare\artifacts\phase11-clean-extract-20260919-02` 解压并运行；SHA256、5 个必需文件、启动前和 smoke 后均无 `data/`、发布版 smoke、Relay 合成展示和窗口重开全部通过。
- CI workflow 已配置为构建客户端、运行测试、生成 ZIP/SHA256，并上传 GitHub Actions artifact；Relay workflow 已配置为安装锁定依赖、typecheck、运行场景测试并生成 bundle。本轮在本机逐项复现了这些命令，GitHub 托管运行仍待提交后由外部服务执行。

本机验证产物目录：

`E:\codexdefault\CodexQuotaShare\artifacts\phase11`

候选发布说明见 [`release-notes-v0.1.0-preview.md`](release-notes-v0.1.0-preview.md)。

## 尚未声称完成

- 尚未在一台全新、无开发 SDK 的 Windows 环境完成 Explorer 重启后的人工交互验证；当前完成的是全新解压目录的自动审计。
- 尚未创建 GitHub Release 或公共 Relay。
- 未安装系统防火墙规则，也未实现外部 Codex Desktop/CLI 的强制拦截。

## 发布前人工清单

1. 在干净 Windows 解压 ZIP，确认不需要额外 .NET Runtime。
2. 启动后确认托盘、打开窗口、刷新和退出。
3. 使用合成 smoke 结果确认不读真实凭据；使用真实账号时只做用户授权的只读联调。
4. 复核 ZIP 内的 `PRIVACY.md`、许可证和 SHA256。
5. 记录 Windows 版本、Codex 版本、Relay 地址类型和是否发生人工绕过，不把估算设备用量宣传成官方账单。
