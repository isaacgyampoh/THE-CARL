# ADR-006: Zazi Credits and paid plans

**Status:** Accepted as a design. **Not built.** Nothing in the codebase sells anything yet.

## Context

Zazi will be paid for with prepaid credits: a business buys a package, uses it up, and buys
another. Credits are a right to use the software. They are **not money**, and the distinction
has to be built in rather than remembered, because everything else in this system genuinely is
money and the two must never be confused.

## Decision

### Credits are their own ledger, beside the money ledger and never inside it

A credit balance is the sum of credit entries, exactly as a cash balance is the sum of
transactions. No column anywhere holds "current credits" as an editable number, because a
balance that can be typed is a balance that can be invented.

Four tables:

| Table | Holds |
|---|---|
| `CreditPackages` | What is for sale: name, credits, price, currency, active or retired. Prices change by retiring a package and adding another, so an old purchase still says what was bought. |
| `CreditPurchases` | One attempt to buy: business, package, amount, currency, provider, provider reference, status (`Pending`, `Paid`, `Failed`, `Abandoned`), timestamps. |
| `CreditEntries` | The ledger: business, signed amount, reason (`Purchase`, `Consumption`, `Grant`, `Expiry`, `Refund`), the purchase or usage it came from, and when. |
| `CreditConsumption` | What used a credit: which business, which action, which record. |

### Credits cannot become money

- No entry may be created except by a paid purchase, an explicit administrative grant with a
  reason, or a consumption.
- There is no withdrawal, no transfer between businesses, and no refund to cash. A refund is
  recorded as a negative entry against the purchase it reverses, and money is returned through
  the payment provider, never through Zazi.
- Credits are never shown in cedis beside cash or float. They have their own screen and their
  own word.

### A payment creates credits exactly once, and only when verified

The order is fixed, and each step is refused if the one before it did not happen:

1. The business chooses a package; a `Pending` purchase is written with a reference of our own.
2. They are sent to the provider.
3. The provider calls our webhook. **The signature is verified** — an unsigned or badly signed
   callback is logged and refused.
4. We call the provider's own API to confirm the payment independently. A callback saying
   "paid" is a claim, not proof.
5. Inside one database transaction: the purchase moves `Pending → Paid` **only if it is still
   `Pending`**, and the credit entry is written with the purchase id as its unique key.

Step 5 is what makes a repeated callback harmless: the second one finds the purchase already
`Paid` and the entry already present, and writes nothing. A failed or abandoned payment leaves
the purchase in its own state and never produces an entry.

The frontend saying "payment successful" is treated as decoration. Only our own verification
creates credits.

### What is audited

Every purchase, verification outcome, grant, refund and consumption is written to the audit
log with who or what caused it. A credit balance that cannot be explained line by line is the
same defect as a cash balance that cannot be.

## Consequences

- Reading a balance costs a sum over the business's entries, which is bounded and indexed by
  business and date, exactly as transactions are.
- A provider outage delays credits; it cannot create them. Purchases sit `Pending` and are
  reconciled by re-querying the provider.
- Switching provider means writing another verifier. Nothing else moves, because the ledger
  knows about purchases rather than about a particular provider.

## Still to do before any of this is switched on

1. Choose the provider (Paystack and Hubtel both serve Ghana and sign their webhooks).
2. Build the four tables and the entry rules, with tests for: duplicate callback, forged
   signature, failed payment, provider timeout, and a purchase that is already paid.
3. Build the screens: packages, buy, balance, history, and a low-balance warning.
4. Decide what consumes a credit, and at what rate. Until that is decided the ledger has
   nothing to subtract, and a pricing model that changes later is far cheaper to change before
   anyone has bought anything.
