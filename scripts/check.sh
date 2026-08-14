#!/usr/bin/env bash
# The gate. All of it, in one command, because nothing else runs it.
#
# There is no CI by decision (IMPLEMENTATION_PLAN.md, first decision), so this
# script is not a convenience wrapper around a hosted runner — it is the thing
# itself. Two things it does that a bare `dotnet test` does not: it clears the
# run record first, so the gate cannot score yesterday's numbers, and it runs
# the gate at the end, which is the only step that can say "forty of fifty-five
# cases, all green".
#
# Run it from a clean stack when the answer matters:
#   docker compose down -v && docker compose up -d --build
# A green run against accumulated local state is a weaker claim, and that is the
# half a hosted runner would have covered.
#
#   ./scripts/check.sh              everything, three samples per eval case
#   ./scripts/check.sh --fast       skip the model, and let the gate say so
#   EVAL_SAMPLES=1 ./scripts/check.sh   one sample, for a quick look
#
# Requires the stack:  docker compose up -d

set -euo pipefail

cd "$(dirname "$0")/.."

fast=false
[[ "${1:-}" == "--fast" ]] && fast=true

# The model credential and host live in .env, like everything else the compose
# stack reads. Sourced rather than duplicated here.
if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

say() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

say "Clearing the run record"
rm -rf artifacts/eval

say "Build"
dotnet build CoupleOS.slnx

say "Unit tests, and the pipeline evals"
dotnet test tests/CoupleOS.UnitTests

say "Integration tests, and the database evals"
dotnet test tests/CoupleOS.IntegrationTests

say "Row-level security assertions, in SQL"
# Piped in rather than read from the container's /repo mount, so the path never
# reaches a shell that might rewrite it. Git Bash converts /repo/... into
# C:/Program Files/Git/repo/... on the way past — a puzzling diagnosis the first
# time and an annoying one every time after.
docker compose exec -T db psql -U "${POSTGRES_USER:-postgres}" -d "${POSTGRES_DB:-coupleos}" \
  -v ON_ERROR_STOP=1 < data/rls-tests.sql

if $fast; then
  say "Skipping the extraction evals (--fast)"
  echo "The gate below will report the 51 cases they cover as never run, and fail."
else
  say "Extraction evals"
  dotnet test tests/CoupleOS.AITests
fi

say "Eval gate"
dotnet run --project tools/CoupleOS.EvalGate
