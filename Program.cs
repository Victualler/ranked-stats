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

    static SteamClient steamClient = null!;
    static CallbackManager manager = null!;
    static SteamGameCoordinator coordinator = null!;
    static SteamUser steamUser = null!;
    static SteamFriends steamFriends = null!;

    static bool dotaSessionStarted = false;
    static readonly object launchLock = new();
    static TaskCompletionSource<bool> personaCheckTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    static List<MatchRecord> existingMatches = new();
    static List<CMsgDOTAGetPlayerMatchHistoryResponse.Match> newlyFetchedMatches = new();
    static Dictionary<int, string> heroIdToName = new();

    static async Task<int> Main(string[] args) {
        Console.WriteLine("Starting Headless Dota 2 MMR Tracker...");

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
        steamFriends = steamClient.GetHandler<SteamFriends>()!;

        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        manager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
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
        if (newlyFetchedMatches.Count > 0) {
            Console.WriteLine($"Merging {newlyFetchedMatches.Count} newly fetched matches...");
            var newRecords = newlyFetchedMatches.Select(m => new MatchRecord {
                MatchId = m.match_id,
                StartTime = m.start_time,
                Date = FormatTime(m.start_time),
                SoloQueue = m.solo_rank,
                HeroId = m.hero_id,
                HeroName = heroIdToName.TryGetValue(m.hero_id, out var hName) ? hName : $"Hero {m.hero_id}",
                StartMmr = m.previous_rank,
                RankChange = m.rank_change
            }).ToList();

            var combinedMap = new Dictionary<ulong, MatchRecord>();
            foreach (var match in newRecords.Concat(existingMatches)) {
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
            var regionName = Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1";
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

        Console.WriteLine("Logged in! Checking for active game sessions...");
        steamFriends.RequestFriendInfo(steamUser.SteamID!, EClientPersonaStateFlag.Status | EClientPersonaStateFlag.GameExtraInfo);

        // Explicitly await persona data up to 5 seconds
        var completedTask = await Task.WhenAny(personaCheckTcs.Task, Task.Delay(5000));

        if (completedTask == personaCheckTcs.Task && await personaCheckTcs.Task) {
            Console.WriteLine("Active game session confirmed. Aborting sync.");
            isRunning = false;
            steamClient.Disconnect();
            Environment.Exit(0);
            return;
        }

        LaunchDotaSession();
    }

    static void OnPersonaState(SteamFriends.PersonaStateCallback callback) {
        if (callback.FriendID == steamUser.SteamID) {
            bool isPlaying = callback.GameID != null && callback.GameID.AppID != 0;
            if (isPlaying) {
                Console.WriteLine($"Active game detected: AppID {callback.GameID!.AppID} ('{callback.GameName}').");
            }
            personaCheckTcs.TrySetResult(isPlaying);
        }
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

            var clientHello = new ClientGCMsgProtobuf<SteamKit2.GC.Dota.Internal.CMsgClientHello>(
                (uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            clientHello.Body.engine = ESourceEngine.k_ESE_Source2;
            coordinator.Send(clientHello, APPID);
        });
    }

    static void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
        Console.WriteLine("Logged off of Steam");
        isRunning = false;
        steamClient.Disconnect();
    }

    static void OnGCMessage(SteamGameCoordinator.MessageCallback callback) {
        var messageMap = new Dictionary<uint, Action<IPacketGCMsg>> {
            { (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
            { (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistoryResponse, OnMatchHistory },
        };

        if (messageMap.TryGetValue(callback.EMsg, out var func)) {
            func(callback.Message);
        }
    }

    static void OnClientWelcome(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);
        Console.WriteLine($"GC connected. Version: {msg.Body.version}");

        if (matches_count > 0)
            Console.WriteLine($"Requesting up to {matches_count} matches...");
        else
            Console.WriteLine("Requesting matches until last stored match ID is reached...");

        FetchMatches();
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
            if (match.start_time != 0 && match.match_id != 0 && match.rank_change != 0 && match.previous_rank != 0) {
                if (match.match_id == latest_saved_mid) {
                    reachedExistingMatch = true;
                    if (matches_count < 0) {
                        matches_remaining = 0;
                    }
                    break;
                }

                newlyFetchedMatches.Add(match);
            }
            matches_start_at_id = match.match_id;
        }

        if (reachedExistingMatch || matches_remaining <= 0 || msg.Body.matches.Count == 0 || msg.Body.matches.Count < matches_requested) {
            Console.WriteLine("Completed match pagination.");
            syncSucceeded = true;
            isRunning = false;
            steamClient.Disconnect();
        } else {
            Thread.Sleep(1000); // Respect GC 1 req/sec rate limit
            Console.WriteLine($"Fetched page. Next start_at_match_id: {matches_start_at_id}");
            FetchMatches();
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
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(records, options);
        File.WriteAllText(path, json);
    }

    static string FormatTime(uint unixTime) {
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().AddSeconds(unixTime).ToString("yyyy-MM-dd HH:mm:ss");
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

    public class MatchRecord {
        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("unix_time")]
        public uint StartTime { get; set; }

        [JsonPropertyName("match_id")]
        public ulong MatchId { get; set; }

        [JsonPropertyName("solo_queue")]
        public bool SoloQueue { get; set; }

        [JsonPropertyName("hero_id")]
        public int HeroId { get; set; }

        [JsonPropertyName("hero_name")]
        public string HeroName { get; set; } = string.Empty;

        [JsonPropertyName("start_mmr")]
        public uint StartMmr { get; set; }

        [JsonPropertyName("rank_change")]
        public int RankChange { get; set; }
    }
}
