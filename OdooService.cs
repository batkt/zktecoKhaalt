using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private static readonly Dictionary<string, DateTime> _lastLocalDecrementTimes = new Dictionary<string, DateTime>();

    public OdooService(HttpClient httpClient, OdooConfig config, ILogger<OdooService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        
        if (!string.IsNullOrWhiteSpace(_config.BaseUrl))
        {
            try
            {
                _httpClient.BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/'));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid Odoo BaseUrl configured: {BaseUrl}", _config.BaseUrl);
            }
        }
        
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
                        // Auto-prune any keys that have 0.0 or less points (except initial -1.0 registration flag)
                        bool modified = false;
                        foreach (var key in db.Keys.ToList())
                        {
                            if (db[key] <= 0.0 && db[key] != -1.0)
                            {
                                db.Remove(key);
                                modified = true;
                            }
                        }
                        if (modified)
                        {
                            SaveDatabase(db);
                            _logger.LogInformation("[LOCAL DB] Auto-pruned depleted 0-point cards from the database file.");
                        }
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
    /// Gets the remaining entries for a given membership barcode from the local database or Odoo.
    /// </summary>
    /// <summary>
    /// Gets the remaining entries for a given membership barcode from the local database or Odoo.
    /// </summary>
    public async Task<int> GetRemainingEntriesAsync(string barcode)
    {
        var localDb = LoadDatabase();
        localDb.TryGetValue(barcode, out double localPoints);

        // 1. ALWAYS query Odoo first as the primary source of truth
        _logger.LogInformation("[ODOO] Checking Odoo first for barcode {Barcode}...", barcode);
        var odooInfo = await GetOdooCardInfoAsync(barcode);
        if (odooInfo != null && odooInfo.Success && odooInfo.Data != null && odooInfo.Data.Active)
        {
            int livePoints = (int)Math.Floor(odooInfo.Data.Points);
            _logger.LogInformation("[ODOO] Barcode {Barcode} found on Odoo. Live points: {Count}.", barcode, livePoints);
            
            // Sync with local DB if different (or not present)
            if (localPoints != livePoints)
            {
                // If local points are lower and we recently decremented them, trust local points to prevent duplicate entry race conditions!
                if (localPoints != -1.0 && localPoints < livePoints && _lastLocalDecrementTimes.TryGetValue(barcode, out DateTime lastDecTime) && (DateTime.Now - lastDecTime).TotalSeconds < 30)
                {
                    _logger.LogWarning("[SYNC] Local points ({Local}) are lower than Odoo points ({Genco}) and decremented recently. Trusting local points to prevent duplicate entry.", localPoints, livePoints);
                    return (int)Math.Floor(localPoints);
                }

                _logger.LogInformation("[SYNC] Syncing local points for {Barcode} ({Local}) to match Genco absolute points ({Genco}).", barcode, localPoints, livePoints);
                var db = LoadDatabase();
                db[barcode] = (double)livePoints;
                SaveDatabase(db);
            }
            return livePoints;
        }

        // 2. Fallback: Check local membership database if not found on Odoo
        if (localDb.TryGetValue(barcode, out double points))
        {
            // If Genco returned successfully but indicated the card is inactive or deleted, revoke access immediately
            if (odooInfo != null && odooInfo.Success && (odooInfo.Data == null || !odooInfo.Data.Active))
            {
                _logger.LogWarning("[LOCAL DB] Barcode {Barcode} is inactive or deleted on Odoo. Revoking local access.", barcode);
                localDb.Remove(barcode);
                SaveDatabase(localDb);
                return 0;
            }

            int remaining = (int)Math.Floor(points);
            _logger.LogInformation("[LOCAL DB] Barcode not found on Odoo. Using local points: {Count}.", remaining);
            return remaining;
        }

        _logger.LogWarning("[LOCAL DB/ODOO] Barcode {Barcode} not found in Odoo or local database. Treating as 0 points.", barcode);
        return 0; // Trigger immediate revocation
    }

    /// <summary>
    /// Decrements the remaining entries in the local database or Odoo.
    /// </summary>
    /// <returns>The new remaining points (0 or greater), or -1 if the operation failed.</returns>
    public async Task<int> DecrementEntryAsync(string barcode, int currentPoints)
    {
        _lastLocalDecrementTimes[barcode] = DateTime.Now;

        // 1. Try live Odoo decrement first (Odoo / Genco is absolute)
        _logger.LogInformation("[ODOO] Attempting live point decrement on Odoo for barcode {Barcode}...", barcode);
        var result = await DeductOdooCardPointAsync(barcode);
        if (result != null && result.Success && result.Data != null)
        {
            int livePoints = (int)Math.Floor(result.Data.Points);
            _logger.LogInformation("[ODOO] Decremented successfully on Odoo. New points: {NewPoints}", livePoints);
            
            var localDb = LoadDatabase();
            if (livePoints <= 0 || !result.Data.Active)
            {
                localDb.Remove(barcode);
                _logger.LogInformation("[ODOO] Odoo card {Barcode} has 0 points or is inactive. Automatically deleted from local database.", barcode);
            }
            else
            {
                localDb[barcode] = (double)livePoints;
            }
            SaveDatabase(localDb);
            return livePoints;
        }

        // 2. Fallback: Decrement locally if Odoo decrement was unsuccessful (e.g. local-only card)
        _logger.LogInformation("[LOCAL] Odoo decrement failed or not found. Falling back to local decrement for {Barcode}...", barcode);
        var db = LoadDatabase();
        if (db.TryGetValue(barcode, out double localPoints))
        {
            int newPoints = currentPoints - 1;
            if (newPoints <= 0)
            {
                db.Remove(barcode);
                _logger.LogInformation("[LOCAL DB] Depleted barcode {Barcode} has reached 0 points and has been automatically deleted from local database.", barcode);
                newPoints = 0;
            }
            else
            {
                db[barcode] = (double)newPoints;
                _logger.LogInformation("[LOCAL DB] Decremented points for {Barcode} to {NewPoints}.", barcode, newPoints);
            }
            SaveDatabase(db);
            return newPoints;
        }

        _logger.LogWarning("[LOCAL DB] Cannot decrement barcode {Barcode} (not found in local DB).", barcode);
        return -1;
    }

    /// <summary>
    /// Saves or updates the point budget for a barcode locally.
    /// </summary>
    public async Task SaveLocalMembershipPointsAsync(string barcode, double points)
    {
        await Task.CompletedTask;

        var db = LoadDatabase();
        if (points == 0.0)
        {
            db.Remove(barcode);
            _logger.LogInformation("[LOCAL DB] Removed barcode {Barcode} from local database because points are 0.", barcode);
        }
        else
        {
            db[barcode] = points;
            _logger.LogInformation("[LOCAL DB] Saved barcode {Barcode} with points {Points}.", barcode, points);
        }
        SaveDatabase(db);
    }

    /// <summary>
    /// Satisfies compilation of the existing enrollment signatures.
    /// </summary>
    public async Task<LoyaltyCardData> GetLoyaltyCardInfoAsync(string barcode)
    {
        var db = LoadDatabase();
        if (db.TryGetValue(barcode, out double points) && points == -1.0)
        {
            var odooInfo = await GetOdooCardInfoAsync(barcode);
            if (odooInfo != null && odooInfo.Success && odooInfo.Data != null)
            {
                return odooInfo.Data;
            }
        }

        int localPoints = await GetRemainingEntriesAsync(barcode);
        return new LoyaltyCardData
        {
            CodeQr = barcode,
            PartnerName = "Local Member",
            Points = localPoints,
            Active = true
        };
    }

    /// <summary>
    /// Fetches loyalty card details from Odoo API.
    /// </summary>
    public async Task<LoyaltyApiResponse> GetOdooCardInfoAsync(string barcode)
    {
        try
        {
            string url = $"/loyalty/api/info?barcode={barcode}";
            _logger.LogInformation("[ODOO API] Fetching card info for {Barcode}", barcode);
            HttpResponseMessage response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LoyaltyApiResponse>(json);
                if (result != null)
                {
                    return result;
                }
            }
            else
            {
                _logger.LogWarning("[ODOO API] Fetch card info failed. Status: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ODOO API] Exception while fetching card info for {Barcode}", barcode);
        }
        return null;
    }

    /// <summary>
    /// Decrements points on Odoo API.
    /// </summary>
    public async Task<LoyaltyApiResponse> DeductOdooCardPointAsync(string barcode)
    {
        try
        {
            string url = "/loyalty/api/decrease_point";
            _logger.LogInformation("[ODOO API] Sending point decrement request for {Barcode}", barcode);
            
            // Send as application/x-www-form-urlencoded
            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("barcode", barcode)
            };
            var content = new FormUrlEncodedContent(values);

            string requestUrl = $"{url}?barcode={barcode}";
            HttpResponseMessage response = await _httpClient.PostAsync(requestUrl, content);

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LoyaltyApiResponse>(json);
                if (result != null)
                {
                    return result;
                }
            }
            else
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("[ODOO API] Deduct card point failed. Status: {Status}, Response: {Response}", response.StatusCode, errorBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ODOO API] Exception while deducting card point for {Barcode}", barcode);
        }
        return null;
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

