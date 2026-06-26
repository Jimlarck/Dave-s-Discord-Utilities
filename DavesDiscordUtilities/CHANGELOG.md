# Changelog

## 0.4.0

- Added `/ddu check-mod-updates` for staff to refresh a plain text ModDB update card in the review channel.
- Added update-card overflow messages so large update lists stay under Discord message limits.
- Added optional mod update config for target game version, ignored mod IDs, and ModDB page overrides.
- Added a review-card Manage button for secondary actions, including confirmed delete cleanup and one-time character selection.
- Ban and Delete review actions now require in-card confirmation before they run.
- Fixed prerelease update ordering so versions like `dev.12` sort after `dev.9`.
- Review button responses now edit the deferred ephemeral response instead of creating extra follow-up messages.

## 0.3.0

- Added an `Allow .charsel` button to approved review cards so helpers can allow one character selection from Discord.
- Review cards now show the most recent helper who allowed class selection for a player.

## 0.2.0

- Added identity verification tracking through Th3Essentials' existing `/auth` and `/dcauth` linked-account system.
- Added `IdentityVerificationMode` with `Off`, `TrackWhenAvailable`, and `Strict` modes.
- Added strict identity verification deadlines that can revoke configured access when an approved player does not link in time.
- Review cards use a compact staff-focused layout and include the player's last known character class for internal tracking. Currently looking at a few ideas to extend the use of class-tracking (flair-roles for people to ping specific classes living in specific settlement hubs, class-based /starterkit rewards, are a few examples of ideas I'm mulling over)
- Review cards show identity verification only when Th3Essentials auth is enabled or strict mode is active.
- `/ddu repair` paces review-card edits to avoid noisy Discord message-edit rate limit warnings during mass repair.

## 0.1.0

- Initial Testing Build (Whitelist-only manager with a calendar status)
