# Broker settings in the configuration registry

The MQTT broker the gateway publishes to is stored in the configuration service's `Brokers` table and can be changed from the API or the demo console's **Broker** tab, without editing files or restarting the gateway. A change that does not work is rolled back automatically.

## What lives where

| Setting | Where | Why |
|---|---|---|
| Host, port, TLS, certificate location and fingerprint | `Brokers` table | Shared by every gateway on that broker; changed centrally |
| Keep-alive, connect timeout, retry delays | `Brokers` table | How to talk to that broker |
| Which broker to use: `Mqtt:BrokerId` (default `local`) | gateway `appsettings.json` | The gateway needs it before it can read the registry |
| Client ID, Sparkplug group/node/host IDs | gateway `appsettings.json` | The gateway's identity; changing it remotely would make it appear as a different node |
| `Mqtt:Host`, `Port`, `UseTls`, `BrokerCertificatePath`, timing keys | gateway `appsettings.json` | First-boot seed and last resort only |
| `Mqtt:BrokerTrialSeconds` (default 60) | gateway `appsettings.json` | How long new settings get before the last working ones are restored |

No passwords or keys are stored. Unknown fields such as `password` are refused by the API.

## Table and API

```
Brokers (Id PK, Host, Port CHECK 1–65535, UseTls, CertificateLocation NULL, CertificateSha256 NULL,
         KeepAliveSeconds, ConnectTimeoutSeconds, RetryDelaySeconds, MaxRetryDelaySeconds)
```

The `AddBrokers` migration creates the table and seeds `local`: `127.0.0.1:8883`, TLS, `certs/broker.crt`, keep-alive 60 s, connect timeout 10 s, retry 2–30 s. These are the values gateways used from `appsettings.json` before.

| Endpoint | Result |
|---|---|
| `GET /brokers`, `GET /brokers/{id}` | list, one |
| `POST /brokers` | create; 409 if the ID exists |
| `PUT /brokers/{id}` | update; 400 if invalid, 404 if missing |
| `DELETE /brokers/{id}` | remove; 409 for the only broker |
| `GET /config?broker={id}` | devices plus that broker in one consistent snapshot; 404 for an unknown broker. Without `broker` the response is unchanged. |

Every change bumps the configuration version, so gateways pick it up on their next refresh (every 10 s).

Validation (also re-checked by the gateway):
- ID without spaces or `/ + #`; host required, without spaces; port 1–65535
- keep-alive 5–3600 s; connect timeout 1–300 s; retry delay 1–3600 s, and maximum ≥ retry delay
- TLS needs a certificate location; no TLS means no certificate fields
- a certificate URL must be `https` and must have a fingerprint; a fingerprint is 64 hex characters

## Certificate

`CertificateLocation` is either a file path on the gateway box (relative paths resolve next to the gateway executable) or an `https://` URL.

`CertificateSha256` pins the certificate: the SHA-256 of the certificate itself. A certificate with another fingerprint is refused. Get it with:

```bash
openssl x509 -in broker/certs/broker.crt -noout -fingerprint -sha256
```

and remove the colons (the console form removes them for you).

- **File path:** read on every connection; the fingerprint is checked if set.
- **URL:** downloaded once and cached as `state/gateway/certs/<fingerprint>.crt`. Later connections use the cached copy, so the URL does not have to stay reachable.
- The certificate's expiry is reported; the console warns 30 days ahead.

## How the gateway chooses and switches

Which settings a connection attempt uses:
1. the registry's broker (named by `Mqtt:BrokerId`)
2. else the **last working** settings: `state/gateway/broker-last-good.json`, written only after a connection published its NBIRTH
3. else the appsettings seed

When the registry's settings change while connected, the gateway ends the session (NDEATH, intentional disconnect, not recorded as an outage) and connects with the new settings at once. Readings keep going into the outbox meanwhile.

- **They connect:** they become the last working settings.
- **They do not connect within the trial window (60 s):** the gateway tries the last working settings. If those connect, the new settings are **rejected** with the connection error, and not tried again until the registry changes. Saving the same values again later earns a fresh trial.
- **Neither connects** (for example, the broker itself is down): both are tried in turn and the first to connect wins. An outage at the moment of a change never rejects good settings.

Failed trial attempts are a real outage (no broker is receiving data), so they appear in `BrokerHistory` like any other.

Other cases:

| Case | Behaviour |
|---|---|
| Configuration service down at startup | last working settings, else the seed (from the cached snapshot if it has a broker) |
| Broker removed or renamed in the registry | devices still update; the gateway keeps the settings it is using |
| Invalid settings reach the gateway | ignored and logged; the current settings stay |
| Certificate missing, unreadable or wrong fingerprint | that attempt fails like a connection failure, so the trial and rollback apply |
| Gateway crash during a trial | restart uses the last working settings and tries the registry's again |

## Visibility

`status.json` (and the console's Broker tab and gateway card) reports:
- `broker.active`: the settings of the last connection that got online, and `source`: `registry`, `last-good` or `appsettings`
- `broker.trying` / `tryingSince`: new settings on trial
- `broker.rejected` / `rejectedReason`: settings rolled back from, with the error
- `broker.certificateExpires`

The console's own MQTT monitor follows the broker the gateway is online with, so its view stays correct through a switch or rollback.

## Security

Anyone who can write to the configuration service can point gateways at another broker, so its write endpoints must require authentication before it is reachable beyond localhost. Today it listens on `127.0.0.1` only. A certificate fingerprint limits the damage to brokers whose certificate an attacker can also register.

## Try it in the demo

1. **Broker** tab → **Edit** `local` → set port `8884` → **Save**. Within about 10 s the gateway shows *trying 127.0.0.1:8884*; the gateway card shows it was last connected to 8883.
2. About 60–80 s later it shows *rejected* with the connection error and runs on 8883 again (*last working settings*). The registry row is marked *gateway still on earlier values*.
3. Set the port back to `8883` and save: no reconnect is needed, and the rejection clears.
