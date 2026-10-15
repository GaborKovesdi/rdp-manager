using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    private readonly RowDragReorder _rowDragReorder;
    private bool _hasUnsavedChanges;

    public MainWindow(ConnectionStore store, IEnumerable<RdpConnection> connections)
    {
        InitializeComponent();
        _store = store;
        ConnectionsGrid.ItemsSource = _connections;
        _rowDragReorder = new RowDragReorder(ConnectionsGrid, _connections, DropIndicator, OnConnectionMoved);

        foreach (var connection in connections)
            _connections.Add(connection);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            var answer = MessageBox.Show(this,
                "A listán vannak olyan módosítások, amelyeket nem sikerült menteni. Kilépsz így?",
                "Nem mentett módosítások", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }

        base.OnClosing(e);
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

    /// <summary>
    /// Persists the list and reports whether that worked, so a caller never announces success
    /// over a failed save. On failure the list on screen is ahead of the file on disk, and the
    /// status line keeps saying so until a save goes through.
    /// </summary>
    private bool SaveAndRefresh()
    {
        try
        {
            _store.Save(_connections);
            ConnectionsGrid.Items.Refresh();
            _hasUnsavedChanges = false;
            return true;
        }
        catch (Exception ex)
        {
            ConnectionsGrid.Items.Refresh();
            _hasUnsavedChanges = true;
            StatusText.Text = "Nem mentett módosítások: a lista eltér a lemezen lévő tárolótól.";
            MessageBox.Show(this, $"Nem sikerült menteni a kapcsolatokat:\n\n{ex.Message}", "Mentési hiba",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importálás .rdp fájlból vagy másik tárolóból",
            Filter = "RDP fájlok és tárolók (*.rdp;*.dat)|*.rdp;*.dat|Minden fájl (*.*)|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var added = 0;
        var skipped = 0;
        RdpConnection? lastRdpImport = null;

        foreach (var file in dialog.FileNames)
        {
            var isStore = string.Equals(Path.GetExtension(file), ".dat", StringComparison.OrdinalIgnoreCase);
            List<RdpConnection> found;
            try
            {
                found = isStore ? ReadOtherStore(file) : new List<RdpConnection> { RdpFileService.ParseFile(file) };
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nem sikerült importálni: {file}\n\n{ex.Message}", "Importálási hiba",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            foreach (var connection in found)
            {
                // Importing the same file twice, or a store that overlaps this one, mustn't double the list.
                if (_connections.Any(c => c.Name == connection.Name && c.Host == connection.Host && c.Port == connection.Port
                                          && c.Domain == connection.Domain && c.Username == connection.Username))
                {
                    skipped++;
                    continue;
                }

                _connections.Add(connection);
                added++;
                if (!isStore)
                    lastRdpImport = connection;
            }
        }

        if (added == 0)
        {
            StatusText.Text = skipped == 0
                ? "Egy kapcsolat sem került importálásra."
                : $"Nincs új kapcsolat: mind a(z) {skipped} már szerepel a listában.";
            return;
        }

        if (SaveAndRefresh())
            StatusText.Text = skipped == 0
                ? $"{added} kapcsolat importálva."
                : $"{added} kapcsolat importálva, {skipped} már szerepelt a listában.";

        // Imported .rdp files never carry a usable password, so open the editor for the last one
        // to fill in credentials straight away. Connections from another store bring their own.
        if (lastRdpImport is not null)
            EditConnection(lastRdpImport);
    }

    /// <summary>Another store's connections, once the user has given its master password; none if they cancel.</summary>
    private List<RdpConnection> ReadOtherStore(string path)
    {
        using var other = new ConnectionStore(path);
        var prompt = new MasterPasswordWindow(other, Path.GetFileName(path)) { Owner = this };
        return prompt.ShowDialog() == true ? prompt.Connections : new List<RdpConnection>();
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
        OnConnectionMoved(connection);
    }

    private void OnConnectionMoved(RdpConnection connection)
    {
        SaveAndRefresh();

        // Refresh() can drop the selection, and keeping it lets the user keep moving the same row.
        ConnectionsGrid.SelectedItem = connection;
        ConnectionsGrid.ScrollIntoView(connection);
    }

    private void ChangeMasterPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MasterPasswordWindow(_store, _connections) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        // The change saved the whole list, including anything an earlier failed save left behind.
        _hasUnsavedChanges = false;
        StatusText.Text = "A mesterjelszó megváltozott.";
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
        // Only a double click on an actual row connects; the header and the empty area below the
        // rows would otherwise launch whatever happened to be selected.
        if (RowDragReorder.FindRowItem(e.OriginalSource as DependencyObject) is { } connection)
            Connect(connection);
    }

    private async void Connect(RdpConnection connection)
    {
        try
        {
            StatusText.Text = $"Csatlakozás: {connection.Name} ({connection.DisplayTarget})...";
            var result = await RdpLauncher.ConnectAsync(connection);

            StatusText.Text = result.PreservedCredentialTargets.Count == 0
                ? $"Az mstsc elindult: {connection.Name}."
                : $"Az mstsc elindult: {connection.Name}. Meglévő Windows-hitelesítő adat érintetlenül maradt: "
                  + string.Join(", ", result.PreservedCredentialTargets);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Sikertelen csatlakozás.";
            MessageBox.Show(this, ex.Message, "Csatlakozási hiba", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
