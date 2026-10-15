using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _passwordClipboardTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DataObject? _copiedPassword;
    private bool _hasUnsavedChanges;

    public MainWindow(ConnectionStore store, IEnumerable<RdpConnection> connections)
    {
        InitializeComponent();
        _store = store;
        ConnectionsGrid.ItemsSource = _connections;

        // A quiet selection - the accent colour at low strength under normal text - instead of the
        // theme's solid accent block, which would compete with the Connect button. Set from code
        // because a brush colour in XAML can't follow a theme resource.
        // shortcut: read once at startup, so switching light/dark mode while the app runs keeps the
        // old selection colours until a restart; listen to SystemEvents.UserPreferenceChanged if that matters.
        var selectedBackground = new SolidColorBrush(SystemColors.AccentColor) { Opacity = 0.3 };
        ConnectionsGrid.Resources["DataGridRowSelectedBackgroundThemeBrush"] = selectedBackground;
        // The theme frames the focused cell; drawn in the selection's own colour the frame blends
        // into the row, so only the row shows as selected.
        ConnectionsGrid.Resources[DataGrid.FocusBorderBrushKey] = selectedBackground;
        ConnectionsGrid.Resources["DataGridRowSelectedForegroundThemeBrush"] = FindResource("TextFillColorPrimaryBrush");
        _rowDragReorder = new RowDragReorder(ConnectionsGrid, _connections, DropIndicator, OnConnectionMoved);

        foreach (var connection in connections)
            _connections.Add(connection);

        _connections.CollectionChanged += (_, _) => UpdateState();
        ConnectionsGrid.SelectionChanged += (_, _) => UpdateState();
        UpdateState();
        _passwordClipboardTimer.Tick += (_, _) => ClearCopiedPassword();

        // Ready for the keyboard straight away: the first connection is selected, so Enter connects.
        Loaded += (_, _) => SelectAndFocus(_connections.FirstOrDefault());
    }

    /// <summary>Enables the row actions only when they can act, and invites a first import into an empty list.</summary>
    private void UpdateState()
    {
        var index = SelectedConnection is { } selected ? _connections.IndexOf(selected) : -1;
        ConnectButton.IsEnabled = EditButton.IsEnabled = DeleteButton.IsEnabled = index >= 0;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _connections.Count - 1;
        EmptyHint.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Selects the row and puts keyboard focus on it, so arrows, Enter and typing a name work from there.</summary>
    private void SelectAndFocus(RdpConnection? connection)
    {
        if (connection is null)
            return;

        ConnectionsGrid.SelectedItem = connection;
        ConnectionsGrid.ScrollIntoView(connection);
        ConnectionsGrid.UpdateLayout();
        if (ConnectionsGrid.ItemContainerGenerator.ContainerFromItem(connection) is DataGridRow row)
            row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (e.Key == Key.N)
            AddButton_Click(sender, e);
        else if (e.Key == Key.O)
            ImportButton_Click(sender, e);
        else
            return;

        e.Handled = true;
    }

    private void ConnectionsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var alt = Keyboard.Modifiers == ModifierKeys.Alt;
        switch (e.Key == Key.System ? e.SystemKey : e.Key)
        {
            // Without this the grid would move on to the next row.
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                ConnectButton_Click(sender, e);
                break;
            case Key.F2:
                EditButton_Click(sender, e);
                break;
            case Key.Delete:
                DeleteButton_Click(sender, e);
                break;
            case Key.Up when alt:
                MoveSelected(-1);
                break;
            case Key.Down when alt:
                MoveSelected(1);
                break;
            // Instead of the grid's own copy of the row's text.
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                CopyPassword(SelectedConnection);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // A right click selects the row under it first, so the menu acts on what was clicked; on empty
    // space it clears the selection, and the menu stays closed.
    private void ConnectionsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        ConnectionsGrid.SelectedItem = RowDragReorder.FindRowItem(e.OriginalSource as DependencyObject);

    private void ConnectionsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        e.Handled = SelectedConnection is null;

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
        // Manager, temp .rdp files, a copied password - would otherwise outlive the app, so drop
        // it before going away.
        ClearCopiedPassword();
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

        if (dialog.ShowDialog(this) == true)
            ImportFiles(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ImportableFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var files = ImportableFiles(e);

        // Deferred so the drop returns at once: the import can open dialogs, and Explorer, the
        // source of the drag, stays frozen until the drop handler has returned.
        if (files.Length > 0)
            Dispatcher.BeginInvoke(() => ImportFiles(files));
    }

    /// <summary>The .rdp and store files in a drag; anything else dragged along with them is ignored.</summary>
    private static string[] ImportableFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => IsStoreFile(f) || Path.GetExtension(f).Equals(".rdp", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    private static bool IsStoreFile(string path) => Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name itself if no connection uses it yet, otherwise the name with the first free
    /// number after it: "srv", then "srv (2)", "srv (3)". Names differing only in case count as taken.
    /// </summary>
    internal static string UniqueName(string name, IEnumerable<RdpConnection> existing)
    {
        var taken = existing.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var number = 2; taken.Contains(candidate); number++)
            candidate = $"{name} ({number})";

        return candidate;
    }

    private void ImportFiles(IEnumerable<string> files)
    {
        var added = 0;
        var skipped = 0;
        RdpConnection? lastRdpImport = null;

        foreach (var file in files)
        {
            var isStore = IsStoreFile(file);
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

                connection.Name = UniqueName(connection.Name, _connections);
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

        SelectAndFocus(_connections[^1]);

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
            SelectAndFocus(connection);
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
        {
            SaveAndRefresh();
            SelectAndFocus(connection);
        }
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
            // The row that moves into the deleted one's place takes over the selection.
            var index = _connections.IndexOf(connection);
            _connections.Remove(connection);
            SaveAndRefresh();
            SelectAndFocus(_connections.ElementAtOrDefault(Math.Min(index, _connections.Count - 1)));
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
        SelectAndFocus(connection);
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
        // rows would otherwise launch whatever happened to be selected. On the key it only copies.
        var source = e.OriginalSource as DependencyObject;
        if (RowDragReorder.FindAncestor<Button>(source) is null && RowDragReorder.FindRowItem(source) is { } connection)
            Connect(connection);
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e) =>
        CopyPassword(((FrameworkElement)sender).DataContext as RdpConnection);

    /// <summary>Puts the connection's password on the clipboard for a minute.</summary>
    private void CopyPassword(RdpConnection? connection)
    {
        if (connection is null)
        {
            StatusText.Text = "Válassz ki egy kapcsolatot a jelszó másolásához.";
            return;
        }

        if (!connection.HasPassword)
        {
            StatusText.Text = $"A(z) \"{connection.Name}\" kapcsolathoz nincs mentett jelszó.";
            return;
        }

        var data = new DataObject(DataFormats.UnicodeText, connection.Password);
        // A zero DWORD in these keeps the password out of the Win+V history and the cloud clipboard,
        // where it would outlive the minute.
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));

        try
        {
            // Not flushed to the system, so Clipboard.IsCurrent can tell later whether it is still ours.
            Clipboard.SetDataObject(data, false);
        }
        catch (ExternalException)
        {
            StatusText.Text = "Nem sikerült a jelszót a vágólapra másolni: egy másik program éppen használja a vágólapot.";
            return;
        }

        _copiedPassword = data;
        _passwordClipboardTimer.Stop();
        _passwordClipboardTimer.Start();
        StatusText.Text = $"A(z) \"{connection.Name}\" jelszava a vágólapon van, egy perc múlva törlődik.";
    }

    /// <summary>Takes the copied password off the clipboard, unless something copied since has already replaced it.</summary>
    private void ClearCopiedPassword()
    {
        if (_copiedPassword is null)
            return;

        try
        {
            if (Clipboard.IsCurrent(_copiedPassword))
            {
                Clipboard.Clear();
                StatusText.Text = "A jelszó lekerült a vágólapról.";
            }
        }
        catch (ExternalException)
        {
            // Another program has the clipboard open; the timer keeps running and tries again.
            return;
        }

        _passwordClipboardTimer.Stop();
        _copiedPassword = null;
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
