export interface TrendGeometryPoint {
  time: string;
  value: number | null;
}

export interface TrendSegment {
  points: Array<TrendGeometryPoint & { timestamp: number; value: number }>;
}

export function getTrendWindow(
  points: TrendGeometryPoint[],
  durationMs = 60 * 60 * 1000,
  requestedEndMs?: number
) {
  const timestamps = points
    .map((point) => Date.parse(point.time))
    .filter((value) => Number.isFinite(value));
  const endMs = Number.isFinite(requestedEndMs)
    ? requestedEndMs as number
    : timestamps.length ? Math.max(...timestamps) : Date.now();
  return { startMs: endMs - durationMs, endMs };
}

export function splitTrendSegments(points: TrendGeometryPoint[], gapMs = 15_000): TrendSegment[] {
  const segments: TrendSegment[] = [];
  let current: TrendSegment['points'] = [];
  let previousTimestamp: number | null = null;

  const flush = () => {
    if (current.length) segments.push({ points: current });
    current = [];
  };

  for (const point of points) {
    const timestamp = Date.parse(point.time);
    if (!Number.isFinite(timestamp) || point.value == null || !Number.isFinite(point.value)) {
      flush();
      previousTimestamp = null;
      continue;
    }

    if (previousTimestamp != null && timestamp - previousTimestamp > gapMs) flush();
    current.push({ ...point, timestamp, value: point.value });
    previousTimestamp = timestamp;
  }

  flush();
  return segments;
}

export function getTrendDomain(kind: 'percent' | 'temperature', points: TrendGeometryPoint[]) {
  if (kind === 'percent') return { min: 0, max: 100, ticks: [100, 50, 0] };

  const values = points
    .map((point) => point.value)
    .filter((value): value is number => value != null && Number.isFinite(value));
  const minimum = values.length ? Math.min(...values) : 0;
  const maximum = values.length ? Math.max(...values) : 100;
  const min = Math.min(0, Math.floor(minimum / 20) * 20);
  const max = Math.max(100, Math.ceil(maximum / 20) * 20);
  const middle = min + (max - min) / 2;
  return { min, max, ticks: [max, middle, min] };
}

export function projectTrendPoint(
  point: { timestamp: number; value: number },
  window: { startMs: number; endMs: number },
  domain: { min: number; max: number },
  width = 360,
  height = 120
) {
  const xRatio = (point.timestamp - window.startMs) / Math.max(1, window.endMs - window.startMs);
  const yRatio = (point.value - domain.min) / Math.max(1, domain.max - domain.min);
  return {
    x: Math.max(0, Math.min(width, xRatio * width)),
    y: Math.max(8, Math.min(height - 8, height - 12 - yRatio * (height - 24)))
  };
}

export function downsampleTrendSegments(segments: TrendSegment[], bucketCount: number): TrendSegment[] {
  const safeBucketCount = Math.max(1, Math.floor(bucketCount));

  return segments.map((segment) => {
    if (segment.points.length <= safeBucketCount) {
      return { points: [...segment.points] };
    }

    const firstTimestamp = segment.points[0].timestamp;
    const lastTimestamp = segment.points.at(-1)?.timestamp ?? firstTimestamp;
    const span = Math.max(1, lastTimestamp - firstTimestamp);
    const buckets = new Map<number, TrendSegment['points']>();

    for (const point of segment.points) {
      const bucket = Math.min(
        safeBucketCount - 1,
        Math.floor(((point.timestamp - firstTimestamp) / span) * safeBucketCount)
      );
      const bucketPoints = buckets.get(bucket) ?? [];
      bucketPoints.push(point);
      buckets.set(bucket, bucketPoints);
    }

    const selected = new Map<number, TrendSegment['points'][number]>();
    for (const bucketPoints of buckets.values()) {
      const candidates = [
        bucketPoints[0],
        bucketPoints.at(-1),
        bucketPoints.reduce((minimum, point) => point.value < minimum.value ? point : minimum),
        bucketPoints.reduce((maximum, point) => point.value > maximum.value ? point : maximum)
      ];
      for (const point of candidates) {
        if (point) selected.set(point.timestamp, point);
      }
    }

    return { points: [...selected.values()].sort((left, right) => left.timestamp - right.timestamp) };
  });
}
