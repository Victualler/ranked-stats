using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;
using Amazon;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

class Program {
    const int APPID = 570; // Dota 2 AppID
    const string OUTPUT_FILE = "matches.json";
    const string PROFILE_FILE = "profile.json";

    static SteamClient steamClient = null!;
    static CallbackManager manager = null!;
    static SteamGameCoordinator coordinator = null!;
    static SteamUser steamUser = null!;

    static bool dotaSessionStarted = false;
    static readonly object launchLock = new();
    static CancellationTokenSource globalCts = new(TimeSpan.FromMinutes(25));

    static string user = string.Empty;
    static string pass = string.Empty;
    static string refreshToken = string.Empty;
    static string sharedSecret = string.Empty;

    static AmazonSimpleSystemsManagementClient? ssmClient;
    static string ssmParamName = "/spr_steam/secrets";
    static SteamSecrets? loadedSsmSecrets;

    static ulong matches_start_at_id = 0;
    static ulong latest_saved_mid = 0;
    static int matches_requested = 20;
    static int matches_remaining = 0;
    static int matches_count = -1; // -1 = fetch all new matches until latest_saved_mid
    static bool isRunning = false;
    static bool haveConnected = false;
    static bool syncSucceeded = false;

    // Async pipeline synchronization states
    static bool historyFetched = false;
    static bool detailsFetched = false;
    static bool profileFetched = false;
    static readonly Queue<ulong> matchDetailsQueue = new();

    static List<MatchRecord> existingMatches = new();
    static Dictionary<ulong, MatchRecord> newMatchRecords = new();
    static ProfileRecord? currentProfile;
    static Dictionary<int, string> heroIdToName = new();

    static async Task<int> Main(string[] args) {
        Console.WriteLine("Starting Headless Dota 2 MMR Tracker & Stats Sync...");

        // 1. Fetch live hero dictionary dynamically from OpenDota constants
        await LoadHeroMapAsync();

        // 2. Resolve secrets from AWS SSM Parameter Store or Environment Variables
        await LoadCredentialsAsync();

        if (string.IsNullOrEmpty(user) || (string.IsNullOrEmpty(refreshToken) && string.IsNullOrEmpty(pass))) {
            Console.Error.WriteLine("Error: Missing credentials. Provide AWS SSM credentials or set STEAM_USER and STEAM_REFRESH_TOKEN.");
            return 1;
        }

        // 3. Parse match count argument if passed (default -1 to fetch all new matches)
        if (args.Length >= 1 && int.TryParse(args[0], out int parsedCount)) {
            matches_count = parsedCount;
        } else if (int.TryParse(Environment.GetEnvironmentVariable("MATCHES_COUNT"), out int envCount)) {
            matches_count = envCount;
        }

        // 4. Load existing matches.json to determine latest saved match ID
        existingMatches = LoadExistingMatches(OUTPUT_FILE);
        latest_saved_mid = existingMatches.Count > 0 ? existingMatches[0].MatchId : 0;
        Console.WriteLine($"Loaded {existingMatches.Count} existing matches. Latest Match ID: {latest_saved_mid}");

        matches_remaining = matches_count > 0 ? matches_count : int.MaxValue;

        // 5. Initialize Steam client
        steamClient = new SteamClient();
        manager = new CallbackManager(steamClient);
        steamUser = steamClient.GetHandler<SteamUser>()!;
        coordinator = steamClient.GetHandler<SteamGameCoordinator>()!;

        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

        isRunning = true;
        haveConnected = false;

        Console.WriteLine("Connecting to Steam...");
        steamClient.Connect();

        while (isRunning && !globalCts.Token.IsCancellationRequested) {
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        if (globalCts.Token.IsCancellationRequested || !syncSucceeded) {
            Console.Error.WriteLine("Error: Sync failed to complete cleanly (timeout, unexpected disconnect, or GC communication failure).");
            steamClient.Disconnect();
            return 1;
        }

        // 6. Merge, sort, and save matches
        if (newMatchRecords.Count > 0) {
            Console.WriteLine($"Merging {newMatchRecords.Count} newly fetched ranked matches...");
            var combinedMap = new Dictionary<ulong, MatchRecord>();

            foreach (var match in newMatchRecords.Values.Concat(existingMatches)) {
                if (!combinedMap.ContainsKey(match.MatchId)) {
                    combinedMap[match.MatchId] = match;
                }
            }

            var sortedList = combinedMap.Values.OrderByDescending(m => m.StartTime).ToList();

            // Monotonic count check: prevent silent data truncation or accidental empty writes
            if (sortedList.Count < existingMatches.Count) {
                Console.Error.WriteLine($"Integrity check failed: Attempted to save {sortedList.Count} matches, but {existingMatches.Count} previously existed. Aborting write.");
                return 1;
            }

            SaveMatchesToJson(sortedList, OUTPUT_FILE);
            Console.WriteLine($"Saved {sortedList.Count} matches to {OUTPUT_FILE}");
        } else {
            Console.WriteLine("No new matches found. JSON file remains up to date.");
        }

        if (currentProfile != null) {
            SaveProfileToJson(currentProfile, PROFILE_FILE);
            Console.WriteLine($"Saved profile stats to {PROFILE_FILE}");
        }

        return 0;
    }

    // -----------------------------------------------------------------------------------------
    // Dynamic Hero Constants
    // -----------------------------------------------------------------------------------------

    static async Task LoadHeroMapAsync() {
        try {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Dota2Tracker/1.0");

            string json = await client.GetStringAsync("https://raw.githubusercontent.com/odota/dotaconstants/master/build/heroes.json");
            using var doc = JsonDocument.Parse(json);

            foreach (var prop in doc.RootElement.EnumerateObject()) {
                if (int.TryParse(prop.Name, out int heroId)) {
                    if (prop.Value.TryGetProperty("localized_name", out var locName)) {
                        var name = locName.GetString();
                        if (!string.IsNullOrEmpty(name)) {
                            heroIdToName[heroId] = name;
                        }
                    }
                }
            }
            Console.WriteLine($"Loaded {heroIdToName.Count} heroes from dynamic registry.");
        } catch (Exception ex) {
            Console.WriteLine($"Warning: Failed to fetch live hero map ({ex.Message}). Fallback names will be used.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Credentials & AWS SSM Handling
    // -----------------------------------------------------------------------------------------

    static async Task LoadCredentialsAsync() {
        var envParamName = Environment.GetEnvironmentVariable("SSM_PARAM_NAME");
        if (!string.IsNullOrEmpty(envParamName)) {
            ssmParamName = envParamName;
        }

        try {
            var regionName = Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-west-2";
            var region = RegionEndpoint.GetBySystemName(regionName);
            ssmClient = new AmazonSimpleSystemsManagementClient(region);

            Console.WriteLine($"Attempting to fetch secrets from AWS SSM parameter '{ssmParamName}' ({region.SystemName})...");
            var request = new GetParameterRequest {
                Name = ssmParamName,
                WithDecryption = true
            };

            var response = await ssmClient.GetParameterAsync(request);
            if (response?.Parameter?.Value != null) {
                loadedSsmSecrets = JsonSerializer.Deserialize<SteamSecrets>(response.Parameter.Value);
                if (loadedSsmSecrets != null) {
                    user = loadedSsmSecrets.AccountName ?? string.Empty;
                    pass = loadedSsmSecrets.Password ?? string.Empty;
                    refreshToken = loadedSsmSecrets.RefreshToken ?? string.Empty;
                    sharedSecret = loadedSsmSecrets.SharedSecret ?? string.Empty;
                    Console.WriteLine("Successfully loaded Steam credentials from AWS SSM.");
                    return;
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"AWS SSM fetch skipped or failed: {ex.Message}. Falling back to environment variables.");
        }

        user = Environment.GetEnvironmentVariable("STEAM_USER") ?? string.Empty;
        pass = Environment.GetEnvironmentVariable("STEAM_PASSWORD") ?? string.Empty;
        refreshToken = Environment.GetEnvironmentVariable("STEAM_REFRESH_TOKEN") ?? string.Empty;
        sharedSecret = Environment.GetEnvironmentVariable("STEAM_SHARED_SECRET") ?? string.Empty;
    }

    static async Task PersistUpdatedTokenAsync(string newToken) {
        refreshToken = newToken;

        if (ssmClient != null && loadedSsmSecrets != null) {
            try {
                loadedSsmSecrets.RefreshToken = newToken;
                string updatedJson = JsonSerializer.Serialize(loadedSsmSecrets, new JsonSerializerOptions { WriteIndented = true });

                var putReq = new PutParameterRequest {
                    Name = ssmParamName,
                    Value = updatedJson,
                    Type = ParameterType.SecureString,
                    Overwrite = true
                };

                await ssmClient.PutParameterAsync(putReq);
                Console.WriteLine("Successfully updated rotated refresh token in AWS SSM Parameter Store.");
            } catch (Exception ex) {
                Console.WriteLine($"Failed to write updated refresh token back to SSM: {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // SteamKit Event Handlers
    // -----------------------------------------------------------------------------------------

    static async void OnConnected(SteamClient.ConnectedCallback callback) {
        Console.WriteLine($"Connected to Steam! Authenticating '{user}'...");
        haveConnected = true;

        try {
            if (!string.IsNullOrEmpty(refreshToken)) {
                steamUser.LogOn(new SteamUser.LogOnDetails {
                    Username = user,
                    AccessToken = refreshToken,
                });
            } else if (!string.IsNullOrEmpty(pass)) {
                if (string.IsNullOrEmpty(sharedSecret)) {
                    Console.Error.WriteLine("Error: Headless login with password requires STEAM_SHARED_SECRET for 2FA.");
                    isRunning = false;
                    steamClient.Disconnect();
                    return;
                }

                var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                    new AuthSessionDetails {
                        Username = user,
                        Password = pass,
                        IsPersistentSession = true,
                        Authenticator = new HeadlessTotpAuthenticator(sharedSecret),
                    }
                );

                var pollResponse = await authSession.PollingWaitForResultAsync();
                await PersistUpdatedTokenAsync(pollResponse.RefreshToken);

                steamUser.LogOn(new SteamUser.LogOnDetails {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken,
                });
            } else {
                Console.Error.WriteLine("No valid refresh token or password available to authenticate.");
                isRunning = false;
                steamClient.Disconnect();
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"Authentication error: {ex.Message}");
            isRunning = false;
            steamClient.Disconnect();
        }
    }

    static void OnDisconnected(SteamClient.DisconnectedCallback callback) {
        Console.WriteLine("Disconnected from Steam");
        if (!haveConnected) {
            Console.WriteLine("Retrying connection in 5 seconds...");
            Thread.Sleep(TimeSpan.FromSeconds(5));
            steamClient.Connect();
        } else {
            isRunning = false;
        }
    }

    static async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
        if (callback.Result != EResult.OK) {
            Console.WriteLine($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");

            if ((callback.Result == EResult.InvalidPassword || callback.Result == EResult.Expired) &&
                !string.IsNullOrEmpty(pass) && !string.IsNullOrEmpty(sharedSecret)) {
                Console.WriteLine("Refresh token expired. Attempting automated re-authentication with sharedSecret...");
                try {
                    var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                        new AuthSessionDetails {
                            Username = user,
                            Password = pass,
                            IsPersistentSession = true,
                            Authenticator = new HeadlessTotpAuthenticator(sharedSecret),
                        }
                    );

                    var pollResponse = await authSession.PollingWaitForResultAsync();
                    await PersistUpdatedTokenAsync(pollResponse.RefreshToken);

                    steamUser.LogOn(new SteamUser.LogOnDetails {
                        Username = pollResponse.AccountName,
                        AccessToken = pollResponse.RefreshToken,
                    });
                    return;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"Automated re-authentication failed: {ex.Message}");
                }
            }

            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        Console.WriteLine("Logged in! Launching Dota 2 session...");
        LaunchDotaSession();
    }

    static void LaunchDotaSession() {
        lock (launchLock) {
            if (dotaSessionStarted || !isRunning) return;
            dotaSessionStarted = true;
        }

        Task.Run(async () => {
            Console.WriteLine("Launching Dota 2 session...");

            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
                game_id = new GameID(APPID),
            });
            steamClient.Send(playGame);

            await Task.Delay(5000);

            if (!isRunning) return;

            var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>(
                (uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            clientHello.Body.engine = ESourceEngine.k_ESE_Source2;
            coordinator.Send(clientHello, APPID);
        });
    }

    static void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
        Console.WriteLine($"Logged off of Steam: {callback.Result}");

        // If kicked because your desktop client is running, skip cleanly with code 0
        if (callback.Result == EResult.LoggedInElsewhere ||
            callback.Result == EResult.LogonSessionReplaced ||
            callback.Result == EResult.AlreadyLoggedInElsewhere) {
            Console.WriteLine("Desktop session is currently active. Skipping sync until next run.");
            isRunning = false;
            steamClient.Disconnect();
            Environment.Exit(0);
            return;
        }

        isRunning = false;
        steamClient.Disconnect();
    }

    static void OnGCMessage(SteamGameCoordinator.MessageCallback callback) {
        var messageMap = new Dictionary<uint, Action<IPacketGCMsg>> {
            { (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
            { (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistoryResponse, OnMatchHistory },
            { (uint)EDOTAGCMsg.k_EMsgGCMatchDetailsResponse, OnMatchDetails },
            { (uint)EDOTAGCMsg.k_EMsgClientToGCGetProfileCardResponse, OnProfileCard },
        };

        if (messageMap.TryGetValue(callback.EMsg, out var func)) {
            func(callback.Message);
        }
    }

    // -----------------------------------------------------------------------------------------
    // GC Request / Response Handlers
    // -----------------------------------------------------------------------------------------

    static void OnClientWelcome(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);
        Console.WriteLine($"GC connected. Version: {msg.Body.version}");

        // 1. Trigger Profile Card fetch
        Console.WriteLine("Requesting account profile card...");
        var profileReq = new ClientGCMsgProtobuf<CMsgClientToGCGetProfileCard>(
            (uint)EDOTAGCMsg.k_EMsgClientToGCGetProfileCard);
        profileReq.Body.account_id = steamUser.SteamID!.AccountID;
        coordinator.Send(profileReq, APPID);

        // 2. Trigger Match History pagination
        if (matches_count > 0)
            Console.WriteLine($"Requesting up to {matches_count} matches...");
        else
            Console.WriteLine("Requesting matches until last stored match ID is reached...");

        FetchMatches();
    }

    static void OnProfileCard(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetProfileCardResponse>(packetMsg);
        Console.WriteLine($"Received Profile Card. Rank Tier: {msg.Body.rank_tier}, Leaderboard: {msg.Body.leaderboard_rank}");

        currentProfile = new ProfileRecord {
            AccountId = msg.Body.account_id,
            LastUpdatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
            RankTier = msg.Body.rank_tier,
            RankName = GetRankTierName(msg.Body.rank_tier),
            LeaderboardRank = msg.Body.leaderboard_rank,
            BadgePoints = msg.Body.badge_points,
            LifetimeGames = msg.Body.lifetime_games,
            LifetimeWins = msg.Body.lifetime_wins,
            WinRatePercent = msg.Body.lifetime_games > 0 
                ? Math.Round((double)msg.Body.lifetime_wins / msg.Body.lifetime_games * 100, 2) 
                : 0,
            ShowcaseSlots = msg.Body.slots?.Select(s => new ProfileSlot {
                SlotId = s.slot_id,
                StatId = s.stat_id,
                StatScore = s.stat_score,
                HeroId = s.hero_id
            }).ToList() ?? new List<ProfileSlot>()
        };

        profileFetched = true;
        CheckSyncCompletion();
    }

    static void FetchMatches() {
        matches_requested = 20;
        matches_remaining -= matches_requested;

        var requestHistory = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistory>(
            (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistory);
        requestHistory.Body.account_id = steamUser.SteamID!.AccountID;
        requestHistory.Body.matches_requested = (uint)matches_requested;

        if (matches_start_at_id > 0) {
            requestHistory.Body.start_at_match_id = matches_start_at_id;
        }

        coordinator.Send(requestHistory, APPID);
    }

    static void OnMatchHistory(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistoryResponse>(packetMsg);
        bool reachedExistingMatch = false;

        foreach (var match in msg.Body.matches) {
            // Stop condition: reach the most recently recorded match ID
            if (match.match_id == latest_saved_mid) {
                reachedExistingMatch = true;
                if (matches_count < 0) {
                    matches_remaining = 0;
                }
                break;
            }

            // Identify ranked matches:
            // lobby_type 7 = Ranked (captures normal ranked + calibration games)
            // rank_change != 0 or previous_rank != 0 = Standard post-calibration ranked
            bool isRanked = match.lobby_type == 7 || match.previous_rank != 0 || match.rank_change != 0;

            if (isRanked && match.start_time != 0 && match.match_id != 0) {
                bool isCalib = match.previous_rank == 0 && match.rank_change == 0;
                uint? calculatedEndMmr = (!isCalib && match.previous_rank > 0)
                    ? (uint)Math.Max(0, (int)match.previous_rank + match.rank_change)
                    : null;

                var record = new MatchRecord {
                    MatchId = match.match_id,
                    StartTime = match.start_time,
                    Date = FormatTime(match.start_time),
                    SoloQueue = match.solo_rank,
                    HeroId = match.hero_id,
                    HeroName = heroIdToName.TryGetValue(match.hero_id, out var hName) ? hName : $"Hero {match.hero_id}",
                    StartMmr = match.previous_rank,
                    RankChange = match.rank_change,
                    EndMmr = calculatedEndMmr,
                    IsCalibration = isCalib,
                    LobbyType = (int)match.lobby_type,
                    GameMode = (int)match.game_mode,
                    PlayerSlot = match.player_slot,
                    Kills = match.kills,
                    Deaths = match.deaths,
                    Assists = match.assists
                };

                if (!newMatchRecords.ContainsKey(match.match_id)) {
                    newMatchRecords[match.match_id] = record;
                }
            }

            matches_start_at_id = match.match_id;
        }

        if (reachedExistingMatch || matches_remaining <= 0 || msg.Body.matches.Count == 0 || msg.Body.matches.Count < matches_requested) {
            Console.WriteLine($"Match history pagination complete. Found {newMatchRecords.Count} new ranked matches.");
            historyFetched = true;

            // Enqueue all newly discovered matches for Deep Match Details inspection
            foreach (var mid in newMatchRecords.Keys) {
                matchDetailsQueue.Enqueue(mid);
            }

            if (matchDetailsQueue.Count > 0) {
                Console.WriteLine($"Enqueuing {matchDetailsQueue.Count} matches for deep scoreboard details...");
                FetchNextMatchDetail();
            } else {
                detailsFetched = true;
                CheckSyncCompletion();
            }
        } else {
            Thread.Sleep(1000); // Respect GC 1 req/sec rate limit
            Console.WriteLine($"Fetched page. Next start_at_match_id: {matches_start_at_id}");
            FetchMatches();
        }
    }

    static void FetchNextMatchDetail() {
        if (matchDetailsQueue.Count == 0) {
            Console.WriteLine("Finished fetching all deep match details.");
            detailsFetched = true;
            CheckSyncCompletion();
            return;
        }

        ulong matchId = matchDetailsQueue.Dequeue();
        Console.WriteLine($"Requesting deep details for match {matchId} ({matchDetailsQueue.Count} remaining)...");

        var req = new ClientGCMsgProtobuf<CMsgGCMatchDetailsRequest>(
            (uint)EDOTAGCMsg.k_EMsgGCMatchDetailsRequest);
        req.Body.match_id = matchId;
        coordinator.Send(req, APPID);
    }

    static void OnMatchDetails(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgGCMatchDetailsResponse>(packetMsg);

        if (msg.Body.match != null) {
            var m = msg.Body.match;
            if (newMatchRecords.TryGetValue(m.match_id, out var record)) {
                record.DurationSeconds = m.duration;
                record.FirstBloodTime = m.first_blood_time;

                uint myAccountId = steamUser.SteamID!.AccountID;
                var player = m.players.FirstOrDefault(p => p.account_id == myAccountId);

                if (player != null) {
                    bool isRadiant = player.player_slot < 128;
                    record.Team = isRadiant ? "radiant" : "dire";
                    record.Win = isRadiant ? m.good_guys_win : !m.good_guys_win;
                    record.Level = player.level;
                    record.Kills = player.kills;
                    record.Deaths = player.deaths;
                    record.Assists = player.assists;
                    record.LastHits = player.last_hits;
                    record.Denies = player.denies;
                    record.Gpm = player.gold_per_min;
                    record.Xpm = player.xp_per_min;
                    record.NetWorth = player.net_worth;
                    record.HeroDamage = player.hero_damage;
                    record.TowerDamage = player.tower_damage;
                    record.HeroHealing = player.hero_healing;
                    record.LeaverStatus = (int)player.leaver_status;

                    if (player.deaths > 0) {
                        record.Kda = Math.Round((double)(player.kills + player.assists) / player.deaths, 2);
                    } else {
                        record.Kda = Math.Round((double)(player.kills + player.assists), 2);
                    }

                    record.Items = new List<int> {
                        (int)player.item_0, (int)player.item_1, (int)player.item_2,
                        (int)player.item_3, (int)player.item_4, (int)player.item_5
                    };

                    record.Backpack = new List<int> {
                        (int)player.backpack_0, (int)player.backpack_1, (int)player.backpack_2
                    };

                    record.NeutralItem = (int)player.item_neutral;
                }
            }
        }

        // Respect GC 1 req/sec rate limit before triggering the next queued match detail
        Thread.Sleep(1000);
        FetchNextMatchDetail();
    }

    static void CheckSyncCompletion() {
        if (profileFetched && historyFetched && detailsFetched) {
            Console.WriteLine("All data synchronization steps completed successfully.");
            syncSucceeded = true;
            isRunning = false;
            steamClient.Disconnect();
        }
    }

    // -----------------------------------------------------------------------------------------
    // File I/O & Helpers
    // -----------------------------------------------------------------------------------------

    static List<MatchRecord> LoadExistingMatches(string path) {
        if (!File.Exists(path)) return new List<MatchRecord>();

        try {
            string json = File.ReadAllText(path);
            var records = JsonSerializer.Deserialize<List<MatchRecord>>(json);
            return records ?? new List<MatchRecord>();
        } catch (Exception ex) {
            Console.WriteLine($"Notice: Unable to parse existing {path}: {ex.Message}. Starting fresh.");
            return new List<MatchRecord>();
        }
    }

    static void SaveMatchesToJson(List<MatchRecord> records, string path) {
        var options = new JsonSerializerOptions { 
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull 
        };
        string json = JsonSerializer.Serialize(records, options);
        File.WriteAllText(path, json);
    }

    static void SaveProfileToJson(ProfileRecord profile, string path) {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(profile, options);
        File.WriteAllText(path, json);
    }

    static string FormatTime(uint unixTime) {
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().AddSeconds(unixTime).ToString("yyyy-MM-dd HH:mm:ss");
    }

    static string GetRankTierName(uint tier) {
        if (tier == 0) return "Uncalibrated";
        uint medal = tier / 10;
        uint stars = tier % 10;
        string medalName = medal switch {
            1 => "Herald",
            2 => "Guardian",
            3 => "Crusader",
            4 => "Archon",
            5 => "Legend",
            6 => "Ancient",
            7 => "Divine",
            8 => "Immortal",
            _ => "Unknown"
        };
        return medal == 8 ? "Immortal" : $"{medalName} {stars}";
    }

    // -----------------------------------------------------------------------------------------
    // Headless TOTP Authenticator (RFC 6238 Steam Guard Implementation)
    // -----------------------------------------------------------------------------------------

    public class HeadlessTotpAuthenticator : IAuthenticator {
        private readonly string _sharedSecret;
        private static readonly char[] SteamAlphabet = "23456789BCDFGHJKMNPQRTVWXY".ToCharArray();

        public HeadlessTotpAuthenticator(string sharedSecret) {
            _sharedSecret = sharedSecret;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
            return Task.FromResult(GenerateSteamCode(_sharedSecret));
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
            Task.FromResult(string.Empty);

        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

        public static string GenerateSteamCode(string secret) {
            if (string.IsNullOrEmpty(secret)) return string.Empty;

            byte[] key = Convert.FromBase64String(secret);
            long timeSlice = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

            byte[] timeBytes = BitConverter.GetBytes(timeSlice);
            if (BitConverter.IsLittleEndian) {
                Array.Reverse(timeBytes);
            }

            using var hmac = new HMACSHA1(key);
            byte[] hash = hmac.ComputeHash(timeBytes);

            int offset = hash[^1] & 0x0F;
            int fullCode = ((hash[offset] & 0x7F) << 24)
                         | ((hash[offset + 1] & 0xFF) << 16)
                         | ((hash[offset + 2] & 0xFF) << 8)
                         | (hash[offset + 3] & 0xFF);

            char[] code = new char[5];
            for (int i = 0; i < 5; i++) {
                code[i] = SteamAlphabet[fullCode % SteamAlphabet.Length];
                fullCode /= SteamAlphabet.Length;
            }

            return new string(code);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Data Models
    // -----------------------------------------------------------------------------------------

    public class SteamSecrets {
        [JsonPropertyName("username")]
        public string AccountName { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("sharedSecret")]
        public string SharedSecret { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    }

    public class ProfileRecord {
        [JsonPropertyName("account_id")]
        public uint AccountId { get; set; }

        [JsonPropertyName("last_updated_utc")]
        public string LastUpdatedUtc { get; set; } = string.Empty;

        [JsonPropertyName("rank_tier")]
        public uint RankTier { get; set; }

        [JsonPropertyName("rank_name")]
        public string RankName { get; set; } = string.Empty;

        [JsonPropertyName("leaderboard_rank")]
        public uint LeaderboardRank { get; set; }

        [JsonPropertyName("badge_points")]
        public uint BadgePoints { get; set; }

        [JsonPropertyName("lifetime_games")]
        public uint LifetimeGames { get; set; }

        [JsonPropertyName("lifetime_wins")]
        public uint LifetimeWins { get; set; }

        [JsonPropertyName("win_rate_percent")]
        public double WinRatePercent { get; set; }

        [JsonPropertyName("showcase_slots")]
        public List<ProfileSlot> ShowcaseSlots { get; set; } = new();
    }

    public class ProfileSlot {
        [JsonPropertyName("slot_id")]
        public uint SlotId { get; set; }

        [JsonPropertyName("stat_id")]
        public uint StatId { get; set; }

        [JsonPropertyName("stat_score")]
        public uint StatScore { get; set; }

        [JsonPropertyName("hero_id")]
        public uint HeroId { get; set; }
    }

    public class MatchRecord {
        // Core History Identifiers
        [JsonPropertyName("match_id")]
        public ulong MatchId { get; set; }

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("unix_time")]
        public uint StartTime { get; set; }

        [JsonPropertyName("solo_queue")]
        public bool SoloQueue { get; set; }

        [JsonPropertyName("lobby_type")]
        public int LobbyType { get; set; }

        [JsonPropertyName("game_mode")]
        public int GameMode { get; set; }

        [JsonPropertyName("is_calibration")]
        public bool IsCalibration { get; set; }

        // MMR Metrics
        [JsonPropertyName("start_mmr")]
        public uint StartMmr { get; set; }

        [JsonPropertyName("rank_change")]
        public int RankChange { get; set; }

        [JsonPropertyName("end_mmr")]
        public uint? EndMmr { get; set; }

        // Hero Details
        [JsonPropertyName("hero_id")]
        public int HeroId { get; set; }

        [JsonPropertyName("hero_name")]
        public string HeroName { get; set; } = string.Empty;

        // Deep Match Scoreboard (Populated from CMsgGCMatchDetailsResponse)
        [JsonPropertyName("duration_seconds")]
        public uint? DurationSeconds { get; set; }

        [JsonPropertyName("first_blood_time")]
        public uint? FirstBloodTime { get; set; }

        [JsonPropertyName("win")]
        public bool? Win { get; set; }

        [JsonPropertyName("team")]
        public string? Team { get; set; }

        [JsonPropertyName("player_slot")]
        public uint? PlayerSlot { get; set; }

        [JsonPropertyName("level")]
        public uint? Level { get; set; }

        [JsonPropertyName("kills")]
        public uint? Kills { get; set; }

        [JsonPropertyName("deaths")]
        public uint? Deaths { get; set; }

        [JsonPropertyName("assists")]
        public uint? Assists { get; set; }

        [JsonPropertyName("kda")]
        public double? Kda { get; set; }

        [JsonPropertyName("last_hits")]
        public uint? LastHits { get; set; }

        [JsonPropertyName("denies")]
        public uint? Denies { get; set; }

        [JsonPropertyName("net_worth")]
        public uint? NetWorth { get; set; }

        [JsonPropertyName("gpm")]
        public uint? Gpm { get; set; }

        [JsonPropertyName("xpm")]
        public uint? Xpm { get; set; }

        [JsonPropertyName("hero_damage")]
        public uint? HeroDamage { get; set; }

        [JsonPropertyName("tower_damage")]
        public uint? TowerDamage { get; set; }

        [JsonPropertyName("hero_healing")]
        public uint? HeroHealing { get; set; }

        [JsonPropertyName("leaver_status")]
        public int? LeaverStatus { get; set; }

        [JsonPropertyName("items")]
        public List<int>? Items { get; set; }

        [JsonPropertyName("backpack")]
        public List<int>? Backpack { get; set; }

        [JsonPropertyName("neutral_item")]
        public int? NeutralItem { get; set; }
    }
}
