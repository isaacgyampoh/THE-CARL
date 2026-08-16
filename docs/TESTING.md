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
| `TheCarl.UnitTests` | EF InMemory | Domain policy, auth, revocation, fingerprints |
| `TheCarl.IntegrationTests` (HTTP) | EF InMemory | Authorization and tenancy through the real pipeline |
| `TheCarl.IntegrationTests/Postgres` | **PostgreSQL** | Idempotency, concurrency, constraints, atomicity |

The InMemory provider enforces neither unique indexes nor check constraints and has no
transactions. Anything that depends on the database actually saying "no" must run on
PostgreSQL — which is why that layer exists separately.

## Supplying PostgreSQL

The fixture tries two sources, in order.

### 1. An existing server — `THECARL_TEST_POSTGRES`

```bash
export THECARL_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=carl;Password=…"
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
WORK="$HOME/.thecarl-testdb" && mkdir -p "$WORK" && cd "$WORK"

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
mkdir -p /tmp/carlpg
#    max_connections must be raised: the suite deliberately drives 100-way concurrency
#    across several test classes, and the default of 100 exhausts the server
#    ("sorry, too many clients already").
./pgdist/bin/pg_ctl -D "$WORK/pgdata" \
  -o "-p 55432 -c listen_addresses=127.0.0.1 -c unix_socket_directories=/tmp/carlpg -c max_connections=400" \
  -l "$WORK/pg.log" start

# 4. Point the tests at it
export THECARL_TEST_POSTGRES="Host=127.0.0.1;Port=55432;Database=postgres;Username=carl;Password=carl-test-password"
dotnet test

# 5. Stop when finished
./pgdist/bin/pg_ctl -D "$WORK/pgdata" stop
```

Notes:

- The distribution ships `initdb`, `pg_ctl` and `postgres` only — no `psql`. The tests
  connect through Npgsql, so no client binary is needed.
- The x86_64 build runs under Rosetta on Apple silicon. That is fine for testing.
- Port 55432 avoids clashing with any real local PostgreSQL on 5432.
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
