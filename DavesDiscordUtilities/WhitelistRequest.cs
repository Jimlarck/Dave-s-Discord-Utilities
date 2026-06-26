using System;

namespace DavesDiscordUtilities;

public class WhitelistRequest
{
    public string Id = "";
    public ulong GuildId;
    public ulong DiscordUserId;
    public string DiscordName = "";
    public string PlayerName = "";
    public string PlayerUid = "";
    public string RequestText = "";
    public string? AppliedNickname;
    public string? PreviousDiscordNickname;
    public bool PreviousDiscordNicknameCaptured;
    public string? NicknameUpdateError;
    public DateTime? NicknameUpdatedAtUtc;
    public DateTime? ClassSelectionAllowedAtUtc;
    public ulong? ClassSelectionAllowedByDiscordUserId;
    public string? ClassSelectionAllowedByName;
    public string? PreviousPlayerName;
    public DateTime? LastUpdatedAtUtc;
    public DateTime? IdentityVerifiedAtUtc;
    public DateTime? IdentityVerificationDueAtUtc;
    public DateTime? IdentityVerificationExpiredAtUtc;
    public ulong? IdentityLinkedDiscordUserId;
    public bool ExistingWhitelistClaim;
    public DateTime? RejectedThreadOpenedAtUtc;
    public DateTime? ApplicantRespondedAtUtc;
    public ulong RequestChannelId;
    public ulong RequestMessageId;
    public ulong ReviewChannelId;
    public ulong ReviewThreadId;
    public ulong CommunicationThreadId;
    public string? CommunicationThreadError;
    public ulong ReviewMessageId;
    public string Status = DavesDiscordUtilitiesStatuses.Pending;
    public ulong? DecidedByDiscordUserId;
    public string? DecidedByName;
    public DateTime CreatedAtUtc = DateTime.UtcNow;
    public DateTime? DateAddedUtc;
    public DateTime? DecidedAtUtc;
}

public static class DavesDiscordUtilitiesStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Denied = "Denied";
    public const string Revoked = "Revoked";
    public const string Removed = "Removed";
    public const string Banned = "Banned";
    public const string Unbanned = "Unbanned";
}
