using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DavesDiscordUtilities;

public class DavesDiscordUtilitiesConfig
{
    public bool Enabled = true;
    public ulong RequestChannelId = 0;
    public ulong ReviewChannelId = 0;
    public List<ulong>? ApproverRoleIds = null;
    public ulong ApprovedRoleId = 0;
    public ulong PendingRoleId = 0;
    public ulong InstructionMessageId = 0;
    public int WhitelistYears = 50;
    public bool DeleteRequestChannelMessages = true;
    public bool UseTemporaryApplicantThreads = true;
    public ulong TemporaryThreadParentChannelId = 0;
    public bool DeleteTemporaryThreadOnApproval = false;
    public int RejectedThreadCleanupHours = 24;
    public bool RenameApprovedMember = true;
    public string ApprovedMemberNicknameFormat = "{playername}";
    public bool LockdownEnabled = false;
    public bool RequirePrivateReviewChannel = true;
    public string IdentityVerificationMode = DavesDiscordUtilitiesIdentityVerificationModes.TrackWhenAvailable;
    public int IdentityVerificationGraceHours = 24;
    public bool StrictVerificationRemovesGameWhitelist = true;
    public bool StrictVerificationRemovesDiscordRole = true;
    public bool BotStatusEnabled = true;
    public bool BotStatusShowPlayerCount = true;
    public bool BotStatusShowWorldDate = true;
    public int BotStatusUpdateSeconds = 120;
    public string BotStatusFormat = "{players} | {date}";
    public string BotStatusPlayerCountFormat = "players: {0}/{1}";
    public string BotStatusWorldDateFormat = "{0}, Year {1}";
    public string BotStatusFallbackDateText = "";
    public string ModUpdateTargetGameVersion = "";
    public ulong ModUpdateCardMessageId = 0;
    public List<ulong>? ModUpdateOverflowMessageIds = null;
    public List<string>? ModUpdateIgnoredModIds = null;
    public Dictionary<string, string>? ModUpdatePageOverrides = null;
    public string PromptMessage = "Welcome to our server! Use `/request` and fill in your in-game username and message. Someone will get to you soon! If you mistyped your username, retry the command with the correct username before approval.";
    public string UsageMessage = "Use `/request` in the whitelist request channel.";
    public string InvalidNameMessage = "That does not look like a valid Vintage Story username. Use `/request` and enter only your in-game username in the playername field.";
    public string UnknownPlayerMessage = "I could not find a Vintage Story account named {0}. Please check the spelling and try again.";
    public string UnsafeReviewChannelMessage = "Dave cannot submit this request because the staff review channel is not private. Please contact staff.";
    public string TemporaryThreadErrorMessage = "Dave cannot create your private request thread. Please contact staff.";
    public string RequestReceivedMessage = "Your whitelist request for {0} has been sent for review. If you mistyped your username, retry `/request` with the correct username before approval.";
    public string RequestUpdatedMessage = "Your pending whitelist request was updated to {0}.";
    public string ExistingWhitelistReviewMessage = "{0} is already whitelisted. Dave sent your account claim to staff for review.";
    public string ExistingWhitelistLinkedMessage = "{0} is already whitelisted. Dave has successfully updated your record.";
    public string LockdownMessage = "This server is currently not accepting new players. Please try again later.";
    public string RequestBanMessage = "I cannot do that, {0}";
    public string RejectedThreadNoResponseMessage = "If no response is received for 24 hours this thread will be closed.";
    public string ApprovedMessage = "Your whitelist request for {0} was approved. You can now join the server.";
    public string DeniedMessage = "Your whitelist request for {0} was denied.";
    public string RevokedMessage = "Your whitelist access for {0} was revoked.";
    public string RemovedMessage = "Your rejected whitelist thread for {0} was removed.";
    public string BannedMessage = "Your whitelist request for {0} was closed. You cannot submit another request.";
    public string UnbannedMessage = "Your whitelist request restriction for {0} was lifted. You can submit another request.";
    public string StrictVerificationApprovedMessage = "To keep your whitelist access, link your Discord and Vintage Story accounts with Th3Essentials within {0} hour(s), before {1}. Run `/auth mode:connect` in Discord, then run the `/dcauth ...` command in-game.";
    public string StrictVerificationExpiredMessage = "Your whitelist access for {0} was revoked because your Discord and Vintage Story accounts were not linked with Th3Essentials in time.";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, WhitelistRequest>? Requests = null;

    public bool ShouldSerializeRequests()
    {
        return Requests != null;
    }
}

public static class DavesDiscordUtilitiesIdentityVerificationModes
{
    public const string Off = "Off";
    public const string TrackWhenAvailable = "TrackWhenAvailable";
    public const string Strict = "Strict";
}
