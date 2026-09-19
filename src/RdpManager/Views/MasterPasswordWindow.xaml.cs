using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Views;

/// <summary>
/// Startup gate: creates the encrypted store on first run, or unlocks it on later runs.
/// The master password itself is never stored anywhere - it only ever derives the store key.
/// </summary>
public partial class MasterPasswordWindow : Window
{
    private const int MinimumLength = 8;

    private readonly ConnectionStore _store;
    private readonly bool _createMode;

    public List<RdpConnection> Connections { get; private set; } = new();

    public MasterPasswordWindow(ConnectionStore store)
    {
        InitializeComponent();
        _store = store;
        _createMode = !store.StoreExists;

        if (_createMode)
        {
            HeadingText.Text = "Adj meg egy mesterjelszót";
            DescriptionText.Text = store.LegacyStoreExists
                ? "Ezzel a jelszóval lesz titkosítva minden mentett kapcsolat és jelszó. A korábban mentett kapcsolataid automatikusan átkerülnek az új tárolóba. Ha elfelejted a jelszót, az adatok nem állíthatók helyre."
                : "Ezzel a jelszóval lesz titkosítva minden mentett kapcsolat és jelszó. Ha elfelejted, az adatok nem állíthatók helyre.";
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

        Loaded += (_, _) => MasterBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var masterPassword = MasterBox.Password;

        if (_createMode)
            CreateStore(masterPassword);
        else
            UnlockStore(masterPassword);
    }

    private void CreateStore(string masterPassword)
    {
        if (masterPassword.Length < MinimumLength)
        {
            ErrorText.Text = $"A mesterjelszó legyen legalább {MinimumLength} karakter hosszú.";
            return;
        }

        if (masterPassword != ConfirmBox.Password)
        {
            ErrorText.Text = "A két jelszó nem egyezik.";
            return;
        }

        try
        {
            Connections = _store.Initialize(masterPassword);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Nem sikerült létrehozni a tárolót: {ex.Message}";
        }
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
                ErrorText.Text = "A tárolófájl sérült vagy nem olvasható (data/connections.dat).";
                break;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
