using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.S3;
using Amazon.S3.Model;

class Program {
    const string MATCHES_FILE = "data/matches.json";
    const string PROFILE_FILE = "data/profile.json";
    const int BASE_DELAY_MS = 1500; // 1.5s delay (40 req/min, well under 60 req/min GET limit)

    static async Task<int> Main(string[] args) {
        if (!File.Exists(MATCHES_FILE)) {
            Console.Error.WriteLine($"File not found: {MATCHES_FILE}");
            return 1;
        }

        string matchesJson = await File.ReadAllTextAsync(MATCHES_FILE);
        var matches = JsonSerializer.Deserialize<List<MatchEntry>>(matchesJson) ?? new();

        var pendingMatches = matches
            // .Where(m => m.OpenDota == null)
            .OrderByDescending(m => m.Gc.StartTime)
            .ToList();

        Console.WriteLine($"Found {pendingMatches.Count} matches pending OpenDota enrichment and archival.");
        if (pendingMatches.Count == 0) return 0;

        uint accountId = LoadAccountId();
        Console.WriteLine($"Target player Account ID: {(accountId != 0 ? accountId.ToString() : "Not found (using hero fallback)")}");

        var secrets = await LoadSecretsAsync();
        string r2AccountId = secrets?.CloudflareAccountId ?? Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID") ?? string.Empty;
        string r2AccessKey = secrets?.R2AccessKeyId ?? Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID") ?? string.Empty;
        string r2SecretKey = secrets?.R2SecretAccessKey ?? Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY") ?? string.Empty;
        string r2Bucket = secrets?.R2BucketName ?? Environment.GetEnvironmentVariable("R2_BUCKET_NAME") ?? string.Empty;

        IAmazonS3? r2Client = null;
        if (!string.IsNullOrEmpty(r2AccountId) && !string.IsNullOrEmpty(r2AccessKey) && !string.IsNullOrEmpty(r2SecretKey)) {
            r2Client = new AmazonS3Client(r2AccessKey, r2SecretKey, new AmazonS3Config {
                ServiceURL = $"https://{r2AccountId}.r2.cloudflarestorage.com",
                AuthenticationRegion = "auto",
                ForcePathStyle = true
            });
            Console.WriteLine($"Connected to Cloudflare R2 bucket: {r2Bucket}");
        } else {
            Console.WriteLine("Warning: R2 credentials missing. Archival will be skipped.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Dota2Tracker-Backfill/1.0");

        int processed = 0;
        int enrichedCount = 0;

        foreach (var match in pendingMatches) {
            ulong mid = match.Gc.MatchId;
            processed++;

            bool completed = false;
            int retryCount = 0;
            const int maxRetries = 5;
            bool rateLimitExhausted = false;

            while (!completed && retryCount < maxRetries) {
                await Task.Delay(BASE_DELAY_MS);

                try {
                    using var response = await httpClient.GetAsync($"https://api.opendota.com/api/matches/{mid}");

                    if (response.IsSuccessStatusCode) {
                        string rawJson = await response.Content.ReadAsStringAsync();

                        if (r2Client != null && !string.IsNullOrEmpty(r2Bucket)) {
                            try {
                                await UploadToR2Async(r2Client, r2Bucket, mid, match.Gc, rawJson);
                            } catch (Exception ex) {
                                Console.Error.WriteLine($"R2 upload failed for {mid}: {ex.Message}");
                            }
                        }

                        var odData = ParseOpenDotaPayload(rawJson, accountId, match.Gc.HeroId);
                        if (odData != null) {
                            match.OpenDota = odData;
                            enrichedCount++;
                            Console.WriteLine($"[{processed}/{pendingMatches.Count}] Successfully enriched match {mid}");
                        } else {
                            Console.WriteLine($"[{processed}/{pendingMatches.Count}] Match {mid}: Data parsed but missing target stats.");
                        }

                        completed = true;
                        break;
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                        retryCount++;
                        var waitTime = ResolveRetryDelay(response);

                        Console.WriteLine($"[Rate Limit] HTTP 429 on match {mid}. Backing off for {waitTime.TotalSeconds:F0}s (Retry {retryCount}/{maxRetries})...");
                        await Task.Delay(waitTime);

                        if (retryCount >= maxRetries) {
                            rateLimitExhausted = true;
                        }
                        continue;
                    }

                    // Non-429 error (404, 500, etc.)
                    Console.WriteLine($"[{processed}/{pendingMatches.Count}] Match {mid}: HTTP {(int)response.StatusCode}");
                    completed = true;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"Error processing match {mid}: {ex.Message}");
                    completed = true;
                }
            }

            if (rateLimitExhausted) {
                Console.WriteLine("\n========================================================");
                Console.WriteLine($"[DAILY QUOTA EXHAUSTED] OpenDota rate limit persisted across {maxRetries} retries.");
                Console.WriteLine($"Saving progress ({enrichedCount} matches enriched in this run).");
                Console.WriteLine("Remaining matches will be resumed on the next run.");
                Console.WriteLine("========================================================\n");
                SaveMatches(matches);
                return 0; // Graceful exit so GitHub Actions commits saved progress
            }

            // Periodic checkpoint every 25 matches
            if (enrichedCount % 25 == 0 && enrichedCount > 0) {
                SaveMatches(matches);
            }
        }

        SaveMatches(matches);
        Console.WriteLine($"Finished backfill. Total newly enriched matches: {enrichedCount}/{pendingMatches.Count}.");
        return 0;
    }

    static TimeSpan ResolveRetryDelay(HttpResponseMessage response) {
        var delay = TimeSpan.FromSeconds(30);

        if (response.Headers.RetryAfter != null) {
            if (response.Headers.RetryAfter.Delta.HasValue) {
                delay = response.Headers.RetryAfter.Delta.Value;
            } else if (response.Headers.RetryAfter.Date.HasValue) {
                var delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero) {
                    delay = delta;
                }
            }
        }

        return delay + TimeSpan.FromSeconds(2);
    }

    static async Task UploadToR2Async(IAmazonS3 client, string bucket, ulong matchId, GcData gcData, string rawOpenDotaJson) {
        using var odDoc = JsonDocument.Parse(rawOpenDotaJson);
        var combinedPayload = new {
            gc = gcData,
            opendota = odDoc.RootElement
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(combinedPayload);

        using var memoryStream = new MemoryStream();
        using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true)) {
            await gzipStream.WriteAsync(bytes, 0, bytes.Length);
        }
        memoryStream.Position = 0;

        await client.PutObjectAsync(new PutObjectRequest {
            BucketName = bucket,
            Key = $"matches/{matchId}.json.gz",
            InputStream = memoryStream,
            ContentType = "application/json",
            Headers = { ContentEncoding = "gzip" },
            DisablePayloadSigning = true
        });
    }

    static OpenDotaData? ParseOpenDotaPayload(string rawJson, uint targetAccountId, int fallbackHeroId) {
        try {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            int? duration = root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number
                ? durProp.GetInt32()
                : null;

            if (!root.TryGetProperty("players", out var playersProp) || playersProp.ValueKind != JsonValueKind.Array) {
                return null;
            }

            JsonElement? myPlayer = null;
            var playerDrafts = new List<PlayerDraft>();

            foreach (var p in playersProp.EnumerateArray()) {
                uint? accId = null;
                if (p.TryGetProperty("account_id", out var aProp) && aProp.ValueKind == JsonValueKind.Number) {
                    uint val = aProp.GetUInt32();
                    if (val != 0) accId = val;
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

                if ((targetAccountId != 0 && accId == targetAccountId) || (myPlayer == null && hId == fallbackHeroId)) {
                    myPlayer = p;
                }
            }

            if (myPlayer == null) return null;
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

    static int? GetIntSafe(JsonElement elem, string prop) {
        return elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }

    static uint LoadAccountId() {
        if (!File.Exists(PROFILE_FILE)) return 0;
        try {
            string json = File.ReadAllText(PROFILE_FILE);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                var last = doc.RootElement.EnumerateArray().LastOrDefault();
                if (last.TryGetProperty("account_id", out var p)) return p.GetUInt32();
            } else if (doc.RootElement.TryGetProperty("account_id", out var p)) {
                return p.GetUInt32();
            }
        } catch { }
        return 0;
    }

    static async Task<SteamSecrets?> LoadSecretsAsync() {
        try {
            string paramName = Environment.GetEnvironmentVariable("SSM_PARAM_NAME") ?? "/spr_steam/secrets";
            string regionName = Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-west-2";
            using var ssm = new AmazonSimpleSystemsManagementClient(RegionEndpoint.GetBySystemName(regionName));

            var res = await ssm.GetParameterAsync(new GetParameterRequest {
                Name = paramName,
                WithDecryption = true
            });
            if (res.Parameter?.Value != null) {
                return JsonSerializer.Deserialize<SteamSecrets>(res.Parameter.Value, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                });
            }
        } catch (Exception ex) {
            Console.WriteLine($"SSM secrets retrieval skipped: {ex.Message}");
        }
        return null;
    }

    static void SaveMatches(List<MatchEntry> records) {
        var options = new JsonSerializerOptions {
            WriteIndented = true
        };
        string json = JsonSerializer.Serialize(records, options);
        string tempFile = MATCHES_FILE + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, MATCHES_FILE, overwrite: true);
    }

    public class SteamSecrets {
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
    }

    public class MatchEntry {
        [JsonPropertyName("gc")]
        public GcData Gc { get; set; } = new();

        [JsonPropertyName("opendota")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public OpenDotaData? OpenDota { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    public class GcData {
        [JsonPropertyName("match_id")]
        public ulong MatchId { get; set; }

        [JsonPropertyName("unix_time")]
        public uint StartTime { get; set; }

        [JsonPropertyName("hero_id")]
        public int HeroId { get; set; }

        [JsonPropertyName("start_mmr")]
        public int? StartMmr { get; set; }

        [JsonPropertyName("rank_change")]
        public int? RankChange { get; set; }

        [JsonPropertyName("is_calibration")]
        public bool? IsCalibration { get; set; }

        [JsonPropertyName("win")]
        public bool? Win { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    public class OpenDotaData {
        [JsonPropertyName("duration")]
        public int? Duration { get; set; }

        [JsonPropertyName("level")]
        public int? Level { get; set; }

        [JsonPropertyName("kills")]
        public int? Kills { get; set; }

        [JsonPropertyName("deaths")]
        public int? Deaths { get; set; }

        [JsonPropertyName("assists")]
        public int? Assists { get; set; }

        [JsonPropertyName("last_hits")]
        public int? LastHits { get; set; }

        [JsonPropertyName("denies")]
        public int? Denies { get; set; }

        [JsonPropertyName("net_worth")]
        public int? NetWorth { get; set; }

        [JsonPropertyName("gold_per_min")]
        public int? GoldPerMin { get; set; }

        [JsonPropertyName("xp_per_min")]
        public int? XpPerMin { get; set; }

        [JsonPropertyName("players")]
        public List<PlayerDraft> Players { get; set; } = new();
    }

    public class PlayerDraft {
        [JsonPropertyName("account_id")]
        public uint? AccountId { get; set; }

        [JsonPropertyName("hero_id")]
        public int HeroId { get; set; }

        [JsonPropertyName("isRadiant")]
        public bool IsRadiant { get; set; }
    }
}
