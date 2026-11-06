# 脚本说明

以下示例从仓库根目录、Windows PowerShell 运行。部署/服务管理需要对目标目录、服务和生成目录有足够权限（通常需要管理员权限）。不会自动安装 `sqlite3.exe` 或其他全局工具。

- `publish-service.ps1`：构建前端和自包含的 Windows 服务发布包。
- `publish-and-deploy.ps1`：构建、验证、备份并部署完整发布包；使用 `deployment-helpers.ps1` 实现安全检查和回滚。
- `test-deployment.ps1`：在临时目录使用模拟服务测试部署，不依赖 Pester。
- `test-vacuum.ps1`：在临时目录使用 Windows 自带 SQLite 库测试维护流程，不接触生产数据库。
- `vacuum-sqlite.ps1`：明确确认前提后的手动 SQLite 空间回收；不会定期或随部署自动执行。
- `publish-frontend-only.ps1`：仅构建和发布前端；**此脚本仍会清空目标 `wwwroot`，没有部署脚本的备份/回滚保护**，不要将用户数据放入其目标目录。
- `install-service.ps1` / `uninstall-service.ps1`：安装或卸载 Windows 服务；发布包会带上这两个脚本。
- `clean-build-artifacts.ps1`：清理仓库的构建/发布产物，可选清理 `node_modules`；会删除仓库内保留的部署暂存目录，先确认不再需要恢复材料。
- `verify-etw-network.ps1`：验证 ETW 网络采集。

## 发布并部署

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 `
  -DeployDir 'C:\GreenSoft\remote-server-monitor-win-x64'
```

默认 `Configuration=Release`、`Runtime=win-x64`、`ServiceName=RemoteServerMonitor`、`RestartService=$true`、`PreserveAppSettings=$true`。可按需指定 `-Configuration`、`-Runtime`、`-ServiceName`。npm 缓存默认遵循现有 npm 配置/环境变量；只有明确传入 `-NpmCacheDir 'C:\some-cache'` 才会为构建临时覆盖，不会修改全局配置。

构建会先写入仓库 `artifacts\deploy-staging\<时间-随机标识>` 并校验必要的 exe、程序集、运行配置和前端首页，再检查服务。发布目录、部署目录、备份目录不能相同或互为父子，不支持经过符号链接、junction 等重解析点。部署只覆盖发布清单里的文件，包括运行时子目录，**不清空任何目录**；原先存在但不再属于新包的文件（包括旧前端静态资源）会保留，需要人工确认后清理。部署期间请阻止其他人/自动任务同时修改目标目录、启动该服务或执行另一轮部署；路径及服务状态检查不是跨进程互斥锁，也无法识别使用不同服务名、直接运行 exe 或其他程序对目标文件的占用。

若服务原先运行，默认先停止，部署成功后恢复运行；若原先停止或不存在，不会启动或创建服务。服务处于暂停/转换等其他状态会拒绝部署。明确禁用重启时，只能在服务已停止或不存在时部署；服务正在运行会直接拒绝，不会在线覆盖：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 `
  -DeployDir 'C:\GreenSoft\remote-server-monitor-win-x64' -RestartService:$false
```

默认保留目标中已存在的 `appsettings.json` 和 `appsettings.*.json`；目标中不存在的对应发布文件仍会复制。如确需采用发布包版本，显式执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 `
  -DeployDir 'C:\GreenSoft\remote-server-monitor-win-x64' -PreserveAppSettings:$false
```

程序文件备份保存在部署目录的同级 `<部署目录名>.deploy-backups\<时间-随机标识>`，含 `deployment-manifest.json`、需要覆盖的旧文件和原服务状态。**这不是数据库备份**：不会将 `monitor.db`、`monitor.db-wal`、`monitor.db-shm`、日志或其他未发布的用户文件作为一致性备份；不能把在线逐文件复制 SQLite 数据库视为可恢复备份。脚本拒绝发布包中出现数据库、日志和常见数据目录，且不删除任何未发布的用户文件；如果自定义用户文件与正常发布文件同路径，部署会覆盖它，应事先移走或另行备份。

备份未完成前不会开始覆盖；覆盖失败或新服务启动失败时，在确认服务已停止后恢复已触碰的旧程序文件、删除本次新增的文件，再尽力恢复原先运行状态。如果不能安全停止服务或回滚/启动失败，脚本报告具体错误，不会继续不安全覆盖或假装成功。失败返回非零退出码，并保留暂存、已创建的备份/清单供人工恢复；备份自身失败可能只有部分文件，**不得当作完整回滚包**。成功时也保留暂存及备份；两者均需按本地保留策略、在确认无需回滚后手动清理。服务进入 `RUNNING` 仅表示 SCM 已报告运行，不等于应用健康检查通过；部署后应另行验证健康状态。

## 手动 SQLite 空间回收

应用默认数据库位于服务程序目录的 `monitor.db`。正常删除历史数据不会必然缩小数据库文件；**不建议也不会自动定期 VACUUM**。只有需要回收磁盘时才安排维护窗口：

1. 管理员手动停止正确的服务，并阻止自动重启；确认没有任何其他进程打开数据库（包括临时工具或其他服务）。必须持续保持独占，直到维护脚本退出。
2. 用 SQLite 在线备份 API/CLI `.backup`，或在所有连接已关闭后按照 SQLite 正确处理 WAL 的要求创建一致备份；确保备份位于独立文件，验证其可恢复。不要把在线逐文件拷贝 `monitor.db`、WAL、SHM 当成一致备份。
3. 确认足够的磁盘空间。脚本会检查数据库本地盘至少剩余 `2 × (数据库文件 + 现存 WAL)`；这是最低保守估算，并不能替代对备份、其他卷及突发写入空间的人工检查。数据库需位于本地盘符路径；临时目录被设置为数据库所在目录，但 SQLite/系统配置可能有其他空间需求。
4. 自行准备与数据库兼容、可信的官方 SQLite 命令行工具 `sqlite3.exe`（具备 `-readonly`、`-batch`、`-bail`）；脚本不会安装或下载它。显式传入路径和全部四项确认：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\vacuum-sqlite.ps1 `
  -DatabasePath 'C:\GreenSoft\remote-server-monitor-win-x64\monitor.db' `
  -BackupPath 'E:\Backups\monitor-verified.db' `
  -Sqlite3Path 'C:\Tools\sqlite3.exe' `
  -ConfirmServiceStopped -ConfirmExclusiveAccess -ConfirmEnoughFreeSpace -ConfirmBackupVerified
```

可指定 `-ServiceName`；默认检查 `RemoteServerMonitor` 必须已停止或不存在，不会停止或启动服务。脚本先对备份做只读 `PRAGMA quick_check`，再设置 SQLite 独占锁、尝试获取独占写锁，执行 `VACUUM` 和 `quick_check`。备份检查**不能证明备份新鲜、完整可恢复或来自正确数据库**，确认责任仍由操作者承担；也不能发现不遵循 SQLite 锁的访问。失败返回非零，保留备份，不会自动恢复数据库；先排查并确认数据健康，再由管理员决定恢复或重新启动服务。

## 测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-deployment.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-vacuum.ps1
```

部署测试仅操作随机临时目录和模拟服务，结束后删除测试目录，不运行构建或真实服务操作。覆盖运行/停止/不存在状态、禁止在线覆盖、复制/备份/启动失败和回滚、配置及持久文件保护、路径冲突和 junction。维护测试使用 Windows 自带 `winsqlite3.dll` 和编译到临时目录的 CLI 替身，在临时数据库上运行 VACUUM，验证确认开关、坏备份和独占锁，不操作生产数据库，也不验证真实官方 CLI 的兼容性。测试依赖 Windows PowerShell 5.1 的 `Add-Type`，也可从 PowerShell 7 启动（会转入 Windows PowerShell 5.1）。
