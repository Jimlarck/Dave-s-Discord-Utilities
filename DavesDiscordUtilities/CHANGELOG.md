# Changelog

## 0.2.0

- Added identity verification tracking through Th3Essentials' existing `/auth` and `/dcauth` linked-account system.
- Added `IdentityVerificationMode` with `Off`, `TrackWhenAvailable`, and `Strict` modes.
- Added strict identity verification deadlines that can revoke configured access when an approved player does not link in time.
- Review cards use a compact staff-focused layout and include the player's last known character class for internal tracking. Currently looking at a few ideas to extend the use of class-tracking (flair-roles for people to ping specific classes living in specific settlement hubs, class-based /starterkit rewards, are a few examples of ideas I'm mulling over)
- Review cards show identity verification only when Th3Essentials auth is enabled or strict mode is active.
- `/ddu repair` paces review-card edits to avoid noisy Discord message-edit rate limit warnings during mass repair.

## 0.1.0

- Initial Testing Build (Whitelist-only manager with a calendar status)
