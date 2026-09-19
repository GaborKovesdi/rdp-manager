using System;
using System.Text.Json.Serialization;

namespace RdpManager.Models;

public class RdpConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;
    public string Domain { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Only ever reaches disk inside the master-password-encrypted store envelope.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Display/session settings from an imported .rdp file, reused when launching.</summary>
    public string? RawRdpContent { get; set; }

    [JsonIgnore]
    public string DisplayTarget => Port == 3389 ? Host : $"{Host}:{Port}";
}
