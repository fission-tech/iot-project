-- 002 may already be applied locally. Remove its optional session tracking without deleting history.
ALTER TABLE device_events DROP COLUMN IF EXISTS bd_seq;
DROP INDEX IF EXISTS device_events_node_order;
