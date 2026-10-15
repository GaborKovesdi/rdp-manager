using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Views;

/// <summary>
/// Startup gate: creates the encrypted store on first run, or unlocks it on later runs. Opened
/// from the main window, it changes the master password of the unlocked store instead.
/// The master password itself is never stored anywhere - it only ever derives the store key.
/// </summary>
public partial class MasterPasswordWindow : Window
{
    private const int MinimumLength = 8;

    private readonly ConnectionStore _store;
    private readonly bool _createMode;
    private readonly IEnumerable<RdpConnection>? _connectionsToRekey;

    public List<RdpConnection> Connections { get; private set; } = new();

    public MasterPasswordWindow(ConnectionStore store)
    {
        InitializeComponent();
        _store = store;
        _createMode = !store.StoreExists;

        if (_createMode)
        {
            HeadingText.Text = "Adj meg egy mesterjelszót";
            DescriptionText.Text = "Ezzel a jelszóval lesz titkosítva minden mentett kapcsolat és jelszó. Ha elfelejted, az adatok nem állíthatók helyre.";
            OkButton.Content = "Létrehozás";
            ConfirmPanel.Visibility = Visibility.Visible;
        }
        else
        {
            HeadingText.Text = "Mesterjelszó";
            DescriptionText.Text = "Add meg a mesterjelszavad a mentett kapcsolatok feloldásához.";
            OkButton.Content = "Feloldás";
            ConfirmPanel.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => (_connectionsToRekey is null ? MasterBox : CurrentBox).Focus();

        // Caps Lock is the commonest reason a correct password "doesn't work", so say when it's on.
        Activated += (_, _) => UpdateCapsLockHint();
        PreviewKeyUp += (_, _) => UpdateCapsLockHint();
    }

    private void UpdateCapsLockHint() =>
        CapsLockHint.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Change mode: re-encrypts the unlocked store, saving <paramref name="connections"/> under the new password.</summary>
    public MasterPasswordWindow(ConnectionStore store, IEnumerable<RdpConnection> connections) : this(store)
    {
        _connectionsToRekey = connections;

        HeadingText.Text = "Mesterjelszó cseréje";
        DescriptionText.Text = "A teljes tároló az új jelszóval lesz újratitkosítva; a régi jelszó ezután már nem nyitja meg.";
        CurrentPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Visible;
        MasterLabel.Text = "Új mesterjelszó";
        OkButton.Content = "Csere";
        CancelButton.Content = "Mégse";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>Import mode: unlocks another store file, whose connections end up in <see cref="Connections"/>.</summary>
    public MasterPasswordWindow(ConnectionStore otherStore, string fileName) : this(otherStore)
    {
        HeadingText.Text = "Importálás másik tárolóból";
        DescriptionText.Text = $"Add meg a(z) „{fileName}” tároló mesterjelszavát. A kapcsolatai a mostani tárolóba kerülnek, a fájl nem változik.";
        OkButton.Content = "Importálás";
        CancelButton.Content = "Mégse";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var masterPassword = MasterBox.Password;

        if (_connectionsToRekey is not null)
            ChangePassword(masterPassword, _connectionsToRekey);
        else if (_createMode)
            CreateStore(masterPassword);
        else
            UnlockStore(masterPassword);
    }

    /// <summary>The rules a new master password has to meet, for a new store and a change alike.</summary>
    private bool IsAcceptableNewPassword(string masterPassword)
    {
        if (masterPassword.Length < MinimumLength)
        {
            ErrorText.Text = $"A mesterjelszó legyen legalább {MinimumLength} karakter hosszú.";
            return false;
        }

        if (masterPassword != ConfirmBox.Password)
        {
            ErrorText.Text = "A két jelszó nem egyezik.";
            return false;
        }

        return true;
    }

    private void ChangePassword(string newPassword, IEnumerable<RdpConnection> connections)
    {
        if (!IsAcceptableNewPassword(newPassword))
            return;

        bool changed;
        try
        {
            changed = _store.ChangeMasterPassword(CurrentBox.Password, newPassword, connections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Nem sikerült menteni, a régi mesterjelszó maradt érvényben: {ex.Message}";
            return;
        }

        if (!changed)
        {
            ErrorText.Text = "A jelenlegi mesterjelszó hibás.";
            CurrentBox.Clear();
            CurrentBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void CreateStore(string masterPassword)
    {
        if (!IsAcceptableNewPassword(masterPassword))
            return;

        try
        {
            _store.Initialize(masterPassword);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Nem sikerült létrehozni a tárolót: {ex.Message}";
            return;
        }

        DialogResult = true;
    }

    private void UnlockStore(string masterPassword)
    {
        switch (_store.TryUnlock(masterPassword, out var connections))
        {
            case UnlockResult.Success:
                Connections = connections;
                DialogResult = true;
                break;

            case UnlockResult.WrongPassword:
                ErrorText.Text = "Hibás mesterjelszó, vagy a tárolófájlt módosították.";
                MasterBox.Clear();
                MasterBox.Focus();
                break;

            case UnlockResult.Corrupted:
                ErrorText.Text = "A tárolófájl sérült, nem olvasható, vagy nem RDP Manager tároló.";
                break;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
