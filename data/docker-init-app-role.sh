#!/bin/bash
# ============================================================================
# Creates the non-superuser role the application connects as.
#
# ADR 0005 enforces visibility with row-level security. Superusers and table
# owners bypass RLS, so an application that connects as `postgres` has every
# policy in data/schema.sql silently disabled. This role is the enforcement
# boundary; treat a connection string naming any other role as a bug.
#
# Runs once, on first container start, after 01-schema.sql.
# ============================================================================
set -euo pipefail

APP_DB_USER="${APP_DB_USER:-app_user}"
APP_DB_PASSWORD="${APP_DB_PASSWORD:-dev_app}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
DO \$\$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${APP_DB_USER}') THEN
        CREATE ROLE ${APP_DB_USER} LOGIN PASSWORD '${APP_DB_PASSWORD}';
    END IF;
END \$\$;

-- Explicitly NOSUPERUSER and NOBYPASSRLS. Either would void every policy.
ALTER ROLE ${APP_DB_USER} NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;

GRANT USAGE ON SCHEMA public TO ${APP_DB_USER};
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ${APP_DB_USER};
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ${APP_DB_USER};

-- Tables created by later EF Core migrations inherit the same grants.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ${APP_DB_USER};
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO ${APP_DB_USER};
SQL

echo "app role ${APP_DB_USER} created (NOSUPERUSER, NOBYPASSRLS)"
