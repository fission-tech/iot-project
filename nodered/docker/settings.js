// Node-RED settings for the Docker image. Paths, identity and broker defaults are the flow's tab
// environment variables, set for the container by prepare-flows.js at build time.
module.exports = {
    flowFile: "flows.json",
    flowFilePretty: true,
    // The flow stores no credentials; set NODE_RED_CREDENTIAL_SECRET if you add nodes that do.
    credentialSecret: process.env.NODE_RED_CREDENTIAL_SECRET || false,
    uiPort: 1880,
    // Function nodes load mqtt, sqlite3 and modbus-serial, installed into the image. Nothing is installed at runtime.
    functionExternalModules: true,
    externalModules: { modules: { allowInstall: false }, palette: { allowInstall: false } },
    logging: { console: { level: "info", metrics: false, audit: false } },
    editorTheme: { projects: { enabled: false } }
};
