# Player dashboard character restoration

This companion serves the Praetoris bot's private character-backup controls.
It is disabled by default. Enable `[PlayerDashboard] EnableCharacterRestore = true`
only after validation on Valdev. The bot must share this server's Enforcer folder.

The moderator asks the player to log out, selects a backup, and confirms it.
ServerInfo consumes a local request under
`BepInEx/config/ValheimEnforcer/DashboardRestores/<reference>/`.
No client RPC or HTTP route can request a restore.

On the main thread, the coordinator checks the active world and rejects connected
accounts or unidentified connections. It requires Enforcer file storage and the
`FlushCharacter` and `Invalidate` methods. Missing compatibility fails closed.
It gives pending character writes at most 250 ms to finish. It then checks the
current file hash, replaces the file, preserves `before.yaml`, invalidates both
numeric and Steam-prefixed cache keys, and verifies the resulting bytes. Login
callbacks cannot interleave with that transaction. The player can reconnect after
the bot receives success. Other players remain connected.

Only `saveSequence` differs from the selected YAML. It advances beyond the current
and backup values so an older sealed client recovery snapshot cannot undo the
restore. World and separate map files are unchanged.

Requests expire after ten seconds and are claimed once. A crash after claiming
does not replay them. Inspect `request.json`, `before.yaml`, `processing`, and
`result.json` when the bot reports an unknown outcome. A replacement may have
completed before an error or crash; do not blindly repeat it. Keep the operation
folder private to the bot and game service account. Recovery folders require an
operator retention policy.

Validation:

```sh
dotnet build -c Release
dotnet run --project RestoreTests/RestoreFiles.Tests.csproj
```

The transaction suite uses an isolated directory and injected runtime checks.
It does not start Valheim. A real Valdev development-character archive also passed
the bot-to-companion file contract locally, including preserved old bytes and
sequence advancement. Live logout/restore/reconnect testing remains required.
Valdev and both Valnet clients were leased by an Endgame validation session during
this implementation. No production deployment or server file replacement occurred.
