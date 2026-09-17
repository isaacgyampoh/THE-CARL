# Deploying Zazi to production

`PILOT.md` covers running the stack on a laptop for a small trial. This covers a real
deployment: a server you control, a domain name, and TLS.

The distinction matters because the release build of the Android app **cannot connect over
plain HTTP**, and both server processes **refuse to start** serving plain HTTP outside
Development. That is deliberate — Zazi carries sign-in credentials and financial evidence —
but it means TLS is not optional and cannot be deferred.

## What you have to provide

These four cannot be produced from inside the repository. Everything else below is already
built and verified.

| | Why it cannot be automated |
|---|---|
| **A server** | Somewhere to run two .NET processes and PostgreSQL. A single small VM is enough for a 100-agent pilot. |
| **A domain name** | The app has to reach a stable address. An IP address will not do: certificates are issued for names. |
| **A TLS certificate** | Let's Encrypt is free and automatic via Caddy or nginx + certbot. Self-signed will not work — Android rejects it and there is no override in the release build. |
| **A release keystore** | Signs the APK. Whoever holds it controls all future updates; lose it and you cannot update the app at all, only publish a new one under a new identity. Back it up somewhere other than the server. |

## Architecture

Put TLS in front, terminate it there, and speak plain HTTP to the two processes on loopback:

```
            ┌──────────── TLS (Let's Encrypt) ────────────┐
Android ────┤  api.yourdomain   → 127.0.0.1:5055  Zazi.Api │
Browser ────┤  app.yourdomain   → 127.0.0.1:5056  Zazi.Web │
            └─────────────────────────────────────────────┘
                                   │
                            PostgreSQL (loopback only)
```

Both processes must then be told TLS is terminated ahead of them:

```
Zazi__BehindTlsProxy=true
```

Without it they refuse to start, because from their own point of view they are serving
cleartext and they cannot tell the difference between "behind a proxy" and "exposed". With
it they also trust `X-Forwarded-For`, which the per-IP login rate limiter needs — otherwise
every request appears to come from the proxy and all users share one bucket, so one attacker
can lock out everybody.

**The proxy must set `X-Forwarded-For` and `X-Forwarded-Proto`, and must not be reachable
except through itself.** Bind both application processes to `127.0.0.1`, never `0.0.0.0`.

## Configuration

Secrets go in the environment, never in `appsettings.json` and never in git.

### Zazi.Api

```sh
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5055
Zazi__BehindTlsProxy=true
ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=5432;Database=zazi;Username=zazi;Password=…"
ZAZI_JWT_KEY="…"          # at least 32 random bytes; the API will not start without it
```

### Zazi.Web

```sh
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5056
Zazi__BehindTlsProxy=true
ConnectionStrings__DefaultConnection="…"   # the same database as the API
ZAZI_JWT_KEY="…"                           # the same key as the API
Zazi__DataProtectionKeyPath=/var/lib/zazi/keys
```

`Zazi__DataProtectionKeyPath` is required and must survive restarts. It holds the keys that
encrypt the session cookie and antiforgery tokens. Point it at a container's ephemeral
filesystem and every deploy signs every agent out mid-shift; run two instances without
sharing it and sign-in works intermittently, which is far harder to diagnose than it sounds.

### The variable name that has cost hours before

It is `ConnectionStrings__DefaultConnection` — two underscores, and `DefaultConnection`, not
`Default`. `ConnectionStrings__Default` is silently ignored. Outside Development the API now
refuses to start rather than falling back to an in-memory store, so this fails loudly instead
of accepting real transactions and discarding them, but the name is still worth getting right
the first time.

## Database

Use a real PostgreSQL 16 installation, not the standalone binaries under `~/.zazi-testdb` —
those exist so tests can run on a laptop without Docker or root.

The ledger depends on two PostgreSQL behaviours, which is why SQLite is not an alternative:
partial unique indexes make submissions idempotent, and `INSERT … ON CONFLICT DO UPDATE`
keeps balances correct when several devices sync at once.

### Migrations

Migrations do **not** run automatically outside Development. This is deliberate: a deploy
meant only to ship code should not silently alter a schema holding financial records.

**Follow `MIGRATION_SAFETY.md`** — it is the authority here, and it requires generating the
idempotent SQL and reading it before anything touches the database. The short version is: back
up first, generate the script, review it, then apply it.

```sh
scripts/backup-database.sh /var/backups/zazi
dotnet ef migrations script --idempotent \
  --project src/Zazi.Infrastructure --startup-project src/Zazi.Api --output migrate.sql
# read migrate.sql, then apply it
```

`dotnet ef` spawns further `dotnet` processes resolved from `PATH`, so a system-wide SDK can
shadow the one pinned in `global.json`. If it reports the wrong SDK version, put the pinned
SDK's directory first on `PATH` — `scripts/start-stack.sh` does this and is worth copying.

If the schema is behind, the API refuses to start and names the first missing migration.

### Backups

`scripts/backup-database.sh` takes a cold copy, because the standalone distribution ships no
`pg_dump`. **On a real PostgreSQL installation, use `pg_dump` instead** — it does not require
stopping the database. Whichever you use, restore it somewhere else and sign in before you
trust it. An untested backup is not a backup.

## Building the release APK

The keystore lives outside the repository and its passwords are passed on the command line or
set in `~/.gradle/gradle.properties`. They must not go in `android/gradle.properties`, which
is tracked by git — the build fails if they do.

```sh
cd android
./gradlew assembleRelease \
  -PapiBaseUrl=https://api.yourdomain/ \
  -PzaziKeystore=/secure/path/zazi-release.keystore \
  -PzaziKeystorePassword=… -PzaziKeyAlias=… -PzaziKeyPassword=…
```

Verify what you are about to distribute:

```sh
apksigner verify --print-certs -v app/build/outputs/apk/release/app-release.apk
```

`apiBaseUrl` **must be `https://`**. The release build declares
`android:usesCleartextTraffic="false"`, so an `http://` address produces an app that installs,
opens, and fails every request.

Never distribute a `pilot` build. It is minified and signed exactly like release but permits
cleartext for any host, which exists so a trial can run on office wifi.

## Before you let real agents on

- [ ] `https://api.yourdomain/health` and `/ready` both return 200
- [ ] `https://app.yourdomain` serves the sign-in page and sign-in works
- [ ] Plain `http://` redirects to `https://` rather than serving anything
- [ ] Both processes are bound to `127.0.0.1` — check with `ss -tlnp`
- [ ] PostgreSQL is not reachable from outside the server
- [ ] A backup has been taken *and restored somewhere else*
- [ ] The keystore is backed up off the server
- [ ] The first owner account exists (`Zazi.Bootstrap`; it refuses if an organization already exists)
- [ ] A signed release APK installs on a real handset and captures one real SMS end to end

That last item is the one that cannot be skipped. Everything above can pass while the app
still fails on a real carrier message.

## What production does not yet include

Stated plainly so it is not discovered later:

- **No automated deploy.** Deployment is manual; there is no CI pipeline publishing releases.
- **No log aggregation.** Logs go to standard output. The dashboard's trace view covers
  application-level diagnosis, but there is no searchable history across restarts.
- **No alerting.** Nothing pages anyone if the API stops. Health endpoints exist for an
  uptime monitor to poll; nothing polls them yet.
- **No horizontal scaling story.** One instance of each process. The rate limiters are
  in-memory and per-process, so a second instance would double the effective limits.
- **Backups are not offsite.** `backup-database.sh` writes to the same machine by default.

None of these block a controlled pilot. All of them matter before this is the system of
record for someone's livelihood.
