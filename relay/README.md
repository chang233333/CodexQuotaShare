# Cloud Relay

Cloudflare Worker + SQLite Durable Object + WebSocket Hibernation。一个设备组一个 DO，默认最多 4 台，Owner 只代表管理角色。

## Deploy to Cloudflare

由项目维护者部署一次，普通用户无需部署服务或数据库。需要自己的 Cloudflare 账号和 Workers/SQLite Durable Objects 可用权限：

~~~sh
cd relay
npm install
npx wrangler login
npx wrangler deploy
~~~

wrangler.jsonc 使用 LOCAL_DEV=false、HTTPS 和 workers.dev，配置配对限流绑定 PAIRING_LIMITER；每个 IP 每分钟最多 10 次创建/加入请求。没有 PostgreSQL、Redis、Docker 等依赖。部署后将实际 HTTPS 地址填入 GitHub repository variable DEFAULT_RELAY_URL。不要填写示例域名后当作可用服务。

生产客户端必须使用 HTTPS origin；拒绝浏览器 Origin。设备请求使用 HMAC-SHA256、时间戳、nonce、持久序列号；Owner 权限在实际事务内再次检查。仅 HTTPS 传输设备秘密，快照/错误/日志不包含秘密。生产入口不会暴露测试 inspect 路由。

## 本地开发

~~~powershell
.\scripts\relay.ps1 -Action Install
.\scripts\relay.ps1 -Action Typecheck
.\scripts\relay.ps1 -Action Test
.\scripts\relay.ps1 -Action Build
.\scripts\relay.ps1 -Action Dev
~~~

这些命令在项目根目录执行。Dev 仅接受 loopback、X-CQS-Local-Test: 1 和无 Origin 请求；客户端连接 localhost 时自动附加该头。Build 只 dry-run，不部署。node_modules 属于此项目，缓存和日志在任务目录。

## 统计与恢复

服务端保存当前周期、权威设备账本、最多 5 分钟的待归因计数和最多 16 条必要通知，不建立分钟历史数据库。重复额度观察不丢弃计数，归因后消费计数一次。无信号计入 Other / Unknown。移除设备将其已分配用量转入 Unknown，保持当前账本总量。

重置需 resetAt 增长与下降信号，经两台设备确认；单在线设备还须已到原重置时间并持续确认至少 60 秒。Owner 离线不阻塞重置。缓存仅做显示，连接先接收权威完整快照。

活动更新最多每设备 5 秒一次；C# 客户端用持久化有界队列重试，REPORT_ACK 确认后清除。过时超过 5 分钟的待发报告丢弃，避免将陈旧活动强行分配给当前消耗。重复序列不重复记账。客户端每 60 秒心跳，支持有界退避重连和静默连接超时。

本机测试不能替代实际 Cloudflare 部署、配额/费用监测和干净 Windows 验收。免费层目标按低频设计，未声称实际生产成本已验证。
