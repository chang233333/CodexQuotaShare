# Phase 1 验证记录

日期：2026-09-18。范围：本地周额度 MVP；不包含云端或设备限制。

## 工具链

.NET SDK 10.0.401 Windows x64 ZIP 来自微软 release-metadata/10.0/releases.json；下载文件 SHA-512 与官方元数据一致。SDK 解压在任务专用 toolchains/dotnet 目录，global.json 固定此版本。未安装系统级 SDK，未修改全局 PATH、已有 .NET 或 Python 环境。NuGet/cache/temp 使用任务目录，项目 bin/obj 留在各项目下。

本次宿主缺失部分 Windows 环境变量，造成 NuGet ConfigurationDefaults 的 path1 异常以及 PowerShell 无法识别原生 exe；仅为构建进程补齐变量并隔离缓存后解决。

## 自动验证

- C# 核心及模拟协议测试：36/36 通过，包含额度边界、两种 weekly 位置、模型隔离、握手、推送、24 个并发请求、取消、超时、EOF、非法消息、退出清理和重连。
- 修复异步读响应晚于推送回调时可能覆盖较新额度的问题：在 stdout 接收时标记单调观测时间；有专门回归用例。
- WinUI Release 构建：0 warning / 0 error。
- self-contained win-x64 发布成功，附带 .NET 10.0.12 运行文件和 Windows App SDK 1.8。
- GUI 合成数据测试通过：tray=true、quotaRendered=true、reopen=true、passed=true，退出码 0。
- 发现并修复 dotnet publish 未带上应用 PRI/XBF 的问题；其症状为首次布局时 InfoBar 找不到 generic.xaml 并退出。项目现有显式资源发布目标，打包脚本检查资源存在。

## 真实 Codex 联调

用户明确授权检查本机 OpenAI/Codex/bin 并执行只读额度查询。测试使用 codex-cli 0.155.0-alpha.9，调用 initialize、initialized 和两次 account/rateLimits/read，成功识别 Plus 账号 weekly window。

不读取 auth.json 或 session 文件；由 Codex 自身读取既有登录。日志只保存 connected/source/planType/weeklyWindowRead/repeatRead 等验证标志，不保存账号标识、凭据、具体额度或原始协议。只销毁本工具创建的 App Server 子进程，不影响已有 Codex。

## 可重现命令

~~~powershell
.\scripts\dev.ps1 -Action Test
.\scripts\dev.ps1 -Action Build
.\scripts\dev.ps1 -Action Publish
.\scripts\dev.ps1 -Action Smoke
~~~

## 尚未宣称完成的验证

- 干净 Windows 虚拟机上的依赖验证；本机验证不代表所有 Windows 安装都兼容。
- Explorer 实际重启后的托盘恢复、完整人工点击流程与空闲 CPU/内存长时间测量。恢复代码已复用，但未主动重启用户 Explorer。
- 在用户实际产生 Codex 消耗时跨进程的额度推送覆盖度；模拟推送测试已通过，真实读取已通过，并保留 60 秒 fallback。
- Pro 真实账号、不同 Codex 版本与其他安装布局；当前仅联调上述 Plus/版本。
- GitHub Actions 配置已写入，尚未在线运行；未部署公共 Relay 或发布 Release。

本阶段仅显示官方账号额度，不提供设备级估算或执行本地限制。

## 最终 ZIP 验证

最终 ZIP 解压后，EXE、应用 DLL、hostfxr.dll、PRI、XBF、README 和第三方声明的 SHA256 与发布目录一致。设置 DOTNET_ROOT 和 DOTNET_ROOT_X64 指向任务目录中的不存在路径后，解压副本的 GUI smoke 仍通过，退出码 0；说明此构建使用包内 .NET 运行文件。此结果不替代干净 Windows 虚拟机验收。

便携包 SHA256：

~~~text
cfa3af30a8baeed2048463c3d2781d343e685c460f8e679f85fb72d0f86b9e93  CodexQuotaShare-win-x64.zip
~~~

仓库文档的 22 个相对链接已检查，目标均存在。
