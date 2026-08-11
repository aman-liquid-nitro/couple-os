#!/usr/bin/env bash
# ============================================================================
# Couple OS — row-level security under concurrent, pooled connections
# Date:    2026-08-11
# Governs: ADR 0005
#
# data/rls-tests.sql proves the policies are correct. It cannot prove they hold
# when many requests for different couples interleave across a small set of
# REUSED connections, which is what a connection pool actually does. That is
# the failure this file exists to catch.
#
#   ./data/rls-concurrency.sh                 # 16 clients x 100 txns = 1600
#   CLIENTS=32 TXNS=200 ./data/rls-concurrency.sh
#
# Requires psql and pgbench. Run against a database already loaded with
# data/schema.sql and data/rls-tests.sql (which creates the fixtures).
# Exits non-zero on any leak. Silence is success.
# ============================================================================
set -euo pipefail

DB="${PGDATABASE:-coupleos}"
CLIENTS="${CLIENTS:-16}"
JOBS="${JOBS:-4}"
TXNS="${TXNS:-100}"
APPUSER="${APPUSER:-app_user}"

C1='c1111111-1111-1111-1111-111111111111'
C2='c2222222-2222-2222-2222-222222222222'
UA='11111111-1111-1111-1111-111111111111'
UC='33333333-3333-3333-3333-333333333333'

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

psql -q -d "$DB" <<SQL
DROP TABLE IF EXISTS probe;
CREATE TABLE probe (
  id serial primary key,
  scoped_couple text NOT NULL,
  visible_total int  NOT NULL,
  foreign_rows  int  NOT NULL
);
ALTER TABLE probe DISABLE ROW LEVEL SECURITY;
GRANT ALL ON probe TO $APPUSER;
GRANT USAGE, SELECT ON SEQUENCE probe_id_seq TO $APPUSER;
SQL

# Each script records what it could see. foreign_rows must always be zero.
cat > "$TMP/c1.sql" <<SQL
BEGIN;
SET LOCAL app.current_couple_id = '$C1';
SET LOCAL app.current_user_id   = '$UA';
INSERT INTO probe (scoped_couple, visible_total, foreign_rows)
SELECT 'C1', count(*), count(*) FILTER (WHERE content LIKE 'C2%') FROM memories;
COMMIT;
SQL
cat > "$TMP/c2.sql" <<SQL
BEGIN;
SET LOCAL app.current_couple_id = '$C2';
SET LOCAL app.current_user_id   = '$UC';
INSERT INTO probe (scoped_couple, visible_total, foreign_rows)
SELECT 'C2', count(*), count(*) FILTER (WHERE content LIKE 'C1%') FROM memories;
COMMIT;
SQL

# -M prepared matches Npgsql, which prepares statements by default.
pgbench -d "$DB" -U "$APPUSER" -n -M prepared \
        -c "$CLIENTS" -j "$JOBS" -t "$TXNS" \
        -f "$TMP/c1.sql@1" -f "$TMP/c2.sql@1" \
  | grep -E "processed|failed"

psql -q -d "$DB" -v ON_ERROR_STOP=1 <<'SQL'
DO $$
DECLARE leaks int; wrong1 int; wrong2 int; total int;
BEGIN
    SELECT count(*) INTO total  FROM probe;
    SELECT count(*) INTO leaks  FROM probe WHERE foreign_rows > 0;
    SELECT count(*) INTO wrong1 FROM probe WHERE scoped_couple='C1' AND visible_total <> 2;
    SELECT count(*) INTO wrong2 FROM probe WHERE scoped_couple='C2' AND visible_total <> 1;

    IF leaks > 0 THEN
        RAISE EXCEPTION 'FAIL — % of % transactions saw another couple''s rows', leaks, total;
    END IF;
    IF wrong1 > 0 OR wrong2 > 0 THEN
        RAISE EXCEPTION 'FAIL — % transactions saw the wrong row count', wrong1 + wrong2;
    END IF;
    RAISE NOTICE 'PASS — % interleaved transactions, 0 cross-couple leaks', total;
END $$;
SQL
