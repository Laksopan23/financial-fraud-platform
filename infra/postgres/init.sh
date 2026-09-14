#!/usr/bin/env bash
set -euo pipefail
psql --username "$POSTGRES_USER" --dbname postgres \
  --set=ingestion_password="$INGESTION_DB_PASSWORD" \
  --set=detection_password="$DETECTION_DB_PASSWORD" \
  --set=audit_password="$AUDIT_DB_PASSWORD" \
  --set=dashboard_password="$DASHBOARD_DB_PASSWORD" \
  --set=keycloak_password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
CREATE ROLE ingestion LOGIN PASSWORD :'ingestion_password';
CREATE ROLE detection LOGIN PASSWORD :'detection_password';
CREATE ROLE audit LOGIN PASSWORD :'audit_password';
CREATE ROLE dashboard LOGIN PASSWORD :'dashboard_password';
CREATE ROLE keycloak LOGIN PASSWORD :'keycloak_password';
CREATE DATABASE ingestion OWNER ingestion;
CREATE DATABASE detection OWNER detection;
CREATE DATABASE audit OWNER audit;
CREATE DATABASE dashboard OWNER dashboard;
CREATE DATABASE keycloak OWNER keycloak;
REVOKE CONNECT ON DATABASE ingestion, detection, audit, dashboard, keycloak FROM PUBLIC;
GRANT CONNECT ON DATABASE ingestion TO ingestion;
GRANT CONNECT ON DATABASE detection TO detection;
GRANT CONNECT ON DATABASE audit TO audit;
GRANT CONNECT ON DATABASE dashboard TO dashboard;
GRANT CONNECT ON DATABASE keycloak TO keycloak;
SQL
