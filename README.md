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

## 部署
发布、安装与卸载见：
- `scripts/README.md`
- `scripts/publish-service.ps1`
- `scripts/install-service.ps1`
- `scripts/uninstall-service.ps1`
