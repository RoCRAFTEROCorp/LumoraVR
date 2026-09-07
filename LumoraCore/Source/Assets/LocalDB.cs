// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lumora.Core.Logging;
using Lumora.Core.Persistence;
using Lumora.Core.Phos;

namespace Lumora.Core.Assets;

public class LocalDB : IDisposable
{
    public enum ImportLocation
    {
        Original,
        Copy,
        Move
    }

    private readonly string _basePath;
    private readonly string _machineId;
    private readonly Dictionary<string, LocalAssetRecord> _assetRecords = new();
    private readonly object _lock = new();
    private bool _initialized;

    // Encrypt asset cache bytes at rest (AES-GCM via LocalEncryption), the same store
    // that already protects records.json. Reads always go through ReadAssetBytesAsync,
    // which transparently decrypts and passes plaintext (legacy / externally-written) files through
    // unchanged - so flipping this on is a forward migration with no rewrite of existing cache files.
    //
    // OFF by default: turning it on is only safe once every cache-byte READER goes through
    // ReadAssetBytesAsync (the engine's local asset gather path) instead of reading the
    // resolved FilePath directly, AND the peer asset transfer DECRYPTS
    // before sending (the master key is machine-bound, so ciphertext can't be shipped to a joiner).
    // Until those two sites are routed through here, leave this false. - xlinka
    public bool EncryptAssetsAtRest { get; set; }

    public string MachineId => _machineId;

    public string BasePath => _basePath;

    public LocalDB(string? dbPath = null)
    {
        _basePath = dbPath ?? GetDefaultBasePath();
        _machineId = GetOrCreateMachineId();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(GetAssetCachePath());
        Directory.CreateDirectory(GetTempPath());

        await LoadAssetRecordsAsync();

        _initialized = true;
        Logger.Log($"LocalDB: Initialized at '{_basePath}' with machine ID '{_machineId}'");
    }

    public async Task<string> ImportLocalAssetAsync(string filePath, ImportLocation location = ImportLocation.Copy)
    {
        if (!File.Exists(filePath))
        {
            Logger.Error($"LocalDB: File not found: {filePath}");
            return null!;
        }

        var hash = await ComputeFileHashAsync(filePath);
        var localUri = $"local://{_machineId}/{hash}";

        lock (_lock)
        {
            if (_assetRecords.TryGetValue(hash, out var existing))
            {
                Logger.Log($"LocalDB: Asset already imported: {localUri}");
                return localUri;
            }
        }

        var extension = Path.GetExtension(filePath);
        var targetPath = Path.Combine(GetAssetCachePath(), hash + extension);

        try
        {
            // Original references the source in place - we don't own that file, so it stays plaintext.
            // Copy/Move land bytes in OUR cache and get wrapped when at-rest encryption is enabled. - xlinka
            bool encrypted = false;
            long plainSize;
            switch (location)
            {
                case ImportLocation.Original:
                    targetPath = filePath;
                    plainSize = new FileInfo(targetPath).Length;
                    break;

                case ImportLocation.Copy:
                    plainSize = await WriteCacheFileAsync(targetPath, filePath, deleteSource: false);
                    encrypted = EncryptAssetsAtRest;
                    break;

                case ImportLocation.Move:
                    plainSize = await WriteCacheFileAsync(targetPath, filePath, deleteSource: true);
                    encrypted = EncryptAssetsAtRest;
                    break;

                default:
                    plainSize = 0;
                    break;
            }

            var record = new LocalAssetRecord
            {
                Hash = hash,
                LocalUri = localUri,
                FilePath = targetPath,
                OriginalPath = filePath,
                OriginalFileName = Path.GetFileName(filePath),
                ImportedAt = DateTime.UtcNow,
                FileSize = plainSize,
                Encrypted = encrypted
            };

            lock (_lock)
            {
                _assetRecords[hash] = record;
            }

            await SaveAssetRecordsAsync();

            Logger.Log($"LocalDB: Imported '{Path.GetFileName(filePath)}' -> {localUri}");
            return localUri;
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: Failed to import '{filePath}': {ex.Message}");
            return null!;
        }
    }

    // Used by the mesh-as-asset path (SaveMeshAsync) but format-agnostic.
    public async Task<string> SaveAssetAsync(byte[] data, string extension = ".lmesh")
    {
        if (data == null)
        {
            Logger.Error("LocalDB: SaveAssetAsync called with null data");
            return null!;
        }

        if (!string.IsNullOrEmpty(extension) && !extension.StartsWith("."))
            extension = "." + extension;

        var hash = ComputeBytesHash(data);
        var localUri = $"local://{_machineId}/{hash}";

        lock (_lock)
        {
            if (_assetRecords.TryGetValue(hash, out _))
            {
                Logger.Debug($"LocalDB: Asset already saved: {localUri}");
                return localUri;
            }
        }

        var targetPath = Path.Combine(GetAssetCachePath(), hash + extension);
        bool encrypted = EncryptAssetsAtRest;
        try
        {
            // Hash addresses the PLAINTEXT (above), so dedup stays content-stable regardless of the
            // at-rest encryption toggle; only the bytes on disk are wrapped. - xlinka
            var onDisk = encrypted ? LocalEncryption.Encrypt(data) : data;
            await File.WriteAllBytesAsync(targetPath, onDisk);
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: Failed to save asset {localUri}: {ex.Message}");
            return null!;
        }

        var record = new LocalAssetRecord
        {
            Hash = hash,
            LocalUri = localUri,
            FilePath = targetPath,
            OriginalPath = localUri,
            OriginalFileName = hash + extension,
            ImportedAt = DateTime.UtcNow,
            FileSize = data.LongLength,
            Encrypted = encrypted
        };

        lock (_lock)
        {
            _assetRecords[hash] = record;
        }

        await SaveAssetRecordsAsync();
        Logger.Log($"LocalDB: Saved {data.Length}-byte asset -> {localUri}");
        return localUri;
    }

    public Task<string> SaveMeshAsync(PhosMesh mesh)
    {
        if (mesh == null)
            return Task.FromResult<string>(null!);

        // Serialize refuses a mesh whose topology cannot round-trip, and it refuses it SYNCHRONOUSLY -
        // so on the import path that throw would escape a Task-returning method and take the whole
        // model down after several meshes had already been written. One unsupported submesh should
        // cost that mesh its bake and nothing else; the caller already falls back to the source URL.
        byte[] data;
        try
        {
            data = PhosMeshSerializer.Serialize(mesh);
        }
        catch (NotSupportedException ex)
        {
            Logger.Warn($"LocalDB: mesh cannot be baked, falling back to the source model - {ex.Message}");
            return Task.FromResult<string>(null!);
        }

        return SaveAssetAsync(data, ".lmesh");
    }

    // DERIVED ASSETS
    //
    // Most entries here are content-addressed: hash the bytes, that's the key. A derived asset is
    // one that is COMPUTED from another asset (a downscaled texture variant, a metadata sidecar)
    // and must be addressable BEFORE it exists, by anyone who knows the base URI. So its key is the
    // base hash plus a suffix instead of its own content hash.
    //
    // That single decision is what makes derived assets transfer for free: the URI still carries
    // the owner's machine id in the same position, so the peer transferer resolves and serves it
    // exactly like any other local asset, and a joiner that has only ever seen the base URI can
    // build the address of a derivative it has never received and request it by name. No manifest
    // of derived assets has to be replicated anywhere. -xlinka

    // '~' is URI-unreserved and filename-safe, and never appears in a hex hash, so it is an
    // unambiguous split point in both a URI and a cache filename.
    private const char DerivedSeparator = '~';

    // The URI of a derived asset: the base URI with suffix appended to its
    // hash, keeping the base's machine id so the owner still serves it. Null if the base is not a
    // local:// URI or is already itself a derivative.
    public static string? DeriveUri(string? baseLocalUri, string suffix)
    {
        if (string.IsNullOrEmpty(baseLocalUri) || string.IsNullOrEmpty(suffix))
            return null;
        if (!baseLocalUri!.StartsWith("local://", StringComparison.Ordinal))
            return null;
        if (baseLocalUri.IndexOf(DerivedSeparator) >= 0)
            return null;
        return baseLocalUri + DerivedSeparator + suffix;
    }

    public static string? GetBaseUri(string? localUri)
    {
        if (string.IsNullOrEmpty(localUri))
            return localUri;
        int separator = localUri!.IndexOf(DerivedSeparator);
        return separator < 0 ? localUri : localUri.Substring(0, separator);
    }

    // Overwrites any previous blob at that address, since a derivative is defined by its inputs: if the suffix
    // matches, the newer computation supersedes the older one.
    public async Task<string> SaveDerivedAssetAsync(string baseLocalUri, string suffix, byte[] data, string extension)
    {
        var uri = DeriveUri(baseLocalUri, suffix);
        if (uri == null || data == null)
        {
            Logger.Error($"LocalDB: cannot derive asset '{suffix}' from '{baseLocalUri}'");
            return null!;
        }

        var key = ExtractKey(uri);
        if (string.IsNullOrEmpty(key))
            return null!;

        if (!string.IsNullOrEmpty(extension) && !extension.StartsWith("."))
            extension = "." + extension;

        // The key can be long-ish (hash + suffix); it is still well inside path limits, and keeping
        // it readable makes the cache directory diagnosable by eye.
        var targetPath = Path.Combine(GetAssetCachePath(), key + extension);
        bool encrypted = EncryptAssetsAtRest;
        try
        {
            var onDisk = encrypted ? LocalEncryption.Encrypt(data) : data;
            await File.WriteAllBytesAsync(targetPath, onDisk);
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: failed to save derived asset {uri}: {ex.Message}");
            return null!;
        }

        var record = new LocalAssetRecord
        {
            Hash = key,
            LocalUri = uri,
            FilePath = targetPath,
            OriginalPath = baseLocalUri,
            OriginalFileName = key + extension,
            ImportedAt = DateTime.UtcNow,
            FileSize = data.LongLength,
            Encrypted = encrypted,
        };

        lock (_lock)
        {
            _assetRecords[key] = record;
        }

        await SaveAssetRecordsAsync();
        return uri;
    }

    // ADOPTION
    //
    // A file that arrived over peer transfer is already addressed: the hash is right there in the URI we
    // asked for. Re-hashing it to find that out reads every byte of an avatar mesh a second time, on the
    // thread that is about to decode it, before anything can be drawn. So take the URI's word for the
    // address, register the record immediately, and check the claim afterwards on a background thread.
    // The window that opens is narrow and bounded: a peer that serves bytes not matching the hash it was
    // asked for gets one load out of it, then loses both the record and the file.
    //
    // Synchronous on purpose. The caller reads the returned path and hands the bytes to a decoder that
    // resolves the format from this record, so the record has to exist by the time this returns or the
    // very first load after a transfer races the write that would have told it what it is. -xlinka
    public string? AdoptGatheredAsset(string localUri, string filePath, string? format = null)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        lock (_lock)
        {
            if (_assetRecords.TryGetValue(key!, out var existing) && File.Exists(existing.FilePath))
            {
                // Idempotent: called again with the file this record already points at, do nothing at all.
                // Deleting there would destroy the cache entry it was asked to create.
                if (string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return existing.FilePath;

                // Someone else already landed these bytes. Drop the duplicate rather than churn the cache.
                try { File.Delete(filePath); } catch { /* temp sweep gets it */ }
                return existing.FilePath;
            }
        }

        var extension = format;
        if (string.IsNullOrEmpty(extension))
            extension = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(extension) && !extension!.StartsWith("."))
            extension = "." + extension;

        var targetPath = Path.Combine(GetAssetCachePath(), key + extension);
        long size;
        try
        {
            // A rename inside our own base path, not a copy: the temp directory and the cache are the
            // same volume by construction.
            File.Move(filePath, targetPath, true);
            size = new FileInfo(targetPath).Length;
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: failed to adopt gathered asset {localUri}: {ex.Message}");
            return null;
        }

        var record = new LocalAssetRecord
        {
            Hash = key!,
            LocalUri = localUri,
            FilePath = targetPath,
            OriginalPath = localUri,
            OriginalFileName = key + extension,
            ImportedAt = DateTime.UtcNow,
            FileSize = size,
            // Adoption moves the bytes as they arrived. At-rest wrapping would mean reading and rewriting
            // the whole file here, which is the cost this path exists to avoid; reads detect the header
            // either way, so a plaintext entry in an otherwise encrypted cache still loads.
            Encrypted = false,
            Verified = false
        };

        lock (_lock)
        {
            _assetRecords[key!] = record;
        }

        _ = SaveAssetRecordsAsync();
        VerifyAdoptedInBackground(key!, targetPath);

        Logger.Log($"LocalDB: adopted gathered asset -> {localUri} ({size} bytes, '{extension}')");
        return targetPath;
    }

    // A derived asset's key is a base hash plus a suffix, so it is not a hash of its own contents and
    // there is nothing here to check it against. Only a bare hash gets verified.
    private static bool IsContentHashKey(string key)
    {
        if (key.Length != 64)
            return false;
        foreach (var c in key)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
                return false;
        }
        return true;
    }

    private void VerifyAdoptedInBackground(string key, string path)
    {
        if (!IsContentHashKey(key))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var actual = await ComputeFileHashAsync(path);
                if (actual == key)
                {
                    lock (_lock)
                    {
                        if (_assetRecords.TryGetValue(key, out var record))
                            record.Verified = true;
                    }
                    await SaveAssetRecordsAsync();
                    return;
                }

                Logger.Error($"LocalDB: adopted asset '{key}' hashes to '{actual}'; a peer served content that is not what was asked for. Dropping it.");
                bool dropped;
                lock (_lock)
                {
                    // Only drop the entry that is still the one we adopted. A legitimate local import of
                    // the same hash can have replaced it while this was hashing, and that one is sound.
                    dropped = _assetRecords.TryGetValue(key, out var record)
                        && string.Equals(record.FilePath, path, StringComparison.OrdinalIgnoreCase)
                        && _assetRecords.Remove(key);
                }

                if (!dropped)
                    return;

                try { File.Delete(path); } catch { /* record is gone either way */ }
                await SaveAssetRecordsAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn($"LocalDB: could not verify adopted asset '{key}': {ex.Message}");
            }
        });
    }

    // METADATA

    // Empty when the asset is unknown here.
    public IReadOnlyDictionary<string, string>? GetAssetMetadata(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return null;
        lock (_lock)
        {
            return _assetRecords.TryGetValue(key, out var record) ? record.Metadata : null;
        }
    }

    // No-op when the asset is not in this database (a peer-owned asset we never received).
    public async Task SetAssetMetadataAsync(string localUri, Action<IDictionary<string, string>> mutate)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key) || mutate == null)
            return;

        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(key, out var record))
                return;
            record.Metadata ??= new Dictionary<string, string>();
            mutate(record.Metadata);
        }

        await SaveAssetRecordsAsync();
    }

    // The record key inside a local:// URI: everything after the machine id.
    private static string? ExtractKey(string localUri)
    {
        if (string.IsNullOrEmpty(localUri) || !localUri.StartsWith("local://", StringComparison.Ordinal))
            return null;
        var parts = localUri.Substring(8).Split('/');
        return parts.Length < 2 ? null : parts[1];
    }

    public string GetFilePath(string localUri)
    {
        if (!localUri.StartsWith("local://"))
            return null!;

        // Parse URI: local://[machineId]/[hash]
        var parts = localUri.Substring(8).Split('/');
        if (parts.Length < 2)
            return null!;

        var hash = parts[1];

        lock (_lock)
        {
            if (_assetRecords.TryGetValue(hash, out var record))
            {
                return record.FilePath;
            }
        }

        return null!;
    }

    public bool Exists(string localUri)
    {
        var path = GetFilePath(localUri);
        return path != null && File.Exists(path);
    }

    // The local:// address of a record this machine holds for a content hash, or null. A cloud
    // asset that was fetched into the database is addressed this way so the variant machinery,
    // which only knows local:// bases, can work on it. -xlinka
    public string? UriForHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        lock (_lock)
        {
            if (_assetRecords.TryGetValue(hash!, out var record) && File.Exists(record.FilePath))
                return $"local://{_machineId}/{hash}";
        }
        return null;
    }

    public string GetTempFilePath(string extension = null!)
    {
        var fileName = Guid.NewGuid().ToString("N");
        if (!string.IsNullOrEmpty(extension))
        {
            if (!extension.StartsWith("."))
                extension = "." + extension;
            fileName += extension;
        }
        return Path.Combine(GetTempPath(), fileName);
    }

    public void CleanupTempFiles(TimeSpan maxAge = default)
    {
        if (maxAge == default)
            maxAge = TimeSpan.FromHours(24);

        var tempPath = GetTempPath();
        if (!Directory.Exists(tempPath))
            return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.GetFiles(tempPath))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch { /* Ignore cleanup errors */ }
        }
    }

    public IReadOnlyList<LocalAssetRecord> GetAllRecords()
    {
        lock (_lock)
        {
            return new List<LocalAssetRecord>(_assetRecords.Values);
        }
    }

    private string GetDefaultBasePath()
    {
        var appData = Lumora.Core.Persistence.PathResolver.LocalPath;
        return Path.Combine(appData, "LumoraVR", "LocalDB");
    }

    private string GetAssetCachePath() => Path.Combine(_basePath, "Assets");
    private string GetTempPath() => Path.Combine(_basePath, "Temp");

    // Cache directory for renderer-side artifacts that are valid only on THIS machine's GPU stack
    // (block-compressed texture variants, above all). These are never content-addressed and never
    // transferred: what a given driver accepts is a property of the machine, not of the asset, so
    // shipping one to a peer would at best waste bandwidth and at worst hand them a format their
    // device cannot sample. Deleting this directory only costs a recompress. -xlinka
    public string GetGpuCachePath()
    {
        var path = Path.Combine(_basePath, "GpuCache");
        try { Directory.CreateDirectory(path); } catch { /* first use recreates it */ }
        return path;
    }

    // Cache directory for renderer-side geometry derived from an asset's own contents - LOD index
    // buffers, above all. Unlike GpuCache these blobs are device-independent (the simplifier is
    // deterministic), they are just expensive: seconds of CPU for a heavy model. Keyed by a hash of
    // the geometry itself, so the same mesh arriving under a new URL still hits. Deleting this
    // directory only costs one re-bake per mesh. -xlinka
    public string GetMeshCachePath()
    {
        var path = Path.Combine(_basePath, "MeshLod");
        try { Directory.CreateDirectory(path); } catch { /* first use recreates it */ }
        return path;
    }

    private string GetOrCreateMachineId()
    {
        // Prefer the install's self-certifying machine identity so local:// asset URIs carry the SAME id
        // the session stamps on User.MachineID. The asset transferer resolves which connected peer owns a
        // local:// asset from that id; when the two differed, a peer-imported mesh/texture could never be
        // relayed to other users. The id is base64url (no '/'), so it doesn't disturb URI parsing, and the
        // transferer reads it case-sensitively from the URI's original string.
        try
        {
            var identityId = Lumora.Core.Security.MachineIdentity.Local.MachineId;
            if (!string.IsNullOrEmpty(identityId))
                return identityId;
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: machine identity unavailable, using a local fallback id: {ex.Message}");
        }

        // Fallback: a stable random id persisted next to the cache.
        var idPath = Path.Combine(_basePath, ".machine_id");
        try
        {
            Directory.CreateDirectory(_basePath);

            if (File.Exists(idPath))
            {
                return File.ReadAllText(idPath).Trim();
            }

            var id = Guid.NewGuid().ToString("N").Substring(0, 16);
            File.WriteAllText(idPath, id);
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }
    }

    // Write a source file into the cache at targetPath, encrypting at rest when enabled, and return the
    // PLAINTEXT byte length. With encryption off we keep the cheap File.Copy/Move (no read-into-memory).
    // With it on we must read -> wrap -> write, since copy alone can't transform the bytes. - xlinka
    private async Task<long> WriteCacheFileAsync(string targetPath, string sourcePath, bool deleteSource)
    {
        if (!EncryptAssetsAtRest)
        {
            if (deleteSource)
                await Task.Run(() => File.Move(sourcePath, targetPath, true));
            else
                await Task.Run(() => File.Copy(sourcePath, targetPath, true));
            return new FileInfo(targetPath).Length;
        }

        var plain = await File.ReadAllBytesAsync(sourcePath);
        await File.WriteAllBytesAsync(targetPath, LocalEncryption.Encrypt(plain));
        if (deleteSource)
        {
            try { File.Delete(sourcePath); } catch { /* best effort - source already consumed */ }
        }
        return plain.LongLength;
    }

    // Read a cached asset's bytes, transparently decrypting if it was stored encrypted. A plaintext or
    // legacy file is returned as-is (Decrypt detects the header), so this
    // is safe to call for every local:// read and doubles as the migration path. Returns null when the
    // URI doesn't resolve. - xlinka
    public async Task<byte[]?> ReadAssetBytesAsync(string localUri)
    {
        var path = GetFilePath(localUri);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var raw = await File.ReadAllBytesAsync(path);
        try
        {
            return LocalEncryption.Decrypt(raw);
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: failed to decrypt cached asset '{localUri}': {ex.Message}");
            return null;
        }
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await Task.Run(() => sha256.ComputeHash(stream));
        return Convert.ToHexString(hashBytes).ToLower();
    }

    private static string ComputeBytesHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(data);
        return Convert.ToHexString(hashBytes).ToLower();
    }

    private async Task LoadAssetRecordsAsync()
    {
        var recordsPath = Path.Combine(_basePath, "records.json");
        if (!File.Exists(recordsPath))
            return;

        try
        {
            // Transparently handle both the encrypted store and a plain/legacy records.json.
            var raw = await File.ReadAllBytesAsync(recordsPath);
            var json = Encoding.UTF8.GetString(LocalEncryption.Decrypt(raw));
            var records = JsonSerializer.Deserialize<List<LocalAssetRecord>>(json);
            if (records == null) return;

            lock (_lock)
            {
                foreach (var record in records)
                {
                    if (!string.IsNullOrEmpty(record.Hash))
                        _assetRecords[record.Hash] = record;
                }
            }

            Logger.Log($"LocalDB: Loaded {records.Count} asset records");
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: Failed to load records: {ex.Message}");
        }
    }

    private async Task SaveAssetRecordsAsync()
    {
        var recordsPath = Path.Combine(_basePath, "records.json");
        try
        {
            List<LocalAssetRecord> snapshot;
            lock (_lock)
                snapshot = new List<LocalAssetRecord>(_assetRecords.Values);

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            // The records store (asset list, original paths, per-asset keys) is encrypted at rest.
            var bytes = LocalEncryption.Encrypt(Encoding.UTF8.GetBytes(json));
            await File.WriteAllBytesAsync(recordsPath, bytes);
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: Failed to save records: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Cleanup temp files older than 1 hour on dispose
        CleanupTempFiles(TimeSpan.FromHours(1));
    }
}

public class LocalAssetRecord
{
    public string Hash { get; set; } = null!;
    public string LocalUri { get; set; } = null!;
    public string FilePath { get; set; } = null!;
    public string OriginalPath { get; set; } = null!;
    public string OriginalFileName { get; set; } = null!;
    public DateTime ImportedAt { get; set; }
    public long FileSize { get; set; }

    // True when this cache file is wrapped with LocalEncryption (AES-GCM) at rest.
    // Stored in the encrypted records store. Reads go through ReadAssetBytesAsync,
    // which detects the encryption header regardless of this flag, so a plaintext/legacy file still
    // reads correctly even if the flag is stale. FileSize is the PLAINTEXT length.
    public bool Encrypted { get; set; }

    // False only between a peer-gathered file being adopted under the hash in its URI and the background
    // check confirming the bytes hash to that. Defaults true so every record written before this existed,
    // and every record whose bytes we hashed ourselves, reads back as settled.
    public bool Verified { get; set; } = true;

    // Reserved. The current model uses a single machine-bound master key (see LocalEncryption),
    // not a per-asset key, so this stays null. Kept for a future per-asset / server-issued key scheme.
    public byte[]? EncryptionKey { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();
}
