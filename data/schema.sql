-- ============================================================================
-- Couple OS — database schema
-- Date:    2026-08-11
-- Target:  PostgreSQL 16
-- Governs: ADR 0002 (pgvector), ADR 0005 (visibility), ADR 0006 (memory)
--
-- Reference schema. EF Core migrations are the source of truth in the repo;
-- this file exists so the shape can be read and argued about in one place.
--
-- Tables marked [V0] ship in the prototype. The rest are created by the same
-- initial migration but are unused until their milestone — creating them early
-- costs nothing and keeps foreign keys honest.
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";
CREATE EXTENSION IF NOT EXISTS "vector";

-- ============================================================================
-- ENUMS
-- ============================================================================

-- ADR 0005: three values, superseding SPEC.md §9's two-value list.
CREATE TYPE visibility        AS ENUM ('private_user', 'shared_couple', 'system');

-- ADR 0006
CREATE TYPE memory_type       AS ENUM ('episodic','semantic','preference','decision',
                                       'commitment','plan','event','temporary_context');
CREATE TYPE memory_assertion  AS ENUM ('user_stated','user_confirmed','inferred','imported');
CREATE TYPE memory_status     AS ENUM ('active','superseded','archived');
CREATE TYPE data_source       AS ENUM ('user_input','chat','imported_document','manual',
                                       'system_inference');

CREATE TYPE task_kind         AS ENUM ('task','reminder','commitment');
CREATE TYPE task_status       AS ENUM ('todo','in_progress','done','cancelled','snoozed');
CREATE TYPE priority_level    AS ENUM ('low','normal','high');

CREATE TYPE shopping_status   AS ENUM ('needed','purchased','cancelled');
CREATE TYPE chore_status      AS ENUM ('pending','done','skipped');
CREATE TYPE goal_status       AS ENUM ('active','achieved','abandoned','paused');
CREATE TYPE plan_status       AS ENUM ('draft','active','completed','abandoned');

CREATE TYPE notification_type AS ENUM ('task_due','event_upcoming','bill_due','goal_update',
                                       'planning_prompt','prediction','important_memory',
                                       'follow_up');
CREATE TYPE notification_state AS ENUM ('pending','sent','dismissed','suppressed','expired');

CREATE TYPE intent_type       AS ENUM ('task','reminder','shopping_item','expense','event',
                                       'memory','goal','plan','decision','note','question',
                                       'unknown');
CREATE TYPE message_role      AS ENUM ('user','assistant','system','tool');
CREATE TYPE action_outcome    AS ENUM ('success','validation_failed','unauthorized',
                                       'execution_failed','cancelled_by_user');

-- ============================================================================
-- IDENTITY  [V0]
-- ============================================================================

CREATE TABLE users (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email         text        NOT NULL,
    display_name  text        NOT NULL,
    timezone      text        NOT NULL DEFAULT 'Asia/Kolkata',
    locale        text        NOT NULL DEFAULT 'en-IN',
    preferences   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX users_email_key ON users (lower(email));

CREATE TABLE couples (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    display_name            text,
    relationship_start_date date,
    anniversary_date        date,
    currency                char(3)     NOT NULL DEFAULT 'INR',
    timezone                text        NOT NULL DEFAULT 'Asia/Kolkata',
    settings                jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz NOT NULL DEFAULT now()
);

-- Join table rather than partner_a_id/partner_b_id (SPEC.md §6), so family mode
-- (§51) becomes a constraint change instead of a schema rewrite. V1 caps at two.
CREATE TABLE couple_members (
    couple_id  uuid NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    user_id    uuid NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    role       text NOT NULL DEFAULT 'partner',
    joined_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (couple_id, user_id)
);
CREATE UNIQUE INDEX couple_members_one_couple_per_user ON couple_members (user_id);

CREATE OR REPLACE FUNCTION enforce_two_member_couple() RETURNS trigger AS $$
BEGIN
    IF (SELECT count(*) FROM couple_members WHERE couple_id = NEW.couple_id) > 2 THEN
        RAISE EXCEPTION 'A couple may have at most two members (SPEC.md §6)';
    END IF;
    RETURN NEW;
END; $$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER couple_members_max_two
    AFTER INSERT ON couple_members DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION enforce_two_member_couple();

-- ADR 0007: magic links. Plaintext token never stored.
CREATE TABLE auth_tokens (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email        text        NOT NULL,
    token_hash   bytea       NOT NULL,
    purpose      text        NOT NULL DEFAULT 'sign_in',   -- sign_in | couple_invite
    couple_id    uuid REFERENCES couples(id) ON DELETE CASCADE,
    expires_at   timestamptz NOT NULL,
    consumed_at  timestamptz,
    created_ip   inet,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX auth_tokens_hash_key ON auth_tokens (token_hash);
CREATE INDEX auth_tokens_email_created ON auth_tokens (lower(email), created_at DESC);

CREATE TABLE sessions (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash     bytea       NOT NULL,
    user_agent     text,
    ip             inet,
    expires_at     timestamptz NOT NULL,
    revoked_at     timestamptz,
    last_seen_at   timestamptz NOT NULL DEFAULT now(),
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX sessions_token_key ON sessions (token_hash);
CREATE INDEX sessions_user ON sessions (user_id) WHERE revoked_at IS NULL;

-- ============================================================================
-- MEMORY  [V0 — lexical retrieval only, see docs/V0_SCOPE.md]
-- ============================================================================

CREATE TABLE memories (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id         uuid          NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id     uuid          REFERENCES users(id) ON DELETE SET NULL,
    visibility        visibility    NOT NULL DEFAULT 'private_user',
    type              memory_type   NOT NULL,
    assertion         memory_assertion NOT NULL DEFAULT 'user_stated',
    status            memory_status NOT NULL DEFAULT 'active',
    content           text          NOT NULL,
    subject_key       text,                       -- canonical subject for dedup (ADR 0006)
    source            data_source   NOT NULL DEFAULT 'chat',
    source_message_id uuid,                       -- FK added after conversation_messages
    confidence        numeric(3,2)  NOT NULL DEFAULT 1.00,
    importance        numeric(3,2)  NOT NULL DEFAULT 0.50,
    superseded_by_id  uuid          REFERENCES memories(id) ON DELETE SET NULL,
    confirmed_at      timestamptz,
    visibility_changed_at timestamptz,
    expires_at        timestamptz,
    embedding         vector(1536),               -- ADR 0002: exists, unused in V0
    search_tsv        tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED,
    created_at        timestamptz   NOT NULL DEFAULT now(),
    updated_at        timestamptz   NOT NULL DEFAULT now(),
    deleted_at        timestamptz,

    CONSTRAINT memories_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL),
    CONSTRAINT memories_confidence_range CHECK (confidence  BETWEEN 0 AND 1),
    CONSTRAINT memories_importance_range CHECK (importance  BETWEEN 0 AND 1),
    -- ADR 0006: temporary context must expire; it may not become permanent.
    CONSTRAINT memories_temp_context_expires
        CHECK (type <> 'temporary_context' OR expires_at IS NOT NULL)
);

CREATE INDEX memories_couple_type   ON memories (couple_id, type)
    WHERE deleted_at IS NULL AND status = 'active';
CREATE INDEX memories_search_tsv    ON memories USING gin (search_tsv);
CREATE INDEX memories_content_trgm  ON memories USING gin (content gin_trgm_ops);
CREATE INDEX memories_subject       ON memories (couple_id, type, subject_key)
    WHERE subject_key IS NOT NULL AND status = 'active';
CREATE INDEX memories_expiry        ON memories (expires_at) WHERE expires_at IS NOT NULL;
-- No vector index until the corpus exceeds ~10k rows (ADR 0002). Then:
--   CREATE INDEX memories_embedding ON memories USING hnsw (embedding vector_cosine_ops);

CREATE TABLE memory_embeddings (
    memory_id   uuid PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
    model       text        NOT NULL,
    dimensions  int         NOT NULL,
    generated_at timestamptz NOT NULL DEFAULT now()
);

-- ============================================================================
-- TASKS / COMMITMENTS / REMINDERS  [V0]
-- One table, discriminated by task_kind. See ARCHITECTURE.md §3.
-- ============================================================================

CREATE TABLE tasks (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id            uuid          NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id        uuid          REFERENCES users(id) ON DELETE SET NULL,
    visibility           visibility    NOT NULL DEFAULT 'shared_couple',
    kind                 task_kind     NOT NULL DEFAULT 'task',
    title                text          NOT NULL,
    description          text,
    status               task_status   NOT NULL DEFAULT 'todo',
    priority             priority_level NOT NULL DEFAULT 'normal',
    due_at               timestamptz,
    recurrence_rule      text,                    -- RFC 5545 RRULE
    committed_to_user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    parent_task_id       uuid REFERENCES tasks(id) ON DELETE CASCADE,
    plan_id              uuid,                    -- FK added after plans
    source               data_source   NOT NULL DEFAULT 'chat',
    source_message_id    uuid,
    created_at           timestamptz   NOT NULL DEFAULT now(),
    updated_at           timestamptz   NOT NULL DEFAULT now(),
    completed_at         timestamptz,
    deleted_at           timestamptz,

    CONSTRAINT tasks_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL),
    -- A reminder without a time is just a task (ARCHITECTURE.md §3).
    CONSTRAINT tasks_reminder_needs_due CHECK (kind <> 'reminder' OR due_at IS NOT NULL),
    CONSTRAINT tasks_commitment_needs_target
        CHECK (kind <> 'commitment' OR committed_to_user_id IS NOT NULL)
);

CREATE INDEX tasks_couple_status ON tasks (couple_id, status) WHERE deleted_at IS NULL;
CREATE INDEX tasks_due          ON tasks (due_at)
    WHERE due_at IS NOT NULL AND status IN ('todo','in_progress','snoozed');

-- ============================================================================
-- SHOPPING  [V0]
-- ============================================================================

CREATE TABLE shopping_items (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id      uuid            NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id  uuid            REFERENCES users(id) ON DELETE SET NULL,
    visibility     visibility      NOT NULL DEFAULT 'shared_couple',
    name           text            NOT NULL,
    normalized_name text           NOT NULL,      -- lower/singular, for dedup + patterns
    quantity       text,
    category       text,
    status         shopping_status NOT NULL DEFAULT 'needed',
    added_by       uuid            REFERENCES users(id) ON DELETE SET NULL,
    assigned_to    uuid            REFERENCES users(id) ON DELETE SET NULL,
    recurring      boolean         NOT NULL DEFAULT false,
    estimated_consumption_days int,               -- learned, advisory only (SPEC.md §12)
    source_message_id uuid,
    created_at     timestamptz     NOT NULL DEFAULT now(),
    purchased_at   timestamptz,
    deleted_at     timestamptz,

    CONSTRAINT shopping_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
);
CREATE INDEX shopping_couple_status ON shopping_items (couple_id, status)
    WHERE deleted_at IS NULL;
CREATE INDEX shopping_pattern ON shopping_items (couple_id, normalized_name, purchased_at DESC)
    WHERE purchased_at IS NOT NULL;

-- ============================================================================
-- FINANCE  [V0 — record only; aggregation ships in V1]
-- ============================================================================

CREATE TABLE expense_categories (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id  uuid REFERENCES couples(id) ON DELETE CASCADE,   -- null = system default
    name       text NOT NULL,
    parent_id  uuid REFERENCES expense_categories(id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX expense_categories_name ON expense_categories
    (COALESCE(couple_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));

CREATE TABLE expenses (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id      uuid          NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id  uuid          REFERENCES users(id) ON DELETE SET NULL,
    visibility     visibility    NOT NULL DEFAULT 'shared_couple',
    -- numeric, never float. SPEC.md §56.7: deterministic calculation.
    amount         numeric(14,2) NOT NULL CHECK (amount > 0),
    currency       char(3)       NOT NULL DEFAULT 'INR',
    category_id    uuid          REFERENCES expense_categories(id) ON DELETE SET NULL,
    description    text,
    merchant       text,
    paid_by        uuid          REFERENCES users(id) ON DELETE SET NULL,
    is_shared      boolean       NOT NULL DEFAULT true,
    occurred_on    date          NOT NULL DEFAULT CURRENT_DATE,
    source         data_source   NOT NULL DEFAULT 'chat',
    source_message_id uuid,
    created_at     timestamptz   NOT NULL DEFAULT now(),
    deleted_at     timestamptz,

    CONSTRAINT expenses_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
);
CREATE INDEX expenses_couple_date ON expenses (couple_id, occurred_on DESC)
    WHERE deleted_at IS NULL;
CREATE INDEX expenses_category    ON expenses (couple_id, category_id, occurred_on DESC);

-- ============================================================================
-- CALENDAR  [V0]
-- ============================================================================

CREATE TABLE events (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id      uuid        NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id  uuid        REFERENCES users(id) ON DELETE SET NULL,
    visibility     visibility  NOT NULL DEFAULT 'shared_couple',
    title          text        NOT NULL,
    description    text,
    starts_at      timestamptz NOT NULL,
    ends_at        timestamptz,
    all_day        boolean     NOT NULL DEFAULT false,
    location       text,
    recurrence_rule text,
    category       text,                          -- birthday | anniversary | bill | trip …
    external_id    text,                          -- V3 calendar sync
    source         data_source NOT NULL DEFAULT 'chat',
    source_message_id uuid,
    created_at     timestamptz NOT NULL DEFAULT now(),
    deleted_at     timestamptz,

    CONSTRAINT events_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL),
    CONSTRAINT events_end_after_start CHECK (ends_at IS NULL OR ends_at >= starts_at)
);
CREATE INDEX events_couple_start ON events (couple_id, starts_at) WHERE deleted_at IS NULL;

-- ============================================================================
-- HOUSEHOLD · GOALS · PLANS   [V1/V2 — created early, unused in V0]
-- ============================================================================

CREATE TABLE chores (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id        uuid         NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id    uuid         REFERENCES users(id) ON DELETE SET NULL,
    visibility       visibility   NOT NULL DEFAULT 'shared_couple',
    name             text         NOT NULL,
    assigned_to      uuid         REFERENCES users(id) ON DELETE SET NULL,
    recurrence_rule  text,
    status           chore_status NOT NULL DEFAULT 'pending',
    last_completed_at timestamptz,
    next_due_at      timestamptz,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    deleted_at       timestamptz,
    CONSTRAINT chores_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
);

CREATE TABLE goals (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id      uuid          NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id  uuid          REFERENCES users(id) ON DELETE SET NULL,
    visibility     visibility    NOT NULL DEFAULT 'shared_couple',
    name           text          NOT NULL,
    description    text,
    target_amount  numeric(14,2) CHECK (target_amount IS NULL OR target_amount > 0),
    currency       char(3)       NOT NULL DEFAULT 'INR',
    target_date    date,
    status         goal_status   NOT NULL DEFAULT 'active',
    created_at     timestamptz   NOT NULL DEFAULT now(),
    deleted_at     timestamptz,
    CONSTRAINT goals_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
);

-- current_amount is NOT a column. It is SUM(goal_transactions.amount).
-- SPEC.md §56.7 — never store a figure the database can compute.
CREATE TABLE goal_transactions (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    goal_id     uuid          NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
    amount      numeric(14,2) NOT NULL,           -- negative = withdrawal
    note        text,
    recorded_by uuid REFERENCES users(id) ON DELETE SET NULL,
    occurred_on date          NOT NULL DEFAULT CURRENT_DATE,
    created_at  timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX goal_transactions_goal ON goal_transactions (goal_id, occurred_on DESC);

CREATE TABLE plans (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id     uuid        NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id uuid        REFERENCES users(id) ON DELETE SET NULL,
    visibility    visibility  NOT NULL DEFAULT 'shared_couple',
    name          text        NOT NULL,
    description   text,
    status        plan_status NOT NULL DEFAULT 'draft',
    starts_on     date,
    ends_on       date,
    budget_amount numeric(14,2),
    goal_id       uuid REFERENCES goals(id) ON DELETE SET NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    deleted_at    timestamptz,
    CONSTRAINT plans_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
);

CREATE TABLE plan_items (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_id     uuid NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    entity_type text NOT NULL,                    -- task | event | goal | note | decision
    entity_id   uuid,
    label       text NOT NULL,
    sort_order  int  NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX plan_items_plan ON plan_items (plan_id, sort_order);

ALTER TABLE tasks ADD CONSTRAINT tasks_plan_fk
    FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE SET NULL;

-- ============================================================================
-- NOTIFICATIONS  [V1 — scored per ADR 0008]
-- ============================================================================

CREATE TABLE notifications (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id     uuid              NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    user_id       uuid              REFERENCES users(id) ON DELETE CASCADE,
    visibility    visibility        NOT NULL DEFAULT 'shared_couple',
    type          notification_type NOT NULL,
    state         notification_state NOT NULL DEFAULT 'pending',
    title         text              NOT NULL,
    body          text,
    entity_type   text,
    entity_id     uuid,
    score         numeric(4,3)      NOT NULL,     -- ADR 0008; delivery requires >= 0.5
    scheduled_for timestamptz       NOT NULL,
    sent_at       timestamptz,
    dismissed_at  timestamptz,
    created_at    timestamptz       NOT NULL DEFAULT now(),
    CONSTRAINT notifications_score_range CHECK (score BETWEEN 0 AND 1)
);
CREATE INDEX notifications_due ON notifications (scheduled_for)
    WHERE state = 'pending';
-- 7-day dedup window per (type, entity) — ADR 0008.
CREATE INDEX notifications_dedup ON notifications (couple_id, type, entity_id, created_at DESC);

-- ============================================================================
-- CONVERSATION  [V0]
-- ============================================================================

CREATE TABLE conversation_sessions (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id  uuid NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    user_id    uuid NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    title      text,
    started_at timestamptz NOT NULL DEFAULT now(),
    ended_at   timestamptz
);

CREATE TABLE conversation_messages (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id     uuid         NOT NULL REFERENCES conversation_sessions(id) ON DELETE CASCADE,
    couple_id      uuid         NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    user_id        uuid         REFERENCES users(id) ON DELETE SET NULL,
    visibility     visibility   NOT NULL DEFAULT 'private_user',
    role           message_role NOT NULL,
    content        text         NOT NULL,
    detected_intent intent_type,
    intent_confidence numeric(3,2),
    created_at     timestamptz  NOT NULL DEFAULT now()
);
CREATE INDEX conversation_messages_session ON conversation_messages (session_id, created_at);

ALTER TABLE memories        ADD CONSTRAINT memories_source_message_fk
    FOREIGN KEY (source_message_id) REFERENCES conversation_messages(id) ON DELETE SET NULL;
ALTER TABLE tasks           ADD CONSTRAINT tasks_source_message_fk
    FOREIGN KEY (source_message_id) REFERENCES conversation_messages(id) ON DELETE SET NULL;
ALTER TABLE shopping_items  ADD CONSTRAINT shopping_source_message_fk
    FOREIGN KEY (source_message_id) REFERENCES conversation_messages(id) ON DELETE SET NULL;
ALTER TABLE expenses        ADD CONSTRAINT expenses_source_message_fk
    FOREIGN KEY (source_message_id) REFERENCES conversation_messages(id) ON DELETE SET NULL;
ALTER TABLE events          ADD CONSTRAINT events_source_message_fk
    FOREIGN KEY (source_message_id) REFERENCES conversation_messages(id) ON DELETE SET NULL;

-- ============================================================================
-- AUDIT  [V0 — from the first commit, per ADR 0004 and SPEC.md §32]
-- ============================================================================

CREATE TABLE ai_actions (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id        uuid           NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    user_id          uuid           REFERENCES users(id) ON DELETE SET NULL,
    message_id       uuid           REFERENCES conversation_messages(id) ON DELETE SET NULL,
    tool_name        text           NOT NULL,
    arguments        jsonb          NOT NULL,
    idempotency_key  text,                        -- ADR 0004: retry-safe creates
    outcome          action_outcome NOT NULL,
    result           jsonb,
    error_message    text,
    entity_type      text,
    entity_id        uuid,
    provider         text,
    model            text,
    llm_role         text,                        -- fast | deep | embed | local (ADR 0003)
    prompt_tokens    int,
    completion_tokens int,
    estimated_cost   numeric(10,6),               -- SPEC.md §50, per-couple cost
    latency_ms       int,
    created_at       timestamptz    NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ai_actions_idempotency ON ai_actions (idempotency_key)
    WHERE idempotency_key IS NOT NULL;
CREATE INDEX ai_actions_couple_time ON ai_actions (couple_id, created_at DESC);
CREATE INDEX ai_actions_entity      ON ai_actions (entity_type, entity_id);

-- before_state/after_state carry entity bodies verbatim, so this table is as
-- sensitive as the rows it describes. It therefore carries the same three
-- columns as every other couple-scoped table (ADR 0005), and the same policy.
-- Verified: without them, a member of another couple could read a private
-- memory's content straight out of after_state.
CREATE TABLE audit_logs (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id     uuid REFERENCES couples(id) ON DELETE CASCADE,
    user_id       uuid REFERENCES users(id) ON DELETE SET NULL,
    owner_user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    -- Deliberately NO DEFAULT. A default of 'shared_couple' fails open: an
    -- audit row written for a private entity by code that forgot to set
    -- visibility would become readable by the partner. With no default, that
    -- same omission is a not-null violation at write time. ADR 0005 puts the
    -- recoverable error on the private side; an unnoticed disclosure is not
    -- recoverable.
    visibility    visibility NOT NULL,
    ai_action_id  uuid REFERENCES ai_actions(id) ON DELETE SET NULL,
    action        text NOT NULL,                  -- create | update | delete | share | …
    entity_type   text NOT NULL,
    entity_id     uuid,
    before_state  jsonb,
    after_state   jsonb,
    created_at    timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT audit_logs_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
    -- INVARIANT the application must maintain: an audit row's visibility and
    -- owner mirror the entity it describes. Writing an audit entry is part of
    -- the same transaction as the mutation, so the values are always in hand.
    -- Not enforced by trigger because the entity may already be deleted by the
    -- time a deletion is audited.
);
CREATE INDEX audit_logs_entity ON audit_logs (entity_type, entity_id, created_at DESC);

-- ============================================================================
-- CAPTURE — DUMP FILES  [V0]   (ADR 0009)
--
-- Replaces the conversational inbox of SPEC.md §7. Visibility is derived from
-- WHICH FILE a block was written in, never inferred from its text. The model
-- cannot set it, exactly as it cannot set couple_id or owner_user_id.
-- ============================================================================

CREATE TYPE dump_file_kind    AS ENUM ('shared','private');
CREATE TYPE dump_block_status AS ENUM ('unprocessed','processing','processed',
                                       'needs_input','ignored','failed');

CREATE TABLE dump_files (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id       uuid           NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id   uuid           REFERENCES users(id) ON DELETE CASCADE,
    visibility      visibility     NOT NULL,
    kind            dump_file_kind NOT NULL,
    title           text           NOT NULL,
    content         text           NOT NULL DEFAULT '',
    -- Optimistic concurrency: two partners editing shared.md is last-write-wins
    -- with a stale-version warning, not a silent overwrite (ADR 0009).
    content_version int            NOT NULL DEFAULT 1,
    last_processed_at timestamptz,
    created_at      timestamptz    NOT NULL DEFAULT now(),
    updated_at      timestamptz    NOT NULL DEFAULT now(),

    -- The two kinds are the two visibility scopes. There is no third combination.
    CONSTRAINT dump_files_kind_matches_visibility CHECK (
        (kind = 'shared'  AND visibility = 'shared_couple' AND owner_user_id IS NULL)
     OR (kind = 'private' AND visibility = 'private_user'  AND owner_user_id IS NOT NULL)
    )
);
CREATE UNIQUE INDEX dump_files_one_shared_per_couple
    ON dump_files (couple_id) WHERE kind = 'shared';
CREATE UNIQUE INDEX dump_files_one_private_per_member
    ON dump_files (couple_id, owner_user_id) WHERE kind = 'private';

CREATE TABLE dump_runs (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id             uuid NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    dump_file_id          uuid NOT NULL REFERENCES dump_files(id) ON DELETE CASCADE,
    triggered_by          uuid REFERENCES users(id) ON DELETE SET NULL,
    started_at            timestamptz NOT NULL DEFAULT now(),
    finished_at           timestamptz,
    blocks_seen           int  NOT NULL DEFAULT 0,
    blocks_processed      int  NOT NULL DEFAULT 0,
    blocks_needing_input  int  NOT NULL DEFAULT 0,
    blocks_failed         int  NOT NULL DEFAULT 0,
    entities_created      int  NOT NULL DEFAULT 0,
    entities_updated      int  NOT NULL DEFAULT 0,
    -- The change report shown to the user. Persisted so it stays correctable
    -- after the fact and so §32's audit story covers batch runs.
    report                jsonb
);
CREATE INDEX dump_runs_file ON dump_runs (dump_file_id, started_at DESC);

CREATE TABLE dump_blocks (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    dump_file_id    uuid              NOT NULL REFERENCES dump_files(id) ON DELETE CASCADE,
    couple_id       uuid              NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id   uuid              REFERENCES users(id) ON DELETE SET NULL,
    visibility      visibility        NOT NULL,
    -- Dedup guard: re-processing a file cannot recreate what it already made.
    content_hash    bytea             NOT NULL,
    raw_text        text              NOT NULL,
    line_start      int,
    line_end        int,
    status          dump_block_status NOT NULL DEFAULT 'unprocessed',
    detected_intent intent_type,
    intent_confidence numeric(3,2),
    question        text,             -- set when status = 'needs_input'
    answered_by_block_id uuid REFERENCES dump_blocks(id) ON DELETE SET NULL,
    run_id          uuid REFERENCES dump_runs(id) ON DELETE SET NULL,
    error_message   text,
    -- Advisory only. Set when gift/surprise-shaped text appears in the shared
    -- file. The system flags; it never relocates the user's words (ADR 0009).
    privacy_flagged boolean           NOT NULL DEFAULT false,
    created_at      timestamptz       NOT NULL DEFAULT now(),
    processed_at    timestamptz,

    CONSTRAINT dump_blocks_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL),
    CONSTRAINT dump_blocks_question_when_needs_input
        CHECK (status <> 'needs_input' OR question IS NOT NULL)
);
CREATE UNIQUE INDEX dump_blocks_dedup ON dump_blocks (dump_file_id, content_hash);
CREATE INDEX dump_blocks_pending ON dump_blocks (dump_file_id, status)
    WHERE status IN ('unprocessed','needs_input');

-- What each block actually did. This is the change report, normalized.
CREATE TABLE dump_block_entities (
    block_id    uuid NOT NULL REFERENCES dump_blocks(id) ON DELETE CASCADE,
    entity_type text NOT NULL,
    entity_id   uuid NOT NULL,
    action      text NOT NULL,   -- created | updated | completed | superseded
    PRIMARY KEY (block_id, entity_type, entity_id, action)
);
CREATE INDEX dump_block_entities_entity ON dump_block_entities (entity_type, entity_id);

-- ============================================================================
-- ATTACHMENTS  [V0 — stored and linked; OCR is SPEC.md §51 territory]
-- ============================================================================

CREATE TABLE attachments (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    couple_id     uuid       NOT NULL REFERENCES couples(id) ON DELETE CASCADE,
    owner_user_id uuid       REFERENCES users(id) ON DELETE SET NULL,
    visibility    visibility NOT NULL,
    dump_file_id  uuid       REFERENCES dump_files(id) ON DELETE SET NULL,
    filename      text       NOT NULL,
    mime_type     text       NOT NULL,
    byte_size     bigint     NOT NULL CHECK (byte_size > 0),
    storage_key   text       NOT NULL,
    checksum      bytea,
    -- Column exists in V0 so the OCR pipeline arrives without a migration.
    ocr_status    text       NOT NULL DEFAULT 'not_attempted',
    ocr_text      text,
    uploaded_by   uuid       REFERENCES users(id) ON DELETE SET NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    deleted_at    timestamptz,

    CONSTRAINT attachments_owner_required_when_private
        CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
);
CREATE UNIQUE INDEX attachments_storage_key ON attachments (storage_key);
CREATE INDEX attachments_couple ON attachments (couple_id) WHERE deleted_at IS NULL;

CREATE TABLE attachment_links (
    attachment_id uuid NOT NULL REFERENCES attachments(id) ON DELETE CASCADE,
    entity_type   text NOT NULL,
    entity_id     uuid NOT NULL,
    PRIMARY KEY (attachment_id, entity_type, entity_id)
);

-- ============================================================================
-- ROW-LEVEL SECURITY  (ADR 0005)
-- The application connects as a non-superuser role and sets, per transaction:
--     SET LOCAL app.current_user_id   = '…';
--     SET LOCAL app.current_couple_id = '…';
-- A forgotten WHERE clause then returns nothing rather than leaking.
--
-- SET LOCAL IS LOAD-BEARING, NOT STYLE. Verified against PostgreSQL 16:
--   * SET LOCAL  — scope ends at COMMIT. A later transaction on the same
--                  pooled connection that forgets to set anything sees 0 rows.
--   * SET        — survives COMMIT. A later transaction on the same pooled
--                  connection sees the PREVIOUS couple's rows. This is a
--                  cross-couple data leak, reproducible in three statements.
-- The application must therefore issue these inside the request transaction,
-- never on connection open, and the pool should DISCARD ALL on return.
-- data/rls-tests.sql asserts both behaviours.
-- ============================================================================

CREATE OR REPLACE FUNCTION app_current_user() RETURNS uuid AS $$
    SELECT nullif(current_setting('app.current_user_id', true), '')::uuid;
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_current_couple() RETURNS uuid AS $$
    SELECT nullif(current_setting('app.current_couple_id', true), '')::uuid;
$$ LANGUAGE sql STABLE;

-- Group A: tables carrying owner_user_id. Generic couple+visibility policy.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'memories','tasks','shopping_items','expenses',
        'events','chores','goals','plans',
        'dump_files','dump_blocks','attachments'
    ] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($f$
            CREATE POLICY couple_scope ON %I
            USING (
                couple_id = app_current_couple()
                AND (
                    visibility = 'shared_couple'
                    OR (visibility = 'private_user' AND owner_user_id = app_current_user())
                )
            )$f$, t);
    END LOOP;
END $$;

-- Group B: tables that key privacy off user_id rather than owner_user_id.
-- A conversation is always private to the person who typed it; the partner
-- reads the resulting records, never the raw messages.
ALTER TABLE conversation_sessions  ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions  FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON conversation_sessions
    USING (couple_id = app_current_couple() AND user_id = app_current_user());

ALTER TABLE conversation_messages  ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_messages  FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON conversation_messages
    USING (
        couple_id = app_current_couple()
        AND (
            visibility = 'shared_couple'
            OR user_id  = app_current_user()
            OR user_id IS NULL          -- assistant/system turns in the caller's session
        )
    );

ALTER TABLE ai_actions  ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai_actions  FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON ai_actions
    USING (couple_id = app_current_couple() AND user_id = app_current_user());

ALTER TABLE dump_runs  ENABLE ROW LEVEL SECURITY;
ALTER TABLE dump_runs  FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON dump_runs
    USING (couple_id = app_current_couple());

-- Child tables carrying no couple_id of their own.
--
-- These were previously left unprotected on the reasoning that they are "only
-- reachable by joining a parent that is already policy-protected". That
-- reasoning is wrong: a direct SELECT joins nothing, and goal_transactions.note
-- and plan_items.label are free text. Verified leaking across couples before
-- these policies existed.
--
-- The policy derives scope from the parent instead of denormalising couple_id.
-- The subquery is itself subject to the parent's policy, so an invisible parent
-- yields an invisible child.
ALTER TABLE goal_transactions   ENABLE ROW LEVEL SECURITY;
ALTER TABLE goal_transactions   FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON goal_transactions
    USING (EXISTS (SELECT 1 FROM goals g WHERE g.id = goal_transactions.goal_id));

ALTER TABLE plan_items          ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_items          FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON plan_items
    USING (EXISTS (SELECT 1 FROM plans p WHERE p.id = plan_items.plan_id));

ALTER TABLE memory_embeddings   ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_embeddings   FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON memory_embeddings
    USING (EXISTS (SELECT 1 FROM memories m WHERE m.id = memory_embeddings.memory_id));

ALTER TABLE dump_block_entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE dump_block_entities FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON dump_block_entities
    USING (EXISTS (SELECT 1 FROM dump_blocks b WHERE b.id = dump_block_entities.block_id));

ALTER TABLE attachment_links    ENABLE ROW LEVEL SECURITY;
ALTER TABLE attachment_links    FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON attachment_links
    USING (EXISTS (SELECT 1 FROM attachments a WHERE a.id = attachment_links.attachment_id));

-- audit_logs: same policy as any other couple-scoped table.
ALTER TABLE audit_logs ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_logs FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON audit_logs
    USING (
        couple_id = app_current_couple()
        AND (
            visibility = 'shared_couple'
            OR (visibility = 'private_user' AND owner_user_id = app_current_user())
        )
    );

-- DELIBERATELY WITHOUT RLS — do not "fix" these:
--   users, couple_members, auth_tokens
-- All three are read during sign-in, before any session variable exists. A
-- fail-closed policy would make authentication impossible. Access is instead
-- constrained by the application never exposing them on an unauthenticated
-- path, and by auth_tokens storing only a hash. expense_categories is global
-- reference data with no couple content.

-- notifications.user_id NULL means "addressed to both partners".
ALTER TABLE notifications  ENABLE ROW LEVEL SECURITY;
ALTER TABLE notifications  FORCE  ROW LEVEL SECURITY;
CREATE POLICY couple_scope ON notifications
    USING (
        couple_id = app_current_couple()
        AND (user_id IS NULL OR user_id = app_current_user())
    );

-- ============================================================================
-- SEED
-- ============================================================================

INSERT INTO expense_categories (couple_id, name) VALUES
    (NULL,'Groceries'),(NULL,'Dining'),(NULL,'Transport'),(NULL,'Utilities'),
    (NULL,'Rent'),(NULL,'Health'),(NULL,'Entertainment'),(NULL,'Shopping'),
    (NULL,'Travel'),(NULL,'Subscriptions'),(NULL,'Gifts'),(NULL,'Other');
