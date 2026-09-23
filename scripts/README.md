# 隔离开发与打包

默认 SDK：E:/codexdefault/CodexQuotaShare/toolchains/dotnet。global.json 固定版本；SDK、NuGet 缓存和临时目录只通过子进程环境设置使用，不修改全局 PATH 或共享环境。

~~~powershell
.\scripts\dev.ps1 -Action Test
.\scripts\dev.ps1 -Action Build
.\scripts\dev.ps1 -Action Publish
.\scripts\dev.ps1 -Action Smoke
.\scripts\audit-release.ps1 -ArchivePath E:\codexdefault\CodexQuotaShare\artifacts\phase11\CodexQuotaShare-win-x64.zip -ExtractionDirectory E:\codexdefault\CodexQuotaShare\artifacts\phase11-clean-extract
~~~

可用 -TaskDirectory 和 -SdkDirectory 指定其他开发位置，-ArtifactDirectory 可独立指定打包输出目录。Publish 生成指定目录下的 self-contained win-x64 ZIP、SHA256SUMS.txt 和 publish/，并把 README、PRIVACY、项目许可证、第三方声明和依赖许可证放入包内。Smoke 打开短暂的演示窗口并自动退出，读取 smoke 输出目录下的合成会话，验证官方额度、Activity、Relay 合成状态、两次刷新和退出；不读取真实账号或会话。

打包包含 WinUI PRI/XBF、应用运行时、项目与依赖许可证，拒绝打包含 data/ 的输出目录。脚本不部署 Relay、不发布 GitHub Release。

## Phase 3 Relay

relay.ps1 提供 Install / Typecheck / Test / Build / Dev。依赖安装使用 npm ci；Build 为 dry-run；Dev 仅 loopback。缓存、日志和产物使用任务目录，可用 -TaskDirectory 覆盖。详见 [Relay 开发说明](../relay/README.md)。

`continue-work.ps1` 是本地 Windows 续作启动器：它打开项目、`task.md`、实施计划和最新交接文件，并写入任务目录日志；它不会伪造 ChatGPT 后台会话。当前机器的 `CodexQuotaShare-ContinueWork` 计划任务由 Windows Task Scheduler 以当前交互用户运行，首个计划边界后每 `PT5H2M` 重复。若迁移机器，应重新注册为当前登录用户的 Interactive 任务并验证 `E:\codexdefault\CodexQuotaShare\logs\continue-work.log`。

`audit-release.ps1` 在一个必须不存在的新目录中解压 ZIP，核对 SHA256、必需文件和 `data/` 污染，然后直接运行发布版 GUI smoke。它验证便携包可运行，不等同于全新 Windows 系统上的人工验收。
