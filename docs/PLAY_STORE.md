# Publishing Zazi on Google Play

What Google asks for, what Zazi answers, and how a release is built. Everything here was
checked against the app's actual behaviour — a Data safety form that disagrees with the code is
grounds for removal, not a formality.

## The release artifact

```bash
cd android
JAVA_HOME=~/.thecarl-toolchain/jdk21/Contents/Home \
  ./gradlew :app:bundleRelease -PapiBaseUrl=https://api.getzazi.com/
# → app/build/outputs/bundle/release/app-release.aab
```

Signing properties live in `~/.gradle/gradle.properties` and the keystore outside the
repository. Neither is ever committed. **If the upload key is lost, the app cannot be updated
again** — keep a copy somewhere you will still have in five years.

The bundle is signed v2 **and v3**. v3 is what carries proof of rotation, so the upload key can
be replaced later without abandoning the app; v1 is off because nothing below API 24 can install
this app anyway. Check any build before uploading it:

```bash
apksigner verify -v --print-certs app/build/outputs/apk/release/app-release.apk
```

The release certificate is `CN=Zazi, OU=Production, O=Zazi, L=Accra, ST=Greater Accra, C=GH`,
SHA-256 `2587a167 8ca90793 afe36a7c 75511550 16f5b492 939f0b81 151f71c2 0bf6dcfd`. If a build
ever shows a different fingerprint, it was signed with the wrong key and Play will reject it.

| Field | Value |
|---|---|
| Package | `app.zazi` |
| App name | Zazi |
| Version | 2.8.0 (versionCode 11) |
| Minimum Android | 8.0 (API 26) |
| Target Android | 16 (API 36) — required by Play for new apps since 31 August 2026 |
| Format | Android App Bundle (.aab) |
| Category | Finance |

## Store listing

**Short description (80 characters max)**

> Record every mobile money transaction, and know what your agents are holding.

**Full description**

> Zazi is for mobile money agent businesses in Ghana.
>
> Your agent's phone records each transaction as the network's own message arrives, so nothing
> is forgotten at a busy counter, and it keeps working when the network does not — everything
> syncs when there is signal again.
>
> As an owner you can see, without ringing anyone: what cash and float each agent is holding,
> what was traded today, what was earned in commission, what the business spent, and what it
> kept. Give an agent cash or float and it is recorded against them; every balance is explained
> by the transactions behind it.
>
> At the end of the day an agent counts their cash and float and Zazi says whether it agrees
> with what was recorded — a shortage is found the same evening, not next month.
>
> Agents without a smartphone are not left out: they can record transactions, ask for float and
> close the day by SMS from any keypad phone.
>
> • Works offline — syncs when there is data
> • Every transaction carries the customer's number and the exact time
> • Statements for any day, week, month or year, as PDF or for Excel
> • Cash and float tracked per agent, per network
> • End-of-day counts with shortages flagged
> • Send a customer a receipt from your own WhatsApp
>
> Zazi records mobile money; it does not move it. You keep using MTN, Telecel and AirtelTigo as
> you do now.

**Assets**, in `android/play-assets/`:

| File | What it is |
|---|---|
| `icon-512.png` | App icon, 512×512 |
| `feature-1024x500.png` | Feature graphic |
| `screenshots/1-today.png` | The agent's day: balances, quick actions, activity |
| `screenshots/2-record.png` | Recording a transaction |
| `screenshots/3-close-day.png` | Counting cash and float at closing |

The screenshots are real captures from the signed 2.8.0 build running against production, not
mock-ups. `icon.html` and `feature.html` are the sources the two graphics were rendered from;
re-render them if the brand changes.

## Data safety answers

| Question | Answer | Why |
|---|---|---|
| Does the app collect or share user data? | Yes, collects. No sharing for advertising. | |
| Personal info — name | Collected | Owner and worker names identify who recorded what. |
| Personal info — email | Collected | Sign-in, password reset, the evening summary. |
| Personal info — phone number | Collected | The business's number, and each customer's number on a transaction. |
| Financial info — other financial info | Collected | Transaction amounts, balances, costs. **Not** payment card or bank account details. |
| Messages — SMS | Collected, optional | Mobile money confirmations are parsed **on the device**. A raw message leaves the phone only when the agent taps "report a mistake". |
| Device or other IDs | Collected | An identifier the app generates for the handset, so a lost phone can be revoked. |
| App activity, crash logs, diagnostics | Collected | Audit trail and error logs, to account for money and to fix faults. |
| Is data encrypted in transit? | Yes | HTTPS everywhere. |
| Can users request deletion? | Yes | support@getzazi.com, within 30 days. |
| Is data shared with third parties? | Service providers only | Hosting (Render), email (Resend), SMS provider, Play distribution. |
| Advertising or analytics SDKs | None | The app carries none. |

Privacy policy URL: `https://app.getzazi.com/privacy`

## Permissions, and what to say about each

| Permission | Why the app asks | Declaration |
|---|---|---|
| `INTERNET`, `ACCESS_NETWORK_STATE` | Syncing to the server | No declaration needed |
| `RECEIVE_SMS` | Reads mobile money confirmations to record transactions without typing | **Needs the SMS permissions declaration.** Core purpose: financial record keeping. The app is fully usable without it — transactions are typed by hand. |
| `WAKE_LOCK`, `RECEIVE_BOOT_COMPLETED`, `FOREGROUND_SERVICE` | Finishing a sync that started while offline | Standard WorkManager use |

The SMS declaration is the one Google scrutinises. Answer it as: the app reads only mobile
money confirmations, parses them on the device, uploads the parsed result rather than the
message, and works without the permission.

## Instructions for the reviewer

A reviewer cannot sign up as a business and then find nothing to look at, so give them an
account with data in it:

```
Owner portal: https://app.getzazi.com
Email:    reviewer@getzazi.com
Password: (create one; rotate after review)

Agent app: open, tap "I have an activation code", enter: (issue a fresh code on the Team page)
```

The **Zazi Smoke Test** business already holds usable data — a worker holding ₵500 cash and
₵1,200 MTN float, with the ledger behind it — so it can seed the reviewer account rather than
building one from nothing. Give the reviewer their own login, not the owner's.

Say plainly: "Zazi records mobile money transactions for agent businesses. It does not move
money and is not a payment app. The SMS permission reads mobile money confirmations on the
device; the app is usable without it."

## Release stages

Google requires a closed test with at least **12 testers for 14 continuous days** before a
personal developer account can publish to production. An organisation account does not have
this requirement. Plan for it: the pilot businesses are the testers.

1. Internal testing — the team, immediately.
2. Closed testing — 12+ testers, 14 days.
3. Production — staged rollout, 20% then 100%.

## Every release after the first

1. Bump `versionCode` and `versionName` in `android/app/build.gradle.kts`.
2. Run the suites: `./gradlew test` and the backend's.
3. Build the bundle (above) and install the APK on a real phone first.
4. Upload the `.aab`, write what changed, roll out in stages.
