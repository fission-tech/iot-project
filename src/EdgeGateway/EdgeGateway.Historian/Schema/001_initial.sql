-- Reading history. Applied once by the historian and recorded in schema_version.
-- Each statement runs on its own: continuous aggregates cannot be created inside a transaction.
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS readings (
    time timestamptz NOT NULL,
    node text NOT NULL,
    device text NOT NULL,
    metric text NOT NULL,
    value double precision,
    quality text NOT NULL,
    historical boolean NOT NULL,
    device_seq bigint,
    -- Includes time, as a hypertable requires. Redelivered and replayed readings hit this key and are skipped.
    PRIMARY KEY (node, device, metric, time)
);

CREATE TABLE IF NOT EXISTS device_events (
    time timestamptz NOT NULL,
    node text NOT NULL,
    device text NOT NULL,
    kind text NOT NULL,
    PRIMARY KEY (node, device, kind, time)
);

SELECT create_hypertable('readings', by_range('time', INTERVAL '1 day'), if_not_exists => TRUE);

SELECT create_hypertable('device_events', by_range('time', INTERVAL '7 days'), if_not_exists => TRUE);

-- Compressed chunks are stored per series, so one device's chart reads only its own data.
ALTER TABLE readings SET (timescaledb.compress, timescaledb.compress_segmentby = 'node, device, metric',
    timescaledb.compress_orderby = 'time DESC');

-- One-minute rollups for long time ranges. Refreshed every minute over the last 7 days,
-- which also picks up readings the gateway replays late from its outbox.
CREATE MATERIALIZED VIEW IF NOT EXISTS readings_1m WITH (timescaledb.continuous) AS
    SELECT time_bucket(INTERVAL '1 minute', time) AS bucket, node, device, metric,
           avg(value) AS avg, min(value) AS min, max(value) AS max, count(*) AS samples,
           count(*) FILTER (WHERE quality <> 'good') AS bad_samples
    FROM readings
    GROUP BY bucket, node, device, metric
    WITH NO DATA;

SELECT add_continuous_aggregate_policy('readings_1m', start_offset => INTERVAL '7 days',
    end_offset => INTERVAL '1 minute', schedule_interval => INTERVAL '1 minute', if_not_exists => TRUE);
