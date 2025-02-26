﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MaxMind.GeoIP2;
using Server = CounterStrikeSharp.API.Server;

namespace Advertisement;

public class User
{
    public bool HtmlPrint { get; set; }
    public string Message { get; set; } = string.Empty;
    public int PrintTime { get; set; }
}

[MinimumApiVersion(305)]
public class Ads : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius & Armatura";
    public override string ModuleName => "Advertisement";
    public override string ModuleVersion => "v1.1.1";

    private readonly List<Timer> _timers = new();
    private readonly List<Timer> _serverTimers = new();

    private readonly Dictionary<ulong, string> _playerIsoCode = new();
    private readonly Dictionary<ulong, string> _playerCity = new();

    // Кеш для результатов опросов серверов.
    // Ключ – (ip, port), значение – последний сформированный текст.
    private readonly Dictionary<(string, int), string> _serverStatusCache = new();

    private readonly User?[] _users = new User?[66];
    public Config Config { get; set; } = null!;

    public override void Load(bool hotReload)
    {
        Config = LoadConfig();

        RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnTick>(OnTick);

        StartTimers();
        StartServerTimers();

        if (hotReload)
        {
            foreach (var player in Utilities.GetPlayers())
                _users[player.Slot] = new User();
        }
    }

    private HookResult EventPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        var player = ev.Userid;
        if (player is null) return HookResult.Continue;

        _playerIsoCode.Remove(player.SteamID);
        _playerCity.Remove(player.SteamID);

        return HookResult.Continue;
    }

    private void OnClientAuthorized(int slot, SteamID id)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        _users[slot] = new User();

        if (player?.IpAddress == null) return;

        var ip = player.IpAddress.Split(':')[0];
        _playerIsoCode.TryAdd(id.SteamId64, GetPlayerIsoCode(ip));
        _playerCity.TryAdd(id.SteamId64, GetPlayerCity(ip));
    }

    private HookResult EventPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
    {
        if (Config.WelcomeMessage == null)
            return HookResult.Continue;

        var player = ev.Userid;
        if (player is null || !player.IsValid)
            return HookResult.Continue;

        // Приветственное сообщение лично подключившемуся
        var welcomeMsg = Config.WelcomeMessage;
        var msg = welcomeMsg.Message
            .Replace("{PLAYERNAME}", player.PlayerName)
            .ReplaceColorTags();
        PrintWrappedLine(0, msg, player, true);

        // Рассылаем всем сообщение о его стране/городе
        if (!string.IsNullOrEmpty(Config.ConnectAnnounce))
        {
            var steam64 = player.SteamID;
            if (_playerIsoCode.TryGetValue(steam64, out var country) &&
                _playerCity.TryGetValue(steam64, out var city))
            {
                var connectMsg = Config.ConnectAnnounce
                    .Replace("{PLAYERNAME}", player.PlayerName)
                    .Replace("{COUNTRY}", country)
                    .Replace("{CITY}", city)
                    .ReplaceColorTags();

                foreach (var p in Utilities.GetPlayers().Where(u => !u.IsBot && u.IsValid))
                    Server.PrintToChatAll(connectMsg);
            }
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            var user = _users[player.Slot];
            if (user == null) continue;

            if (user.HtmlPrint)
            {
                var showWhenDead = Config.ShowHtmlWhenDead ?? false;
                if (!showWhenDead && !player.PawnIsAlive)
                    continue;

                var duration = Config.HtmlCenterDuration;
                if (duration != null && TimeSpan.FromSeconds(user.PrintTime / 64.0).Seconds < duration.Value)
                {
                    player.PrintToCenterHtml(user.Message);
                    user.PrintTime++;
                }
                else
                {
                    user.HtmlPrint = false;
                }
            }
        }
    }

    private void StartTimers()
    {
        if (Config.Ads == null) return;
        foreach (var ad in Config.Ads)
            _timers.Add(AddTimer(ad.Interval, () => ShowAd(ad), TimerFlags.REPEAT));
    }

    private void ShowAd(Advertisement ad)
    {
        var messages = ad.NextMessages;
        foreach (var (type, message) in messages)
        {
            switch (type)
            {
                case "Chat":
                    PrintWrappedLine(HudDestination.Chat, message);
                    break;
                case "Center":
                    PrintWrappedLine(HudDestination.Center, message);
                    break;
            }
        }
    }

    private void StartServerTimers()
    {
        if (Config.Servers == null) return;
        foreach (var serverInfo in Config.Servers)
        {
            _serverTimers.Add(AddTimer(serverInfo.Interval,
                () => QueryAndAnnounceServer(serverInfo),
                TimerFlags.REPEAT));
        }
    }

    private void QueryAndAnnounceServer(ServerInfo serverInfo)
    {
        try
        {
            var info = AdvancedA2S.GetServerInfo(serverInfo.Ip, (ushort)serverInfo.Port);

            if (info == null)
            {
                Console.WriteLine("GetInfo() вернул null (сервер не ответил или не поддерживает запрос).");
                throw new NullReferenceException("info == null");
            }

            // Выведем в консоль поля info
            Console.WriteLine("--- GetInfo() data ---");
            Console.WriteLine($"Map: {info.Map}");
            Console.WriteLine($"Players: {info.Players}");
            Console.WriteLine($"MaxPlayers: {info.MaxPlayers}");
            Console.WriteLine("----------------------");

            // Формируем сообщение по вашему шаблону
            var msg = serverInfo.MessageTemplate
                .Replace("{SERVER_IP}", serverInfo.Ip)
                .Replace("{SERVER_PORT}", serverInfo.Port.ToString())
                .Replace("{SERVER_MAP}", info.Map)
                .Replace("{SERVER_PLAYERS}", info.Players.ToString())
                .Replace("{SERVER_MAXPLAYERS}", info.MaxPlayers.ToString());

            // Сохраняем в кеш (если он у вас есть)
            _serverStatusCache[(serverInfo.Ip, serverInfo.Port)] = msg;

            // Рассылаем всем в чат
            foreach (var p in Utilities.GetPlayers().Where(u => !u.IsBot && u.IsValid))
                p.PrintToChat(msg);
        }
        catch (Exception ex)
        { ;
            _serverStatusCache.Remove((serverInfo.Ip, serverInfo.Port));
            Console.WriteLine($"[Ads] Ошибка опроса {serverInfo.Ip}:{serverInfo.Port} => {ex.Message}");
        }
    }

    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [ConsoleCommand("css_servers", "Показать список серверов из кеша")]
    public void ShowServersCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null) return;

        if (Config.Servers == null || Config.Servers.Count == 0)
        {
            return;
        }

        foreach (var server in Config.Servers)
        {
            if (_serverStatusCache.TryGetValue((server.Ip, server.Port), out var cachedMsg))
            {
                controller.PrintToChat(cachedMsg);
                controller.ExecuteClientCommand("connect " + server.Ip + ":" + server.Port);
                return;
            }
        }
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_advert_reload", "configuration restart")]
    public void ReloadAdvertConfig(CCSPlayerController? controller, CommandInfo command)
    {
        Config = LoadConfig();

        foreach (var t in _timers) t.Kill();
        _timers.Clear();

        foreach (var t in _serverTimers) t.Kill();
        _serverTimers.Clear();

        _serverStatusCache.Clear(); // очистим кеш при перезагрузке

        StartTimers();
        StartServerTimers();

        // Повторно подгрузим страну/город для текущих игроков
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IpAddress == null || player.AuthorizedSteamID == null)
                continue;

            var ip = player.IpAddress.Split(':')[0];
            _playerIsoCode[player.AuthorizedSteamID.SteamId64] = GetPlayerIsoCode(ip);
            _playerCity[player.AuthorizedSteamID.SteamId64] = GetPlayerCity(ip);
        }

        const string msg = "[Advertisement] configuration successfully rebooted!";
        if (controller == null)
            Console.WriteLine(msg);
        else
            controller.PrintToChat(msg);
    }

    private void PrintWrappedLine(HudDestination? destination, string message,
        CCSPlayerController? connectPlayer = null, bool isWelcome = false)
    {
        if (connectPlayer != null && !connectPlayer.IsBot && isWelcome)
        {
            var welcomeMessage = Config.WelcomeMessage;
            if (welcomeMessage is null) return;

            AddTimer(welcomeMessage.DisplayDelay, () =>
            {
                if (connectPlayer == null || !connectPlayer.IsValid) return;

                var processed = ProcessMessage(message, connectPlayer.SteamID)
                    .Replace("{PLAYERNAME}", connectPlayer.PlayerName);

                switch (welcomeMessage.MessageType)
                {
                    case MessageType.Chat:
                        connectPlayer.PrintToChat(processed);
                        break;
                    case MessageType.Center:
                        connectPlayer.PrintToChat(processed);
                        break;
                    case MessageType.CenterHtml:
                        SetHtmlPrintSettings(connectPlayer, processed);
                        break;
                }
            });
        }
        else
        {
            foreach (var player in Utilities.GetPlayers()
                         .Where(u => !isWelcome && !u.IsBot && u.IsValid))
            {
                var processed = ProcessMessage(message, player.SteamID);
                if (destination == HudDestination.Chat)
                {
                    player.PrintToChat(" " + processed);
                }
                else
                {
                    if (Config.PrintToCenterHtml == true)
                        SetHtmlPrintSettings(player, processed);
                    else
                        player.PrintToCenter(processed);
                }
            }
        }
    }

    private void SetHtmlPrintSettings(CCSPlayerController player, string message)
    {
        var user = _users[player.Slot];
        if (user == null)
        {
            _users[player.Slot] = new User();
            user = _users[player.Slot];
        }

        user.HtmlPrint = true;
        user.PrintTime = 0;
        user.Message = message;
    }

    private string ProcessMessage(string message, ulong steamId)
    {
        if (Config.LanguageMessages == null)
            return ReplaceMessageTags(message);

        var matches = Regex.Matches(message, @"\{([^}]*)\}");
        foreach (Match match in matches)
        {
            var tag = match.Groups[0].Value;
            var tagName = match.Groups[1].Value;

            if (!Config.LanguageMessages.TryGetValue(tagName, out var language)) continue;

            var isoCode = _playerIsoCode.TryGetValue(steamId, out var code)
                ? code
                : Config.DefaultLang;

            if (isoCode != null && language.TryGetValue(isoCode, out var replacement))
                message = message.Replace(tag, replacement);
            else if (Config.DefaultLang != null &&
                     language.TryGetValue(Config.DefaultLang, out var defReplacement))
                message = message.Replace(tag, defReplacement);
        }

        return ReplaceMessageTags(message);
    }

    private string ReplaceMessageTags(string message)
    {
        var mapName = NativeAPI.GetMapName();
        var replacedMessage = message
            .Replace("{MAP}", mapName)
            .Replace("{TIME}", DateTime.Now.ToString("HH:mm:ss"))
            .Replace("{DATE}", DateTime.Now.ToString("dd.MM.yyyy"))
            .Replace("{SERVERNAME}", ConVar.Find("hostname")?.StringValue ?? "Server")
            .Replace("{IP}", ConVar.Find("ip")?.StringValue ?? "127.0.0.1")
            .Replace("{PORT}", ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString() ?? "27015")
            .Replace("{MAXPLAYERS}", Server.MaxPlayers.ToString())
            .Replace("{PLAYERS}", Utilities.GetPlayers().Count(u => u.PlayerPawn?.Value?.IsValid == true).ToString())
            .Replace("\n", "\u2029")
            .ReplaceColorTags();

        if (Config.MapsName != null && Config.MapsName.TryGetValue(mapName, out var niceName))
            replacedMessage = replacedMessage.Replace(mapName, niceName);

        return replacedMessage;
    }

    private Config LoadConfig()
    {
        var directory = Path.Combine(Application.RootDirectory, "configs/plugins/Advertisement");
        Directory.CreateDirectory(directory);

        var configPath = Path.Combine(directory, "Advertisement.json");
        if (!File.Exists(configPath))
            return CreateConfig(configPath);

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<Config>(json,
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
        return config ?? new Config();
    }

    private Config CreateConfig(string configPath)
    {
        var config = new Config
        {
            PrintToCenterHtml = false,
            WelcomeMessage = new WelcomeMessage
            {
                MessageType = MessageType.Chat,
                Message = "Welcome, {BLUE}{PLAYERNAME}",
                DisplayDelay = 5
            },
            Ads = new List<Advertisement>
            {
                new()
                {
                    Interval = 35,
                    Messages = new List<Dictionary<string, string>>
                    {
                        new() { ["Chat"] = "{map_name}", ["Center"] = "Section 1 Center 1" },
                        new() { ["Chat"] = "{current_time}" }
                    }
                },
                new()
                {
                    Interval = 40,
                    Messages = new List<Dictionary<string, string>>
                    {
                        new() { ["Chat"] = "Section 2 Chat 1" },
                        new() { ["Chat"] = "Section 2 Chat 2", ["Center"] = "Section 2 Center 1" }
                    }
                }
            },
            DefaultLang = "US",
            LanguageMessages = new Dictionary<string, Dictionary<string, string>>
            {
                {
                    "map_name", new Dictionary<string, string>
                    {
                        ["RU"] = "Текущая карта: {MAP}",
                        ["US"] = "Current map: {MAP}",
                        ["CN"] = "{GRAY}当前地图: {RED}{MAP}"
                    }
                },
                {
                    "current_time", new Dictionary<string, string>
                    {
                        ["RU"] = "{GRAY}Текущее время: {RED}{TIME}",
                        ["US"] = "{GRAY}Current time: {RED}{TIME}",
                        ["CN"] = "{GRAY}当前时间: {RED}{TIME}"
                    }
                }
            },
            MapsName = new Dictionary<string, string>
            {
                ["de_mirage"] = "Mirage",
                ["de_dust"] = "Dust II"
            },
            ConnectAnnounce = "Игрок {PLAYERNAME} зашёл из {COUNTRY}, {CITY}",
            Servers = new List<ServerInfo>
            {
                new ServerInfo
                {
                    Ip = "127.0.0.1",
                    Port = 27015,
                    Interval = 60,
                    MessageTemplate = "{SERVER_IP}:{SERVER_PORT} - {SERVER_MAP} | {SERVER_PLAYERS}/{SERVER_MAXPLAYERS}"
                }
            }
        };

        File.WriteAllText(configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine("[Advertisement] Created default config at: " + configPath);
        Console.ResetColor();

        return config;
    }

    private string GetPlayerIsoCode(string ip)
    {
        var defaultLang = Config.DefaultLang ?? "";
        if (ip == "127.0.0.1") return defaultLang;

        try
        {
            using var reader = new DatabaseReader(Path.Combine(ModuleDirectory, "GeoLite2-Country.mmdb"));
            var response = reader.Country(IPAddress.Parse(ip));
            return response.Country.IsoCode ?? defaultLang;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ads] Country lookup error => {ex.Message}");
        }

        return defaultLang;
    }

    private string GetPlayerCity(string ip)
    {
        if (ip == "127.0.0.1") return "";
        try
        {
            using var reader = new DatabaseReader(Path.Combine(ModuleDirectory, "GeoLite2-City.mmdb"));
            var response = reader.City(IPAddress.Parse(ip));
            return response.City?.Name ?? "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ads] City lookup error => {ex.Message}");
        }

        return "";
    }
}

// Конфиг
public class Config
{
    public bool? PrintToCenterHtml { get; init; }
    public float? HtmlCenterDuration { get; init; }
    public bool? ShowHtmlWhenDead { get; set; }
    public WelcomeMessage? WelcomeMessage { get; init; }
    public List<Advertisement>? Ads { get; init; }
    public List<string>? Panel { get; init; }
    public string? DefaultLang { get; init; }
    public Dictionary<string, Dictionary<string, string>>? LanguageMessages { get; init; }
    public Dictionary<string, string>? MapsName { get; init; }

    // Новые поля
    public List<ServerInfo>? Servers { get; set; }
    public string? ConnectAnnounce { get; set; }
}

public class WelcomeMessage
{
    public MessageType MessageType { get; init; }
    public required string Message { get; init; }
    public float DisplayDelay { get; set; } = 2;
}

public class Advertisement
{
    public float Interval { get; init; }
    public List<Dictionary<string, string>> Messages { get; init; } = null!;

    private int _currentMessageIndex;
    [JsonIgnore] public Dictionary<string, string> NextMessages => Messages[_currentMessageIndex++ % Messages.Count];
}

public enum MessageType
{
    Chat = 0,
    Center,
    CenterHtml
}

public class ServerInfo
{
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public float Interval { get; set; }
    public string MessageTemplate { get; set; } = "";
}