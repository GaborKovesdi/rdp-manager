using System;
using System.Windows;
using System.Windows.Controls;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Views;

public partial class ConnectionEditorWindow : Window
{
    private readonly RdpConnection _connection;

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

        if (hasPassword)
            PasswordHint.Visibility = Visibility.Visible;

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

        _connection.Name = name.Length > 0 ? name : host;
        _connection.Host = host;
        _connection.Port = port;
        _connection.Domain = DomainBox.Text.Trim();
        _connection.Username = UsernameBox.Text.Trim();

        if (!string.IsNullOrEmpty(PasswordBox.Password))
            _connection.Password = PasswordBox.Password;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
