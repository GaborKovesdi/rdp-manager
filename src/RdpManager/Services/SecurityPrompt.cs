using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RdpManager.Services;

/// <summary>
/// Answers the security warning mstsc shows for every unsigned .rdp file since the 2026 Windows
/// update. It lists the redirections the file asks for (clipboard, drives, printers...) all
/// unticked, has no "don't ask again", and ignores the old LocalDevices pre-approval in the
/// registry. The file is the one this app generated from the user's own settings, so the dialog
/// only ever asks about what the user chose: this ticks those boxes and presses Connect.
/// </summary>
internal static class SecurityPrompt
{
    // Control IDs from mstsc's dialog resource, the same in every language. Only the security
    // warning has the resource list, so no other mstsc dialog (certificate warning, errors) is
    // ever touched.
    private const int ResourceListId = 16331;
    private const int ConnectButtonId = 1;

    private const int BM_GETCHECK = 0x00F0;
    private const int BM_CLICK = 0x00F5;
    private const int BST_CHECKED = 1;

    private const int PollIntervalMs = 250;
    private const int GiveUpAfterMs = 30_000;

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
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Waits for the warning of the given mstsc process and accepts it. Gives up quietly when none
    /// appears (a Windows without the new dialog), leaving everything to the user as before.
    /// </summary>
    public static async Task AcceptAsync(int processId)
    {
        var deadline = Environment.TickCount64 + GiveUpAfterMs;
        while (Environment.TickCount64 < deadline)
        {
            if (FindDialog(processId) is { } dialog)
            {
                Accept(dialog);
                return;
            }

            await Task.Delay(PollIntervalMs);
        }
    }

    private static IntPtr? FindDialog(int processId)
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
}
