import * as signalR from '@microsoft/signalr';
import type {
  AppTrafficItemDto,
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
// Reserved: network realtime is intentionally unused for now.
// Keep the listener set and subscribe API so the connection layer stays stable if restored later.
const networkListeners = new Set<Listener<NetworkRealtimeDto>>();
// Reserved: top apps realtime is intentionally unused for now.
// Keep the listener set and subscribe API so the connection layer stays stable if restored later.
const topAppsListeners = new Set<Listener<AppTrafficItemDto[]>>();
const connectionStateListeners = new Set<Listener<RealtimeConnectionState>>();

let connection: signalR.HubConnection | null = null;
let startPromise: Promise<void> | null = null;
let reconnectTimer: number | null = null;
let currentConnectionState: RealtimeConnectionState = 'disconnected';

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

  // Reserved: the current backend does not actively push networkRealtime.
  connection.on('networkRealtime', (payload: NetworkRealtimeDto) => {
    emitPayload(networkListeners, payload);
  });

  // Reserved: the current backend does not actively push topAppsRealtime.
  connection.on('topAppsRealtime', (payload: AppTrafficItemDto[]) => {
    emitPayload(topAppsListeners, payload);
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

export function subscribeHardwareRealtime(listener: Listener<HardwareRealtimeDto>) {
  return subscribe(hardwareListeners, listener);
}

// Reserved: current UI does not consume network realtime, but keep the API for compatibility.
export function subscribeNetworkRealtime(listener: Listener<NetworkRealtimeDto>) {
  return subscribe(networkListeners, listener);
}

// Reserved: current UI does not consume top apps realtime, but keep the API for compatibility.
export function subscribeTopAppsRealtime(listener: Listener<AppTrafficItemDto[]>) {
  return subscribe(topAppsListeners, listener);
}

export function subscribeRealtimeConnectionState(listener: Listener<RealtimeConnectionState>) {
  listener(currentConnectionState);
  return subscribe(connectionStateListeners, listener);
}
