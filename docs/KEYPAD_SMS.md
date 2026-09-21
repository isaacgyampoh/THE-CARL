# Zazi for keypad phones (SMS)

Most Ghanaian MoMo agents work on a keypad phone with no internet. Zazi reaches them by SMS,
which works on any phone, anywhere, with no data. The agent never needs the app; the owner sees
everything in the portal.

## What an agent does

| Text to Zazi's number | What happens |
|---|---|
| `ZAZI <activation code>` | Links this phone to the agent. Once. |
| *(forward any MoMo confirmation)* | Recorded automatically — amount, customer number, MTN transaction ID and time read from the network's own message. |
| `CO 50 0244123456` | Cash out recorded by hand. |
| `CI 200 0201234567` | Cash in recorded by hand. Add `MTN`, `TELECEL` or `AT` at the end to name the network. |
| `FIND 0244123456` | The customer's last five transactions with times — for a complaint at the counter. |
| `TODAY` | Today's totals and what the agent is holding. |
| `CLOSE 1200 3500` | Closes the day: cash counted, then float on all SIMs. The reply says OK, SHORT or OVER against everything recorded since the last close. The owner sees it on the Closing page. |
| `FLOAT 500 MTN` | Asks the owner for float. The owner is texted at once and answers by SMS or in the portal. |
| `COMM` | This month's commission, by network. |
| `LANG TWI` | Replies in Twi (or `LANG GA`, `LANG EWE`, `LANG EN`) — only languages the service has switched on. |
| `HELP` | The list above. |

Every message gets a reply. Agents who traded get a summary each evening at 20:00, and if they
have not closed the day it ends with a reminder to send `CLOSE cash float`.

## What the owner gets

- **Low float warnings.** Set a level per network on the Cash & float page. When an agent's
  float on that network falls below it, the reply to their next transaction says so ("Low MTN
  float: GHS 180.00. Top up soon.", or "Very low" below half the level). No extra SMS is sent.
- **A text when someone closes short.** If an agent's count is short by a cedi or more, the
  business's phone number (the one given at sign-up) gets an SMS straight away, with the cash
  and float differences. The Closing page and the Today page show it too.

- **Float requests, answered from any phone.** An agent's `FLOAT 500 MTN` texts the owner:
  "Kofi asks for GHS 500.00 MTN float. Reply OK 4821 to give it, or NO 4821." Replying from the
  business's SMS number (set on the Settings page) records the float against the agent and
  tells them. The portal's Cash & float page lists waiting requests with Give / Decline.
- **Customer receipts (optional).** Switched on in Settings, each cash in and cash out is
  followed by an SMS to the customer: amount, network, time, a reference and the business's
  number. Off by default, because each receipt is an SMS the business pays for.

## Languages

English is complete. Twi is a first draft; Ga and Ewe are not written yet and fall back to
English line by line. A language is offered to agents only when it is listed in
`Sms__Languages` on the API service (e.g. `EN,TWI`). Before adding one:

1. Have a native speaker read every line in `src/Zazi.Infrastructure/Keypad/KeypadText.cs`
   for that language, and fill any gaps.
2. Keep it plain ASCII (write ɛ as e and ɔ as o), so replies stay one GSM SMS.
3. Add it to `Sms__Languages` and redeploy.

## Rules that keep it safe

- Only a number linked with a valid, single-use activation code can record anything.
- Revoking the phone on the Team page, or disabling the worker, stops it at the next text.
- The same MoMo message forwarded twice is recorded once; a gateway re-delivering a typed
  command is recorded once.
- A transaction that reaches Zazi by two routes — forwarded from the keypad phone and read by
  the app on another handset — is recorded once. The network's transaction ID, direction and
  amount identify it across routes.
- Everything recorded from the keypad phone also appears in the agent's app (marked "via
  keypad phone") and in the app's totals, whenever the app has a connection.
- The gateway's callback URL must carry a secret; without it every request is refused.
- The network is read from the message itself first, so a Telecel message forwarded from an
  MTN-numbered phone is filed under Telecel. Only when the message names no network is the
  agent SIM's own network used — and every reply says which network was used.
- Replies are plain GSM text, so each is one SMS rather than a costlier Unicode one.

## Going live — checklist

1. **Open an account** with an SMS provider that offers **two-way (inbound) SMS on a Ghana
   number**. Zazi supports Africa's Talking today; the sender is one small class, so another
   provider can be added.
2. **Get a number agents can text.** Ask the provider for a two-way long code or shortcode.
3. **Set these on the API service** (Render → zazi-api → Environment):

   | Variable | Value |
   |---|---|
   | `Sms__Provider` | `AfricasTalking` |
   | `Sms__Username` | your Africa's Talking username (`sandbox` while testing) |
   | `AFRICASTALKING_API_KEY` | your API key — **only here, never in code or chat** |
   | `Sms__SenderId` | the sender name or shortcode, if the provider assigned one |
   | `Sms__InboundSecret` | a long random string you generate |

4. **Set on the web service** (so owners see the number next to activation codes):

   | Variable | Value |
   |---|---|
   | `Sms__InboundNumber` | the number agents text, e.g. `020 000 0000` |

5. **Point the provider's inbound SMS callback at:**

   ```
   https://api.getzazi.com/api/v1/sms-gateway/inbound?key=<your Sms__InboundSecret>
   ```

6. **Test:** issue an activation code on the Team page, text `ZAZI <code>` from a keypad phone,
   then forward a MoMo message. Both should get a reply, and the transaction should appear on
   the Transactions page.

## Trying it without a gateway

In development the API has a simulator:

```
POST /api/v1/sms-gateway/simulate
{ "from": "0244000111", "text": "ZAZI ABCD-..." }
```

It returns the reply that would have been texted. It does not exist outside development.
