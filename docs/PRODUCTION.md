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

## Where to host it (and why not Vercel)

**Vercel cannot run Zazi.** This is not a configuration problem, so it is worth being clear
about rather than attempting:

- Vercel runs Node.js, Python, Go and Ruby functions. There is no supported .NET runtime.
- The dashboard is **Blazor Server**. It holds an open SignalR connection per signed-in user
  for the life of their session, and keeps that user's UI state in the server's memory.
  Serverless functions are stateless, time-limited and cannot hold a long-lived connection.
  This is not a limitation to work around — the two models are incompatible.
- The rate limiters are in-memory and per-process. On a platform that starts a fresh process
  per request, a login limiter counts to one forever and never triggers.
- Vercel does not host PostgreSQL.

The same reasoning rules out Netlify, Cloudflare Pages and GitHub Pages. What Zazi needs is
somewhere that runs **two long-lived processes and a database** — a plain server, or a
platform that runs containers rather than functions.

| Option | Good for |
|---|---|
| **A VM** (Hetzner, DigitalOcean, Vultr) + Caddy | Cheapest and fully in your control. A small instance handles 100 agents comfortably. You manage updates and backups. |
| **Render / Railway / Fly.io** | Containers with TLS and PostgreSQL handled for you. Less to run, more per month, less control. |
| **Azure App Service** | First-class .NET and Blazor Server support, including WebSockets. |

For a 100-agent pilot, a single small VM with Caddy in front is the straightforward choice:
Caddy obtains and renews the Let's Encrypt certificate automatically, which is the part that
otherwise takes the longest. `deploy/` contains a working Caddyfile and systemd units.

**On region:** agents are in Ghana, and the nearest regions for most providers are in Europe
(roughly 100–150 ms). That is fine here — the Android app is offline-first and syncs in
batches, so it is not sensitive to round-trip latency. Do not pick a US region.

## Architecture

Cloudflare fronts the domain. Caddy terminates TLS on the server and speaks plain HTTP to
the two processes on loopback. Nothing else is exposed.

```
                     Cloudflare (DNS, TLS, WAF, proxy)
                                  │
                        ┌─────────┴─────────┐
                        │   Caddy on the VM │  ports 80/443, Cloudflare IPs only
                        └─────────┬─────────┘
             api.<DOMAIN> ────────┤──────── 127.0.0.1:5055   Zazi.Api
             app.<DOMAIN> ────────┘──────── 127.0.0.1:5056   Zazi.Web
                                  │
                           PostgreSQL, loopback only
```

`<DOMAIN>` appears in exactly one place on the server, `/etc/default/caddy`. The Caddyfile
reads it as `{$ZAZI_DOMAIN}`, so no deployment's domain is written into this repository.

For the current deployment that value is `getzazi.com`, giving `api.getzazi.com` for the API
and `app.getzazi.com` for the dashboard. `getzazi.com` itself is not served by Zazi — neither
process answers on the apex, and pointing it at this server would return a certificate error
rather than anything useful.

### Three things about this shape that are easy to get wrong

**The client IP.** With Cloudflare in front there are two proxies, not one. Caddy appends
the address it received from — a Cloudflare edge server — to `X-Forwarded-For`, and Zazi
trusts one hop and takes the last entry. Left alone it would treat the Cloudflare edge as
the client, and Zazi rate limits sign-in and device activation *per IP*. Every agent behind
the same Cloudflare point of presence would share one bucket of ten attempts a minute: one
person mistyping a password locks out a whole city.

`deploy/Caddyfile` fixes this with `trusted_proxies` and `client_ip_headers CF-Connecting-IP`,
so Caddy resolves the real client and passes that. The firewall rule below is what makes
that header trustworthy — a header is only as good as the guarantee about who can set it.

**The SSL mode.** Cloudflare must be **Full (strict)**. On *Flexible*, Cloudflare speaks
plain HTTP to the origin, Zazi sees `X-Forwarded-Proto: http`, redirects to HTTPS, and
Cloudflare requests over HTTP again — an endless redirect that looks like an application
bug and is not one.

**The WebSocket.** The dashboard is Blazor Server and holds a SignalR connection open for
the life of a session. Caddy upgrades WebSockets without configuration, but the default
read timeout cuts an idle stream, and a dashboard left open on a desk is idle for long
stretches. The Caddyfile disables that timeout for the dashboard only. The symptom if you
skip it is a page that reconnects every few minutes and reads as a flaky network.

## The server

The smallest sensible production server for the current workload:

| | Minimum | Why |
|---|---|---|
| **OS** | Ubuntu 24.04 LTS | `install.sh` targets it. Any current Debian-family release works with minor changes. |
| **CPU** | 2 vCPU | Two .NET processes and PostgreSQL. One vCPU works until a sync batch and a dashboard render coincide. |
| **RAM** | 2 GB | ~250 MB per .NET process, ~256 MB PostgreSQL shared buffers, the rest headroom. 1 GB survives until the first large batch sync, then the OOM killer takes PostgreSQL. |
| **Disk** | 25 GB SSD | OS ~8 GB, runtime ~1 GB, database small for a long time. Sized for logs and 14 days of local backups, not for the data. |
| **Region** | Europe | Agents are in Ghana; Europe is ~100–150 ms away. Fine — the app is offline-first and syncs in batches. Do not pick a US region. |

A 100-agent pilot fits comfortably. Grow RAM before CPU: PostgreSQL benefits first.

**Required ports.** Inbound 443 and 80 from Cloudflare's ranges only, and 22 for you.
Nothing else. **5055 and 5056 are never opened** — the services bind to `127.0.0.1` and the
firewall would refuse regardless. Both, because one of them will eventually be wrong.

**Packages, accounts, directories** — all created by `deploy/install.sh`:
`aspnetcore-runtime-8.0`, `postgresql`, `caddy` (≥ 2.7, from Caddy's own repository — the
distribution package lags and `trusted_proxies` needs 2.7), `ufw`. A `zazi` system account
with no shell and no home. `/opt/zazi` (application, root-owned), `/etc/zazi` (secrets, mode
600), `/var/lib/zazi/keys` (data-protection key ring, owned by `zazi`, mode 700),
`/var/log/caddy`.

### On Hetzner Cloud

| | Recommendation |
|---|---|
| **Shape** | **CX22** — 2 vCPU, 4 GB, 40 GB. Comfortably above the minimum; the next size down is 2 GB and leaves no headroom for a batch sync landing while the dashboard renders. |
| **Location** | **Falkenstein, Nuremberg or Helsinki.** ~130 ms to Ghana. Fine — Zazi is offline-first and syncs in batches. |
| **Image** | Ubuntu 24.04 LTS |
| **Firewall** | Hetzner Cloud Firewall (console) *or* the host `ufw` that `install.sh` configures. One is enough. |

Two things Hetzner does differently from most providers, both in your favour:

**One firewall, not two.** The image ships no preinstalled `iptables` ruleset, so `ufw` is the
only thing filtering and what it reports is what is happening. `install.sh` still checks for a
persistent ruleset and removes it if present, which is a no-op here.

If you prefer to manage ingress in the Hetzner console instead, the rules are the same: allow
TCP 80 and 443 from Cloudflare's ranges, TCP 22 from your address, deny the rest. Do not
configure both and expect them to agree — pick one.

**IPv6 is on by default and is free.** Cloudflare proxies IPv6 origins, so an AAAA record
works as well as an A record. Nothing in Zazi cares which; the Caddyfile already trusts
Cloudflare's IPv6 ranges.

**Take a snapshot** once `https://api.getzazi.com/ready` returns 200 and before real agents
are on it. Hetzner snapshots are cheap and restore in minutes, which is a far shorter path
back than rebuilding from this runbook.

## Database

**PostgreSQL 16** (what Ubuntu 24.04 ships; the test suite runs against 16.2). No extensions
are required — Zazi uses plain relational features, `numeric`, and partial unique indexes.

**On the same VM**, not a managed database, at this size. The application and database are
the only tenants, loopback is faster and simpler than TLS to a remote host, and a managed
instance adds monthly cost and a network hop for no benefit until you outgrow one machine.
Revisit when you run more than one API process.

**Migrations do not run by default in production.** `Database:MigrateOnStartup` defaults to
*on* in Development and *off* everywhere else, because several instances rolling out together
would race each other through the same migration, and a deployment that only meant to ship
code would silently alter the schema with no step to review or gate.

The API then **refuses to start** when migrations are pending, naming the first one. That is
deliberate — serving traffic against a schema the code does not match surfaces as scattered
column-not-found errors rather than one clear failure — but on a fresh server *every*
migration is pending, so the first start fails unless you have chosen one of these:

- **Single API process (this deployment).** Set `Database__MigrateOnStartup=true` in
  `/etc/zazi/api.env`. `install.sh` writes it for you. The race the default guards against
  cannot happen with one instance.
- **More than one API process.** Leave it off and migrate as a deployment step before
  rolling out:
  ```bash
  ConnectionStrings__DefaultConnection="…"     scripts/dotnet.sh ef database update --project src/Zazi.Infrastructure
  ```

There is no destructive fallback anywhere: a migration that cannot apply stops the service
rather than dropping anything. See `MIGRATION_SAFETY.md`.

**SSL to the database** is unnecessary on loopback and adds nothing. Add `SSL Mode=Require`
only if you move PostgreSQL to another host.

**Connection pooling** is Npgsql's default and is appropriate; do not add PgBouncer at this
size.

## Backups

`deploy/backup.sh`, from a cron entry or systemd timer, **daily**. It backs up three things
because the database alone is the backup people take and the other two are what they wish
they had taken:

| What | Why |
|---|---|
| The database | The transactions. `pg_dump`, custom format, no outage. |
| `/etc/zazi` | Connection string and JWT signing key. Restore a database without the key and every session issued before is void. |
| `/var/lib/zazi/keys` | Data-protection key ring. Lose it and every signed-in owner is signed out. |

Keeps 14 days locally. Set `ZAZI_BACKUP_REMOTE` to an rsync destination for offsite copies —
a backup on the same machine protects against a mistake, not against losing the machine.

**Restore-test one.** A backup nobody has restored is a hope, not a backup.

## Health endpoints

| Endpoint | Says |
|---|---|
| `https://api.<DOMAIN>/health` | The process is up. |
| `https://api.<DOMAIN>/ready` | **The process can reach its database.** 503 when it cannot. |
| `https://api.<DOMAIN>/api/v1/status` | Service name only — no version, no backing-store detail. |

**`/ready` is the one that matters.** A process answering `/health` while its database is
gone is the failure people actually hit, and it looks exactly like health. Point your uptime
monitor at `/ready`.

## Deployment runbook

Steps marked **you** need a person — an account, a payment, a decision, or a device.
Steps marked **prepared** are already written in this repository.

| # | Step | Who |
|---|---|---|
| 1 | Provision the VM (spec above) | **you** — provider dashboard |
| 2 | Add the domain to Cloudflare | **you** — Cloudflare dashboard |
| 3 | DNS records, see below | **you** — Cloudflare dashboard |
| 3b | **Oracle only:** open 80/443 in the VCN Security List | **you** — OCI console |
| 4 | Install runtime, PostgreSQL, Caddy, firewall | **prepared** — `deploy/install.sh` |
| 5 | Create database and role | **prepared** — same script |
| 6 | Write `/etc/zazi/api.env` | **prepared** — same script, secrets generated on the server |
| 7 | Write `/etc/zazi/web.env` | **prepared** — same script |
| 8 | Install systemd units | **prepared** — same script |
| 9 | Configure Caddy | **prepared** — same script |
| 10 | Publish the application to the VM | **you** — one command, below |
| 11 | Start the services (migrations run on first start) | **you** — one command, below |
| 12 | Verify `/health` | **you** — browser |
| 13 | Verify `/ready` — proves the database | **you** — browser |
| 14 | Verify the dashboard signs in | **you** — browser |
| 15 | Verify HTTPS and Cloudflare | **you** — browser |
| 16 | Build the Android release with the real URL | **prepared** — command below |
| 17 | Physical Pixel test | **you** — device required |
| 18 | Production signing | **you** — keystore required |
| 19 | Release | **you** |

### The commands you cannot avoid

There are three. Everything else is a dashboard.

**On the VM, once** — installs everything and stops before starting anything:

```bash
sudo ZAZI_DOMAIN=<your-domain> bash install.sh
```

**From your machine** — publishes the built application to the server:

```bash
scripts/dotnet.sh publish src/Zazi.Api -c Release -o /tmp/zazi-api
scripts/dotnet.sh publish src/Zazi.Web -c Release -o /tmp/zazi-web
rsync -a /tmp/zazi-api/ root@<server>:/opt/zazi/api/
rsync -a /tmp/zazi-web/ root@<server>:/opt/zazi/web/
rsync -a scripts/healthcheck.sh deploy/backup.sh root@<server>:/opt/zazi/scripts/
```

**On the VM** — start everything:

```bash
sudo systemctl enable --now zazi-api zazi-web zazi-healthcheck.timer
sudo systemctl reload caddy
```

Then open `https://api.<your-domain>/ready` in a browser. A `200` with
`{"status":"ready"}` means the API is up, TLS works, Cloudflare is proxying and the database
is reachable. That single page is the deployment test.

## Cloudflare configuration

Two A records, both **PROXIED (orange cloud)**:

| Type | Name | Content | Proxy |
|---|---|---|---|
| A | `api` | your VM's public IPv4 | **Proxied** |
| A | `app` | your VM's public IPv4 | **Proxied** |

**Why proxied rather than DNS-only.** Proxied is what gives you the WAF, DDoS absorption and
a hidden origin address, and it is what makes the firewall rule possible: with the origin
reachable only from Cloudflare's ranges, nobody can bypass those protections by connecting
to the IP directly. It is also what populates `CF-Connecting-IP`, which is how Zazi sees the
real client and rate limits per agent rather than per point of presence.

Grey-cloud both records and three things break at once: the origin is publicly addressable,
the firewall blocks the traffic anyway, and `CF-Connecting-IP` is absent.

**SSL/TLS mode: Full (strict).** Not Flexible — see the architecture note above. Caddy holds
a real Let's Encrypt certificate, so strict validation succeeds.

**Leave these alone:** Cloudflare's Rocket Loader and Auto Minify can interfere with Blazor's
SignalR negotiation. Zazi's own rate limiting is per-IP and sufficient; Cloudflare rate
limiting rules on top are optional and not required by this deployment.

## Android release build

Once `https://api.<your-domain>/ready` answers:

```bash
cd android
./gradlew :app:assembleRelease -PapiBaseUrl=https://api.<your-domain>/
```

The build **refuses to produce an artifact** without an explicit HTTPS URL. It rejects a
missing URL, any `http://` URL, and by extension the emulator address that used to be the
silent default. Nothing is written to `outputs/` when it refuses — an earlier version failed
the build but left a complete APK behind carrying the rejected URL, which is worse than no
guard.

Signing is separate and needs the production keystore. Without it the APK is unsigned and
cannot be distributed.

## Before you let real agents on

- [ ] `https://api.<DOMAIN>/health` and `/ready` both return 200
- [ ] `https://app.<DOMAIN>` serves the sign-in page and sign-in works
- [ ] Plain `http://` redirects to `https://` rather than serving anything
- [ ] Both processes are bound to `127.0.0.1` — check with `ss -tlnp`
- [ ] PostgreSQL is not reachable from outside the server
- [ ] Both DNS records are **proxied** (orange cloud), not DNS-only
- [ ] Cloudflare SSL/TLS mode is **Full (strict)** — Flexible causes an endless redirect
- [ ] The VM's IP is *not* reachable on 443 from anywhere but Cloudflare
- [ ] Two sign-in failures from two different devices do not share a rate-limit budget —
      the quickest check that `CF-Connecting-IP` is reaching the application
- [ ] The dashboard stays connected when left open for ten minutes (SignalR timeout)
- [ ] `scripts/healthcheck.sh` passes against the real URLs
- [ ] The health-check timer is enabled and `OnFailure=` points at something that reaches you
- [ ] A backup has been taken *and restored somewhere else*
- [ ] `ZAZI_BACKUP_REMOTE` is set, and a backup has arrived at the far end
- [ ] The keystore is backed up off the server
- [ ] The first owner account exists (`Zazi.Bootstrap`; it refuses if an organization already exists)
- [ ] A signed release APK installs on a real handset and captures one real SMS end to end
- [ ] A worker activates on that handset with a code from the owner portal

That last item is the one that cannot be skipped. Everything above can pass while the app
still fails on a real carrier message.

## What production does not yet include

Stated plainly so it is not discovered later:

- **No automated deploy.** Deployment is manual; there is no CI pipeline publishing releases.
- **No log aggregation.** Logs go to standard output. The dashboard's trace view covers
  application-level diagnosis, but there is no searchable history across restarts.
- **No notification channel.** `scripts/healthcheck.sh` knows what healthy means here and
  exits non-zero when it is not — `deploy/zazi-healthcheck.timer` runs it every minute, and
  systemd's `OnFailure=` starts whatever you point it at. What is missing is the thing it
  points at: how you want to be woken up is not something this repository can choose for you.
- **No horizontal scaling story.** One instance of each process. The rate limiters are
  in-memory and per-process, so a second instance would double the effective limits.
- **Backups are offsite only if you point them there.** `backup-database.sh` writes locally
  and, with `ZAZI_BACKUP_REMOTE` set, copies each backup to an rsync destination. Nothing
  sets that for you, and a backup on the same machine as the database protects against a
  mistake, not against losing the machine.

None of these block a controlled pilot. All of them matter before this is the system of
record for someone's livelihood.
