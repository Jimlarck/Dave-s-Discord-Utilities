# Config

Dave creates this file on first server start:

```text
ModConfig/DavesDiscordUtilitiesConfigs/DavesDiscordUtilitiesConfig.json
```

Stop the server before editing it.

## Sample

Replace the fake IDs with real Discord channel and role IDs.

```json
{
  "Enabled": true,
  "RequestChannelId": 111111111111111111,
  "ReviewChannelId": 222222222222222222,
  "ApproverRoleIds": [333333333333333333],
  "ApprovedRoleId": 444444444444444444,
  "PendingRoleId": 0,
  "InstructionMessageId": 0,
  "WhitelistYears": 50,
  "DeleteRequestChannelMessages": true,
  "UseTemporaryApplicantThreads": true,
  "TemporaryThreadParentChannelId": 0,
  "DeleteTemporaryThreadOnApproval": false,
  "RejectedThreadCleanupHours": 24,
  "RenameApprovedMember": true,
  "ApprovedMemberNicknameFormat": "{playername}",
  "LockdownEnabled": false,
  "RequirePrivateReviewChannel": true,
  "IdentityVerificationMode": "TrackWhenAvailable",
  "IdentityVerificationGraceHours": 24,
  "StrictVerificationRemovesGameWhitelist": true,
  "StrictVerificationRemovesDiscordRole": true,
  "BotStatusEnabled": true,
  "BotStatusShowPlayerCount": true,
  "BotStatusShowWorldDate": true,
  "BotStatusUpdateSeconds": 120,
  "BotStatusFormat": "{players} | {date}",
  "BotStatusPlayerCountFormat": "players: {0}/{1}",
  "BotStatusWorldDateFormat": "{0}, Year {1}",
  "BotStatusFallbackDateText": "",
  "ModUpdateTargetGameVersion": "",
  "ModUpdateCardMessageId": 0,
  "ModUpdateOverflowMessageIds": null,
  "ModUpdateIgnoredModIds": null,
  "ModUpdatePageOverrides": null,
  "LockdownMessage": "This server is currently not accepting new players. Please try again later.",
  "RequestBanMessage": "I cannot do that, {0}",
  "RejectedThreadNoResponseMessage": "If no response is received for 24 hours this thread will be closed.",
  "StrictVerificationApprovedMessage": "To keep your whitelist access, link your Discord and Vintage Story accounts with Th3Essentials within {0} hour(s), before {1}. Run `/auth mode:connect` in Discord, then run the `/dcauth ...` command in-game.",
  "StrictVerificationExpiredMessage": "Your whitelist access for {0} was revoked because your Discord and Vintage Story accounts were not linked with Th3Essentials in time."
}
```

Keep `ApproverRoleIds` as an array, even with one role.

## Required Values

- `Enabled`: turns this mod on or off. 
- `RequestChannelId`: channel where players run `/request`.
- `ReviewChannelId`: staff-only channel for review cards.
- `ApproverRoleIds`: helper role IDs that can use review buttons.
- `ApprovedRoleId`: role granted after approval. Use `0` to skip Discord role changes.

## Request Settings

- `PendingRoleId`: optional guest role to remove after approval.
- `InstructionMessageId`: saved by Dave after posting instructions.
- `WhitelistYears`: number of years added to the Vintage Story whitelist.
- `DeleteRequestChannelMessages`: deletes normal messages in the request channel.
- `UseTemporaryApplicantThreads`: creates a private thread for each applicant.
- `TemporaryThreadParentChannelId`: optional parent channel for applicant threads. Use `0` to use `RequestChannelId`.
- `DeleteTemporaryThreadOnApproval`: deletes the applicant thread after approval. `false` archives and locks it.
- `RejectedThreadCleanupHours`: hours before Dave deletes a rejected or revoked thread with no applicant reply.
- `LockdownEnabled`: blocks new requests without creating cards or threads.
- `RequirePrivateReviewChannel`: refuses to post cards if the review channel is not private.

## Role And Nickname Settings

- `RenameApprovedMember`: renames approved Discord members.
- `ApprovedMemberNicknameFormat`: nickname format. Supports `{playername}`, `{discord}`, and `{discordid}`.

Discord nicknames can only be 32 characters.

## Identity Settings

- `IdentityVerificationMode`: use `Off`, `TrackWhenAvailable`, or `Strict`.
- `IdentityVerificationGraceHours`: hours an approved strict-mode player has to link accounts.
- `StrictVerificationRemovesGameWhitelist`: removes the Vintage Story whitelist entry when strict verification expires.
- `StrictVerificationRemovesDiscordRole`: removes `ApprovedRoleId` when strict verification expires.

`TrackWhenAvailable` shows account link status when Th3Essentials auth is enabled.

`Strict` requires Th3Essentials auth and can revoke access if the player does not link in time.

## Bot Status Settings

- `BotStatusEnabled`: lets Dave set Discord activity text.
- `BotStatusShowPlayerCount`: includes current players.
- `BotStatusShowWorldDate`: includes the in-game month and year.
- `BotStatusUpdateSeconds`: slow refresh interval. Values below 60 are treated as 60.
- `BotStatusFormat`: activity format with `{players}` and `{date}`.
- `BotStatusPlayerCountFormat`: player count format. `{0}` is online players and `{1}` is max players.
- `BotStatusWorldDateFormat`: world date format. `{0}` is month and `{1}` is year.
- `BotStatusFallbackDateText`: optional text used before Dave has a saved world date.

Example status:

```text
players: 4/16 | Oct, Year 1
```

## Mod Update Settings

- `ModUpdateTargetGameVersion`: optional Vintage Story version to check against. Leave empty to use the running server API version.
- `ModUpdateCardMessageId`: saved by Dave after posting the update card.
- `ModUpdateOverflowMessageIds`: saved by Dave when the update list needs more than one Discord message.
- `ModUpdateIgnoredModIds`: optional mod IDs to skip during update checks.
- `ModUpdatePageOverrides`: optional map from installed mod ID to a ModDB page slug, asset ID, or full URL.

Example page override:

```json
{
  "ModUpdatePageOverrides": {
    "th3essentials": "theessentials"
  }
}
```

Run `/ddu check-mod-updates` from Discord to refresh the card in `ReviewChannelId`.

## Message Settings

These can be changed to match your server:

- `PromptMessage`
- `UsageMessage`
- `InvalidNameMessage`
- `UnknownPlayerMessage`
- `UnsafeReviewChannelMessage`
- `TemporaryThreadErrorMessage`
- `RequestReceivedMessage`
- `RequestUpdatedMessage`
- `ExistingWhitelistReviewMessage`
- `ExistingWhitelistLinkedMessage`
- `LockdownMessage`
- `RequestBanMessage`
- `RejectedThreadNoResponseMessage`
- `ApprovedMessage`
- `DeniedMessage`
- `RevokedMessage`
- `RemovedMessage`
- `BannedMessage`
- `UnbannedMessage`
- `StrictVerificationApprovedMessage`
- `StrictVerificationExpiredMessage`

## State Files

Dave writes these files in the same config folder:

- `DavesDiscordUtilitiesRequests.json`: request history and review card state.
- `DavesDiscordUtilitiesPlayerActivity.json`: join and leave times.
- `DavesDiscordUtilitiesBotStatus.json`: saved world date for bot status.

Do not edit state files while the server is running.
