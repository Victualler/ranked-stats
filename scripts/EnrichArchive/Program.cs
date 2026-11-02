using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
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

    static async Task<int> Main(string[] args) {
        if (!File.Exists(MATCHES_FILE)) {
            Console.Error.WriteLine($"File not found: {MATCHES_FILE}");
            return 1;
        }

        string matchesJson = await File.ReadAllTextAsync(MATCHES_FILE);
        var matches = JsonSerializer.Deserialize<List<MatchEntry>>(matchesJson) ?? new();
        Console.WriteLine($"Loaded {matches.Count} matches from {MATCHES_FILE}.");

        var secrets = await LoadSecretsAsync();
        string r2AccountId = secrets?.CloudflareAccountId ?? Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID") ?? string.Empty;
        string r2AccessKey = secrets?.R2AccessKeyId ?? Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID") ?? string.Empty;
        string r2SecretKey = secrets?.R2SecretAccessKey ?? Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY") ?? string.Empty;
        string r2Bucket = secrets?.R2BucketName ?? Environment.GetEnvironmentVariable("R2_BUCKET_NAME") ?? "ranked-stats-archive";

        if (string.IsNullOrEmpty(r2AccountId) || string.IsNullOrEmpty(r2AccessKey) || string.IsNullOrEmpty(r2SecretKey)) {
            Console.Error.WriteLine("Error: Missing R2 credentials in SSM/environment variables.");
            return 1;
        }

        using var r2Client = new AmazonS3Client(r2AccessKey, r2SecretKey, new AmazonS3Config {
            ServiceURL = $"https://{r2AccountId}.r2.cloudflarestorage.com",
            AuthenticationRegion = "auto",
            ForcePathStyle = true
        });

        Console.WriteLine($"Connected to R2 bucket '{r2Bucket}'. Re-extracting scores and items from archives...");

        int processed = 0;
        int updated = 0;

        foreach (var match in matches) {
            processed++;
            ulong mid = match.Gc.GetProperty("match_id").GetUInt64();
            string key = $"matches/{mid}.json.gz";

            try {
                using var getResponse = await r2Client.GetObjectAsync(r2Bucket, key);
                using var gzipStream = new GZipStream(getResponse.ResponseStream, CompressionMode.Decompress);
                using var doc = await JsonDocument.ParseAsync(gzipStream);

                JsonElement odRoot = doc.RootElement.TryGetProperty("opendota", out var odProp)
                    ? odProp
                    : doc.RootElement;

                if (match.OpenDota != null) {
                    // 1. Team scores
                    match.OpenDota.RadiantScore = GetIntSafe(odRoot, "radiant_score");
                    match.OpenDota.DireScore = GetIntSafe(odRoot, "dire_score");

                    // 2. Player items
                    if (odRoot.TryGetProperty("players", out var playersProp) && playersProp.ValueKind == JsonValueKind.Array) {
                        var rawPlayers = playersProp.EnumerateArray().ToList();

                        foreach (var targetPlayer in match.OpenDota.Players) {
                            var raw = rawPlayers.FirstOrDefault(p =>
                                p.TryGetProperty("hero_id", out var h) &&
                                h.ValueKind == JsonValueKind.Number &&
                                h.GetInt32() == targetPlayer.HeroId);

                            if (raw.ValueKind != JsonValueKind.Undefined) {
                                targetPlayer.Items = new int[] {
                                    GetIntSafe(raw, "item_0") ?? 0,
                                    GetIntSafe(raw, "item_1") ?? 0,
                                    GetIntSafe(raw, "item_2") ?? 0,
                                    GetIntSafe(raw, "item_3") ?? 0,
                                    GetIntSafe(raw, "item_4") ?? 0,
                                    GetIntSafe(raw, "item_5") ?? 0
                                };

                                targetPlayer.Backpack = new int[] {
                                    GetIntSafe(raw, "backpack_0") ?? 0,
                                    GetIntSafe(raw, "backpack_1") ?? 0,
                                    GetIntSafe(raw, "backpack_2") ?? 0
                                };

                                int? neutral = GetIntSafe(raw, "item_neutral");
                                targetPlayer.NeutralItem = (neutral != null && neutral != 0) ? neutral : null;

                                targetPlayer.AghanimsScepter = GetIntSafe(raw, "aghanims_scepter") ?? 0;
                                targetPlayer.AghanimsShard = GetIntSafe(raw, "aghanims_shard") ?? 0;
                                targetPlayer.Moonshard = GetIntSafe(raw, "moonshard") ?? 0;
                            }
                        }
                    }

                    updated++;
                }

                if (processed % 50 == 0 || processed == matches.Count) {
                    Console.WriteLine($"[{processed}/{matches.Count}] Processed match archives...");
                    SaveMatches(matches);
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"[{processed}/{matches.Count}] Failed reading {key}: {ex.Message}");
            }
        }

        SaveMatches(matches);
        Console.WriteLine($"\nFinished migration. Successfully enriched {updated}/{matches.Count} matches directly from R2.");
        return 0;
    }

    static int? GetIntSafe(JsonElement elem, string prop) {
        return elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
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

        [JsonPropertyName("r2AccessKeyId")]
        public string R2AccessKeyId { get; set; } = string.Empty;

        [JsonPropertyName("r2SecretAccessKey")]
        public string R2SecretAccessKey { get; set; } = string.Empty;

        [JsonPropertyName("r2BucketName")]
        public string R2BucketName { get; set; } = string.Empty;
    }

    public class MatchEntry {
        [JsonPropertyName("gc")]
        public JsonElement Gc { get; set; }

        [JsonPropertyName("opendota")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public OpenDotaData? OpenDota { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    public class OpenDotaData {
        [JsonPropertyName("duration")]
        public int? Duration { get; set; }

        [JsonPropertyName("radiant_score")]
        public int? RadiantScore { get; set; }

        [JsonPropertyName("dire_score")]
        public int? DireScore { get; set; }

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

        [JsonPropertyName("items")]
        public int[] Items { get; set; } = Array.Empty<int>();

        [JsonPropertyName("backpack")]
        public int[] Backpack { get; set; } = Array.Empty<int>();

        [JsonPropertyName("neutral_item")]
        public int? NeutralItem { get; set; }

        [JsonPropertyName("aghanims_scepter")]
        public int AghanimsScepter { get; set; }

        [JsonPropertyName("aghanims_shard")]
        public int AghanimsShard { get; set; }

        [JsonPropertyName("moonshard")]
        public int Moonshard { get; set; }
    }
}
