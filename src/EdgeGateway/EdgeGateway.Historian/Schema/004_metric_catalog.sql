-- What each device publishes, for the generated per-device Grafana dashboards. Births add rows; the device
-- registry fills in register area, data type, unit and scale. Rows are kept after a device or metric is removed,
-- so its history stays viewable.
CREATE TABLE IF NOT EXISTS device_catalog (
    node text NOT NULL,
    device text NOT NULL,
    -- Grafana dashboard UID, so the overview can link to the device dashboard.
    uid text NOT NULL,
    in_registry boolean NOT NULL DEFAULT true,
    first_seen timestamptz NOT NULL,
    last_seen timestamptz NOT NULL,
    PRIMARY KEY (node, device)
);

CREATE TABLE IF NOT EXISTS metric_catalog (
    node text NOT NULL,
    device text NOT NULL,
    metric text NOT NULL,
    -- From the Sparkplug datatype in the birth: 'boolean' or 'numeric'.
    kind text NOT NULL,
    -- From the device registry; NULL until it has been reachable.
    register text,
    data_type text,
    unit text,
    scale double precision,
    in_registry boolean NOT NULL DEFAULT true,
    first_seen timestamptz NOT NULL,
    last_seen timestamptz NOT NULL,
    PRIMARY KEY (node, device, metric)
);
