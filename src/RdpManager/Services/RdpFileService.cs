using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RdpManager.Models;

namespace RdpManager.Services;

/// <summary>
/// Parses imported .rdp files and builds the .rdp content used to launch mstsc.exe.
/// </summary>
public static class RdpFileService
{
    private const int DefaultPort = 3389;

    private static readonly string[] DefaultTemplateLines =
    {
        "screen mode id:i:2",
        "use multimon:i:0",
        "desktopwidth:i:1920",
        "desktopheight:i:1080",
        "session bpp:i:32",
        "compression:i:1",
        "keyboardhook:i:2",
        "audiocapturemode:i:0",
        "videoplaybackmode:i:1",
        "connection type:i:7",
        "networkautodetect:i:1",
        "bandwidthautodetect:i:1",
        "displayconnectionbar:i:1",
        "disable wallpaper:i:0",
        "allow font smoothing:i:1",
        "allow desktop composition:i:1",
        "disable full window drag:i:0",
        "disable menu anims:i:0",
        "disable themes:i:0",
        "bitmapcachepersistenable:i:1",
        "authentication level:i:2",
    };

    // Keys never copied over verbatim: either we regenerate them (identity), or they hold a
    // password blob bound to the user/machine that exported the file.
    private static readonly HashSet<string> SkipKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password 51",
        "full address",
        "username",
        "domain",
        "prompt for credentials",
    };

    public static RdpConnection ParseFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var entries = ParseEntries(content);

        var connection = new RdpConnection
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            // The source file's encrypted password blob is useless to us and would only be an
            // extra secret at rest, so it never enters our store.
            RawRdpContent = StripKey(content, "password 51"),
            Port = DefaultPort,
        };

        if (entries.TryGetValue("full address", out var address))
        {
            var (host, port) = ParseAddress(address);
            connection.Host = host;
            connection.Port = port;
        }

        if (entries.TryGetValue("username", out var username))
        {
            var backslash = username.IndexOf('\\');
            if (backslash >= 0)
            {
                connection.Domain = username[..backslash];
                connection.Username = username[(backslash + 1)..];
            }
            else
            {
                connection.Username = username;
            }
        }

        if (entries.TryGetValue("domain", out var domain) && !string.IsNullOrWhiteSpace(domain))
            connection.Domain = domain;

        return connection;
    }

    /// <summary>Splits "host", "host:port", "[::1]" or "[::1]:port" - a bare IPv6 literal has no port part.</summary>
    private static (string Host, int Port) ParseAddress(string value)
    {
        value = value.Trim();

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                var bracketed = value[..(close + 1)];
                var rest = value[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketPort))
                    return (bracketed, bracketPort);

                return (bracketed, DefaultPort);
            }
        }

        var lastColon = value.LastIndexOf(':');
        var isSingleColon = lastColon > 0 && value.IndexOf(':') == lastColon;
        if (isSingleColon && int.TryParse(value[(lastColon + 1)..], out var port))
            return (value[..lastColon], port);

        return (value, DefaultPort);
    }

    private static Dictionary<string, string> ParseEntries(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split(':', 3);
            if (parts.Length == 3)
                result[parts[0].Trim()] = parts[2];
        }

        return result;
    }

    private static string StripKey(string content, string key)
    {
        var kept = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split(':', 3);
            if (parts.Length == 3 && string.Equals(parts[0].Trim(), key, StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(line);
        }

        return string.Join("\r\n", kept);
    }

    /// <summary>
    /// The address exactly as it goes into "full address" - which is also the name mstsc uses to
    /// look up a saved credential, so the two must be derived from the same place. The port is
    /// left off when it is the default, because "host" and "host:3389" are different lookup keys.
    /// </summary>
    public static string GetAddress(RdpConnection connection)
    {
        var host = connection.Host.Contains(':') && !connection.Host.StartsWith('[')
            ? $"[{connection.Host}]"
            : connection.Host;

        return connection.Port == DefaultPort ? host : $"{host}:{connection.Port}";
    }

    /// <summary>The RD Gateway this connection goes through, or null when it connects directly.</summary>
    public static string? GetGatewayHost(RdpConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.RawRdpContent))
            return null;

        var entries = ParseEntries(connection.RawRdpContent);

        if (entries.TryGetValue("gatewayusagemethod", out var usage) && usage.Trim() == "0")
            return null;

        if (entries.TryGetValue("gatewayhostname", out var gateway))
        {
            gateway = gateway.Trim();
            if (gateway.Length > 0)
                return gateway;
        }

        return null;
    }

    /// <summary>
    /// Builds the .rdp text used to launch a connection: keeps any imported display/session
    /// settings, but always injects the current host/username and forces "prompt for credentials"
    /// off, since the password is supplied through Windows Credential Manager (see RdpLauncher).
    /// </summary>
    public static string BuildRdpContent(RdpConnection connection)
    {
        var lines = new List<string>();

        var sourceLines = !string.IsNullOrWhiteSpace(connection.RawRdpContent)
            ? connection.RawRdpContent.Split('\n')
            : DefaultTemplateLines;

        foreach (var rawLine in sourceLines)
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split(':', 3);
            if (parts.Length < 3 || !SkipKeys.Contains(parts[0].Trim()))
                lines.Add(line);
        }

        var fullUsername = string.IsNullOrWhiteSpace(connection.Domain)
            ? connection.Username
            : $"{connection.Domain}\\{connection.Username}";

        lines.Add($"full address:s:{GetAddress(connection)}");
        lines.Add($"username:s:{fullUsername}");
        lines.Add("prompt for credentials:i:0");

        // The password field mstsc itself writes when you tick "remember me": the password
        // encrypted with DPAPI for the current Windows user, hex encoded. mstsc reads it straight
        // out of the file, which avoids depending on it finding the right Credential Manager entry.
        if (!string.IsNullOrEmpty(connection.Password))
            lines.Add($"password 51:b:{ProtectPassword(connection.Password)}");

        return string.Join("\r\n", lines);
    }

    private static string ProtectPassword(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        try
        {
            return Convert.ToHexString(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Writes the connection's .rdp content to a fresh temp file and returns its path.</summary>
    public static string WriteTempRdpFile(RdpConnection connection)
    {
        // The file name is built only from a fresh GUID: stored fields must never be able to
        // steer where this gets written.
        var path = Path.Combine(Path.GetTempPath(), $"rdpmanager-{Guid.NewGuid():N}.rdp");
        File.WriteAllText(path, BuildRdpContent(connection));
        return path;
    }
}
