# Scalability

Zazi is built to grow without unnecessary early infrastructure complexity.

## Measured, not assumed

`tools/Zazi.LoadTest` seeds a database that is not production and times the queries behind the
pages people wait on. Run it yourself:

```bash
dotnet run --project tools/Zazi.LoadTest -- --seed --businesses 2000 --agents 3 --days 30 --per-agent-day 25
dotnet run --project tools/Zazi.LoadTest -- --measure
dotnet run --project tools/Zazi.LoadTest -- --size
```

Result at **4,500,000 transactions across 2,000 businesses** (22 September 2026, PostgreSQL 16
on a laptop, median of five runs):

| Query behind | Median | Worst | Plan |
|---|---|---|---|
| Today's totals | 0.3 ms | 5.3 ms | index |
| The 31-day chart | 1.2 ms | 3.9 ms | index |
| Transactions, first page | 0.2 ms | 0.8 ms | index |
| One customer's history | 0.2 ms | 0.4 ms | index |
| One agent's ledger | 0.9 ms | 1.6 ms | index |
| A month's statement | 0.5 ms | 0.8 ms | index |
| Sync duplicate check | 0.1 ms | 0.2 ms | index |
| Cross-route duplicate check | 0.2 ms | 0.4 ms | index |

Every one is index-backed; none degrades into a sequential scan at that size. **The query
design is not what limits growth.** What limits it is the size of the managed instance:

| Measured | Figure |
|---|---|
| Storage per transaction, indexes included | ≈ 430 bytes |
| 4.5M transactions | 1.84 GB |
| Production disk today | **1 GB**, autoscaling off |

So the disk fills at roughly **2.3 million transactions**. In the shape above that is about
2,000 businesses trading for a fortnight, or 200 businesses for five months.

## What to change, and when

Do nothing until a threshold is actually near. Then take the smallest step:

| When | Step |
|---|---|
| Database above ~700 MB | Turn on disk autoscaling, or raise the disk. Storage first: it is the wall that arrives first. |
| Sustained CPU above ~70%, or cache hit ratio falling | Move the database off `0.1c-256mb`. 256 MB holds very little of a multi-gigabyte table in memory. |
| API instance CPU sustained above ~70% | A second API instance. The API is stateless, so this is a slider. |
| Connections refused | Turn on Render's PgBouncer (free, port 6432) before buying a larger plan. |
| The portal feels slow under many owners at once | A second portal instance **only with sticky sessions** — Blazor Server holds each signed-in owner's UI state in the instance's memory. |

Each service already bounds its own connection pool (`DatabaseConnection.WithPoolCeiling`):
20 for the API, 10 for the portal. Npgsql's default of 100 per process would exhaust a small
instance, and a database refusing connections fails every request at once.

## Current approach

- PostgreSQL as the source of truth
- database indexes and constrained queries
- paginated APIs
- bounded sync processing
- stateless API design
- future-ready worker model for background processing

## Not introduced yet

Redis is intentionally not a hard dependency. It is reserved for clear performance problems such as distributed rate limiting, distributed locking, or high-volume ephemeral state. The system remains cost-conscious and does not add infrastructure for speculative scaling.

## Horizontal scaling path

- add API instances behind a load balancer
- keep session and auth state stateless
- rely on PostgreSQL indexes and query discipline
- add background workers only when observed demand justifies them
