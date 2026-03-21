# win11-system-monitor

Win11 本地系统监控项目。

当前已完成：
- 工程目录骨架初始化
- 解决方案与项目文件初始化
- 后端模块边界与基础接口骨架
- 独立前端项目骨架

当前阻塞：
- 本机仅安装了 .NET Runtime，未安装 .NET SDK
- 在 SDK 安装完成前，无法执行 dotnet restore / build / test
- 外部 NuGet 依赖与前端 npm 依赖也尚未实际安装

下一步建议：
1. 安装 .NET 8 SDK
2. 执行依赖还原
3. 继续实现任务 4~6（硬件采集 MVP）
