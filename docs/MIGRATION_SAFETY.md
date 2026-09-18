# Migration Safety

Rules for schema change in a system that holds financial records.

## The rule

> A migration must never destroy financial data to make a schema change convenient.

Financial tables — `Transactions`, `TransactionEvidence`, `Reconciliations`, `AuditLogs`,
`CashBalances`, `FloatBalances` — are append-only in intent. A correction is a new row that
references the original, never an edit or a delete.

---

## Development-stage exceptions

Two migrations so far are knowingly destructive. Both are acceptable **only** because no
production deployment exists yet. Neither would be acceptable afterwards.

### `Phase10AuthorizationHardening` — deletes all refresh tokens

```sql
DELETE FROM "RefreshTokens";
```

Refresh tokens moved from clear text to SHA-256 hashes. The original secrets cannot be
recovered to backfill, and leaving the rows would give every one `TokenHash = ''`, breaking
the new unique index.

- **Data lost:** active sessions only. No financial data.
- **User impact:** every client is signed out once and must log in again.
- **Production alternative:** add `TokenHash` as nullable, hash on next use, expire the
  clear-text column after the refresh window, then drop it. A three-step migration across
  three releases.

### `Phase12FinancialIntegrityAndOfflineContract` — drops `CapturedSmsMessages`

The table is replaced by `TransactionEvidence`, which is source-agnostic and carries the
canonical fingerprint. The migration drops it rather than copying rows across.

- **Data lost:** captured SMS evidence rows.
- **Production alternative:** create `TransactionEvidence`, copy with a computed fingerprint
  per row, verify counts, then drop in a later release.

---

## What the Phase 12 migration does correctly

Three defects in the EF-generated migration were corrected by hand. They are worth recording
because the same traps recur.

**1. A semantically wrong rename.** EF inferred `SourceDeviceId → SessionId` because both
are `uuid`. They are unrelated; reusing the column would have silently populated `SessionId`
with device identifiers. Replaced with an explicit drop and add.

> EF matches columns by type and position, not meaning. Every generated `RenameColumn` on a
> financial table must be read and confirmed.

**2. An enum whose ordinals moved.** `Status → State` reuses the column, but
`TransactionStatus` and `TransactionLifecycleState` do not line up — old `Captured(1)` would
read as `Parsed(1)`, demoting an accepted ledger row back to evidence. An explicit `UPDATE …
CASE` remaps values, ordered highest-first so no row is rewritten twice.

> `TransactionType` ordinals are part of the persisted contract. `CashIn = 0` and
> `CashOut = 1` deliberately retain the ordinals of the former `Deposit` and `Withdrawal`,
> which is why that enum needed no data migration. Never reorder or renumber; append only.

**3. An invalid system-column write.** EF emitted `AddColumn("xmin")`. `xmin` is a PostgreSQL
system column and cannot be added; the migration would have failed on first run. The
optimistic-concurrency token was removed instead — financial transactions are append-only, so
concurrent *updates* are not the risk. Concurrent *creation* is, and that is covered by the
unique idempotency indexes.

**Backfill.** Pre-existing rows have no stored `CashDelta`/`FloatDelta`. The migration
backfills them from the authoritative direction so a rebuilt projection matches what was
originally posted. Types without an automatic rule are left at zero for review rather than
guessed.

---

## Pre-production checklist

Before any migration reaches an environment holding real records:

1. **Read every generated operation.** Treat `DropColumn`, `DropTable` and `RenameColumn` on
   a financial table as defects until proven otherwise.
2. **Generate and review the SQL** — `dotnet ef migrations script --idempotent`. Never apply
   a migration that has not been read as SQL.
3. **Verify the `Down` path** actually reverses `Up`, or document that it cannot.
4. **Enum ordinal check.** Confirm no persisted enum changed meaning.
5. **Back up first**, and rehearse the restore — an untested backup is not a backup.
6. **Rehearse on a production-shaped copy**, not an empty database. Constraint violations
   only appear where the offending data exists.
7. **Row counts before and after** for every table touched.
8. **Additive first.** Expand → migrate → contract, across separate releases, so a rollback
   never needs the dropped column back.
9. **Long locks.** `ALTER TABLE` on a large table takes an `ACCESS EXCLUSIVE` lock. A plain
   `CREATE INDEX` is not much better: it blocks `INSERT` for the duration, and the writers on
   these tables are agents' handsets syncing captured transactions.

   Build indexes concurrently, and do it **inside** the migration rather than as a manual step
   beside it, so the change stays versioned and repeatable. EF allows this with
   `suppressTransaction`, which `CREATE INDEX CONCURRENTLY` requires because it cannot run in
   a transaction block. `TransactionOrganizationDateIndex` is the worked example:

   ```csharp
   migrationBuilder.Sql(
       """
       CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_..." ON "Transactions" (...);
       """,
       suppressTransaction: true);
   ```

   A concurrent build that fails leaves the index behind marked `INVALID`, where no query uses
   it — which looks exactly like success. Check after any index migration:

   ```sql
   SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
   ```
10. **Never `EnsureCreated`.** It bypasses migrations and leaves the database unmigratable.
    Program.cs calls `MigrateAsync` for relational providers for exactly this reason.

## Applying migrations

```bash
export PATH="$HOME/.dotnet:$PATH"
export ConnectionStrings__DefaultConnection="Host=…;Database=zazi;Username=…;Password=…"

# Review as SQL first — always.
dotnet ef migrations script --idempotent \
  --project src/Zazi.Infrastructure -o migration.sql

dotnet ef database update --project src/Zazi.Infrastructure
```

`DesignTimeDbContextFactory` supplies the Npgsql provider to the CLI, so scaffolding does not
need a live database and does not accidentally target the in-memory provider.

## Migrating on startup is a development convenience

The API applies migrations at startup only when `Database:MigrateOnStartup` is true, which it
defaults to in Development and nowhere else.

Automatic migration is convenient on one machine and hazardous on several. A rollout starts
every replica at once, so they race through the same migration; and a deployment that only
meant to ship code silently alters the schema, with no separate step anyone can review, gate
or roll back. Production therefore runs migrations as a deliberate deployment step:

```bash
dotnet ef database update --project src/Zazi.Infrastructure --startup-project src/Zazi.Api
```

If the schema is behind, the API refuses to start and names the first missing migration.
That is deliberate. Serving traffic against a schema the code does not match produces
scattered column-not-found errors at random moments, which is far harder to diagnose than one
clear refusal at boot.

## Probes

`/health` is liveness: the process is running. It touches nothing, so a database blip cannot
cause an orchestrator to kill an otherwise healthy instance.

`/ready` is readiness: this instance can actually serve, which it proves by reaching the
database. Point the load balancer at this one. A health endpoint that returns 200 while the
database is unreachable keeps traffic flowing to an instance that can answer nothing — and
during development, an API running against an in-memory store looked perfectly healthy while
every login failed.

Neither probe reports a version or any backing-store detail: both are unauthenticated
surfaces, and the failure reason is logged rather than returned.
