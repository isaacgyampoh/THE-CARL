# ADR-007: Android / Play compliance

- Status: Accepted
- Date: 2026-08-15

## Context

Google Play treats SMS permissions as highly sensitive. A compliant implementation must be validated before distribution.

## Decision

Do not write or ship restricted SMS permissions until the exact use case is reviewed and eligibility is confirmed. Manual transaction entry remains supported at all times. If the use case is not eligible, the product will not use restricted SMS permissions and will rely on compliant alternatives or manual entry.

## Consequences

- Email and Play Store compliance are treated as a product gate, not a technical afterthought
- The app stays within policy boundaries and avoids insecure workaround logic
- The product remains usable on non-SMS paths
