# Player directory reporter

The reporter supplies account and character observations to the Praetoris bot
directory. It is disabled by default and independent of the existing status
heartbeat. It has no deletion or membership enforcement features.

## Configuration

In `warpalicious.ServerInfo.cfg`:

```ini
[PlayerDirectory]
Enabled = true
EndpointUrl = http://<isolated collector Tailscale IP>:18199/api/player-directory
ApiKey = <separate server-only test key>
IntervalSeconds = 30
```

Configure only Valdev during testing. Do not send test observations to the live
bot or expose the key in a client config. `Enabled` defaults to false. The
endpoint and key default to empty. Collection runs only on a game server.

## Behavior

- Read the platform account ID from the server peer's socket identity. Accept
  Steam and PlayFab identities. Do not use socket addresses or peer session IDs.
- Read the creator ID from `ZDOVars.s_playerID` on the character ZDO. This differs
  from the peer UID and the character object's ZDOID. If unavailable, report an
  empty value and collect it when the character data arrives.
- Observe accepted peer information, character assignment, and disconnects.
  Also inspect ready peers once per second. Record presence at the configured
  interval, with a minimum interval of five seconds.
- Coalesce pending records per account and creator ID. Keep the latest observed
  name and original observation timestamp. Last play is a recorded presence,
  not a claim that every moment of the session was captured.
- Save pending records before delivery. Use batches of at most 256 records.
  Retain a batch on failed HTTP delivery. Retry at the configured interval.
  Do not discard a newer observation when an older batch is acknowledged.
- Persist the queue at `BepInEx/config/ServerInfo/player-directory-pending.json`.
  The queue contains identities and timestamps, but no API key. An endpoint
  mismatch or damaged queue pauses collection to preserve the old data.
- A failed disk write pauses delivery. Logs report error types and HTTP status,
  without credentials, endpoint URLs, response bodies, or player identifiers.

The bot's matching endpoint is `/api/player-directory`. It needs a configured
admin channel and a dedicated directory API key. See `jneb802/praetoris-bot`,
branch `feature/player-directory`, and `docs/player-directory.md`.

## Local checks

```sh
dotnet build ServerInfo.csproj -c Release --nologo
dotnet run --project Tests/ReporterTests.csproj -c Release
```

The reporter tests run its source with simulated game and transport adapters.
They check disabled/client behavior, account ID validation, restart retries,
late creator IDs, acknowledgment during a newer observation, damaged queues,
endpoint isolation, and short-session capture. The harness uses .NET 8 or newer.
It is excluded from the production mod build.

These tests do not prove actual Unity serialization, Harmony hooks, or the game
connection flow. Valdev and Valnet validation remains pending. On 2026-10-04,
the clients still had another validation setup active despite no lease owners.
No candidate files were deployed and no game services were changed.

Before live proof, verify the current production release and installed mirror
content. Back up affected files in the maintained profiles. Use only an isolated
collector and test characters. Restore plugins/configs and remove the test queue
afterward. Confirm restoration before releasing the device leases.
