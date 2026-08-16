# Scalability

THE CARL is built to grow without unnecessary early infrastructure complexity.

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
