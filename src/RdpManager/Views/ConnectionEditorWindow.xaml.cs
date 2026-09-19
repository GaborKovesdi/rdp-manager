using System;
using System.Windows;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Views;

public partial class ConnectionEditorWindow : Window
{
    private readonly RdpConnection _connection;
    private readonly bool _hasExistingPassword;

    public ConnectionEditorWindow(RdpConnection connection)
    {
        InitializeComponent();
        _connection = connection;
        _hasExistingPassword = !string.IsNullOrEmpty(connection.Password);

        NameBox.Text = connection.Name;
        HostBox.Text = connection.Host;
        PortBox.Text = connection.Port.ToString();
        DomainBox.Text = connection.Domain;
        UsernameBox.Text = connection.Username;
        PasswordBox.Password = string.Empty;

        if (_hasExistingPassword)
            PasswordBox.ToolTip = "Hagyd üresen a jelenlegi jelszó megtartásához.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "A név megadása kötelező.";
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            ErrorText.Text = "A gépnév vagy IP-cím megadása kötelező.";
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            ErrorText.Text = "Érvénytelen port (1-65535).";
            return;
        }

        _connection.Name = name;
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
