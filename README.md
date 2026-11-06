# Windows服务器远程监控

## 项目定位
一个面向 Windows 11 / Windows Server 的本机监控工具。

能力范围：
- 硬件实时监控：CPU、内存、磁盘温度、磁盘空间、开机时长
- 网络流量统计：WAN / LAN、上传 / 下载、按时间区间汇总、Top N 应用排行
- Web 页面查看：前端页面 + SignalR 实时刷新
- 本机部署：可发布为 Windows 服务并开机自启

## 技术架构
### 后端
- `Monitor.Service`：宿主进程，负责启动 Web、采集任务、聚合任务、清理任务
- `Monitor.WebApi`：HTTP API、SignalR Hub、异常处理中间件
- `Monitor.Hardware`：硬件采集（LibreHardwareMonitor）
- `Monitor.Network`：网络采集与地址分类（含 ETW 采集）
- `Monitor.Storage`：SQLite 持久化、设置读写、历史聚合查询
- `Monitor.Contracts`：DTO、配置项、共享契约

### 前端
- `Monitor.Frontend`：Vue 3 + TypeScript + Vite
- 页面分为：首页、网络、设置
- 实时数据通过 SignalR 推送，历史统计通过 HTTP 查询

## 运行逻辑
1. `Monitor.Service` 启动
2. 读取 `appsettings.json` 和同级 `monitor.db` 中的持久化设置
3. 初始化 SQLite 数据库结构
4. 启动硬件采集、网络采集、聚合、清理后台任务
5. 暴露 Web API 与 SignalR Hub
6. 前端页面通过 API + SignalR 展示实时与历史数据

## 数据与日志
- 配置文件：`appsettings.json`（与 exe 同级）
- 数据库：`monitor.db`（与 exe 同级）
- 日志目录：`logs\`
- 日志策略：仅 Warning / Error，单文件 10MB，自动滚动

## 界面与开发
- 界面采用精密仪表台设计：中性灰白／石墨底色，数据曲线和状态作为重点，支持浅色、深色和跟随系统。手机端保留完整操作，硬件型号可展开查看。
- 样式分层与设计约束见 [`src/Monitor.Frontend/UI-DESIGN.md`](src/Monitor.Frontend/UI-DESIGN.md)。本次设计方法参考 Damir Sokolovsky 的 [unslop](https://github.com/heydamir/unslop)（CC BY 4.0）。
- 网络页保留起止时间、全部/WAN/LAN/Loopback、上传/下载/总量、Top N、七种时间预设，以及忽略、撤销、恢复和分段明细。
- `npm run dev`（在 `src/Monitor.Frontend` 下）默认将 `/api`、`/hubs` 和 `/health` 同源代理到 `127.0.0.1:5188`。显式设置 `VITE_API_BASE_URL` 才改用其他 API 地址；生产默认同源。

## 历史查询与运行边界
- 历史接口使用 UTC 半开区间 `[from, to)`；单次最多查询 31 天，长期保留的数据需要分段查询，保留时间并未被缩短。
- 硬件历史最多返回 2,000 点；分钟级候选摘要保留时间边界及关键指标极值，并由后台分批回填。尚未回填的区域仍读取原始样本，不应把它当作恒定成本查询。
- 正常旧版本采集的 UTC 数据可继续使用。如果曾由自定义工具写入带非零时区偏移的历史文本，需要先通过单独维护规范化；不会在启动时隐式全表改写这类数据。
- 网络事件队列默认最多 65,536 项，活动及待持久化桶合计最多 100,000 项。超出预算会显式拒绝和计数，避免持续耗尽内存；故障期间不能保证统计完整。
- `/health` 区分当前可用性和本次服务运行以来的统计完整性。`network.statisticsComplete=false` 表示发生过采集故障、ETW 丢失、聚合拒绝或处理失败；恢复运行不会抹掉已有缺口。重启后内存诊断重新计数，并不证明更早历史完整。
- 正常停止会追平已接受事件并封口当前桶；内存缓冲没有磁盘消息日志，不承诺进程崩溃、长期磁盘故障或停机超时下零丢失。
- HTTP 默认启用 Brotli/gzip；带内容哈希的静态资产可长期缓存，HTML 重新验证，API/健康状态不缓存。HTTPS 压缩默认不启用，需要部署者结合认证与响应内容评估后配置反向代理。
- 历史清理释放的 SQLite 页可复用，但主数据库文件不会自动缩小。需要归还文件空间时使用显式维护流程，见 `scripts/README.md`；不要在每轮清理后自动 `VACUUM`。

## 验证
- 后端构建：`dotnet build win11-system-monitor.sln`
- 后端测试：`dotnet test tests/Monitor.Tests/Monitor.Tests.csproj`（需要 .NET 8 / ASP.NET Core 8 运行时）。测试使用模拟采集器和临时数据库，不启动生产服务。
- 前端：在 `src/Monitor.Frontend` 下运行 `npm run typecheck`、`npm test`、`npm run build`。
- 部署回滚测试：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-deployment.ps1`
- 临时 SQLite 维护测试：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-vacuum.ps1`
- 真实 ETW、管理员权限和高流量持续运行应在独立 Windows 验证环境进一步测试，不能仅凭模拟测试宣称完成生产压测。

## 部署
发布、安装与卸载见：
- `scripts/README.md`
- `scripts/publish-service.ps1`
- `scripts/install-service.ps1`
- `scripts/uninstall-service.ps1`
