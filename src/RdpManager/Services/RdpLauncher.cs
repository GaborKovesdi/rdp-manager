using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RdpManager.Models;

namespace RdpManager.Services;

/// <summary>What a connection attempt did beyond starting mstsc, so the UI can say so.</summary>
public sealed record ConnectResult(IReadOnlyList<string> PreservedCredentialTargets);

/// <summary>
/// Launches mstsc.exe for a saved connection in one step: parks the password in Windows
/// Credential Manager under the names mstsc looks up, starts mstsc with a generated .rdp file,
/// then removes the credentials and temp file again once the session window is closed.
/// </summary>
public static class RdpLauncher
{
    private const int EarlyFailureWindowMs = 1000;

    private readonly record struct CredentialRef(string Target, uint Type);

    private static readonly object Sync = new();

    /// <summary>
    /// Credentials this app wrote, counted per open session. Two sessions can need the same entry
    /// - the same host twice, or two hosts behind one RD Gateway - so the entry is only removed
    /// once the last of them is gone.
    /// </summary>
    private static readonly Dictionary<CredentialRef, int> OwnedCredentials = new();

    /// <summary>Temp .rdp files that still need deleting; entries stay until a delete succeeds.</summary>
    private static readonly HashSet<string> PendingTempFiles = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ConnectResult> ConnectAsync(RdpConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Host))
            throw new InvalidOperationException("A kapcsolathoz nincs megadva gépnév vagy IP-cím.");

        var targets = BuildCredentialTargets(connection);
        var credentialUser = QualifyUsername(connection);

        var rdpPath = RdpFileService.WriteTempRdpFile(connection);
        lock (Sync)
            PendingTempFiles.Add(rdpPath);

        var written = new List<CredentialRef>();
        var preserved = new List<string>();
        Process? process = null;

        try
        {
            if (!string.IsNullOrEmpty(connection.Password))
            {
                lock (Sync)
                {
                    foreach (var target in targets)
                        written.AddRange(SaveCredential(target, credentialUser, connection.Password, preserved));
                }

                if (written.Count == 0 && preserved.Count == 0)
                    throw new InvalidOperationException(
                        "Nem sikerült eltárolni a hitelesítő adatot a Windows Credential Managerben.");
            }

            process = Process.Start(new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = $"\"{rdpPath}\"",
                UseShellExecute = false,
            });

            if (process is null)
                throw new InvalidOperationException("Nem sikerült elindítani az mstsc.exe-t.");

            // mstsc stays open for the whole session, so an immediate exit means it refused to
            // start at all - report that instead of claiming success.
            var exitedImmediately = await Task.Run(() => process.WaitForExit(EarlyFailureWindowMs));
            if (exitedImmediately && process.ExitCode != 0)
                throw new InvalidOperationException($"Az mstsc.exe hibával kilépett (kód: {process.ExitCode}).");
        }
        catch
        {
            process?.Dispose();
            ReleaseCredentials(written);
            TryDeleteFile(rdpPath);
            throw;
        }

        // Clean up only after the RDP window is closed, so the login isn't wiped out before
        // mstsc has had a chance to use it.
        var started = process;
        _ = Task.Run(async () =>
        {
            try
            {
                await started.WaitForExitAsync();
            }
            finally
            {
                started.Dispose();
                ReleaseCredentials(written);
                TryDeleteFile(rdpPath);
            }
        });

        return new ConnectResult(preserved);
    }

    /// <summary>
    /// Writes the password as a domain password credential - the only type CredSSP will delegate,
    /// and what Windows itself stores for "remember me" RDP logins - plus a generic one, which is
    /// what some parts of the client (and the gateway prompt) read instead.
    ///
    /// A name that already holds a credential this app did not write is left completely alone.
    /// Windows keeps one credential per name and a domain password blob can never be read back, so
    /// overwriting an entry the user saved themselves would destroy it for good. The password also
    /// travels inside the generated .rdp file, so skipping the write does not break the login.
    /// </summary>
    private static List<CredentialRef> SaveCredential(string target, string username, string password, List<string> preserved)
    {
        var written = new List<CredentialRef>();

        foreach (var type in new[] { NativeCredentialManager.CredTypeDomainPassword, NativeCredentialManager.CredTypeGeneric })
        {
            var reference = new CredentialRef(target, type);
            var alreadyOurs = OwnedCredentials.ContainsKey(reference);

            if (!alreadyOurs && NativeCredentialManager.Exists(target, type))
            {
                if (!preserved.Contains(target))
                    preserved.Add(target);
                continue;
            }

            if (alreadyOurs || NativeCredentialManager.TrySave(target, username, password, type))
            {
                OwnedCredentials[reference] = OwnedCredentials.GetValueOrDefault(reference) + 1;
                written.Add(reference);
            }
        }

        return written;
    }

    /// <summary>
    /// A domain password credential is rejected unless the user name is qualified, so an entry
    /// saved without a domain is scoped to the remote machine the way Windows itself does it.
    /// </summary>
    private static string QualifyUsername(RdpConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Domain))
            return $"{connection.Domain}\\{connection.Username}";

        if (connection.Username.Contains('\\') || connection.Username.Contains('@'))
            return connection.Username;

        return $"{connection.Host}\\{connection.Username}";
    }

    /// <summary>
    /// The credential names mstsc will look up: the remote computer exactly as it appears in
    /// "full address", plus the RD Gateway, which authenticates separately under its own name.
    /// </summary>
    private static List<string> BuildCredentialTargets(RdpConnection connection)
    {
        var targets = new List<string> { $"TERMSRV/{RdpFileService.GetAddress(connection)}" };

        if (RdpFileService.GetGatewayHost(connection) is { } gateway)
            targets.Add($"TERMSRV/{gateway}");

        return targets;
    }

    /// <summary>
    /// Drops every credential and temp file still pending; called when the app exits, since a
    /// session left open would otherwise outlive the app that is responsible for cleaning it up.
    /// </summary>
    public static void ReleaseAll()
    {
        List<CredentialRef> credentials;
        List<string> files;

        lock (Sync)
        {
            credentials = OwnedCredentials.Keys.ToList();
            files = PendingTempFiles.ToList();
            OwnedCredentials.Clear();
        }

        foreach (var reference in credentials)
            NativeCredentialManager.Delete(reference.Target, reference.Type);

        foreach (var path in files)
            TryDeleteFile(path);
    }

    private static void ReleaseCredentials(IEnumerable<CredentialRef> references)
    {
        var releasable = new List<CredentialRef>();

        lock (Sync)
        {
            foreach (var reference in references)
            {
                if (!OwnedCredentials.TryGetValue(reference, out var count))
                    continue;

                if (count <= 1)
                {
                    OwnedCredentials.Remove(reference);
                    releasable.Add(reference);
                }
                else
                {
                    OwnedCredentials[reference] = count - 1;
                }
            }
        }

        foreach (var reference in releasable)
            NativeCredentialManager.Delete(reference.Target, reference.Type);
    }

    /// <summary>
    /// Deletes a generated .rdp file. It holds the password (DPAPI encrypted), so a file that
    /// cannot be deleted right now stays on the list and is tried again when the app exits.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (Sync)
            PendingTempFiles.Remove(path);
    }
}
