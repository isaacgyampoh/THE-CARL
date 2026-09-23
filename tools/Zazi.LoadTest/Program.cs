using System.Diagnostics;
using System.Globalization;
using Npgsql;

// Zazi load test.
//
// Answers one question with evidence: does the system still answer quickly when there are
// thousands of businesses and millions of transactions in the database? It seeds a database
// that is NOT production with a realistic shape of data, then times the queries the product
// actually runs and prints the plan for each, so a sequential scan cannot hide behind a
// number that happens to look acceptable today.
//
// Never point this at production. It refuses hosts that are not local unless --i-know is
// passed, because seeding invents businesses and transactions.

var arguments = Args.Parse(args);
var connectionString = arguments.Value("connection")
    ?? "Host=127.0.0.1;Port=55433;Database=zazi_load;Username=carl;Password=carl-test-password";

if (!connectionString.Contains("127.0.0.1", StringComparison.Ordinal)
    && !connectionString.Contains("localhost", StringComparison.Ordinal)
    && !arguments.Has("i-know"))
{
    Console.Error.WriteLine("Refusing to run against a non-local database. Seeding invents data.");
    return 1;
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

if (arguments.Has("seed"))
{
    var businesses = arguments.Number("businesses", 2_000);
    var agentsEach = arguments.Number("agents", 3);
    var days = arguments.Number("days", 30);
    var perAgentPerDay = arguments.Number("per-agent-day", 25);

    Console.WriteLine($"Seeding {businesses:N0} businesses × {agentsEach} agents × {days} days × {perAgentPerDay}/day");
    Console.WriteLine($"≈ {(long)businesses * agentsEach * days * perAgentPerDay:N0} transactions");

    var started = Stopwatch.GetTimestamp();
    await Seed.RunAsync(connection, businesses, agentsEach, days, perAgentPerDay);
    Console.WriteLine($"Seeded in {Stopwatch.GetElapsedTime(started).TotalSeconds:N1}s");
}

if (arguments.Has("size"))
{
    // What the data actually costs on disk, which is what decides when the production plan
    // needs more storage — a question no amount of query timing answers.
    await using var command = new NpgsqlCommand(
        """
        SELECT pg_size_pretty(pg_total_relation_size('"Transactions"')),
               pg_size_pretty(pg_database_size(current_database())),
               (SELECT count(*) FROM "Transactions"),
               current_setting('max_connections')
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"transactions table {reader.GetString(0)} · database {reader.GetString(1)} · " +
                          $"{reader.GetInt64(2):N0} rows · max_connections here {reader.GetString(3)}");
    }
}

if (arguments.Has("measure"))
{
    await Measure.RunAsync(connection, arguments.Number("runs", 5));
}

return 0;

internal static class Seed
{
    public static async Task RunAsync(NpgsqlConnection connection, int businesses, int agents, int days, int perDay)
    {
        // Seeding is repeatable: whatever a previous run left behind goes first, in foreign-key
        // order. Only rows this tool created are touched, and only in a database that is not
        // production — the guard at the top of the program sees to that.
        await ExecuteAsync(connection, """
            DELETE FROM "Transactions" WHERE "OrganizationId" IN
                (SELECT "Id" FROM "Organizations" WHERE "Name" LIKE 'Load business %')
            """);
        await ExecuteAsync(connection, """
            DELETE FROM "Users" WHERE "OrganizationId" IN
                (SELECT "Id" FROM "Organizations" WHERE "Name" LIKE 'Load business %')
            """);
        await ExecuteAsync(connection, """
            DELETE FROM "Branches" WHERE "OrganizationId" IN
                (SELECT "Id" FROM "Organizations" WHERE "Name" LIKE 'Load business %')
            """);
        await ExecuteAsync(connection, """DELETE FROM "Organizations" WHERE "Name" LIKE 'Load business %'""");

        // One statement per table, server-side: pulling millions of rows through the client
        // would measure this program rather than the database.
        await ExecuteAsync(connection, """
            INSERT INTO "Organizations" ("Id", "Name", "Country", "CurrencyCode", "MfaPolicy",
                "SendCustomerReceipts", "SendDailyDigest", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), 'Load business ' || i, 'GH', 'GHS', 0, false, true, now(), now()
            FROM generate_series(1, @businesses) AS i
            """, ("businesses", businesses));

        await ExecuteAsync(connection, """
            INSERT INTO "Branches" ("Id", "OrganizationId", "Name", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), o."Id", 'Main', now(), now()
            FROM "Organizations" o WHERE o."Name" LIKE 'Load business %'
            """);

        await ExecuteAsync(connection, """
            INSERT INTO "Users" ("Id", "OrganizationId", "BranchId", "FullName", "Email", "IsActive",
                "EmailVerified", "PhoneVerified", "PasswordHash", "PasswordSalt", "FailedLoginAttempts",
                "SecurityStamp", "CredentialType", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), b."OrganizationId", b."Id", 'Agent ' || a, NULL, true,
                   false, false, '', '', 0, gen_random_uuid()::text, 1, now(), now()
            FROM "Branches" b
            JOIN "Organizations" o ON o."Id" = b."OrganizationId" AND o."Name" LIKE 'Load business %'
            CROSS JOIN generate_series(1, @agents) AS a
            """, ("agents", agents));

        // Transactions: the table everything else is read from, so it gets the real shape —
        // a customer number, a network, a direction, and deltas that move balances.
        await ExecuteAsync(connection, $"""
            INSERT INTO "Transactions" ("Id", "OrganizationId", "BranchId", "AgentId", "Network",
                "Type", "Amount", "Currency", "CustomerPhoneNumber", "TransactionAtUtc", "Source",
                "State", "ConfidenceScore", "CashDelta", "FloatDelta", "AcceptedAtUtc", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), u."OrganizationId", u."BranchId", u."Id",
                   (ARRAY['MTN','TELECEL','AIRTELTIGO'])[1 + (random() * 2)::int],
                   CASE WHEN random() < 0.5 THEN 0 ELSE 1 END,
                   (20 + (random() * 480)::int)::numeric(18,4),
                   'GHS',
                   '024' || lpad((random() * 9999999)::int::text, 7, '0'),
                   now() - (d || ' days')::interval - ((random() * 12)::int || ' hours')::interval,
                   0, 5, 1,
                   CASE WHEN random() < 0.5 THEN 1 ELSE -1 END * (20 + (random() * 480)::int)::numeric(18,4),
                   CASE WHEN random() < 0.5 THEN -1 ELSE 1 END * (20 + (random() * 480)::int)::numeric(18,4),
                   now(), now(), now()
            FROM "Users" u
            JOIN "Organizations" o ON o."Id" = u."OrganizationId" AND o."Name" LIKE 'Load business %'
            CROSS JOIN generate_series(0, @days - 1) AS d
            CROSS JOIN generate_series(1, @perDay) AS n
            """, ("days", days), ("perDay", perDay));

        await ExecuteAsync(connection, "ANALYZE");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, int Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var started = Stopwatch.GetTimestamp();
        var rows = await command.ExecuteNonQueryAsync();
        Console.WriteLine($"  {rows,12:N0} rows in {Stopwatch.GetElapsedTime(started).TotalSeconds,6:N1}s");
    }
}

internal static class Measure
{
    /// <summary>
    /// The queries behind the pages people wait on. Each is run against the busiest business
    /// in the seeded set, which is the worst realistic case rather than an average one.
    /// </summary>
    private static readonly (string Name, string Sql)[] Queries =
    [
        ("Today: totals for the day", """
            SELECT count(*), coalesce(sum("Amount"), 0)
            FROM "Transactions"
            WHERE "OrganizationId" = @org AND "TransactionAtUtc" >= date_trunc('day', now())
            """),
        ("Today: 31-day chart series", """
            SELECT date_trunc('day', "TransactionAtUtc") AS d, "Type", count(*), sum("Amount")
            FROM "Transactions"
            WHERE "OrganizationId" = @org AND "TransactionAtUtc" >= now() - interval '31 days'
            GROUP BY 1, 2
            """),
        ("Transactions: first page, newest first", """
            SELECT * FROM "Transactions"
            WHERE "OrganizationId" = @org
            ORDER BY "TransactionAtUtc" DESC
            LIMIT 50
            """),
        ("Transactions: a customer's history", """
            SELECT * FROM "Transactions"
            WHERE "OrganizationId" = @org AND "CustomerPhoneNumber" = @customer
            ORDER BY "TransactionAtUtc" DESC
            LIMIT 50
            """),
        ("Agent ledger: one agent's movements", """
            SELECT * FROM "Transactions"
            WHERE "OrganizationId" = @org AND "AgentId" = @agent
              AND ("CashDelta" <> 0 OR "FloatDelta" <> 0)
            ORDER BY "TransactionAtUtc" DESC
            LIMIT 50
            """),
        ("Statement: a month for the business", """
            SELECT count(*), sum("Amount") FROM "Transactions"
            WHERE "OrganizationId" = @org
              AND "TransactionAtUtc" >= date_trunc('month', now())
            """),
        ("Sync: is this client id already recorded?", """
            SELECT 1 FROM "Transactions"
            WHERE "OrganizationId" = @org AND "ClientTransactionId" = 'CTX-00000000-0000000000000000000000000A'
            """),
        ("Cross-route duplicate check", """
            SELECT 1 FROM "Transactions"
            WHERE "OrganizationId" = @org AND "ProviderReference" = 'MP000000.0000.A00000'
              AND "Network" = 'MTN' AND "Type" = 1 AND "Amount" = 250.50
            """)
    ];

    public static async Task RunAsync(NpgsqlConnection connection, int runs)
    {
        var (organizationId, agentId, customer, rows) = await BusiestAsync(connection);
        Console.WriteLine();
        Console.WriteLine($"Transactions in table: {rows:N0}");
        Console.WriteLine($"Measuring against the busiest business ({runs} runs each, median reported)");
        Console.WriteLine();
        Console.WriteLine($"{"Query",-42}{"median",10}{"worst",10}  plan");

        foreach (var (name, sql) in Queries)
        {
            var timings = new List<double>();
            for (var run = 0; run < runs; run++)
            {
                timings.Add(await TimeAsync(connection, sql, organizationId, agentId, customer));
            }

            timings.Sort();
            var plan = await PlanAsync(connection, sql, organizationId, agentId, customer);
            var scan = plan.Contains("Seq Scan on \"Transactions\"", StringComparison.Ordinal) ? "SEQ SCAN" : "index";
            Console.WriteLine($"{name,-42}{timings[timings.Count / 2],8:N1}ms{timings[^1],8:N1}ms  {scan}");
        }
    }

    private static async Task<(Guid Org, Guid Agent, string Customer, long Rows)> BusiestAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT t."OrganizationId", t."AgentId", t."CustomerPhoneNumber",
                   (SELECT count(*) FROM "Transactions")
            FROM "Transactions" t
            GROUP BY t."OrganizationId", t."AgentId", t."CustomerPhoneNumber"
            ORDER BY count(*) DESC
            LIMIT 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("No transactions: seed first with --seed.");
        }

        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3));
    }

    private static async Task<double> TimeAsync(NpgsqlConnection connection, string sql, Guid org, Guid agent, string customer)
    {
        await using var command = Command(connection, sql, org, agent, customer);
        var started = Stopwatch.GetTimestamp();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static async Task<string> PlanAsync(NpgsqlConnection connection, string sql, Guid org, Guid agent, string customer)
    {
        await using var command = Command(connection, "EXPLAIN " + sql, org, agent, customer);
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, Guid org, Guid agent, string customer)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("agent", agent);
        command.Parameters.AddWithValue("customer", customer);
        return command;
    }
}

internal sealed record Args(Dictionary<string, string?> Values)
{
    public static Args Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            values[key] = value;
        }

        return new Args(values);
    }

    public bool Has(string key) => Values.ContainsKey(key);

    public string? Value(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public int Number(string key, int fallback) =>
        int.TryParse(Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
