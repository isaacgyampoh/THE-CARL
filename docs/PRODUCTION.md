# Deploying Zazi to production

> **Zazi's production today runs on Render**, not on a server of our own: two Docker services
> (`zazi-api`, `zazi-web`) and a managed PostgreSQL, all in Frankfurt, defined by `render.yaml`
> and described in [RENDER.md](RENDER.md). Capacity, backups and the restore drill are in
> [PRODUCTION_READINESS.md](PRODUCTION_READINESS.md).
>
> This document remains the guide for running Zazi on **a server you control** — which is where
> it will move if hosting costs or data residency ever require it. Nothing below is obsolete;
> it is simply a different deployment target from the one in use.

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
rsync -a --delete /tmp/zazi-api/ root@<server>:/opt/zazi/api/
rsync -a --delete /tmp/zazi-web/ root@<server>:/opt/zazi/web/
rsync -a scripts/healthcheck.sh deploy/backup.sh root@<server>:/opt/zazi/scripts/
```

**`--delete` is not optional, and it is not there for tidiness.** A publish directory is a
closed set: the application loads what is next to it in preference to the shared framework.
Without `--delete`, a file from an earlier publish that no longer exists in the current one
stays on the server forever — and if that file is a `Microsoft.AspNetCore.*` assembly left
behind by a self-contained or differently-targeted build, the application silently runs on it
instead of the runtime you installed. Nothing reports this. The binaries you deployed hash
correctly, the service starts, and the behaviour is from code you are not looking at.

A quick check that the directory is clean, run on the server:

```bash
ls -1 /opt/zazi/web | grep -c '^Microsoft\.AspNetCore'   # expect 0
ls -1 /opt/zazi/api | grep -c '^Microsoft\.AspNetCore'   # expect 0
```

Anything other than `0` means a stale publish is being loaded, and the fix is to empty the
directory and deploy again rather than to sync over the top of it.

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

## Password reset

An account holder who has forgotten their password can get back in without an administrator.
**There is no switch for it** — unlike signup, which a deployment opts into, somebody locked
out of their own records needs a route back whether or not this deployment accepts new
signups. It works as soon as two things are configured:

```
Portal__PublicBaseUrl=https://app.getzazi.com
```

plus working email (see above). Without the URL, `/forgot-password` says password reset is
unavailable and tells people to contact an administrator, rather than pretending to send a
message it cannot build a link for. The portal still starts either way — adding this setting
must not be able to take a running deployment down.

### The flow

1. `/forgot-password` — anonymous, rate limited by the same per-IP policy as sign-in.
2. The person enters an address. **The page says exactly the same thing whatever happens.**
3. For an account that is active and uses a password, a 32-byte random token is generated.
   Only its SHA-256 is stored, alongside an expiry 30 minutes out.
4. The link is emailed as `Zazi <no-reply@getzazi.com>` through Resend.
5. `/reset-password?token=…` checks the token *without consuming it* — a page load is not a
   reset, and mail clients prefetch links.
6. Submitting a new password consumes the token and sets the password in one transaction.
7. Every session is closed and the security stamp rotated, so the new password is required
   everywhere immediately.
8. A confirmation email goes out, with nothing to click.

### Why 30 minutes and not 24 hours

Email verification links last a day; these last half an hour. A verification link only proves
an address. This one changes the password on an account holding financial records, and it sits
in a mailbox for exactly as long as it is valid.

### Things it deliberately does not do

**It does not reveal whether an address is registered.** Same message, same status, no redirect
difference — and the response is held to a floor of 900ms so that the *timing* cannot say
either. A known address writes a token and calls the email provider; an unknown one does a
single lookup. Without the floor a stopwatch reads off the difference and the identical wording
buys nothing.

**It does not reset inactive or activation-only accounts.** An owner who has not verified their
address needs the verification link, not a reset — issuing one would activate an account
through the back door. Workers who authenticate by activation code have no password to reset.

**It does not spend the token on a weak password.** The policy is checked first, so someone who
picks a short password can simply try again rather than having to request a whole new link.

**Only one concurrent use wins.** The token is cleared and claimed by a single conditional
`UPDATE`; the loser is refused rather than quietly writing a second password over the first.

**Nothing sensitive reaches the logs.** Audit entries record `PASSWORD_RESET_REQUESTED` and
`PASSWORD_RESET_COMPLETED` against the user and organization, and contain no token, no hash and
no password. Log lines mask the address.

### After a reset

Every session is closed: the security stamp is rotated, `AuthSessions` are revoked and
refresh-token families are killed, through the same `IIdentityRevocationService` an
administrator revocation uses, with trigger `PasswordChanged`. A reset that left whoever forced
it still signed in would defeat the point. Lockout counters are cleared too — someone who was
locked out and has now proved control of the mailbox has answered the question lockout asked.

## Self-service signup

A business can create its own Zazi account without anyone at Zazi being involved. It is
**off by default** — opening a financial system to public registration is a decision to make
deliberately, and a pilot running with a handful of known agents has no reason to accept
accounts from whoever finds the URL.

### Turning it on

Email has to work first. A signup that cannot send its verification link creates accounts
nobody can open. Configure Resend, confirm it with the live smoke test above, then add to
`/etc/zazi/web.env`:

```
Portal__PublicBaseUrl=https://app.getzazi.com
SignUp__Enabled=true
```

and `sudo systemctl restart zazi-web`. The dashboard refuses to start if `SignUp__Enabled` is
true without a valid absolute `Portal__PublicBaseUrl`.

`Portal__PublicBaseUrl` is read from configuration and **never from the request's `Host`
header**. These links activate accounts and change passwords, so one assembled from a header
the caller chose is a link an attacker can aim at a site they control.

### What happens

1. Someone fills in the form at `/sign-up`: business name, their name, email, password.
2. Zazi creates the organization, its first branch and the owner **in one transaction**.
3. The owner is written **inactive and unverified**. Login refuses an inactive user, so at
   this point there is no account anyone can sign into — including whoever typed the address,
   if it was not theirs to type.
4. A verification link is emailed. The token is 32 random bytes; only its SHA-256 is stored.
5. Opening the link activates the owner and clears the token, which is what makes it
   single-use. It expires after 24 hours.
6. The owner signs in and sets up branches, staff and activation codes as usual.

### Things it deliberately does not do

**It does not say whether an address is already registered.** A second signup on a known
address returns exactly the same response as a new one, creates nothing, and sends the real
account holder an email saying someone tried. Otherwise the form is a way to enumerate Zazi's
customers, which would undo the care the sign-in form already takes.

**It does not grant anything beyond the new tenant.** The owner gets `OWNER` for their own
organization and nothing else — never `PLATFORM_ADMIN`, which can reach every tenant.

**It does not roll back the account if the email fails.** The organization is committed first
and the send reported separately, so a transient delivery problem does not lose the tenant.
The page then offers to resend rather than telling someone to watch an inbox that will stay
empty.

**Resends are throttled** to one per address every couple of minutes, and issue a *new* token
that retires the previous one. Without the throttle, the form is a way to have Zazi send
unlimited mail to an address chosen by whoever is asking — which costs the sending domain its
reputation rather than costing them anything.

## If the dashboard redirects to /sign-in forever

The symptom: `https://app.<your-domain>/sign-in` answers `302` pointing at `/sign-in`. Nobody
can sign in, so nothing is reachable. `/health` still returns `200`, which makes it look like
the application is fine.

It looks fine in the logs too. A stream of redirects to the sign-in page is exactly what a
healthy portal serving signed-out visitors produces, so there is nothing anomalous to find.

**Read the `Location` header.** It carries the answer and takes one command:

```bash
curl -sSI https://app.<your-domain>/sign-in | grep -i '^location:'
```

| `ReturnUrl` | Meaning |
|---|---|
| `%2Ferror` | Rendering the page threw, and the error page sent the visitor back. The exception is in `journalctl -u zazi-web`; the redirect is a symptom, not the fault. |
| `%2Fsign-in` | No endpoint matched `/sign-in`, so it fell to the deny-by-default fallback policy. Almost always a stale publish directory — see `--delete` above. |

Both causes are now guarded at startup. `AnonymousRouteGuard` checks the endpoints the
application actually built and refuses to start if either `/sign-in` or the error page is
missing or not anonymous, naming the page. So on a current build, a portal that starts at all
has both of these routes working — which means a loop on a running instance points at the
exception, not at routing.

## Transactional email

Zazi sends email through [Resend](https://resend.com). One provider, one endpoint, one
credential. The application posts to `https://api.resend.com/emails` directly rather than
through a client library: Resend publishes no official .NET SDK, the community package is
pre-1.0, and what it would save is a single HTTP call.

### 1. Verify the domain

At <https://resend.com/domains>, add `getzazi.com`. Resend gives you a set of records; add
them in Cloudflare exactly as shown, and set every one of them to **DNS-only (grey cloud)**.
Proxying is for HTTP. A proxied MX or TXT record does not resolve to what the receiving mail
server needs, and verification silently never completes.

| Type | Purpose | Notes |
|---|---|---|
| `TXT` | DKIM | The long public key. Copy it whole; a truncated value fails verification with no useful error. |
| `MX` | Bounce handling | On the `send` subdomain, not the apex. It does not affect your inbound mail. |
| `TXT` | SPF | Merge into your existing SPF record if you already have one — a domain with **two** SPF records is treated as having none. |

Verification usually completes in minutes. Nothing sends until it does; an unverified domain
comes back as a `403` with `The from domain is not verified`, which is logged in full.

**Status: `getzazi.com` is verified.** Confirmed on 19 September 2026 by a live send from
`no-reply@getzazi.com`, which an unverified domain would have rejected. This step is done
unless the DNS records are changed.

DMARC is not required by Resend and is worth adding anyway once DKIM and SPF pass, starting
at `p=none` so you see reports before anything is rejected.

### 2. Create a key

At <https://resend.com/api-keys>, create one with **Sending access**, not full access. The
dashboard only ever posts a message; a full-access key additionally grants read access to
every message the domain has ever sent, which is a meaningful difference in what a leak of
this server costs.

Resend shows the key once. It starts `re_`.

### 3. Configure the server

Both lines, in `/etc/zazi/web.env` only:

```
Email__Provider=Resend
RESEND_API_KEY=re_...
```

Then `sudo systemctl restart zazi-web`.

**Only that file.** The API sends no email — signup and verification are the dashboard's —
so the key does not go in `api.env`. The file is mode `600` and owned by root; `install.sh`
writes both lines commented out, ready to uncomment.

**The key is never read from configuration.** Not `appsettings.json`, not a user-secrets
file — the application calls `Environment.GetEnvironmentVariable("RESEND_API_KEY")` and
consults nothing else. A key placed in a settings file does not work, which is the point:
settings files are committed.

### What happens when it is wrong

| Situation | Behaviour |
|---|---|
| `Email__Provider=Resend`, no key | **The dashboard refuses to start**, naming the variable. Deliberate: a deployment that believes it can send and cannot leaves people waiting for a message that is never coming. |
| Neither line set | Starts normally. Every send is reported as failed and logged as an error. Nothing silently disappears. |
| Key rejected (`401`/`403`) | Logged at **error**. No retry helps; the key or the domain is wrong. |
| Rate limited, or Resend `5xx` | Logged at warning, reported as transient, safe to retry. |
| Provider slow | Abandoned after `Email:TimeoutSeconds` (10). Someone is waiting on a signup response; an unsent email beats a hung request. |

Nothing about a failure is shown to the person signing up beyond that the message could not
be sent — provider responses stay in the server log.

### What is in the logs

Recipients are masked to `am***@example.com`: enough to match a support request to a send,
not enough to build a mailing list from a log file. Anything key-shaped is stripped before
writing. Message bodies are never logged in production — in development, where no provider
is configured, the whole message *is* written to the log so you can click the verification
link without an account, which is exactly why that sender is unreachable outside Development.

### Proving it actually works

Everything else about email is tested against a stub, which proves the code is right and
proves nothing about the account. Whether the key has the right scope and whether the sender
domain is verified cannot be discovered locally, and getting either wrong presents the same
way: signup appears to work and nobody receives anything.

There is an opt-in test that sends a real message through the real API, to Resend's own sink
address so nothing lands in front of a person:

```bash
ZAZI_EMAIL_LIVE_TEST=1 RESEND_API_KEY='re_...' \
  scripts/dotnet.sh test tests/Zazi.IntegrationTests/Zazi.IntegrationTests.csproj \
  --filter FullyQualifiedName~ResendLiveSmokeTest
```

It needs **both** variables. `RESEND_API_KEY` alone is not enough, because that variable is
legitimately set on a machine configured for real use and a normal test run must not start
sending mail. A pass means the credential, its scope and the sender domain are all good.

Run it after issuing or rotating a key. A `403` means the domain is not verified; a `401`
means the key is wrong or lacks Sending access.

### Rotating the key

Create the new key in Resend first, swap the value in `/etc/zazi/web.env`, restart
`zazi-web`, confirm a send, then revoke the old one. Resend allows several live keys, so
there is no window where the dashboard has none.

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
