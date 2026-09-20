using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Services;

/// <summary>
/// The outcome of reading a legacy store. A failed read is kept distinct from an empty one so a
/// damaged file is never mistaken for "there was nothing to migrate".
/// </summary>
public sealed record MigrationResult(bool ReadFailed, List<RdpConnection> Connections, int PasswordsLost)
{
    public static MigrationResult NotAttempted { get; } = new(false, new List<RdpConnection>(), 0);
}

/// <summary>
/// Reads the pre-master-password store format (plain JSON, passwords encrypted with DPAPI) so
/// connections saved by an earlier build survive the switch to the encrypted store. Can be
/// dropped once no installation carries a legacy connections.json any more.
/// </summary>
internal static class LegacyStoreMigration
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RdpManager.v1.LocalStore");

    private sealed class LegacyConnection
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 3389;
        public string Domain { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string EncryptedPassword { get; set; } = string.Empty;
        public string? RawRdpContent { get; set; }
    }

    public static MigrationResult Read(string legacyFilePath)
    {
        List<LegacyConnection>? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<List<LegacyConnection>>(File.ReadAllText(legacyFilePath));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new MigrationResult(true, new List<RdpConnection>(), 0);
        }

        var connections = new List<RdpConnection>();
        var passwordsLost = 0;

        foreach (var item in legacy ?? new List<LegacyConnection>())
        {
            var password = TryUnprotect(item.EncryptedPassword);
            if (password.Length == 0 && item.EncryptedPassword.Length > 0)
                passwordsLost++;

            connections.Add(new RdpConnection
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                Name = item.Name,
                Host = item.Host,
                Port = item.Port,
                Domain = item.Domain,
                Username = item.Username,
                Password = password,
                RawRdpContent = item.RawRdpContent,
            });
        }

        return new MigrationResult(false, connections, passwordsLost);
    }

    private static string TryUnprotect(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        byte[] bytes;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipherText), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Legacy DPAPI blobs only decrypt for the Windows user that wrote them; on any other
            // account the connection is still migrated, just without its password.
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
