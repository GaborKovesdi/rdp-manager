using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Views;

public partial class ConnectionEditorWindow : Window
{
    private readonly RdpConnection _connection;
    private readonly List<string> _shownSettings;

    public ConnectionEditorWindow(RdpConnection connection)
    {
        InitializeComponent();
        _connection = connection;
        var hasPassword = !string.IsNullOrEmpty(connection.Password);

        Title = string.IsNullOrEmpty(connection.Host) ? "Új kapcsolat" : "Kapcsolat szerkesztése";
        NameBox.Text = connection.Name;
        HostBox.Text = connection.Host;
        PortBox.Text = connection.Port.ToString();
        DomainBox.Text = connection.Domain;
        UsernameBox.Text = connection.Username;
        PasswordBox.Password = string.Empty;
        _shownSettings = RdpFileService.GetSettingLines(connection);
        AdvancedBox.Text = string.Join("\r\n", _shownSettings);

        // The box always starts empty, so this line is what tells whether a password is stored, and since when.
        PasswordHint.Text = !hasPassword ? "Nincs mentett jelszó."
            : connection.PasswordSavedAt is { } savedAt ? $"Van mentett jelszó, mentve: {savedAt.LocalDateTime:g}. Üresen hagyva megmarad."
            : "Van mentett jelszó. Üresen hagyva megmarad.";

        // A fresh import has everything but the password, which is what the editor was opened for.
        Loaded += (_, _) => (connection.Host.Length > 0 && !hasPassword ? (Control)PasswordBox : NameBox).Focus();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            ShowError("A gépnév vagy IP-cím megadása kötelező.");
            HostBox.Focus();
            return;
        }

        // "host:port" typed into the host box would otherwise be bracketed as an IPv6 address.
        var (parsedHost, parsedPort) = RdpFileService.ParseAddress(host);
        if (parsedHost != host)
        {
            host = parsedHost;
            PortBox.Text = parsedPort.ToString();
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            ShowError("A port 1 és 65535 közötti szám legyen (alapértelmezés: 3389).");
            PortBox.Focus();
            return;
        }

        // Untouched settings stay exactly as stored, so a connection on the defaults keeps following them.
        var settings = RdpFileService.ParseSettingLines(AdvancedBox.Text);
        var settingsChanged = !settings.SequenceEqual(_shownSettings);
        if (settingsChanged && RdpFileService.FindInvalidSettingLine(settings) is { } invalid)
        {
            ShowError($"Hibás sor a speciális beállítások között: \"{invalid}\". A sorok formája kulcs:típus:érték, a típus i, s vagy b.");
            AdvancedExpander.IsExpanded = true;
            AdvancedBox.Focus();
            return;
        }

        if (settingsChanged)
            _connection.RawRdpContent = settings.Count > 0 ? string.Join("\r\n", settings) : null;

        _connection.Name = name.Length > 0 ? name : host;
        _connection.Host = host;
        _connection.Port = port;
        _connection.Domain = DomainBox.Text.Trim();
        _connection.Username = UsernameBox.Text.Trim();

        if (!string.IsNullOrEmpty(PasswordBox.Password))
        {
            _connection.Password = PasswordBox.Password;
            _connection.PasswordSavedAt = DateTimeOffset.Now;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
