# Offline Sync

THE CARL is built around realistic device connectivity instead of assuming all transaction devices are always online.

## Supported scenarios

- MODE 1: Android transaction phone running THE CARL and receiving SMS locally
- MODE 2: dedicated approved companion Android device linked to a branch device
- MODE 3: manual transaction entry for feature phones or unsupported devices

## Queue model

- local transaction persistence
- idempotency key
- message fingerprint
- device/source metadata
- sync status tracking
- retry and dead-letter handling
- replay protection before any server-side financial mutation

## Sync processing

Pending queue items are processed in bounded batches. Successful items are marked synced. Failed items are retried with backoff. Persistent dead-letter records retain the original payload and reason so operators can investigate without losing information.

## Safety rules

- The same transaction may be received more than once.
- The server checks idempotency keys and message fingerprints before creating ledger effects.
- A transaction is never counted twice because of duplicate ingest or replay.
- A completely offline feature phone must never be treated as remotely reachable unless there is a real communication path.
