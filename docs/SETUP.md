# Setup

## Requirements

- Vintage Story server 1.22.x.
- Th3Essentials 2.17.1 or newer.
- Th3Essentials Discord integration already working.
- Discord bot invite with `applications.commands`.
- Discord Message Content intent enabled.
- Discord Server Members intent enabled if Th3Essentials rewards/auth is enabled.

## Install

1. Install Th3Essentials.
2. Configure Th3Essentials Discord.
3. Put `davesdiscordutilities_0.2.0.zip` in the server `Mods` folder.
4. Start the server once.
5. Stop the server.
6. Edit `ModConfig/DavesDiscordUtilitiesConfigs/DavesDiscordUtilitiesConfig.json`.
7. Start the server again.
8. Check Discord for `/request` and `/ddu repair`.

If commands do not show up, restart after Dave is online or run `!setupth3essentials`.

## Channels

Use this basic layout:

- `#server-info`: public rules and whitelist instructions.
- `#request-whitelist`: guests run `/request` here.
- `#requests`: staff-only review cards.
- normal channels: hidden until approval.

Dave posts review cards in `#requests`.

Applicant conversation happens in a private thread under `#request-whitelist`, unless `TemporaryThreadParentChannelId` points to another channel.

## Roles

Use this basic layout:

- default role: guest access.
- `Whitelisted`: normal member access.
- `Helper`: staff who can review requests.
- bot role: above `Whitelisted` and any role it manages.

## `#request-whitelist` Permissions

Default role:

- allow `View Channel`.
- allow `Use Application Commands`.
- allow `Send Messages in Threads`.
- deny `Send Messages` if you want the channel clean.

`Whitelisted`:

- deny `View Channel`.

`Helper`:

- allow `View Channel`.
- allow `Read Message History`.
- allow `Send Messages in Threads`.
- allow `Manage Threads`.

Bot:

- allow `View Channel`.
- allow `Send Messages`.
- allow `Manage Messages`.
- allow `Read Message History`.
- allow `Use Application Commands`.
- allow `Create Private Threads`.
- allow `Send Messages in Threads`.
- allow `Manage Threads`.

## `#requests` Permissions

Default role:

- deny `View Channel`.

`Whitelisted`:

- deny `View Channel`.

`Helper`:

- allow `View Channel`.
- allow `Read Message History`.
- allow `Send Messages`.

Bot:

- allow `View Channel`.
- allow `Read Message History`.
- allow `Send Messages`.

## Bot Permissions

The bot needs:

- Use Application Commands.
- Manage Roles.
- Manage Nicknames if `RenameApprovedMember` is `true`.
- Manage Messages if `DeleteRequestChannelMessages` is `true`.
- Create Private Threads.
- Send Messages in Threads.
- Manage Threads.
- View Channel.
- Send Messages.
- Read Message History.

The bot role must be above roles it manages and members it renames.

## Identity Checks

Dave can use Th3Essentials account links.

Identity modes:

- `Off`: no account link checks.
- `TrackWhenAvailable`: shows link status on review cards when Th3Essentials auth is enabled.
- `Strict`: approved players must link before the deadline, or Dave revokes the configured access.

Players link accounts with Th3Essentials:

1. Run `/auth mode:connect` in Discord.
2. Copy the command Th3Essentials gives them.
3. Run `/dcauth <token>` in-game.

Th3Essentials only enables this when `DiscordConfig.Rewards` is `true`.

After changing auth settings, restart the server and run `/ddu repair`.

## First Live Check

Before opening requests:

1. Use Discord "View Server As Role".
2. Confirm guests can see `#request-whitelist`.
3. Confirm guests cannot see `#requests`.
4. Confirm `Whitelisted` cannot see `#request-whitelist`.
5. Confirm helpers can see `#requests`.
6. Confirm the bot role is above `Whitelisted`.
