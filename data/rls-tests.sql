-- ============================================================================
-- Couple OS — row-level security test harness
-- Date:    2026-08-11
-- Target:  PostgreSQL 16
-- Governs: ADR 0005 (visibility enforced at the database)
--
-- Run against a database freshly loaded from data/schema.sql:
--     psql -v ON_ERROR_STOP=1 -d coupleos -f data/rls-tests.sql
--
-- Exits non-zero on the first failed assertion. Silence is success.
--
-- This file is the specification that CoupleOS.IntegrationTests must port to
-- xUnit in Milestone M0. Every assertion here failed at least once against an
-- earlier version of the schema; none of them are hypothetical.
-- ============================================================================

\set ON_ERROR_STOP on
SET client_min_messages TO WARNING;
-- Assertions are silent; only the summary prints. A failure raises and stops.
\o /dev/null

-- ----------------------------------------------------------------------------
-- Harness
-- ----------------------------------------------------------------------------
DROP TABLE IF EXISTS t_results;
CREATE TABLE t_results (id serial primary key, label text, ok boolean, detail text);
ALTER TABLE t_results DISABLE ROW LEVEL SECURITY;

CREATE OR REPLACE FUNCTION t_eq(label text, actual bigint, expected bigint)
RETURNS void AS $$
BEGIN
    INSERT INTO t_results (label, ok, detail)
        VALUES (label, actual = expected, format('got %s, want %s', actual, expected));
    IF actual <> expected THEN
        RAISE EXCEPTION 'FAIL % — got %, want %', label, actual, expected;
    END IF;
END; $$ LANGUAGE plpgsql;

-- Asserts that a statement is REJECTED by a policy. A test that expects a
-- refusal must fail loudly if the refusal stops happening.
CREATE OR REPLACE FUNCTION t_blocked(label text, stmt text)
RETURNS void AS $$
BEGIN
    BEGIN
        EXECUTE stmt;
    EXCEPTION WHEN insufficient_privilege OR others THEN
        INSERT INTO t_results (label, ok, detail) VALUES (label, true, SQLERRM);
        RETURN;
    END;
    INSERT INTO t_results (label, ok, detail) VALUES (label, false, 'statement was ALLOWED');
    RAISE EXCEPTION 'FAIL % — statement was allowed but must be blocked', label;
END; $$ LANGUAGE plpgsql;

-- ----------------------------------------------------------------------------
-- Fixtures — two couples, three people, one secret each
-- ----------------------------------------------------------------------------
-- Idempotent: DROP ROLE fails if the role owns grants in any other database,
-- which makes a plain DROP/CREATE pair unusable on a shared cluster.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        CREATE ROLE app_user LOGIN NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;
ALTER ROLE app_user NOSUPERUSER NOBYPASSRLS;
GRANT USAGE ON SCHEMA public TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;

\set C1 '''c1111111-1111-1111-1111-111111111111'''
\set C2 '''c2222222-2222-2222-2222-222222222222'''
\set UA '''11111111-1111-1111-1111-111111111111'''
\set UB '''22222222-2222-2222-2222-222222222222'''
\set UC '''33333333-3333-3333-3333-333333333333'''

INSERT INTO users (id,email,display_name) VALUES
 (:UA,'a@x.com','Partner A'), (:UB,'b@x.com','Partner B'), (:UC,'c@x.com','Stranger C');
INSERT INTO couples (id,display_name) VALUES (:C1,'Couple One'), (:C2,'Couple Two');
INSERT INTO couple_members (couple_id,user_id) VALUES (:C1,:UA), (:C1,:UB), (:C2,:UC);

INSERT INTO memories (couple_id,owner_user_id,visibility,type,content) VALUES
 (:C1, NULL,'shared_couple','semantic','C1 SHARED'),
 (:C1, :UA,'private_user','episodic','C1 PRIVATE-A: necklace for the anniversary'),
 (:C1, :UB,'private_user','episodic','C1 PRIVATE-B: surprise trip'),
 (:C2, NULL,'shared_couple','semantic','C2 SHARED');

-- An audit row whose after_state quotes a private memory verbatim.
INSERT INTO audit_logs (couple_id,user_id,owner_user_id,visibility,action,entity_type,entity_id,after_state)
SELECT :C1, :UA, m.owner_user_id, m.visibility, 'create', 'memory', m.id,
       jsonb_build_object('content', m.content)
  FROM memories m WHERE m.content LIKE '%necklace%';

INSERT INTO goals (id,couple_id,visibility,name) VALUES
 ('9111e111-1111-1111-1111-111111111111',:C1,'shared_couple','C1 Goal');
INSERT INTO goal_transactions (goal_id,amount,note) VALUES
 ('9111e111-1111-1111-1111-111111111111',5000,'C1 SECRET: saving for the ring');
INSERT INTO plans (id,couple_id,visibility,name) VALUES
 ('9222e222-2222-2222-2222-222222222222',:C1,'shared_couple','C1 Plan');
INSERT INTO plan_items (plan_id,entity_type,label) VALUES
 ('9222e222-2222-2222-2222-222222222222','note','C1 SECRET: proposal at the lake');

-- ============================================================================
-- A. READ ISOLATION
-- ============================================================================
BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '11111111-1111-1111-1111-111111111111';
SELECT t_eq('A1 partner A sees shared + own private only',
            (SELECT count(*) FROM memories), 2);
SELECT t_eq('A2 partner A cannot see partner B private',
            (SELECT count(*) FROM memories WHERE content LIKE '%PRIVATE-B%'), 0);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '22222222-2222-2222-2222-222222222222';
SELECT t_eq('A3 partner B cannot see the necklace (SPEC.md 19)',
            (SELECT count(*) FROM memories WHERE content LIKE '%necklace%'), 0);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c2222222-2222-2222-2222-222222222222';
SET LOCAL app.current_user_id   = '33333333-3333-3333-3333-333333333333';
SELECT t_eq('A4 stranger sees only their own couple',
            (SELECT count(*) FROM memories), 1);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SELECT t_eq('A5 no session variables set returns zero rows, not all rows',
            (SELECT count(*) FROM memories), 0);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SELECT t_eq('A6 couple set but user unset yields shared rows only',
            (SELECT count(*) FROM memories), 1);
COMMIT;

-- ============================================================================
-- B. WRITE PATH
-- ============================================================================
BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '11111111-1111-1111-1111-111111111111';
SELECT t_blocked('B1 cannot insert into another couple',
  $$INSERT INTO memories (couple_id,visibility,type,content)
    VALUES ('c2222222-2222-2222-2222-222222222222','shared_couple','semantic','INJECTED')$$);
SELECT t_blocked('B2 cannot forge a private memory owned by the partner',
  $$INSERT INTO memories (couple_id,owner_user_id,visibility,type,content)
    VALUES ('c1111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',
            'private_user','semantic','FORGED')$$);
SELECT t_blocked('B3 cannot move a row into another couple',
  $$UPDATE memories SET couple_id='c2222222-2222-2222-2222-222222222222'
    WHERE content = 'C1 SHARED'$$);
COMMIT;

-- Invisible rows are a SILENT no-op, not an error. Application code must check
-- affected-row counts and must never infer success from the absence of a throw.
BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '11111111-1111-1111-1111-111111111111';
WITH u AS (UPDATE memories SET content='TAMPERED'
           WHERE content LIKE '%PRIVATE-B%' RETURNING 1)
SELECT t_eq('B4 updating the partner private row affects zero rows',
            (SELECT count(*) FROM u), 0);
WITH d AS (DELETE FROM memories WHERE content LIKE '%PRIVATE-B%' RETURNING 1)
SELECT t_eq('B5 deleting the partner private row affects zero rows',
            (SELECT count(*) FROM d), 0);
COMMIT;

SELECT t_eq('B6 fixtures survived every write attempt',
            (SELECT count(*) FROM memories), 4);

-- ============================================================================
-- C. CONNECTION REUSE  — the reason this file exists
-- ============================================================================
BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '11111111-1111-1111-1111-111111111111';
SELECT t_eq('C1 SET LOCAL scopes correctly inside its transaction',
            (SELECT count(*) FROM memories), 2);
COMMIT;

-- Same connection, next transaction, application forgot to set anything.
BEGIN;
SET LOCAL ROLE app_user;
SELECT t_eq('C2 SET LOCAL does not survive COMMIT (pool-safe)',
            (SELECT count(*) FROM memories), 0);
COMMIT;

-- The hazard: plain SET survives COMMIT and leaks to the next request.
BEGIN;
SET LOCAL ROLE app_user;
SET app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET app.current_user_id   = '11111111-1111-1111-1111-111111111111';
SELECT 1;
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SELECT t_eq('C3 plain SET LEAKS across transactions — never use it',
            (SELECT count(*) FROM memories), 2);
COMMIT;

DISCARD ALL;
BEGIN;
SET LOCAL ROLE app_user;
SELECT t_eq('C4 DISCARD ALL clears a leaked session variable',
            (SELECT count(*) FROM memories), 0);
COMMIT;

-- ============================================================================
-- D. TABLES THAT LEAKED BEFORE THESE POLICIES EXISTED
-- ============================================================================
BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c2222222-2222-2222-2222-222222222222';
SET LOCAL app.current_user_id   = '33333333-3333-3333-3333-333333333333';
SELECT t_eq('D1 audit_logs does not leak across couples',
            (SELECT count(*) FROM audit_logs), 0);
SELECT t_eq('D2 goal_transactions does not leak across couples',
            (SELECT count(*) FROM goal_transactions), 0);
SELECT t_eq('D3 plan_items does not leak across couples',
            (SELECT count(*) FROM plan_items), 0);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '22222222-2222-2222-2222-222222222222';
SELECT t_eq('D4 partner B cannot read the audit trail of A private memory',
            (SELECT count(*) FROM audit_logs), 0);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '11111111-1111-1111-1111-111111111111';
SELECT t_eq('D5 owner still reads their own audit trail',
            (SELECT count(*) FROM audit_logs), 1);
SELECT t_eq('D6 own couple child rows remain visible (no false positives)',
            (SELECT count(*) FROM goal_transactions) + (SELECT count(*) FROM plan_items), 2);
COMMIT;

-- ============================================================================
-- E. THE APPLICATION ROLE CANNOT DISARM ANY OF THIS
-- ============================================================================
BEGIN;
SET LOCAL ROLE app_user;
SELECT t_blocked('E1 app role cannot disable RLS',
                 'ALTER TABLE memories DISABLE ROW LEVEL SECURITY');
SELECT t_blocked('E2 app role cannot drop FORCE',
                 'ALTER TABLE memories NO FORCE ROW LEVEL SECURITY');
SELECT t_blocked('E3 app role cannot write its own policy',
                 'CREATE POLICY evil ON memories USING (true)');
COMMIT;

SELECT t_eq('E4 app role holds neither SUPERUSER nor BYPASSRLS',
            (SELECT count(*) FROM pg_roles
             WHERE rolname='app_user' AND (rolsuper OR rolbypassrls)), 0);

-- ============================================================================
-- E5. Every couple-scoped table must be armed. This is the assertion that
--     catches a future migration adding a table and forgetting the policy.
-- ============================================================================
SELECT t_eq('E5 every table with couple_id has RLS, except the documented three',
    (SELECT count(*) FROM pg_class c
       JOIN pg_namespace n ON n.oid=c.relnamespace
       JOIN information_schema.columns col
         ON col.table_name=c.relname AND col.column_name='couple_id'
        AND col.table_schema='public'
      WHERE n.nspname='public' AND c.relkind='r' AND NOT c.relrowsecurity
        AND c.relname NOT IN ('users','couple_members','auth_tokens','expense_categories')
    ), 0);

-- ============================================================================
-- F. PREPARED STATEMENTS AND GENERIC PLANS
--
-- Npgsql prepares statements, and after five executions PostgreSQL may switch
-- to a generic plan. If the RLS predicate were bound at plan time rather than
-- execution time, a connection that served couple A would keep applying A's
-- filter for couple B. force_generic_plan makes that the guaranteed case
-- instead of a rare one.
-- ============================================================================
SET plan_cache_mode = force_generic_plan;
PREPARE mem_count AS SELECT count(*) FROM memories;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c1111111-1111-1111-1111-111111111111';
SET LOCAL app.current_user_id   = '11111111-1111-1111-1111-111111111111';
-- Six executions: enough to force the generic plan to be built and reused.
EXECUTE mem_count; EXECUTE mem_count; EXECUTE mem_count;
EXECUTE mem_count; EXECUTE mem_count;
SELECT t_eq('F1 generic plan, partner A', (SELECT count(*) FROM memories), 2);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SET LOCAL app.current_couple_id = 'c2222222-2222-2222-2222-222222222222';
SET LOCAL app.current_user_id   = '33333333-3333-3333-3333-333333333333';
SELECT t_eq('F2 same prepared statement does not reuse the previous couple filter',
            (SELECT count(*) FROM memories), 1);
COMMIT;

BEGIN;
SET LOCAL ROLE app_user;
SELECT t_eq('F3 generic plan still fails closed when nothing is set',
            (SELECT count(*) FROM memories), 0);
COMMIT;
RESET plan_cache_mode;
DEALLOCATE mem_count;

-- ============================================================================
-- G. AUDIT VISIBILITY MUST BE STATED, NEVER ASSUMED
--
-- audit_logs.before_state/after_state quote entity bodies verbatim, so an audit
-- row is exactly as sensitive as the row it describes. It therefore has no
-- default visibility: code that forgets must fail at write time rather than
-- disclose at read time.
-- ============================================================================
SELECT t_blocked('G1 audit row without visibility is rejected at write time',
  $$INSERT INTO audit_logs (couple_id,user_id,action,entity_type)
    VALUES ('c1111111-1111-1111-1111-111111111111',
            '11111111-1111-1111-1111-111111111111','create','memory')$$);

SELECT t_blocked('G2 private audit row without an owner is rejected',
  $$INSERT INTO audit_logs (couple_id,user_id,visibility,action,entity_type)
    VALUES ('c1111111-1111-1111-1111-111111111111',
            '11111111-1111-1111-1111-111111111111','private_user','create','memory')$$);

-- The invariant itself: every audit row describing a memory agrees with that
-- memory's visibility and owner. Checked as superuser so RLS cannot hide a
-- mismatch from the assertion.
SELECT t_eq('G3 audit rows agree with the memory they describe',
    (SELECT count(*) FROM audit_logs a JOIN memories m ON m.id = a.entity_id
      WHERE a.entity_type = 'memory'
        AND (a.visibility IS DISTINCT FROM m.visibility
             OR a.owner_user_id IS DISTINCT FROM m.owner_user_id)), 0);

-- ----------------------------------------------------------------------------
\o
\echo ''
\echo '================ RLS TEST SUMMARY ================'
SELECT format('%-62s %s', label, CASE WHEN ok THEN 'PASS' ELSE 'FAIL' END) FROM t_results ORDER BY id;
SELECT format('%s assertions, %s passed, %s failed',
              count(*), count(*) FILTER (WHERE ok), count(*) FILTER (WHERE NOT ok)) FROM t_results;
