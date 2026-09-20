using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Services;

public enum UnlockResult
{
    Success,
    WrongPassword,
    Corrupted,
}

/// <summary>
/// Stores all connections - including their passwords - as a single AES-GCM encrypted blob in a
/// "data" folder next to the executable, keyed by the master password. Because the whole payload
/// is authenticated, host names and usernames can't be altered on disk without the file failing
/// to open, and because the key comes from the master password rather than DPAPI, the folder
/// remains usable when copied to another machine.
/// </summary>
public sealed class ConnectionStore : IDisposable
{
    private sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    private const int CurrentVersion = 1;
    private const int MinimumIterations = 100_000;
    private const int MaximumIterations = 2_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly string _legacyFilePath;

    private byte[]? _key;
    private byte[] _salt = Array.Empty<byte>();
    private int _iterations = StoreCrypto.DefaultIterations;

    public ConnectionStore()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "connections.dat");
        _legacyFilePath = Path.Combine(dataDir, "connections.json");
    }

    public bool StoreExists => File.Exists(_filePath);

    public bool LegacyStoreExists => !StoreExists && File.Exists(_legacyFilePath);

    /// <summary>
    /// Creates a new store protected by the given master password, taking over any connections
    /// found in a legacy store. Returns the connections the new store starts with.
    /// </summary>
    public MigrationResult Initialize(string masterPassword)
    {
        var migrateFrom = LegacyStoreExists ? _legacyFilePath : null;

        var migration = migrateFrom is not null
            ? LegacyStoreMigration.Read(migrateFrom)
            : MigrationResult.NotAttempted;

        // A legacy file that cannot be read stops the whole thing: creating the store now would
        // hide the old connections behind an empty one that never retries the migration, and the
        // old file would be renamed away as though it had been taken over.
        if (migration.ReadFailed)
            return migration;

        _salt = StoreCrypto.CreateSalt();
        _iterations = StoreCrypto.DefaultIterations;
        _key = StoreCrypto.DeriveKey(masterPassword, _salt, _iterations);

        Save(migration.Connections);

        if (migrateFrom is not null)
            ArchiveLegacyStore(migrateFrom);

        return migration;
    }

    public UnlockResult TryUnlock(string masterPassword, out List<RdpConnection> connections)
    {
        connections = new List<RdpConnection>();

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(_filePath), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return UnlockResult.Corrupted;
        }

        // Everything the file claims is checked before the key derivation runs: the work factor
        // comes out of the file, so an absurd value would otherwise hang the app on a hostile or
        // damaged store.
        if (envelope is null
            || envelope.Version != CurrentVersion
            || envelope.Iterations < MinimumIterations
            || envelope.Iterations > MaximumIterations)
            return UnlockResult.Corrupted;

        byte[] salt, nonce, ciphertext, tag;
        try
        {
            salt = Convert.FromBase64String(envelope.Salt ?? string.Empty);
            nonce = Convert.FromBase64String(envelope.Nonce ?? string.Empty);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext ?? string.Empty);
            tag = Convert.FromBase64String(envelope.Tag ?? string.Empty);
        }
        catch (FormatException)
        {
            return UnlockResult.Corrupted;
        }

        if (salt.Length != StoreCrypto.SaltSize || nonce.Length != StoreCrypto.NonceSize || tag.Length != StoreCrypto.TagSize)
            return UnlockResult.Corrupted;

        var key = StoreCrypto.DeriveKey(masterPassword, salt, envelope.Iterations);
        byte[] plaintext;
        try
        {
            plaintext = StoreCrypto.Decrypt(key, nonce, ciphertext, tag);
        }
        catch (CryptographicException)
        {
            // Wrong master password, or the file was altered after it was written.
            CryptographicOperations.ZeroMemory(key);
            return UnlockResult.WrongPassword;
        }
        catch (ArgumentException)
        {
            CryptographicOperations.ZeroMemory(key);
            return UnlockResult.Corrupted;
        }

        try
        {
            connections = JsonSerializer.Deserialize<List<RdpConnection>>(plaintext, JsonOptions) ?? new List<RdpConnection>();
        }
        catch (JsonException)
        {
            CryptographicOperations.ZeroMemory(key);
            return UnlockResult.Corrupted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        _key = key;
        _salt = salt;
        _iterations = envelope.Iterations;
        return UnlockResult.Success;
    }

    public void Save(IEnumerable<RdpConnection> connections)
    {
        if (_key is null)
            throw new InvalidOperationException("A tároló nincs feloldva.");

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(connections, JsonOptions);
        try
        {
            var (nonce, ciphertext, tag) = StoreCrypto.Encrypt(_key, plaintext);
            var envelope = new Envelope
            {
                Iterations = _iterations,
                Salt = Convert.ToBase64String(_salt),
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag),
            };

            WriteAtomic(JsonSerializer.Serialize(envelope, JsonOptions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Writes through a temp file so an interrupted save can't leave a truncated store behind.</summary>
    private void WriteAtomic(string content)
    {
        // The temp name carries this process's id: two instances writing "connections.dat.tmp" at
        // once would otherwise tear each other's half-written file.
        var tempPath = $"{_filePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tempPath, content);

        if (File.Exists(_filePath))
            File.Replace(tempPath, _filePath, null);
        else
            File.Move(tempPath, _filePath);
    }

    /// <summary>Keeps the migrated legacy file around (renamed) rather than deleting the user's only copy.</summary>
    private static void ArchiveLegacyStore(string legacyFilePath)
    {
        try
        {
            File.Move(legacyFilePath, legacyFilePath + ".migrated", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);

        _key = null;
    }
}
