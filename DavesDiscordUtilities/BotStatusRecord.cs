using System;

namespace DavesDiscordUtilities;

public class BotStatusRecord
{
    public string WorldKey = "";
    public string MonthName = "";
    public int YearNumber;
    public double TotalDays;
    public DateTime UpdatedAtUtc = DateTime.UtcNow;
}
