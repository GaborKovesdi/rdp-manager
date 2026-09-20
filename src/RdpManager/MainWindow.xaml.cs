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

        if (SaveAndRefresh())
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
        OnConnectionMoved(connection);
    }

    private void OnConnectionMoved(RdpConnection connection)
    {
        SaveAndRefresh();

        // Refresh() can drop the selection, and keeping it lets the user keep moving the same row.
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
