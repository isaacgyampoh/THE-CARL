# Reconciliation

The reconciliation engine compares expected cash and float positions against actual branch state.

## Model

- opening float
- deposits and withdrawals
- transfer effects and adjustments
- expected vs actual cash
- expected vs actual float
- balanced/short/over results

## Rules

- historical transactions are never silently changed to balance the books
- corrections create audit events and adjustment records
- reconciliation is tied to a branch session and retains the difference value for review
- low-float and reconciliation alerts are produced from the same financial history as the ledger
