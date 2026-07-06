import * as signalR from '@microsoft/signalr';
import type {
  HardwareRealtimeDto,
  NetworkRealtimeDto
} from '../types/monitor';

type Listener<T> = (payload: T) => void;

export type RealtimeConnectionState =
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'disconnected';

const hardwareListeners = new Set<Listener<HardwareRealtimeDto>>();
// 当前 UI 暂不消费 network realtime，但保留 listener 集合和订阅 API，方便后续恢复网络实时推送时保持连接层兼容。
const networkListeners = new Set<Listener<NetworkRealtimeDto>>();
const connectionStateListeners = new Set<Listener<RealtimeConnectionState>>();

let connection: signalR.HubConnection | null = null;
let startPromise: Promise<void> | null = null;
let reconnectTimer: number | null = null;
let currentConnectionState: RealtimeConnectionState = 'disconnected';

// 向页面广播实时连接状态，供首页等页面展示“已连接/重连中”等状态
function emitConnectionState(state: RealtimeConnectionState) {
  currentConnectionState = state;
  connectionStateListeners.forEach((listener) => listener(state));
}

function emitPayload<T>(listeners: Set<Listener<T>>, payload: T) {
  listeners.forEach((listener) => listener(payload));
}

function resolveHubUrl() {
  const apiBase = import.meta.env.VITE_API_BASE_URL?.replace(/\/$/, '');
  const baseUrl = apiBase || window.location.origin;
  return new URL('/hubs/monitor', baseUrl).toString();
}

function clearReconnectTimer() {
  if (reconnectTimer !== null) {
    window.clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
}

// SignalR 断开后兜底重连：后端 Hub 不可用时，前端每 5 秒再尝试一次
function scheduleReconnect() {
  if (reconnectTimer !== null) {
    return;
  }

  reconnectTimer = window.setTimeout(() => {
    reconnectTimer = null;
    void startRealtimeConnection().catch(() => {
      scheduleReconnect();
    });
  }, 5000);
}

// 创建并缓存 SignalR 连接，对应后端 /hubs/monitor 实时推送通道
function ensureConnection() {
  if (connection) {
    return connection;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl(resolveHubUrl())
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('hardwareRealtime', (payload: HardwareRealtimeDto) => {
    emitPayload(hardwareListeners, payload);
  });

  // 后端当前不主动推送 networkRealtime；监听保留用于兼容未来恢复的事件名。
  connection.on('networkRealtime', (payload: NetworkRealtimeDto) => {
    emitPayload(networkListeners, payload);
  });

  connection.onreconnecting(() => {
    emitConnectionState('reconnecting');
  });

  connection.onreconnected(() => {
    clearReconnectTimer();
    emitConnectionState('connected');
  });

  connection.onclose(() => {
    emitConnectionState('disconnected');
    scheduleReconnect();
  });

  return connection;
}

// 启动与后端 Hub 的实时连接：首页硬件卡片会依赖这条连接接收推送
export async function startRealtimeConnection() {
  const hubConnection = ensureConnection();
  if (hubConnection.state === signalR.HubConnectionState.Connected) {
    emitConnectionState('connected');
    return;
  }

  if (
    hubConnection.state === signalR.HubConnectionState.Connecting ||
    hubConnection.state === signalR.HubConnectionState.Reconnecting
  ) {
    return startPromise ?? Promise.resolve();
  }

  if (startPromise) {
    return startPromise;
  }

  // startPromise 复用同一次连接启动过程，避免多个页面同时订阅时重复调用 HubConnection.start()。
  emitConnectionState('connecting');
  startPromise = hubConnection
    .start()
    .then(() => {
      clearReconnectTimer();
      emitConnectionState('connected');
    })
    .catch((error) => {
      emitConnectionState('disconnected');
      scheduleReconnect();
      throw error;
    })
    .finally(() => {
      startPromise = null;
    });

  return startPromise;
}

function subscribe<T>(listeners: Set<Listener<T>>, listener: Listener<T>) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

// 首页使用：订阅后端推送的 hardwareRealtime 事件
export function subscribeHardwareRealtime(listener: Listener<HardwareRealtimeDto>) {
  return subscribe(hardwareListeners, listener);
}

// 当前页面未消费网络实时事件，但保留订阅函数，避免未来恢复推送时改动调用方 API。
export function subscribeNetworkRealtime(listener: Listener<NetworkRealtimeDto>) {
  return subscribe(networkListeners, listener);
}

// 页面可通过这个订阅连接状态变化，用于提示当前实时通道是否正常
export function subscribeRealtimeConnectionState(listener: Listener<RealtimeConnectionState>) {
  listener(currentConnectionState);
  return subscribe(connectionStateListeners, listener);
}
