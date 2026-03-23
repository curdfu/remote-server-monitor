# Scripts

- `publish-service.ps1`：构建前端并发布本机服务包
- `publish-and-deploy.ps1`：构建前后端并自动同步到指定部署目录，可选自动停止/启动 Windows 服务
- `publish-frontend-only.ps1`：只构建并发布前端静态文件到指定 `wwwroot`
- `install-service.ps1`：安装并启动 Windows 服务，发布后会复制到 exe 同级目录（需管理员 PowerShell）
- `uninstall-service.ps1`：卸载 Windows 服务，发布后会复制到 exe 同级目录（需管理员 PowerShell）
- `clean-build-artifacts.ps1`：清理仓库中的中间产物和发布产物，可选连 `node_modules` 一并清理
- `verify-etw-network.ps1`：验证 ETW 网络采集是否正常工作，支持健康检查、实时采样、历史聚合检查，以及可选的自动/手动造流量

  ## 用法示例

  ### 最常用：发布并部署到你的服务目录

  powershell -ExecutionPolicy Bypass -File .\publish-and-deploy.ps1 -DeployDir 'C:\GreenSoft\remote-server-monitor-win-x64'

  ———

  ## 可选参数

  ### 不重启服务

  powershell -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 -DeployDir 'C:\GreenSoft\remote-server-
  monitor-win-x64' -RestartService:$false

  ### 不保留旧的 appsettings.json

  默认会保留已有 appsettings.json。
  如果你想覆盖它：

  powershell -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 -DeployDir 'C:\GreenSoft\remote-server-
  monitor-win-x64' -PreserveAppSettings:$false

  ### 指定配置和运行时

  powershell -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 `
    -DeployDir 'C:\GreenSoft\remote-server-monitor-win-x64' `
    -Configuration Release `
    -Runtime win-x64