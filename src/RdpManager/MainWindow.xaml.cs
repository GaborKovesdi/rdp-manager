using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RdpManager.Models;
using RdpManager.Services;
using RdpManager.Views;

namespace RdpManager;

public partial class MainWindow : Window
{
    private readonly ConnectionStore _store;
    private readonly ObservableCollection<RdpConnection> _connections = new();

    public MainWindow(ConnectionStore store, IEnumerable<RdpConnection> connections)
    {
        InitializeComponent();
        _store = store;
        ConnectionsGrid.ItemsSource = _connections;

        foreach (var connection in connections)
            _connections.Add(connection);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Anything still parked for a session we started - credentials in Windows Credential
        // Manager, temp .rdp files - would otherwise outlive the app, so drop it before going away.
        RdpLauncher.ReleaseAll();
        _store.Dispose();
        base.OnClosed(e);
    }

    private RdpConnection? SelectedConnection => ConnectionsGrid.SelectedItem as RdpConnection;

    private void SaveAndRefresh()
    {
        try
        {
            _store.Save(_connections);
            ConnectionsGrid.Items.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Nem sikerült menteni a kapcsolatokat:\n\n{ex.Message}", "Mentési hiba",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "RDP fájl(ok) importálása",
            Filter = "RDP fájlok (*.rdp)|*.rdp|Minden fájl (*.*)|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var imported = new List<RdpConnection>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                imported.Add(RdpFileService.ParseFile(file));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nem sikerült importálni: {file}\n\n{ex.Message}", "Importálási hiba",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (imported.Count == 0)
        {
            StatusText.Text = "Egy kapcsolat sem került importálásra.";
            return;
        }

        foreach (var connection in imported)
            _connections.Add(connection);

        SaveAndRefresh();
        StatusText.Text = $"{imported.Count} kapcsolat importálva.";

        // Imported .rdp files never carry a usable password, so open the editor for the last one
        // to fill in credentials straight away.
        EditConnection(imported[^1]);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var connection = new RdpConnection();
        var editor = new ConnectionEditorWindow(connection) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _connections.Add(connection);
            SaveAndRefresh();
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is { } connection)
            EditConnection(connection);
        else
            StatusText.Text = "Válassz ki egy kapcsolatot a szerkesztéshez.";
    }

    private void EditConnection(RdpConnection connection)
    {
        var editor = new ConnectionEditorWindow(connection) { Owner = this };
        if (editor.ShowDialog() == true)
            SaveAndRefresh();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection)
        {
            StatusText.Text = "Válassz ki egy kapcsolatot a törléshez.";
            return;
        }

        var result = MessageBox.Show(this, $"Biztosan törlöd a(z) \"{connection.Name}\" kapcsolatot?",
            "Törlés megerősítése", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _connections.Remove(connection);
            SaveAndRefresh();
        }
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (SelectedConnection is not { } connection)
        {
            StatusText.Text = "Válassz ki egy kapcsolatot az átrendezéshez.";
            return;
        }

        var oldIndex = _connections.IndexOf(connection);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= _connections.Count)
            return;

        _connections.Move(oldIndex, newIndex);
        SaveAndRefresh();

        // Refresh() can drop the selection, and keeping it lets the user keep clicking the arrow.
        ConnectionsGrid.SelectedItem = connection;
        ConnectionsGrid.ScrollIntoView(connection);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is { } connection)
            Connect(connection);
        else
            StatusText.Text = "Válassz ki egy kapcsolatot a csatlakozáshoz.";
    }

    private void ConnectionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedConnection is { } connection)
            Connect(connection);
    }

    private async void Connect(RdpConnection connection)
    {
        try
        {
            StatusText.Text = $"Csatlakozás: {connection.Name} ({connection.DisplayTarget})...";
            await RdpLauncher.ConnectAsync(connection);
            StatusText.Text = $"Az mstsc elindult: {connection.Name}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Sikertelen csatlakozás.";
            MessageBox.Show(this, ex.Message, "Csatlakozási hiba", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
