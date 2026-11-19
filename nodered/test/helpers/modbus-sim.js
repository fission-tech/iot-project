// Minimal Modbus TCP server for tests: function codes 1-4, several unit IDs per port.
// A unit is either a register map, "exception" (answers 0x0B, gateway target failed), "illegal" (answers 0x02,
// illegal data address: the device itself replied) or "silent" (never answers).
// Unknown unit IDs also get exception 0x0B, like a TCP-to-RTU gateway.
const net = require("net");

// record: false keeps no request history, for a simulator that runs for days.
function createSim({ port, units, onRequest = () => {}, record = true }) {
    const sockets = new Set();
    const requests = [];
    let totalConnections = 0;
    let server = null;

    function reply(request) {
        const tid = request.readUInt16BE(0), uid = request[6], fc = request[7];
        const address = request.readUInt16BE(8), quantity = request.readUInt16BE(10);
        const unit = units[uid];
        const entry = { at: Date.now(), uid, fc, address, quantity,
            outcome: unit === "silent" ? "no reply" : unit === "illegal" ? "exception 2" : !unit || unit === "exception" ? "exception 11" : "ok" };
        if (record) requests.push(entry);
        onRequest(entry);
        if (unit === "silent") return null;
        let pdu;
        if (unit === "illegal") {
            pdu = Buffer.from([fc | 0x80, 0x02]);
        } else if (!unit || unit === "exception") {
            pdu = Buffer.from([fc | 0x80, 0x0B]);
        } else if (fc === 3 || fc === 4) {
            const table = (fc === 3 ? unit.holding : unit.input) || {};
            pdu = Buffer.alloc(2 + quantity * 2);
            pdu[0] = fc;
            pdu[1] = quantity * 2;
            for (let i = 0; i < quantity; i++) pdu.writeUInt16BE(table[address + i] || 0, 2 + i * 2);
        } else if (fc === 1 || fc === 2) {
            const table = (fc === 1 ? unit.coils : unit.discrete) || {};
            const bytes = Math.ceil(quantity / 8);
            pdu = Buffer.alloc(2 + bytes);
            pdu[0] = fc;
            pdu[1] = bytes;
            for (let i = 0; i < quantity; i++) if (table[address + i]) pdu[2 + (i >> 3)] |= 1 << (i & 7);
        } else {
            pdu = Buffer.from([fc | 0x80, 0x01]);
        }
        const header = Buffer.alloc(7);
        header.writeUInt16BE(tid, 0);
        header.writeUInt16BE(0, 2);
        header.writeUInt16BE(pdu.length + 1, 4);
        header[6] = uid;
        return Buffer.concat([header, pdu]);
    }

    function start() {
        return new Promise((resolve, reject) => {
            server = net.createServer(socket => {
                totalConnections++;
                sockets.add(socket);
                let pending = Buffer.alloc(0);
                socket.on("data", chunk => {
                    pending = Buffer.concat([pending, chunk]);
                    // Requests can arrive split or batched; each ADU is 6 bytes + its length field.
                    while (pending.length >= 7 && pending.length >= 6 + pending.readUInt16BE(4)) {
                        const size = 6 + pending.readUInt16BE(4);
                        const response = reply(pending.subarray(0, size));
                        pending = pending.subarray(size);
                        if (response) socket.write(response);
                    }
                });
                socket.on("close", () => sockets.delete(socket));
                socket.on("error", () => {});
            });
            server.once("error", reject);
            // In Docker the server must accept connections from the Node-RED container.
            server.listen(port, process.env.E2E_LISTEN || "127.0.0.1", resolve);
        });
    }

    // Closes the listener and drops every client, like a device losing power.
    function stop() {
        for (const socket of sockets) socket.destroy();
        sockets.clear();
        return new Promise(resolve => (server ? server.close(() => resolve()) : resolve()));
    }

    return {
        start,
        stop,
        requests,
        get openConnections() { return sockets.size; },
        get totalConnections() { return totalConnections; }
    };
}

module.exports = { createSim };
