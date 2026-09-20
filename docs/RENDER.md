# Moving Zazi to Render

Written for someone who does not want to administer a server. **Hetzner keeps serving real
users throughout everything below.** Nothing here touches it, and nothing here changes DNS.

## What Render replaces

| On Hetzner today | On Render |
|---|---|
| systemd keeps the apps running | Render does |
| Caddy handles HTTPS and routing | Render does |
| You SSH in to deploy | You push to `main` |
| PostgreSQL you maintain | Managed PostgreSQL |

The application code does not change.

## What this costs, and why not the free tier

Three paid things. The free tier cannot do this job, and it is worth being clear why rather
than discovering it at midnight.

| Service | Plan | Roughly | Why not free |
|---|---|---|---|
| PostgreSQL | Basic | ~$7/mo | **Render deletes a free database after 30 days.** For the record of somebody's takings, that is a deadline, not a tier. |
| Web portal | Starter | ~$7/mo | Persistent disks are not available on free instances, and the portal needs one (below). |
| API | Starter | ~$7/mo | Free instances sleep when idle. The first agent to open the app after a quiet spell would wait through a cold start. |

**Around $21/month.** Confirm current prices on Render's pricing page — these move.

If tonight's budget only stretches to one, pay for **the database** first. A lost database is
unrecoverable; a slow API is merely annoying.

## The one thing that needs care

Zazi keeps encryption keys on disk. They are what keep people signed in and what protects the
forms against forgery. Render rebuilds the container on **every deploy**, so without permanent
storage every deploy would sign out every logged-in agent, and any form open at that moment
would fail on submit.

The fix is a **Disk** — Render's permanent storage — attached to the web service at `/data`.
It is already described in `render.yaml`, so it is created for you.

Two consequences, both acceptable here but worth knowing:

- A disk requires a paid instance (hence Starter above).
- A service with a disk runs as a single copy and deploys briefly interrupt rather than
  rolling over. Zazi is pinned to one copy anyway — its rate limiting counts per process, so
  a second copy would silently double every limit.

Render mounts disks owned by `root`, so the web container runs as root to write to them. The
API takes no disk and runs with reduced privileges. This is recorded in `Dockerfile.web`.

## Setting it up

### 1. Create the services

Render reads `render.yaml` from the repository and creates everything described in it.

1. Sign in to Render, choose **New → Blueprint**.
2. Connect the `isaacgyampoh/THE-CARL` repository.
3. Render shows what it will create: one database and two services. Approve it.

It will then ask for the values marked "sync: false" — these are the private ones below.

### 2. The values you paste

Render prompts for each. **Nothing here belongs in the repository, in a chat, or in a log.**

**Both services** need these two, and they must match each other exactly:

| Field | What to put |
|---|---|
| `ConnectionStrings__DefaultConnection` | see the next section |
| `ZAZI_JWT_KEY` | a long random secret — the same value in both services |

For `ZAZI_JWT_KEY`, any long random string of at least 32 characters. Generate it in Render's
own field if it offers to, or use a password manager. **The two services must have the
identical value**: the portal validates tokens the API signed, and a mismatch makes every
sign-in fail with nothing useful in the logs.

**The web service** also needs:

| Field | What to put |
|---|---|
| `Portal__PublicBaseUrl` | the service's own `onrender.com` address — **not** `app.getzazi.com` |
| `RESEND_API_KEY` | the rotated Resend key |

`Portal__PublicBaseUrl` is the address that goes inside every email Zazi sends. Until DNS is
cut over, `app.getzazi.com` still points at the old server — so setting it to that now would
send password-reset links to a machine where reset is switched off, and the test would fail
for a reason that has nothing to do with Render.

It is set to the Render address while testing and changed to `https://app.getzazi.com` as
part of the DNS cutover, not before. The service's address is only known once the service
exists, so this variable is added after creation rather than during it.

### 3. The connection string

Render gives the database a `postgres://…` address. **.NET cannot read that format**, so it
has to be rewritten. Open the database in Render, find its connection details, and build:

```
Host=HOSTNAME;Port=5432;Database=DATABASE;Username=USER;Password=PASSWORD;SSL Mode=Require;Trust Server Certificate=true
```

Substituting the four values from that page. Use the **internal** hostname — it keeps database
traffic inside Render's network rather than crossing the public internet.

Paste the same string into both services.

### 4. Signup stays off

`SignUp__Enabled` is deliberately not in the configuration. Public registration gets switched
on by adding it — set to `true` — in the dashboard **after** a real password-reset email has
actually arrived, and not before.

## GitHub settings for automatic deploys

**Secrets** (repo Settings → Secrets and variables → Actions → Secrets):

| Secret | Where it comes from |
|---|---|
| `RENDER_API_DEPLOY_HOOK` | API service → Settings → Deploy Hook |
| `RENDER_WEB_DEPLOY_HOOK` | Web service → Settings → Deploy Hook |

**Variables** (same page, Variables tab — not secret):

| Variable | Value |
|---|---|
| `API_HEALTH_URL` | the API's `onrender.com` address + `/ready` |
| `WEB_SIGNIN_URL` | the web service's `onrender.com` address + `/sign-in` |

Use the Render addresses here, not getzazi.com, until DNS has been cut over — otherwise the
pipeline would "verify" the deploy by testing the old Hetzner server.

## How a deploy works afterwards

Push to `main`. GitHub builds, runs all 602 tests against a real PostgreSQL, and only then
deploys — API first, waits for it to be healthy, then the portal, then checks the sign-in page
actually serves. Any failure stops the deploy before it reaches the portal.

## Rolling back

Render dashboard → the service → **Events** → find the last good deploy → **Rollback**.

A rollback does not undo a database migration. Both migrations in this release only add new
optional columns, so an older build ignores them and keeps working. A future migration that
removed or renamed something would not be reversible this way and would need planning first.

## Order of work

1. Blueprint creates the database and both services.
2. Paste the private values. Leave `SignUp__Enabled` off.
3. Confirm the Render addresses serve `/sign-in`, `/sign-up`, `/forgot-password`.
4. Copy the database across from Hetzner; check row counts match.
5. Send a real password-reset email and confirm it arrives.
6. Only then set `SignUp__Enabled=true`.
7. Only then change DNS.

Hetzner keeps serving real users until step 7, and stays untouched as a fallback well beyond it.

## The Android app

It is **not deployed here**. It is a native Android application, installed from an APK, and it
reaches the API over the public internet at `api.getzazi.com`.

Because the DNS cutover keeps that hostname and only changes where it points, **already
installed copies keep working with no update and no rebuild**. There is nothing to do for the
handsets as part of this migration.

There is no PWA. The portal is a normal website that works on a phone browser; it is not
installable and has no offline mode.
