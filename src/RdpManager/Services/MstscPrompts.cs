using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RdpManager.Services;

/// <summary>
/// Answers the dialogs Windows puts up for a connection this app launched, so that a saved
/// connection really connects in one step. Dialogs of other connections or other programs are
/// never touched.
/// </summary>
internal static class MstscPrompts
{
    // Control IDs from mstsc's security warning resource, the same in every language. Only the
    // security warning has the resource list, so no other mstsc dialog (certificate warning,
    // errors) is ever touched.
    private const int ResourceListId = 16331;
    private const int ConnectButtonId = 1;

    private const string CredentialDialogClass = "Credential Dialog Xaml Host";
    private const string CredentialOkButtonId = "OkButton";

    private const int BM_GETCHECK = 0x00F0;
    private const int BM_CLICK = 0x00F5;
    private const int BST_CHECKED = 1;
    private const uint GW_OWNER = 4;

    private const int PollIntervalMs = 250;
    private const int SecurityWarningGiveUpAfterMs = 30_000;
    // The server asks for the password only after the network logon, which can take a while,
    // especially through an RD Gateway.
    private const int PasswordPromptGiveUpAfterMs = 120_000;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Answers the security warning mstsc shows for every unsigned .rdp file since the 2026
    /// Windows update. It lists the redirections the file asks for (clipboard, drives,
    /// printers...) all unticked, has no "don't ask again", and ignores the old LocalDevices
    /// pre-approval in the registry. The file is the one this app generated from the user's own
    /// settings, so the dialog only ever asks about what the user chose: this ticks those boxes
    /// and presses Connect. Gives up quietly when none appears (a Windows without the new dialog).
    /// </summary>
    public static async Task AcceptSecurityWarningAsync(int processId)
    {
        var deadline = Environment.TickCount64 + SecurityWarningGiveUpAfterMs;
        while (Environment.TickCount64 < deadline)
        {
            if (FindSecurityWarning(processId) is { } dialog)
            {
                Accept(dialog);
                return;
            }

            await Task.Delay(PollIntervalMs);
        }
    }

    /// <summary>
    /// Answers the "Windows Security" password prompt, which comes up even with a saved password
    /// when the server is set to always ask for it. CredentialUIBroker draws that dialog one
    /// integrity level above ordinary apps, so Windows lets only an elevated process read or fill
    /// it: elevated, this fills in the password and presses OK; otherwise copyPassword puts it on
    /// the clipboard to paste. Only the first prompt is answered, so a saved password that is
    /// wrong costs at most one extra failed logon rather than a locked account.
    /// </summary>
    public static async Task AnswerPasswordPromptAsync(
        int processId, string host, string username, string password, Action copyPassword)
    {
        var elevated = IsElevated();
        var deadline = Environment.TickCount64 + PasswordPromptGiveUpAfterMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                foreach (AutomationElement dialog in AutomationElement.RootElement.FindAll(TreeScope.Children,
                             new PropertyCondition(AutomationElement.ClassNameProperty, CredentialDialogClass)))
                {
                    if (elevated && AsksFor(dialog, host, username))
                    {
                        if (!Fill(dialog, password))
                            copyPassword();
                        return;
                    }

                    if (!elevated && IsOwnedBy(dialog, processId))
                    {
                        copyPassword();
                        return;
                    }
                }
            }
            catch (Exception e) when (e is ElementNotAvailableException or COMException)
            {
                // A dialog closed while it was being looked at; look again on the next round.
            }

            await Task.Delay(PollIntervalMs);
        }
    }

    private static IntPtr? FindSecurityWarning(int processId)
    {
        IntPtr? found = null;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window) && GetDlgItem(window, ResourceListId) != IntPtr.Zero)
                found = window;

            return found is null;
        }, IntPtr.Zero);

        return found;
    }

    private static void Accept(IntPtr dialog)
    {
        var boxes = new List<IntPtr>();
        EnumChildWindows(GetDlgItem(dialog, ResourceListId), (box, _) =>
        {
            if (IsWindowVisible(box))
                boxes.Add(box);
            return true;
        }, IntPtr.Zero);

        foreach (var box in boxes)
        {
            if (SendMessage(box, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) != BST_CHECKED)
                SendMessage(box, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        // A click can fail on an inactive dialog; then leave it to the user rather than connect
        // with only part of what they asked for.
        foreach (var box in boxes)
        {
            if (SendMessage(box, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) != BST_CHECKED)
                return;
        }

        SendMessage(GetDlgItem(dialog, ConnectButtonId), BM_CLICK, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Not elevated, the dialog can't be read, but its owner is the mstsc window that asked for it.</summary>
    private static bool IsOwnedBy(AutomationElement dialog, int processId)
    {
        GetWindowThreadProcessId(GetWindow(new IntPtr(dialog.Current.NativeWindowHandle), GW_OWNER), out var owner);
        return owner == processId;
    }

    /// <summary>
    /// Elevated, mstsc runs elevated too and the dialog loses its owner, but it can be read: it is
    /// this connection's when it names this host and shows exactly this user. Both are data, not
    /// interface text, so this works in any Windows language.
    /// </summary>
    private static bool AsksFor(AutomationElement dialog, string host, string username)
    {
        var texts = dialog.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(text => text.Current.Name)
            .ToList();

        return texts.Any(text => text.Contains(host, StringComparison.OrdinalIgnoreCase))
               && texts.Any(text => string.Equals(text, username, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Fill(AutomationElement dialog, string password)
    {
        var box = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsPasswordProperty, true));
        var ok = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, CredentialOkButtonId));
        if (box is null || ok is null)
            return false;

        try
        {
            ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(password);
            ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return true;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }
}
