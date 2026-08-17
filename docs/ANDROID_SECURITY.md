# Android Security Model

What THE CARL protects on an Android device, how, and what it deliberately does not claim.

---

## What is stored where

| Data | Location | Protection |
|---|---|---|
| Access token | `thecarl.credentials` preferences | AES-256/GCM, key in Android Keystore |
| Refresh token | same | same |
| Device installation id | same | same |
| Database passphrase | `thecarl.database` preferences | 32 random bytes, wrapped by the same Keystore key |
| Evidence, transactions, outbox | `thecarl.db` (Room) | SQLCipher, key from above |
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

All seven executed on a `carl-test` AVD (Android 35, `google_apis`, `arm64-v8a`) and passed.
**SQLCipher encryption and the Keystore key handling are verified on a real Android runtime**,
not merely configured.

Note the dependency this needs: `androidx.test.ext:junit` does not bring `androidx.test:runner`
transitively, so `androidTestImplementation(libs.androidx.test.runner)` must stay. Without it
the test APK builds and installs, then dies at startup with `ClassNotFoundException` before a
single assertion runs — which reads as "no tests" rather than as a failure.
