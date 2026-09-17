# Testing

## Running the suite

```bash
export PATH="$HOME/.dotnet:$PATH"   # 8.0.424 — see global.json
dotnet restore && dotnet build && dotnet test
```

Without a PostgreSQL server the 74 PostgreSQL-backed tests **skip with a reason**. They do
not pass. A green run that reports skips has not verified idempotency, concurrency,
constraints, atomicity, reversal safety, or failure recovery — read the skip count, not just
the colour.

## Test layers

| Project | Provider | Proves |
|---|---|---|
| `Zazi.UnitTests` | EF InMemory | Domain policy, auth, revocation, fingerprints |
| `Zazi.IntegrationTests` (HTTP) | EF InMemory | Authorization and tenancy through the real pipeline |
| `Zazi.IntegrationTests/Postgres` | **PostgreSQL** | Idempotency, concurrency, constraints, atomicity |

The InMemory provider enforces neither unique indexes nor check constraints and has no
transactions. Anything that depends on the database actually saying "no" must run on
PostgreSQL — which is why that layer exists separately.

## Supplying PostgreSQL

The fixture tries two sources, in order.

### 1. An existing server — `ZAZI_TEST_POSTGRES`

```bash
export ZAZI_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=carl;Password=…"
dotnet test
```

The fixture creates a uniquely-named database on that server, migrates it, and drops it
afterwards. The account needs `CREATEDB`.

### 2. Testcontainers

Used automatically when a Docker-compatible socket is present. Nothing to configure. This is
the CI path.

---

## Setup options

### Docker Desktop — recommended for most developers

```bash
# Apple silicon
curl -fsSLO https://desktop.docker.com/mac/main/arm64/Docker.dmg
sudo hdiutil attach Docker.dmg
sudo /Volumes/Docker/Docker.app/Contents/MacOS/install --accept-license
sudo hdiutil detach /Volumes/Docker
open -a Docker           # first launch initialises the VM

docker info               # confirm the daemon is up
dotnet test               # Testcontainers takes over from here
```

Requires administrator rights.

### Colima — lighter, no GUI, Homebrew

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
brew install colima docker
colima start --cpu 2 --memory 4

docker info && dotnet test
```

### Standalone PostgreSQL binaries — no root, no container runtime

This is the route used to verify the suite on a machine with no Docker, no Homebrew and no
administrator access. The binaries come from Maven Central (the distribution the
`zonky.io` embedded-postgres project publishes) and run entirely in userspace.

> **Do not put the cluster under `/tmp`.** macOS periodically cleans it, and a mid-session
> wipe takes the binaries, the data directory and the log with it — the suite then silently
> reverts to skipping every PostgreSQL test. Use a durable path.

```bash
WORK="$HOME/.zazi-testdb" && mkdir -p "$WORK" && cd "$WORK"

# 1. Fetch and unpack (arm64; use ...-darwin-amd64 on Intel)
curl -fsSLO https://repo1.maven.org/maven2/io/zonky/test/postgres/embedded-postgres-binaries-darwin-arm64v8/16.2.0/embedded-postgres-binaries-darwin-arm64v8-16.2.0.jar
unzip -o -q embedded-postgres-binaries-darwin-arm64v8-16.2.0.jar postgres-darwin-arm_64.txz
mkdir -p pgdist && tar -xJf postgres-darwin-arm_64.txz -C pgdist

# 2. Initialise a cluster
echo "carl-test-password" > pgpass.txt
./pgdist/bin/initdb -D "$WORK/pgdata" -U carl --pwfile=pgpass.txt -A md5 -E UTF8

# 3. Start it.
#    The socket directory must be SHORT: PostgreSQL caps the Unix socket path at 103 bytes
#    and a deep temp path silently fails to start.
mkdir -p /tmp/zazipg
#    max_connections must be raised: the suite deliberately drives 100-way concurrency
#    across several test classes, and the default of 100 exhausts the server
#    ("sorry, too many clients already").
./pgdist/bin/pg_ctl -D "$WORK/pgdata" \
  -o "-p 55433 -c listen_addresses=127.0.0.1 -c unix_socket_directories=/tmp/zazipg -c max_connections=400" \
  -l "$WORK/pg.log" start

# 4. Point the tests at it
export ZAZI_TEST_POSTGRES="Host=127.0.0.1;Port=55433;Database=postgres;Username=carl;Password=carl-test-password"
dotnet test

# 5. Stop when finished
./pgdist/bin/pg_ctl -D "$WORK/pgdata" stop
```

Notes:

- The distribution ships `initdb`, `pg_ctl` and `postgres` only — no `psql`. The tests
  connect through Npgsql, so no client binary is needed.
- The x86_64 build runs under Rosetta on Apple silicon. That is fine for testing.
- Port 55433 avoids clashing with any real local PostgreSQL on 5432. It is **not**
  guaranteed free: another project on the same machine may already hold it, and a foreign
  cluster accepts the TCP connection and then rejects the `carl` role, which reads like a
  broken client rather than the wrong server. Check first, and pick another port if taken:

  ```bash
  lsof -nP -iTCP:55433 -sTCP:LISTEN     # empty means free
  ```

  Every port below is overridable — `pg_ctl -o "-p <port>"` and the matching
  `Port=<port>` in `ZAZI_TEST_POSTGRES`.
- `max_connections=400` is a test-harness requirement, not a product one. Each test class
  holds its own API factory with a bounded pool (15), and the concurrency tests open many at
  once.

---

## What the PostgreSQL suite verifies

### `IdempotencyConcurrencyTests`

| Test | Guarantee |
|---|---|
| `TwoSimultaneousSubmissions…` | 2 concurrent submissions → 1 transaction |
| `TenSimultaneousSubmissions…` | 10 concurrent → 1 transaction |
| `OneHundredSimultaneousSubmissions…` | 100 concurrent → 1 transaction |
| `SequentialRetryAfterALostResponse…` | Retry after a lost response is an idempotent replay |
| `DistinctClientIdsCreateDistinct…` | No over-eager deduplication losing real transactions |
| `TheSameClientIdInTwoTenants…` | Idempotency is organization-scoped |
| `ConcurrentSubmissionsLeaveTheBalance…` | Balance projection stays exact under contention |
| `ANegativeAmountIsRejected…` | `CK_Transactions_AmountNonNegative` |
| `AnUnknownTypedTransactionCannotCarry…` | `CK_Transactions_UnknownHasNoLedgerEffect` |
| `AReversalWithoutAnOriginalIsRejected…` | `CK_Transactions_ReversalHasOriginal` |

Concurrent attempts are released through a `Barrier` so they genuinely contend rather than
trickling in as each task starts.

### `PostgresTenantIsolationTests`

Tenant A cannot see, create, synchronise, modify, or aggregate over tenant B's transactions,
devices, sessions or dashboards. Plus: identical evidence in two tenants produces two
transactions, while the same evidence twice in one tenant produces one — with both
observations recorded, because a duplicate arriving is itself auditable.

---

## Two defects this layer caught

Both were invisible to the InMemory provider and would have reached production.

**A migration that could not run.** `RenameIndex` on an index that a preceding `DropColumn`
had already removed — `relation "IX_Transactions_SourceDeviceId" does not exist`. The
migration would have failed on first deployment.

**A dashboard that would crash.** `x.TransactionAtUtc.Date == todayUtc` cannot be translated
by Npgsql against a `timestamptz` column; it throws at runtime. Replaced with a half-open
range over the Africa/Accra business day, which is also index-friendly — a function over the
column would have prevented an index seek even if it had worked.


---

## Failure-injection layer

`FailureInjectionTests` and `ReversalSafetyTests` prove financial correctness when things
break. Failures are injected against the real database rather than mocked — a mocked
`SaveChanges` throwing proves the C# handles an exception, not that PostgreSQL rolled back.

| Mechanism | Used for |
|---|---|
| `ROLLBACK` on a real transaction | Nothing partially commits at any boundary |
| `pg_terminate_backend(pid)` | The server dies mid-transaction |
| Terminating every backend | Failover or restart mid-batch |
| Check-constraint violation | The ledger step fails after the row is written |
| 1 ms `HttpClient` timeout | Client abandons a request the server is still processing |
| `CancellationToken` mid-flight | Connection closes part-way through a batch |
| Direct `DbContext` insert | Constraints hold even if application logic is bypassed |

`FreshDeploymentTests` creates a brand-new database each run and applies the whole migration
chain, so a migration that cannot actually run is caught before deployment rather than after.

## Recorded throughput

1,000 transactions across 10 batches of 100, same machine:

| Run | Time | Throughput |
|---|---|---|
| Phase 2 baseline | 3,124 ms | 320 tx/s |
| Phase 3 (4 runs) | 3,178 / 3,793 / 3,875 / 4,363 ms | 315 / 264 / 258 / 229 tx/s |

Run-to-run variance is roughly ±25%, so the Phase 2 figure sits inside the Phase 3 band and
no regression can be claimed from these numbers. The trend is slightly slower, which is
consistent with the extra unique index added for the reversal invariant costing a little
index maintenance per insert — a deliberate trade for a database-enforced financial
guarantee. Treat this as a regression tripwire, not a benchmark: investigate if a run drops
below ~150 tx/s.

## Android SMS capture

Automatic capture is exercised end to end by injecting real messages into the emulator, so
the manifest registration, the broadcast, PDU reassembly and the capture pipeline are all
covered rather than assumed.

```bash
adb shell pm grant app.zazi android.permission.RECEIVE_SMS

# Establish a session and record a baseline
adb shell am instrument -w \
  -e class 'app.zazi.SmsReceiverInstrumentedTest#signsInAndRecordsTheBaseline' \
  -e carlEmail '<agent>' -e carlPassword '<password>' \
  app.zazi.test/androidx.test.runner.AndroidJUnitRunner

adb shell am kill app.zazi          # see the warning below

REF="MP240817.$(date +%H%M%S).X$RANDOM"
MSG="Cash In of GHS 750.00 from 0241000099 TEST SYNTHETIC. Ref: $REF"
adb emu sms send MTN "$MSG"; sleep 14
adb emu sms send MTN "$MSG"; sleep 14   # the same message twice, on purpose

adb shell am instrument -w \
  -e class 'app.zazi.SmsReceiverInstrumentedTest#theInjectedMessageBecameExactlyOneTransaction' \
  -e carlEmail '<agent>' -e carlPassword '<password>' \
  app.zazi.test/androidx.test.runner.AndroidJUnitRunner
```

Three things will waste an afternoon if they are not known in advance:

- **Use `am kill`, never `am force-stop`.** `force-stop` puts the package into Android's
  stopped state, and the system does not deliver broadcasts to a stopped package until the
  user launches it again. The SMS arrives, appears in the inbox, and the receiver never runs
  — which looks exactly like a broken receiver. `am kill` ends the process without setting
  that flag, which is the real cold-start case anyway.
- **Use a fresh reference each run.** The evidence fingerprint is doing its job: a message
  replayed from an earlier run is recognised as a duplicate and creates nothing, so the test
  sees no new transaction and appears to fail.
- **The item may already be synced.** A successful capture wakes the sync worker, so asserting
  the outbox is still `PENDING` is a race. Assert `PENDING + SYNCED` instead.
- **`OK (1 test)` does not always mean the test ran.** These tests use `assumeTrue`, and JUnit
  reports an unmet assumption as a pass. A device that needs re-enrolment skips the baseline
  step and reports success while writing nothing, after which the second half fails on a
  missing file. Pass `-e carlEnrolmentCode` whenever the emulator has been recreated, and
  confirm the baseline exists before trusting a green first half:

  ```bash
  adb shell run-as app.zazi cat files/sms-receiver-baseline.txt
  ```

`adb emu sms send` delivers a single-part message. Multipart joining is covered separately by
`SmsReassemblyTest`, which tests the ordering rule directly; PDU decoding itself is the
platform's responsibility.

Before any end-to-end run, confirm the environment is what you think it is:

```bash
scripts/verify-e2e-env.sh
```

It checks that the API is backed by PostgreSQL rather than the in-memory store, and that the
cluster answering the port belongs to this project. Both have previously produced convincing
false diagnoses.

## Web dashboard

`Zazi.Web` is a Blazor Server application that reads the same PostgreSQL database through the
same application services as the API. It is a second caller of the existing domain, not a
second system: `IAuthService` verifies credentials, `IDashboardService` produces the figures,
and `ClaimsTenantIdentity` decides which organization the caller belongs to — the same code
the bearer-token path uses.

```bash
cd src/Zazi.Web
export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=55433;Database=zazi;Username=…;Password=…"
export Jwt__Key="<the same key the API uses>"
dotnet run --no-launch-profile          # http://127.0.0.1:5080
```

`Jwt__Key` is required because credential verification runs through `IAuthService`, which
issues tokens as part of a successful login even though the browser session is carried by a
cookie rather than by those tokens.

The application refuses to start without a connection string rather than falling back to an
in-memory store. A dashboard that quietly reports an empty branch is worse than one that will
not start.

Two properties worth re-checking after any change to routing or authentication:

```bash
# Deny by default: an anonymous request for any page must redirect to sign-in
curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' http://127.0.0.1:5080/

# A signed-in session reaches its own organization's figures
curl -s -c jar.txt -X POST http://127.0.0.1:5080/auth/sign-in \
  --data-urlencode 'email=…' --data-urlencode 'password=…'
curl -s -b jar.txt http://127.0.0.1:5080/ | grep -o '<span class="value">[^<]*</span>'
```

The sign-in and sign-out endpoints live under `/auth/` rather than on the page routes. A Razor
page and a minimal endpoint sharing a path match ambiguously and fail the request at runtime,
because component endpoints are not distinguished by HTTP method.
