using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

class Program {
    const string MATCHES_FILE = "data/matches.json";

    static async Task<int> Main(string[] args) {
        if (!File.Exists(MATCHES_FILE)) {
            Console.Error.WriteLine($"File not found: {MATCHES_FILE}");
            return 1;
        }

        string json = await File.ReadAllTextAsync(MATCHES_FILE);
        var matches = JsonSerializer.Deserialize<List<MatchEntry>>(json) ?? new();

        var pendingMatches = matches
            .Where(m => m.OpenDota == null)
            .OrderByDescending(m => m.Gc.StartTime)
            .ToList();

        Console.WriteLine($"Found {pendingMatches.Count} matches pending OpenDota parse.");
        if (pendingMatches.Count == 0) return 0;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Dota2Tracker-Backfill/1.0");

        int processed = 0;
        int failed = 0;

        foreach (var match in pendingMatches) {
            ulong mid = match.Gc.MatchId;
            processed++;

            await Task.Delay(1200);

            try {
                var response = await client.PostAsync($"https://api.opendota.com/api/request/{mid}", null);
                if (response.IsSuccessStatusCode) {
                    Console.WriteLine($"[{processed}/{pendingMatches.Count}] Requested parse for match {mid} (Status: {(int)response.StatusCode})");
                } else {
                    failed++;
                    Console.Error.WriteLine($"[{processed}/{pendingMatches.Count}] Failed parse request for {mid}: HTTP {(int)response.StatusCode}");
                }
            } catch (Exception ex) {
                failed++;
                Console.Error.WriteLine($"[{processed}/{pendingMatches.Count}] Exception on match {mid}: {ex.Message}");
            }
        }

        Console.WriteLine($"Completed parse requests. Success: {processed - failed}, Failed: {failed}.");
        return 0;
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