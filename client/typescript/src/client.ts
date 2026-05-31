// TypeScript client for the Inova-API-Plugin.
//
// Covers all current endpoints — HTTP GETs plus the /state/stream WebSocket.
// Imports WebSocket from "ws" because Node added native WebSocket only in v22.
// In a browser, replace the import with the global WebSocket.

import WebSocket from "ws";

const DEFAULT_BASE_URL =
    process.env.INOVA_API_BASE_URL ?? "http://192.168.1.146:5001";

// ---- Response shapes ----------------------------------------------------

export interface SystemTimestamp {
    timestamp: number;
    isEmpty: boolean;
    totalSeconds: number;
    elapsedFromNow: string;
}

export interface Position {
    x: number;
    y: number;
    z1: number;
    z2: number;
    r: number;
}

export interface LightsState {
    isEnabled: boolean;
    lightCount: number;
}

export interface PowerEntry {
    timestamp: SystemTimestamp;
    id: string;
    power: number;
}

export interface PowermanState {
    maxPower: number;
    currentPower: number;
    requiredPower: number;
    poweredPinsDescription: string;
}

export interface PowerState {
    entries: PowerEntry[];
    powerman: PowermanState;
}

export interface TemperatureEntry {
    timestamp: SystemTimestamp;
    id: string;
    targetTemperature: number | null;
    currentTemperature: number;
    averageTemperature: number;
    settable: boolean;
    targetReached: boolean;
}

export interface TemperatureState {
    entries: TemperatureEntry[];
}

export interface StateSnapshot {
    position: Position;
    lights: LightsState;
    power: PowerState;
    temperature: TemperatureState;
}

export interface InfoResponse {
    plugin: string;
    version: string;
    listenPort: number;
    startedAtUtc: string;
    uptimeSeconds: number;
}

export interface Timed<T> {
    respondedAt: string;
    data: T;
}

// ---- Client -------------------------------------------------------------

export class InovaClient {
    private baseUrl: string;

    constructor(baseUrl: string = DEFAULT_BASE_URL) {
        this.baseUrl = baseUrl.replace(/\/$/, "");
    }

    async ping(): Promise<string> {
        const r = await fetch(`${this.baseUrl}/ping`);
        if (!r.ok) throw new Error(`ping failed: HTTP ${r.status}`);
        return r.text();
    }

    info(): Promise<InfoResponse> {
        return this.getJson<InfoResponse>("/info");
    }

    movementPosition(): Promise<Timed<Position>> {
        return this.getJson<Timed<Position>>("/movement/position");
    }

    lightsState(): Promise<Timed<LightsState>> {
        return this.getJson<Timed<LightsState>>("/lights/state");
    }

    powerCurrent(): Promise<Timed<PowerState>> {
        return this.getJson<Timed<PowerState>>("/power/current");
    }

    temperatureCurrent(): Promise<Timed<TemperatureState>> {
        return this.getJson<Timed<TemperatureState>>("/temperature/current");
    }

    stateSnapshot(): Promise<Timed<StateSnapshot>> {
        return this.getJson<Timed<StateSnapshot>>("/state/snapshot");
    }

    /**
     * Yields decoded JSON frames from /state/stream until the consumer breaks
     * out of the loop or the socket closes. Each frame is `Timed<StateSnapshot>`.
     */
    async *streamState(hz = 10): AsyncGenerator<Timed<StateSnapshot>> {
        const wsUrl = this.baseUrl
            .replace(/^http:\/\//, "ws://")
            .replace(/^https:\/\//, "wss://");
        const ws = new WebSocket(`${wsUrl}/state/stream?hz=${hz}`);

        // Bridge the event-based WebSocket to an async iterator via a queue.
        const queue: string[] = [];
        const waiters: ((value: string | null) => void)[] = [];
        let closed = false;
        const closeReason = { value: null as Error | null };

        ws.on("message", (raw) => {
            const text = typeof raw === "string" ? raw : raw.toString("utf-8");
            const waiter = waiters.shift();
            if (waiter) waiter(text);
            else queue.push(text);
        });
        ws.on("close", () => { closed = true; waiters.forEach(w => w(null)); waiters.length = 0; });
        ws.on("error", (err) => { closeReason.value = err; closed = true; waiters.forEach(w => w(null)); waiters.length = 0; });

        try {
            // Wait until socket is open before yielding.
            await new Promise<void>((resolve, reject) => {
                ws.once("open", () => resolve());
                ws.once("error", reject);
            });

            while (!closed) {
                const text = queue.shift() ?? await new Promise<string | null>(r => waiters.push(r));
                if (text === null) break;
                yield JSON.parse(text) as Timed<StateSnapshot>;
            }
            if (closeReason.value) throw closeReason.value;
        } finally {
            ws.close();
        }
    }

    private async getJson<T>(path: string): Promise<T> {
        const r = await fetch(`${this.baseUrl}${path}`);
        if (!r.ok) throw new Error(`GET ${path} failed: HTTP ${r.status}`);
        return r.json() as Promise<T>;
    }
}
