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

## What the pipeline cannot catch

Steps 6 and 7 protect against messages the parser cannot read. A result below the confidence
floor, or with no recoverable amount or type, is held for human classification and never
becomes a ledger event. That gate works.

It has nothing to say about a message the parser reads **confidently and wrongly**, and that is
the failure mode that has actually occurred. Both defects found on 21 Sep 2026 produced
high-confidence wrong answers:

- one network's messages claimed by another network's parser, because parser selection took the
  first adapter whose `canHandle` matched and body matching could fire ahead of sender matching;
- a cash-out recorded as a deposit, because direction was searched for across the whole message
  and a trailing balance reminder — "Your cash in hand is now GHS 1,750.00" — was found first.

Both were caught by inventing a more realistic message than the corpus held, not by anything
structural. The corpus in `contracts/sms-contract-fixtures.json` is the shared contract between
the Kotlin and C# parsers and keeps them from diverging, but every message in it was written
rather than captured, and it is MTN-heavy: of 21 fixtures, one is Telecel and one AirtelTigo.

So the parser is verified against what we *believe* the formats are.

## Parsing reports

The correction for that is the agent, who knows what happened at the counter. A transaction
captured from SMS carries a "This is wrong" action on its detail screen, which asks what was
wrong — wrong way round, wrong amount, wrong network, not a transaction — and sends the
provider's message with the answer.

This is the only path by which a message body leaves a handset. The sync payload
(`SyncTransactionRequestItem`) carries the parsed result and never the text, which is the right
default for software sitting on other people's financial correspondence. It is also why, before
this existed, a wrong transaction could not be diagnosed at all from the server: the reading was
visible and the message that produced it was not.

The exception is therefore made as narrowly as it can be:

- one body, about one transaction, sent because an agent tapped;
- the dialog says what will be sent before the tap, rather than burying consent in a setting;
- no background send, no batch, and no setting that turns it on for everything;
- deliberately **not** routed through the outbox — that retries indefinitely, and a customer's
  message re-sending itself while the network flaps is not what the agent agreed to. A failed
  report is dropped and can be sent again by hand.

The body is stored verbatim. Normalising or trimming it on the way in would destroy the evidence,
and the clause that caused the direction defect sat at the very end of the message, which is
exactly what a trim takes.

`POST /api/v1/parsing-reports` accepts one (`evidence.submit`, so agents included).
`GET /api/v1/parsing-reports` returns the unreviewed backlog oldest first, under `audit.read`
— submitting is an agent's job, reading every message their colleagues reported is not.
