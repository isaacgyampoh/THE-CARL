# Running a Zazi pilot

> For a real deployment — your own server, a domain name and TLS — see
> [PRODUCTION.md](PRODUCTION.md). This document covers a trial run from a laptop.

How to get Zazi in front of a real agent. Everything below has been executed end to end; the
commands are the ones that were actually run, not a sketch.

## What a pilot proves, and what it does not

Zazi observes and reconciles. It does not move money, hold funds, or talk to MTN, Telecel or
AirtelTigo. An agent keeps working exactly as they do now; Zazi records what happened and
tells them when its record and the provider's disagree.

So a pilot answers questions no test can: does automatic capture actually recognise the
messages this agent's network sends, is the till figure the one they would have written down,
and do they trust it enough to stop keeping a paper book alongside.

## The database is already PostgreSQL

Worth stating plainly, because it is easy to assume otherwise: this project already runs
PostgreSQL 16.2, installed without Docker, Homebrew or root. The binaries were extracted from
a Maven Central archive into `~/.zazi-testdb` and are started by `scripts/start-stack.sh`.
There is nothing to install before a pilot.

That matters beyond convenience. Two of the ledger's guarantees are PostgreSQL features and
do not exist in a lighter database:

- **Idempotency** comes from partial unique indexes on `(OrganizationId, ClientTransactionId)`
  and `(OrganizationId, EvidenceFingerprint)`. They are what stop a retried submission
  becoming a second transaction.
- **Balance correctness under concurrency** comes from `INSERT … ON CONFLICT … DO UPDATE`,
  which increments inside the database rather than reading and writing back.

Moving to a simpler store to "get started" would mean removing both, running the pilot without
them, and reinstating them later — with real money already recorded in between.

## 1. Start the stack

```bash
scripts/start-stack.sh
```

That starts PostgreSQL, applies migrations, and brings up the API on 5055 and the dashboard on
5080. It generates a signing key once and reuses it, because a key that changed on every start
would invalidate every session and every enrolled device.

If a port is already taken it says so and stops, rather than connecting to whatever else is
listening. Stop everything with `scripts/stop-stack.sh`.

## 2. Create the first owner

A new database has no accounts, and every endpoint that could create one requires an
authenticated caller. The bootstrap tool closes that gap:

```bash
ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=55433;Database=zazi;Username=…;Password=…" \
ZAZI_JWT_KEY="$(cat ~/.zazi-testdb/signing.key)" \
dotnet run --project src/Zazi.Bootstrap -- \
  --organization "Kofi Mobile Money" \
  --branch "Accra Central" \
  --owner-name "Kofi Mensah" --owner-email owner@example.test --owner-password '…' \
  --agent-name "Ama Agent"  --agent-email agent@example.test --agent-password '…'
```

It refuses to run against a database that already has an organization. A bootstrap that also
worked on a live system would be a way to add a tenant nobody authorised — further accounts
are created through the API, where the action is authenticated and audited.

## 3. Sign in to the dashboard

`http://127.0.0.1:5080` as the owner. A new organization shows ₵0.00 everywhere, which is
correct and worth confirming before any transaction exists — it is how you tell a working
dashboard from one that cannot read.

## 4. Enrol the agent's phone

Issue a code as the owner or a manager:

```bash
TOKEN=$(curl -s -X POST http://127.0.0.1:5055/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"owner@example.test","password":"…"}' | jq -r .accessToken)

curl -s -X POST http://127.0.0.1:5055/api/v1/devices/enrollment-codes \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"branchId":"<branch id from bootstrap>"}' | jq -r .code
```

Install the APK, sign in as the agent, and enter the code. The code is single-use and
short-lived; issue a fresh one per device.

The app must reach the API. On an emulator that is `10.0.2.2:5055`, which the debug build
already points at. On a real handset it needs the machine's address on the same network, which
means rebuilding with `API_BASE_URL` set accordingly — see `android/app/build.gradle.kts`.

## 5. Turn on automatic capture

The dashboard in the app distinguishes two things deliberately:

- *supported by this device* — the server's judgement about this kind of handset
- *permission granted* — whether Android has actually allowed it

Both must hold before a message can be captured. Tap **Allow SMS access**. Declining is a
legitimate answer: manual capture keeps working and nothing queued is affected.

## What to watch for

**The number that matters is the till.** At the end of a session, ask the agent what they
think their cash position is, then compare. A difference is the finding, whichever direction
it goes.

**Held transactions are the feature, not a fault.** Zazi refuses to post a message it cannot
read confidently — a truncated alert, an unrecognised template, anything without a provider
reference. Those wait for review rather than moving money on a guess. A pile of held items
means the parser has not met that network's wording yet, and the messages are exactly what is
needed to fix it.

**Unsynced work is safe.** Transactions captured with no signal live on the phone and sync
when it returns. Signing out does not delete them. Revoking the device does not delete them.

## Reporting a capture problem

The useful report is the message itself, with the personal parts replaced:

- customer phone → `0241000001`
- customer name → `JOHN SYNTHETIC`
- keep the amount, the reference format, the punctuation and the word order exactly

Those go into `contracts/sms-contract-fixtures.json`, which both the server and Android test
suites read, so a template only has to be fixed once and cannot then regress on one platform
while passing on the other.

## Known limits

- Parsers are verified against synthetic messages. Real network wording may differ, and the
  safe direction is built in: an unrecognised template is held, never posted.
- iOS and a full web reconciliation workflow do not exist yet. iOS will use manual capture —
  no iPhone can read another handset's SMS.
- The dashboard is one summary page. Evidence review and device management are still done
  through the API.

## Backing up

```bash
scripts/backup-database.sh [destination]      # defaults to ~/zazi-backups
```

Run it daily during the pilot, and before any deployment.

The embedded PostgreSQL distribution ships only `initdb`, `pg_ctl` and `postgres` — no
`pg_dump`. The script therefore takes a cold copy: it stops the server, copies the data
directory and starts it again. That is a complete, restorable backup at the cost of a short
outage, which is a fair trade for a pilot rather than adding a dependency purely to take a
backup. Copying a *running* data directory would produce something that looks like a backup
and refuses to start.

It keeps the last seven and prunes older ones, because backups filling the disk the database
runs on turn a safety measure into an outage. Restore instructions are written into each
backup as `BACKUP_INFO.txt`.

Verified end to end: a backup taken from the running pilot database was restored into a
separate cluster, started, and queried — users, devices and audit history all present.

## Who can join the pilot

There is no feature flag, and none is needed: enrolment codes already are the gate. A device
cannot reach any financial endpoint until a manager issues it a single-use code, so the pilot
group is exactly the set of devices you have enrolled. Stop issuing codes and the pilot stops
growing; revoke a device and it stops posting immediately while keeping its unsynced work.

## Building the pilot APK

```bash
cd android
./gradlew :app:assemblePilot \
  -PapiBaseUrl=http://YOUR-SERVER:5055/ \
  -PzaziKeystore=$HOME/.zazi/zazi-pilot.keystore \
  -PzaziKeystorePassword=… -PzaziKeyAlias=zazi-pilot -PzaziKeyPassword=…
```

Output: `android/app/build/outputs/apk/pilot/app-pilot.apk`

`-PapiBaseUrl` is required for a real handset. The default is `10.0.2.2`, which is the
emulator's alias for the host machine and unreachable from a phone.

The APK contains no signing key, no database password and no server secret — verified by
scanning the built artifact, not by inspection of the source.

## Configuration the backend requires

| Variable | Why |
|---|---|
| `ConnectionStrings__DefaultConnection` | Required outside Development. The API now refuses to start without it rather than falling back to an in-memory store. |
| `ZAZI_JWT_KEY` | Token signing. The API refuses to start without one outside Development. |
| `ASPNETCORE_ENVIRONMENT` | Anything other than `Development` enables HSTS, HTTPS redirection and the two refusals above. |

Note the key name: `ConnectionStrings__Default` is silently ignored, and cost hours once.
Run `scripts/verify-e2e-env.sh` after deploying — it catches exactly that.
