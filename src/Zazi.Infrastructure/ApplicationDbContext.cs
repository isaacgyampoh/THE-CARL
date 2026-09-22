using Microsoft.EntityFrameworkCore;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Infrastructure;

public class ApplicationDbContext : DbContext
{
    private readonly ICurrentUserContext? _currentUser;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    /// <summary>
    /// Used by the hosts, which can supply the caller's identity.
    /// </summary>
    /// <remarks>
    /// Optional so migrations, design-time tooling and tests can construct a context with no
    /// request in scope. When it is absent, audit entries are simply written without a
    /// correlation id rather than failing.
    /// </remarks>
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ICurrentUserContext currentUser) : base(options)
    {
        _currentUser = currentUser;
    }

    /// <summary>
    /// Stamps audit entries with the request they belong to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nine services write audit entries, and none of them knew about correlation ids. Doing
    /// this here rather than in each of them means a tenth writer is correlated the day it is
    /// added, instead of being the one gap nobody notices until an incident cannot be
    /// reconstructed.
    /// </para>
    /// <para>
    /// Only fills what a caller left blank, so a service that deliberately sets its own
    /// source or correlation keeps it.
    /// </para>
    /// </remarks>
    private void StampAuditEntries()
    {
        // Deliberately not gated on authentication. A correlation id is not identity, and
        // the entries most worth correlating — a failed sign-in, an enrolment that was
        // refused — are written while the caller is still anonymous.
        if (_currentUser is null)
        {
            return;
        }

        string? correlationId = null;

        foreach (var entry in ChangeTracker.Entries<AuditLogEntry>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            // An entry that arrived from a client describes a different moment than the
            // request carrying it. Stamping the upload's correlation onto it would file a
            // login under the trace of the telemetry batch that happened to deliver it, and
            // the resulting timeline would be fiction.
            if (entry.Entity.Source is not null)
            {
                continue;
            }

            // Resolved once, and only when there is something to stamp.
            correlationId ??= _currentUser.CorrelationId;

            if (string.IsNullOrWhiteSpace(entry.Entity.CorrelationId)
                && !string.IsNullOrWhiteSpace(correlationId))
            {
                entry.Entity.CorrelationId = correlationId;
            }

            entry.Entity.Source = entry.Entity.ActorType;
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditEntries();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAuditEntries();
        return base.SaveChanges();
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<FinancialTransaction> Transactions => Set<FinancialTransaction>();
    public DbSet<TransactionEvidence> TransactionEvidence => Set<TransactionEvidence>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<MfaCredential> MfaCredentials => Set<MfaCredential>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<DeviceLink> DeviceLinks => Set<DeviceLink>();
    public DbSet<SyncAttempt> SyncAttempts => Set<SyncAttempt>();
    public DbSet<DeadLetterTransaction> DeadLetterTransactions => Set<DeadLetterTransaction>();
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();
    public DbSet<ReconciliationRecord> Reconciliations => Set<ReconciliationRecord>();
    public DbSet<CashBalance> CashBalances => Set<CashBalance>();
    public DbSet<FloatBalance> FloatBalances => Set<FloatBalance>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();
    public DbSet<AlertThreshold> AlertThresholds => Set<AlertThreshold>();
    public DbSet<SyncQueueEntry> SyncQueue => Set<SyncQueueEntry>();
    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();
    public DbSet<DeviceEnrollmentCode> DeviceEnrollmentCodes => Set<DeviceEnrollmentCode>();
    public DbSet<ParsingReport> ParsingReports => Set<ParsingReport>();
    public DbSet<DayClose> DayCloses => Set<DayClose>();
    public DbSet<FloatRequest> FloatRequests => Set<FloatRequest>();
    public DbSet<BusinessExpense> Expenses => Set<BusinessExpense>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Organization>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
            builder.Property(x => x.Email).HasMaxLength(255);
            builder.Property(x => x.PhoneNumber).HasMaxLength(30);
            builder.Property(x => x.Country).IsRequired().HasMaxLength(10);
            builder.Property(x => x.CurrencyCode).IsRequired().HasMaxLength(10);
            builder.HasIndex(x => x.Name);
            builder.HasMany(x => x.Branches).WithOne().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.Users).WithOne().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Branch>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
            builder.Property(x => x.Location).HasMaxLength(255);
            builder.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            builder.HasMany(x => x.Devices).WithOne().HasForeignKey("BranchId").OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.Sessions).WithOne().HasForeignKey("BranchId").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.FullName).IsRequired().HasMaxLength(200);
            // Nullable: a worker activated by code has no account of their own, and inventing
            // a synthetic address to satisfy a NOT NULL would put fake data into a field the
            // owner portal shows. The unique index below still protects real addresses —
            // PostgreSQL treats nulls as distinct, so credential-less workers do not collide.
            builder.Property(x => x.Email).HasMaxLength(255);
            builder.Property(x => x.CredentialType).HasConversion<int>();
            builder.Property(x => x.PhoneNumber).HasMaxLength(30);
            builder.Property(x => x.PasswordHash).HasMaxLength(512);
            builder.Property(x => x.PasswordSalt).HasMaxLength(128);
            builder.Property(x => x.SecurityStamp).HasMaxLength(128);
            builder.HasIndex(x => new { x.OrganizationId, x.Email }).IsUnique();

            builder.Property(x => x.EmailVerificationTokenHash).HasMaxLength(128);

            // Verification looks a user up by this hash and nothing else, so it needs an index
            // or every click on a verification link is a full table scan. Filtered, because the
            // column is null for every account that is already verified — which, before long,
            // is nearly all of them.
            builder
                .HasIndex(x => x.EmailVerificationTokenHash)
                .HasFilter("\"EmailVerificationTokenHash\" IS NOT NULL");

            builder.Property(x => x.PasswordResetTokenHash).HasMaxLength(128);

            // Same shape and the same reason as the index above: reset looks a user up by this
            // hash alone, and the column is null for every account not currently resetting —
            // which is nearly all of them, nearly all the time.
            builder
                .HasIndex(x => x.PasswordResetTokenHash)
                .HasFilter("\"PasswordResetTokenHash\" IS NOT NULL");

            // Roles are organization-scoped entities shared by many users, so this must be a
            // many-to-many join. The previous mapping used OrganizationId as a foreign key back
            // to User.Id, which meant assigning an existing role to a second user rewrote that
            // role's owner and silently removed it from the first user.
            builder
                .HasMany(x => x.Roles)
                .WithMany()
                .UsingEntity<Dictionary<string, object>>(
                    "UserRoles",
                    right => right.HasOne<Role>().WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade),
                    left => left.HasOne<User>().WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade),
                    join =>
                    {
                        join.HasKey("UserId", "RoleId");
                        join.HasIndex("RoleId");
                    });
        });

        modelBuilder.Entity<Role>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
            builder.Property(x => x.Description).HasMaxLength(500);
            builder.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            builder.HasMany(x => x.Permissions).WithOne().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Permission>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).IsRequired().HasMaxLength(120);
            builder.Property(x => x.Description).IsRequired().HasMaxLength(500);
            builder.HasIndex(x => new { x.RoleId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
            builder.Property(x => x.FamilyId).IsRequired().HasMaxLength(100);
            builder.Property(x => x.RevokedReason).HasMaxLength(200);
            builder.Property(x => x.ReplacedByTokenHash).HasMaxLength(128);
            builder.HasIndex(x => x.UserId);
            builder.HasIndex(x => x.TokenHash).IsUnique();
            builder.HasIndex(x => new { x.UserId, x.FamilyId });
            builder.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<Device>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
            builder.Property(x => x.DeviceIdentifier).IsRequired().HasMaxLength(200);
            builder.Property(x => x.Platform).IsRequired().HasMaxLength(50);
            builder.Property(x => x.PreferredLanguage).HasMaxLength(8);
            builder.Property(x => x.Network).IsRequired().HasMaxLength(50);
            builder.Property(x => x.AppVersion).HasMaxLength(50);
            builder.Property(x => x.OsVersion).HasMaxLength(80);
            builder.HasIndex(x => new { x.OrganizationId, x.DeviceIdentifier }).IsUnique();
            builder.HasIndex(x => x.BranchId);
            builder.HasIndex(x => new { x.OrganizationId, x.DeviceType });

            builder.ToTable(table =>
            {
                // Capabilities are granted from DeviceType, so an out-of-range ordinal would
                // silently produce an unknown platform with undefined capability behaviour.
                // The C# enum cannot stop a bad value arriving from a raw SQL path or a
                // future migration, so the database enforces the range too.
                table.HasCheckConstraint(
                    "CK_Devices_DeviceTypeInRange",
                    "\"DeviceType\" >= 0 AND \"DeviceType\" <= 6");
            });
        });

        modelBuilder.Entity<DeviceRegistration>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.DeviceIdentifier).IsRequired().HasMaxLength(200);
            builder.Property(x => x.Platform).IsRequired().HasMaxLength(50);
            builder.Property(x => x.RegistrationToken).IsRequired().HasMaxLength(500);
            builder.HasIndex(x => new { x.OrganizationId, x.DeviceIdentifier }).IsUnique();
        });

        modelBuilder.Entity<DeviceLink>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.LinkType).IsRequired().HasMaxLength(80);
            builder.Property(x => x.Status).IsRequired().HasMaxLength(40);
            builder.HasIndex(x => new { x.OrganizationId, x.SourceDeviceId, x.LinkedDeviceId }).IsUnique();
        });

        modelBuilder.Entity<Session>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.OrganizationId).IsRequired();
            builder.Property(x => x.BranchId).IsRequired();
            builder.Property(x => x.UserId).IsRequired();
            builder.HasIndex(x => x.UserId);
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.OpenedAt });

            // Opening balances were unconstrained numeric while the CashBalance and
            // FloatBalance rows written from the very same request value were numeric(18,4).
            // A client sending more than four decimal places made a session and its own
            // balances disagree about the same figure from the moment they were created.
            builder.Property(x => x.OpeningCash)
                .HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.OpeningFloat)
                .HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
        });

        modelBuilder.Entity<FinancialTransaction>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Network).IsRequired().HasMaxLength(80);
            builder.Property(x => x.ProviderReference).HasMaxLength(200);
            builder.Property(x => x.CustomerPhoneNumber).HasMaxLength(30);
            builder.Property(x => x.Notes).HasMaxLength(1000);

            // "What did this number do?" — asked at the counter when a customer disputes a
            // transaction, and answered by an exact match on the normalised number.
            builder.HasIndex(x => new { x.OrganizationId, x.CustomerPhoneNumber });
            builder.Property(x => x.CorrectionReason).HasMaxLength(1000);
            builder.Property(x => x.ClientTransactionId).HasMaxLength(64);
            builder.Property(x => x.EvidenceFingerprint).HasMaxLength(64);
            builder.Property(x => x.Currency).IsRequired().HasMaxLength(10);
            builder.Property(x => x.ConfidenceScore).HasPrecision(5, 4);

            // Money is numeric(18,4) everywhere. Never float or double.
            builder.Property(x => x.Amount).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.CashDelta).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.FloatDelta).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);

            // No optimistic-concurrency token by design. Financial transactions are
            // append-only: a mistake is corrected by a new Reversal or Adjustment row that
            // references the original, never by updating it in place. Concurrent *creation*
            // is guarded by the two unique idempotency indexes below, which is where the
            // real race lives. (EF also emits an invalid AddColumn for the xmin system
            // column, so mapping it would break the migration.)

            builder.HasIndex(x => x.OrganizationId);
            builder.HasIndex(x => x.BranchId);
            builder.HasIndex(x => x.AgentId);
            builder.HasIndex(x => x.DeviceId);
            builder.HasIndex(x => x.SessionId);
            builder.HasIndex(x => x.EvidenceId);
            builder.HasIndex(x => x.TransactionAtUtc);

            // Both hot read paths filter by organization *and* a date range: the dashboard's
            // "today" aggregate, and the paged transaction list, which then sorts by the same
            // column. With only single-column indexes PostgreSQL has to scan a day across
            // every tenant and discard the rest, and sort the survivors. Leading with
            // OrganizationId keeps a tenant's rows contiguous, and the trailing date supplies
            // the range and the ordering without a sort.
            builder.HasIndex(x => new { x.OrganizationId, x.TransactionAtUtc });

            builder.HasIndex(x => x.ReversesTransactionId);
            builder.HasIndex(x => x.AdjustsTransactionId);

            // ─── Idempotency boundaries ──────────────────────────────────────
            // Rationale is documented in docs/OFFLINE_TRANSACTION_CONTRACT.md.
            //
            // 1. Replay: one row per client-generated id per organization. Guarantees that
            //    retrying a submission — after a lost response, a reboot, or a week offline
            //    — cannot create a second ledger entry.
            builder.HasIndex(x => new { x.OrganizationId, x.ClientTransactionId })
                .IsUnique()
                .HasDatabaseName("UX_Transactions_Organization_ClientTransactionId")
                .HasFilter("\"ClientTransactionId\" IS NOT NULL");

            // 2. Observation: one row per real-world provider event per organization. Two
            //    devices that both witness the same SMS describe one financial event, so the
            //    boundary is the organization rather than the device.
            builder.HasIndex(x => new { x.OrganizationId, x.EvidenceFingerprint })
                .IsUnique()
                .HasDatabaseName("UX_Transactions_Organization_EvidenceFingerprint")
                .HasFilter("\"EvidenceFingerprint\" IS NOT NULL");

            // One original, at most one effective reversal. Reversing twice would create
            // money that never existed. An application pre-check cannot guarantee this:
            // two concurrent reversals can both read "not yet reversed" before either
            // commits, so the invariant has to live in the database.
            //
            // Partial, so the overwhelming majority of rows (which reverse nothing) do not
            // collide on NULL. Multiple partial reversals are NOT supported: if the business
            // ever needs them, this constraint is the deliberate gate that must be revisited.
            builder.HasIndex(x => new { x.OrganizationId, x.ReversesTransactionId })
                .IsUnique()
                .HasDatabaseName("UX_Transactions_Organization_ReversesTransactionId")
                .HasFilter("\"ReversesTransactionId\" IS NOT NULL");

            builder.HasIndex(x => new { x.OrganizationId, x.ProviderReference });
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.TransactionAtUtc });

            builder.ToTable(table =>
            {
                // Amount is a magnitude; direction lives in Type. A negative amount would
                // silently invert a ledger movement.
                table.HasCheckConstraint(
                    "CK_Transactions_AmountNonNegative",
                    "\"Amount\" >= 0");

                table.HasCheckConstraint(
                    "CK_Transactions_ConfidenceRange",
                    "\"ConfidenceScore\" >= 0 AND \"ConfidenceScore\" <= 1");

                // A reversal without a referenced original has no determinable direction.
                table.HasCheckConstraint(
                    "CK_Transactions_ReversalHasOriginal",
                    "\"Type\" <> 3 OR \"ReversesTransactionId\" IS NOT NULL");

                // Unknown-typed rows must never carry a balance effect.
                table.HasCheckConstraint(
                    "CK_Transactions_UnknownHasNoLedgerEffect",
                    "\"Type\" <> 6 OR (\"CashDelta\" = 0 AND \"FloatDelta\" = 0)");
            });
        });

        modelBuilder.Entity<TransactionEvidence>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Provider).IsRequired().HasMaxLength(80);
            builder.Property(x => x.SenderAddress).HasMaxLength(50);
            builder.Property(x => x.CustomerPhoneNumber).HasMaxLength(30);
            builder.Property(x => x.ProviderReference).HasMaxLength(200);
            builder.Property(x => x.Currency).IsRequired().HasMaxLength(10);
            builder.Property(x => x.Fingerprint).IsRequired().HasMaxLength(64);
            builder.Property(x => x.FingerprintVersion).IsRequired().HasMaxLength(10);
            builder.Property(x => x.RawHash).HasMaxLength(64);
            builder.Property(x => x.ParserName).HasMaxLength(80);
            builder.Property(x => x.ParserVersion).HasMaxLength(40);
            builder.Property(x => x.OutcomeReason).HasMaxLength(500);
            builder.Property(x => x.RawMessage).HasColumnType("text");
            builder.Property(x => x.Amount).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.ConfidenceScore).HasPrecision(5, 4);

            builder.HasIndex(x => x.OrganizationId);
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.ServerReceivedAtUtc });
            builder.HasIndex(x => new { x.OrganizationId, x.Fingerprint });
            builder.HasIndex(x => new { x.OrganizationId, x.RawHash });
            builder.HasIndex(x => x.FinancialTransactionId);
            builder.HasIndex(x => x.State);

            // Evidence is intentionally NOT uniquely constrained on fingerprint: every
            // observation is recorded, including duplicates, because the fact that a
            // duplicate arrived is itself auditable. Uniqueness is enforced on the ledger.
        });

        modelBuilder.Entity<BusinessExpense>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Amount).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.Currency).IsRequired().HasMaxLength(10);
            builder.Property(x => x.Note).HasMaxLength(500);
            builder.Property(x => x.SubmissionToken).HasMaxLength(64);
            // "What did this month cost?" is the question; the day is how it is asked.
            builder.HasIndex(x => new { x.OrganizationId, x.SpentOn });
            // A repeated submission collides here rather than becoming a second cost.
            builder.HasIndex(x => new { x.OrganizationId, x.SubmissionToken })
                .IsUnique()
                .HasFilter("\"SubmissionToken\" IS NOT NULL");
        });

        modelBuilder.Entity<FloatRequest>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Network).IsRequired().HasMaxLength(32);
            builder.Property(x => x.Code).IsRequired().HasMaxLength(8);
            builder.Property(x => x.Channel).IsRequired().HasMaxLength(16);
            builder.Property(x => x.DecidedVia).HasMaxLength(16);
            builder.Property(x => x.Amount).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            // The owner's list of what is waiting, and an SMS answer found by its code.
            builder.HasIndex(x => new { x.OrganizationId, x.Status, x.Code });
            builder.HasIndex(x => new { x.OrganizationId, x.AgentId, x.RequestedAtUtc });
        });

        modelBuilder.Entity<DayClose>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Channel).IsRequired().HasMaxLength(16);
            builder.Property(x => x.Note).HasMaxLength(500);
            builder.Property(x => x.CountedCash).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.CountedFloat).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.ExpectedCash).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.ExpectedFloat).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.CashMovement).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Property(x => x.FloatMovement).HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            builder.Ignore(x => x.CashDifference);
            builder.Ignore(x => x.FloatDifference);
            // "The agent's last close" and "everyone's close for a day" are the two reads.
            builder.HasIndex(x => new { x.OrganizationId, x.AgentId, x.ClosedAtUtc });
            builder.HasIndex(x => new { x.OrganizationId, x.BusinessDate });
        });

        modelBuilder.Entity<ParsingReport>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.ClientTransactionId).IsRequired().HasMaxLength(100);
            builder.Property(x => x.SenderIdentity).HasMaxLength(50);
            builder.Property(x => x.ObservedNetwork).IsRequired().HasMaxLength(32);
            builder.Property(x => x.ParserVersion).HasMaxLength(40);
            builder.Property(x => x.AppVersion).HasMaxLength(40);
            builder.Property(x => x.Note).HasMaxLength(1000);

            // text, not a bounded string. A provider is free to lengthen its own messages, and
            // truncating the body would quietly destroy the one thing the report exists to
            // carry — most likely at the end, which is where the balance reminder that caused
            // the direction defect sat.
            builder.Property(x => x.RawMessage).IsRequired().HasColumnType("text");

            builder.HasIndex(x => x.OrganizationId);

            // The working queue: unreviewed first, oldest first. Filtered so the index stays
            // the size of the backlog rather than the size of the history.
            builder.HasIndex(x => new { x.ReviewedAtUtc, x.CreatedAt })
                .HasFilter("\"ReviewedAtUtc\" IS NULL");

            // One agent reporting the same transaction twice is a double tap, not two reports.
            builder.HasIndex(x => new { x.OrganizationId, x.ReportedByUserId, x.ClientTransactionId })
                .IsUnique();
        });

        modelBuilder.Entity<SyncAttempt>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.EntityType).IsRequired().HasMaxLength(80);
            builder.Property(x => x.ErrorMessage).HasMaxLength(500);
            builder.HasIndex(x => new { x.OrganizationId, x.TransactionId, x.AttemptNumber }).IsUnique();
            builder.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<DeadLetterTransaction>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.EntityType).IsRequired().HasMaxLength(80);
            builder.Property(x => x.Payload).IsRequired().HasColumnType("text");
            builder.Property(x => x.Reason).HasMaxLength(500);
            builder.HasIndex(x => x.OrganizationId);
            builder.HasIndex(x => x.TransactionId);
        });

        modelBuilder.Entity<AuditLogEntry>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Action).IsRequired().HasMaxLength(150);
            builder.Property(x => x.ActorType).HasMaxLength(50);
            builder.Property(x => x.Details).IsRequired().HasMaxLength(2000);
            builder.Property(x => x.CorrelationId).HasMaxLength(100);
            builder.Property(x => x.Source).HasMaxLength(50);
            builder.Property(x => x.Status).HasMaxLength(50);
            builder.Property(x => x.ErrorCode).HasMaxLength(100);
            builder.Property(x => x.AppVersion).HasMaxLength(50);
            builder.Property(x => x.Platform).HasMaxLength(50);

            builder.HasIndex(x => x.OrganizationId);
            builder.HasIndex(x => x.UserId);
            builder.HasIndex(x => x.RelatedTransactionId);

            // The dashboard's three questions, each of which would otherwise scan the table:
            // "what has gone wrong lately", "what happened in this one request", and "what
            // has this device been doing".
            builder.HasIndex(x => new { x.OrganizationId, x.Severity, x.CreatedAt })
                .HasDatabaseName("IX_AuditLogEntries_Org_Severity_CreatedAt");

            builder.HasIndex(x => x.CorrelationId)
                .HasDatabaseName("IX_AuditLogEntries_CorrelationId");

            builder.HasIndex(x => new { x.DeviceId, x.CreatedAt })
                .HasDatabaseName("IX_AuditLogEntries_Device_CreatedAt");

            // Error grouping reads this directly; without it, grouping recurring failures
            // means reading every error the organization has ever recorded.
            builder.HasIndex(x => new { x.OrganizationId, x.ErrorCode, x.CreatedAt })
                .HasDatabaseName("IX_AuditLogEntries_Org_ErrorCode_CreatedAt");
        });

        modelBuilder.Entity<ReconciliationRecord>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Status).IsRequired().HasMaxLength(40);
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.SessionId }).IsUnique();

            // These are the numbers an operator acts on when a till does not balance. The
            // service already rounds them; the column now refuses to store anything else.
            foreach (var money in new[] { "OpeningCash", "CashInflow", "CashOutflow", "ExpectedCash", "ActualCash", "Difference" })
            {
                builder.Property<decimal>(money)
                    .HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);
            }
        });

        modelBuilder.Entity<FloatBalance>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Network).IsRequired().HasMaxLength(80);

            // The agent is part of what makes a float balance unique. Without them in the key
            // a second agent at the same branch and network could not have a balance at all —
            // the insert failed on the old unique index rather than giving them their own row.
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.AgentId, x.Network }).IsUnique();
            builder.Property(x => x.CurrentFloat).HasPrecision(18, 4);
            builder.Property(x => x.Threshold).HasPrecision(18, 4);
            builder.Property(x => x.OpeningFloat).HasPrecision(18, 4);
        });

        modelBuilder.Entity<CashBalance>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.AgentId }).IsUnique();
            builder.Property(x => x.CurrentCash).HasPrecision(18, 4);
            builder.Property(x => x.OpeningCash).HasPrecision(18, 4);
        });

        modelBuilder.Entity<AlertRecord>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Type).IsRequired().HasMaxLength(100);
            builder.Property(x => x.Message).IsRequired().HasMaxLength(500);
            builder.Property(x => x.Severity).IsRequired().HasMaxLength(30);
            builder.Property(x => x.Network).IsRequired().HasMaxLength(80);
            builder.Property(x => x.Threshold).HasPrecision(18, 4);
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.Type, x.IsAcknowledged, x.CreatedAt });
        });

        modelBuilder.Entity<AlertThreshold>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Network).IsRequired().HasMaxLength(80);
            builder.Property(x => x.WarningThreshold).HasPrecision(18, 4);
            builder.Property(x => x.CriticalThreshold).HasPrecision(18, 4);
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.Network }).IsUnique();
        });

        modelBuilder.Entity<SyncQueueEntry>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.EntityType).IsRequired().HasMaxLength(100);
            builder.Property(x => x.EventType).IsRequired().HasMaxLength(100);
            builder.Property(x => x.Payload).IsRequired().HasColumnType("text");
            builder.Property(x => x.ErrorMessage).HasMaxLength(500);
            builder.HasIndex(x => x.Status);
            builder.HasIndex(x => x.OrganizationId);
            builder.HasIndex(x => x.NextAttemptAtUtc);
        });

        modelBuilder.Entity<SyncConflict>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.ClientTransactionId).IsRequired().HasMaxLength(64);
            builder.Property(x => x.ReasonCode).IsRequired().HasMaxLength(80);
            builder.Property(x => x.CorrelationId).HasMaxLength(64);
            builder.Property(x => x.SubmittedCurrency).HasMaxLength(10);
            builder.Property(x => x.Resolution).HasMaxLength(500);
            builder.Property(x => x.ResolutionNotes).HasMaxLength(2000);
            builder.Property(x => x.SubmittedAmount)
                .HasPrecision(LedgerPolicy.StoragePrecision, LedgerPolicy.StorageScale);

            // Open conflicts are the operational queue, so that is the query to index for.
            builder.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt });
            builder.HasIndex(x => new { x.OrganizationId, x.ClientTransactionId });
            builder.HasIndex(x => x.TransactionId);
            builder.HasIndex(x => x.RelatedTransactionId);
            builder.HasIndex(x => x.BatchId);

            builder.ToTable(table =>
            {
                // A resolved conflict must say who resolved it and when. Without this an
                // audit trail can record that something was settled but not by whom.
                table.HasCheckConstraint(
                    "CK_SyncConflicts_ResolutionComplete",
                    "(\"Status\" IN (0, 1) AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL) OR " +
                    "(\"Status\" IN (2, 3) AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<DeviceEnrollmentCode>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.CodeHash).IsRequired().HasMaxLength(64);
            builder.Property(x => x.CodePrefix).IsRequired().HasMaxLength(16);
            builder.Property(x => x.Label).HasMaxLength(120);

            // Globally unique: the hash is a 160-bit secret, and redemption looks it up by
            // hash. Two identical hashes across tenants would make the lookup ambiguous.
            builder.HasIndex(x => x.CodeHash).IsUnique();

            builder.HasIndex(x => new { x.OrganizationId, x.Status, x.ExpiresAtUtc });
            builder.HasIndex(x => new { x.OrganizationId, x.BranchId });
            builder.HasIndex(x => x.RedeemedByDeviceId);

            builder.ToTable(table =>
            {
                // A code must expire. Without this an issued code could outlive the staff
                // member it was created for and become a permanent way into the tenant.
                table.HasCheckConstraint(
                    "CK_DeviceEnrollmentCodes_MustExpire",
                    "\"ExpiresAtUtc\" > \"CreatedAt\"");

                // Redemption and revocation must record when they happened.
                table.HasCheckConstraint(
                    "CK_DeviceEnrollmentCodes_TerminalStateHasTimestamp",
                    "(\"Status\" = 0) OR " +
                    "(\"Status\" = 1 AND \"RedeemedAtUtc\" IS NOT NULL) OR " +
                    "(\"Status\" = 2 AND \"RevokedAtUtc\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<AuthSession>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.FamilyId).IsRequired().HasMaxLength(100);
            builder.Property(x => x.SecurityStampAtIssue).IsRequired().HasMaxLength(128);
            builder.Property(x => x.ClientFingerprint).HasMaxLength(128);
            builder.Property(x => x.CreatedFromIpPrefix).HasMaxLength(64);
            builder.Property(x => x.RevokedReason).HasMaxLength(200);

            builder.HasIndex(x => new { x.UserId, x.Status });
            builder.HasIndex(x => x.FamilyId).IsUnique();
            builder.HasIndex(x => x.DeviceId);
            builder.HasIndex(x => new { x.OrganizationId, x.Status });
            builder.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<MfaCredential>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.KeyId).HasMaxLength(64);
            builder.Property(x => x.CodeHash).HasMaxLength(512);
            builder.Property(x => x.CodeSalt).HasMaxLength(128);

            builder.HasIndex(x => new { x.UserId, x.Method, x.Status });
            builder.HasIndex(x => x.OrganizationId);

            builder.ToTable(table =>
            {
                // A TOTP credential carries an encrypted secret; a recovery code carries a
                // hash. Neither may ever be stored in the other's shape, and a plaintext
                // secret column does not exist at all.
                table.HasCheckConstraint(
                    "CK_MfaCredentials_SecretShape",
                    "(\"Method\" = 0 AND \"EncryptedSecret\" IS NOT NULL AND \"CodeHash\" IS NULL) OR " +
                    "(\"Method\" = 1 AND \"CodeHash\" IS NOT NULL AND \"EncryptedSecret\" IS NULL)");
            });
        });
    }
}
