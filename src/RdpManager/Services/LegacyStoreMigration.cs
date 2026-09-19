using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Services;

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

    public static List<RdpConnection> Read(string legacyFilePath)
    {
        List<LegacyConnection>? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<List<LegacyConnection>>(File.ReadAllText(legacyFilePath));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<RdpConnection>();
        }

        var result = new List<RdpConnection>();
        foreach (var item in legacy ?? new List<LegacyConnection>())
        {
            result.Add(new RdpConnection
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                Name = item.Name,
                Host = item.Host,
                Port = item.Port,
                Domain = item.Domain,
                Username = item.Username,
                Password = TryUnprotect(item.EncryptedPassword),
                RawRdpContent = item.RawRdpContent,
            });
        }

        return result;
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
