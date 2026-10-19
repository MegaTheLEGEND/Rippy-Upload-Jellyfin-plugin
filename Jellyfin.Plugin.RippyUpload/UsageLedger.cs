using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.RippyUpload;

/// <summary>
/// Tracks which uploaded files belong to which user, so per-user quotas can be enforced on what
/// currently sits in the upload (review) directory. Files moved or deleted by an administrator
/// stop counting automatically.
/// </summary>
public sealed class UsageLedger
{
    private readonly object _sync = new object();
    private readonly string _filePath;
    private readonly Dictionary<string, LedgerRecord> _entries;
    private readonly Dictionary<Guid, long> _reserved = new Dictionary<Guid, long>();

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageLedger"/> class.
    /// </summary>
    /// <param name="filePath">Where the ledger is persisted.</param>
    public UsageLedger(string filePath)
    {
        _filePath = filePath;
        _entries = new Dictionary<string, LedgerRecord>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Load();
    }

    /// <summary>
    /// Gets the bytes the user currently has in the upload directory.
    /// </summary>
    /// <param name="user">The Jellyfin user ID.</param>
    /// <param name="destination">The upload directory.</param>
    /// <returns>Bytes in use.</returns>
    public long GetUsage(Guid user, string destination)
    {
        lock (_sync)
        {
            return SumAndPrune(destination, user).GetValueOrDefault(user);
        }
    }

    /// <summary>
    /// Gets the bytes currently in the upload directory for every user.
    /// </summary>
    /// <param name="destination">The upload directory.</param>
    /// <returns>Bytes in use keyed by user ID.</returns>
    public Dictionary<Guid, long> GetAllUsage(string destination)
    {
        lock (_sync)
        {
            return SumAndPrune(destination, null);
        }
    }

    /// <summary>
    /// Atomically checks the quota and reserves space for an upload that is about to start.
    /// </summary>
    /// <param name="user">The Jellyfin user ID.</param>
    /// <param name="destination">The upload directory.</param>
    /// <param name="quotaBytes">The quota in bytes; 0 or less means unlimited.</param>
    /// <param name="contentLength">The declared upload size, if known.</param>
    /// <param name="allowance">The most bytes this upload may write.</param>
    /// <param name="reserved">The reservation to pass to <see cref="Release"/>.</param>
    /// <param name="used">Bytes the user already has in the upload directory.</param>
    /// <returns>True when the upload may proceed.</returns>
    public bool TryReserve(Guid user, string destination, long quotaBytes, long? contentLength, out long allowance, out long reserved, out long used)
    {
        lock (_sync)
        {
            used = SumAndPrune(destination, user).GetValueOrDefault(user);
            reserved = 0;
            if (quotaBytes <= 0)
            {
                allowance = long.MaxValue;
                return true;
            }

            _reserved.TryGetValue(user, out var inFlight);
            var available = quotaBytes - used - inFlight;
            allowance = Math.Max(0, available);
            if (available <= 0 || (contentLength.HasValue && contentLength.Value > available))
            {
                return false;
            }

            reserved = contentLength ?? available;
            _reserved[user] = inFlight + reserved;
            return true;
        }
    }

    /// <summary>
    /// Releases a reservation made by <see cref="TryReserve"/>.
    /// </summary>
    /// <param name="user">The Jellyfin user ID.</param>
    /// <param name="reserved">The reserved bytes.</param>
    public void Release(Guid user, long reserved)
    {
        lock (_sync)
        {
            if (!_reserved.TryGetValue(user, out var inFlight))
            {
                return;
            }

            inFlight -= reserved;
            if (inFlight <= 0)
            {
                _reserved.Remove(user);
            }
            else
            {
                _reserved[user] = inFlight;
            }
        }
    }

    /// <summary>
    /// Records a completed upload.
    /// </summary>
    /// <param name="user">The Jellyfin user ID.</param>
    /// <param name="relativePath">Path relative to the upload directory, using "/".</param>
    /// <param name="size">The file size in bytes.</param>
    /// <returns>False if the ledger could not be saved to disk (it is still tracked in memory).</returns>
    public bool Record(Guid user, string relativePath, long size)
    {
        lock (_sync)
        {
            _entries[relativePath] = new LedgerRecord { Path = relativePath, User = user, Size = size, UploadedUtc = DateTime.UtcNow };
            return Save();
        }
    }

    // Caller holds the lock.
    private Dictionary<Guid, long> SumAndPrune(string destination, Guid? onlyUser)
    {
        var totals = new Dictionary<Guid, long>();

        // If the destination is unavailable (for example an unmounted drive), count everything and prune nothing.
        var canPrune = Directory.Exists(destination);
        List<string>? stale = null;

        foreach (var pair in _entries)
        {
            if (onlyUser.HasValue && pair.Value.User != onlyUser.Value)
            {
                continue;
            }

            if (canPrune)
            {
                var full = Path.Combine(destination, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                long? length = null;
                try
                {
                    var info = new FileInfo(full);
                    if (info.Exists)
                    {
                        length = info.Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    length = pair.Value.Size;
                }

                if (length is null || length.Value != pair.Value.Size)
                {
                    (stale ??= new List<string>()).Add(pair.Key);
                    continue;
                }
            }

            totals[pair.Value.User] = totals.GetValueOrDefault(pair.Value.User) + pair.Value.Size;
        }

        if (stale is not null)
        {
            foreach (var key in stale)
            {
                _entries.Remove(key);
            }

            Save();
        }

        return totals;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<List<LedgerRecord>>(File.ReadAllText(_filePath));
            if (records is null)
            {
                return;
            }

            foreach (var record in records.Where(r => !string.IsNullOrEmpty(r.Path)))
            {
                _entries[record.Path] = record;
            }
        }
        catch (JsonException)
        {
            try
            {
                File.Move(_filePath, _filePath + ".corrupt", true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done; start with an empty ledger.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Start with an empty ledger; it is rewritten on the next upload.
        }
    }

    // Caller holds the lock.
    private bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Values.ToList()));
            File.Move(temp, _filePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class LedgerRecord
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public Guid User { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("uploadedUtc")]
        public DateTime UploadedUtc { get; set; }
    }
}
