# Working agreement for this repository

Read this before writing code, tests or documentation here.

## YAGNI is the default

Build what the current milestone needs and nothing else. If a thing is not
needed by an exit criterion in [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md)
or a box in [docs/V0_SCOPE.md](docs/V0_SCOPE.md), do not build it.

- No abstraction with one implementation "for later". No interface, options
  class, extension point or strategy added ahead of a second caller.
- No V1 feature smuggled in as a small addition to a V0 change.
- Prefer deleting to disabling, and a smaller change to a more general one.
- When something out of scope looks worth doing, write it down as a numbered
  debt in [docs/STATUS.md](docs/STATUS.md) instead of doing it.

## Tests: fewer, and each one earning its place

The suite is already large. Treat every new test as a cost.

- **Do not add a test that no longer distinguishes anything.** Before adding
  one, name the change to production code that would make it fail on its own.
  If an existing test already fails on that change, extend or leave it be.
- **Prefer extending an existing test** over adding a near-duplicate one.
- **One assertion path per property, not one per surface**, unless the surfaces
  genuinely enforce it separately (the `visibility` rule is the exception, and
  it says why in its own comment).
- **Do not test the framework, the mapping, or a constant.** Test behaviour the
  couple would notice.
- Integration and database tests are the expensive tier. Use them for
  properties only PostgreSQL and row-level security can decide; use unit tests
  for the rest.
- Removing a redundant test is a legitimate change. Say what it used to cover
  and where that coverage now lives.

## Documentation

`docs/STATUS.md` records state, the plan records intent. Keep prose short — a
paragraph that explains why a decision was made is worth keeping; a paragraph
restating what the code says is not. Do not narrate a change in three files.

## Non-negotiables that YAGNI does not override

These are the properties the project exists to hold, and a lean change must
still hold them:

- No tool schema may offer the model `visibility`, `couple_id`, `owner_user_id`
  or `user_id` (ADR 0005, ADR 0004).
- Every tool call is audited, refusals included (TOOLS.md rule 3).
- SQL owns the schema; there are no EF migrations (ADR 0012).
- A failed tool call never produces success language.
