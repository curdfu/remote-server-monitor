# MVP 数据表设计

## 设计目标
MVP 阶段的数据设计目标：
- 能保存硬件实时历史
- 能保存按 app 聚合后的网络流量
- 能支持按时间区间查询
- 能支持 WAN / LAN 维度统计
- 结构尽量简单，便于快速落地

数据库建议：**SQLite**

---

## 一、settings

用于保存系统配置。

```sql
CREATE TABLE settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    http_port INTEGER NOT NULL,
    hardware_sample_interval_ms INTEGER NOT NULL,
    network_sample_interval_ms INTEGER NOT NULL,
    aggregate_interval_seconds INTEGER NOT NULL,
    history_retention_days INTEGER NOT NULL,
    top_n_default INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
```

### 字段说明
- `http_port`：本地 HTTP 服务端口
- `hardware_sample_interval_ms`：硬件采样间隔
- `network_sample_interval_ms`：网络聚合刷新间隔
- `aggregate_interval_seconds`：默认聚合粒度
- `history_retention_days`：历史保留天数
- `top_n_default`：默认榜单数量

---

## 二、hardware_samples

保存硬件实时快照。

```sql
CREATE TABLE hardware_samples (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sample_time TEXT NOT NULL,
    cpu_usage_percent REAL,
    cpu_temperature_c REAL,
    cpu_frequency_mhz REAL,
    memory_total_mb REAL,
    memory_used_mb REAL,
    memory_usage_percent REAL,
    disk_temperature_c REAL,
    uptime_seconds INTEGER NOT NULL
);

CREATE INDEX idx_hardware_samples_sample_time
ON hardware_samples(sample_time);
```

### 说明
MVP 先以“单机总体快照”为主，先不拆每块磁盘、多 CPU Package、多传感器明细。

---

## 三、app_registry

保存进程/应用基础信息，避免流量表重复写名称。

```sql
CREATE TABLE app_registry (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    app_key TEXT NOT NULL UNIQUE,
    process_name TEXT NOT NULL,
    display_name TEXT,
    executable_path TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
```

### app_key 建议
可用：
- `process_name + executable_path` 的哈希
- 或标准化后的 exe 路径

这样可以减少同名不同路径进程混淆。

---

## 四、network_usage_agg

核心聚合表，按时间桶 + app + 方向 + 范围保存统计值。

```sql
CREATE TABLE network_usage_agg (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    bucket_start_time TEXT NOT NULL,
    bucket_granularity_seconds INTEGER NOT NULL,
    app_id INTEGER NOT NULL,
    direction TEXT NOT NULL,
    scope_type TEXT NOT NULL,
    bytes INTEGER NOT NULL,
    packets INTEGER,
    FOREIGN KEY(app_id) REFERENCES app_registry(id)
);

CREATE INDEX idx_network_usage_agg_bucket_time
ON network_usage_agg(bucket_start_time);

CREATE INDEX idx_network_usage_agg_app_bucket
ON network_usage_agg(app_id, bucket_start_time);

CREATE INDEX idx_network_usage_agg_scope_direction
ON network_usage_agg(scope_type, direction, bucket_start_time);
```

### direction 枚举
- `inbound`
- `outbound`

### scope_type 枚举
- `wan`
- `lan`
- `loopback`
- `other`

### 说明
MVP 不直接存每条网络事件，而是存聚合结果，明显降低数据量。

---

## 五、推荐查询示例

## 1. 查询某时间区间 Top App 下载量

```sql
SELECT a.display_name,
       a.process_name,
       SUM(n.bytes) AS total_bytes
FROM network_usage_agg n
JOIN app_registry a ON a.id = n.app_id
WHERE n.bucket_start_time >= @from
  AND n.bucket_start_time < @to
  AND n.direction = 'inbound'
GROUP BY n.app_id
ORDER BY total_bytes DESC
LIMIT @topN;
```

## 2. 查询 WAN 上传/下载汇总

```sql
SELECT direction,
       SUM(bytes) AS total_bytes
FROM network_usage_agg
WHERE bucket_start_time >= @from
  AND bucket_start_time < @to
  AND scope_type = 'wan'
GROUP BY direction;
```

## 3. 查询硬件历史趋势

```sql
SELECT sample_time,
       cpu_usage_percent,
       memory_usage_percent,
       cpu_temperature_c,
       disk_temperature_c
FROM hardware_samples
WHERE sample_time >= @from
  AND sample_time < @to
ORDER BY sample_time ASC;
```

---

## 七、MVP 阶段建表总结

MVP 最少只需要这 4 张：
- `settings`
- `hardware_samples`
- `app_registry`
- `network_usage_agg`

这样已经足够支撑：
- 实时总览
- 历史曲线
- 按 app 统计
- WAN/LAN 统计
- 自定义时间区间查询
