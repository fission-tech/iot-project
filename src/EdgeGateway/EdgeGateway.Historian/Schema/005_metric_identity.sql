-- Permanent metric identity. readings.metric and metric_catalog.metric now hold a metric's permanent key, which never
-- changes; metric_catalog.name is its current name, which a rename changes. A metric that already had history keeps its
-- name as its key, so no stored reading is rewritten. New metrics from the registry are keyed by their registry ID.
ALTER TABLE metric_catalog ADD COLUMN IF NOT EXISTS name text;
UPDATE metric_catalog SET name = metric WHERE name IS NULL;
ALTER TABLE metric_catalog ALTER COLUMN name SET NOT NULL;

-- The registry's permanent ID, from the DBIRTH metric property "metricId" or the registry itself.
ALTER TABLE metric_catalog ADD COLUMN IF NOT EXISTS registry_id text;
CREATE UNIQUE INDEX IF NOT EXISTS metric_catalog_registry_id ON metric_catalog (node, device, registry_id);

-- Every name a key has had. DDATA carries only names, and the gateway replays readings under the name they had
-- when taken, so an old name must still find its key after a rename. A reused name points to its newest owner.
CREATE TABLE IF NOT EXISTS metric_names (
    node text NOT NULL,
    device text NOT NULL,
    name text NOT NULL,
    metric text NOT NULL,
    PRIMARY KEY (node, device, name)
);

-- Metrics with history but no catalog row (removed before the catalog existed). Dashboards join the catalog for
-- names, so every stored metric needs a row.
INSERT INTO metric_catalog (node, device, metric, name, kind, in_registry, first_seen, last_seen)
SELECT node, device, metric, metric, 'numeric', false, min(time), max(time)
FROM readings
GROUP BY node, device, metric
ON CONFLICT DO NOTHING;

INSERT INTO metric_names (node, device, name, metric)
SELECT node, device, name, metric FROM metric_catalog
ON CONFLICT DO NOTHING;
