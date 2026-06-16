using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace DamineAdmin;

/// <summary>
/// Self-contained admin / moderation plugin for CounterStrikeSharp.
/// Uses ONLY built-in CSSharp APIs (built-in chat menus, built-in admin/permission
/// system). No external shared assemblies, no NuGet-resolved dependencies — so it
/// registers reliably with PluginResolveNugetPackages=false and cannot conflict with
/// GameModeManager / MenuManagerAPI / PlayerSettings. Every command body is wrapped so
/// a handler error can never crash the game server.
/// </summary>
public sealed class DamineAdmin : BasePlugin
{
    public override string ModuleName => "Damine Admin";
    public override string ModuleVersion => "1.5.0";
    public override string ModuleAuthor => "damine";
    public override string ModuleDescription => "Self-contained admin: kick/ban/slay/slap/timed-gag+mute/teleport/hp/armor/team/swap + strip/give/blind/respawn/noclip/god + workshop map change, vote, extend & round-restart + center-say/warn + rcon + admin menu + timed advertisements. All entity actions deferred to next frame (crash-safe).";

    private const string Tag = "\x04[Admin]\x01";

    // Tunables (kept as constants to avoid extra config surface / risk).
    private const float VoteDurationSeconds = 25.0f;
    private const float MapChangeDelaySeconds = 4.0f;

    private readonly HashSet<ulong> _gagged = new();
    private readonly HashSet<ulong> _muted = new();
    private readonly Dictionary<ulong, long> _gagExpiry = new();
    private readonly Dictionary<ulong, long> _muteExpiry = new();

    private string _banFile = string.Empty;
    private Dictionary<string, BanEntry> _bans = new();

    private List<MapEntry> _maps = new();

    // Timed advertisements (feature-discovery messages on a rotating timer).
    private readonly List<string> _ads = new();
    private int _adIndex;
    private int _adIntervalSeconds = 180;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _adTimer;

    // Map-vote state.
    private bool _voteActive;
    private List<MapEntry> _voteCandidates = new();
    private readonly Dictionary<int, int> _votes = new();

    public override void Load(bool hotReload)
    {
        try
        {
            _banFile = Path.Combine(ModuleDirectory, "bans.json");
            LoadBans();
            LoadMaps();
            LoadAds();

            // Enforce bans on authorize.
            RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);

            // Chat gag enforcement (reliable: blocks say / say_team for gagged players).
            AddCommandListener("say", OnSayHook, HookMode.Pre);
            AddCommandListener("say_team", OnSayHook, HookMode.Pre);

            // Timed advertisements: (re)arm on each map start so the timer survives map changes.
            RegisterListener<Listeners.OnMapStart>(_ => SetupAds());
            SetupAds();

            Logger.LogInformation("Damine Admin loaded ({Bans} bans, {Maps} vote maps, {Ads} ads).", _bans.Count, _maps.Count, _ads.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Damine Admin failed to initialize cleanly.");
        }
    }

    // ----------------------------------------------------------------- listeners

    private void OnClientAuthorized(int slot, SteamID id)
    {
        try
        {
            if (!IsBanned(id.SteamId64.ToString(), out var be)) return;
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is { IsValid: true } && p.UserId.HasValue)
                Server.ExecuteCommand($"kickid {p.UserId.Value} Banned: {Sanitize(be?.Reason)}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ban-enforce on authorize failed for slot {Slot}", slot);
        }
    }

    private HookResult OnSayHook(CCSPlayerController? player, CommandInfo info)
    {
        if (player is { IsValid: true } && _gagged.Contains(player.SteamID))
            return HookResult.Handled; // swallow chat from gagged players
        return HookResult.Continue;
    }

    // ----------------------------------------------------------------- menus / help

    [ConsoleCommand("css_admin", "Open the admin menu")]
    [ConsoleCommand("css_sm_admin", "Open the admin menu")]
    [RequiresPermissions("@css/generic")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdAdmin(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        if (caller is not { IsValid: true }) return;
        var menu = new ChatMenu($"{ChatColors.Gold}Admin Menu");
        menu.AddMenuOption("Kick player", (p, _) => OpenPlayerMenu(p, "Kick", t => DoKick(p, t, "Kicked by admin")));
        menu.AddMenuOption("Slay player", (p, _) => OpenPlayerMenu(p, "Slay", t => DoSlay(p, t)));
        menu.AddMenuOption("Slap player", (p, _) => OpenPlayerMenu(p, "Slap", t => DoSlap(p, t, 5)));
        menu.AddMenuOption("Ban player (perm)", (p, _) => OpenPlayerMenu(p, "Ban", t => DoBan(p, t, 0, "Banned by admin")));
        menu.AddMenuOption("Gag player", (p, _) => OpenPlayerMenu(p, "Gag", t => SetGag(p, t, true, 0)));
        menu.AddMenuOption("Ungag player", (p, _) => OpenPlayerMenu(p, "Ungag", t => SetGag(p, t, false, 0)));
        menu.AddMenuOption("Mute player", (p, _) => OpenPlayerMenu(p, "Mute", t => SetMute(p, t, true, 0)));
        menu.AddMenuOption("Freeze player", (p, _) => OpenPlayerMenu(p, "Freeze", t => DoFreeze(p, t, true)));
        menu.AddMenuOption("Unfreeze player", (p, _) => OpenPlayerMenu(p, "Unfreeze", t => DoFreeze(p, t, false)));
        menu.AddMenuOption("Bring player", (p, _) => OpenPlayerMenu(p, "Bring", t => DoBring(p, t)));
        menu.AddMenuOption("Change map", (p, _) => OpenMapMenu(p));
        menu.AddMenuOption("Start map vote", (p, _) => StartVoteFromConfig(p));
        MenuManager.OpenChatMenu(caller, menu);
    });

    [ConsoleCommand("css_web", "Show the stats website + your profile link")]
    [ConsoleCommand("css_stats", "Show the stats website + your profile link")]
    [ConsoleCommand("css_profile", "Show the stats website + your profile link")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdWeb(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        if (caller is not { IsValid: true }) return;
        const string site = "https://stats.damineweb.work";
        caller.PrintToChat($" {Tag} {ChatColors.Default}Stats & rankings: {ChatColors.Gold}{site}");
        if (!caller.IsBot && caller.SteamID != 0)
            caller.PrintToChat($" {Tag} {ChatColors.Default}Your profile: {ChatColors.Gold}{site}/player.php?id={caller.SteamID}");
        caller.PrintToChat($" {Tag} {ChatColors.Default}Try {ChatColors.Gold}!rank{ChatColors.Default} and {ChatColors.Gold}!store{ChatColors.Default} in chat too.");
    });

    [ConsoleCommand("css_help", "List admin commands")]
    [ConsoleCommand("css_sm_help", "List admin commands")]
    [RequiresPermissions("@css/generic")]
    public void CmdHelp(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        cmd.ReplyToCommand($"{Tag} Moderation: !admin !kick !ban !addban !unban !slay !slap !gag !ungag !mute !unmute !silence !unsilence !freeze !unfreeze !noclip !respawn !hp !god");
        cmd.ReplyToCommand($"{Tag} Movement: !bring !goto !team !rename | Maps: !ws !votemap !cancelvote !sm_map | Info: !who !help | Players use !rtv for rock-the-vote.");
        cmd.ReplyToCommand($"{Tag} Targets: name, #userid, @all @me @ct @t @bots @humans. Gag/Mute accept optional [minutes] (0 = permanent).");
    });

    [ConsoleCommand("css_who", "List players with their #userid and SteamID")]
    [ConsoleCommand("css_sm_who", "List players with their #userid and SteamID")]
    [ConsoleCommand("css_players", "List players with their #userid and SteamID")]
    [RequiresPermissions("@css/generic")]
    public void CmdWho(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        cmd.ReplyToCommand($"{Tag} Players online:");
        foreach (var p in Players())
            cmd.ReplyToCommand($"  #{p.UserId} \"{p.PlayerName}\" {(p.IsBot ? "BOT" : p.SteamID.ToString())} [{p.Team}]");
    });

    // ----------------------------------------------------------------- moderation

    [ConsoleCommand("css_kick", "Kick a player")]
    [ConsoleCommand("css_sm_kick", "Kick a player")]
    [RequiresPermissions("@css/kick")]
    [CommandHelper(minArgs: 1, usage: "<target> [reason]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdKick(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var reason = ArgsFrom(cmd, 2, "Kicked by admin");
        ForEachTarget(caller, cmd, t => DoKick(caller, t, reason));
    });

    [ConsoleCommand("css_slay", "Kill a player")]
    [ConsoleCommand("css_sm_slay", "Kill a player")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSlay(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoSlay(caller, t)));

    [ConsoleCommand("css_slap", "Slap a player (optional damage)")]
    [ConsoleCommand("css_sm_slap", "Slap a player (optional damage)")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target> [damage]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSlap(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var dmg = ParseInt(cmd.GetArg(2), 0);
        ForEachTarget(caller, cmd, t => DoSlap(caller, t, dmg));
    });

    [ConsoleCommand("css_ban", "Ban an online player by minutes (0 = permanent)")]
    [ConsoleCommand("css_sm_ban", "Ban an online player by minutes (0 = permanent)")]
    [RequiresPermissions("@css/ban")]
    [CommandHelper(minArgs: 1, usage: "<target> [minutes] [reason]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdBan(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var minutes = ParseInt(cmd.GetArg(2), 0);
        var reason = ArgsFrom(cmd, 3, "Banned by admin");
        var t = ResolveTargets(cmd.GetArg(1), caller).FirstOrDefault(p => !p.IsBot);
        if (t is null) { cmd.ReplyToCommand($"{Tag} No matching human player."); return; }
        DoBan(caller, t, minutes, reason);
    });

    [ConsoleCommand("css_addban", "Ban a SteamID64 offline")]
    [ConsoleCommand("css_sm_addban", "Ban a SteamID64 offline")]
    [RequiresPermissions("@css/ban")]
    [CommandHelper(minArgs: 1, usage: "<steamid64> [minutes] [reason]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAddBan(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var sid = cmd.GetArg(1).Trim();
        if (!ulong.TryParse(sid, out _)) { cmd.ReplyToCommand($"{Tag} Invalid SteamID64."); return; }
        var minutes = ParseInt(cmd.GetArg(2), 0);
        var reason = ArgsFrom(cmd, 3, "Banned by admin");
        _bans[sid] = new BanEntry { Name = "(offline)", Reason = reason, ExpiryUnix = minutes > 0 ? Now() + minutes * 60L : 0 };
        SaveBans();
        var online = Players().FirstOrDefault(p => p.SteamID.ToString() == sid);
        if (online is { UserId: not null }) Server.ExecuteCommand($"kickid {online.UserId.Value} Banned: {Sanitize(reason)}");
        Announce($"{CallerName(caller)} banned SteamID {sid} ({DurText(minutes)}): {reason}");
    });

    [ConsoleCommand("css_unban", "Remove a ban by SteamID64")]
    [ConsoleCommand("css_sm_unban", "Remove a ban by SteamID64")]
    [RequiresPermissions("@css/ban")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdUnban(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var sid = cmd.GetArg(1).Trim();
        if (_bans.Remove(sid)) { SaveBans(); cmd.ReplyToCommand($"{Tag} Unbanned {sid}."); }
        else cmd.ReplyToCommand($"{Tag} {sid} is not banned.");
    });

    [ConsoleCommand("css_gag", "Block a player's text chat (optional minutes)")]
    [ConsoleCommand("css_sm_gag", "Block a player's text chat (optional minutes)")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<target> [minutes]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdGag(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var minutes = ParseInt(cmd.GetArg(2), 0);
        ForEachTarget(caller, cmd, t => SetGag(caller, t, true, minutes));
    });

    [ConsoleCommand("css_ungag", "Unblock a player's text chat")]
    [ConsoleCommand("css_sm_ungag", "Unblock a player's text chat")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdUngag(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => SetGag(caller, t, false, 0)));

    [ConsoleCommand("css_mute", "Mute a player's voice (optional minutes)")]
    [ConsoleCommand("css_sm_mute", "Mute a player's voice (optional minutes)")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<target> [minutes]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdMute(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var minutes = ParseInt(cmd.GetArg(2), 0);
        ForEachTarget(caller, cmd, t => SetMute(caller, t, true, minutes));
    });

    [ConsoleCommand("css_unmute", "Unmute a player's voice")]
    [ConsoleCommand("css_sm_unmute", "Unmute a player's voice")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdUnmute(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => SetMute(caller, t, false, 0)));

    [ConsoleCommand("css_silence", "Gag + mute a player (optional minutes)")]
    [ConsoleCommand("css_sm_silence", "Gag + mute a player (optional minutes)")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<target> [minutes]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSilence(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var minutes = ParseInt(cmd.GetArg(2), 0);
        ForEachTarget(caller, cmd, t => { SetGag(caller, t, true, minutes); SetMute(caller, t, true, minutes); });
    });

    [ConsoleCommand("css_unsilence", "Remove gag + mute from a player")]
    [ConsoleCommand("css_sm_unsilence", "Remove gag + mute from a player")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdUnsilence(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => { SetGag(caller, t, false, 0); SetMute(caller, t, false, 0); }));

    [ConsoleCommand("css_freeze", "Freeze a player in place")]
    [ConsoleCommand("css_sm_freeze", "Freeze a player in place")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdFreeze(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoFreeze(caller, t, true)));

    [ConsoleCommand("css_unfreeze", "Unfreeze a player")]
    [ConsoleCommand("css_sm_unfreeze", "Unfreeze a player")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdUnfreeze(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoFreeze(caller, t, false)));

    [ConsoleCommand("css_noclip", "Toggle noclip for a player")]
    [ConsoleCommand("css_sm_noclip", "Toggle noclip for a player")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdNoclip(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, DoNoclip));

    [ConsoleCommand("css_god", "Toggle godmode (invulnerability) for a player")]
    [ConsoleCommand("css_sm_god", "Toggle godmode (invulnerability) for a player")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdGod(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoGod(caller, t)));

    [ConsoleCommand("css_hp", "Set a player's health")]
    [ConsoleCommand("css_sm_hp", "Set a player's health")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target> [hp]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdHp(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var hp = ParseInt(cmd.GetArg(2), 100);
        if (hp < 1) hp = 1;
        ForEachTarget(caller, cmd, t => DoHp(caller, t, hp));
    });

    [ConsoleCommand("css_respawn", "Respawn a player")]
    [ConsoleCommand("css_sm_respawn", "Respawn a player")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRespawn(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoRespawn(caller, t)));

    [ConsoleCommand("css_strip", "Remove all of a player's weapons")]
    [ConsoleCommand("css_sm_strip", "Remove all of a player's weapons")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdStrip(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoStrip(caller, t)));

    [ConsoleCommand("css_give", "Give a weapon to a player")]
    [ConsoleCommand("css_sm_give", "Give a weapon to a player")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 2, usage: "<target> <weapon>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdGive(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var w = cmd.GetArg(2).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(w)) { cmd.ReplyToCommand($"{Tag} Provide a weapon, e.g. ak47."); return; }
        if (!w.StartsWith("weapon_") && !w.StartsWith("item_")) w = "weapon_" + w;
        ForEachTarget(caller, cmd, t => DoGive(caller, t, w));
    });

    [ConsoleCommand("css_armor", "Set a player's armor (default 100)")]
    [ConsoleCommand("css_sm_armor", "Set a player's armor (default 100)")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target> [value]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdArmor(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var v = ParseInt(cmd.GetArg(2), 100);
        if (v < 0) v = 0;
        ForEachTarget(caller, cmd, t => DoArmor(caller, t, v));
    });

    [ConsoleCommand("css_blind", "Flash/blind a player (default 5s, max 30)")]
    [ConsoleCommand("css_sm_blind", "Flash/blind a player (default 5s, max 30)")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target> [seconds]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdBlind(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var secs = ParseInt(cmd.GetArg(2), 5);
        if (secs < 1) secs = 1;
        if (secs > 30) secs = 30;
        ForEachTarget(caller, cmd, t => DoBlind(caller, t, secs));
    });

    [ConsoleCommand("css_swap", "Swap a player to the opposite team")]
    [ConsoleCommand("css_sm_swap", "Swap a player to the opposite team")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSwap(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoSwap(caller, t)));

    // ----------------------------------------------------------------- movement

    [ConsoleCommand("css_bring", "Teleport a player to you")]
    [ConsoleCommand("css_sm_bring", "Teleport a player to you")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdBring(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ForEachTarget(caller, cmd, t => DoBring(caller, t)));

    [ConsoleCommand("css_goto", "Teleport yourself to a player")]
    [ConsoleCommand("css_sm_goto", "Teleport yourself to a player")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdGoto(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        if (caller is not { IsValid: true }) return;
        var t = ResolveTargets(cmd.GetArg(1), caller).FirstOrDefault();
        if (t is null) { cmd.ReplyToCommand($"{Tag} No matching target."); return; }
        DoGoto(caller, t);
    });

    [ConsoleCommand("css_team", "Move a player to a team (ct/t/spec)")]
    [ConsoleCommand("css_sm_team", "Move a player to a team (ct/t/spec)")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 2, usage: "<target> <ct|t|spec>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdTeam(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var team = ParseTeam(cmd.GetArg(2));
        if (team is null) { cmd.ReplyToCommand($"{Tag} Team must be ct, t or spec."); return; }
        ForEachTarget(caller, cmd, t => DoTeam(caller, t, team.Value));
    });

    [ConsoleCommand("css_rename", "Rename a player")]
    [ConsoleCommand("css_sm_rename", "Rename a player")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(minArgs: 2, usage: "<target> <new name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRename(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var newName = ArgsFrom(cmd, 2, string.Empty);
        if (string.IsNullOrWhiteSpace(newName)) { cmd.ReplyToCommand($"{Tag} Provide a new name."); return; }
        var t = ResolveTargets(cmd.GetArg(1), caller).FirstOrDefault();
        if (t is null) { cmd.ReplyToCommand($"{Tag} No matching target."); return; }
        DoRename(caller, t, newName);
    });

    // ----------------------------------------------------------------- maps

    [ConsoleCommand("css_say", "Send an admin chat message to everyone")]
    [ConsoleCommand("css_sm_say", "Send an admin chat message to everyone")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<message>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSay(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        Server.PrintToChatAll($" {ChatColors.Red}(ADMIN) {ChatColors.Default}{ArgsFrom(cmd, 1, string.Empty)}"));

    [ConsoleCommand("css_csay", "Send a center (HUD) message to everyone")]
    [ConsoleCommand("css_sm_csay", "Send a center (HUD) message to everyone")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 1, usage: "<message>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdCsay(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var msg = ArgsFrom(cmd, 1, string.Empty);
        if (string.IsNullOrWhiteSpace(msg)) return;
        Defer(() => { foreach (var p in Players()) { if (p is { IsValid: true }) p.PrintToCenter(msg); } });
    });

    [ConsoleCommand("css_warn", "Privately warn a player in chat")]
    [ConsoleCommand("css_sm_warn", "Privately warn a player in chat")]
    [RequiresPermissions("@css/chat")]
    [CommandHelper(minArgs: 2, usage: "<target> <message>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdWarn(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var msg = ArgsFrom(cmd, 2, string.Empty);
        if (string.IsNullOrWhiteSpace(msg)) { cmd.ReplyToCommand($"{Tag} Provide a warning message."); return; }
        var t = ResolveTargets(cmd.GetArg(1), caller).FirstOrDefault(p => !p.IsBot);
        if (t is null) { cmd.ReplyToCommand($"{Tag} No matching human player."); return; }
        DoWarn(caller, t, msg);
    });

    [ConsoleCommand("css_extend", "Extend the current map time limit (minutes, default 15)")]
    [ConsoleCommand("css_sm_extend", "Extend the current map time limit (minutes, default 15)")]
    [RequiresPermissions("@css/changemap")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdExtend(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var mins = ParseInt(cmd.GetArg(1), 15);
        if (mins < 1) mins = 1;
        Defer(() =>
        {
            var cv = ConVar.Find("mp_timelimit");
            var cur = cv?.GetPrimitiveValue<float>() ?? 0f;
            Server.ExecuteCommand($"mp_timelimit {cur + mins}");
        });
        Announce($"{CallerName(caller)} extended the map by {mins} minute{(mins == 1 ? "" : "s")}.");
    });

    [ConsoleCommand("css_rr", "Restart the round")]
    [ConsoleCommand("css_sm_rr", "Restart the round")]
    [RequiresPermissions("@css/slay")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRr(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        Announce($"{CallerName(caller)} restarted the round.");
        Defer(() => Server.ExecuteCommand("mp_restartgame 1"));
    });

    [ConsoleCommand("css_rcon", "Execute a server console command")]
    [ConsoleCommand("css_sm_rcon", "Execute a server console command")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 1, usage: "<command>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRcon(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var line = ArgsFrom(cmd, 1, string.Empty);
        if (string.IsNullOrWhiteSpace(line)) return;
        Logger.LogInformation("RCON by {Admin}: {Cmd}", CallerName(caller), line);
        Defer(() => Server.ExecuteCommand(line));
        Reply(caller, $"{Tag} Executed: {line}");
    });

    // Arbitrary map changer. GameModeManager owns "css_map" (!map), so we register
    // only "css_sm_map" / "css_ws" to avoid any command collision.
    [ConsoleCommand("css_sm_map", "Change map (stock name or workshop id)")]
    [RequiresPermissions("@css/changemap")]
    [CommandHelper(minArgs: 1, usage: "<map|workshopid>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSmMap(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
        ChangeFromArg(caller, cmd.GetArg(1)));

    [ConsoleCommand("css_ws", "Change to a workshop map by id, or open the map menu")]
    [ConsoleCommand("css_sm_ws", "Change to a workshop map by id, or open the map menu")]
    [RequiresPermissions("@css/changemap")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdWs(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        var arg = cmd.GetArg(1).Trim();
        if (!string.IsNullOrEmpty(arg)) { ChangeFromArg(caller, arg); return; }
        if (caller is not { IsValid: true }) { cmd.ReplyToCommand($"{Tag} Usage from console: css_ws <map|workshopid>."); return; }
        OpenMapMenu(caller);
    });

    [ConsoleCommand("css_votemap", "Start a player vote for the next map")]
    [ConsoleCommand("css_sm_votemap", "Start a player vote for the next map")]
    [RequiresPermissions("@css/changemap")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdVoteMap(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () => StartVoteFromConfig(caller));

    [ConsoleCommand("css_cancelvote", "Cancel an active map vote")]
    [ConsoleCommand("css_sm_cancelvote", "Cancel an active map vote")]
    [RequiresPermissions("@css/changemap")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdCancelVote(CCSPlayerController? caller, CommandInfo cmd) => Guard(cmd, () =>
    {
        if (!_voteActive) { cmd.ReplyToCommand($"{Tag} No vote is running."); return; }
        _voteActive = false;
        Announce($"{CallerName(caller)} cancelled the map vote.");
    });

    // ----------------------------------------------------------------- actions

    // CRITICAL SAFETY: CS2 native entity mutations (suicide, teleport, team change,
    // movetype/health/name schema writes, respawn) MUST NOT run synchronously inside a
    // chat-command / menu-selection hook — doing so can corrupt the entity system
    // mid-iteration and SEGFAULT the whole server (a native crash that bypasses any C#
    // try/catch). Defer() runs the mutation at the start of the next frame (a safe point
    // outside entity iteration) and re-validates the target, so a player disconnecting in
    // between can't cause a stale-pointer crash either.
    private void Defer(Action body) => Server.NextFrame(() =>
    {
        try { body(); }
        catch (Exception ex) { Logger.LogWarning(ex, "deferred admin action failed (server kept safe)"); }
    });

    private void DoKick(CCSPlayerController? by, CCSPlayerController t, string reason)
    {
        Announce($"{CallerName(by)} kicked {t.PlayerName} ({reason})");
        var uid = t is { IsValid: true } ? t.UserId : null;
        if (uid.HasValue) Defer(() => Server.ExecuteCommand($"kickid {uid.Value} {Sanitize(reason)}"));
    }

    private void DoSlay(CCSPlayerController? by, CCSPlayerController t)
    {
        Announce($"{CallerName(by)} slayed {t.PlayerName}");
        Defer(() =>
        {
            if (t is not { IsValid: true, PawnIsAlive: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is { IsValid: true }) pawn.CommitSuicide(false, true);
        });
    }

    private void DoSlap(CCSPlayerController? by, CCSPlayerController t, int damage)
    {
        Announce($"{CallerName(by)} slapped {t.PlayerName}{(damage > 0 ? $" (-{damage}hp)" : "")}");
        Defer(() =>
        {
            if (t is not { IsValid: true, PawnIsAlive: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is not { IsValid: true }) return;
            var origin = pawn.AbsOrigin;
            var angles = pawn.AbsRotation;
            if (origin is null || angles is null) return;
            var rng = Random.Shared;
            var vel = new Vector(pawn.AbsVelocity.X + rng.Next(-180, 180),
                                 pawn.AbsVelocity.Y + rng.Next(-180, 180),
                                 pawn.AbsVelocity.Z + 250);
            pawn.Teleport(origin, angles, vel); // no-move teleport applies velocity safely
            if (damage > 0)
            {
                var hp = pawn.Health - damage;
                if (hp <= 0) { pawn.CommitSuicide(false, true); }
                else { pawn.Health = hp; Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); }
            }
        });
    }

    private void DoBan(CCSPlayerController? by, CCSPlayerController t, int minutes, string reason)
    {
        if (t.IsBot) return;
        var sid = t.SteamID.ToString();
        _bans[sid] = new BanEntry { Name = t.PlayerName, Reason = reason, ExpiryUnix = minutes > 0 ? Now() + minutes * 60L : 0 };
        SaveBans();
        Announce($"{CallerName(by)} banned {t.PlayerName} ({DurText(minutes)}): {reason}");
        var uid = t.UserId;
        if (uid.HasValue) Defer(() => Server.ExecuteCommand($"kickid {uid.Value} Banned: {Sanitize(reason)}"));
    }

    private void SetGag(CCSPlayerController? by, CCSPlayerController t, bool on, int minutes)
    {
        var sid = t.SteamID;
        if (on)
        {
            _gagged.Add(sid);
            _gagExpiry[sid] = minutes > 0 ? Now() + minutes * 60L : 0;
            if (minutes > 0)
                AddTimer(minutes * 60f, () =>
                {
                    if (_gagExpiry.TryGetValue(sid, out var e) && e != 0 && e <= Now())
                    { _gagged.Remove(sid); _gagExpiry.Remove(sid); }
                });
            Announce($"{CallerName(by)} gagged {t.PlayerName}{DurSuffix(minutes)}");
        }
        else
        {
            _gagged.Remove(sid); _gagExpiry.Remove(sid);
            Announce($"{CallerName(by)} ungagged {t.PlayerName}");
        }
    }

    private void SetMute(CCSPlayerController? by, CCSPlayerController t, bool on, int minutes)
    {
        var sid = t.SteamID;
        if (on)
        {
            _muted.Add(sid);
            _muteExpiry[sid] = minutes > 0 ? Now() + minutes * 60L : 0;
            Defer(() => { if (t is { IsValid: true }) t.VoiceFlags |= VoiceFlags.Muted; });
            if (minutes > 0)
                AddTimer(minutes * 60f, () =>
                {
                    if (_muteExpiry.TryGetValue(sid, out var e) && e != 0 && e <= Now())
                    {
                        _muteExpiry.Remove(sid); _muted.Remove(sid);
                        Defer(() =>
                        {
                            var p = Players().FirstOrDefault(x => x.SteamID == sid);
                            if (p is { IsValid: true }) p.VoiceFlags &= ~VoiceFlags.Muted;
                        });
                    }
                });
        }
        else
        {
            _muted.Remove(sid); _muteExpiry.Remove(sid);
            Defer(() => { if (t is { IsValid: true }) t.VoiceFlags &= ~VoiceFlags.Muted; });
        }
        Announce($"{CallerName(by)} {(on ? $"muted {t.PlayerName}{DurSuffix(minutes)}" : $"unmuted {t.PlayerName}")}");
    }

    private void DoFreeze(CCSPlayerController? by, CCSPlayerController t, bool on)
    {
        Announce($"{CallerName(by)} {(on ? "froze" : "unfroze")} {t.PlayerName}");
        Defer(() =>
        {
            if (t is not { IsValid: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is not { IsValid: true }) return;
            pawn.MoveType = on ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        });
    }

    private void DoNoclip(CCSPlayerController t) => Defer(() =>
    {
        if (t is not { IsValid: true, PawnIsAlive: true }) return;
        var pawn = t.PlayerPawn?.Value;
        if (pawn is not { IsValid: true }) return;
        pawn.MoveType = pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP ? MoveType_t.MOVETYPE_WALK : MoveType_t.MOVETYPE_NOCLIP;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    });

    private void DoGod(CCSPlayerController? by, CCSPlayerController t)
    {
        Defer(() =>
        {
            if (t is not { IsValid: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is not { IsValid: true }) return;
            var god = !pawn.TakesDamage;
            pawn.TakesDamage = god;
            Announce($"{CallerName(by)} {(god ? "enabled" : "disabled")} godmode on {t.PlayerName}");
        });
    }

    private void DoHp(CCSPlayerController? by, CCSPlayerController t, int hp)
    {
        Announce($"{CallerName(by)} set {t.PlayerName}'s HP to {hp}");
        Defer(() =>
        {
            if (t is not { IsValid: true, PawnIsAlive: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is not { IsValid: true }) return;
            if (hp > pawn.MaxHealth) pawn.MaxHealth = hp;
            pawn.Health = hp;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        });
    }

    private void DoRespawn(CCSPlayerController? by, CCSPlayerController t) => Defer(() =>
    {
        if (t is { IsValid: true } && !t.PawnIsAlive) t.Respawn();
    });

    private void DoBring(CCSPlayerController? by, CCSPlayerController t)
    {
        Announce($"{CallerName(by)} brought {t.PlayerName}");
        Defer(() =>
        {
            if (by is not { IsValid: true } || t is not { IsValid: true, PawnIsAlive: true }) return;
            var dest = by.PlayerPawn?.Value;
            var pawn = t.PlayerPawn?.Value;
            if (dest is not { IsValid: true } || pawn is not { IsValid: true }) return;
            var o = dest.AbsOrigin; var r = dest.AbsRotation;
            if (o is null || r is null) return;
            pawn.Teleport(o, r, new Vector(0, 0, 0));
        });
    }

    private void DoGoto(CCSPlayerController by, CCSPlayerController t)
    {
        Announce($"{CallerName(by)} teleported to {t.PlayerName}");
        Defer(() =>
        {
            if (by is not { IsValid: true, PawnIsAlive: true } || t is not { IsValid: true }) return;
            var self = by.PlayerPawn?.Value;
            var dest = t.PlayerPawn?.Value;
            if (self is not { IsValid: true } || dest is not { IsValid: true }) return;
            var o = dest.AbsOrigin; var r = dest.AbsRotation;
            if (o is null || r is null) return;
            self.Teleport(o, r, new Vector(0, 0, 0));
        });
    }

    private void DoTeam(CCSPlayerController? by, CCSPlayerController t, CsTeam team)
    {
        Announce($"{CallerName(by)} moved {t.PlayerName} to {team}");
        // ChangeTeam is the gentle, crash-safe team move (no forced mid-hook respawn).
        Defer(() => { if (t is { IsValid: true }) t.ChangeTeam(team); });
    }

    private void DoRename(CCSPlayerController? by, CCSPlayerController t, string newName)
    {
        var old = t.PlayerName;
        Announce($"{CallerName(by)} renamed {old} to {newName}");
        Defer(() =>
        {
            if (t is not { IsValid: true }) return;
            t.PlayerName = newName;
            Utilities.SetStateChanged(t, "CBasePlayerController", "m_iszPlayerName");
        });
    }

    private void DoStrip(CCSPlayerController? by, CCSPlayerController t)
    {
        Announce($"{CallerName(by)} stripped {t.PlayerName}'s weapons");
        Defer(() => { if (t is { IsValid: true, PawnIsAlive: true }) t.RemoveWeapons(); });
    }

    private void DoGive(CCSPlayerController? by, CCSPlayerController t, string weapon)
    {
        Announce($"{CallerName(by)} gave {t.PlayerName} {weapon}");
        Defer(() => { if (t is { IsValid: true, PawnIsAlive: true }) t.GiveNamedItem(weapon); });
    }

    private void DoArmor(CCSPlayerController? by, CCSPlayerController t, int armor)
    {
        Announce($"{CallerName(by)} set {t.PlayerName}'s armor to {armor}");
        Defer(() =>
        {
            if (t is not { IsValid: true, PawnIsAlive: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is not { IsValid: true }) return;
            pawn.ArmorValue = armor;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        });
    }

    private void DoBlind(CCSPlayerController? by, CCSPlayerController t, int seconds)
    {
        Announce($"{CallerName(by)} blinded {t.PlayerName} for {seconds}s");
        Defer(() =>
        {
            if (t is not { IsValid: true, PawnIsAlive: true }) return;
            var pawn = t.PlayerPawn?.Value;
            if (pawn is not { IsValid: true }) return;
            pawn.FlashDuration = seconds;
            pawn.FlashMaxAlpha = 255f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
        });
    }

    private void DoSwap(CCSPlayerController? by, CCSPlayerController t)
    {
        var opp = t.Team == CsTeam.CounterTerrorist ? CsTeam.Terrorist
                : t.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist
                : (CsTeam?)null;
        if (opp is null) { Reply(by, $"{Tag} {t.PlayerName} isn't on a playing team."); return; }
        DoTeam(by, t, opp.Value);
    }

    private void DoWarn(CCSPlayerController? by, CCSPlayerController t, string msg)
    {
        Announce($"{CallerName(by)} warned {t.PlayerName}");
        Defer(() => { if (t is { IsValid: true }) t.PrintToChat($" {Tag} {ChatColors.Red}WARNING: {ChatColors.Default}{msg}"); });
    }

    // ----------------------------------------------------------------- map change + vote

    private void ChangeFromArg(CCSPlayerController? by, string arg)
    {
        arg = (arg ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(arg)) return;
        if (ulong.TryParse(arg, out _)) ChangeToMap(by, arg, arg);
        else ChangeToMap(by, arg, string.Empty);
    }

    private void ChangeToMap(CCSPlayerController? by, string display, string id)
    {
        var shown = string.IsNullOrEmpty(display) ? id : display;
        Announce($"{CallerName(by)} is changing the map to {shown}...");
        AddTimer(MapChangeDelaySeconds, () =>
        {
            if (!string.IsNullOrEmpty(id)) Server.ExecuteCommand($"host_workshop_map {id}");
            else Server.ExecuteCommand($"changelevel {Sanitize(display)}");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OpenMapMenu(CCSPlayerController admin)
    {
        if (_maps.Count == 0) { admin.PrintToChat($" {Tag} No maps configured (maps.json)."); return; }
        var menu = new ChatMenu($"{ChatColors.Gold}Change Map");
        foreach (var m in _maps)
        {
            var mm = m;
            menu.AddMenuOption(mm.Name, (p, _) => ChangeToMap(p, mm.Name, mm.Id));
        }
        MenuManager.OpenChatMenu(admin, menu);
    }

    private void StartVoteFromConfig(CCSPlayerController? by)
    {
        if (_voteActive) { Reply(by, $"{Tag} A vote is already running."); return; }
        var cands = _maps.Take(6).ToList();
        if (cands.Count < 2) { Reply(by, $"{Tag} Need at least 2 maps in maps.json to start a vote."); return; }
        _voteActive = true;
        _voteCandidates = cands;
        _votes.Clear();

        var menu = new ChatMenu($"{ChatColors.Gold}Vote: next map");
        for (var i = 0; i < cands.Count; i++)
        {
            var idx = i;
            menu.AddMenuOption(cands[i].Name, (p, _) => RecordVote(p, idx));
        }
        foreach (var p in Players())
        {
            try { MenuManager.OpenChatMenu(p, menu); } catch (Exception ex) { Logger.LogWarning(ex, "open vote menu failed"); }
        }
        Server.PrintToChatAll($" {Tag} {ChatColors.Default}Map vote started by {CallerName(by)} — choose in the menu! ({(int)VoteDurationSeconds}s)");
        AddTimer(VoteDurationSeconds, CloseVote, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RecordVote(CCSPlayerController? p, int idx)
    {
        if (!_voteActive || p is not { IsValid: true }) return;
        if (idx < 0 || idx >= _voteCandidates.Count) return;
        _votes[p.Slot] = idx;
        p.PrintToChat($" {Tag} {ChatColors.Default}You voted for {ChatColors.Gold}{_voteCandidates[idx].Name}{ChatColors.Default}.");
    }

    private void CloseVote()
    {
        try
        {
            if (!_voteActive) return;
            _voteActive = false;
            if (_voteCandidates.Count == 0) return;

            var tally = new int[_voteCandidates.Count];
            foreach (var v in _votes.Values)
                if (v >= 0 && v < tally.Length) tally[v]++;

            var best = -1;
            var top = new List<int>();
            for (var i = 0; i < tally.Length; i++)
            {
                if (tally[i] > best) { best = tally[i]; top.Clear(); top.Add(i); }
                else if (tally[i] == best) top.Add(i);
            }
            var win = top.Count > 0 ? top[Random.Shared.Next(top.Count)] : 0;
            var w = _voteCandidates[win];
            Server.PrintToChatAll($" {Tag} {ChatColors.Default}Vote ended — next map: {ChatColors.Gold}{w.Name}{ChatColors.Default} ({best} vote{(best == 1 ? "" : "s")}).");
            ChangeToMap(null, w.Name, w.Id);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CloseVote failed");
            _voteActive = false;
        }
    }

    // ----------------------------------------------------------------- menu helpers

    private void OpenPlayerMenu(CCSPlayerController admin, string verb, Action<CCSPlayerController> onPick)
    {
        var menu = new ChatMenu($"{ChatColors.Gold}{verb}: pick a player");
        foreach (var p in Players())
        {
            var target = p;
            menu.AddMenuOption($"{p.PlayerName} ({(p.IsBot ? "BOT" : p.Team.ToString())})", (caller, _) =>
            {
                if (target is { IsValid: true }) onPick(target);
            });
        }
        MenuManager.OpenChatMenu(admin, menu);
    }

    // ----------------------------------------------------------------- utilities

    /// <summary>
    /// Returns valid, non-HLTV players. Defensive: CounterStrikeSharp's
    /// Utilities.GetPlayers() throws NativeException("Entity system is not yet
    /// initialized") when called between maps / before the world is ready. In that
    /// state there are no actionable players, so we return an empty set instead of
    /// letting the command fault.
    /// </summary>
    // ----------------------------------------------------------------- advertisements

    private void LoadAds()
    {
        try
        {
            _ads.Clear();
            var f = Path.Combine(ModuleDirectory, "ads.json");
            if (!File.Exists(f)) return;
            var cfg = JsonSerializer.Deserialize<AdConfig>(File.ReadAllText(f));
            if (cfg == null) return;
            _adIntervalSeconds = cfg.IntervalSeconds > 0 ? cfg.IntervalSeconds : 180;
            foreach (var m in cfg.Messages ?? new())
                if (!string.IsNullOrWhiteSpace(m)) _ads.Add(Colorize(m));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "could not read ads.json; advertisements disabled until fixed");
            _ads.Clear();
        }
    }

    private void SetupAds()
    {
        try
        {
            _adTimer?.Kill();
            _adTimer = null;
            if (_ads.Count == 0) return;
            _adTimer = AddTimer(_adIntervalSeconds, ShowNextAd, TimerFlags.REPEAT);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "could not arm advertisement timer");
        }
    }

    private void ShowNextAd()
    {
        try
        {
            if (_ads.Count == 0) return;
            // Only advertise when at least one human is connected (no console spam on an empty/hibernating server).
            if (!Players().Any()) return;
            var msg = _ads[_adIndex % _ads.Count];
            _adIndex++;
            Server.PrintToChatAll($" {Tag} {ChatColors.Default}{msg}");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "advertisement tick skipped");
        }
    }

    // Translate {color} tokens so ad messages can be styled without embedding control chars.
    private static string Colorize(string s) => s
        .Replace("{default}", ChatColors.Default.ToString())
        .Replace("{white}", ChatColors.White.ToString())
        .Replace("{green}", ChatColors.Green.ToString())
        .Replace("{lime}", ChatColors.Lime.ToString())
        .Replace("{red}", ChatColors.Red.ToString())
        .Replace("{yellow}", ChatColors.Yellow.ToString())
        .Replace("{gold}", ChatColors.Gold.ToString())
        .Replace("{blue}", ChatColors.Blue.ToString())
        .Replace("{purple}", ChatColors.Purple.ToString())
        .Replace("{grey}", ChatColors.Grey.ToString());

    private IEnumerable<CCSPlayerController> Players()
    {
        List<CCSPlayerController> list;
        try
        {
            list = Utilities.GetPlayers();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "GetPlayers() unavailable (entity system not ready); returning empty set");
            return Enumerable.Empty<CCSPlayerController>();
        }
        return list.Where(p => p is { IsValid: true, IsHLTV: false });
    }

    private List<CCSPlayerController> ResolveTargets(string token, CCSPlayerController? caller)
    {
        var all = Players().ToList();
        token = (token ?? string.Empty).Trim();
        switch (token.ToLowerInvariant())
        {
            case "@all": return all;
            case "@me": return caller is { IsValid: true } ? new() { caller } : new();
            case "@ct": return all.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();
            case "@t": return all.Where(p => p.Team == CsTeam.Terrorist).ToList();
            case "@bots": return all.Where(p => p.IsBot).ToList();
            case "@humans": return all.Where(p => !p.IsBot).ToList();
        }
        if (token.StartsWith("#"))
        {
            var rest = token[1..];
            if (int.TryParse(rest, out var uid)) return all.Where(p => p.UserId == uid).ToList();
            if (ulong.TryParse(rest, out var sid)) return all.Where(p => p.SteamID == sid).ToList();
            return new();
        }
        return all.Where(p => p.PlayerName.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ForEachTarget(CCSPlayerController? caller, CommandInfo cmd, Action<CCSPlayerController> action)
    {
        var targets = ResolveTargets(cmd.GetArg(1), caller);
        if (targets.Count == 0) { cmd.ReplyToCommand($"{Tag} No matching target for \"{cmd.GetArg(1)}\"."); return; }
        foreach (var t in targets.ToList())
            if (t is { IsValid: true }) action(t);
    }

    private void Guard(CommandInfo cmd, Action body)
    {
        try { body(); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "command '{Cmd}' threw", cmd.GetCommandString);
            try { cmd.ReplyToCommand($"{Tag} command error (logged, server safe)."); } catch { /* ignore */ }
        }
    }

    private void Announce(string msg) => Server.PrintToChatAll($" {Tag} {ChatColors.Default}{msg}");

    private static void Reply(CCSPlayerController? c, string msg)
    {
        if (c is { IsValid: true }) c.PrintToChat(msg);
        else Server.PrintToConsole(msg + "\n");
    }

    private static string CallerName(CCSPlayerController? c) => c is { IsValid: true } ? c.PlayerName : "Console";

    private static string ArgsFrom(CommandInfo cmd, int start, string fallback)
    {
        if (cmd.ArgCount <= start) return fallback;
        var parts = new List<string>();
        for (var i = start; i < cmd.ArgCount; i++) parts.Add(cmd.GetArg(i));
        var s = string.Join(' ', parts).Trim();
        return string.IsNullOrWhiteSpace(s) ? fallback : s;
    }

    private static int ParseInt(string s, int fallback) => int.TryParse(s?.Trim(), out var v) ? v : fallback;

    private static CsTeam? ParseTeam(string s) => (s ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ct" or "counterterrorist" or "3" => CsTeam.CounterTerrorist,
        "t" or "terrorist" or "2" => CsTeam.Terrorist,
        "spec" or "spectator" or "spectate" or "1" => CsTeam.Spectator,
        _ => null,
    };

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string DurText(int minutes) => minutes <= 0 ? "permanent" : $"{minutes}m";

    private static string DurSuffix(int minutes) => minutes > 0 ? $" for {minutes}m" : " (permanent)";

    private static string Sanitize(string? s) => string.IsNullOrWhiteSpace(s) ? "admin" : s.Replace("\"", "").Replace(";", "").Replace("\n", " ").Trim();

    // ----------------------------------------------------------------- stores

    private bool IsBanned(string sid, out BanEntry? entry)
    {
        entry = null;
        if (!_bans.TryGetValue(sid, out var be)) return false;
        if (be.ExpiryUnix != 0 && be.ExpiryUnix <= Now())
        {
            _bans.Remove(sid);
            SaveBans();
            return false;
        }
        entry = be;
        return true;
    }

    private void LoadBans()
    {
        try
        {
            if (File.Exists(_banFile))
                _bans = JsonSerializer.Deserialize<Dictionary<string, BanEntry>>(File.ReadAllText(_banFile)) ?? new();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "could not read bans.json; starting empty");
            _bans = new();
        }
    }

    private void SaveBans()
    {
        try
        {
            File.WriteAllText(_banFile, JsonSerializer.Serialize(_bans, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "could not write bans.json");
        }
    }

    private void LoadMaps()
    {
        try
        {
            var f = Path.Combine(ModuleDirectory, "maps.json");
            if (File.Exists(f))
                _maps = JsonSerializer.Deserialize<List<MapEntry>>(File.ReadAllText(f)) ?? new();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "could not read maps.json; map menu/vote disabled until fixed");
            _maps = new();
        }
    }

    public sealed class BanEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
        [JsonPropertyName("expiry_unix")] public long ExpiryUnix { get; set; }
    }

    public sealed class MapEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        // Empty id => stock map changed via changelevel <name>. Non-empty => workshop id via host_workshop_map.
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }

    public sealed class AdConfig
    {
        [JsonPropertyName("IntervalSeconds")] public int IntervalSeconds { get; set; } = 180;
        [JsonPropertyName("Messages")] public List<string> Messages { get; set; } = new();
    }
}
