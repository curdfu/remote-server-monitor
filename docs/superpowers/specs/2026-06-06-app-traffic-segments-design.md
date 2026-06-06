# 应用流量分段统计设计

## 背景

网络页当前已经支持按时间范围、网络范围和方向查询应用流量排行。用户希望点击排行中的具体应用后，查看该应用在历史时间段内的分段流量统计，用于判断流量峰值出现在哪些时间片段。

现有数据链路已经包含：

- `network_usage_agg`：按时间桶、应用、方向、网络范围保存原始聚合流量。
- `network_usage_rollup_12h`：按 12 小时窗口保存历史 rollup。
- `/api/network/apps`：返回当前筛选条件下的应用排行。
- `NetworkView.vue`：展示筛选区、汇总卡片和应用排行。

本需求不修改采集链路，不新增数据库表，优先复用现有历史聚合数据。

## 目标

点击“应用流量排行”中的应用后，展示该应用在当前筛选时间范围内的分段流量统计。

必须满足：

- 详情查询按 `appKey` 精确定位应用。
- 详情统计跟随当前 `from`、`to`、`scope`、`direction` 筛选条件。
- 分段粒度由后端自动选择。
- 最近 30 天范围按 12 小时切分。
- 空数据、加载中和请求失败都有明确 UI 状态。

## 非目标

本次不做以下内容：

- 不新增新的网络采集方式。
- 不修改 `app_key` 生成规则。
- 不引入图表库。
- 不做跨应用对比。
- 不做进程实例级别分析。
- 不新增数据库表。

## 自动粒度规则

后端根据查询时间跨度自动选择分段粒度：

| 时间跨度 | 分段粒度 |
| --- | --- |
| `<= 24h` | 1 小时 |
| `> 24h` | 12 小时 |

说明：

- 30 天范围会产生约 60 个分段，前端渲染和 SQLite 查询都可控。
- 大于 1 天的范围统一使用 12 小时粒度，与现有 `network_usage_rollup_12h` 对齐，避免为 6 小时粒度扫描大量原始桶。
- 如果用户选择超过 30 天，也继续按 12 小时切分，但接口需要通过最大分段数保护查询成本。
- 建议最大分段数为 200。超过时返回 `400 Bad Request`，提示时间范围过大。

## API 设计

新增端点：

```http
GET /api/network/apps/{appKey}/segments?from=2026-06-05T00:00:00Z&to=2026-06-06T00:00:00Z&scope=wan&direction=upload
```

查询参数：

- `from`：可选。缺省时使用 `to - 1h`。
- `to`：可选。缺省时使用当前 UTC 时间。
- `scope`：可选，支持 `all`、`wan`、`lan`、`loopback`，缺省为 `all`。
- `direction`：可选，支持 `total`、`upload`、`download`，缺省为 `total`。

响应 DTO：

```csharp
public sealed class AppTrafficSegmentDto
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public long TotalUploadBytes { get; init; }
    public long TotalDownloadBytes { get; init; }
    public long WanUploadBytes { get; init; }
    public long WanDownloadBytes { get; init; }
    public long LanUploadBytes { get; init; }
    public long LanDownloadBytes { get; init; }
    public long LoopbackUploadBytes { get; init; }
    public long LoopbackDownloadBytes { get; init; }
    public long OtherUploadBytes { get; init; }
    public long OtherDownloadBytes { get; init; }
}
```

响应示例：

```json
[
  {
    "from": "2026-06-05T00:00:00+00:00",
    "to": "2026-06-05T01:00:00+00:00",
    "totalUploadBytes": 1048576,
    "totalDownloadBytes": 524288,
    "wanUploadBytes": 1048576,
    "wanDownloadBytes": 524288,
    "lanUploadBytes": 0,
    "lanDownloadBytes": 0,
    "loopbackUploadBytes": 0,
    "loopbackDownloadBytes": 0,
    "otherUploadBytes": 0,
    "otherDownloadBytes": 0
  }
]
```

错误行为：

- `from >= to`：返回 `400 Bad Request`。
- 分段数量超过 200：返回 `400 Bad Request`。
- `appKey` 不存在：返回空数组，不返回 404。这样前端可以统一显示“无历史数据”。

## 后端设计

### DTO

新增文件：

- `src/Monitor.Contracts/Dtos/AppTrafficSegmentDto.cs`

职责：

- 定义前后端共享的分段流量响应结构。
- 字段与 `NetworkPeriodSummaryDto` 保持一致，额外包含 `From` 和 `To`。

### 查询模型

新增内部模型：

- `src/Monitor.Network/Models/AppTrafficSegment.cs`

职责：

- 仓储层返回按分段聚合后的应用流量。
- 字段与 DTO 对齐。

### 仓储查询

修改：

- `src/Monitor.Storage/Repositories/NetworkTrafficRepository.cs`

新增方法：

```csharp
public Task<IReadOnlyList<AppTrafficSegment>> QueryAppSegmentsAsync(
    string appKey,
    DateTimeOffset from,
    DateTimeOffset to,
    TimeSpan segmentDuration,
    TrafficScopeFilter scopeFilter = TrafficScopeFilter.All,
    TrafficDirectionFilter directionFilter = TrafficDirectionFilter.Total,
    CancellationToken cancellationToken = default)
```

查询策略：

- 使用现有 `BuildTrafficSourceSqlAsync` 复用 rollup 与原始桶混合查询能力。
- 查询开始时先通过 `app_registry` 将 `appKey` 解析为 `app_id`，不存在时返回空数组。
- 在 C# 中生成最多 200 个分段边界，再逐段执行汇总查询。
- `<= 24h` 的 1 小时分段会自然读取 `network_usage_agg` 原始桶。
- `> 24h` 的 12 小时分段会对齐现有 rollup 窗口；完整 12 小时窗口优先使用 `network_usage_rollup_12h`，首尾不足 12 小时的边界段读取 `network_usage_agg`。
- 每段 SQL 都按 `app_id + time` 过滤，确保能利用现有 app/time 索引。

注意：

- 不需要修改现有 `BuildTrafficSourceSqlAsync` 输出列，避免影响排行和汇总查询。
- 不应破坏现有排行和汇总查询的 SQL 输出列。
- 参数必须继续使用 SQLite 参数，不能拼接用户输入。

### API 端点

修改：

- `src/Monitor.WebApi/Endpoints/NetworkEndpoints.cs`

新增：

```csharp
app.MapGet("/api/network/apps/{appKey}/segments", async (...));
```

职责：

- 复用现有 `NormalizeRange`、`ParseScope`、`ParseDirection`。
- 新增 `ResolveSegmentDuration(from, to)`。
- 新增最大分段数校验。
- 调用 `QueryAppSegmentsAsync`。
- 将 `AppTrafficSegment` 映射到 `AppTrafficSegmentDto`。

## 前端设计

### 类型与 API

修改：

- `src/Monitor.Frontend/src/types/monitor.ts`
- `src/Monitor.Frontend/src/services/api.ts`

新增 TypeScript 类型：

```ts
export interface AppTrafficSegmentDto {
  from: string;
  to: string;
  totalUploadBytes: number;
  totalDownloadBytes: number;
  wanUploadBytes: number;
  wanDownloadBytes: number;
  lanUploadBytes: number;
  lanDownloadBytes: number;
  loopbackUploadBytes: number;
  loopbackDownloadBytes: number;
  otherUploadBytes: number;
  otherDownloadBytes: number;
}
```

新增 API 方法：

```ts
export function getNetworkAppSegments(appKey: string, params?: {
  from?: string;
  to?: string;
  scope?: 'all' | 'wan' | 'lan' | 'loopback';
  direction?: 'total' | 'upload' | 'download';
})
```

`appKey` 必须通过 `encodeURIComponent` 放入路径。

### UI 交互

修改：

- `src/Monitor.Frontend/src/views/NetworkView.vue`

新增状态：

- `selectedApp`：当前选中的排行应用。
- `appSegments`：当前应用的分段统计。
- `isSegmentsLoading`：详情加载状态。
- `segmentsErrorMessage`：详情错误信息。

交互规则：

- 点击排行项后设置 `selectedApp` 并加载详情。
- 再次点击同一应用不关闭详情，只刷新详情。
- 筛选条件变化时：
  - 排行重新加载。
  - 如果 `selectedApp` 仍存在，则重新加载该应用详情。
  - 如果新排行不包含 `selectedApp`，保留详情但显示“该应用不在当前排行范围内”的轻提示。
- 手动刷新时同时刷新排行和已选应用详情。

展示方式：

- 在排行列表下方增加一个同面板详情区。
- 顶部显示应用名、进程名、当前粒度、分段数。
- 主体显示每个分段的时间范围、当前筛选方向的流量值、上传/下载拆分和水平条。
- 不引入图表库，使用现有 CSS 变量和列表/条形样式。

## 取值规则

详情条形值与排行保持一致：

- `scope=wan + direction=upload` 使用 `wanUploadBytes`。
- `scope=wan + direction=download` 使用 `wanDownloadBytes`。
- `scope=wan + direction=total` 使用 `wanUploadBytes + wanDownloadBytes`。
- `scope=lan`、`scope=loopback` 同理。
- `scope=all + direction=upload` 使用 `totalUploadBytes`。
- `scope=all + direction=download` 使用 `totalDownloadBytes`。
- `scope=all + direction=total` 使用 `totalUploadBytes + totalDownloadBytes`。

## 测试与验证

后端验证：

- `dotnet build win11-system-monitor.sln`
- 针对 `QueryAppSegmentsAsync` 的最小测试建议：
  - 1 小时时间范围返回 1 小时粒度。
  - 3 天时间范围返回 12 小时粒度。
  - 30 天时间范围返回 12 小时粒度。
  - `scope` 和 `direction` 过滤后只统计目标维度。
  - 不存在的 `appKey` 返回空数组。

前端验证：

- `npm run test`，工作目录为 `src/Monitor.Frontend`。
- `npm run build`，工作目录为 `src/Monitor.Frontend`。
- 手工验证：
  - 点击排行项能显示详情。
  - 修改时间范围后详情随之刷新。
  - 最近 30 天详情显示约 60 个分段。
  - 空数据和请求失败状态可见。

## 风险与处理

### 查询成本

风险：长时间范围加小粒度会导致 SQLite 扫描大量原始桶。

处理：后端自动粒度，超过 24 小时固定 12 小时，并限制最大分段数为 200。查询必须先用 `appKey` 解析出 `app_id`，再按 `app_id + time` 过滤，确保能使用 `idx_network_usage_agg_app_bucket` 和 `idx_network_usage_rollup_12h_app_window`。

### rollup 与原始桶混用

风险：分段查询如果重复读取 rollup 和 raw，可能重复计算。

处理：复用现有 `BuildTrafficSourcePlanAsync` 的窗口规划逻辑，确保每个时间片只来自 rollup 或 raw 的其中一种来源。

### appKey 路径编码

风险：`appKey` 可能包含路径、空格或特殊字符。

处理：前端使用 `encodeURIComponent(appKey)`；后端通过路由参数接收原始值，不手写 URL 解析。

### UI 拥挤

风险：排行面板已经较密，详情区域可能过长。

处理：详情区域限制最大高度并允许内部滚动；移动端详情放在排行列表下方。

## 实施边界

建议按以下顺序实施：

1. 新增 DTO 和查询模型。
2. 在仓储层实现 `QueryAppSegmentsAsync`。
3. 新增 Web API 端点。
4. 增加前端类型和 API 方法。
5. 在 `NetworkView.vue` 增加选中应用和详情展示。
6. 补充或更新前端 Node 测试。
7. 运行 .NET build、前端 test/build 验证。
