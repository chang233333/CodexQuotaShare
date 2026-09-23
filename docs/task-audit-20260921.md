# task.md 对照审计（2026-09-21）

## 结论

原实现没有完成 task.md。P13 的 65 个客户端测试、51 个 Relay 测试和演示截图，不足以证明产品可用：测试没有运行真正的 C# RelayClient → Worker 链路，且生产入口被主动禁用。此轮已修复主要实现缺口并补充完整链路验证，但由于用户确认尚未准备 GitHub 仓库与公共 Relay，最终“下载、运行、创建/加入即可使用”的上线验收仍不成立。

原 task.md 未修改；没有修改父目录、同级参考项目、真实账号/session、Python 环境、系统防火墙或计划任务。历史交接与阶段报告保留作历史记录，以本报告为当前状态。修改前源文件备份在本轮任务输出目录 original/。

## 主要发现与修复

| 原问题 | 修复 |
| --- | --- |
| LOCAL_DEV=false 时所有生产请求返回 RELAY_NOT_READY | HTTPS 生产入口、Cloudflare 配对限流绑定、部署文档及生产模式测试 |
| UriBuilder 把 ?deviceId 放入 Path，真实握手地址与签名不一致 | 正确分离 URI path/query，四个真实 C# 客户端与 Worker 验收 |
| 活动只保存最后一份；重复 quota 观察推进起点导致已发生消耗丢失 | 有界累计待归因信号；相同额度不清空，消费一次；80/20、零活动、重复观察回归 |
| 单台设备一份报告即可清零，低用量周期可能永不重置 | 两设备佐证；单在线设备须服务端已到旧 resetAt 并重复确认至少 60 秒；低用量案例覆盖 |
| 断网队列只在内存、可能堆积大量等待任务 | 有界持久化汇总队列、确认后清除、时间界限、重连完整快照和连接超时 |
| 本地缓存伪造更高 version 可阻止服务器修复 | 重连第一份权威快照覆盖不可信缓存；在线仍拒绝旧/同版本 |
| 同时多个设备达到限额只保留最后通知 | 服务端最多 16 条必要事件；客户端按 eventId 逐条去重消费 |
| 提高设备限额后本机仍锁定 | 权威账本低于新限额可解锁；缓存和离线本身不能解锁 |
| “启动 Codex”实际启动 app-server | 清除观察进程参数，启动正常 CLI |
| 软限制只限制一个按钮 | 增加同会话、精确可执行文件路径的关闭监测，豁免自身额度读取进程；配置开关与中断说明 |
| 仅托盘气泡，缺少 Windows Toast | 使用 Windows App SDK AppNotificationManager，并保留气泡 fallback；实际系统展示待人工验证 |
| 客户端缺少重命名、删除、转让 Owner、重新邀请、离开、缓存重载 | 接通已存在服务端 API、补上自助离开；按设备名称选择而非输入 UUID |
| 设备面板不显示限额、真实数据隐私页却声称不联网 | 展示设备估算/限额/最后在线、红色限制警告；更新隐私与协议文档 |
| 缺少开机启动、通知开关、软限制开关和更新入口 | 增加设置、当前用户启动项、GitHub Release 查询/打开、托盘暂停通知 |
| CI 只有 artifact；测试硬编码 E 盘 | 增加完整 Release workflow 和可注入默认服务/仓库，测试使用隔离 TEMP |
| 重启丢失未配对时的官方额度显示 | 保存本地额度缓存，启动时标为过时；保留旧版群组缓存迁移 |

## 对照任务范围

| task.md 章节 | 当前实现和边界 |
| --- | --- |
| 1–6、12、25、74–76、86、88 | Windows/Cloudflare DO 架构、2–4 台、原生轻量 UI、无主电脑服务器；正式公共服务和 Release 未上线 |
| 7–8、62–64、89 | 项目已有 research.md、参考 manifest、四份许可原文和 attribution；本次在项目内核对，不重新访问先前交接提及的同级目录 |
| 9–11、18、47、78–79 | 10080 分钟窗口解析、真实账号级值和设备估算分离、低频 activity；不由 quota 数值伪造账号哈希，同组同账号由使用者保证，未实现额外账号认证服务 |
| 13–17、31–32、40 | DPAPI 身份、HMAC、防 replay、Owner/Member API 和管理 UI；Owner 离组须先转让；Enhanced enforcement 为可选未实现 |
| 19–22、26–27、56 | DeltaQ + 活动权重、Unknown、置信度；延迟/取整导致估算误差，五分钟前信号不用于当前归因 |
| 23–24、57、60、69 | 多设备佐证/服务端时间确认重置，所有账本归零、通知和解锁，不要求 Owner 在线 |
| 28–30、39、44–46、58–59、80–81 | 完整快照、单调版本、HMAC、重连/超时、持久队列、离线保留限制；私有序列提交不会伪造公共版本变化 |
| 33–36、67–72 | 默认软限制、跨设备限额通知、红色警告；真实 Codex 各安装形态与实际 Toast 显示尚需人工验收，未用正在工作的 Codex 作终止测试 |
| 37–43、51 | 托盘、设置、隐私、固定码日志、更新入口；不开静默升级，不上传账号凭据和内容 |
| 48–50、52–53、73、77 | 可部署 Relay、自包含 ZIP/SHA256、CI 和 Release 草稿流程；维护者尚无实际公共地址和 GitHub 目标，README 如实标明候选版 |
| 54–61、65–68、87 | 自动测试和四个真实 C# 客户端验收见下；真实 Cloudflare 和真实四台 Windows 不等于本机 workerd 测试 |
| 82–85 | 只保留当前状态及有界汇总、nullable、取消支持、最小依赖；没有增加数据库或代理。空闲 CPU/内存目标尚未在干净系统测量 |

## 本轮自动验证

- Core / 假 App Server / Activity / DPAPI / HMAC / 缓存 / gate / 进程匹配决策：68/68。
- Relay：28 个协议测试 + 27 个场景测试 = 55/55。包括真实 workerd 休眠和 SQLite 重启、生产 HTTPS/限流、单在线 Member 的低用量周期重置。
- TypeScript typecheck 与 Wrangler dry-run bundle 通过；生产绑定包含 QuotaGroup、PAIRING_LIMITER、LOCAL_DEV=false。
- 四个真正的 C# RelayClient 连接本机 workerd：四台可见、账号额度同步、Owner 离线继续活动归因、LAB-PC 达到 30%、全部在线成员接收通知、重启/伪造更高版本缓存被修复、Member 修改限额被拒、Member 确认周重置/解锁、停止再连接获取完整快照，全部通过。
- Windows Release 构建与 self-contained publish 通过。最终 ZIP 为 88,635,880 bytes；SHA256 为 972931c6e815ce34f3627a436deca2b16a0393b67b1c715d7cc9c5f67c765d1e。
- 全新 clean-extract 解压审计通过：SHA256 一致、必需文件存在、启动前/后均无 data/，smokePassed / relayRendered / reopen 均为 true。
- GUI smoke 已验证托盘、额度、Activity、设备估算、限制告警、窗口重新打开；[实际控件截图](screenshots/dashboard-synthetic.png)明确标识合成数据。

测试过程中遇到的失败没有隐藏：首次并行客户端构建发生 DLL 文件锁，已改顺序；打包脚本替换时出现转义问题，已修复；PowerShell 对 Node 未等待导致空 LASTEXITCODE，已改 Start-Process -Wait -PassThru，C# 实际结果与宿主退出均确认成功。

## 第 87 节十项场景

| 场景 | 证据 |
| --- | --- |
| 1、2 四台账号额度/设备可见 | C# / Worker integration |
| 3 Owner 关闭后其他设备继续 | integration + Relay 四设备广播 |
| 4 活动增长并归因 | integration + 单活动/80:20 单元场景 |
| 5 达到 30%、所有在线设备通知 | integration 验证事件、gate；OS Toast 人工展示待验 |
| 6 LAB 重启不清零 | integration 重建实际 RelayClient 与磁盘缓存 |
| 7 重置归零并解除限制 | integration + 双设备/单设备低用量重置测试 |
| 8 网络断开恢复 | 客户端 Stop/Start 重连及 Relay runtime restart；物理断网人工验收待做 |
| 9 Member 篡改 limit/ledger 被拒 | integration 权限拒绝 + 协议字段白名单测试 |
| 10 usage=0 缓存篡改后恢复 | integration 同时篡改用量及 version=999999，再连接恢复服务器 30% |

## 剩余外部交付

1. 用户建立 GitHub 仓库后提交此项目，确认 CI 在线运行。
2. 维护者部署 Cloudflare Relay，设置 DEFAULT_RELAY_URL；正式包自动带入地址和仓库信息。
3. tag 流程产生 ZIP/SHA256 和 Release 草稿，完成新 Windows 上的托盘/实际 Toast、Codex 安装识别与限额关闭、网络恢复、资源用量人工验收，再公开 Release。

这三项没有完成，不能宣称 task.md 全部完成。此轮没有伪造部署地址、Release 链接、真实账号读取或人工验收结果。
