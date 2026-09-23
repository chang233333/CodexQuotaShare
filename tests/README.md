# Tests

无第三方测试框架依赖的 C# 执行器，失败时退出码为 1。当前共 60 项：36 项额度/模拟 App Server 协议与生命周期、7 项 Activity Core、17 项生产 Activity Reader 集成测试。

~~~powershell
.\scripts\dev.ps1 -Action Test
~~~

测试项目直接链接生产 CodexActivityReader.cs，不引用 WinUI。集成用例在任务隔离 TEMP 内创建合成 JSONL，覆盖连续/并发刷新、退出取消、短文件增长、半行与 UTF-8、重启去重、旧状态原文缓存迁移、历史/新会话、截断替换、损坏状态及跨日。每项等待有超时，完成后清理自身测试目录。

测试进程使用自身 --fake-server 模式；默认不读取用户 Codex 登录或会话。--probe 模式仅供已授权的真实只读联调，输出成功标志和订阅类型，不输出原始协议或具体额度。

GUI smoke 使用 --smoke-test 和合成数据，检查托盘、官方额度显示、生产读取器连续刷新后的 Activity 显示、窗口重新打开和进程退出。合成文件仅写入显式 smoke 输出路径旁的 .activity/ 目录，不读取用户真实会话。详见[Phase 2 验证记录](../docs/phase2-validation.md)。Relay 和 2/4 客户端场景仍待后续阶段。
