# Phase 2 验证记录

日期：2026-09-19。范围：本机 Codex Activity 增量读取、检阅发现问题的修复及便携交付；不包含 Relay、设备归因账本或本地限制。

## 检阅问题与修复

1. **刷新与退出死锁**：RefreshAsync 成功取得信号量后在 finally 中释放；目录缺失、IO 异常、取消和正常完成均覆盖，去除重复错误事件。
2. **半行原文落盘**：游标只提交完整换行后的字节位置。半行留在源文件中重读，不再序列化 PendingBase64。版本 1 状态在载入时立即迁移，重写状态和遗留临时文件，保留数字统计；即使会话目录不存在也执行迁移。
3. **短文件追加误判重建**：保存 PrefixLength，在相同长度上比较前缀；校验与读取使用同一个文件句柄。真实缩短或前缀变化才重建 baseline。
4. **新会话首批漏计**：持久化 TrackingSince，以事件时间区分监控前历史与监控后活动。初次历史扫描只建立 baseline；新会话首批、已有会话追加以及关闭期间新建会话的活动都能计入。晚发现的旧文件仍先建立历史 baseline。

读取器只使用具有有效时间戳的 token_count 事件，避免将未知时间的历史数据算作当前活动。超长损坏行整行忽略，不将尾部误当成独立 JSON。状态中的损坏或 null 条目可恢复。UI 隐私文案已更新，明确扫描本机会话文件，不再称“不读取会话文件”。

## 本地状态与统计语义

- 默认每 20 秒扫描本机 %USERPROFILE%\.codex\sessions 下的 JSONL。读取内容仅在内存中分行并提取 token 计数/时间戳，不持久化 prompt、response 或会话原文。
- data/activity-state.json 原子保存版本、监控起点、会话路径、完整行游标、前缀 hash 及其长度、每日计数和会话累计值。没有 Relay 通信。
- 旧版升级保留已存的每日计数，但无法自动补回旧版漏计的用量；历史重建不会重复计入今日。写入及迁移要求解压目录可写。
- 当前会话指最近具有活动时间的会话，活跃数采用最近 30 分钟窗口；今日计数按本地日期切换。Activity 是额度归因信号，不是官方设备额度账单。

## 实际自动验证

- **60/60 测试通过**：36 项额度/模拟 App Server 测试，7 项 Activity Core 测试，17 项直接链接生产读取器的文件集成测试。
- 读取器覆盖连续/并发刷新、后台定时更新、取消与退出、目录缺失、IO 错误、短文件追加、空文件增长、重启去重、新会话首批、导入历史、离线新会话、UTF-8/CRLF 半行、prompt 不落盘、截断和等长替换、旧状态迁移、损坏状态、缺失时间戳/超长行及跨本地日期重启。测试仅使用任务临时目录中的合成会话。
- **Windows Release：0 warning / 0 error**。
- **self-contained win-x64 发布成功**；ZIP 检查验证运行文件、PRI/XBF、README 和许可证存在，不含 data/sessions/activity-state，并核对归档内 DLL/PRI/XBF 与实际执行 smoke 的版本一致。SHA256 文件校验通过。
- **扩展 GUI smoke 通过，进程退出码 0**：{"demo":true,"tray":true,"quotaRendered":true,"activityReader":true,"activityRendered":true,"reopen":true,"passed":true}。除托盘、官方额度与窗口重新打开外，实际生产读取器对合成文件连续刷新，确认累计 100→120 得到 delta/today=20、current=120，并验证 Activity 文本和 App 的读取器退出清理。

产物：E:/codexdefault/CodexQuotaShare/artifacts/phase2/CodexQuotaShare-win-x64.zip

SHA256：

~~~text
065273058072d301702a4b3cd278140929defb4cac3ecb59f1ae5af46bacf27d  CodexQuotaShare-win-x64.zip
~~~

本次日志：E:/codexdefault/CodexQuotaShare/diagnostics/phase2-fix/ 下 regression.log、windows-build.log、publish.log、gui-smoke.log、archive-check.log。脚本默认产物切换到 artifacts/phase2，可用 -ArtifactDirectory 指定位置；CI 配置同步更新，未宣称已在远端运行。

## 验证边界

- 本轮未读取真实用户会话或重新查询真实额度，未进行真实 session 长期运行或干净 Windows 虚拟机验证。
- 前缀校验最多 4096 字节；若文件改写仅发生在此前已读区域的更后方，同时保留相同前缀且长度不缩短，当前机制不保证识别。
- 累计 token 下降仍沿用现有“重置 baseline”语义；不同 Codex 版本下的实际下降/重置语义仍需单独核验。
- 历史基线处理依赖源事件时间；没有可靠时间戳的事件跳过，系统时钟大幅变化未宣称覆盖。
- Relay、跨设备归因、通知和本地限制尚未实现。
