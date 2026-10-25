using System;
using System.Text.Json.Serialization;

namespace RdpManager.Models;

public class RdpConnection
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;
    public string Domain { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Only ever reaches disk inside the master-password-encrypted store envelope.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>When the password was last entered in the editor; null for passwords from stores written before this was kept.</summary>
    public DateTimeOffset? PasswordSavedAt { get; set; }

    /// <summary>Display/session settings from an imported .rdp file or the editor's advanced block, reused when launching; null means the defaults.</summary>
    public string? RawRdpContent { get; set; }

    [JsonIgnore]
    public string DisplayTarget => Port == 3389 ? Host : $"{Host}:{Port}";

    /// <summary>"DOMAIN\user", or just the user name without a domain - as the list shows it and the .rdp file carries it.</summary>
    [JsonIgnore]
    public string FullUsername => string.IsNullOrWhiteSpace(Domain) ? Username : $"{Domain}\\{Username}";

    /// <summary>Whether the list offers copying the password - the key in the row's last column.</summary>
    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>A separate copy of every field, the password and the .rdp settings included.</summary>
    public RdpConnection Clone() => (RdpConnection)MemberwiseClone();

    /// <summary>What screen readers announce for the connection's row in the list.</summary>
    public override string ToString() => Name;
}
