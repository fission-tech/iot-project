// Simulated plant for staging devices, which have no real Modbus devices: a pump (unit 1) and a tank (unit 2)
// whose values change over time, so the gateway publishes readings worth looking at.
// Usage: node simulator.js [port]   (default 1502). seed.js registers the matching devices with the gateway.
const { createSim } = require("../../test/helpers/modbus-sim");

const port = Number(process.argv[2] || 1502);
const pump = { holding: {}, input: {}, coils: {}, discrete: {} };
const tank = { holding: {}, discrete: {} };
const started = Date.now();

function float32Words(value) {
    const buffer = Buffer.alloc(4);
    buffer.writeFloatBE(value);
    return [buffer.readUInt16BE(0), buffer.readUInt16BE(2)];
}

// Values a given time after the start; exported so the tests can check them without a server.
function plantAt(elapsedMs) {
    const minutes = elapsedMs / 60000;
    // The pump stops for the last minute of every 10, which shows up as zero flow and a falling temperature.
    const running = minutes % 10 < 9;
    const temperature = 22 + (running ? 3 : 0) + 1.5 * Math.sin(minutes * 2 * Math.PI / 5);
    const flow = running ? 12 + 1.5 * Math.sin(minutes * 2 * Math.PI / 3) : 0;
    // The tank fills over 15 minutes and empties over 15, between 20 and 90 %.
    const phase = (minutes % 30) / 15;
    const level = 20 + 70 * (phase <= 1 ? phase : 2 - phase);
    return { running, temperature, flow, level, runMinutes: Math.floor(minutes * 0.9) };
}

function update() {
    const plant = plantAt(Date.now() - started);
    pump.holding[0] = Math.round(plant.temperature * 10) & 0xFFFF;
    [pump.holding[1], pump.holding[2]] = float32Words(plant.flow);
    pump.input[0] = plant.runMinutes & 0xFFFF;
    pump.coils[0] = plant.running ? 1 : 0;
    tank.holding[0] = Math.round(plant.level * 10);
    tank.discrete[0] = plant.level > 85 ? 1 : 0;
}

if (require.main === module) {
    update();
    setInterval(update, 1000);
    const sim = createSim({ port, units: { 1: pump, 2: tank }, record: false });
    sim.start().then(() => {
        console.log("Simulated plant on " + (process.env.E2E_LISTEN || "127.0.0.1") + ":" + port + ": pump unit 1, tank unit 2");
    }, error => {
        console.error("Could not listen on port " + port + ": " + error.message);
        process.exit(1);
    });
}

module.exports = { plantAt };
