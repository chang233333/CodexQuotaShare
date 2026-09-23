# CodexQuotaShare

Share and limit Codex weekly quota across your own computers.

面向同一 ChatGPT Plus / Pro 账号的 2–4 台 Windows 电脑：官方账号周额度、设备估算用量、独立限额、跨设备通知、自动周重置。Windows Client → Cloudflare Worker → Durable Object，Owner 无须保持在线。自包含 win-x64 ZIP，无需用户安装 .NET、Python、Node 或数据库。

**状态：本地候选版本，尚未上线。** 2026-09-21 对照 task.md 复核并修复了生产 Relay 被禁用、真实 WebSocket 地址错误、归因漏算、单观察错误重置、客户端管理入口缺失等问题。详见 [本轮审计与验收](docs/task-audit-20260921.md)。公共 Relay 和目标 GitHub 仓库尚未准备，因此没有可供普通用户直接下载并配对的正式 Release；不能将本地测试通过等同于完成全部任务。

## 使用

正式包配置公共同步地址后：下载 ZIP → 完整解压 → 运行 CodexQuotaShare.exe → 托盘打开 → 创建/加入群组 → Owner 选择设备设置 1–100% 或 Unlimited。邀请码有效 15 分钟，可重新生成。Member 可以查看和离开群组；Owner 可以重命名、移除设备和转让所有权。

当前候选包未预设公共地址，开发者可使用高级连接设置连接本地测试或自己的 HTTPS Relay。[使用说明](docs/phase1-user-guide.md) · [Deploy to Cloudflare](relay/README.md) · [隐私](docs/privacy.md)。GitHub 下载链接需在确定目标仓库后填写，未使用虚构链接。

## 指标和限制

- **Official Account Usage** 来自 OpenAI 的 10080 分钟周窗口，不假设固定 token 或消息额度。
- **Estimated Device Usage** 由账号额度增量和本地 Activity 权重估算，token 不是额度。无有效信号时归入 Other / Unknown。
- 相同额度观察不会清空待归因信号；信号最多保留 5 分钟。计量、会话写入和网络延迟仍可能导致误差，30% 是近似管理，不能保证精确到 30.000%。首次加入前的用量不追溯分配。
- 新 resetAt 配合明显下降需另一设备佐证；只有一台在线时，需服务端确认原重置时间已到，并在至少 60 秒后再次观察。低用量周期同样可恢复。不因本机日期或断网清零。
- Soft enforcement 默认开启：达到限额后阻止应用内启动，并关闭同一 Windows 会话内、路径精确匹配的已识别 Codex Desktop / CLI；当前任务可能中断。软件自身额度观察进程豁免。未安装防火墙规则，管理员或停用本工具者可以绕过。其他安装布局可能需要手动指定 CLI；真实版本兼容性仍需实机验证。

## 隐私

仅上传设备标识/名称、额度百分比、重置时间、活动汇总、时间戳及签名元数据。OpenAI 凭据、Cookie、auth.json、提示词、响应、源码、会话原文和文件内容不上传。设备秘密使用 Windows DPAPI；本地队列不包含会话内容。HMAC 证明配对设备身份，不能独立证明客户端声称的 OpenAI 额度真实。

## 验证与构建

~~~powershell
.\scripts\dev.ps1 -Action Test
.\scripts\relay.ps1 -Action Typecheck
.\scripts\relay.ps1 -Action Test
.\scripts\integration.ps1
.\scripts\dev.ps1 -Action Publish -ArtifactDirectory E:\codexdefault\CodexQuotaShare\release
.\scripts\dev.ps1 -Action Smoke -ArtifactDirectory E:\codexdefault\CodexQuotaShare\release
~~~

工具和缓存放在任务专用目录。integration 使用真正的 C# RelayClient 和本机 workerd，数据均为合成数据，不读取真实账号。CI 包含客户端、Relay、跨语言验收和 ZIP/SHA256；tag 流程在全部通过且设置 DEFAULT_RELAY_URL 后创建 GitHub Release 草稿。普通用户不需要构建这些组件。

## 结构

~~~text
client/       Core + WinUI 3 Windows 客户端
relay/        Cloudflare Worker、Durable Object 和测试
protocol/     协议说明
tests/        Core、Activity、C# 跨语言验收
scripts/      测试、构建、打包、发布审计
.github/      CI 和 Release workflow
docs/         调研、架构、隐私、验收与截图
licenses/     第三方许可原文
~~~

[Phase 0 调研](docs/research.md) · [第三方声明](THIRD_PARTY_NOTICES.md) · [MIT License](LICENSE)

This project is not affiliated with, endorsed by, or sponsored by OpenAI.
