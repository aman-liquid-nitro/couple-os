# 0012. The database schema is owned by SQL, not by EF Core migrations

Date: 2026-08-12
Status: Accepted
Supersedes: the header note in `data/schema.sql`, and M0's "migration matches
data/schema.sql" exit criterion

## Context

`data/schema.sql` describes itself as a reference, with EF Core migrations as
the source of truth. That was written before the schema existed in full. It now
contains a great deal that EF Core cannot express:

```text
row-level security policies on 16 tables      ALTER … FORCE ROW LEVEL SECURITY
a generated tsvector column                   GENERATED ALWAYS AS … STORED
partial indexes                               WHERE deleted_at IS NULL
a deferrable constraint trigger               couple_members_max_two
a DO block applying policies in a loop
three enum types with policy-bearing values
```

An EF migration for this would be a thin wrapper around `migrationBuilder.Sql`
containing the same text, generated into C# nobody reads. Worse, the parts that
matter most — the ADR 0005 policies — would move from a reviewable SQL file into
generated code, which is precisely where security rules should not live.

There is also an immediate practical problem. The EF model deliberately covers
three tables, because each was added only when something needed it and could
test it. `dotnet ef migrations add` today would emit a three-table schema and
call it authoritative.

## Decision

**`data/schema.sql` is the source of truth. EF Core is a consumer of it.**

- The database is built by applying `data/schema.sql`, in development via the
  Docker init script and in deployment by running the same file.
- No `dotnet ef migrations add`. `Microsoft.EntityFrameworkCore.Design` stays
  for tooling such as scaffolding and `dbcontext script`, not for authoring.
- Schema changes are written as SQL, reviewed as SQL, and applied as SQL.
- **A parity test replaces the migration check.** It asserts that every table
  and column the EF model maps actually exists, that nullability agrees, and
  that no column which is `NOT NULL` without a default is left unmapped on a
  table the application inserts into — the last being the one that fails at
  runtime rather than at build time.

## Consequences

**Easier.** The security-critical part of the system is reviewed in the language
it is written in. One file to read, argue about and diff. No generated migration
whose semantics differ subtly from the SQL it replaced.

**Harder.** No automatic migration history, so schema evolution needs numbered
change scripts once there is data worth preserving — that arrives with M1, not
before. Nothing warns at build time that the model and the schema have drifted;
the parity test is the only thing that catches it, so it must run in the same
suite as everything else rather than being optional.

**Accepting.** EF's model-diffing is genuinely useful and this gives it up. The
trade is deliberate: it buys a schema whose visibility rules can be read
top-to-bottom by a person deciding whether to trust this with their private
data.

## Alternatives considered

**Model all 28 tables and generate the migration** — rejected for now, not
forever. It is a large amount of entity configuration for tables nothing yet
reads, and the RLS half would still be raw SQL. Worth revisiting only if EF
gains real policy support.

**Both, kept in sync by hand** — rejected outright. Two sources of truth is
zero sources of truth, and the failure mode is a policy that exists in one and
not the other.
