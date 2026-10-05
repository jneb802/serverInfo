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

The queue and request use `DataContractJsonSerializer`, which is supplied by
the game's managed .NET runtime. The tests use this same serializer and check
that Unicode, quotes, backslashes, and a line break survive persistence/retry.
The game and transport adapters remain simulated.

## Live validation

On 2026-10-04 Pacific time (2026-10-05 UTC), Valdev and both Valnet clients
used the maintained Season 8 profiles, temporarily aligned with production's
installed mod DLLs and location assets. Production state reported 8.0.35.
Its staged manifest had newer dependencies than the running server, so the
installed hashes and live enforcement policy supplied the baseline.

The first live build produced empty observation JSON through Unity's serializer.
The collector rejected it with HTTP 422. Replacing that serializer with
`DataContractJsonSerializer` fixed the live failure. The corrected build loaded
the Harmony hooks and delivered real observations from both clients.

- Two fresh development characters connected and passed the server's mod checks.
- The collector stored two accounts and two creator IDs. Account IDs matched
  the clients' Steam identities. Creator IDs appeared in their character saves.
- Creator records became available after the characters loaded. Presence
  updates did not duplicate accounts or characters.
- The existing `!link` command completed against an isolated database and a
  synthetic Discord identity. No Discord bot connection was used.
- With the collector stopped, the reporter persisted both observations. After
  both clients logged out, a server restart loaded the same queue. Restoring
  the collector drained it and preserved the original observation timestamps.
- Existing shader and portal errors matched earlier server/client logs. The
  corrected reporter produced no unexpected warning. HTTP failures occurred
  during the deliberate outage.

Original plugin/configuration hashes and prior client profiles were restored.
Test additions and the queue were removed. Both clients were powered off.
Valdev was returned to its original running baseline. Production was inspected
only. No production deployment or Discord post occurred.

Evidence is stored locally at
`/Users/benjmarston/Develop/validation-evidence/player-directory-20261005`.
It contains private identities and test credentials and must not be committed.
Real Discord rendering and interaction delivery remain untested because no
separate test Discord bot/server is configured.

Before live proof, verify the current production release and installed mirror
content. Back up affected files in the maintained profiles. Use only an isolated
collector and test characters. Restore plugins/configs and remove the test queue
afterward. Confirm restoration before releasing the device leases.
