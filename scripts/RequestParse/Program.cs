using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

class Program {
    const string MATCHES_FILE = "data/matches.json";
    const int BASE_DELAY_MS = 10500; // 10.5s regeneration delay for 10-token parse cost

    static async Task<int> Main(string[] args) {
        int skip = 0;
        if (args.Length > 0 && int.TryParse(args[0], out int parsedSkip)) {
            skip = Math.Max(0, parsedSkip);
        }

        if (!File.Exists(MATCHES_FILE)) {
            Console.Error.WriteLine($"File not found: {MATCHES_FILE}");
            return 1;
        }

        string json = await File.ReadAllTextAsync(MATCHES_FILE);
        var matches = JsonSerializer.Deserialize<List<MatchEntry>>(json) ?? new();

        var allPending = matches
            .Where(m => m.OpenDota == null)
            .OrderByDescending(m => m.Gc.StartTime)
            .ToList();

        var targetMatches = allPending.Skip(skip).ToList();

        Console.WriteLine($"Total unparsed matches: {allPending.Count}. Skipping first: {skip}. Matches to process: {targetMatches.Count}.");
        if (targetMatches.Count == 0) return 0;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Dota2Tracker-Backfill/1.0");

        int processed = 0;
        int failed = 0;

        foreach (var match in targetMatches) {
            ulong mid = match.Gc.MatchId;
            processed++;
            int globalIndex = skip + processed;

            bool completed = false;
            int retryCount = 0;
            const int maxRetries = 5;
            bool rateLimitExhausted = false;

            while (!completed && retryCount < maxRetries) {
                await Task.Delay(BASE_DELAY_MS);

                try {
                    using var response = await client.PostAsync($"https://api.opendota.com/api/request/{mid}", null);

                    if (response.IsSuccessStatusCode) {
                        Console.WriteLine($"[{globalIndex}/{allPending.Count}] Requested parse for match {mid} (Status: {(int)response.StatusCode})");
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

                    failed++;
                    Console.Error.WriteLine($"[{globalIndex}/{allPending.Count}] Failed parse request for {mid}: HTTP {(int)response.StatusCode}");
                    completed = true;
                } catch (Exception ex) {
                    failed++;
                    Console.Error.WriteLine($"[{globalIndex}/{allPending.Count}] Exception on match {mid}: {ex.Message}");
                    completed = true;
                }
            }

            if (rateLimitExhausted) {
                Console.WriteLine("\n========================================================");
                Console.WriteLine($"[DAILY QUOTA EXHAUSTED] OpenDota rate limit persisted across {maxRetries} retries.");
                Console.WriteLine($"Stopped at match {mid} (index {globalIndex}).");
                Console.WriteLine($"To resume tomorrow, run this workflow with skip = {globalIndex - 1}");
                Console.WriteLine("========================================================\n");
                return 0; // Graceful exit so GitHub Actions completes cleanly
            }

            if (!completed) {
                failed++;
                Console.Error.WriteLine($"[{globalIndex}/{allPending.Count}] Exceeded maximum retries for match {mid}. Skipping.");
            }
        }

        Console.WriteLine($"Batch completed. Success: {processed - failed}, Failed: {failed}.");
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

    public class MatchEntry {
        [JsonPropertyName("gc")]
        public GcData Gc { get; set; } = new();

        [JsonPropertyName("opendota")]
        public object? OpenDota { get; set; }
    }

    public class GcData {
        [JsonPropertyName("match_id")]
        public ulong MatchId { get; set; }

        [JsonPropertyName("unix_time")]
        public uint StartTime { get; set; }
    }
}
