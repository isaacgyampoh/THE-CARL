# SMS Engine

Zazi accepts transaction evidence only from supported, explicitly permitted sources. The system does not claim to read arbitrary personal SMS from feature phones without a valid communication path.

## Supported model

- Android device with explicit SMS permission
- companion device or approved relay with explicit registration
- manual transaction entry as the fallback when SMS capture is unavailable
- provider-specific parser adapters mounted behind a common interface

## Provider-independent pipeline

1. SMS received
2. message normalization
3. provider detection
4. parser selection
5. extraction of amount, reference, phone, type, timestamp
6. validation and confidence scoring
7. duplicate detection using message hash and reference heuristics
8. transaction evidence persistence
9. queueing for sync if connectivity is offline

## Provider adapters

- MTN
- AirtelTigo
- Telecel
- generic fallback parser for unknown or unsupported messages

Unknown transaction classes never become financial ledger events. They remain evidence-only records and are routed for review.
