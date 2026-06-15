using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace DavesDiscordUtilities;

public class DavesDiscordUtilitiesModSystem : ModSystem
{
    private const string ConfigFolder = "DavesDiscordUtilitiesConfigs";
    private const string ConfigFile = ConfigFolder + "/DavesDiscordUtilitiesConfig.json";
    private const string RequestsFile = ConfigFolder + "/DavesDiscordUtilitiesRequests.json";
    private const string ActivityFile = ConfigFolder + "/DavesDiscordUtilitiesPlayerActivity.json";
    private const string BotStatusFile = ConfigFolder + "/DavesDiscordUtilitiesBotStatus.json";
    private const string CommandName = "request";
    private const string AdminCommandName = "ddu";
    private const string RepairSubcommandName = "repair";
    private const string LockdownSubcommandName = "lockdown";
    private const string LockdownEnabledOption = "enabled";
    private const string PlayerNameOption = "playername";
    private const string MessageOption = "message";
    private const string ButtonPrefix = "davesdiscordutilities";
    private const string AddAction = "add";
    private const string RevokeAction = "revoke";
    private const string RemoveAction = "remove";
    private const string BanAction = "ban";
    private const string UnbanAction = "unban";
    private const string CharacterClassAttribute = "characterClass";
    private const int RequestChannelNoticeCooldownSeconds = 60;
    private const int RejectedThreadCleanupTickMs = 5 * 60 * 1000;
    private const int ReviewCardRepairDelayMs = 1250;
    private static readonly HttpClient HttpClient = new();
    private static readonly string[] MonthNames =
    {
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
    };
    private static readonly Emoji ApproveEmoji = new("\u2705");
    private static readonly Emoji DenyEmoji = new("\u274C");
    private static readonly Regex PlayerNamePattern = new("^[A-Za-z0-9_]{2,32}$", RegexOptions.Compiled);

    private ICoreServerAPI _sapi = null!;
    private DavesDiscordUtilitiesConfig _config = null!;
    private Dictionary<string, PlayerActivityRecord> _activity = new();
    private Dictionary<string, WhitelistRequest> _requests = new();
    private BotStatusRecord _botStatusRecord = new();
    private object? _th3Essentials;
    private Th3DiscordBridge? _discord;
    private EventInfo? _th3DiscordReadyEvent;
    private Delegate? _th3DiscordReadyHandler;
    private Delegate? _createSlashCommandHandler;
    private Delegate? _createDduSlashCommandHandler;
    private long _botStatusTickListenerId;
    private long _rejectedThreadCleanupTickListenerId;
    private int _botStatusUpdateQueued;
    private int _rejectedThreadCleanupQueued;
    private string _lastBotStatusText = "";
    private bool _botStatusCanUseLiveCalendar;
    private readonly Dictionary<ulong, DateTime> _requestChannelNoticeSentAt = new();
    private readonly object _requestChannelNoticeLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestDecisionLocks = new();

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        _sapi = sapi;
        LoadConfig();
        LoadPlayerActivity();
        LoadBotStatusRecord();
        _sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        _sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

        _th3Essentials = FindTh3Essentials(sapi);
        if (_th3Essentials == null)
        {
            sapi.Logger.Error("DavesDiscordUtilities requires Th3Essentials.");
            return;
        }

        SubscribeTh3DiscordReady(_th3Essentials);

        var th3Discord = _th3Essentials.GetType().GetProperty("Th3Discord", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_th3Essentials);
        if (th3Discord != null)
        {
            RegisterDiscord(th3Discord);
        }
    }

    private void LoadConfig()
    {
        EnsureConfigFolder();

        var config = _sapi.LoadModConfig<DavesDiscordUtilitiesConfig>(ConfigFile);
        if (config == null)
        {
            _config = new DavesDiscordUtilitiesConfig();
            SaveConfig();
            _sapi.Logger.Warning($"DavesDiscordUtilities created {ConfigFile}. Set RequestChannelId, ReviewChannelId, ApproverRoleIds, and ApprovedRoleId before opening applications.");
        }
        else
        {
            _config = config;
        }

        LoadRequests();
    }

    private void LoadRequests()
    {
        _requests = _sapi.LoadModConfig<Dictionary<string, WhitelistRequest>>(RequestsFile) ?? new Dictionary<string, WhitelistRequest>();

        if (_config.Requests is not { Count: > 0 } legacyRequests) return;

        var migrated = 0;
        foreach (var legacyRequest in legacyRequests)
        {
            if (_requests.ContainsKey(legacyRequest.Key)) continue;

            _requests[legacyRequest.Key] = legacyRequest.Value;
            migrated++;
        }

        _config.Requests = null;
        SaveRequests();
        SaveConfig();

        _sapi.Logger.Notification($"DavesDiscordUtilities migrated {migrated} request record(s) from {ConfigFile} to {RequestsFile}.");
    }

    private void LoadPlayerActivity()
    {
        _activity = _sapi.LoadModConfig<Dictionary<string, PlayerActivityRecord>>(ActivityFile) ?? new Dictionary<string, PlayerActivityRecord>();

        foreach (var record in _activity.Values)
        {
            record.IsOnline = false;
        }

        var onlinePlayers = _sapi.World?.AllOnlinePlayers?.OfType<IServerPlayer>().ToList() ?? new List<IServerPlayer>();
        _botStatusCanUseLiveCalendar = onlinePlayers.Count > 0;

        foreach (var player in onlinePlayers)
        {
            TrackPlayerActivity(player, online: true, save: false);
        }

        SavePlayerActivity();
    }

    private void LoadBotStatusRecord()
    {
        _botStatusRecord = _sapi.LoadModConfig<BotStatusRecord>(BotStatusFile) ?? new BotStatusRecord();
    }

    private void EnsureConfigFolder()
    {
        Directory.CreateDirectory(Path.Combine(_sapi.GetOrCreateDataPath("ModConfig"), ConfigFolder));
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        _botStatusCanUseLiveCalendar = true;
        TrackPlayerActivity(player, online: true);
        QueueBotStatusUpdate(delayMs: 1500, force: true);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        if (player.ConnectionState == EnumClientState.Connecting)
        {
            QueueBotStatusUpdate(delayMs: 1500, force: true);
            return;
        }

        TrackPlayerActivity(player, online: false);
        QueueBotStatusUpdate(delayMs: 1500, force: true);
    }

    private void TrackPlayerActivity(IServerPlayer player, bool online, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        if (!_activity.TryGetValue(player.PlayerUID, out var record))
        {
            record = new PlayerActivityRecord();
            _activity[player.PlayerUID] = record;
        }

        record.PlayerUid = player.PlayerUID;
        record.PlayerName = player.PlayerName ?? record.PlayerName;
        record.PlayerClassCode = ReadPlayerClassCode(player) ?? record.PlayerClassCode;
        record.IsOnline = online;

        var now = DateTime.UtcNow;
        if (online)
        {
            record.LastJoinUtc = now;
        }
        else
        {
            record.LastLeaveUtc = now;
        }

        if (save)
        {
            SavePlayerActivity();
            _ = RefreshReviewCardsForPlayerAsync(player.PlayerUID, player.PlayerName ?? "");
        }
    }

    private object? FindTh3Essentials(ICoreServerAPI sapi)
    {
        return sapi.ModLoader.GetModSystem("Th3Essentials.Th3Essentials");
    }

    private void SubscribeTh3DiscordReady(object th3Essentials)
    {
        _th3DiscordReadyEvent = th3Essentials.GetType().GetEvent("OnTh3DiscordReady", BindingFlags.Public | BindingFlags.Instance);
        if (_th3DiscordReadyEvent?.EventHandlerType == null)
        {
            _sapi.Logger.Error("DavesDiscordUtilities could not find Th3Essentials OnTh3DiscordReady event.");
            return;
        }

        try
        {
            var method = GetType().GetMethod(nameof(RegisterDiscord), BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return;

            _th3DiscordReadyHandler = Delegate.CreateDelegate(_th3DiscordReadyEvent.EventHandlerType, this, method);
            _th3DiscordReadyEvent.AddEventHandler(th3Essentials, _th3DiscordReadyHandler);
        }
        catch (Exception exception)
        {
            _sapi.Logger.Error($"DavesDiscordUtilities could not subscribe to Th3Essentials Discord ready event: {exception.Message}");
        }
    }

    private void RegisterDiscord(object discord)
    {
        if (_discord != null) return;

        if (!Th3DiscordBridge.TryCreate(discord, _sapi, out var bridge))
        {
            return;
        }

        _discord = bridge;
        _createSlashCommandHandler = _discord.AddCreateSlashCommandHandler(this, nameof(CreateRequestSlashCommand));
        _createDduSlashCommandHandler = _discord.AddCreateSlashCommandHandler(this, nameof(CreateDduSlashCommand));
        _discord.Client.SlashCommandExecuted += OnSlashCommandExecuted;
        _discord.Client.ButtonExecuted += OnButtonExecuted;
        _discord.Client.MessageReceived += OnMessageReceived;
        _discord.Client.Ready += OnDiscordReadyAsync;

        StartBotStatusTickListener();
        StartRejectedThreadCleanupListener();
        _lastBotStatusText = "";
        QueueBotStatusUpdate(delayMs: 1500, force: true);
        _ = EnsureInstructionMessageAsync();
        _ = EnsureSlashCommandAsync();
        _sapi.Logger.Notification("DavesDiscordUtilities connected to Th3Essentials Discord client.");
    }

    private Task OnDiscordReadyAsync()
    {
        StartBotStatusTickListener();
        StartRejectedThreadCleanupListener();
        _lastBotStatusText = "";
        QueueBotStatusUpdate(delayMs: 1500, force: true);
        _ = EnsureInstructionMessageAsync();
        _ = EnsureSlashCommandAsync();
        return Task.CompletedTask;
    }

    private void StartBotStatusTickListener()
    {
        if (_botStatusTickListenerId != 0 || !_config.BotStatusEnabled) return;

        var intervalMs = Math.Max(60, _config.BotStatusUpdateSeconds) * 1000;
        _botStatusTickListenerId = _sapi.Event.RegisterGameTickListener(_ => QueueBotStatusUpdate(force: true), intervalMs);
    }

    private void StartRejectedThreadCleanupListener()
    {
        if (_rejectedThreadCleanupTickListenerId != 0) return;
        if (!_config.UseTemporaryApplicantThreads && !IdentityVerificationIsStrict()) return;

        _rejectedThreadCleanupTickListenerId = _sapi.Event.RegisterGameTickListener(_ => QueueRejectedThreadCleanup(), RejectedThreadCleanupTickMs);
        QueueRejectedThreadCleanup();
    }

    private void QueueBotStatusUpdate(int delayMs = 0, bool force = false)
    {
        if (_discord == null || !_config.BotStatusEnabled) return;
        if (Interlocked.Exchange(ref _botStatusUpdateQueued, 1) == 1) return;

        _ = UpdateBotStatusQueuedAsync(delayMs, force);
    }

    private void QueueRejectedThreadCleanup()
    {
        if (_discord == null) return;
        if (!_config.UseTemporaryApplicantThreads && !IdentityVerificationIsStrict()) return;
        if (Interlocked.Exchange(ref _rejectedThreadCleanupQueued, 1) == 1) return;

        RunDiscordTask(CleanupRejectedApplicantThreadsAsync, "rejected applicant thread cleanup");
    }

    private async Task UpdateBotStatusQueuedAsync(int delayMs, bool force)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }

            await UpdateBotStatusAsync(force);
        }
        catch (Exception exception)
        {
            _sapi.Logger.Debug($"Could not update DavesDiscordUtilities bot status: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _botStatusUpdateQueued, 0);
        }
    }

    private async Task UpdateBotStatusAsync(bool force)
    {
        if (_discord == null || !_config.BotStatusEnabled) return;

        var status = BuildBotStatusText();
        if (status.Length == 0 || (!force && status == _lastBotStatusText)) return;

        await _discord.Client.SetGameAsync(status);
        _lastBotStatusText = status;
    }

    private string BuildBotStatusText()
    {
        var playerText = _config.BotStatusShowPlayerCount
            ? string.Format(_config.BotStatusPlayerCountFormat, GetAdmittedPlayerCount(), GetMaxClientCount())
            : "";
        var dateText = _config.BotStatusShowWorldDate
            ? GetBotStatusWorldDateText()
            : "";

        var text = _config.BotStatusFormat
            .Replace("{players}", playerText)
            .Replace("{date}", dateText)
            .Trim();

        text = Regex.Replace(text, @"(^[\s|]+|[\s|]+$)", "");
        text = Regex.Replace(text, @"\s{2,}", " ");
        return text.Length <= 128 ? text : text[..128];
    }

    private int GetAdmittedPlayerCount()
    {
        return _sapi.Server?.Players?.Count(player => player.ConnectionState.IsAdmitted()) ?? 0;
    }

    private int GetMaxClientCount()
    {
        return _sapi.Server?.Config?.MaxClients ?? 0;
    }

    private string GetBotStatusWorldDateText()
    {
        var current = ReadCurrentWorldDate();
        if (current == null)
        {
            if (_botStatusRecord.YearNumber > 0 && !string.IsNullOrWhiteSpace(_botStatusRecord.MonthName))
            {
                return string.Format(_config.BotStatusWorldDateFormat, _botStatusRecord.MonthName, _botStatusRecord.YearNumber);
            }

            return string.IsNullOrWhiteSpace(_config.BotStatusFallbackDateText)
                ? ""
                : _config.BotStatusFallbackDateText.Trim();
        }

        var hasSavedDate = HasSavedWorldDate(current.WorldKey);

        if (!_botStatusCanUseLiveCalendar && hasSavedDate)
        {
            return string.Format(_config.BotStatusWorldDateFormat, _botStatusRecord.MonthName, _botStatusRecord.YearNumber);
        }

        if (!_botStatusCanUseLiveCalendar && !string.IsNullOrWhiteSpace(_config.BotStatusFallbackDateText))
        {
            return _config.BotStatusFallbackDateText.Trim();
        }

        if (!_botStatusCanUseLiveCalendar)
        {
            return string.Format(_config.BotStatusWorldDateFormat, current.MonthName, current.YearNumber);
        }

        if (!hasSavedDate || IsLiveWorldDateReady(current))
        {
            SaveBotStatusRecordIfChanged(current);
            return string.Format(_config.BotStatusWorldDateFormat, current.MonthName, current.YearNumber);
        }

        return string.Format(_config.BotStatusWorldDateFormat, _botStatusRecord.MonthName, _botStatusRecord.YearNumber);
    }

    private BotStatusRecord? ReadCurrentWorldDate()
    {
        var calendar = _sapi.World?.Calendar;
        if (calendar == null)
        {
            return null;
        }

        var daysPerMonth = Math.Max(1.0, (double)calendar.DaysPerMonth);
        var monthIndex = (int)Math.Floor(calendar.DayOfYear / daysPerMonth);
        monthIndex = Math.Clamp(monthIndex, 0, MonthNames.Length - 1);

        var daysPerYear = Math.Max(1.0, (double)calendar.DaysPerYear);

        return new BotStatusRecord
        {
            WorldKey = GetWorldKey(),
            MonthName = MonthNames[monthIndex],
            YearNumber = (int)Math.Floor(calendar.TotalDays / daysPerYear) + 1,
            TotalDays = calendar.TotalDays,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private string GetWorldKey()
    {
        var worldManager = _sapi.WorldManager;
        if (worldManager == null)
        {
            return "default";
        }

        var type = worldManager.GetType();
        var worldFilepath = type.GetProperty("CurrentWorldFilepath", BindingFlags.Public | BindingFlags.Instance)?.GetValue(worldManager) as string;
        if (!string.IsNullOrWhiteSpace(worldFilepath))
        {
            return worldFilepath;
        }

        var worldName = type.GetProperty("CurrentWorldName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(worldManager) as string;
        return string.IsNullOrWhiteSpace(worldName) ? "default" : worldName;
    }

    private bool HasSavedWorldDate(string worldKey)
    {
        return _botStatusRecord.YearNumber > 0
            && !string.IsNullOrWhiteSpace(_botStatusRecord.MonthName)
            && string.Equals(_botStatusRecord.WorldKey, worldKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLiveWorldDateReady(BotStatusRecord current)
    {
        return current.TotalDays > 1.0;
    }

    private void SaveBotStatusRecordIfChanged(BotStatusRecord current)
    {
        if (string.Equals(_botStatusRecord.WorldKey, current.WorldKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_botStatusRecord.MonthName, current.MonthName, StringComparison.Ordinal)
            && _botStatusRecord.YearNumber == current.YearNumber)
        {
            return;
        }

        _botStatusRecord = current;
        SaveBotStatusRecord();
    }

    private ApplicationCommandProperties CreateRequestSlashCommand()
    {
        var options = new List<SlashCommandOptionBuilder>
        {
            new()
            {
                Name = PlayerNameOption,
                Description = "Vintage Story in-game username",
                Type = ApplicationCommandOptionType.String,
                IsRequired = true
            },
            new()
            {
                Name = MessageOption,
                Description = "Optional note for the review team",
                Type = ApplicationCommandOptionType.String,
                IsRequired = false
            }
        };

        return new SlashCommandBuilder
        {
            Name = CommandName,
            Description = "Request access to the Vintage Story server.",
            Options = options
        }.Build();
    }

    private ApplicationCommandProperties CreateDduSlashCommand()
    {
        var options = new List<SlashCommandOptionBuilder>
        {
            new()
            {
                Name = RepairSubcommandName,
                Description = "Repair or repost whitelist review cards.",
                Type = ApplicationCommandOptionType.SubCommand
            },
            new()
            {
                Name = LockdownSubcommandName,
                Description = "Enable or disable whitelist request lockdown.",
                Type = ApplicationCommandOptionType.SubCommand,
                Options = new List<SlashCommandOptionBuilder>
                {
                    new()
                    {
                        Name = LockdownEnabledOption,
                        Description = "True blocks new requests; false allows them again.",
                        Type = ApplicationCommandOptionType.Boolean,
                        IsRequired = true
                    }
                }
            }
        };

        return new SlashCommandBuilder
        {
            Name = AdminCommandName,
            Description = "Staff tools for Dave's Discord Utilities.",
            Options = options
        }.Build();
    }

    private async Task EnsureSlashCommandAsync()
    {
        if (_discord == null) return;

        try
        {
            var guild = GetConfiguredGuild();
            if (guild == null) return;

            var commands = await guild.GetApplicationCommandsAsync();
            await EnsureGuildCommandAsync(guild.Id, commands, CommandName, CreateRequestSlashCommand());
            await EnsureGuildCommandAsync(guild.Id, commands, AdminCommandName, CreateDduSlashCommand());
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"Could not register DavesDiscordUtilities Discord commands: {exception.Message}");
        }
    }

    private async Task EnsureGuildCommandAsync(ulong guildId, IReadOnlyCollection<SocketApplicationCommand> commands, string name, ApplicationCommandProperties properties)
    {
        var existing = commands.FirstOrDefault(command => command.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            await _discord!.Client.Rest.CreateGuildCommand(properties, guildId);
            _sapi.Logger.Notification($"DavesDiscordUtilities registered the /{name} Discord command.");
            return;
        }

        if (CommandSchemaIsCurrent(existing, name))
        {
            return;
        }

        if (properties is SlashCommandProperties slashProperties)
        {
            await existing.ModifyAsync<SlashCommandProperties>(modified =>
            {
                modified.Name = slashProperties.Name;
                modified.Description = slashProperties.Description;
                modified.Options = slashProperties.Options;
            });
        }
        else
        {
            await existing.DeleteAsync();
            await _discord!.Client.Rest.CreateGuildCommand(properties, guildId);
        }

        _sapi.Logger.Notification($"DavesDiscordUtilities updated the /{name} Discord command.");
    }

    private static bool CommandSchemaIsCurrent(SocketApplicationCommand command, string name)
    {
        if (name.Equals(CommandName, StringComparison.OrdinalIgnoreCase))
        {
            return command.Options.Any(option =>
                option.Name.Equals(PlayerNameOption, StringComparison.OrdinalIgnoreCase) &&
                option.Type == ApplicationCommandOptionType.String);
        }

        if (name.Equals(AdminCommandName, StringComparison.OrdinalIgnoreCase))
        {
            var hasRepair = command.Options.Any(option =>
                option.Name.Equals(RepairSubcommandName, StringComparison.OrdinalIgnoreCase) &&
                option.Type == ApplicationCommandOptionType.SubCommand &&
                (option.Options == null || !option.Options.Any()));
            var hasLockdown = command.Options.Any(option =>
                option.Name.Equals(LockdownSubcommandName, StringComparison.OrdinalIgnoreCase) &&
                option.Type == ApplicationCommandOptionType.SubCommand &&
                option.Options != null &&
                option.Options.Any(child =>
                    child.Name.Equals(LockdownEnabledOption, StringComparison.OrdinalIgnoreCase) &&
                    child.Type == ApplicationCommandOptionType.Boolean));

            return hasRepair && hasLockdown;
        }

        return true;
    }

    private SocketGuild? GetConfiguredGuild()
    {
        if (_discord == null) return null;

        if (_discord.Client.GetChannel(_config.RequestChannelId) is SocketGuildChannel requestChannel)
        {
            return requestChannel.Guild;
        }

        if (_discord.Client.GetChannel(_config.ReviewChannelId) is SocketGuildChannel reviewChannel)
        {
            return reviewChannel.Guild;
        }

        return _discord.Client.Guilds.FirstOrDefault();
    }

    private async Task EnsureInstructionMessageAsync()
    {
        if (!IsConfigured() || _discord == null) return;
        if (_discord.Client.GetChannel(_config.RequestChannelId) is not IMessageChannel requestChannel) return;

        try
        {
            if (_config.InstructionMessageId != 0 && await requestChannel.GetMessageAsync(_config.InstructionMessageId) is IUserMessage existing)
            {
                await existing.ModifyAsync(properties => properties.Content = _config.PromptMessage);
                return;
            }

            var message = await requestChannel.SendMessageAsync(_config.PromptMessage);
            _config.InstructionMessageId = message.Id;
            SaveConfig();
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"Could not create or update DavesDiscordUtilities instruction message: {exception.Message}");
        }
    }

    private async Task OnSlashCommandExecuted(SocketSlashCommand command)
    {
        if (!command.Data.Name.Equals(CommandName, StringComparison.OrdinalIgnoreCase) &&
            !command.Data.Name.Equals(AdminCommandName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!await TryDeferSlashCommandAsync(command)) return;

        RunDiscordTask(() => HandleDeferredSlashCommandAsync(command), $"slash command /{command.Data.Name}");
    }

    private Task OnMessageReceived(SocketMessage message)
    {
        TrackApplicantThreadResponse(message);
        if (!ShouldCleanRequestChannelMessage(message)) return Task.CompletedTask;
        RunDiscordTask(() => CleanRequestChannelMessageAsync((SocketUserMessage)message), "request channel cleanup");
        return Task.CompletedTask;
    }

    private void TrackApplicantThreadResponse(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        var request = _requests.Values.FirstOrDefault(candidate =>
            candidate.DiscordUserId == message.Author.Id &&
            IsRejectedForCleanup(candidate.Status) &&
            GetCommunicationThreadId(candidate) == message.Channel.Id);
        if (request == null) return;

        request.ApplicantRespondedAtUtc = DateTime.UtcNow;
        SaveRequests();
    }

    private void RunDiscordTask(Func<Task> work, string context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception exception)
            {
                _sapi.Logger.Error($"DavesDiscordUtilities failed during {context}: {exception}");
            }
        });
    }

    private bool ShouldCleanRequestChannelMessage(SocketMessage message)
    {
        return _config is { Enabled: true, DeleteRequestChannelMessages: true }
            && message is SocketUserMessage
            && message.Channel.Id == _config.RequestChannelId
            && message.Author is { IsBot: false };
    }

    private async Task CleanRequestChannelMessageAsync(SocketUserMessage message)
    {
        try
        {
            await message.DeleteAsync();
        }
        catch (HttpException exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not delete request-channel message {message.Id}: {exception.Reason ?? exception.Message}");
        }
        catch (Exception exception)
        {
            _sapi.Logger.Debug($"DavesDiscordUtilities could not delete request-channel message {message.Id}: {exception.Message}");
        }

        if (!ShouldSendRequestChannelNotice(message.Author.Id)) return;
        await SendRequestChannelCleanupNoticeAsync(message.Author);
    }

    private bool ShouldSendRequestChannelNotice(ulong discordUserId)
    {
        var now = DateTime.UtcNow;
        lock (_requestChannelNoticeLock)
        {
            if (_requestChannelNoticeSentAt.TryGetValue(discordUserId, out var lastNotice) &&
                now - lastNotice < TimeSpan.FromSeconds(RequestChannelNoticeCooldownSeconds))
            {
                return false;
            }

            _requestChannelNoticeSentAt[discordUserId] = now;
            return true;
        }
    }

    private async Task SendRequestChannelCleanupNoticeAsync(SocketUser user)
    {
        var request = FindLatestRequestByUser(user.Id);
        if (request != null)
        {
            var thread = await GetTemporaryApplicantThreadAsync(request);
            if (thread != null)
            {
                try
                {
                    await thread.SendMessageAsync($"{user.Mention} I removed your message from <#{_config.RequestChannelId}> to keep the request channel clean. Please continue here.");
                    return;
                }
                catch (Exception exception)
                {
                    _sapi.Logger.Debug($"Could not send DavesDiscordUtilities cleanup notice in temporary thread {thread.Id}: {exception.Message}");
                }
            }
        }

        try
        {
            await user.SendMessageAsync(BuildRequestChannelCleanupNotice(request));
        }
        catch (Exception exception)
        {
            _sapi.Logger.Debug($"Could not send DavesDiscordUtilities cleanup DM to Discord user {user.Id}: {exception.Message}");
        }
    }

    private string BuildRequestChannelCleanupNotice(WhitelistRequest? request)
    {
        if (request == null)
        {
            return $"I removed your message from <#{_config.RequestChannelId}> to keep the request channel clean. Please use `/request` in that channel and fill in your in-game username and message.";
        }

        return request.Status switch
        {
            DavesDiscordUtilitiesStatuses.Pending => $"I removed your message from <#{_config.RequestChannelId}> to keep the request channel clean. Your whitelist request for `{SafeInline(request.PlayerName)}` is open. Use your private thread for follow-up, or rerun `/request` if you mistyped your username.",
            DavesDiscordUtilitiesStatuses.Approved => $"I removed your message from <#{_config.RequestChannelId}> to keep the request channel clean. Your whitelist request for `{SafeInline(request.PlayerName)}` is already approved.",
            DavesDiscordUtilitiesStatuses.Banned => string.Format(_config.RequestBanMessage, $"<@{request.DiscordUserId}>"),
            DavesDiscordUtilitiesStatuses.Denied or DavesDiscordUtilitiesStatuses.Revoked or DavesDiscordUtilitiesStatuses.Removed or DavesDiscordUtilitiesStatuses.Unbanned => $"I removed your message from <#{_config.RequestChannelId}> to keep the request channel clean. Please rerun `/request` if you need to submit or correct a whitelist request.",
            _ => $"I removed your message from <#{_config.RequestChannelId}> to keep the request channel clean. Please use `/request` for whitelist requests."
        };
    }

    private async Task<bool> TryDeferSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            await command.DeferAsync(ephemeral: true);
            return true;
        }
        catch (HttpException exception)
        {
            if (IsUnknownInteraction(exception))
            {
                return false;
            }

            _sapi.Logger.Warning($"DavesDiscordUtilities could not defer /{command.Data.Name}: {exception.Reason ?? exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not defer /{command.Data.Name}: {exception.Message}");
            return false;
        }
    }

    private async Task HandleDeferredSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            if (command.Data.Name.Equals(CommandName, StringComparison.OrdinalIgnoreCase))
            {
                await HandleRequestCommandAsync(command);
                return;
            }

            if (command.Data.Name.Equals(AdminCommandName, StringComparison.OrdinalIgnoreCase))
            {
                await HandleDduCommandAsync(command);
            }
        }
        catch (Exception exception)
        {
            _sapi.Logger.Error(exception);
            await UpdateSlashResponseAsync(command, "Dave could not process that command. Please contact staff.");
        }
    }

    private async Task HandleRequestCommandAsync(SocketSlashCommand command)
    {
        if (!IsConfigured())
        {
            await UpdateSlashResponseAsync(command, "Whitelist requests are not configured yet. Please contact staff.");
            return;
        }

        var strictVerificationError = GetStrictIdentityVerificationConfigurationError();
        if (strictVerificationError != null)
        {
            await UpdateSlashResponseAsync(command, strictVerificationError);
            return;
        }

        if (_config.LockdownEnabled)
        {
            await UpdateSlashResponseAsync(command, _config.LockdownMessage);
            return;
        }

        if (command.Channel.Id != _config.RequestChannelId)
        {
            await UpdateSlashResponseAsync(command, _config.UsageMessage);
            return;
        }

        if (command.User is not SocketGuildUser guildUser)
        {
            await UpdateSlashResponseAsync(command, "Whitelist requests must be made inside the Discord server.");
            return;
        }

        if (FindRequestBanForUser(command.User.Id) != null)
        {
            await UpdateSlashResponseAsync(command, string.Format(_config.RequestBanMessage, command.User.Mention));
            return;
        }

        var playerName = ReadStringOption(command, PlayerNameOption)?.Trim() ?? "";
        var requestText = ReadStringOption(command, MessageOption)?.Trim() ?? "";

        if (!PlayerNamePattern.IsMatch(playerName))
        {
            await UpdateSlashResponseAsync(command, _config.InvalidNameMessage);
            return;
        }

        var playerUid = await ResolvePlayerUidAsync(playerName);
        if (playerUid == null)
        {
            await UpdateSlashResponseAsync(command, string.Format(_config.UnknownPlayerMessage, playerName));
            return;
        }

        var alreadyWhitelisted = IsAlreadyWhitelisted(playerName, playerUid);

        var existingForPlayer = FindActiveRequestByPlayer(playerName);
        if (existingForPlayer != null && existingForPlayer.DiscordUserId != command.User.Id)
        {
            await UpdateSlashResponseAsync(command, $"There is already an active whitelist request for `{playerName}`.");
            return;
        }

        var existingForUser = FindActiveRequestByUser(command.User.Id);
        if (existingForUser != null)
        {
            if (existingForUser.Status == DavesDiscordUtilitiesStatuses.Pending)
            {
                if (alreadyWhitelisted)
                {
                    await UpdatePendingRequestFromExistingWhitelistAsync(existingForUser, guildUser, playerName, playerUid, requestText, command);
                    return;
                }

                await UpdatePendingRequestAsync(existingForUser, guildUser, playerName, playerUid, requestText, command);
                return;
            }

            if (alreadyWhitelisted &&
                existingForUser.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                await SyncApprovedExistingWhitelistRequestAsync(existingForUser, guildUser, command);
                return;
            }

            await UpdateSlashResponseAsync(command, $"Your Discord account already has an approved whitelist request for `{existingForUser.PlayerName}`. Ask staff to revoke it before submitting a different username.");
            return;
        }

        if (alreadyWhitelisted)
        {
            await CreateExistingWhitelistRequestAsync(guildUser, playerName, playerUid, requestText, command);
            return;
        }

        var request = CreateRequest(guildUser, playerName, playerUid, requestText, command);
        if (await CreateReviewCardAsync(request, guildUser, command) == null) return;

        _requests[request.Id] = request;
        SaveRequests();

        await UpdateSlashResponseAsync(command, string.Format(_config.RequestReceivedMessage, request.PlayerName));
    }

    private async Task HandleDduCommandAsync(SocketSlashCommand command)
    {
        if (!IsConfigured())
        {
            await UpdateSlashResponseAsync(command, "DavesDiscordUtilities is not configured.");
            return;
        }

        if (command.User is not SocketGuildUser staff || !HasApprovalPermission(staff))
        {
            await UpdateSlashResponseAsync(command, "Only configured helpers can use Dave staff tools.");
            return;
        }

        var subcommand = command.Data.Options.FirstOrDefault();
        if (subcommand == null)
        {
            await HandleRepairCommandAsync(command, staff);
            return;
        }

        if (subcommand.Name.Equals(RepairSubcommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleRepairCommandAsync(command, staff);
            return;
        }

        if (subcommand.Name.Equals(LockdownSubcommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleLockdownCommandAsync(command, staff, subcommand);
            return;
        }

        if (!subcommand.Name.Equals(RepairSubcommandName, StringComparison.OrdinalIgnoreCase))
        {
            await UpdateSlashResponseAsync(command, $"Unknown Dave staff command `{SafeInline(subcommand.Name)}`. Use `/ddu repair` or `/ddu lockdown`.");
            return;
        }
    }

    private async Task HandleLockdownCommandAsync(SocketSlashCommand command, SocketGuildUser staff, SocketSlashCommandDataOption subcommand)
    {
        var enabledOption = subcommand.Options?
            .FirstOrDefault(option => option.Name.Equals(LockdownEnabledOption, StringComparison.OrdinalIgnoreCase));
        if (enabledOption?.Value is not bool enabled)
        {
            await UpdateSlashResponseAsync(command, "Use `/ddu lockdown enabled:true` or `/ddu lockdown enabled:false`.");
            return;
        }

        _config.LockdownEnabled = enabled;
        SaveConfig();

        _sapi.Logger.Audit($"{staff.DisplayName}({staff.Id}) set DavesDiscordUtilities lockdown to {enabled}.");
        await UpdateSlashResponseAsync(command, enabled
            ? "Whitelist request lockdown is now enabled."
            : "Whitelist request lockdown is now disabled.");
    }

    private async Task HandleRepairCommandAsync(SocketSlashCommand command, SocketGuildUser staff)
    {
        var targets = _requests.Values
            .OrderByDescending(GetRequestLastActivityUtc)
            .ToList();

        if (targets.Count == 0)
        {
            await UpdateSlashResponseAsync(command, "No tracked whitelist requests were found.");
            return;
        }

        var refreshed = 0;
        var reposted = 0;
        var failed = 0;
        var changed = false;
        var errors = new List<string>();

        await UpdateSlashResponseAsync(command, BuildRepairStartedMessage(targets.Count));

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var result = await RepairReviewCardAsync(target);
            switch (result.Status)
            {
                case ReviewCardRepairStatus.Refreshed:
                    refreshed++;
                    break;
                case ReviewCardRepairStatus.Reposted:
                    reposted++;
                    changed = true;
                    break;
                default:
                    failed++;
                    errors.Add($"`{SafeInline(target.Id)}`: {result.Error ?? "unknown error"}");
                    break;
            }

            if (index < targets.Count - 1)
            {
                await Task.Delay(ReviewCardRepairDelayMs);
            }
        }

        if (changed)
        {
            SaveRequests();
        }

        var summary = $"Repair checked {targets.Count} request card(s): {refreshed} refreshed, {reposted} reposted, {failed} failed.";
        if (errors.Count > 0)
        {
            summary += "\n" + string.Join("\n", errors.Take(5));
        }

        _sapi.Logger.Audit($"{staff.DisplayName}({staff.Id}) ran DavesDiscordUtilities review-card repair for all requests.");
        await UpdateSlashResponseAsync(command, summary);
    }

    private static string BuildRepairStartedMessage(int targetCount)
    {
        var minimumSeconds = (int)Math.Ceiling(Math.Max(0, targetCount - 1) * ReviewCardRepairDelayMs / 1000d);
        return $"Repairing {targetCount} request card(s). Dave is pacing card edits to avoid Discord message-edit rate limits. Estimated minimum time: {FormatShortDuration(minimumSeconds)}.";
    }

    private static string FormatShortDuration(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds}s";
        }

        var minutes = seconds / 60;
        var remainingSeconds = seconds % 60;
        return remainingSeconds == 0 ? $"{minutes}m" : $"{minutes}m {remainingSeconds}s";
    }

    private async Task<ReviewCardRepairResult> RepairReviewCardAsync(WhitelistRequest request)
    {
        var channel = await GetReviewMessageChannelAsync(request);
        if (channel == null)
        {
            return ReviewCardRepairResult.Failed("Dave cannot find the staff review channel.");
        }

        if (_config.RequirePrivateReviewChannel && ReviewChannelExposesRequests(channel, out var exposureReason))
        {
            return ReviewCardRepairResult.Failed($"review channel is not private: {exposureReason}");
        }

        if (UpdateIdentityVerificationState(request))
        {
            SaveRequests();
        }

        try
        {
            if (request.ReviewMessageId != 0 &&
                await channel.GetMessageAsync(request.ReviewMessageId) is IUserMessage message)
            {
                await message.ModifyAsync(properties =>
                {
                    properties.Content = BuildReviewMessage(request);
                    properties.Components = BuildReviewComponents(request);
                });

                return ReviewCardRepairResult.Refreshed();
            }

            var newMessage = await channel.SendMessageAsync(BuildReviewMessage(request), components: BuildReviewComponents(request));
            request.ReviewChannelId = channel.Id;
            request.ReviewMessageId = newMessage.Id;
            return ReviewCardRepairResult.Reposted();
        }
        catch (HttpException exception)
        {
            return ReviewCardRepairResult.Failed(exception.Reason ?? exception.Message);
        }
        catch (Exception exception)
        {
            return ReviewCardRepairResult.Failed(exception.Message);
        }
    }

    private WhitelistRequest CreateRequest(SocketGuildUser guildUser, string playerName, string playerUid, string requestText, SocketSlashCommand command)
    {
        return new WhitelistRequest
        {
            Id = NewRequestId(),
            GuildId = guildUser.Guild.Id,
            DiscordUserId = command.User.Id,
            DiscordName = SafeInline(GetDisplayName(command.User)),
            PlayerName = playerName,
            PlayerUid = playerUid,
            RequestText = requestText,
            RequestChannelId = command.Channel.Id,
            RequestMessageId = command.Id,
            ReviewChannelId = _config.ReviewChannelId
        };
    }

    private async Task<IMessageChannel?> CreateReviewCardAsync(WhitelistRequest request, SocketGuildUser guildUser, SocketSlashCommand command)
    {
        if (_discord!.Client.GetChannel(_config.ReviewChannelId) is not IMessageChannel reviewChannel)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities ReviewChannelId {_config.ReviewChannelId} is not available to the bot.");
            await UpdateSlashResponseAsync(command, "Dave is missing the staff review channel. Please contact staff.");
            return null;
        }

        if (_config.RequirePrivateReviewChannel && ReviewChannelExposesRequests(reviewChannel, out var exposureReason))
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities refused to post a request because review channel {_config.ReviewChannelId} is not private: {exposureReason}.");
            await UpdateSlashResponseAsync(command, _config.UnsafeReviewChannelMessage);
            return null;
        }

        if (_config.UseTemporaryApplicantThreads)
        {
            var thread = await CreateTemporaryApplicantThreadAsync(request, guildUser);
            if (thread == null)
            {
                await UpdateSlashResponseAsync(command, _config.TemporaryThreadErrorMessage);
                return null;
            }
        }

        UpdateIdentityVerificationState(request);
        var reviewMessage = await reviewChannel.SendMessageAsync(BuildReviewMessage(request), components: BuildReviewComponents(request));
        request.ReviewMessageId = reviewMessage.Id;
        return reviewChannel;
    }

    private async Task CreateExistingWhitelistRequestAsync(SocketGuildUser guildUser, string playerName, string playerUid, string requestText, SocketSlashCommand command)
    {
        var request = CreateRequest(guildUser, playerName, playerUid, requestText, command);
        request.ExistingWhitelistClaim = true;

        var reviewMessageChannel = await CreateReviewCardAsync(request, guildUser, command);
        if (reviewMessageChannel == null) return;

        _requests[request.Id] = request;
        SaveRequests();

        await UpdateSlashResponseAsync(command, string.Format(_config.ExistingWhitelistReviewMessage, request.PlayerName));
    }

    private async Task UpdatePendingRequestAsync(WhitelistRequest request, SocketGuildUser guildUser, string playerName, string playerUid, string requestText, SocketSlashCommand command)
    {
        var previousPlayerName = UpdateRequestIdentity(request, guildUser, playerName, playerUid, requestText, command);
        request.ExistingWhitelistClaim = false;

        SaveRequests();

        try
        {
            await RefreshReviewCardAsync(request);
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities updated request {request.Id} but could not refresh the review card: {exception.Message}");
            await UpdateSlashResponseAsync(command, $"Your pending whitelist request was updated to `{request.PlayerName}`, but Dave could not refresh the review card. Please contact staff.");
            return;
        }

        _sapi.Logger.Audit($"{guildUser.DisplayName}({guildUser.Id}) updated Discord whitelist request {request.Id} from {previousPlayerName} to {request.PlayerName}.");
        await UpdateSlashResponseAsync(command, string.Format(_config.RequestUpdatedMessage, request.PlayerName));
    }

    private async Task UpdatePendingRequestFromExistingWhitelistAsync(WhitelistRequest request, SocketGuildUser guildUser, string playerName, string playerUid, string requestText, SocketSlashCommand command)
    {
        var previousPlayerName = UpdateRequestIdentity(request, guildUser, playerName, playerUid, requestText, command);
        request.ExistingWhitelistClaim = true;

        SaveRequests();
        await RefreshReviewCardAsync(request);

        _sapi.Logger.Audit($"{guildUser.DisplayName}({guildUser.Id}) updated Discord whitelist request {request.Id} from {previousPlayerName} to existing whitelisted player {request.PlayerName}; staff approval is still required.");
        await UpdateSlashResponseAsync(command, string.Format(_config.ExistingWhitelistReviewMessage, request.PlayerName));
    }

    private async Task SyncApprovedExistingWhitelistRequestAsync(WhitelistRequest request, SocketGuildUser guildUser, SocketSlashCommand command)
    {
        request.DiscordName = SafeInline(GetDisplayName(guildUser));
        request.RequestChannelId = command.Channel.Id;
        request.RequestMessageId = command.Id;
        request.LastUpdatedAtUtc = DateTime.UtcNow;

        var actionError = await SyncApprovedDiscordAccessAsync(request);
        if (actionError == null)
        {
            ApplyStrictIdentityVerificationDeadline(request);
            UpdateIdentityVerificationState(request);
            await ApplyTemporaryApplicantThreadStateAsync(request);
        }

        SaveRequests();
        await RefreshReviewCardAsync(request);

        if (actionError != null)
        {
            await UpdateSlashResponseAsync(command, $"Dave refreshed your archive card for `{request.PlayerName}`, but could not update Discord access: {actionError}");
            return;
        }

        await UpdateSlashResponseAsync(command, string.Format(_config.ExistingWhitelistLinkedMessage, request.PlayerName));
    }

    private string UpdateRequestIdentity(WhitelistRequest request, SocketGuildUser guildUser, string playerName, string playerUid, string requestText, SocketSlashCommand command)
    {
        var previousPlayerName = request.PlayerName;
        var playerChanged = !previousPlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase);

        if (playerChanged)
        {
            request.PreviousPlayerName = previousPlayerName;
            ClearIdentityVerificationState(request);
        }

        request.PlayerName = playerName;
        request.PlayerUid = playerUid;
        request.RequestText = requestText;
        request.DiscordName = SafeInline(GetDisplayName(guildUser));
        request.RequestChannelId = command.Channel.Id;
        request.RequestMessageId = command.Id;
        request.LastUpdatedAtUtc = DateTime.UtcNow;
        request.AppliedNickname = null;
        request.NicknameUpdateError = null;
        request.NicknameUpdatedAtUtc = null;
        request.RejectedThreadOpenedAtUtc = null;
        request.ApplicantRespondedAtUtc = null;

        return previousPlayerName;
    }

    private async Task<string?> SyncApprovedDiscordAccessAsync(WhitelistRequest request)
    {
        var roleError = ValidateRequesterRoleUpdate(request, GetApproveRoleIds());
        if (roleError != null) return roleError;

        await CapturePreviousDiscordNicknameAsync(request);

        roleError = await UpdateRequesterRolesAsync(request, approved: true, restorePendingRole: false);
        if (roleError != null) return roleError;

        await RenameApprovedMemberAsync(request);
        return null;
    }

    private static string? ReadStringOption(SocketSlashCommand command, string optionName)
    {
        return command.Data.Options.FirstOrDefault(option => option.Name == optionName)?.Value as string;
    }

    private async Task<string?> ResolvePlayerUidAsync(string playerName)
    {
        var playerData = _sapi.PlayerData.GetPlayerDataByLastKnownName(playerName);
        if (playerData != null) return playerData.PlayerUID;

        foreach (var player in _sapi.World.AllPlayers)
        {
            if (player.PlayerUID.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                return player.PlayerUID;
            }
        }

        using var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("playername", playerName)
        });
        var result = await HttpClient.PostAsync("https://auth.vintagestory.at/resolveplayername", body);
        if (result.StatusCode != HttpStatusCode.OK) return null;

        try
        {
            var responseData = await result.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<PlayerResolveResponse>(responseData)?.playeruid;
        }
        catch (Exception exception)
        {
            _sapi.Logger.Debug($"Could not resolve Vintage Story username {playerName}: {exception.Message}");
            return null;
        }
    }

    private async Task<IThreadChannel?> CreateTemporaryApplicantThreadAsync(WhitelistRequest request, IGuildUser applicant)
    {
        if (_discord == null) return null;

        var guild = _discord.Client.GetGuild(request.GuildId);
        if (guild == null)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not find guild {request.GuildId} for temporary applicant thread.");
            return null;
        }

        var parentChannelId = GetTemporaryThreadParentChannelId(request);
        if (_discord.Client.GetChannel(parentChannelId) is not ITextChannel parentChannel)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not find temporary thread parent channel {parentChannelId}.");
            return null;
        }

        try
        {
            var thread = await parentChannel.CreateThreadAsync(
                BuildThreadName(request),
                ThreadType.PrivateThread,
                ThreadArchiveDuration.OneWeek,
                message: null,
                invitable: false);

            request.CommunicationThreadId = thread.Id;
            request.ReviewThreadId = 0;
            request.CommunicationThreadError = null;

            await thread.AddUserAsync(applicant);
            await AddApproversToThreadAsync(thread, guild, applicant.Id);
            var approverMentions = BuildApproverMentions();
            var threadMessage = string.IsNullOrWhiteSpace(approverMentions)
                ? $"{applicant.Mention} Your whitelist request for `{SafeInline(request.PlayerName)}` is open. Staff can ask follow-up questions here."
                : $"{approverMentions} {applicant.Mention} Your whitelist request for `{SafeInline(request.PlayerName)}` is open. Staff can ask follow-up questions here.";
            await thread.SendMessageAsync(threadMessage);
            return thread;
        }
        catch (HttpException exception)
        {
            request.CommunicationThreadError = exception.Reason ?? exception.Message;
            _sapi.Logger.Warning($"DavesDiscordUtilities could not create a temporary applicant thread in channel {parentChannelId}: {request.CommunicationThreadError}");
            return null;
        }
    }

    private ulong GetTemporaryThreadParentChannelId(WhitelistRequest request)
    {
        return _config.TemporaryThreadParentChannelId == 0
            ? request.RequestChannelId
            : _config.TemporaryThreadParentChannelId;
    }

    private async Task AddApproversToThreadAsync(IThreadChannel thread, SocketGuild guild, ulong applicantId)
    {
        var approvers = await GetApproverUsersAsync(guild, applicantId);
        foreach (var approver in approvers)
        {
            try
            {
                await thread.AddUserAsync(approver);
            }
            catch (Exception exception)
            {
                _sapi.Logger.Debug($"Could not add approver {approver.Id} to DavesDiscordUtilities thread {thread.Id}: {exception.Message}");
            }
        }
    }

    private async Task<List<IGuildUser>> GetApproverUsersAsync(SocketGuild guild, ulong applicantId)
    {
        await Task.CompletedTask;
        return guild.Users
            .Where(user => user.Id != applicantId && !user.IsBot && HasApprovalPermission(user))
            .Cast<IGuildUser>()
            .ToList();
    }

    private string BuildApproverMentions()
    {
        if (_config.ApproverRoleIds == null || _config.ApproverRoleIds.Count == 0)
        {
            return "";
        }

        return string.Join(" ", _config.ApproverRoleIds.Select(roleId => $"<@&{roleId}>"));
    }

    private static string BuildThreadName(WhitelistRequest request)
    {
        var rawName = $"whitelist-{request.PlayerName}-{request.Id}";
        var safe = Regex.Replace(rawName, @"[^A-Za-z0-9_-]+", "-").Trim('-');
        return safe.Length <= 90 ? safe : safe[..90];
    }

    private WhitelistRequest? FindActiveRequestByUser(ulong discordUserId)
    {
        return _requests.Values.FirstOrDefault(request =>
            IsActive(request.Status) &&
            request.DiscordUserId == discordUserId);
    }

    private WhitelistRequest? FindLatestRequestByUser(ulong discordUserId)
    {
        return _requests.Values
            .Where(request => request.DiscordUserId == discordUserId)
            .OrderByDescending(GetRequestLastActivityUtc)
            .FirstOrDefault();
    }

    private WhitelistRequest? FindRequestBanForUser(ulong discordUserId)
    {
        return _requests.Values
            .Where(request =>
                request.DiscordUserId == discordUserId &&
                request.Status == DavesDiscordUtilitiesStatuses.Banned)
            .OrderByDescending(GetRequestLastActivityUtc)
            .FirstOrDefault();
    }

    private static DateTime GetRequestLastActivityUtc(WhitelistRequest request)
    {
        return request.LastUpdatedAtUtc ??
               request.DecidedAtUtc ??
               request.CreatedAtUtc;
    }

    private WhitelistRequest? FindActiveRequestByPlayer(string playerName)
    {
        return _requests.Values.FirstOrDefault(request =>
            IsActive(request.Status) &&
            request.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsActive(string status)
    {
        return status is DavesDiscordUtilitiesStatuses.Pending or DavesDiscordUtilitiesStatuses.Approved;
    }

    private static bool IsRejectedForCleanup(string status)
    {
        return status is DavesDiscordUtilitiesStatuses.Denied or DavesDiscordUtilitiesStatuses.Revoked;
    }

    private async Task OnButtonExecuted(SocketMessageComponent component)
    {
        if (!TryParseButton(component.Data.CustomId, out var action, out var requestId))
        {
            return;
        }

        if (!await TryDeferComponentAsync(component)) return;

        RunDiscordTask(() => HandleDeferredButtonAsync(component, action, requestId), "review button");
    }

    private async Task<bool> TryDeferComponentAsync(SocketMessageComponent component)
    {
        try
        {
            await component.DeferAsync(ephemeral: true);
            return true;
        }
        catch (HttpException exception)
        {
            if (IsUnknownInteraction(exception))
            {
                return false;
            }

            _sapi.Logger.Warning($"DavesDiscordUtilities could not defer review button {component.Data.CustomId}: {exception.Reason ?? exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not defer review button {component.Data.CustomId}: {exception.Message}");
            return false;
        }
    }

    private async Task HandleDeferredButtonAsync(SocketMessageComponent component, string action, string requestId)
    {
        try
        {
            await HandleReviewButtonAsync(component, action, requestId);
        }
        catch (Exception exception)
        {
            _sapi.Logger.Error(exception);
            await UpdateComponentResponseAsync(component, "Dave could not process that review action. Check the server log.");
        }
    }

    private async Task HandleReviewButtonAsync(SocketMessageComponent component, string action, string requestId)
    {
        if (!IsConfigured())
        {
            await UpdateComponentResponseAsync(component, "DavesDiscordUtilities is not configured.");
            return;
        }

        if (!_requests.TryGetValue(requestId, out var request))
        {
            await UpdateComponentResponseAsync(component, "That whitelist request is no longer tracked.");
            return;
        }

        if (!ReviewActionIsInExpectedChannel(component.Channel.Id, request))
        {
            await UpdateComponentResponseAsync(component, "This review action is not valid in this channel.");
            return;
        }

        if (component.User is not SocketGuildUser reviewer || !HasApprovalPermission(reviewer))
        {
            await UpdateComponentResponseAsync(component, "Only configured helpers can review whitelist requests.");
            return;
        }

        var decisionLock = GetRequestDecisionLock(requestId);
        if (!await decisionLock.WaitAsync(0))
        {
            await UpdateComponentResponseAsync(component, "Another helper is already processing this request. Try again after the review card updates.");
            return;
        }

        try
        {
            var changed = false;
            string? actionError = null;
            if (action == AddAction)
            {
                actionError = await ApproveRequestAsync(request, reviewer);
                changed = actionError == null;
            }
            else if (action == RevokeAction)
            {
                actionError = await RevokeRequestAsync(request, reviewer);
                changed = actionError == null;
            }
            else if (action == RemoveAction)
            {
                actionError = await RemoveRejectedThreadAsync(request, reviewer);
                changed = actionError == null;
            }
            else if (action == BanAction)
            {
                actionError = await BanRequestAsync(request, reviewer);
                changed = actionError == null;
            }
            else if (action == UnbanAction)
            {
                actionError = await UnbanRequestAsync(request, reviewer);
                changed = actionError == null;
            }

            if (actionError != null)
            {
                await UpdateComponentResponseAsync(component, actionError);
                return;
            }

            if (!changed)
            {
                await UpdateComponentResponseAsync(component, "Unknown whitelist action.");
                return;
            }

            SaveRequests();
            await RefreshReviewCardAsync(request);

            var response = $"Request `{request.PlayerName}` is now {request.Status.ToLowerInvariant()}.";
            if (!string.IsNullOrWhiteSpace(request.NicknameUpdateError))
            {
                response += $" Nickname note: {request.NicknameUpdateError}";
            }

            await UpdateComponentResponseAsync(component, response);
            RunDiscordTask(() => CompleteReviewActionAfterResponseAsync(request), $"post-review cleanup for {request.Id}");
        }
        finally
        {
            decisionLock.Release();
        }
    }

    private async Task CompleteReviewActionAfterResponseAsync(WhitelistRequest request)
    {
        if (IsRejectedForCleanup(request.Status))
        {
            await OpenTemporaryApplicantThreadAsync(request);
            request.RejectedThreadOpenedAtUtc ??= DateTime.UtcNow;
            await NotifyRequesterAsync(request, GetDecisionMessage(request, includeRejectedThreadNotice: true));
        }
        else if (request.Status == DavesDiscordUtilitiesStatuses.Banned)
        {
            await NotifyRequesterAsync(request, GetDecisionMessage(request), notifyInThread: false);
            await DeleteTemporaryApplicantThreadAsync(request);
        }
        else if (request.Status == DavesDiscordUtilitiesStatuses.Removed)
        {
            await DeleteTemporaryApplicantThreadAsync(request);
        }
        else
        {
            await NotifyRequesterAsync(request, GetDecisionMessage(request), notifyInThread: false);
            await ApplyTemporaryApplicantThreadStateAsync(request);
        }

        SaveRequests();
        await RefreshReviewCardAsync(request);
    }

    private SemaphoreSlim GetRequestDecisionLock(string requestId)
    {
        return _requestDecisionLocks.GetOrAdd(requestId, _ => new SemaphoreSlim(1, 1));
    }

    private static bool ReviewActionIsInExpectedChannel(ulong channelId, WhitelistRequest request)
    {
        return channelId == request.ReviewChannelId;
    }

    private static bool TryParseButton(string customId, out string action, out string requestId)
    {
        action = "";
        requestId = "";

        var parts = customId.Split(':');
        if (parts.Length != 3 || parts[0] != ButtonPrefix) return false;
        if (parts[1] is not (AddAction or RevokeAction or RemoveAction or BanAction or UnbanAction)) return false;
        if (parts[2].Length == 0) return false;

        action = parts[1];
        requestId = parts[2];
        return true;
    }

    private bool IsAlreadyWhitelisted(string playerName, string playerUid)
    {
        var latestRequest = FindLatestRequestForPlayer(playerName, playerUid);
        if (latestRequest is { ExistingWhitelistClaim: false } &&
            latestRequest.Status is DavesDiscordUtilitiesStatuses.Revoked or DavesDiscordUtilitiesStatuses.Banned ||
            latestRequest is { ExistingWhitelistClaim: false, Status: DavesDiscordUtilitiesStatuses.Removed or DavesDiscordUtilitiesStatuses.Unbanned, DateAddedUtc: not null })
        {
            return false;
        }

        if (latestRequest?.Status == DavesDiscordUtilitiesStatuses.Approved)
        {
            return true;
        }

        return IsServerWhitelisted(playerName, playerUid);
    }

    private WhitelistRequest? FindLatestRequestForPlayer(string playerName, string playerUid)
    {
        return _requests.Values
            .Where(request =>
                request.PlayerUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase) ||
                request.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(request => request.DecidedAtUtc ?? request.CreatedAtUtc)
            .FirstOrDefault();
    }

    private bool IsServerWhitelisted(string playerName, string playerUid)
    {
        var playerDataManager = ((ServerMain)_sapi.World).PlayerDataManager;
        var managerType = playerDataManager.GetType();

        if (TryWhitelistLookupMethod(playerDataManager, managerType, playerName, playerUid, out var found))
        {
            return found;
        }

        foreach (var memberName in new[] { "WhitelistedPlayers", "WhiteListedPlayers", "WhitelistPlayers", "WhiteListPlayers", "Whitelist", "WhiteList" })
        {
            var property = managerType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property?.GetValue(playerDataManager) is IEnumerable propertyEntries && ContainsWhitelistEntry(propertyEntries, playerName, playerUid))
            {
                return true;
            }

            var field = managerType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(playerDataManager) is IEnumerable fieldEntries && ContainsWhitelistEntry(fieldEntries, playerName, playerUid))
            {
                return true;
            }
        }

        var playerData = _sapi.PlayerData.GetPlayerDataByLastKnownName(playerName);
        return playerData != null && ReadBoolMember(playerData, "Whitelisted", "WhiteListed", "IsWhitelisted", "IsWhiteListed");
    }

    private static bool TryWhitelistLookupMethod(object playerDataManager, Type managerType, string playerName, string playerUid, out bool found)
    {
        found = false;
        foreach (var methodName in new[] { "GetPlayerWhitelist", "GetPlayerWhiteList", "GetWhitelistEntry", "GetWhiteListEntry", "IsWhitelisted", "IsWhiteListed" })
        {
            foreach (var method in managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(method => method.Name == methodName))
            {
                var parameters = method.GetParameters();
                object?[]? args = parameters.Length switch
                {
                    1 => new object?[] { playerUid },
                    2 => new object?[] { playerName, playerUid },
                    _ => null
                };

                if (args == null) continue;

                try
                {
                    var result = method.Invoke(playerDataManager, args);
                    if (result is bool boolResult)
                    {
                        found = boolResult;
                        return true;
                    }

                    if (result != null)
                    {
                        found = true;
                        return true;
                    }
                }
                catch
                {
                    // vintage story has changed whitelist internals across versions.
                }
            }
        }

        return false;
    }

    private static bool ContainsWhitelistEntry(IEnumerable entries, string playerName, string playerUid)
    {
        foreach (var entry in entries)
        {
            if (entry == null) continue;

            var entryUid = ReadStringMember(entry, "PlayerUID", "PlayerUid", "UID", "Uid");
            var entryName = ReadStringMember(entry, "PlayerName", "Name", "LastKnownPlayerName");

            if (!string.IsNullOrEmpty(entryUid) && entryUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(entryName) && entryName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadStringMember(object instance, params string[] names)
    {
        var type = instance.GetType();
        foreach (var name in names)
        {
            if (type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is string propertyValue)
            {
                return propertyValue;
            }

            if (type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is string fieldValue)
            {
                return fieldValue;
            }
        }

        return null;
    }

    private static bool ReadBoolMember(object instance, params string[] names)
    {
        var type = instance.GetType();
        foreach (var name in names)
        {
            if (type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is bool propertyValue)
            {
                return propertyValue;
            }

            if (type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is bool fieldValue)
            {
                return fieldValue;
            }
        }

        return false;
    }

    private async Task<string?> ApproveRequestAsync(WhitelistRequest request, SocketGuildUser moderator)
    {
        var strictVerificationError = GetStrictIdentityVerificationConfigurationError();
        if (strictVerificationError != null) return strictVerificationError;

        var roleError = ValidateRequesterRoleUpdate(request, GetApproveRoleIds());
        if (roleError != null) return roleError;

        var preserveWhitelist = request.ExistingWhitelistClaim;
        if (!preserveWhitelist)
        {
            WhitelistPlayer(request, moderator);
        }

        await CapturePreviousDiscordNicknameAsync(request);

        roleError = await UpdateRequesterRolesAsync(request, approved: true, restorePendingRole: false);
        if (roleError != null)
        {
            if (!preserveWhitelist)
            {
                UnWhitelistPlayer(request, moderator);
            }

            return roleError;
        }

        request.Status = DavesDiscordUtilitiesStatuses.Approved;
        request.RejectedThreadOpenedAtUtc = null;
        request.ApplicantRespondedAtUtc = null;
        request.DecidedByDiscordUserId = moderator.Id;
        request.DecidedByName = SafeInline(moderator.DisplayName);
        request.DecidedAtUtc = DateTime.UtcNow;
        request.DateAddedUtc ??= request.DecidedAtUtc;
        ApplyStrictIdentityVerificationDeadline(request);
        UpdateIdentityVerificationState(request);

        await RenameApprovedMemberAsync(request);

        _sapi.Logger.Audit($"{moderator.DisplayName}({moderator.Id}) approved Discord whitelist request {request.Id} for {request.PlayerName} ({request.DiscordUserId}).");
        return null;
    }

    private async Task<string?> RevokeRequestAsync(WhitelistRequest request, SocketGuildUser moderator)
    {
        var wasApproved = request.Status == DavesDiscordUtilitiesStatuses.Approved;
        var actionError = wasApproved ? await RemoveApprovedAccessAsync(request, moderator) : null;
        if (actionError != null) return actionError;

        request.Status = wasApproved ? DavesDiscordUtilitiesStatuses.Revoked : DavesDiscordUtilitiesStatuses.Denied;
        request.RejectedThreadOpenedAtUtc = DateTime.UtcNow;
        request.ApplicantRespondedAtUtc = null;
        request.DecidedByDiscordUserId = moderator.Id;
        request.DecidedByName = SafeInline(moderator.DisplayName);
        request.DecidedAtUtc = DateTime.UtcNow;

        _sapi.Logger.Audit($"{moderator.DisplayName}({moderator.Id}) set Discord whitelist request {request.Id} for {request.PlayerName} to {request.Status}.");
        return null;
    }

    private async Task<string?> BanRequestAsync(WhitelistRequest request, SocketGuildUser moderator)
    {
        var wasApproved = request.Status == DavesDiscordUtilitiesStatuses.Approved;
        var actionError = wasApproved ? await RemoveApprovedAccessAsync(request, moderator) : null;
        if (actionError != null) return actionError;

        request.Status = DavesDiscordUtilitiesStatuses.Banned;
        request.RejectedThreadOpenedAtUtc = null;
        request.ApplicantRespondedAtUtc = null;
        request.DecidedByDiscordUserId = moderator.Id;
        request.DecidedByName = SafeInline(moderator.DisplayName);
        request.DecidedAtUtc = DateTime.UtcNow;

        _sapi.Logger.Audit($"{moderator.DisplayName}({moderator.Id}) banned Discord user {request.DiscordUserId} from DavesDiscordUtilities whitelist requests through request {request.Id}.");
        return null;
    }

    private Task<string?> UnbanRequestAsync(WhitelistRequest request, SocketGuildUser moderator)
    {
        if (request.Status != DavesDiscordUtilitiesStatuses.Banned)
        {
            return Task.FromResult<string?>("This request is not request-banned.");
        }

        request.Status = DavesDiscordUtilitiesStatuses.Unbanned;
        request.RejectedThreadOpenedAtUtc = null;
        request.ApplicantRespondedAtUtc = null;
        request.DecidedByDiscordUserId = moderator.Id;
        request.DecidedByName = SafeInline(moderator.DisplayName);
        request.DecidedAtUtc = DateTime.UtcNow;

        _sapi.Logger.Audit($"{moderator.DisplayName}({moderator.Id}) lifted the DavesDiscordUtilities whitelist request ban for Discord user {request.DiscordUserId} through request {request.Id}.");
        return Task.FromResult<string?>(null);
    }

    private async Task<string?> RemoveRejectedThreadAsync(WhitelistRequest request, SocketGuildUser moderator)
    {
        if (!IsRejectedForCleanup(request.Status) && request.Status != DavesDiscordUtilitiesStatuses.Removed)
        {
            return "Remove only cleans up rejected applicant threads.";
        }

        await DeleteTemporaryApplicantThreadAsync(request);
        request.Status = DavesDiscordUtilitiesStatuses.Removed;
        request.LastUpdatedAtUtc = DateTime.UtcNow;
        request.RejectedThreadOpenedAtUtc = null;
        request.ApplicantRespondedAtUtc = null;

        _sapi.Logger.Audit($"{moderator.DisplayName}({moderator.Id}) removed rejected applicant thread for Discord whitelist request {request.Id}.");
        return null;
    }

    private async Task<string?> RemoveApprovedAccessAsync(WhitelistRequest request, SocketGuildUser moderator)
    {
        return await RemoveApprovedAccessAsync(
            request,
            removeDiscordRole: true,
            removeGameWhitelist: true,
            auditName: moderator.DisplayName,
            auditDiscordUserId: moderator.Id);
    }

    private async Task<string?> RemoveApprovedAccessAsync(WhitelistRequest request, bool removeDiscordRole, bool removeGameWhitelist, string auditName, ulong? auditDiscordUserId)
    {
        if (removeDiscordRole)
        {
            var roleError = ValidateRequesterRoleUpdate(request, GetRemoveRoleIds(restorePendingRole: true));
            if (roleError != null) return roleError;

            roleError = await UpdateRequesterRolesAsync(request, approved: false, restorePendingRole: true);
            if (roleError != null) return roleError;
        }

        if (removeGameWhitelist && !request.ExistingWhitelistClaim)
        {
            UnWhitelistPlayer(request, auditName, auditDiscordUserId);
        }

        if (removeDiscordRole)
        {
            await RestoreApprovedNicknameAsync(request);
        }

        return null;
    }

    private void WhitelistPlayer(WhitelistRequest request, SocketGuildUser moderator)
    {
        var until = DateTime.Now.ToLocalTime().AddYears(Math.Max(1, _config.WhitelistYears));
        var reason = $"Discord whitelist request {request.Id}";
        ((ServerMain)_sapi.World).PlayerDataManager.WhitelistPlayer(request.PlayerName, request.PlayerUid, moderator.DisplayName, reason, until);
    }

    private void UnWhitelistPlayer(WhitelistRequest request, SocketGuildUser moderator)
    {
        UnWhitelistPlayer(request, moderator.DisplayName, moderator.Id);
    }

    private void UnWhitelistPlayer(WhitelistRequest request, string auditName, ulong? auditDiscordUserId)
    {
        _ = ((ServerMain)_sapi.World).PlayerDataManager.UnWhitelistPlayer(request.PlayerName, request.PlayerUid);
        var actor = auditDiscordUserId == null ? auditName : $"{auditName}({auditDiscordUserId})";
        _sapi.Logger.Audit($"{actor} removed {request.PlayerName} from whitelist through Discord request {request.Id}.");
    }

    private List<ulong> GetApproveRoleIds()
    {
        var roleIds = new List<ulong>();
        if (_config.ApprovedRoleId != 0) roleIds.Add(_config.ApprovedRoleId);
        if (_config.PendingRoleId != 0) roleIds.Add(_config.PendingRoleId);
        return roleIds;
    }

    private List<ulong> GetRemoveRoleIds(bool restorePendingRole)
    {
        var roleIds = new List<ulong>();
        if (_config.ApprovedRoleId != 0) roleIds.Add(_config.ApprovedRoleId);
        if (restorePendingRole && _config.PendingRoleId != 0) roleIds.Add(_config.PendingRoleId);
        return roleIds;
    }

    private string? ValidateRequesterRoleUpdate(WhitelistRequest request, IReadOnlyCollection<ulong> roleIds)
    {
        if (roleIds.Count == 0) return null;
        if (_discord == null) return "Dave is not connected to Discord.";

        var guild = _discord.Client.GetGuild(request.GuildId);
        if (guild == null) return "Dave cannot find the configured Discord server.";

        var botUser = guild.CurrentUser;
        if (!botUser.GuildPermissions.Administrator && !botUser.GuildPermissions.ManageRoles)
        {
            return "Dave needs the Manage Roles permission to update whitelist roles.";
        }

        foreach (var roleId in roleIds.Distinct())
        {
            var role = guild.GetRole(roleId);
            if (role == null)
            {
                return $"Configured role `{roleId}` was not found in Discord.";
            }

            if (botUser.Hierarchy <= role.Position)
            {
                return $"Dave's highest role must be above `{role.Name}` in Discord's role list before it can manage that role.";
            }
        }

        return null;
    }

    private async Task<string?> UpdateRequesterRolesAsync(WhitelistRequest request, bool approved, bool restorePendingRole)
    {
        if (_discord == null) return null;

        try
        {
            if (approved)
            {
                if (_config.ApprovedRoleId != 0)
                {
                    await _discord.Client.Rest.AddRoleAsync(request.GuildId, request.DiscordUserId, _config.ApprovedRoleId);
                }

                if (_config.PendingRoleId != 0)
                {
                    await _discord.Client.Rest.RemoveRoleAsync(request.GuildId, request.DiscordUserId, _config.PendingRoleId);
                }
            }
            else
            {
                if (_config.ApprovedRoleId != 0)
                {
                    await _discord.Client.Rest.RemoveRoleAsync(request.GuildId, request.DiscordUserId, _config.ApprovedRoleId);
                }

                if (restorePendingRole && _config.PendingRoleId != 0)
                {
                    await _discord.Client.Rest.AddRoleAsync(request.GuildId, request.DiscordUserId, _config.PendingRoleId);
                }
            }

            return null;
        }
        catch (HttpException exception)
        {
            var message = ExplainRoleUpdateFailure(exception);
            _sapi.Logger.Warning($"DavesDiscordUtilities could not update Discord roles for request {request.Id}: {message}");
            return message;
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not update Discord roles for request {request.Id}: {exception.Message}");
            return $"Dave could not update Discord roles: {exception.Message}";
        }
    }

    private static string ExplainRoleUpdateFailure(HttpException exception)
    {
        if (exception.HttpCode == HttpStatusCode.Forbidden || exception.Reason.Contains("Missing Access", StringComparison.OrdinalIgnoreCase))
        {
            return "Dave is missing access to update Discord roles. Check Manage Roles and make sure Dave's highest role is above the configured whitelist roles.";
        }

        return $"Discord rejected the role update: {exception.Reason ?? exception.Message}";
    }

    private async Task RenameApprovedMemberAsync(WhitelistRequest request)
    {
        request.NicknameUpdateError = null;

        if (!_config.RenameApprovedMember)
        {
            return;
        }

        if (_discord == null)
        {
            request.NicknameUpdateError = "Dave is not connected to Discord.";
            return;
        }

        var guild = _discord.Client.GetGuild(request.GuildId);
        if (guild == null)
        {
            request.NicknameUpdateError = "Dave cannot find the configured Discord server.";
            return;
        }

        var botUser = guild.CurrentUser;
        if (!botUser.GuildPermissions.Administrator && !botUser.GuildPermissions.ManageNicknames)
        {
            request.NicknameUpdateError = "Dave needs Manage Nicknames to set approved member nicknames.";
            _sapi.Logger.Warning($"DavesDiscordUtilities could not rename Discord member for request {request.Id}: {request.NicknameUpdateError}");
            return;
        }

        var requester = await GetGuildUserAsync(request);
        if (requester == null)
        {
            request.NicknameUpdateError = "Dave cannot find the approved Discord member.";
            return;
        }

        if (requester.Id != botUser.Id && botUser.Hierarchy <= requester.Hierarchy)
        {
            request.NicknameUpdateError = "Dave's highest role must be above the approved member before it can rename them.";
            _sapi.Logger.Warning($"DavesDiscordUtilities could not rename Discord member for request {request.Id}: {request.NicknameUpdateError}");
            return;
        }

        var nickname = BuildApprovedNickname(request);
        CapturePreviousDiscordNickname(request, requester);

        try
        {
            if (requester.Nickname != nickname)
            {
                await requester.ModifyAsync(properties => properties.Nickname = nickname);
            }

            request.AppliedNickname = nickname;
            request.NicknameUpdatedAtUtc = DateTime.UtcNow;
        }
        catch (HttpException exception)
        {
            request.NicknameUpdateError = ExplainNicknameUpdateFailure(exception);
            _sapi.Logger.Warning($"DavesDiscordUtilities could not rename Discord member for request {request.Id}: {request.NicknameUpdateError}");
        }
        catch (Exception exception)
        {
            request.NicknameUpdateError = $"Dave could not update the Discord nickname: {exception.Message}";
            _sapi.Logger.Warning($"DavesDiscordUtilities could not rename Discord member for request {request.Id}: {exception.Message}");
        }
    }

    private async Task CapturePreviousDiscordNicknameAsync(WhitelistRequest request)
    {
        if (!_config.RenameApprovedMember || request.PreviousDiscordNicknameCaptured || _discord == null)
        {
            return;
        }

        var requester = await GetGuildUserAsync(request);
        if (requester == null)
        {
            return;
        }

        CapturePreviousDiscordNickname(request, requester);
    }

    private static void CapturePreviousDiscordNickname(WhitelistRequest request, IGuildUser requester)
    {
        if (request.PreviousDiscordNicknameCaptured)
        {
            return;
        }

        request.PreviousDiscordNickname = requester.Nickname;
        request.PreviousDiscordNicknameCaptured = true;
    }

    private async Task RestoreApprovedNicknameAsync(WhitelistRequest request)
    {
        request.NicknameUpdateError = null;

        if (!_config.RenameApprovedMember || _discord == null)
        {
            return;
        }

        var guild = _discord.Client.GetGuild(request.GuildId);
        if (guild == null)
        {
            request.NicknameUpdateError = "Dave cannot find the configured Discord server.";
            return;
        }

        var botUser = guild.CurrentUser;
        if (!botUser.GuildPermissions.Administrator && !botUser.GuildPermissions.ManageNicknames)
        {
            request.NicknameUpdateError = "Dave needs Manage Nicknames to restore removed member nicknames.";
            _sapi.Logger.Warning($"DavesDiscordUtilities could not restore Discord nickname for request {request.Id}: {request.NicknameUpdateError}");
            return;
        }

        var requester = await GetGuildUserAsync(request);
        if (requester == null)
        {
            request.NicknameUpdateError = "Dave cannot find the removed Discord member.";
            return;
        }

        if (requester.Id != botUser.Id && botUser.Hierarchy <= requester.Hierarchy)
        {
            request.NicknameUpdateError = "Dave's highest role must be above the removed member before it can restore their nickname.";
            _sapi.Logger.Warning($"DavesDiscordUtilities could not restore Discord nickname for request {request.Id}: {request.NicknameUpdateError}");
            return;
        }

        var shouldRestoreNickname = request.PreviousDiscordNicknameCaptured || !string.IsNullOrWhiteSpace(request.AppliedNickname);
        if (!shouldRestoreNickname)
        {
            request.NicknameUpdatedAtUtc = null;
            return;
        }

        var restoredNickname = GetRestoredNickname(request);
        try
        {
            await requester.ModifyAsync(properties => properties.Nickname = restoredNickname);

            request.AppliedNickname = null;
            request.PreviousDiscordNickname = null;
            request.PreviousDiscordNicknameCaptured = false;
            request.NicknameUpdatedAtUtc = DateTime.UtcNow;
        }
        catch (HttpException exception)
        {
            request.NicknameUpdateError = ExplainNicknameUpdateFailure(exception);
            _sapi.Logger.Warning($"DavesDiscordUtilities could not restore Discord nickname for request {request.Id}: {request.NicknameUpdateError}");
        }
        catch (Exception exception)
        {
            request.NicknameUpdateError = $"Dave could not restore the Discord nickname: {exception.Message}";
            _sapi.Logger.Warning($"DavesDiscordUtilities could not restore Discord nickname for request {request.Id}: {exception.Message}");
        }
    }

    private static string? GetRestoredNickname(WhitelistRequest request)
    {
        if (request.PreviousDiscordNicknameCaptured)
        {
            return request.PreviousDiscordNickname;
        }

        var fallback = request.DiscordName.Trim();
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return null;
        }

        return fallback.Length <= 32 ? fallback : fallback[..32].TrimEnd();
    }

    private string BuildApprovedNickname(WhitelistRequest request)
    {
        var format = string.IsNullOrWhiteSpace(_config.ApprovedMemberNicknameFormat)
            ? "{playername}"
            : _config.ApprovedMemberNicknameFormat;
        var nickname = format
            .Replace("{playername}", request.PlayerName)
            .Replace("{discord}", request.DiscordName)
            .Replace("{discordid}", request.DiscordUserId.ToString())
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        nickname = Regex.Replace(nickname, @"\s+", " ");
        if (string.IsNullOrWhiteSpace(nickname))
        {
            nickname = request.PlayerName;
        }

        return nickname.Length <= 32 ? nickname : nickname[..32].TrimEnd();
    }

    private static string ExplainNicknameUpdateFailure(HttpException exception)
    {
        if (exception.HttpCode == HttpStatusCode.Forbidden || exception.Reason.Contains("Missing Access", StringComparison.OrdinalIgnoreCase))
        {
            return "Dave is missing access to update Discord nicknames. Check Manage Nicknames and make sure Dave's highest role is above the member.";
        }

        return $"Discord rejected the nickname update: {exception.Reason ?? exception.Message}";
    }

    private async Task ApplyTemporaryApplicantThreadStateAsync(WhitelistRequest request)
    {
        if (!_config.UseTemporaryApplicantThreads) return;

        if (IsRejectedForCleanup(request.Status))
        {
            await OpenTemporaryApplicantThreadAsync(request);
            return;
        }

        if (request.Status is DavesDiscordUtilitiesStatuses.Removed or DavesDiscordUtilitiesStatuses.Banned)
        {
            await DeleteTemporaryApplicantThreadAsync(request);
            return;
        }

        if (request.Status == DavesDiscordUtilitiesStatuses.Approved && _config.DeleteTemporaryThreadOnApproval)
        {
            await DeleteTemporaryApplicantThreadAsync(request);
            return;
        }

        if (request.Status == DavesDiscordUtilitiesStatuses.Approved)
        {
            await CloseTemporaryApplicantThreadAsync(request);
        }
    }

    private async Task<IThreadChannel?> OpenTemporaryApplicantThreadAsync(WhitelistRequest request)
    {
        if (!_config.UseTemporaryApplicantThreads) return null;

        var requester = await GetGuildUserAsync(request);
        if (requester == null) return null;

        var thread = await GetTemporaryApplicantThreadAsync(request);
        if (thread == null)
        {
            thread = await CreateTemporaryApplicantThreadAsync(request, requester);
            if (thread != null)
            {
                SaveRequests();
            }

            return thread;
        }

        try
        {
            await thread.ModifyAsync(properties =>
            {
                properties.Archived = false;
                properties.Locked = false;
            });

            await thread.AddUserAsync(requester);

            if (_discord?.Client.GetGuild(request.GuildId) is { } guild)
            {
                await AddApproversToThreadAsync(thread, guild, requester.Id);
            }

            request.CommunicationThreadError = null;
        }
        catch (HttpException exception)
        {
            request.CommunicationThreadError = exception.Reason ?? exception.Message;
            _sapi.Logger.Warning($"DavesDiscordUtilities could not open temporary applicant thread {GetCommunicationThreadId(request)}: {request.CommunicationThreadError}");
        }
        catch (Exception exception)
        {
            request.CommunicationThreadError = exception.Message;
            _sapi.Logger.Warning($"DavesDiscordUtilities could not open temporary applicant thread {GetCommunicationThreadId(request)}: {request.CommunicationThreadError}");
        }

        return thread;
    }

    private async Task CloseTemporaryApplicantThreadAsync(WhitelistRequest request)
    {
        var threadId = GetCommunicationThreadId(request);
        if (threadId == 0) return;

        var thread = await GetTemporaryApplicantThreadAsync(request);
        if (thread == null)
        {
            return;
        }

        try
        {
            await thread.ModifyAsync(properties =>
            {
                properties.Archived = false;
                properties.Locked = false;
            });

            var requester = await GetGuildUserAsync(request);
            if (requester != null)
            {
                try
                {
                    await thread.RemoveUserAsync(requester);
                }
                catch (HttpException exception)
                {
                    _sapi.Logger.Debug($"DavesDiscordUtilities could not remove applicant {request.DiscordUserId} from temporary thread {threadId}: {exception.Reason ?? exception.Message}");
                }
                catch (Exception exception)
                {
                    _sapi.Logger.Debug($"DavesDiscordUtilities could not remove applicant {request.DiscordUserId} from temporary thread {threadId}: {exception.Message}");
                }
            }

            if (_discord?.Client.GetGuild(request.GuildId) is { } guild)
            {
                await AddApproversToThreadAsync(thread, guild, request.DiscordUserId);
            }

            await thread.ModifyAsync(properties =>
            {
                properties.Locked = true;
                properties.Archived = true;
            });

            request.CommunicationThreadError = null;
        }
        catch (HttpException exception)
        {
            request.CommunicationThreadError = exception.Reason ?? exception.Message;
            _sapi.Logger.Warning($"DavesDiscordUtilities could not close temporary applicant thread {threadId}: {request.CommunicationThreadError}");
        }
        catch (Exception exception)
        {
            request.CommunicationThreadError = exception.Message;
            _sapi.Logger.Warning($"DavesDiscordUtilities could not close temporary applicant thread {threadId}: {request.CommunicationThreadError}");
        }
    }

    private async Task DeleteTemporaryApplicantThreadAsync(WhitelistRequest request)
    {
        var threadId = GetCommunicationThreadId(request);
        if (threadId == 0) return;

        try
        {
            var thread = await GetTemporaryApplicantThreadAsync(request);
            if (thread != null)
            {
                await thread.DeleteAsync();
            }

            ClearCommunicationThreadId(request);
            SaveRequests();
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not delete temporary applicant thread {threadId}: {exception.Message}");
        }
    }

    private async Task CleanupRejectedApplicantThreadsAsync()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, _config.RejectedThreadCleanupHours));
            var targets = _requests.Values
                .Where(request =>
                    IsRejectedForCleanup(request.Status) &&
                    request.RejectedThreadOpenedAtUtc != null &&
                    request.RejectedThreadOpenedAtUtc <= cutoff &&
                    request.ApplicantRespondedAtUtc == null &&
                    GetCommunicationThreadId(request) != 0)
                .ToList();

            foreach (var request in targets)
            {
                await DeleteTemporaryApplicantThreadAsync(request);
                request.Status = DavesDiscordUtilitiesStatuses.Removed;
                request.LastUpdatedAtUtc = DateTime.UtcNow;
                request.RejectedThreadOpenedAtUtc = null;
                request.ApplicantRespondedAtUtc = null;
                _sapi.Logger.Notification($"DavesDiscordUtilities removed unanswered rejected applicant thread for request {request.Id}.");

                try
                {
                    await RefreshReviewCardAsync(request);
                }
                catch (Exception exception)
                {
                    _sapi.Logger.Debug($"Could not refresh DavesDiscordUtilities review card {request.Id} after thread cleanup: {exception.Message}");
                }
            }

            var verificationChanged = await CleanupExpiredStrictIdentityVerificationsAsync();

            if (targets.Count > 0 || verificationChanged)
            {
                SaveRequests();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _rejectedThreadCleanupQueued, 0);
        }
    }

    private async Task<bool> CleanupExpiredStrictIdentityVerificationsAsync()
    {
        if (!IdentityVerificationIsStrict() || !Th3IdentityAuthIsEnabled()) return false;

        var now = DateTime.UtcNow;
        var targets = _requests.Values
            .Where(request =>
                request.Status == DavesDiscordUtilitiesStatuses.Approved &&
                request.IdentityVerifiedAtUtc == null &&
                request.IdentityVerificationDueAtUtc != null &&
                request.IdentityVerificationDueAtUtc <= now)
            .ToList();

        var changed = false;
        foreach (var request in targets)
        {
            var decisionLock = GetRequestDecisionLock(request.Id);
            if (!await decisionLock.WaitAsync(0)) continue;

            try
            {
                changed |= UpdateIdentityVerificationState(request);
                if (request.IdentityVerifiedAtUtc != null) continue;

                var actionError = await RemoveApprovedAccessAsync(
                    request,
                    removeDiscordRole: _config.StrictVerificationRemovesDiscordRole,
                    removeGameWhitelist: _config.StrictVerificationRemovesGameWhitelist,
                    auditName: "Dave",
                    auditDiscordUserId: null);

                if (actionError != null)
                {
                    _sapi.Logger.Warning($"DavesDiscordUtilities could not revoke expired strict identity verification for request {request.Id}: {actionError}");
                    continue;
                }

                request.Status = DavesDiscordUtilitiesStatuses.Revoked;
                request.IdentityVerificationExpiredAtUtc = now;
                request.RejectedThreadOpenedAtUtc = now;
                request.ApplicantRespondedAtUtc = null;
                request.DecidedByDiscordUserId = null;
                request.DecidedByName = "Dave";
                request.DecidedAtUtc = now;
                changed = true;

                await CompleteReviewActionAfterResponseAsync(request);
                _sapi.Logger.Audit($"Dave revoked Discord whitelist request {request.Id} for {request.PlayerName} because strict identity verification expired.");
            }
            finally
            {
                decisionLock.Release();
            }
        }

        return changed;
    }

    private async Task<IGuildUser?> GetGuildUserAsync(WhitelistRequest request)
    {
        if (_discord == null) return null;

        var guild = _discord.Client.GetGuild(request.GuildId);
        if (guild?.GetUser(request.DiscordUserId) is { } socketUser)
        {
            return socketUser;
        }

        return await _discord.Client.Rest.GetGuildUserAsync(request.GuildId, request.DiscordUserId);
    }

    private async Task RefreshReviewCardsForPlayerAsync(string playerUid, string playerName)
    {
        if (_discord == null || _config == null) return;

        var requests = _requests.Values
            .Where(request =>
                request.ReviewMessageId != 0 &&
                (request.PlayerUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase) ||
                 request.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var request in requests)
        {
            try
            {
                await RefreshReviewCardAsync(request);
            }
            catch (Exception exception)
            {
                _sapi.Logger.Debug($"Could not refresh DavesDiscordUtilities review card {request.Id}: {exception.Message}");
            }
        }
    }

    private async Task RefreshReviewCardAsync(WhitelistRequest request)
    {
        var channel = await GetReviewMessageChannelAsync(request);
        if (channel == null) return;

        if (UpdateIdentityVerificationState(request))
        {
            SaveRequests();
        }

        if (await channel.GetMessageAsync(request.ReviewMessageId) is IUserMessage message)
        {
            await message.ModifyAsync(properties =>
            {
                properties.Content = BuildReviewMessage(request);
                properties.Components = BuildReviewComponents(request);
            });
        }
    }

    private async Task<IMessageChannel?> GetReviewMessageChannelAsync(WhitelistRequest request)
    {
        if (_discord == null) return null;

        var channelId = request.ReviewChannelId == 0 ? _config.ReviewChannelId : request.ReviewChannelId;
        if (_discord.Client.GetChannel(channelId) is IMessageChannel socketChannel)
        {
            return socketChannel;
        }

        try
        {
            return await _discord.Client.Rest.GetChannelAsync(channelId) as IMessageChannel;
        }
        catch (Exception exception)
        {
            _sapi.Logger.Debug($"Could not fetch DavesDiscordUtilities review channel {channelId}: {exception.Message}");
            return null;
        }
    }

    private async Task<IThreadChannel?> GetTemporaryApplicantThreadAsync(WhitelistRequest request)
    {
        if (_discord == null) return null;

        var threadId = GetCommunicationThreadId(request);
        if (threadId == 0) return null;

        if (_discord.Client.GetChannel(threadId) is IThreadChannel socketThread)
        {
            return socketThread;
        }

        try
        {
            var thread = await _discord.Client.Rest.GetChannelAsync(threadId) as IThreadChannel;
            if (thread != null)
            {
                request.CommunicationThreadError = null;
            }

            return thread;
        }
        catch (HttpException exception)
        {
            request.CommunicationThreadError = exception.Reason ?? exception.Message;
            _sapi.Logger.Debug($"Could not fetch DavesDiscordUtilities temporary applicant thread {threadId}: {request.CommunicationThreadError}");
            return null;
        }
        catch (Exception exception)
        {
            request.CommunicationThreadError = exception.Message;
            _sapi.Logger.Debug($"Could not fetch DavesDiscordUtilities temporary applicant thread {threadId}: {request.CommunicationThreadError}");
            return null;
        }
    }

    private static ulong GetCommunicationThreadId(WhitelistRequest request)
    {
        return request.CommunicationThreadId != 0 ? request.CommunicationThreadId : request.ReviewThreadId;
    }

    private static void ClearCommunicationThreadId(WhitelistRequest request)
    {
        request.CommunicationThreadId = 0;
        request.ReviewThreadId = 0;
        request.CommunicationThreadError = null;
    }

    private async Task NotifyRequesterAsync(WhitelistRequest request, string text, bool notifyInThread = true)
    {
        if (_discord == null) return;

        if (notifyInThread)
        {
            var thread = await GetTemporaryApplicantThreadAsync(request);
            if (thread != null)
            {
                try
                {
                    await thread.SendMessageAsync($"<@{request.DiscordUserId}> {text}");
                }
                catch (Exception exception)
                {
                    _sapi.Logger.Debug($"Could not send DavesDiscordUtilities thread notification for Discord user {request.DiscordUserId}: {exception.Message}");
                }
            }
        }

        try
        {
            IUser? user = _discord.Client.GetUser(request.DiscordUserId);
            user ??= await _discord.Client.Rest.GetUserAsync(request.DiscordUserId);
            if (user == null) return;

            await user.SendMessageAsync(text);
        }
        catch (Exception exception)
        {
            _sapi.Logger.Debug($"Could not send DavesDiscordUtilities direct message to Discord user {request.DiscordUserId}: {exception.Message}");
        }
    }

    private async Task UpdateSlashResponseAsync(SocketSlashCommand command, string message)
    {
        try
        {
            await command.ModifyOriginalResponseAsync(properties => properties.Content = message);
        }
        catch (HttpException exception)
        {
            if (IsUnknownInteraction(exception))
            {
                return;
            }

            _sapi.Logger.Warning($"DavesDiscordUtilities could not update /{command.Data.Name} response: {exception.Reason ?? exception.Message}");
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not update /{command.Data.Name} response: {exception.Message}");
        }
    }

    private async Task UpdateComponentResponseAsync(SocketMessageComponent component, string message)
    {
        try
        {
            await component.FollowupAsync(message, ephemeral: true);
        }
        catch (HttpException exception)
        {
            if (IsUnknownInteraction(exception))
            {
                return;
            }

            _sapi.Logger.Warning($"DavesDiscordUtilities could not send review button response: {exception.Reason ?? exception.Message}");
        }
        catch (Exception exception)
        {
            _sapi.Logger.Warning($"DavesDiscordUtilities could not send review button response: {exception.Message}");
        }
    }

    private static bool IsUnknownInteraction(HttpException exception)
    {
        return (exception.Reason?.Contains("Unknown interaction", StringComparison.OrdinalIgnoreCase) ?? false) ||
               exception.Message.Contains("10062", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("Unknown interaction", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasApprovalPermission(IGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        return _config.ApproverRoleIds != null && user.RoleIds.Any(roleId => _config.ApproverRoleIds.Contains(roleId));
    }

    private bool IsConfigured()
    {
        return _config.Enabled && _discord != null && _config.RequestChannelId != 0 && _config.ReviewChannelId != 0;
    }

    private bool ReviewChannelExposesRequests(IMessageChannel channel, out string reason)
    {
        reason = "";
        if (channel is not SocketGuildChannel guildChannel) return false;

        if (RoleCanViewChannel(guildChannel, guildChannel.Guild.EveryoneRole))
        {
            reason = "@everyone can view the channel";
            return true;
        }

        if (_config.PendingRoleId != 0 &&
            guildChannel.Guild.GetRole(_config.PendingRoleId) is { } pendingRole &&
            RoleCanViewChannel(guildChannel, pendingRole))
        {
            reason = $"pending role {pendingRole.Name} can view the channel";
            return true;
        }

        if (_config.ApprovedRoleId != 0 &&
            guildChannel.Guild.GetRole(_config.ApprovedRoleId) is { } approvedRole &&
            RoleCanViewChannel(guildChannel, approvedRole))
        {
            reason = $"approved role {approvedRole.Name} can view the channel";
            return true;
        }

        var allowedRoleIds = new HashSet<ulong>(_config.ApproverRoleIds ?? Enumerable.Empty<ulong>());
        if (guildChannel.Guild.CurrentUser != null)
        {
            foreach (var role in guildChannel.Guild.CurrentUser.Roles)
            {
                allowedRoleIds.Add(role.Id);
            }
        }

        foreach (var role in guildChannel.Guild.Roles)
        {
            if (role.Id == guildChannel.Guild.EveryoneRole.Id ||
                role.Permissions.Administrator ||
                allowedRoleIds.Contains(role.Id))
            {
                continue;
            }

            if (RoleCanViewChannel(guildChannel, role))
            {
                reason = $"role {role.Name} can view the channel";
                return true;
            }
        }

        if (ChannelHasUnsafeMemberViewOverwrite(guildChannel, out reason))
        {
            return true;
        }

        if (guildChannel is SocketTextChannel { Category: SocketGuildChannel category } &&
            ChannelHasUnsafeMemberViewOverwrite(category, out reason))
        {
            return true;
        }

        return false;
    }

    private static bool ChannelHasUnsafeMemberViewOverwrite(SocketGuildChannel channel, out string reason)
    {
        reason = "";
        foreach (var overwrite in channel.PermissionOverwrites)
        {
            if (overwrite.TargetType != PermissionTarget.User ||
                overwrite.Permissions.ViewChannel != PermValue.Allow ||
                overwrite.TargetId == channel.Guild.CurrentUser?.Id)
            {
                continue;
            }

            reason = $"member-specific view override exists for user {overwrite.TargetId}";
            return true;
        }

        return false;
    }

    private static bool RoleCanViewChannel(SocketGuildChannel channel, SocketRole role)
    {
        var everyoneRole = channel.Guild.EveryoneRole;
        var visible = everyoneRole.Permissions.ViewChannel || role.Permissions.ViewChannel;

        if (channel is SocketTextChannel { Category: { } category })
        {
            ApplyViewChannelOverwrite(category.GetPermissionOverwrite(everyoneRole), ref visible);
            if (role.Id != everyoneRole.Id)
            {
                ApplyViewChannelOverwrite(category.GetPermissionOverwrite(role), ref visible);
            }
        }

        ApplyViewChannelOverwrite(channel.GetPermissionOverwrite(everyoneRole), ref visible);
        if (role.Id != everyoneRole.Id)
        {
            ApplyViewChannelOverwrite(channel.GetPermissionOverwrite(role), ref visible);
        }

        return visible;
    }

    private static void ApplyViewChannelOverwrite(OverwritePermissions? overwrite, ref bool visible)
    {
        if (overwrite == null) return;

        visible = overwrite.Value.ViewChannel switch
        {
            PermValue.Allow => true,
            PermValue.Deny => false,
            _ => visible
        };
    }

    private string BuildReviewMessage(WhitelistRequest request)
    {
        var decidedLine = request.DecidedByDiscordUserId == null
            ? "Decision: pending"
            : $"Decision: {request.Status.ToLowerInvariant()} by {request.DecidedByName} ({request.DecidedByDiscordUserId})";
        var requestText = string.IsNullOrWhiteSpace(request.RequestText) ? "(no message provided)" : SafeBlock(request.RequestText);
        var whitelistStatus = IsAlreadyWhitelisted(request.PlayerName, request.PlayerUid) ? "Whitelisted" : "Not whitelisted";
        var lastOnline = GetLastOnlineText(request);
        var requestType = request.ExistingWhitelistClaim ? "Existing server whitelist claim" : "New whitelist request";
        var applicantThread = FormatApplicantThread(request);
        var playerClass = GetPlayerClassText(request);
        var identityVerification = GetIdentityVerificationText(request);
        var lines = new List<string>
        {
            "**Whitelist review**",
            $"ID: `{request.Id}` | Status: {request.Status}",
            $"Type: {requestType}",
            $"Player: `{SafeInline(request.PlayerName)}` (`{SafeInline(request.PlayerUid)}`)",
            $"Class: {playerClass}",
            $"Discord: <@{request.DiscordUserId}> (`{request.DiscordUserId}`, {SafeInline(request.DiscordName ?? "")})",
            $"Whitelist: {whitelistStatus} | Last online: {lastOnline}"
        };

        if (identityVerification != null)
        {
            lines.Add($"Identity: {identityVerification}");
        }

        lines.Add($"Thread: {applicantThread}");
        lines.Add($"Submitted: {request.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        lines.Add(decidedLine);
        lines.Add(request.ExistingWhitelistClaim
            ? "Note: existing server whitelist claim; approval verifies Discord access only."
            : "Note: Dave verifies the Vintage Story account exists, not Discord ownership.");

        if (request.LastUpdatedAtUtc != null)
        {
            lines.Add($"Correction: {GetCorrectionStatusText(request)}");
        }

        if (request.Status == DavesDiscordUtilitiesStatuses.Banned)
        {
            lines.Add("Restriction: cannot submit new whitelist requests");
        }

        if (!string.IsNullOrWhiteSpace(request.NicknameUpdateError))
        {
            lines.Add($"Nickname warning: {SafeInline(request.NicknameUpdateError)}");
        }

        lines.Add("");
        lines.Add("Message:");
        lines.Add(requestText);

        return string.Join("\n", lines);
    }

    private string FormatApplicantThread(WhitelistRequest request)
    {
        var applicantThreadId = GetCommunicationThreadId(request);
        if (applicantThreadId == 0)
        {
            return string.IsNullOrWhiteSpace(request.CommunicationThreadError)
                ? "none"
                : $"none (access warning: {SafeInline(request.CommunicationThreadError)})";
        }

        var state = request.Status switch
        {
            DavesDiscordUtilitiesStatuses.Approved => "archived",
            DavesDiscordUtilitiesStatuses.Denied or DavesDiscordUtilitiesStatuses.Revoked => BuildRejectedThreadState(request),
            DavesDiscordUtilitiesStatuses.Removed => "removed",
            DavesDiscordUtilitiesStatuses.Banned => "removed; banned",
            DavesDiscordUtilitiesStatuses.Unbanned => "removed; unbanned",
            _ => "open to applicant"
        };

        if (!string.IsNullOrWhiteSpace(request.CommunicationThreadError))
        {
            state += $"; access warning: {SafeInline(request.CommunicationThreadError)}";
        }

        return $"<#{applicantThreadId}> ({state})";
    }

    private string BuildRejectedThreadState(WhitelistRequest request)
    {
        if (request.ApplicantRespondedAtUtc != null)
        {
            return $"open; applicant responded at {request.ApplicantRespondedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }

        return request.RejectedThreadOpenedAtUtc == null
            ? "open to applicant"
            : $"open; cleanup after {request.RejectedThreadOpenedAtUtc.Value.AddHours(Math.Max(1, _config.RejectedThreadCleanupHours)):yyyy-MM-dd HH:mm:ss} UTC";
    }

    private static string GetCorrectionStatusText(WhitelistRequest request)
    {
        if (request.LastUpdatedAtUtc == null)
        {
            return "None";
        }

        var previous = string.IsNullOrWhiteSpace(request.PreviousPlayerName)
            ? ""
            : $" (previous username: `{SafeInline(request.PreviousPlayerName)}`)";
        return $"{request.LastUpdatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC{previous}";
    }

    private string GetLastOnlineText(WhitelistRequest request)
    {
        var onlinePlayer = _sapi.World.AllOnlinePlayers.FirstOrDefault(player => player.PlayerUID == request.PlayerUid);
        if (onlinePlayer != null) return "Online now";

        var activity = FindPlayerActivity(request);
        if (activity != null)
        {
            if (activity.IsOnline) return "Online now";
            if (activity.LastLeaveUtc != null) return $"{activity.LastLeaveUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
            if (activity.LastJoinUtc != null) return $"{activity.LastJoinUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
        }

        var playerData = _sapi.PlayerData.GetPlayerDataByUid(request.PlayerUid) ??
                         _sapi.PlayerData.GetPlayerDataByLastKnownName(request.PlayerName);
        if (playerData == null) return "Unknown";

        if (TryReadDateMember(playerData, out var dateTime, "LastOnline", "LastSeen", "LastLogin", "LastLogout", "LastPlayed", "LastConnection"))
        {
            return $"{dateTime.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC";
        }

        if (TryReadLongMember(playerData, out var unixTime, "LastOnline", "LastSeen", "LastLogin", "LastLogout", "LastPlayed", "LastConnection"))
        {
            try
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
                return $"{date:yyyy-MM-dd HH:mm:ss} UTC";
            }
            catch
            {
                return "Unknown";
            }
        }

        return "Unknown";
    }

    private string GetPlayerClassText(WhitelistRequest request)
    {
        var classCode = FindPlayerClassCode(request);
        if (string.IsNullOrWhiteSpace(classCode))
        {
            return "Unknown";
        }

        return $"{FormatClassCode(classCode)} (`{SafeInline(classCode)}`)";
    }

    private string? GetIdentityVerificationText(WhitelistRequest request)
    {
        var mode = GetIdentityVerificationMode();
        if (mode == DavesDiscordUtilitiesIdentityVerificationModes.Off)
        {
            return null;
        }

        var th3AuthEnabled = Th3IdentityAuthIsEnabled();
        if (mode == DavesDiscordUtilitiesIdentityVerificationModes.TrackWhenAvailable && !th3AuthEnabled)
        {
            return null;
        }

        if (mode == DavesDiscordUtilitiesIdentityVerificationModes.Strict && !th3AuthEnabled)
        {
            return "unavailable; enable Th3Essentials rewards/auth";
        }

        ulong linkedDiscordUserId = 0;
        var hasCurrentLink = _discord != null && _discord.TryGetLinkedDiscordUserId(request.PlayerUid, out linkedDiscordUserId);
        if (hasCurrentLink && linkedDiscordUserId != request.DiscordUserId)
        {
            return $"linked to another Discord user (`{linkedDiscordUserId}`)";
        }

        if (hasCurrentLink || request.IdentityVerifiedAtUtc != null)
        {
            return "verified via Th3Essentials";
        }

        if (request.IdentityVerificationExpiredAtUtc != null)
        {
            return $"expired at {request.IdentityVerificationExpiredAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }

        if (mode == DavesDiscordUtilitiesIdentityVerificationModes.Strict && request.IdentityVerificationDueAtUtc != null)
        {
            return $"unverified, due by {request.IdentityVerificationDueAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }

        return "unverified";
    }

    private bool UpdateIdentityVerificationState(WhitelistRequest request)
    {
        if (!IdentityVerificationCanReadTh3Auth())
        {
            return false;
        }

        var changed = false;
        if (_discord!.TryGetLinkedDiscordUserId(request.PlayerUid, out var linkedDiscordUserId))
        {
            if (request.IdentityLinkedDiscordUserId != linkedDiscordUserId)
            {
                request.IdentityLinkedDiscordUserId = linkedDiscordUserId;
                changed = true;
            }

            if (linkedDiscordUserId == request.DiscordUserId && request.IdentityVerifiedAtUtc == null)
            {
                request.IdentityVerifiedAtUtc = DateTime.UtcNow;
                request.IdentityVerificationDueAtUtc = null;
                request.IdentityVerificationExpiredAtUtc = null;
                changed = true;
            }
        }
        else if (request.IdentityLinkedDiscordUserId != null)
        {
            request.IdentityLinkedDiscordUserId = null;
            changed = true;
        }

        if (IdentityVerificationIsStrict() &&
            request.Status == DavesDiscordUtilitiesStatuses.Approved &&
            request.IdentityVerifiedAtUtc == null &&
            request.IdentityVerificationDueAtUtc == null)
        {
            ApplyStrictIdentityVerificationDeadline(request);
            changed = true;
        }

        return changed;
    }

    private void ApplyStrictIdentityVerificationDeadline(WhitelistRequest request)
    {
        if (!IdentityVerificationIsStrict() || request.Status != DavesDiscordUtilitiesStatuses.Approved)
        {
            return;
        }

        if (request.IdentityVerifiedAtUtc != null)
        {
            request.IdentityVerificationDueAtUtc = null;
            return;
        }

        request.IdentityVerificationDueAtUtc ??= DateTime.UtcNow.AddHours(Math.Max(1, _config.IdentityVerificationGraceHours));
    }

    private void ClearIdentityVerificationState(WhitelistRequest request)
    {
        request.IdentityVerifiedAtUtc = null;
        request.IdentityVerificationDueAtUtc = null;
        request.IdentityVerificationExpiredAtUtc = null;
        request.IdentityLinkedDiscordUserId = null;
    }

    private bool IdentityVerificationCanReadTh3Auth()
    {
        return _discord != null &&
               Th3IdentityAuthIsEnabled() &&
               GetIdentityVerificationMode() != DavesDiscordUtilitiesIdentityVerificationModes.Off;
    }

    private bool Th3IdentityAuthIsEnabled()
    {
        return _discord?.RewardsEnabled == true;
    }

    private bool IdentityVerificationIsStrict()
    {
        return GetIdentityVerificationMode() == DavesDiscordUtilitiesIdentityVerificationModes.Strict;
    }

    private string GetIdentityVerificationMode()
    {
        var configured = _config.IdentityVerificationMode?.Trim();
        if (configured != null && configured.Equals(DavesDiscordUtilitiesIdentityVerificationModes.Off, StringComparison.OrdinalIgnoreCase))
        {
            return DavesDiscordUtilitiesIdentityVerificationModes.Off;
        }

        if (configured != null && configured.Equals(DavesDiscordUtilitiesIdentityVerificationModes.Strict, StringComparison.OrdinalIgnoreCase))
        {
            return DavesDiscordUtilitiesIdentityVerificationModes.Strict;
        }

        return DavesDiscordUtilitiesIdentityVerificationModes.TrackWhenAvailable;
    }

    private string? GetStrictIdentityVerificationConfigurationError()
    {
        if (!IdentityVerificationIsStrict() || Th3IdentityAuthIsEnabled())
        {
            return null;
        }

        return "Dave is configured for strict identity verification, but Th3Essentials auth is not enabled. Enable Th3Essentials DiscordConfig.Rewards, restart the server, then use `/auth mode:connect` and `/dcauth` for account linking.";
    }

    private string? FindPlayerClassCode(WhitelistRequest request)
    {
        foreach (var player in _sapi.World.AllOnlinePlayers)
        {
            if (PlayerMatchesRequest(player, request))
            {
                var classCode = ReadPlayerClassCode(player);
                if (!string.IsNullOrWhiteSpace(classCode)) return classCode;
            }
        }

        foreach (var player in _sapi.World.AllPlayers)
        {
            if (PlayerMatchesRequest(player, request))
            {
                var classCode = ReadPlayerClassCode(player);
                if (!string.IsNullOrWhiteSpace(classCode)) return classCode;
            }
        }

        var activity = FindPlayerActivity(request);
        return string.IsNullOrWhiteSpace(activity?.PlayerClassCode) ? null : activity.PlayerClassCode;
    }

    private static bool PlayerMatchesRequest(IPlayer player, WhitelistRequest request)
    {
        return player.PlayerUID == request.PlayerUid ||
               (!string.IsNullOrWhiteSpace(player.PlayerName) &&
                player.PlayerName.Equals(request.PlayerName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadPlayerClassCode(IPlayer player)
    {
        try
        {
            var classCode = player.Entity?.WatchedAttributes?.GetString(CharacterClassAttribute, "");
            return string.IsNullOrWhiteSpace(classCode) ? null : classCode.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatClassCode(string classCode)
    {
        var parts = classCode
            .Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(FormatClassCodePart);

        var display = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(display) ? SafeInline(classCode) : display;
    }

    private static string FormatClassCodePart(string part)
    {
        return part.Length == 0
            ? part
            : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
    }

    private PlayerActivityRecord? FindPlayerActivity(WhitelistRequest request)
    {
        if (_activity.TryGetValue(request.PlayerUid, out var byUid))
        {
            return byUid;
        }

        return _activity.Values.FirstOrDefault(record =>
            !string.IsNullOrWhiteSpace(record.PlayerName) &&
            record.PlayerName.Equals(request.PlayerName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadDateMember(object instance, out DateTime value, params string[] names)
    {
        value = default;
        var type = instance.GetType();
        foreach (var name in names)
        {
            var raw = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) ??
                      type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);

            if (raw is DateTime dateTime)
            {
                value = dateTime;
                return true;
            }

            if (raw is DateTimeOffset dateTimeOffset)
            {
                value = dateTimeOffset.UtcDateTime;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLongMember(object instance, out long value, params string[] names)
    {
        value = 0;
        var type = instance.GetType();
        foreach (var name in names)
        {
            var raw = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) ??
                      type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);

            switch (raw)
            {
                case long longValue:
                    value = longValue;
                    return true;
                case int intValue:
                    value = intValue;
                    return true;
            }
        }

        return false;
    }

    private string GetDecisionMessage(WhitelistRequest request, bool includeRejectedThreadNotice = false)
    {
        var message = request.Status switch
        {
            DavesDiscordUtilitiesStatuses.Approved => string.Format(_config.ApprovedMessage, request.PlayerName),
            DavesDiscordUtilitiesStatuses.Denied => string.Format(_config.DeniedMessage, request.PlayerName),
            DavesDiscordUtilitiesStatuses.Revoked when request.IdentityVerificationExpiredAtUtc != null => string.Format(_config.StrictVerificationExpiredMessage, request.PlayerName),
            DavesDiscordUtilitiesStatuses.Revoked => string.Format(_config.RevokedMessage, request.PlayerName),
            DavesDiscordUtilitiesStatuses.Removed => string.Format(_config.RemovedMessage, request.PlayerName),
            DavesDiscordUtilitiesStatuses.Banned => string.Format(_config.BannedMessage, request.PlayerName),
            DavesDiscordUtilitiesStatuses.Unbanned => string.Format(_config.UnbannedMessage, request.PlayerName),
            _ => string.Format(_config.RequestReceivedMessage, request.PlayerName)
        };

        if (request.Status == DavesDiscordUtilitiesStatuses.Approved &&
            request.IdentityVerifiedAtUtc == null &&
            request.IdentityVerificationDueAtUtc != null)
        {
            message += "\n" + string.Format(
                _config.StrictVerificationApprovedMessage,
                Math.Max(1, _config.IdentityVerificationGraceHours),
                $"{request.IdentityVerificationDueAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (includeRejectedThreadNotice && IsRejectedForCleanup(request.Status))
        {
            message += $"\n{_config.RejectedThreadNoResponseMessage}";
        }

        return message;
    }

    private static MessageComponent BuildReviewComponents(WhitelistRequest request)
    {
        var builder = new ComponentBuilder();

        if (request.Status == DavesDiscordUtilitiesStatuses.Banned)
        {
            builder.WithButton("Unban", $"{ButtonPrefix}:{UnbanAction}:{request.Id}", ButtonStyle.Success);
            return builder.Build();
        }

        if (request.Status != DavesDiscordUtilitiesStatuses.Approved)
        {
            builder.WithButton("Add", $"{ButtonPrefix}:{AddAction}:{request.Id}", ButtonStyle.Success, ApproveEmoji);
        }

        if (request.Status is DavesDiscordUtilitiesStatuses.Pending or DavesDiscordUtilitiesStatuses.Approved)
        {
            builder.WithButton("Revoke", $"{ButtonPrefix}:{RevokeAction}:{request.Id}", ButtonStyle.Danger, DenyEmoji);
        }

        if (IsRejectedForCleanup(request.Status) && GetCommunicationThreadId(request) != 0)
        {
            builder.WithButton("Remove", $"{ButtonPrefix}:{RemoveAction}:{request.Id}", ButtonStyle.Secondary);
        }

        builder.WithButton("Ban", $"{ButtonPrefix}:{BanAction}:{request.Id}", ButtonStyle.Danger);

        return builder.Build();
    }

    private static string NewRequestId()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }

    private static string GetDisplayName(SocketUser user)
    {
        return user is SocketGuildUser guildUser ? guildUser.DisplayName : user.GlobalName ?? user.Username;
    }

    private static string SafeInline(string value)
    {
        var safe = value
            .Replace("@", "@\u200B")
            .Replace("<", "")
            .Replace(">", "")
            .Replace("`", "'");

        return safe.Length <= 80 ? safe : safe[..80];
    }

    private static string SafeBlock(string value)
    {
        var safe = value
            .Replace("@", "@\u200B")
            .Replace("<", "")
            .Replace(">", "")
            .Replace("```", "'''")
            .Trim();

        if (safe.Length > 600)
        {
            safe = safe[..600] + "...";
        }

        return $"```{safe}```";
    }

    private void SaveConfig()
    {
        StoreDduModConfig(_config, ConfigFile);
    }

    private void SaveRequests()
    {
        StoreDduModConfig(_requests, RequestsFile);
    }

    private void SavePlayerActivity()
    {
        StoreDduModConfig(_activity, ActivityFile);
    }

    private void SaveBotStatusRecord()
    {
        StoreDduModConfig(_botStatusRecord, BotStatusFile);
    }

    private void StoreDduModConfig<T>(T value, string fileName)
    {
        EnsureConfigFolder();
        _sapi.StoreModConfig(value, fileName);
    }

    private enum ReviewCardRepairStatus
    {
        Refreshed,
        Reposted,
        Failed
    }

    private readonly record struct ReviewCardRepairResult(ReviewCardRepairStatus Status, string? Error)
    {
        public static ReviewCardRepairResult Refreshed() => new(ReviewCardRepairStatus.Refreshed, null);
        public static ReviewCardRepairResult Reposted() => new(ReviewCardRepairStatus.Reposted, null);
        public static ReviewCardRepairResult Failed(string error) => new(ReviewCardRepairStatus.Failed, error);
    }

    private sealed class PlayerResolveResponse
    {
        public string? playeruid { get; set; }
    }

    private sealed class Th3DiscordBridge
    {
        private readonly object _instance;
        private readonly EventInfo _createSlashCommandEvent;

        private Th3DiscordBridge(object instance, DiscordSocketClient client, EventInfo createSlashCommandEvent)
        {
            _instance = instance;
            Client = client;
            _createSlashCommandEvent = createSlashCommandEvent;
        }

        public DiscordSocketClient Client { get; }

        public bool RewardsEnabled => TryReadBoolConfigValue("Rewards", out var enabled) && enabled;

        public bool TryGetLinkedDiscordUserId(string playerUid, out ulong discordUserId)
        {
            discordUserId = 0;
            if (string.IsNullOrWhiteSpace(playerUid)) return false;

            try
            {
                var linkedAccounts = ReadConfigMember("LinkedAccounts");
                if (linkedAccounts is not IEnumerable entries) return false;

                foreach (var entry in entries)
                {
                    var entryType = entry.GetType();
                    var rawKey = entryType.GetProperty("Key")?.GetValue(entry)?.ToString();
                    if (!playerUid.Equals(rawKey, StringComparison.OrdinalIgnoreCase)) continue;

                    var rawValue = entryType.GetProperty("Value")?.GetValue(entry)?.ToString();
                    return ulong.TryParse(rawValue, out discordUserId);
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public static bool TryCreate(object instance, ICoreServerAPI sapi, out Th3DiscordBridge bridge)
        {
            bridge = null!;

            var type = instance.GetType();
            var client = type.GetProperty("Client", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as DiscordSocketClient;
            var createSlashCommandEvent = type.GetEvent("OnCreateSlashCommand", BindingFlags.Public | BindingFlags.Instance);

            if (client == null || createSlashCommandEvent?.EventHandlerType == null)
            {
                sapi.Logger.Error("DavesDiscordUtilities could not read Th3Essentials Discord client.");
                return false;
            }

            bridge = new Th3DiscordBridge(instance, client, createSlashCommandEvent);
            return true;
        }

        private bool TryReadBoolConfigValue(string name, out bool value)
        {
            value = false;
            var raw = ReadConfigMember(name);
            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            return false;
        }

        private object? ReadConfigMember(string name)
        {
            var config = _instance.GetType()
                .GetField("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(_instance);
            if (config == null) return null;

            var configType = config.GetType();
            return configType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(config) ??
                   configType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(config);
        }

        public Delegate AddCreateSlashCommandHandler(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException($"Could not find {methodName}.");
            }

            var handler = Delegate.CreateDelegate(_createSlashCommandEvent.EventHandlerType!, target, method);
            _createSlashCommandEvent.AddEventHandler(_instance, handler);
            return handler;
        }

        public void RemoveCreateSlashCommandHandler(Delegate handler)
        {
            _createSlashCommandEvent.RemoveEventHandler(_instance, handler);
        }
    }

    public override void Dispose()
    {
        if (_sapi != null)
        {
            if (_botStatusTickListenerId != 0)
            {
                _sapi.Event.UnregisterGameTickListener(_botStatusTickListenerId);
                _botStatusTickListenerId = 0;
            }

            if (_rejectedThreadCleanupTickListenerId != 0)
            {
                _sapi.Event.UnregisterGameTickListener(_rejectedThreadCleanupTickListenerId);
                _rejectedThreadCleanupTickListenerId = 0;
            }

            _sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            _sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        }

        if (_discord != null)
        {
            if (_createSlashCommandHandler != null)
            {
                _discord.RemoveCreateSlashCommandHandler(_createSlashCommandHandler);
                _createSlashCommandHandler = null;
            }

            if (_createDduSlashCommandHandler != null)
            {
                _discord.RemoveCreateSlashCommandHandler(_createDduSlashCommandHandler);
                _createDduSlashCommandHandler = null;
            }

            _discord.Client.SlashCommandExecuted -= OnSlashCommandExecuted;
            _discord.Client.ButtonExecuted -= OnButtonExecuted;
            _discord.Client.MessageReceived -= OnMessageReceived;
            _discord.Client.Ready -= OnDiscordReadyAsync;
        }

        if (_th3Essentials != null && _th3DiscordReadyEvent != null && _th3DiscordReadyHandler != null)
        {
            _th3DiscordReadyEvent.RemoveEventHandler(_th3Essentials, _th3DiscordReadyHandler);
            _th3DiscordReadyHandler = null;
        }
    }
}
