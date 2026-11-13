// Runs Function node code straight from flows.json, so the tests exercise the flow as deployed.
const fs = require("fs");
const path = require("path");

const flowsPath = path.join(__dirname, "..", "..", "flows.json");
const flows = JSON.parse(fs.readFileSync(flowsPath, "utf8"));

function functionNode(id) {
    const node = flows.find(n => n.id === id && n.type === "function");
    if (!node) throw new Error("No function node " + id + " in flows.json");
    return node;
}

// A stand-in for Node-RED's flow/context stores (plain in-memory map).
function store(initial = {}) {
    const values = { ...initial };
    return {
        get: key => values[key],
        set: (key, value) => { values[key] = value; },
        values
    };
}

function fakeNode() {
    const warnings = [];
    const errors = [];
    return {
        warnings,
        errors,
        logs: [],
        log(text) { this.logs.push(text); },
        warn: text => warnings.push(text),
        error: (text) => errors.push(text),
        status: () => {},
        send: () => {}
    };
}

// Runs one node's code (func, initialize or finalize) the way Node-RED does: as a function body.
function run(id, msg, { flow = store(), context = store(), global = store(), node = fakeNode(), now = Date.now(), part = "func", env = {} } = {}) {
    const code = functionNode(id)[part];
    // Shadow Date so tests control the clock; everything else behaves like the real Date.
    const FakeDate = class extends Date {
        constructor(...args) { super(...(args.length ? args : [now])); }
        static now() { return now; }
    };
    const body = new Function("msg", "node", "flow", "context", "global", "Date", "Buffer", "env", code);
    const result = body(structuredClone(msg ?? {}), node, flow, context, global, FakeDate, Buffer, { get: name => env[name] });
    return { result, flow, context, global, node };
}

module.exports = { flows, functionNode, store, fakeNode, run };
