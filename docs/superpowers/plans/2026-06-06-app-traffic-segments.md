# App Traffic Segments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 点击网络页应用流量排行中的具体应用后，显示该应用在当前历史时间范围内的分段流量统计。

**Architecture:** 后端新增按 `appKey` 查询分段流量的 API，仓储层复用现有 raw/12h rollup 规划逻辑并按分段聚合。前端在 `NetworkView.vue` 内维护选中应用、详情加载状态和分段列表，不引入新图表库。

**Tech Stack:** .NET 8 Minimal API, SQLite, Vue 3 `script setup`, TypeScript, Vite, Node test runner.

---

## File Structure

- Create: `src/Monitor.Contracts/Dtos/AppTrafficSegmentDto.cs`
  - 前后端共享的应用分段流量响应 DTO。
- Create: `src/Monitor.Network/Models/AppTrafficSegment.cs`
  - 仓储层返回的内部模型。
- Modify: `src/Monitor.Storage/Repositories/NetworkTrafficRepository.cs`
  - 新增 `QueryAppSegmentsAsync`，逐段复用现有 raw/12h rollup 汇总 source。
- Modify: `src/Monitor.WebApi/Endpoints/NetworkEndpoints.cs`
  - 新增 `/api/network/apps/{appKey}/segments` 端点、粒度解析、分段数量保护和 DTO 映射。
- Modify: `src/Monitor.Frontend/src/types/monitor.ts`
  - 新增 `AppTrafficSegmentDto` 类型。
- Modify: `src/Monitor.Frontend/src/services/api.ts`
  - 新增 `getNetworkAppSegments`。
- Modify: `src/Monitor.Frontend/src/views/NetworkView.vue`
  - 排行项可点击，新增详情状态、加载逻辑和分段列表 UI。
- Modify or create: `src/Monitor.Frontend/network-view-app-segments.test.js`
  - 用源码断言覆盖前端接口和详情 UI 的关键结构。

## Task 1: Backend Contracts And Models

**Files:**
- Create: `src/Monitor.Contracts/Dtos/AppTrafficSegmentDto.cs`
- Create: `src/Monitor.Network/Models/AppTrafficSegment.cs`

- [ ] **Step 1: Add DTO**

```csharp
namespace Monitor.Contracts.Dtos;

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

- [ ] **Step 2: Add repository model**

```csharp
namespace Monitor.Network.Models;

public sealed class AppTrafficSegment
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

## Task 2: Repository Segment Query

**Files:**
- Modify: `src/Monitor.Storage/Repositories/NetworkTrafficRepository.cs`

- [ ] **Step 1: Add public query method**

Add `QueryAppSegmentsAsync` with signature:

```csharp
public async Task<IReadOnlyList<AppTrafficSegment>> QueryAppSegmentsAsync(
    string appKey,
    DateTimeOffset from,
    DateTimeOffset to,
    TimeSpan segmentDuration,
    TrafficScopeFilter scopeFilter = TrafficScopeFilter.All,
    TrafficDirectionFilter directionFilter = TrafficDirectionFilter.Total,
    CancellationToken cancellationToken = default)
```

Implementation requirements:

- Throw `ArgumentException` when `appKey` is blank.
- Return empty list when `from >= to`.
- Resolve `appKey` to `app_id` before querying traffic tables.
- Generate segment ranges in C#.
- For each segment, reuse `BuildTrafficSourceSqlAsync` so completed 12-hour windows use rollup and partial windows use raw buckets.
- Return one row per segment, including zero-traffic segments for existing apps.

- [ ] **Step 2: Add segment range builder**

Add private helpers:

```csharp
private static IReadOnlyList<TimeRange> BuildSegmentRanges(
    DateTimeOffset from,
    DateTimeOffset to,
    TimeSpan segmentDuration)

private static IReadOnlyList<TimeRange> BuildRollupAlignedSegmentRanges(
    DateTimeOffset from,
    DateTimeOffset to)
```

Requirements:

- 1-hour ranges are continuous from the query start.
- 12-hour ranges align to existing rollup windows, with partial raw-backed first or last ranges when the query boundaries are not aligned.
- Existing `BuildTrafficSourceSqlAsync` remains unchanged for summary/ranking queries.

- [ ] **Step 3: Keep SQL parameterized**

Use parameters for:

- `$appId`
- raw range parameters already produced by source builder

Only `scope_type` and `direction` literals may continue using existing internally generated enum values.

## Task 3: Network API Endpoint

**Files:**
- Modify: `src/Monitor.WebApi/Endpoints/NetworkEndpoints.cs`

- [ ] **Step 1: Add endpoint**

Add:

```csharp
app.MapGet("/api/network/apps/{appKey}/segments", async (...));
```

Endpoint behavior:

- Normalize range using existing `NormalizeRange`.
- Reject `rangeFrom >= rangeTo` with `400`.
- Resolve segment duration:
  - `<= 24h`: `TimeSpan.FromHours(1)`
  - `> 24h`: `TimeSpan.FromHours(12)`
- Reject more than 200 segments with `400`.
- Parse `scope` and `direction` with existing helpers.
- Return `AppTrafficSegmentDto[]`.

- [ ] **Step 2: Add mapper**

Add:

```csharp
private static AppTrafficSegmentDto ToSegmentDto(AppTrafficSegment segment)
```

Map all fields one-to-one.

## Task 4: Frontend Types And API

**Files:**
- Modify: `src/Monitor.Frontend/src/types/monitor.ts`
- Modify: `src/Monitor.Frontend/src/services/api.ts`

- [ ] **Step 1: Add TS type**

Add `AppTrafficSegmentDto` with the same field names as JSON response.

- [ ] **Step 2: Add API client**

Add:

```ts
export function getNetworkAppSegments(appKey: string, params?: {
  from?: string;
  to?: string;
  scope?: 'all' | 'wan' | 'lan' | 'loopback';
  direction?: 'total' | 'upload' | 'download';
}) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.scope) query.set('scope', params.scope);
  if (params?.direction) query.set('direction', params.direction);
  return request<AppTrafficSegmentDto[]>(`/api/network/apps/${encodeURIComponent(appKey)}/segments${query.toString() ? `?${query}` : ''}`);
}
```

## Task 5: Frontend Detail UI

**Files:**
- Modify: `src/Monitor.Frontend/src/views/NetworkView.vue`

- [ ] **Step 1: Add state**

Add:

```ts
const selectedApp = ref<AppTrafficSummaryDto | null>(null);
const appSegments = ref<AppTrafficSegmentDto[]>([]);
const isSegmentsLoading = ref(false);
const segmentsErrorMessage = ref('');
let segmentsRequestVersion = 0;
```

- [ ] **Step 2: Add click handler**

Add:

```ts
function selectApp(item: AppTrafficSummaryDto) {
  selectedApp.value = item;
  void loadSelectedAppSegments();
}
```

- [ ] **Step 3: Add segment loader with request version guard**

Ensure fast clicks cannot let old responses overwrite new state.

- [ ] **Step 4: Refresh selected app detail after filter reload**

After `loadApps` completes, if `selectedApp.value` exists, update it from the new app list when possible and call `loadSelectedAppSegments()`.

- [ ] **Step 5: Render detail panel**

Render detail below the ranking list:

- selected app title
- process name
- active segment duration label
- loading/error/empty states
- segment rows with time range, selected metric value, upload/download bytes, progress bar

## Task 6: Tests And Validation

**Files:**
- Create or modify: `src/Monitor.Frontend/network-view-app-segments.test.js`

- [ ] **Step 1: Add frontend source assertions**

Assert:

- `getNetworkAppSegments` uses `encodeURIComponent(appKey)`.
- `NetworkView.vue` imports `getNetworkAppSegments`.
- Ranking items have click handling.
- Segment request version guard exists.
- Segment detail panel has loading, error and empty states.

- [ ] **Step 2: Run frontend tests**

Run from `src/Monitor.Frontend`:

```powershell
npm run test
```

- [ ] **Step 3: Run frontend build**

Run from `src/Monitor.Frontend`:

```powershell
npm run build
```

- [ ] **Step 4: Run backend build**

Run from repository root:

```powershell
dotnet build win11-system-monitor.sln
```

## Git Handling

This repository's active instructions say git is read-only for the agent. The agent must not commit, pull, push, reset, rebase or clean. After verification, the user should review the diff and commit manually if desired.
