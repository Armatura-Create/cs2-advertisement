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

// Класс для хранения текущего состояния игрока
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
    public override string ModuleVersion => "v1.2.1";

    private readonly List<Timer> _timers = [];
    private readonly List<Timer> _serverTimers = [];

    // Для определения страны/города
    private readonly Dictionary<ulong, string> _playerIsoCode = new();
    private readonly Dictionary<ulong, string> _playerCity = new();

    // Кеш для результатов опросов серверов
    // Ключ – (ip, port), значение – последний сформированный текст (или пусто, если ошибка)
    private readonly Dictionary<(string, int), string> _serverStatusCache = new();

    // Пользовательские данные по слотам
    private readonly User?[] _users = new User?[66];

    public Config Config { get; set; } = null!;

    public override void Load(bool hotReload)
    {
        // Загружаем конфиг
        Config = LoadConfig();

        // Регистрируем различные события
        RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnectPre, HookMode.Pre);
        RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnTick>(OnTick);

        // 1) Сразу делаем начальный опрос серверов (без анонса)
        //    чтобы при первом !servers у нас уже было что показать в кеше
        InitialServerQuery();

        // 2) Запускаем таймеры для рекламы и таймеры для повторного опроса
        StartTimers();
        StartServerTimers();

        if (!hotReload) return;
        
        foreach (var player in Utilities.GetPlayers())
            _users[player.Slot] = new User();
    }

    // --- События игрока ---

    private HookResult EventPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        var player = ev.Userid;
        if (player is null) return HookResult.Continue;

        if (!string.IsNullOrEmpty(Config.DisconnectMessage))
        {
            if (_playerIsoCode.TryGetValue(player.SteamID, out var country) &&
                _playerCity.TryGetValue(player.SteamID, out var city))
            {
                city = string.IsNullOrEmpty(city) ? "Unknown" : city; // Если город не найден, заменить на "Unknown"

                var connectMsg = Config.DisconnectMessage
                    .Replace("{PLAYERNAME}", player.PlayerName)
                    .Replace("{COUNTRY}", country)
                    .Replace("{CITY}", city);

                // Всем игрокам:
                PrintWrappedLine(HudDestination.Chat, connectMsg);
            }
            else
            {
                var connectMsg = Config.DisconnectMessage
                    .Replace("{PLAYERNAME}", player.PlayerName)
                    .Replace("{COUNTRY}", "")
                    .Replace("{CITY}", "");
                
                // Всем игрокам:
                PrintWrappedLine(HudDestination.Chat, connectMsg);
            }
        }

        _playerIsoCode.Remove(player.SteamID);
        _playerCity.Remove(player.SteamID);

        return HookResult.Continue;
    }
    
    private HookResult EventPlayerDisconnectPre(EventPlayerDisconnect ev, GameEventInfo info)
    {
        info.DontBroadcast = true;
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
        var player = ev.Userid;
        if (player is null || !player.IsValid)
            return HookResult.Continue;

        // Если ConnectAnnounce не пуст, оповестим всех о стране/городе
        if (!string.IsNullOrEmpty(Config.ConnectAnnounce))
        {
            if (_playerIsoCode.TryGetValue(player.SteamID, out var country) &&
                _playerCity.TryGetValue(player.SteamID, out var city))
            {
                city = string.IsNullOrEmpty(city) ? "Unknown" : city; // Если город не найден, заменить на "Unknown"

                var connectMsg = Config.ConnectAnnounce
                    .Replace("{PLAYERNAME}", player.PlayerName)
                    .Replace("{COUNTRY}", country)
                    .Replace("{CITY}", city);

                // Всем игрокам:
                PrintWrappedLine(HudDestination.Chat, connectMsg);
            }
            else
            {
                var connectMsg = Config.ConnectAnnounce
                    .Replace("{PLAYERNAME}", player.PlayerName)
                    .Replace("{COUNTRY}", "")
                    .Replace("{CITY}", "");
                
                // Всем игрокам:
                PrintWrappedLine(HudDestination.Chat, connectMsg);
            }
        }

        // Если WelcomeMessage отсутствует или пустая, ничего не выводим
        if (Config.WelcomeMessage == null || string.IsNullOrEmpty(Config.WelcomeMessage.Message))
            return HookResult.Continue;

        // Приветственное сообщение лично подключившемуся
        var welcomeMsg = Config.WelcomeMessage;
        var msg = welcomeMsg.Message
            .Replace("{PLAYERNAME}", player.PlayerName);

        AddTimer(Config.WelcomeMessage.DisplayDelay, () => { PrintWrappedLine(0, msg, player, true); });

        return HookResult.Continue;
    }

    // --- Основные таймеры ---

    private void StartTimers()
    {
        if (Config.Ads == null) return;
        foreach (var ad in Config.Ads)
        {
            _timers.Add(AddTimer(ad.Interval, () => ShowAd(ad), TimerFlags.REPEAT));
        }
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
                case "Console":
                    PrintWrappedLine(HudDestination.Console, message);
                    break;
            }
        }
    }

    // Опрос серверов по таймеру и их реклама
    private void StartServerTimers()
    {
        if (Config.Servers == null || Config.Servers.List.Count == 0) return;


        // Каждые serverInfo.Interval секунд делаем опрос
        _serverTimers.Add(AddTimer(Config.Servers.Interval, () =>
        {
            var print = false;
            foreach (var serverInfo in Config.Servers.List)
            {
                // Обновляем кеш (QueryServer)
                var success = QueryServer(serverInfo);
                // Если успешно, анонсируем всем (AnnounceServersInChat) с титулом (если есть)
                if (success)
                {
                    print = true;
                }
            }

            if (print)
                AnnounceServersInChat(isAd: true);
        }, TimerFlags.REPEAT));
    }

    // --- Команды ---

    // Команда для игрока: !servers
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [ConsoleCommand("css_servers", "Показать список серверов из кеша")]
    public void ShowServersCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null) return;

        if (Config.Servers == null || Config.Servers.List.Count == 0)
            return;

        // Печатаем СТРОГО без заголовка, только контент
        AnnounceServersToPlayer(controller);
    }

    // Команда перезагрузки конфига
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
        
        InitialServerQuery();

        // Повторно подгрузим язык/город для текущих игроков
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IpAddress == null || player.IsBot || !player.IsValid)
                continue;

            var ip = player.IpAddress.Split(':')[0];
            _playerIsoCode[player.SteamID] = GetPlayerIsoCode(ip);
            _playerCity[player.SteamID] = GetPlayerCity(ip);
        }

        // После Reload заново делаем начальный опрос
        InitialServerQuery();

        // И включаем таймеры
        StartTimers();
        StartServerTimers();

        const string msg = "[Advertisement] configuration successfully rebooted!";
        if (controller == null)
            Console.WriteLine(msg);
        else
            controller.PrintToChat(msg);
    }

    // --- Опрос серверов ---

    /// <summary>Единоразовый начальный опрос всех серверов (без анонса).</summary>
    private void InitialServerQuery()
    {
        if (Config.Servers == null || Config.Servers.List.Count == 0) return;

        foreach (var serverInfo in Config.Servers.List)
        {
            QueryServer(serverInfo); // просто заполняем кеш, не выводим в чат
        }
    }

    /// <summary>Опрос одного сервера, заполнение кеша.</summary>
    /// <returns>true, если удалось получить инфу</returns>
    private bool QueryServer(ServerData serverInfo)
    {
        try
        {
            var info = AdvancedA2S.GetServerInfo(serverInfo.Ip, (ushort)serverInfo.Port);
            if (info == null)
            {
                Console.WriteLine($"[Ads] {serverInfo.Ip}:{serverInfo.Port} -> info == null");
                _serverStatusCache.Remove((serverInfo.Ip, serverInfo.Port));
                return false;
            }

            // Сформируем строку по шаблону
            var msg = serverInfo.MessageTemplate
                .Replace("{SERVER_IP}", serverInfo.Ip)
                .Replace("{SERVER_PORT}", serverInfo.Port.ToString())
                .Replace("{SERVER_MAP}", info.Map.Trim())
                .Replace("{SERVER_PLAYERS}", (info.Players - info.Bots < 0 ? 0 : info.Players - info.Bots).ToString())
                .Replace("{SERVER_MAXPLAYERS}", info.MaxPlayers.ToString());

            _serverStatusCache[(serverInfo.Ip, serverInfo.Port)] = ProcessMessage(msg, 0);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ads] Ошибка опроса {serverInfo.Ip}:{serverInfo.Port} => {ex.Message}");
            _serverStatusCache.Remove((serverInfo.Ip, serverInfo.Port));
            return false;
        }
    }

    // --- Вывод списка серверов ---

    /// <summary>Анонсируем ВСЕМ игрокам (либо с заголовком, если isAd=true) список серверов из кеша.</summary>
    private void AnnounceServersInChat(bool isAd)
    {
        var players = Utilities.GetPlayers().Where(u => !u.IsBot && u.IsValid);
        if (!players.Any()) return; // никто не увидит

        // Если в конфиге прописан заголовок и это реклама — выведем его
        if (isAd && !string.IsNullOrEmpty(Config.TitleAnnounceServers))
        {
            PrintWrappedLine(HudDestination.Chat, Config.TitleAnnounceServers!);
        }

        // Теперь выводим строки из кеша
        foreach (var pair in _serverStatusCache)
        {
            var msg = pair.Value;
            if (!string.IsNullOrEmpty(msg))
                PrintWrappedLine(HudDestination.Chat, msg);
        }
    }

    /// <summary>Анонсируем ОДНОМУ игроку (без заголовка, если isAd=false) список серверов из кеша.</summary>
    private void AnnounceServersToPlayer(CCSPlayerController controller)
    {
        // Если реклама и есть заголовок — выводим, иначе пропускаем
        if (!string.IsNullOrEmpty(Config.TitleAnnounceServers))
        {
            PrintWrappedLine(HudDestination.Chat, Config.TitleAnnounceServers, controller, true);
        }

        // Выводим строки из кеша
        foreach (var pair in _serverStatusCache)
        {
            var msg = pair.Value;
            if (!string.IsNullOrEmpty(msg))
            {
                PrintWrappedLine(HudDestination.Chat, msg, controller, true);
            }
        }
    }

    // --- Логика вывода рекламы (OnTick и т.д.) ---

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

    // --- Вспомогательные методы ---

    private void PrintWrappedLine(HudDestination? destination, string message,
        CCSPlayerController? connectPlayer = null, bool privateMsg = false)
    {
        // Если это личное приветствие
        if (connectPlayer != null && connectPlayer is { IsValid: true, IsBot: false } && privateMsg)
        {
            var welcomeMessage = Config.WelcomeMessage;
            if (welcomeMessage == null || string.IsNullOrEmpty(welcomeMessage.Message)) return;

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
                case MessageType.Console:
                    connectPlayer.PrintToConsole(processed);
                    break;
                case MessageType.CenterHtml:
                    SetHtmlPrintSettings(connectPlayer, processed);
                    break;
            }
        }
        else
        {
            // Обычные сообщения всем игрокам
            foreach (var player in Utilities.GetPlayers()
                         .Where(u => !privateMsg && !u.IsBot && u.IsValid))
            {
                var processed = ProcessMessage(message, player.SteamID);

                switch (destination)
                {
                    case HudDestination.Chat:
                        player.PrintToChat(processed);
                        break;
                    case HudDestination.Console:
                        player.PrintToConsole(processed);
                        break;
                    default:
                        if (Config.PrintToCenterHtml == true)
                            SetHtmlPrintSettings(player, processed);
                        else
                            player.PrintToCenter(processed);
                        break;
                }
            }
        }

        if (!Config.Debug) return;
        {
            var processed = ProcessMessage(message, 0);
            Console.WriteLine("[ADS DEBUG] " + Regex.Replace(processed, "[\x01-\x10]", ""));
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
        // Если нет мульти-язычных сообщений, сразу подставим {MAP}, {TIME} и т.д.
        if (Config.LanguageMessages == null)
            return ReplaceMessageTags(message);

        // Иначе ищем теги {tag} => и пробуем найти их переводы
        var matches = Regex.Matches(message, @"\{([^}]*)\}");
        foreach (Match match in matches)
        {
            var tag = match.Groups[0].Value;
            var tagName = match.Groups[1].Value;

            if (!Config.LanguageMessages.TryGetValue(tagName, out var language))
                continue;

            var isoCode = steamId > 0 && _playerIsoCode.TryGetValue(steamId, out var code)
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
    
        // Основные замены
        var replacedMessage = message
            .Replace("{MAP}", mapName)
            .Replace("{TIME}", DateTime.Now.ToString("HH:mm:ss"))
            .Replace("{DATE}", DateTime.Now.ToString("dd.MM.yyyy"))
            .Replace("{SERVERNAME}", ConVar.Find("hostname")?.StringValue ?? "Server")
            .Replace("{IP}", ConVar.Find("ip")?.StringValue ?? "127.0.0.1")
            .Replace("{PORT}", ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString() ?? "27015")
            .Replace("{MAXPLAYERS}", Server.MaxPlayers.ToString())
            .Replace("{PLAYERS}", Utilities.GetPlayers().Count(u => u.PlayerPawn?.Value?.IsValid == true).ToString())
            .Replace("\n", "\u2029");

        // Проверяем {SERVER_MAP}, чтобы тоже подставлять кастомные названия карт
        if (Config.MapsName == null) return replacedMessage.ReplaceColorTags();
        
        foreach (var (key, niceName) in Config.MapsName)
        {
            replacedMessage = Regex.Replace(replacedMessage, $@"\b{Regex.Escape(key)}\b", niceName);
        }

        return replacedMessage.ReplaceColorTags();
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
            Debug = false,
            PrintToCenterHtml = false,
            WelcomeMessage = new WelcomeMessage
            {
                MessageType = MessageType.Chat,
                Message = "Welcome, {BLUE}{PLAYERNAME}",
                DisplayDelay = 5
            },
            DisconnectMessage = "Goodbye, {PLAYERNAME}",
            Ads =
            [
                new Advertisement
                {
                    Interval = 35,
                    Messages = new List<Dictionary<string, string>>
                    {
                        new() { ["Chat"] = "{map_name}", ["Center"] = "Section 1 Center 1" },
                        new() { ["Chat"] = "{current_time}" }
                    }
                },

                new Advertisement
                {
                    Interval = 40,
                    Messages = new List<Dictionary<string, string>>
                    {
                        new() { ["Chat"] = "Section 2 Chat 1" },
                        new() { ["Chat"] = "Section 2 Chat 2", ["Center"] = "Section 2 Center 1" }
                    }
                }
            ],
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
            TitleAnnounceServers = "Список серверов:",
            Servers = new ServerInfo
            {
                Interval = 65,
                List =
                [
                    new ServerData
                    {
                        Ip = "127.0.0.1",
                        Port = 27015,
                        MessageTemplate =
                            "{SERVER_IP}:{SERVER_PORT} - {SERVER_MAP} | {SERVER_PLAYERS}/{SERVER_MAXPLAYERS}",
                        MessageTemplateConsole = "{SERVER_IP}:{SERVER_PORT} - {SERVER_MAP} | {SERVER_PLAYERS}/{SERVER_MAXPLAYERS}"
                    }
                ]
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

// ----------------- Конфигурация -------------------
public class Config
{
    public bool? PrintToCenterHtml { get; init; }
    public float? HtmlCenterDuration { get; init; }
    public bool? ShowHtmlWhenDead { get; set; }
    public bool Debug { get; set; } = false;
    public WelcomeMessage? WelcomeMessage { get; init; }
    public string? DisconnectMessage { get; init; }
    public List<Advertisement>? Ads { get; init; }
    public List<string>? Panel { get; init; }
    public string? DefaultLang { get; init; }
    public Dictionary<string, Dictionary<string, string>>? LanguageMessages { get; init; }
    public Dictionary<string, string>? MapsName { get; init; }

    // Новые поля
    public string? TitleAnnounceServers { get; set; }
    public ServerInfo? Servers { get; init; }
    public string? ConnectAnnounce { get; set; }
}

// Параметры приветствия
public class WelcomeMessage
{
    public MessageType MessageType { get; init; }
    public required string Message { get; init; }
    public float DisplayDelay { get; set; } = 2;
}

// Блок рекламы
public class Advertisement
{
    public float Interval { get; init; }
    public List<Dictionary<string, string>> Messages { get; init; } = null!;

    private int _currentMessageIndex;
    [JsonIgnore] public Dictionary<string, string> NextMessages => Messages[_currentMessageIndex++ % Messages.Count];
}

// Типы сообщений
public enum MessageType
{
    Chat = 0,
    Center,
    CenterHtml,
    Console
}

// Данные о внешнем сервере
public class ServerInfo
{
    public float Interval { get; set; } // как часто опрашивать
    public List<ServerData> List { get; set; }
}

public class ServerData
{
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string MessageTemplate { get; set; } = "";
    public string MessageTemplateConsole { get; set; } = "";
}