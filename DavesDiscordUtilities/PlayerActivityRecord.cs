using System;

namespace DavesDiscordUtilities;

public class PlayerActivityRecord
{
    public string PlayerUid = "";
    public string PlayerName = "";
    public string PlayerClassCode = "";
    public DateTime? LastJoinUtc;
    public DateTime? LastLeaveUtc;
    public bool IsOnline;
}
