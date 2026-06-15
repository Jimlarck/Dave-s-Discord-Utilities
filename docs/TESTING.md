# Testing

Run these on a test server before using the mod live.

## Startup

1. Start the server with Th3Essentials enabled.
2. Check the log for `DavesDiscordUtilities connected to Th3Essentials Discord client.`
3. Confirm Discord shows `/request`.
4. Confirm Discord shows `/ddu repair` and `/ddu lockdown`.

## New Request

1. Submit `/request` with a valid Vintage Story account that is not whitelisted.
2. Confirm the applicant gets a private response.
3. Confirm Dave creates a private applicant thread.
4. Confirm Dave posts a review card in `#requests`.
5. Click Add.
6. Confirm the player is on the Vintage Story whitelist.
7. Confirm the Discord member gets `ApprovedRoleId`.
8. Confirm the nickname changes if `RenameApprovedMember` is enabled.
9. Confirm the applicant no longer sees the archived private thread.

## Revoke

1. Click Revoke on an approved card.
2. Confirm the Vintage Story whitelist entry is removed.
3. Confirm the Discord role is removed.
4. Confirm the nickname is restored if Dave captured the old nickname.
5. Confirm the applicant thread reopens or is recreated.

## Already Whitelisted Player

1. Submit `/request` for a username already on the Vintage Story whitelist.
2. Confirm Dave posts a helper review card.
3. Click Add.
4. Confirm the Vintage Story whitelist entry is not changed.
5. Confirm Discord role, nickname, and thread state update.
6. Click Revoke.
7. Confirm the Vintage Story whitelist entry is still not changed.

## Repair

1. Delete or edit a review card.
2. Run `/ddu repair`.
3. Confirm Dave refreshes or reposts the card.

Large repair runs can take a few minutes because Dave paces Discord message edits.

## Ban And Unban

1. Click Ban on a card.
2. Confirm the card says the user cannot submit new whitelist requests.
3. Have that Discord user run `/request`.
4. Confirm Dave replies with the ban message.
5. Click Unban.
6. Confirm the user can submit `/request` again.

## Lockdown

1. Run `/ddu lockdown enabled:true`.
2. Submit a new `/request`.
3. Confirm Dave sends the lockdown message.
4. Confirm no card or thread is created.
5. Run `/ddu lockdown enabled:false`.
6. Confirm requests work again.

## Identity

Test this only if Th3Essentials rewards/auth is enabled.

1. Approve a test request.
2. Run `/auth mode:connect` in Discord.
3. Run the `/dcauth <token>` command in-game.
4. Run `/ddu repair`.
5. Confirm the card says `Identity: verified via Th3Essentials`.

For strict mode:

1. Set `IdentityVerificationMode` to `Strict`.
2. Set a short test value for `IdentityVerificationGraceHours`.
3. Approve an unlinked test request.
4. Wait for the deadline.
5. Confirm Dave revokes the configured access.

## Permissions

Use Discord "View Server As Role".

Check:

- guests can see `#request-whitelist`.
- guests cannot see `#requests`.
- guests cannot see normal member channels.
- guests cannot see other applicants' private threads.
- helpers can see `#requests`.
- helpers can open applicant threads.
- whitelisted members cannot see `#request-whitelist`.
