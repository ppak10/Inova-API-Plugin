// Runnable demo of the TypeScript Inova client.
//
// Setup:
//     cd client/typescript
//     npm install
//
// Run:
//     npm run example

import { InovaClient } from "./client.js";

async function main(): Promise<void> {
    const client = new InovaClient();

    // One-shot endpoints
    console.log("ping:", await client.ping());

    const info = await client.info();
    console.log(`plugin: ${info.plugin} v${info.version} ` +
                `(uptime ${info.uptimeSeconds.toFixed(1)}s)`);

    const pos = (await client.movementPosition()).data;
    console.log(`position: x=${pos.x} y=${pos.y} z1=${pos.z1.toFixed(2)} ` +
                `z2=${pos.z2} r=${pos.r}`);

    const lights = (await client.lightsState()).data;
    console.log(`lights: enabled=${lights.isEnabled} count=${lights.lightCount}`);

    const snapshot = await client.stateSnapshot();
    const nTemps = snapshot.data.temperature.entries.length;
    const nPower = snapshot.data.power.entries.length;
    console.log(`snapshot: ${nTemps} temp sensors, ${nPower} power channels`);

    // Live stream — take 5 frames at 4 Hz and exit.
    console.log("\nstreaming /state/stream at 4 Hz (5 frames)...");
    let count = 0;
    for await (const frame of client.streamState(4)) {
        const p = frame.data.position;
        console.log(`  [${frame.respondedAt.slice(11, 23)}] ` +
                    `x=${p.x} y=${p.y} z1=${p.z1.toFixed(2)}`);
        if (++count >= 5) break;
    }
}

main().catch((err) => {
    console.error(err);
    process.exit(1);
});
