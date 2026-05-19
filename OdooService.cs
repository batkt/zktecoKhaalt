using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace zktecoKhaalt;

public class OdooService
{
    private readonly HttpClient _httpClient;
    private readonly OdooConfig _config;
    private readonly ILogger<OdooService> _logger;
    private readonly string _dbFilePath;
    private static readonly object _fileLock = new object();

    public OdooService(HttpClient httpClient, OdooConfig config, ILogger<OdooService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        
        // Resolve database file to project root in development so clean/rebuilds never delete it
        _dbFilePath = GetDatabaseFilePath();
        _logger.LogInformation("Offline mode active. Local membership database: {Path}", _dbFilePath);
    }

    private string GetDatabaseFilePath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        
        if (baseDir.Contains("bin") && baseDir.Contains("net6.0"))
        {
            DirectoryInfo dir = new DirectoryInfo(baseDir);
            while (dir != null && (dir.Name.Equals("win-x86", StringComparison.OrdinalIgnoreCase) ||
                                   dir.Name.Equals("net6.0", StringComparison.OrdinalIgnoreCase) ||
                                   dir.Name.Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                                   dir.Name.Equals("Release", StringComparison.OrdinalIgnoreCase) ||
                                   dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            {
                dir = dir.Parent;
            }
            if (dir != null)
            {
                return Path.Combine(dir.FullName, "membership_db.json");
            }
        }
        
        return Path.Combine(baseDir, "membership_db.json");
    }

    private Dictionary<string, double> LoadDatabase()
    {
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(_dbFilePath))
                {
                    string json = File.ReadAllText(_dbFilePath);
                    var db = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
                    if (db != null)
                    {
                        return db;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load local membership database.");
            }
            return new Dictionary<string, double>();
        }
    }

    private void SaveDatabase(Dictionary<string, double> db)
    {
        lock (_fileLock)
        {
            try
            {
                string json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dbFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save local membership database.");
            }
        }
    }

    /// <summary>
    /// Gets all local membership records from the JSON database.
    /// </summary>
    public async Task<Dictionary<string, double>> GetAllLocalMembershipsAsync()
    {
        await Task.CompletedTask;
        return LoadDatabase();
    }

    /// <summary>
    /// Gets the remaining entries for a given membership barcode from the local database.
    /// </summary>
    public async Task<int> GetRemainingEntriesAsync(string barcode)
    {
        await Task.CompletedTask;
        
        var db = LoadDatabase();
        if (db.TryGetValue(barcode, out double points))
        {
            int remaining = (int)Math.Floor(points);
            _logger.LogInformation("[LOCAL DB] Barcode {Barcode} has {Count} points remaining.", barcode, remaining);
            return remaining;
        }

        _logger.LogWarning("[LOCAL DB] Barcode {Barcode} not found in database. Treating as 0 points.", barcode);
        return 0; // Trigger immediate revocation
    }

    /// <summary>
    /// Decrements the remaining entries in the local database.
    /// </summary>
    /// <returns>True if successful.</returns>
    public async Task<bool> DecrementEntryAsync(string barcode, int currentPoints)
    {
        await Task.CompletedTask;

        var db = LoadDatabase();
        int newPoints = currentPoints - 1;
        
        db[barcode] = (double)newPoints;
        SaveDatabase(db);
        
        _logger.LogInformation("[LOCAL DB] Decremented points for {Barcode} to {NewPoints}.", barcode, newPoints);
        return true;
    }

    /// <summary>
    /// Saves or updates the point budget for a barcode locally.
    /// </summary>
    public async Task SaveLocalMembershipPointsAsync(string barcode, double points)
    {
        await Task.CompletedTask;

        var db = LoadDatabase();
        db[barcode] = points;
        SaveDatabase(db);

        _logger.LogInformation("[LOCAL DB] Saved barcode {Barcode} with points {Points}.", barcode, points);
    }

    /// <summary>
    /// Satisfies compilation of the existing enrollment signatures.
    /// </summary>
    public async Task<LoyaltyCardData> GetLoyaltyCardInfoAsync(string barcode)
    {
        int points = await GetRemainingEntriesAsync(barcode);
        return new LoyaltyCardData
        {
            CodeQr = barcode,
            PartnerName = "Local Member",
            Points = points,
            Active = true
        };
    }
}

public class LoyaltyApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public LoyaltyCardData Data { get; set; }
}

public class LoyaltyCardData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("codeqr")]
    public string CodeQr { get; set; } = "";

    [JsonPropertyName("points")]
    public double Points { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("partner_name")]
    public string PartnerName { get; set; } = "";
}

public class LoyaltyUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

