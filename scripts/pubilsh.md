  ## 用法示例

  ### 最常用：发布并部署到你的服务目录

  powershell -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 -DeployDir 'C:\GreenSoft\remote-server-monitor-win-x64'

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