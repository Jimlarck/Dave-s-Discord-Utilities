# Dave's Discord Utilities

Server-side Vintage Story mod for Th3Essentials.

It adds:

- `/request` for Discord whitelist requests, allowing a discord-only whitelisting process.
- staff review cards with buttons(add, revoke, ban from requesting).
- private applicant threads (which are removed once application is closed to avoid cluttering the channel list).
- Discord role and nickname updates.
- optional Th3Essentials identity checks to link a player's discord and vintagestory account.
- strict identity mode that can revoke access if accounts are not linked in time.
- optional Dave bot status text.
- `/ddu check-mod-updates` for a staff-only ModDB update card in the review channel.

## Requirements

- Vintage Story server 1.22.x.
- Th3Essentials 2.17.1 or newer.
- A Discord bot already working through Th3Essentials.
- Discord bot invite with the `applications.commands` scope.
- Discord Message Content intent enabled for the bot application.
- Discord Server Members intent enabled if Th3Essentials `DiscordConfig.Rewards` is `true`.

## Install

1. Install and configure Th3Essentials first.
2. Put `davesdiscordutilities_0.4.0.zip` in the server `Mods` folder.
3. Start the server once.
4. Stop the server.
5. Edit `ModConfig/DavesDiscordUtilitiesConfigs/DavesDiscordUtilitiesConfig.json`.
6. Start the server again.
7. Check Discord for `/request` and `/ddu repair`.

If commands do not show up, restart after Dave is online or run `!setupth3essentials`.

## Basic Discord Setup

Use these channels:

- `#request-whitelist`: guests run `/request` here.
- `#requests`: staff review cards go here.
- normal community channels: only visible after approval.

Use these roles:

- guest/default role: can only see public info and `#request-whitelist`.
- `Whitelisted`: normal member access.
- `Helper`: can review requests.
- bot role: above every role it grants or removes.

Keep `#requests` staff-only. The mod has a privacy check and will refuse to post cards if guests or normal members can see the review channel.

## Main Config Values

Set these before opening requests:

- `RequestChannelId`: channel ID for `#request-whitelist`.
- `ReviewChannelId`: channel ID for `#requests`.
- `ApproverRoleIds`: helper role IDs as a JSON array.
- `ApprovedRoleId`: role Dave grants after approval.
- `IdentityVerificationMode`: use `Off`, `TrackWhenAvailable`, or `Strict`.

See [docs/CONFIG.md](docs/CONFIG.md) for a full sample config.

## Identity Modes

- `Off`: Dave does not check Th3Essentials account links.
- `TrackWhenAvailable`: Dave shows link status when Th3Essentials auth is enabled. 
- `Strict`: approved players must link their Discord and Vintage Story accounts before the deadline, or Dave revokes the configured access. By default, this deadline is 24 hours. 

## Test Before Going Live

Run the short checklist in [docs/TESTING.md](docs/TESTING.md).

At minimum, test:

- a normal new whitelist request.
- approval.
- revoke.
- an already-whitelisted username.
- `/ddu repair`.
- request ban and unban.
- lockdown on and off.
- strict identity mode if you plan to use it.
- Discord channel visibility with "View Server As Role".

## Build

Run this from the repository root:

```powershell
.\scripts\Build-Release.ps1
```

If the script cannot find Vintage Story:

```powershell
.\scripts\Build-Release.ps1 -VintageStoryPath "C:\Path\To\Vintagestory"
```

To build and copy the zip to a test server:

```powershell
.\scripts\Build-Release.ps1 -Install -InstallTo "C:\Path\To\Server\Mods"
```

The generated archive is written to `Releases\davesdiscordutilities_0.4.0.zip`.

## Source Notes

- Source code lives in `DavesDiscordUtilities/`.
- Release packaging lives in `scripts/Build-Release.ps1`.
- Release notes live in `DavesDiscordUtilities/CHANGELOG.md`.
