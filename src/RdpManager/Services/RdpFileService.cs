using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal static (string Host, int Port) ParseAddress(string value)
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
        var lines = GetSettingLines(connection);

        lines.Add($"full address:s:{GetAddress(connection)}");
        lines.Add($"username:s:{connection.FullUsername}");
        lines.Add("prompt for credentials:i:0");

        // The password field mstsc itself writes when you tick "remember me": the password
        // encrypted with DPAPI for the current Windows user, hex encoded. mstsc reads it straight
        // out of the file, which avoids depending on it finding the right Credential Manager entry.
        if (!string.IsNullOrEmpty(connection.Password))
            lines.Add($"password 51:b:{ProtectPassword(connection.Password)}");

        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// The connection's own .rdp settings - the imported ones, or the defaults - as the editor's
    /// advanced block shows them and the launched file starts out from.
    /// </summary>
    public static List<string> GetSettingLines(RdpConnection connection) =>
        ParseSettingLines(!string.IsNullOrWhiteSpace(connection.RawRdpContent)
            ? connection.RawRdpContent
            : string.Join("\r\n", DefaultTemplateLines));

    /// <summary>The non-empty lines of .rdp text, without the keys BuildRdpContent always sets itself.</summary>
    public static List<string> ParseSettingLines(string content)
    {
        var lines = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split(':', 3);
            if (parts.Length < 3 || !SkipKeys.Contains(parts[0].Trim()))
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>The first line that is not a "key:type:value" .rdp setting, or null when all of them are.</summary>
    internal static string? FindInvalidSettingLine(IEnumerable<string> lines) =>
        lines.FirstOrDefault(line => line.Split(':', 3) is not [var key, "i" or "s" or "b", _] || key.Trim().Length == 0);

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

    /// <summary>
    /// Writes the connection's .rdp content to a fresh temp file and returns its path. mstsc puts
    /// the file name into the session window's title - and so into the taskbar - which is why the
    /// file is named after the connection. The folder is a fresh GUID, so stored fields still can
    /// never steer where the file lands; only the leaf name comes from them, and it is sanitised.
    /// </summary>
    public static string WriteTempRdpFile(RdpConnection connection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rdpmanager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, ToSafeFileName(connection.Name) + ".rdp");
        try
        {
            File.WriteAllText(path, BuildRdpContent(connection));
        }
        catch
        {
            // The caller never gets the path of a failed write, so nothing else would ever remove
            // the folder - or a half-written file in it, which may already hold the password.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        return path;
    }

    private const int MaxFileNameLength = 80;

    // Device names Windows resolves whatever extension follows them, including the superscript
    // COM/LPT ports and the console handles.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>A connection name made safe to use as a single file name component.</summary>
    public static string ToSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);

        // Windows drops trailing dots and spaces, and ".." must never survive as a name.
        var safe = builder.ToString().Trim().Trim('.', ' ');
        if (safe.Length > MaxFileNameLength)
        {
            // Never cut between the two halves of a surrogate pair, such as an emoji.
            var cut = char.IsHighSurrogate(safe[MaxFileNameLength - 1]) ? MaxFileNameLength - 1 : MaxFileNameLength;
            safe = safe[..cut].TrimEnd('.', ' ');
        }

        // Nothing of the name survived: it was empty, or made only of characters that were replaced.
        if (safe.All(c => c == '_'))
            return "RDP";

        // Windows ignores spaces before the extension too, so "NUL .txt" is still the NUL device.
        return ReservedDeviceNames.Contains(safe.Split('.')[0].TrimEnd(' ')) ? "_" + safe : safe;
    }
}
