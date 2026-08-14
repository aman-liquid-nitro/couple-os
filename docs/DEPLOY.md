# Running the validation week

**If you have not set this up before, read [SETUP.md](./SETUP.md) instead.** It
covers the same ground for a Windows 11 laptop, from `git clone` to both phones,
assuming no knowledge of Docker or Tailscale. This file is the terse version and
the record of what was decided and why.

V0 is done — [V0_SCOPE.md](./V0_SCOPE.md)'s done-checklist is fourteen of
fourteen. What it is not is *validated*, and the bar for that is in the same
file: seven days of real use by two real people. This document is how the stack
gets in front of them.

Three decisions were taken here rather than left open, and each cost something:

- **Self-hosted behind Tailscale**, not a VPS. The couple's data stays on a
  machine they own, which is SPEC.md §40's stance, and there is no public TLS,
  firewall or domain to get wrong. **Cost:** the machine has to stay awake. A
  laptop that sleeps is an outage, and the run is measuring whether people
  capture things in doorways.
- **maildev keeps sending the magic links**, read from its web UI rather than an
  inbox. No transactional sender, no credentials, no deliverability. **Cost:**
  see the security note at the bottom — this one is worth reading before you
  start rather than after.
- **Ollama's hosted `gemma4:31b`** (ADR 0013), not local inference. It passes
  `data/ollama-toolcall-smoke.json` at 1.2s and its stated policy is that
  prompts are never logged or trained on. **Cost:** it is weaker than Anthropic,
  so ADR 0011's caveat stands — a poor week is ambiguous between the idea being
  wrong and the model being too small, and a *good* week is unambiguous.

---

## Once

**On the host machine**, which needs Docker, Tailscale, and to be a machine both
partners' devices are already on the tailnet with.

Enable MagicDNS and HTTPS certificates in the Tailscale admin console. Without
them `tailscale serve` has no certificate to terminate with, and the whole
arrangement below depends on the browser seeing HTTPS.

Copy `.env.example` to `.env` and set what the run needs on top of the
development values:

```bash
BIND_ADDRESS=127.0.0.1
PUBLIC_BASE_URL=https://your-machine.your-tailnet.ts.net
OLLAMA_BASE_URL=https://ollama.com
OLLAMA_API_KEY=your-key-from-ollama.com/settings/keys
LLM_FAST_MODEL=gemma4:31b
LLM_DEEP_MODEL=gemma4:31b
```

`BIND_ADDRESS=127.0.0.1` is the one that matters most and looks like the least.
It keeps every published port on the loopback interface, so Docker exposes
nothing to the network and Tailscale is the only way in — which means the access
control is the tailnet's ACL rather than whatever the host's firewall happens to
be. Left at `0.0.0.0`, Postgres listens on every interface with `dev_app` as its
password.

Then publish the two surfaces onto the tailnet:

```bash
tailscale serve --bg 8080
```

```bash
tailscale serve --bg --https=8443 1080
```

The first puts the application at `https://your-machine.your-tailnet.ts.net`.
The second puts maildev at the same host on port 8443, which is where both
partners read their own sign-in links. Check both with `tailscale serve status`.

---

## Every start

```bash
docker compose -f docker-compose.yml -f docker-compose.live.yml up -d --build
```

The overlay refuses to start if `PUBLIC_BASE_URL` or `OLLAMA_API_KEY` is empty.
Both failures would otherwise be silent and expensive — links nobody can use in
the first case, and a week of the 4b development model in the second.

Confirm all three containers are healthy before telling anyone it is up:

```bash
docker compose ps
```

A healthy `api` means it reached Postgres as the non-superuser role, not merely
that the process is alive.

**Sign in, both partners.** Enter an address at `/signin`, open maildev at
`https://your-machine.your-tailnet.ts.net:8443`, click the link. Do this once
for each partner before the week starts, and form the couple — a first capture
against a broken sign-in is not a data point about the product.

---

## Backups

This database holds the only copy of things the couple has told no one else, and
`docker compose down -v` deletes it. Take both of these daily — the second is
not optional, because attachment bytes live on a volume rather than in Postgres
(ADR 0014), so a database-only backup restores a receipt as a broken link.

```bash
docker compose exec -T db pg_dump -U postgres coupleos | gzip > backup-$(date +%F).sql.gz
```

```bash
docker run --rm -v couple-os_coupleos-attachments:/data -v "$PWD:/out" alpine tar czf /out/attachments-$(date +%F).tar.gz -C /data .
```

On Git Bash for Windows that second command fails with a path that has
`C:/Program Files/Git` prepended to it — MSYS rewrites the container-side `/out`
as if it were a host path. Prefix it with `MSYS_NO_PATHCONV=1`. It is correct as
written everywhere else.

**Test the restore once, before the week, not after it.** ARCHITECTURE.md §7
says restore is tested rather than assumed, and an untested dump is a file, not
a backup. This procedure was run against the development database on 2026-08-14
and restored 22 row-level-security policies along with the rows — which is the
part worth checking rather than the counts, because a restore that brought back
the data and not the policies would look entirely successful and would have
opened every private memory to the partner:

```bash
docker compose exec -T db psql -U postgres -c 'CREATE DATABASE restore_check'
```

```bash
gunzip -c backup-*.sql.gz | docker compose exec -T db psql -U postgres -d restore_check
```

```bash
docker compose exec -T db psql -U postgres -d restore_check -c 'SELECT count(*) FROM pg_policies' -c 'SELECT count(*) FROM memories'
```

Drop `restore_check` afterwards. These are local files on the same disk as the
database they protect, which is enough for one week and is not a backup strategy
— STATUS debt 52.

---

## Security notes, both of them real

**The maildev inbox is the credential.** A magic link *is* the sign-in, and
maildev shows every message it has ever received to anyone who opens it. So any
device on the tailnet can read either partner's link and sign in as them. For a
two-person tailnet holding only the couple's own devices that is an acceptable
week; it stops being acceptable the moment a third device joins, a friend is
shared into the tailnet, or the run is extended. Either move to a real SMTP
sender or take the maildev route down — the choice is reversible and is
`Smtp__Host` plus one `tailscale serve` command.

**Do not point the SQL harnesses at this database.** `data/rls-tests.sql` and the
pgbench harness leave `probe` and `t_results` behind, unprotected and granted to
the app role (STATUS debt 18). They belong against a local stack only.

---

## Ending the week

Stop the stack without destroying it — `down` alone keeps the volumes, and
`down -v` is the command that does not:

```bash
docker compose -f docker-compose.yml -f docker-compose.live.yml down
```

```bash
tailscale serve --https=443 off && tailscale serve --https=8443 off
```

Then answer the five bullets under **Definition of validated** in
[V0_SCOPE.md](./V0_SCOPE.md). The last one decides what happens next, and the
file already says what to do if it comes out false: change the product, not
build V1.
