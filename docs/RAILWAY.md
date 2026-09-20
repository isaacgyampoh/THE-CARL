# Moving Zazi to Railway

Written for someone who does not want to administer a server. Hetzner stays running and
serving real users throughout everything below. Nothing here touches it.

## What Railway replaces

| On Hetzner today | On Railway |
|---|---|
| systemd keeps the apps running | Railway does |
| Caddy handles HTTPS and routing | Railway does |
| You SSH in to deploy | You push to `main` |
| PostgreSQL you maintain | Managed PostgreSQL |

The application code does not change. Everything below is configuration.

## The one thing that needs care

Zazi keeps a set of encryption keys on disk. They are what keep people signed in and what
protects the forms against forgery. On Hetzner they live in a permanent folder.

**Railway wipes and rebuilds the container on every deploy.** Without somewhere permanent to
put those keys, every deploy signs out every agent who was logged in, and any form open at
that moment fails when submitted.

The fix is a **Volume** — Railway's permanent storage — attached to the web service and
mounted at `/data`. It is two clicks and no code change. Do not skip it.

One consequence worth knowing: Railway mounts volumes owned by `root`, so the web container
runs as root in order to write to it. The API container does not, because it takes no volume.
This is noted in `Dockerfile.web` with instructions for tightening it later.

## Services to create

Three, in one Railway project:

1. **Postgres** — from Railway's database templates.
2. **API** — from this GitHub repo. Set its config file to `railway.api.json`.
3. **Web** — from the same repo. Set its config file to `railway.web.json`.

Both app services build from the `Dockerfile.api` / `Dockerfile.web` in this repository, which
is why the config files exist: they tell Railway which service builds which Dockerfile.

**Turn OFF "Auto Deploy" on both app services.** Deploys are driven by the GitHub Actions
workflow instead, so that a commit whose tests fail cannot reach production. Railway's own
auto-deploy would ship it regardless.

## Environment variables

Set these in each service's Variables tab. Values marked **secret** should be typed or pasted
directly into Railway by you — they should never appear in this repository, in a chat, or in
a log.

### Both the API and the Web service

| Variable | Value |
|---|---|
| `ASPNETCORE_URLS` | `http://0.0.0.0:${{PORT}}` |
| `ConnectionStrings__DefaultConnection` | see below |
| `ZAZI_JWT_KEY` | **secret** — must be byte-for-byte identical in both services |
| `Zazi__BehindTlsProxy` | `true` |

`ASPNETCORE_URLS` matters: Railway picks the port and injects it as `PORT`. The old fixed
ports 5055 and 5056 are gone; the app has to listen on whatever it is given.

`ZAZI_JWT_KEY` being identical is not a detail. The web portal validates tokens the API
signed. If the two differ, sign-in fails with a generic rejection and nothing in the logs
explains why.

**The connection string.** Railway publishes a `DATABASE_URL` in `postgres://` form, which
.NET cannot read. Use Railway's variable references to build the form it can:

```
Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}};SSL Mode=Require;Trust Server Certificate=true
```

(If the Postgres service is named something other than `Postgres`, use that name.)

### API only

| Variable | Value |
|---|---|
| `Database__MigrateOnStartup` | `true` |

This is what applies pending database migrations when the API starts. Without it the API
refuses to start while migrations are pending — deliberately, so that a schema change is
never half-applied — and on a fresh database every migration is pending.

### Web only

| Variable | Value |
|---|---|
| `Zazi__DataProtectionKeyPath` | `/data/keys` |
| `Portal__PublicBaseUrl` | `https://app.getzazi.com` |
| `Email__Provider` | `Resend` |
| `RESEND_API_KEY` | **secret** — the rotated key, pasted by you |
| `SignUp__Enabled` | **leave unset until a real reset email has arrived** |

`Zazi__DataProtectionKeyPath` must match where the Volume is mounted. `/data/keys` with the
volume on `/data`.

`Portal__PublicBaseUrl` is what every emailed link is built from. It is read from
configuration and never from the incoming request, because a link built from a request header
is a link an attacker chooses — and these links verify accounts and reset passwords.

## GitHub settings the pipeline needs

**Secrets** (Settings → Secrets and variables → Actions → Secrets):

| Secret | What it is |
|---|---|
| `RAILWAY_TOKEN` | A Railway project token, created in Railway's project settings |

**Variables** (same page, Variables tab — these are not secret):

| Variable | Value |
|---|---|
| `RAILWAY_API_SERVICE` | the API service's name in Railway |
| `RAILWAY_WEB_SERVICE` | the Web service's name in Railway |
| `API_HEALTH_URL` | the API's Railway URL + `/ready` |
| `WEB_SIGNIN_URL` | the Web service's Railway URL + `/sign-in` |

Use the Railway-provided URLs here, not the getzazi.com ones, until DNS has been cut over.
The pipeline checks these after deploying, and pointing them at Hetzner would mean the
pipeline reports success by testing the old server.

## How a deploy works afterwards

Push to `main`. GitHub Actions builds, runs all 602 tests against a real PostgreSQL, and only
then deploys — API first, waits for it to be healthy, then the Web portal, then checks the
sign-in page is actually serving. Any failure stops the deploy.

## Rolling back

In the Railway dashboard: open the service, find the previous deployment in the list, press
**Redeploy**. It goes back to that exact build.

A rollback does **not** undo a database migration. Both migrations in this release only add
new optional columns, so an older build ignores them and keeps working — but a future
migration that removes or renames something would not be reversible this way, and would need
planning before it ships.

## Order of work

1. Create the project, the three services and the volume.
2. Set the variables. Leave `SignUp__Enabled` off.
3. Let the pipeline deploy. Confirm the Railway URLs serve `/sign-in`, `/sign-up` and
   `/forgot-password`.
4. Copy the database across from Hetzner, and check row counts match.
5. Send a real password-reset email and confirm it arrives.
6. Only then set `SignUp__Enabled=true`.
7. Only then change DNS.

Hetzner keeps serving real users until step 7, and stays untouched as a fallback well beyond it.
