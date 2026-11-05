-- Lifecycle times represent observation time, separately from measurement timestamps.
-- Existing events cannot be accurately retimed because their arrival times were not stored.
CREATE SEQUENCE IF NOT EXISTS device_event_order;
ALTER TABLE device_events ADD COLUMN IF NOT EXISTS event_order bigint NOT NULL DEFAULT nextval('device_event_order');
ALTER TABLE device_events ADD COLUMN IF NOT EXISTS bd_seq bigint;
CREATE INDEX IF NOT EXISTS device_events_node_order ON device_events (node, event_order DESC);
