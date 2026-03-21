# 本机部署

## 部署目标
- 本机安装为 Windows 服务
- 开机自启动
- 局域网访问
- 配置文件与数据库文件放在可执行文件同级目录
- 日志写入 `logs` 目录，仅记录 Warning / Error，单文件 10MB 自动滚动

## 部署步骤

### 1. 发布程序
在仓库根目录执行：
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-service.ps1
```

说明：
- 该脚本会先执行前端 `npm run build`
- 再执行后端 `dotnet publish`
- 最后把前端构建产物复制到发布目录的 `wwwroot\`
- 因此前端**不需要单独发布**

默认输出目录：
- `artifacts\publish\win-x64`

### 2. 安装服务
进入发布目录后，以管理员 PowerShell 执行：
```powershell
powershell -ExecutionPolicy Bypass -File .\install-service.ps1
```

服务信息：
- 服务名：`RemoteServerMonitor`
- 显示名：`Windows服务器远程监控`
- 启动类型：自动

### 3. 验证运行
服务启动后访问：
- 本机：`http://127.0.0.1:5188`
- 局域网：`http://<本机IP>:5188`

### 4. 卸载服务
如需卸载，以管理员 PowerShell 执行：
```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
```

## 发布
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-service.ps1
```

默认输出目录：
- `artifacts\publish\win-x64`

发布目录内容：
- `Monitor.Service.exe`
- `appsettings.json`
- `install-service.ps1`
- `uninstall-service.ps1`
- `wwwroot\`（前端静态文件）
- `logs\`（运行后自动创建）
- `monitor.db`（服务首次启动时自动创建，与 exe 同级）

也可以直接用发布目录下的可执行文件：
```powershell
.\Monitor.Service.exe install
.\Monitor.Service.exe uninstall
```

## 目录说明
发布目录示例：
```text
Monitor.Service.exe
appsettings.json
install-service.ps1
uninstall-service.ps1
logs\
wwwroot\
```

首次运行后会在同级目录生成：
```text
monitor.db
```

## 访问方式
- 本机：`http://127.0.0.1:5188`
- 局域网：`http://<本机IP>:5188`

## 说明
- 防火墙由运维侧自行放行
- 公网访问可通过你自己的反向代理处理
- 服务运行后不显示控制台窗口
