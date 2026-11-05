#!/bin/sh
# Runs once when the TimescaleDB volume is created. Grafana connects as grafana_reader, which can only read;
# the historian grants it SELECT on the history tables when it creates them.
set -e
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v password="$GRAFANA_DB_PASSWORD" <<'SQL'
CREATE ROLE grafana_reader LOGIN PASSWORD :'password';
GRANT CONNECT ON DATABASE :"DBNAME" TO grafana_reader;
GRANT USAGE ON SCHEMA public TO grafana_reader;
SQL
