// Node-RED settings for the Docker image. Paths, identity and broker defaults are the flow's tab
// environment variables, set for the container by prepare-flows.js at build time and the Dockerfile's ENV.
const crypto = require("crypto");

// Extra gateways on one device (qbee-deploy.js --instances) get their number appended to GATEWAY_ID.
// deploy/simulator/seed.js applies the same rule.
const instance = Number(process.env.GATEWAY_INSTANCE || 1);
if (instance > 1) process.env.GATEWAY_ID = (process.env.GATEWAY_ID || "nodered-gateway-01") + "-" + instance;

// Logins are on when NODE_RED_PASSWORD is set (on devices), and off otherwise (local runs and the tests).
// One account, NODE_RED_USER (default "admin"), opens both the editor and the registry page and API.
const user = process.env.NODE_RED_USER || "admin";
const password = process.env.NODE_RED_PASSWORD || "";

function matches(givenUser, givenPassword) {
    // Compare digests so the comparison takes the same time whatever the input length.
    const digest = value => crypto.createHash("sha256").update(String(value ?? "")).digest();
    const userOk = crypto.timingSafeEqual(digest(givenUser), digest(user));
    const passwordOk = crypto.timingSafeEqual(digest(givenPassword), digest(password));
    return userOk && passwordOk;
}

// The registry page and its API (http-in nodes) use HTTP basic authentication.
function httpNodeLogin(req, res, next) {
    const header = req.headers.authorization || "";
    const [scheme, encoded] = header.split(" ");
    if (scheme === "Basic" && encoded) {
        const decoded = Buffer.from(encoded, "base64").toString("utf8");
        const colon = decoded.indexOf(":");
        if (colon > 0 && matches(decoded.slice(0, colon), decoded.slice(colon + 1))) return next();
    }
    res.set("WWW-Authenticate", 'Basic realm="Node-RED gateway", charset="UTF-8"');
    res.status(401).send("Login required");
}

module.exports = {
    flowFile: "flows.json",
    flowFilePretty: true,
    // The flow stores no credentials; set NODE_RED_CREDENTIAL_SECRET if you add nodes that do.
    credentialSecret: process.env.NODE_RED_CREDENTIAL_SECRET || false,
    uiPort: 1880,
    adminAuth: password ? {
        type: "credentials",
        users: username => Promise.resolve(username === user ? { username: user, permissions: "*" } : null),
        authenticate: (username, given) => Promise.resolve(matches(username, given) ? { username: user, permissions: "*" } : null)
    } : undefined,
    httpNodeMiddleware: password ? httpNodeLogin : undefined,
    // Function nodes load mqtt, sqlite3 and modbus-serial, installed into the image. Nothing is installed at runtime.
    functionExternalModules: true,
    externalModules: { modules: { allowInstall: false }, palette: { allowInstall: false } },
    logging: { console: { level: "info", metrics: false, audit: false } },
    editorTheme: { projects: { enabled: false } }
};
