using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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
using Amazon.S3;
using Amazon.S3.Model;

class Program {
    const int APPID = 570; // Dota 2 AppID
    const string OUTPUT_FILE = "data/matches.json";
    const string PROFILE_FILE = "data/profile.json";
    const string HEROES_FILE = "constants/heroes.json";

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
    static uint myAccountId = 0;

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

    static List<MatchEntry> existingMatches = new();
    static Dictionary<ulong, MatchEntry> newMatchRecords = new();
    static Dictionary<int, string> heroIdToName = new();

    static async Task<int> Main(string[] args) {
        Console.WriteLine("Starting Headless Dota 2 MMR Tracker & Minimal Stats Sync...");

        Directory.CreateDirectory("constants");
        Directory.CreateDirectory("data");

        // 1. Initialize local hero mapping with on-demand fallback
        EnsureHeroMapLoaded();

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
        latest_saved_mid = existingMatches.Count > 0 ? existingMatches[0].Gc.MatchId : 0;
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

        // 6. OpenDota Enrichment & Cloudflare R2 Archival Pipeline
        bool dataModified = newMatchRecords.Count > 0;

        // Bounded retry: Target all newly fetched matches plus the top 24 existing matches missing OpenDota data
        var pendingExisting = existingMatches
            .Take(24)
            .Where(m => m.OpenDota == null)
            .ToList();

        var matchesToEnrich = newMatchRecords.Values
            .Concat(pendingExisting)
            .GroupBy(m => m.Gc.MatchId)
            .Select(g => g.First())
            .ToList();

        if (matchesToEnrich.Count > 0) {
            Console.WriteLine($"Found {matchesToEnrich.Count} matches queued for OpenDota enrichment and R2 archival...");

            IAmazonS3? r2Client = null;
            string r2Bucket = loadedSsmSecrets?.R2BucketName ?? Environment.GetEnvironmentVariable("R2_BUCKET_NAME") ?? string.Empty;
            string r2AccountId = loadedSsmSecrets?.CloudflareAccountId ?? Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID") ?? string.Empty;
            string r2AccessKey = loadedSsmSecrets?.R2AccessKeyId ?? Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID") ?? string.Empty;
            string r2SecretKey = loadedSsmSecrets?.R2SecretAccessKey ?? Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY") ?? string.Empty;

            if (!string.IsNullOrEmpty(r2AccountId) && !string.IsNullOrEmpty(r2AccessKey) && !string.IsNullOrEmpty(r2SecretKey)) {
                var s3Config = new AmazonS3Config {
                    ServiceURL = $"https://{r2AccountId}.r2.cloudflarestorage.com",
                    AuthenticationRegion = "auto",
                    ForcePathStyle = true
                };
                r2Client = new AmazonS3Client(r2AccessKey, r2SecretKey, s3Config);
                Console.WriteLine($"Initialized Cloudflare R2 S3 Client (Bucket: {r2Bucket}).");
            } else {
                Console.WriteLine("Notice: Cloudflare R2 credentials not fully specified. Skipping cloud archival.");
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Dota2Tracker/1.0");

            foreach (var match in matchesToEnrich) {
                ulong mid = match.Gc.MatchId;
                Console.WriteLine($"Enriching match {mid} via OpenDota...");

                try {
                    await Task.Delay(1200);

                    var response = await httpClient.GetAsync($"https://api.opendota.com/api/matches/{mid}");

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                        Console.WriteLine("[OpenDota Warning] Rate limited (HTTP 429). Deferring remaining enrichment to the next run.");
                        break;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
                        Console.WriteLine($"[OpenDota Notice] Match {mid} not yet indexed by OpenDota (404). Will retry on subsequent runs.");
                        continue;
                    }

                    if (!response.IsSuccessStatusCode) {
                        Console.Error.WriteLine($"[OpenDota Warning] Match {mid}: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                        continue;
                    }

                    string rawJson = await response.Content.ReadAsStringAsync();

                    // Compress in-memory and upload raw payload to R2
                    if (r2Client != null && !string.IsNullOrEmpty(r2Bucket)) {
                        try {
                            await UploadRawMatchToR2Async(r2Client, r2Bucket, mid, rawJson);
                            Console.WriteLine($"Archived raw match {mid}.json.gz to R2.");
                        } catch (Exception ex) {
                            Console.Error.WriteLine($"[R2 Archival Error] Failed to upload match {mid}: {ex.Message}");
                        }
                    }

                    // Extract lean performance stats & 10-player draft list
                    var odData = ParseOpenDotaPayload(rawJson, myAccountId, match.Gc.HeroId);
                    if (odData != null) {
                        match.OpenDota = odData;
                        dataModified = true;
                        Console.WriteLine($"Successfully populated OpenDota metrics for match {mid}.");
                    } else {
                        Console.Error.WriteLine($"[OpenDota Warning] Match {mid}: Could not parse valid player stats.");
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[OpenDota Error] Failed to process match {mid}: {ex.Message}");
                }
            }
        }

        // 7. Merge, sort, and save matches
        if (dataModified) {
            Console.WriteLine("Consolidating match records...");
            var combinedMap = new Dictionary<ulong, MatchEntry>();

            foreach (var match in existingMatches) {
                combinedMap[match.Gc.MatchId] = match;
            }
            foreach (var match in newMatchRecords.Values) {
                combinedMap[match.Gc.MatchId] = match;
            }

            var sortedList = combinedMap.Values.OrderByDescending(m => m.Gc.StartTime).ToList();

            // Monotonic count check: prevent silent data truncation or accidental empty writes
            if (sortedList.Count < existingMatches.Count) {
                Console.Error.WriteLine($"Integrity check failed: Attempted to save {sortedList.Count} matches, but {existingMatches.Count} previously existed. Aborting write.");
                return 1;
            }

            SaveMatchesToJson(sortedList, OUTPUT_FILE);
            Console.WriteLine($"Saved {sortedList.Count} matches to {OUTPUT_FILE}");
        } else {
            Console.WriteLine("No new matches or pending OpenDota records to update. JSON file remains up to date.");
        }

        return 0;
    }

    // -----------------------------------------------------------------------------------------
    // OpenDota & R2 Archival Helpers
    // -----------------------------------------------------------------------------------------

    static async Task UploadRawMatchToR2Async(IAmazonS3 s3Client, string bucketName, ulong matchId, string rawJson) {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(rawJson);

        using var memoryStream = new MemoryStream();
        using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true)) {
            await gzipStream.WriteAsync(jsonBytes, 0, jsonBytes.Length);
        }

        memoryStream.Position = 0;

        var putRequest = new PutObjectRequest {
            BucketName = bucketName,
            Key = $"matches/{matchId}.json.gz",
            InputStream = memoryStream,
            ContentType = "application/json",
            Headers = {
                ContentEncoding = "gzip"
            }
        };

        await s3Client.PutObjectAsync(putRequest);
    }

    static OpenDotaData? ParseOpenDotaPayload(string rawJson, uint targetAccountId, int fallbackHeroId) {
        try {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            int duration = root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number
                ? durProp.GetInt32()
                : 0;

            if (!root.TryGetProperty("players", out var playersProp) || playersProp.ValueKind != JsonValueKind.Array) {
                return null;
            }

            JsonElement? myPlayer = null;
            var playerDrafts = new List<PlayerDraft>();

            foreach (var p in playersProp.EnumerateArray()) {
                uint accId = 0;
                if (p.TryGetProperty("account_id", out var aProp) && aProp.ValueKind == JsonValueKind.Number) {
                    accId = aProp.GetUInt32();
                }

                int hId = p.TryGetProperty("hero_id", out var hProp) && hProp.ValueKind == JsonValueKind.Number
                    ? hProp.GetInt32()
                    : 0;

                bool isRadiant = false;
                if (p.TryGetProperty("isRadiant", out var rProp) && (rProp.ValueKind == JsonValueKind.True || rProp.ValueKind == JsonValueKind.False)) {
                    isRadiant = rProp.GetBoolean();
                } else if (p.TryGetProperty("player_slot", out var slotProp) && slotProp.ValueKind == JsonValueKind.Number) {
                    isRadiant = slotProp.GetInt32() < 128;
                }

                playerDrafts.Add(new PlayerDraft {
                    AccountId = accId,
                    HeroId = hId,
                    IsRadiant = isRadiant
                });

                // Match player via account ID, fallback to hero ID if anonymous
                if ((targetAccountId != 0 && accId == targetAccountId) || (myPlayer == null && hId == fallbackHeroId)) {
                    myPlayer = p;
                }
            }

            if (myPlayer == null) {
                return null;
            }

            var target = myPlayer.Value;

            return new OpenDotaData {
                Duration = duration,
                Level = GetIntSafe(target, "level"),
                Kills = GetIntSafe(target, "kills"),
                Deaths = GetIntSafe(target, "deaths"),
                Assists = GetIntSafe(target, "assists"),
                LastHits = GetIntSafe(target, "last_hits"),
                Denies = GetIntSafe(target, "denies"),
                NetWorth = GetIntSafe(target, "net_worth"),
                GoldPerMin = GetIntSafe(target, "gold_per_min"),
                XpPerMin = GetIntSafe(target, "xp_per_min"),
                Players = playerDrafts
            };
        } catch {
            return null;
        }
    }

    static int GetIntSafe(JsonElement elem, string prop) {
        return elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }

    // -----------------------------------------------------------------------------------------
    // Offline-First Hero Mapping
    // -----------------------------------------------------------------------------------------

    static void EnsureHeroMapLoaded() {
        if (File.Exists(HEROES_FILE)) {
            try {
                string json = File.ReadAllText(HEROES_FILE);
                ParseHeroMapJson(json);
                Console.WriteLine($"Loaded {heroIdToName.Count} heroes from local {HEROES_FILE}.");
            } catch (Exception ex) {
                Console.WriteLine($"Warning: Failed to parse local {HEROES_FILE}: {ex.Message}");
            }
        }

        if (heroIdToName.Count == 0) {
            Console.WriteLine("No local hero definitions found. Fetching initial registry from remote...");
            UpdateHeroMapFromRemote();
        }
    }

    static void UpdateHeroMapFromRemote() {
        try {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Dota2Tracker/1.0");

            string json = client.GetStringAsync("https://raw.githubusercontent.com/odota/dotaconstants/master/build/heroes.json").GetAwaiter().GetResult();
            ParseHeroMapJson(json);
            File.WriteAllText(HEROES_FILE, json);
            Console.WriteLine($"Successfully updated {HEROES_FILE} with {heroIdToName.Count} heroes.");
        } catch (Exception ex) {
            Console.WriteLine($"Warning: Failed to update heroes from remote: {ex.Message}");
        }
    }

    static void ParseHeroMapJson(string json) {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject()) {
            if (int.TryParse(prop.Name, out int heroId) && prop.Value.TryGetProperty("localized_name", out var locName)) {
                var name = locName.GetString();
                if (!string.IsNullOrEmpty(name)) {
                    heroIdToName[heroId] = name;
                }
            }
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
                loadedSsmSecrets = JsonSerializer.Deserialize<SteamSecrets>(response.Parameter.Value, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                });

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

        myAccountId = steamUser.SteamID?.AccountID ?? 0;
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

            var clientHello = new ClientGCMsgProtobuf<SteamKit2.GC.Dota.Internal.CMsgClientHello>(
                (uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            clientHello.Body.engine = ESourceEngine.k_ESE_Source2;
            coordinator.Send(clientHello, APPID);
        });
    }

    static void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
        Console.WriteLine($"Logged off of Steam: {callback.Result}");

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
            try {
                func(callback.Message);
            } catch (Exception ex) {
                Console.Error.WriteLine($"[GC Handler Error] Exception handling message {callback.EMsg}: {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // GC Request / Response Handlers
    // -----------------------------------------------------------------------------------------

    static void OnClientWelcome(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);
        Console.WriteLine($"GC connected. Version: {msg.Body.version}");

        myAccountId = steamUser.SteamID?.AccountID ?? 0;

        // 1. Request account profile card
        Console.WriteLine("Requesting account profile card...");
        var profileReq = new ClientGCMsgProtobuf<CMsgClientToGCGetProfileCard>(
            (uint)EDOTAGCMsg.k_EMsgClientToGCGetProfileCard);
        profileReq.Body.account_id = myAccountId;
        coordinator.Send(profileReq, APPID);

        // 2. Request match history pagination
        if (matches_count > 0)
            Console.WriteLine($"Requesting up to {matches_count} matches...");
        else
            Console.WriteLine("Requesting matches until last stored match ID is reached...");

        FetchMatches();
    }

    static void OnProfileCard(IPacketGCMsg packetMsg) {
        var msg = new ClientGCMsgProtobuf<CMsgDOTAProfileCard>(packetMsg);
        if (msg.Body == null) {
            Console.Error.WriteLine("[GC Warning] Received null Profile Card body.");
            profileFetched = true;
            CheckSyncCompletion();
            return;
        }

        Console.WriteLine($"Received Profile Card. Rank Tier: {msg.Body.rank_tier}, Leaderboard: {msg.Body.leaderboard_rank}");

        ProcessProfileBadgeTimeline(msg.Body);

        profileFetched = true;
        CheckSyncCompletion();
    }

    static void ProcessProfileBadgeTimeline(CMsgDOTAProfileCard card) {
        var timeline = LoadExistingProfileTimeline(PROFILE_FILE);
        var latestMilestone = timeline.LastOrDefault();

        bool hasBadgeChanged = latestMilestone == null ||
                               latestMilestone.RankTier != card.rank_tier ||
                               latestMilestone.LeaderboardRank != card.leaderboard_rank;

        if (hasBadgeChanged) {
            Console.WriteLine($"Badge change detected! Old: {latestMilestone?.RankTier ?? 0} ({latestMilestone?.RankName ?? "None"}), New: {card.rank_tier} ({GetRankTierName(card.rank_tier)})");

            var milestone = new ProfileBadgeMilestone {
                Date = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                UnixTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                AccountId = card.account_id,
                RankTier = card.rank_tier,
                RankName = GetRankTierName(card.rank_tier),
                LeaderboardRank = card.leaderboard_rank,
                BadgePoints = card.badge_points,
                LifetimeGames = card.lifetime_games,
                IsPlusSubscriber = card.is_plus_subscriber,
                RankTierScore = card.rank_tier_score,
                LeaderboardRankCore = card.leaderboard_rank_core
            };

            timeline.Add(milestone);
            SaveProfileTimelineToJson(timeline, PROFILE_FILE);
            Console.WriteLine($"Appended new badge milestone to {PROFILE_FILE}. Total milestones recorded: {timeline.Count}");
        } else {
            Console.WriteLine($"Rank tier ({card.rank_tier}) has not changed since last recorded milestone. Timeline up to date.");
        }
    }

    static void FetchMatches() {
        matches_requested = 20;
        matches_remaining -= matches_requested;

        var requestHistory = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistory>(
            (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistory);
        requestHistory.Body.account_id = myAccountId != 0 ? myAccountId : (steamUser.SteamID?.AccountID ?? 0);
        requestHistory.Body.matches_requested = (uint)matches_requested;

        if (matches_start_at_id > 0) {
            requestHistory.Body.start_at_match_id = matches_start_at_id;
        }

        coordinator.Send(requestHistory, APPID);
    }

    static void OnMatchHistory(IPacketGCMsg packetMsg) {
        try {
            var msg = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistoryResponse>(packetMsg);

            if (msg.Body.matches == null) {
                Console.Error.WriteLine("[GC Warning] Received null matches list from match history response.");
                historyFetched = true;
                detailsFetched = true;
                CheckSyncCompletion();
                return;
            }

            bool reachedExistingMatch = false;

            foreach (var match in msg.Body.matches) {
                if (match.match_id == latest_saved_mid) {
                    reachedExistingMatch = true;
                    if (matches_count < 0) {
                        matches_remaining = 0;
                    }
                    break;
                }

                bool isRanked = match.lobby_type == 7 || match.previous_rank != 0 || match.rank_change != 0;

                if (!isRanked) {
                    Console.WriteLine($"[GC Debug] Match {match.match_id} skipped: Not recognized as ranked (lobby_type: {match.lobby_type}, previous_rank: {match.previous_rank}, rank_change: {match.rank_change}).");
                    matches_start_at_id = match.match_id;
                    continue;
                }

                if (match.start_time == 0 || match.match_id == 0) {
                    Console.Error.WriteLine($"[GC Warning] Malformed match record detected (match_id: {match.match_id}, start_time: {match.start_time}). Skipping.");
                    matches_start_at_id = match.match_id;
                    continue;
                }

                bool isCalib = match.previous_rank == 0 && match.rank_change == 0;

                var entry = new MatchEntry {
                    Gc = new GcData {
                        MatchId = match.match_id,
                        StartTime = match.start_time,
                        HeroId = match.hero_id,
                        StartMmr = match.previous_rank,
                        RankChange = match.rank_change,
                        IsCalibration = isCalib
                    },
                    OpenDota = null
                };

                if (!newMatchRecords.ContainsKey(match.match_id)) {
                    newMatchRecords[match.match_id] = entry;
                }

                matches_start_at_id = match.match_id;
            }

            if (reachedExistingMatch || matches_remaining <= 0 || msg.Body.matches.Count == 0 || msg.Body.matches.Count < matches_requested) {
                Console.WriteLine($"Match history pagination complete. Found {newMatchRecords.Count} new ranked matches.");
                historyFetched = true;

                foreach (var mid in newMatchRecords.Keys) {
                    matchDetailsQueue.Enqueue(mid);
                }

                if (matchDetailsQueue.Count > 0) {
                    Console.WriteLine($"Enqueuing {matchDetailsQueue.Count} matches to resolve outcome and leaver status...");
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
        } catch (Exception ex) {
            Console.Error.WriteLine($"[GC Error] Failed to process match history response: {ex.Message}");
            isRunning = false;
            steamClient.Disconnect();
        }
    }

    static void FetchNextMatchDetail() {
        if (matchDetailsQueue.Count == 0) {
            Console.WriteLine("Finished fetching match outcomes.");
            detailsFetched = true;
            CheckSyncCompletion();
            return;
        }

        ulong matchId = matchDetailsQueue.Dequeue();
        Console.WriteLine($"Resolving details for match {matchId} ({matchDetailsQueue.Count} remaining)...");

        var req = new ClientGCMsgProtobuf<CMsgGCMatchDetailsRequest>(
            (uint)EDOTAGCMsg.k_EMsgGCMatchDetailsRequest);
        req.Body.match_id = matchId;
        coordinator.Send(req, APPID);
    }

    static void OnMatchDetails(IPacketGCMsg packetMsg) {
        try {
            var msg = new ClientGCMsgProtobuf<CMsgGCMatchDetailsResponse>(packetMsg);

            if (msg.Body.match == null) {
                Console.Error.WriteLine("[GC Warning] Received null match details payload from GC.");
                return;
            }

            var m = msg.Body.match;
            if (!newMatchRecords.TryGetValue(m.match_id, out var entry)) {
                Console.WriteLine($"[GC Notice] Received details for untracked match {m.match_id}.");
                return;
            }

            uint accId = myAccountId != 0 ? myAccountId : (steamUser.SteamID?.AccountID ?? 0);
            var player = m.players.FirstOrDefault(p => p.account_id == accId);

            if (player == null) {
                Console.Error.WriteLine($"[GC Warning] Match {m.match_id}: Account ID {accId} not found in player list! Total players: {m.players.Count}. Win outcome could not be resolved.");
                return;
            }

            int outcomeVal = (int)m.match_outcome;
            string outcomeStr = m.match_outcome.ToString();
            bool isRadiantWin = outcomeVal == 2 || outcomeStr.Contains("RadVictory");

            if (outcomeVal == 0 && !outcomeStr.Contains("Victory")) {
                Console.Error.WriteLine($"[GC Warning] Match {m.match_id}: Unknown or unpopulated match_outcome value: '{m.match_outcome}' ({outcomeVal}).");
            }

            bool isRadiant = player.player_slot < 128;
            bool teamWon = isRadiant ? isRadiantWin : !isRadiantWin;
            bool didAbandon = player.leaver_status > 1;

            entry.Gc.Win = teamWon && !didAbandon;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[GC Error] Failed to process match details: {ex.Message}");
        } finally {
            Thread.Sleep(1000);
            FetchNextMatchDetail();
        }
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

    static List<MatchEntry> LoadExistingMatches(string path) {
        if (!File.Exists(path)) return new List<MatchEntry>();

        try {
            string json = File.ReadAllText(path);
            var records = JsonSerializer.Deserialize<List<MatchEntry>>(json);
            return records ?? new List<MatchEntry>();
        } catch (Exception ex) {
            Console.WriteLine($"Notice: Unable to parse existing {path}: {ex.Message}. Starting fresh.");
            return new List<MatchEntry>();
        }
    }

    static void SaveMatchesToJson(List<MatchEntry> records, string path) {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        string json = JsonSerializer.Serialize(records, options);
        string tempFile = path + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, path, overwrite: true);
    }

    static List<ProfileBadgeMilestone> LoadExistingProfileTimeline(string path) {
        if (!File.Exists(path)) return new List<ProfileBadgeMilestone>();

        try {
            string json = File.ReadAllText(path);
            if (json.TrimStart().StartsWith("{")) {
                var single = JsonSerializer.Deserialize<ProfileBadgeMilestone>(json);
                return single != null ? new List<ProfileBadgeMilestone> { single } : new List<ProfileBadgeMilestone>();
            }
            var list = JsonSerializer.Deserialize<List<ProfileBadgeMilestone>>(json);
            return list ?? new List<ProfileBadgeMilestone>();
        } catch (Exception ex) {
            Console.WriteLine($"Notice: Unable to parse existing {path}: {ex.Message}. Starting fresh timeline.");
            return new List<ProfileBadgeMilestone>();
        }
    }

    static void SaveProfileTimelineToJson(List<ProfileBadgeMilestone> timeline, string path) {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        string json = JsonSerializer.Serialize(timeline, options);
        string tempFile = path + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, path, overwrite: true);
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

        [JsonPropertyName("cloudflareAccountId")]
        public string CloudflareAccountId { get; set; } = string.Empty;

        [JsonPropertyName("r2Token")]
        public string R2Token { get; set; } = string.Empty;

        [JsonPropertyName("r2AccessKeyId")]
        public string R2AccessKeyId { get; set; } = string.Empty;

        [JsonPropertyName("r2SecretAccessKey")]
        public string R2SecretAccessKey { get; set; } = string.Empty;

        [JsonPropertyName("r2BucketName")]
        public string R2BucketName { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    }

    public class ProfileBadgeMilestone {
        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("unix_time")]
        public uint UnixTime { get; set; }

        [JsonPropertyName("account_id")]
        public uint AccountId { get; set; }

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

        [JsonPropertyName("is_plus_subscriber")]
        public bool IsPlusSubscriber { get; set; }

        [JsonPropertyName("rank_tier_score")]
        public uint RankTierScore { get; set; }

        [JsonPropertyName("leaderboard_rank_core")]
        public uint LeaderboardRankCore { get; set; }
    }

    public class MatchEntry {
        [JsonPropertyName("gc")]
        public GcData Gc { get; set; } = new();

        [JsonPropertyName("opendota")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public OpenDotaData? OpenDota { get; set; } = null;
    }

    public class GcData {
        [JsonPropertyName("match_id")]
        public ulong MatchId { get; set; }

        [JsonPropertyName("unix_time")]
        public uint StartTime { get; set; }

        [JsonPropertyName("hero_id")]
        public int HeroId { get; set; }

        [JsonPropertyName("start_mmr")]
        public uint StartMmr { get; set; }

        [JsonPropertyName("rank_change")]
        public int RankChange { get; set; }

        [JsonPropertyName("is_calibration")]
        public bool IsCalibration { get; set; }

        [JsonPropertyName("win")]
        public bool Win { get; set; }
    }

    public class OpenDotaData {
        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("kills")]
        public int Kills { get; set; }

        [JsonPropertyName("deaths")]
        public int Deaths { get; set; }

        [JsonPropertyName("assists")]
        public int Assists { get; set; }

        [JsonPropertyName("last_hits")]
        public int LastHits { get; set; }

        [JsonPropertyName("denies")]
        public int Denies { get; set; }

        [JsonPropertyName("net_worth")]
        public int NetWorth { get; set; }

        [JsonPropertyName("gold_per_min")]
        public int GoldPerMin { get; set; }

        [JsonPropertyName("xp_per_min")]
        public int XpPerMin { get; set; }

        [JsonPropertyName("players")]
        public List<PlayerDraft> Players { get; set; } = new();
    }

    public class PlayerDraft {
        [JsonPropertyName("account_id")]
        public uint AccountId { get; set; }

        [JsonPropertyName("hero_id")]
        public int HeroId { get; set; }

        [JsonPropertyName("isRadiant")]
        public bool IsRadiant { get; set; }
    }
}
