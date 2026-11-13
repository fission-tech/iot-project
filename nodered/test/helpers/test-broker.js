// An in-process MQTT broker the tests can stop and start, to simulate broker outages.
// It records every telemetry message the gateway publishes, server side, so replays are seen even before a subscriber reconnects.
const net = require("node:net");
const { Aedes } = require("aedes");

function createTestBroker(port) {
    const published = [];
    const sockets = new Set();
    let aedes = null;
    let server = null;

    async function start() {
        aedes = await Aedes.createBroker();
        // Includes messages the broker publishes on a client's behalf, such as its Last Will.
        aedes.on("publish", packet => {
            if (!packet.topic.startsWith("iot/")) return;
            try { published.push({ topic: packet.topic, payload: JSON.parse(packet.payload.toString()), retain: !!packet.retain, receivedAt: Date.now() }); } catch { }
        });
        server = net.createServer(socket => {
            sockets.add(socket);
            socket.on("close", () => sockets.delete(socket));
            socket.on("error", () => {});
            aedes.handle(socket);
        });
        await new Promise((resolve, reject) => {
            server.once("error", reject);
            server.listen(port, process.env.E2E_LISTEN || "127.0.0.1", resolve);
        });
    }

    // Drops every client at once, like a broker crash or a network cut.
    async function stop() {
        for (const socket of sockets) socket.destroy();
        sockets.clear();
        if (server) await new Promise(resolve => server.close(() => resolve()));
        if (aedes) await new Promise(resolve => aedes.close(() => resolve()));
        server = null;
        aedes = null;
    }

    return { start, stop, published, get connectedClients() { return sockets.size; } };
}

module.exports = { createTestBroker };
