// Runs the test Modbus TCP server by hand and prints every request it receives.
// Usage: node sim.js [port]   (default 1502)
const { createSim } = require("./helpers/modbus-sim");

const port = Number(process.argv[2] || 1502);
const areas = { 1: "coil", 2: "discrete", 3: "holding", 4: "input" };
const units = {
    // Unit 3: one value in every area. Holding 1-2 is float32 12.5 (big-endian words).
    3: { holding: { 0: 212, 1: 0x4148, 2: 0x0000, 10: 0xFFFF }, input: { 0: 65535 }, coils: { 0: 1 }, discrete: { 0: 0 } },
    4: { holding: { 0: 77 } },
    7: "silent",     // accepts requests, never answers (times out)
    9: "exception"   // answers exception 11, like a gateway whose target is missing
};

const sim = createSim({
    port,
    units,
    onRequest: r => console.log(
        new Date(r.at).toISOString().slice(11, 23),
        "unit", String(r.uid).padEnd(3),
        (areas[r.fc] || "fc " + r.fc).padEnd(8),
        "address", String(r.address).padEnd(5),
        "count", String(r.quantity).padEnd(4),
        r.outcome,
        "| open connections:", sim.openConnections)
});

sim.start().then(() => {
    console.log("Modbus test server on " + (process.env.E2E_LISTEN || "127.0.0.1") + ":" + port);
    console.log("unit 3 holding 0=212 1-2=float 12.5 10=-1, input 0=65535, coil 0=1, discrete 0=0");
    console.log("unit 4 holding 0=77 | unit 7 never answers | unit 9 and unknown units: exception 11");
}, error => {
    console.error("Could not listen on port " + port + ": " + error.message);
    process.exit(1);
});
