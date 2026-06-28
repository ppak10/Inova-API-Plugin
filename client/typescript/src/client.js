// TypeScript client for the Inova-API-Plugin.
//
// Covers all current endpoints — HTTP GETs plus the /state/stream WebSocket.
// Imports WebSocket from "ws" because Node added native WebSocket only in v22.
// In a browser, replace the import with the global WebSocket.
import WebSocket from "ws";
const DEFAULT_BASE_URL = process.env.INOVA_API_BASE_URL ?? "http://192.168.1.146:5001";
// ---- Client -------------------------------------------------------------
export class InovaClient {
    baseUrl;
    constructor(baseUrl = DEFAULT_BASE_URL) {
        this.baseUrl = baseUrl.replace(/\/$/, "");
    }
    async ping() {
        const r = await fetch(`${this.baseUrl}/ping`);
        if (!r.ok)
            throw new Error(`ping failed: HTTP ${r.status}`);
        return r.text();
    }
    info() {
        return this.getJson("/info");
    }
    movementPosition() {
        return this.getJson("/movement/position");
    }
    lightsState() {
        return this.getJson("/lights/state");
    }
    powerCurrent() {
        return this.getJson("/power/current");
    }
    temperatureCurrent() {
        return this.getJson("/temperature/current");
    }
    bedMatrix() {
        return this.getJson("/temperature/bedmatrix");
    }
    plotterInfo() {
        return this.getJson("/plotter/info");
    }
    plotterMask() {
        return this.getJson("/plotter/mask");
    }
    stateSnapshot() {
        return this.getJson("/state/snapshot");
    }
    /**
     * Yields decoded JSON frames from /movement/position/stream. Without `hz`,
     * the firmware emits at its native ~1 kHz; with `hz`, sends are decimated
     * server-side (no client-side dropping). Each frame is `Timed<PositionHighFrequency>`.
     */
    async *streamPosition(hz) {
        const path = hz === undefined ? "/movement/position/stream" : `/movement/position/stream?hz=${hz}`;
        for await (const frame of this.streamWs(path))
            yield frame;
    }
    /**
     * Yields decoded JSON frames from /temperature/bedmatrix/stream. The thermal
     * camera reports at ~6 Hz natively; `hz` decimates further (clamped 1..60).
     * Each frame is `Timed<TemperatureMatrix>` — frames with a null BedMatrix
     * are dropped server-side and never yielded.
     */
    async *streamBedMatrix(hz) {
        const path = hz === undefined ? "/temperature/bedmatrix/stream" : `/temperature/bedmatrix/stream?hz=${hz}`;
        for await (const frame of this.streamWs(path))
            yield frame;
    }
    /**
     * Yields decoded JSON frames from /state/stream until the consumer breaks
     * out of the loop or the socket closes. Each frame is `Timed<StateSnapshot>`.
     */
    async *streamState(hz = 100) {
        for await (const frame of this.streamWs(`/state/stream?hz=${hz}`))
            yield frame;
    }
    async *streamWs(path) {
        const wsUrl = this.baseUrl
            .replace(/^http:\/\//, "ws://")
            .replace(/^https:\/\//, "wss://");
        const ws = new WebSocket(`${wsUrl}${path}`);
        // Bridge the event-based WebSocket to an async iterator via a queue.
        const queue = [];
        const waiters = [];
        let closed = false;
        const closeReason = { value: null };
        ws.on("message", (raw) => {
            const text = typeof raw === "string" ? raw : raw.toString("utf-8");
            const waiter = waiters.shift();
            if (waiter)
                waiter(text);
            else
                queue.push(text);
        });
        ws.on("close", () => { closed = true; waiters.forEach(w => w(null)); waiters.length = 0; });
        ws.on("error", (err) => { closeReason.value = err; closed = true; waiters.forEach(w => w(null)); waiters.length = 0; });
        try {
            await new Promise((resolve, reject) => {
                ws.once("open", () => resolve());
                ws.once("error", reject);
            });
            while (!closed) {
                const text = queue.shift() ?? await new Promise(r => waiters.push(r));
                if (text === null)
                    break;
                yield JSON.parse(text);
            }
            if (closeReason.value)
                throw closeReason.value;
        }
        finally {
            ws.close();
        }
    }
    async getJson(path) {
        const r = await fetch(`${this.baseUrl}${path}`);
        if (!r.ok)
            throw new Error(`GET ${path} failed: HTTP ${r.status}`);
        return r.json();
    }
}
