# Scripts

- `publish-service.ps1`：构建前端并发布本机服务包
- `publish-frontend-only.ps1`：只构建并发布前端静态文件到指定 `wwwroot`
- `install-service.ps1`：安装并启动 Windows 服务，发布后会复制到 exe 同级目录（需管理员 PowerShell）
- `uninstall-service.ps1`：卸载 Windows 服务，发布后会复制到 exe 同级目录（需管理员 PowerShell）
- `clean-build-artifacts.ps1`：清理仓库中的中间产物和发布产物，可选连 `node_modules` 一并清理
