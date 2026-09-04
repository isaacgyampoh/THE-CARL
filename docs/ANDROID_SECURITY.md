# Android Security Model

What Zazi protects on an Android device, how, and what it deliberately does not claim.

---

## What is stored where

| Data | Location | Protection |
|---|---|---|
| Access token | `zazi.credentials` preferences | AES-256/GCM, key in Android Keystore |
| Refresh token | same | same |
| Device installation id | same | same |
| Database passphrase | `zazi.database` preferences | 32 random bytes, wrapped by the same Keystore key |
| Evidence, transactions, outbox | `zazi.db` (Room) | SQLCipher, key from above |
| Raw SMS bodies | `transaction_evidence.rawMessage` | Encrypted with the database; purgeable |

**Credentials are never in Room.** The database is for operational data; a database
disclosure must not also yield authentication material. They are two separate stores with
two separate failure modes on purpose.

---

## Key handling

`AndroidKeystoreCryptoBox` generates an AES-256 key **inside** the Android Keystore. The
application holds a handle, never the key bytes — the secret cannot be extracted by reading
process memory or application storage.

**GCM, not CBC.** GCM is authenticated. Tampering with stored ciphertext fails the
authentication tag and surfaces as a clean `null`, which the app treats as "no credentials,
log in again". An unauthenticated mode would decrypt tampered data into plausible-looking
garbage that the app would then act on.

**No key is in source or configuration.** The database passphrase is 32 bytes from
`SecureRandom`, generated on first launch and stored only wrapped. A hardcoded or
constant-derived key would make every installation openable with one secret extracted from
the APK.

### `setUserAuthenticationRequired` is deliberately off

Requiring device authentication to use the key would be stronger, and is wrong here: the
sync worker must drain an agent's outbox in the background with the screen locked. Requiring
authentication would strand queued transactions until someone unlocked the phone — trading a
real availability failure for a marginal confidentiality gain.

---

## What is *not* claimed

**Hardware backing is not asserted.** Whether the Keystore key is bound to a secure element
or TEE depends on the device. Where no secure hardware exists, the Keystore falls back to a
software-isolated implementation. Both are substantially better than clear text; the code
does not claim a guarantee it cannot verify at runtime.

**Root defeats this.** On a rooted or compromised device, an attacker with the app's identity
can ask the Keystore to decrypt. Local encryption raises the cost of offline attacks — a
stolen phone, a pulled backup, a forensic image — it does not defend against an adversary
already executing as the app.

**Backups are excluded, not encrypted-and-backed-up.** `data_extraction_rules.xml` excludes
every domain from cloud backup and device transfer. A backup would move the database
somewhere the Keystore key cannot follow, producing either a leak or an unopenable file.

---

## If the key is lost

A Keystore key can be invalidated — a factory reset, a restored backup, some lock-screen
changes. When that happens `decrypt` returns null and:

- **Credentials** → treated as absent; the user logs in again. Fully recoverable.
- **Database key** → a new key is generated and the old database cannot be opened.

The second is a real data-loss path, and the honest position is that it is the correct
outcome: a database whose key is gone cannot be trusted or read. Local data is a projection
plus an outbox. The projection is rebuildable from the server. **The outbox is not** — it
holds work that never reached the server, which is why unsynced counts are surfaced to the
agent rather than hidden, and why the sync worker drains aggressively when connectivity
returns.

---

## Data minimisation

Raw SMS bodies are stored only while needed for review and are nullable so they can be
purged (`EvidenceDao.purgeRawMessagesOlderThan`). Purging does not break duplicate detection,
because the fingerprint is computed from extracted fields rather than message text.

Never logged: tokens, the database key, raw SMS bodies, full customer phone numbers.

---

## Test coverage

| Property | Where | Status |
|---|---|---|
| Save / read / overwrite / clear | `CredentialStoreTest` (Robolectric) | ✅ RUN |
| No plaintext token on disk | `CredentialStoreTest` | ✅ RUN |
| Corrupt ciphertext → null, no crash | `CredentialStoreTest` | ✅ RUN |
| Survives store recreation | `CredentialStoreTest` | ✅ RUN |
| **Real Keystore round-trip** | `EncryptedDatabaseInstrumentedTest` | ✅ RUN |
| **Tampered ciphertext rejected** | same | ✅ RUN |
| **Nonce differs per encryption** | same | ✅ RUN |
| **Database file is not plaintext SQLite** | same | ✅ RUN |
| **Data survives close/reopen encrypted** | same | ✅ RUN |
| **Database key stable across instances** | same | ✅ RUN |
| **Credentials survive a Keystore-backed store** | same | ✅ RUN |
| **Wrong key cannot open the database** | same | ✅ RUN |

Robolectric does not implement the `AndroidKeyStore` provider, and SQLCipher ships native
libraries for Android ABIs that cannot load under a JVM runner. The crypto boundary is
therefore split: storage logic is host-tested through a fake `CryptoBox`, and the real
platform crypto is instrumentation-tested.

**To run the instrumentation suite:**

```bash
sdkmanager "system-images;android-35;google_apis;arm64-v8a"
avdmanager create avd -n carl-test -k "system-images;android-35;google_apis;arm64-v8a"
emulator -avd carl-test -no-window &
adb wait-for-device

cd android && ./gradlew :core:data:connectedDebugAndroidTest
```

All eight executed on a `carl-test` AVD (Android 35, `google_apis`, `arm64-v8a`) and passed.
**SQLCipher encryption and the Keystore key handling are verified on a real Android runtime**,
not merely configured.

Note the dependency this needs: `androidx.test.ext:junit` does not bring `androidx.test:runner`
transitively, so `androidTestImplementation(libs.androidx.test.runner)` must stay. Without it
the test APK builds and installs, then dies at startup with `ClassNotFoundException` before a
single assertion runs — which reads as "no tests" rather than as a failure.


## Minification and the SQLCipher JNI field

SQLCipher's native library resolves `long mNativeHandle` on
`net.sqlcipher.database.SQLiteDatabase` **by name** from `JNI_OnLoad`. If R8 removes or
renames that field, `System.loadLibrary` throws `NoSuchFieldError` from inside `nativeLoad`,
and the process aborts before any application code can report it. `app/proguard-rules.pro`
keeps the SQLCipher packages for exactly this reason.

A build type that is minified without those rules will therefore abort on startup. That is
not hypothetical: the `pilot` build type did precisely this, because it was declared *before*
`release` and `initWith(release)` copies a build type as it stands at that moment — so it
inherited a release that had no `proguardFiles` yet. The build succeeded, the APK installed,
and it died opening the database.

Two things follow. Declaration order in `buildTypes` is load-bearing whenever `initWith` is
used, and a minified build must be launched before it is trusted: a successful
`assembleRelease` proves nothing about R8 output, because no unit or instrumentation test
executes it.

To confirm a variant actually received the rules:

```bash
grep -c "proguard-rules.pro" app/build/outputs/mapping/<variant>/configuration.txt
```

Zero means the variant is being minified with no keep rules at all.

## Client telemetry

The handset reports what it observed so a failure can be diagnosed from the dashboard rather
than from someone's logcat. It is the client half of the server's existing audit model: each
report becomes an `AuditLogEntry` with `Source = "Android"`, alongside the entries the server
writes for itself.

**What is sent.** An event type from a closed set, a severity, a status, a classification
code, a short summary built from fixed vocabulary, a duration, and the correlation id of the
request it describes.

**What is never sent.** There is deliberately no field for any of it: no password or PIN, no
access or refresh token, no `Authorization` header, no cookie, no request or response body, no
database key, no raw SMS, no customer phone number, and no exception message. Failures travel
as an `errorCode` from a fixed enum, so telemetry cannot become a way to move message contents
off the device. HTTP events record the method and a normalised route — `/api/v1/transactions/{id}`,
never the URL, because a query string can carry an email address and a path with an id in it
would also split one failing endpoint into one error group per transaction.

**Correlation.** The interceptor generates a correlation id when the caller has none and sends
it as `X-Correlation-Id`, the header the backend already reads. The server echoes it and stamps
its own audit entry with it, so one trace shows the handset's attempt and the server's answer.
Client events that describe a specific request carry that request's id rather than the id of the
batch that later delivered them.

**Delivery is best effort.** Recording happens off the caller's thread and every failure is
swallowed: an operation has already happened by the time it is described, and telemetry that
could fail a capture, a login or a sync would be worse than none. Uploads ride along with the
existing sync pass rather than scheduling work of their own, so diagnostics never wake a handset
by themselves. Events are deleted only after the server accepts them.

**The queue is bounded**: 500 events and seven days, whichever comes first, with the newest
kept. A handset out of signal for a week discards old diagnostics rather than filling its own
storage.

**Investigating an incident.** Find the failure on `/operations`, follow its correlation id to
`/operations/trace/{id}`, and read the handset's events and the server's on one timeline. Device
health shows app version, last seen and recent failures per device.
