# Production readiness

What was audited before real businesses were let on, what was found, and how each answer was
obtained. Every line here is a check that was run, not an intention.

Audited 22–23 September 2026.

## Where production actually is

| Part | Reality |
|---|---|
| API | Render web service `zazi-api`, Docker, plan `starter`, Frankfurt, auto-deploy **off** |
| Portal | Render web service `zazi-web`, Docker, plan `starter`, Frankfurt, auto-deploy **off** |
| Database | Render PostgreSQL 18, plan `0.1c-256mb`, 1 GB disk, no high availability, no read replica |
| Addresses | `https://api.getzazi.com`, `https://app.getzazi.com` (TLS at Render's edge, Cloudflare in front) |
| Deploys | `scripts/render-deploy.sh <service-id> <name>`, which waits for the deploy to go live |

There is **no Hetzner VPS, no Caddy and no systemd** in production. `PRODUCTION.md` describes
the self-hosted path, which is the fallback rather than what runs.

## Backups, and the restore drill

Render keeps point-in-time recovery for the database. On top of that, logical exports can be
taken on demand:

```bash
RENDER_API_KEY=... scripts/render-backup.sh [download-directory]
```

**The drill that was actually run** (23 September 2026):

1. Took a logical export of production through the API.
2. Downloaded it (59 KB compressed — production is still small).
3. Restored it into a throwaway local database with PostgreSQL 18's `pg_restore`.
4. Counted what came back: 3 organisations, 6 users, 4 transactions, 31 tables, 21 migrations.
5. Repeated the drill on a larger local database and confirmed the **append-only trigger**,
   all rows and all 117 indexes survive a dump and restore.
6. Deleted the local copy of production data and the drill database.

Two things to know before relying on it:

- **`pg_restore` must be version 18 or newer.** Older client tools abort on an 18 archive. If
  you do not have them, a restore into a new Render database from the dashboard needs no local
  tooling at all.
- Render keeps each logical export for **seven days**. Anything that must outlive that has to
  be downloaded and kept somewhere of your own.

## Security

| Check | Result | Evidence |
|---|---|---|
| Unknown API paths | Refused | `/swagger`, `/openapi/v1.json`, `/metrics`, `/hangfire` all answer 401 — deny by default, nothing enumerable |
| Development endpoints in production | Closed | `POST /api/v1/sms-gateway/simulate` answers 404 with a valid body |
| API security headers | Present | CSP `default-src 'none'`, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`, HSTS |
| Portal security headers | **Was missing — fixed** | CSP, frame, MIME, referrer and permissions headers now set; verified no policy violations across every page |
| Cross-origin access to the API | Not possible from a browser | No CORS headers are emitted; a cross-site call has no permission to read a response |
| Tenant isolation | Enforced server-side | Integration suites cover it; a business's ledger cannot be read with another's token |
| Secrets in the repository | None | Keys are `sync: false` in `render.yaml` and read from the environment |
| Ledger immutability | Enforced by the database | `transactions_append_only` trigger, and it survives backup and restore |

## Capacity

Measured, not assumed — see [SCALABILITY.md](SCALABILITY.md). At 4.5 million transactions every
query behind a page is index-backed and under 2 ms. The limit that arrives first is **storage**:
1 GB holds roughly 2.3 million transactions.

Each service now bounds its database connections (API 20, portal 10) so the two together cannot
exhaust a small instance.

## Health and release identification

| Endpoint | Says |
|---|---|
| `/health` | The process is up, **which build it is** (version and commit), and the environment |
| `/ready` | The process can reach its database; 503 when it cannot. This is what a monitor should watch. |

The portal's Operations page shows the same three facts — portal build, database reachability,
and whether the API is answering — so an incident starts with evidence rather than a guess.

## What is still outstanding

| | Why it is not done here |
|---|---|
| Rotate the Render API key and the database password | Both appeared in a working session transcript. Only the account owner can rotate them. |
| Restrict the database's IP allow list | It currently accepts connections from any address (`0.0.0.0/0`). Tightening it may cut off admin tools the owner uses, so it is their call. |
| Uptime monitoring against `/ready` | Needs an account with a monitoring provider. |
| Storage headroom | Turn on disk autoscaling before the database passes ~700 MB. This changes billing. |
| Play Store submission | The signed bundle is built; publishing needs the Play Console account. See [PLAY_STORE.md](PLAY_STORE.md). |
| Paid credits | Designed, not built. See [adr/ADR-006-zazi-credits.md](adr/ADR-006-zazi-credits.md). |
