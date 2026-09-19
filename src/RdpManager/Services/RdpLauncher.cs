using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using RdpManager.Models;

namespace RdpManager.Services;

/// <summary>
/// Launches mstsc.exe for a saved connection in one step: parks the password in Windows
/// Credential Manager under the names mstsc looks up, starts mstsc with a generated .rdp file,
/// then removes the credentials and temp file again once the session window is closed.
/// </summary>
public static class RdpLauncher
{
    private const int EarlyFailureWindowMs = 1000;

    private readonly record struct CredentialRef(string Target, uint Type);

    /// <summary>Credentials this app created and is still responsible for removing.</summary>
    private static readonly ConcurrentDictionary<CredentialRef, byte> OwnedCredentials = new();

    /// <summary>Temp .rdp files belonging to sessions that haven't been closed yet.</summary>
    private static readonly ConcurrentDictionary<string, byte> PendingTempFiles = new();

    public static async Task ConnectAsync(RdpConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Host))
            throw new InvalidOperationException("A kapcsolathoz nincs megadva gépnév vagy IP-cím.");

        var targets = BuildCredentialTargets(connection);
        var credentialUser = QualifyUsername(connection);

        var rdpPath = RdpFileService.WriteTempRdpFile(connection);
        PendingTempFiles[rdpPath] = 0;
        var written = new List<CredentialRef>();
        Process? process = null;

        try
        {
            if (!string.IsNullOrEmpty(connection.Password))
            {
                foreach (var target in targets)
                    written.AddRange(SaveCredential(target, credentialUser, connection.Password));

                if (written.Count == 0)
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
    }

    /// <summary>
    /// Writes the password as a domain password credential - the only type CredSSP will delegate,
    /// and what Windows itself stores for "remember me" RDP logins - plus a generic one, which is
    /// what some parts of the client (and the gateway prompt) read instead.
    /// </summary>
    private static List<CredentialRef> SaveCredential(string target, string username, string password)
    {
        var written = new List<CredentialRef>();

        foreach (var type in new[] { NativeCredentialManager.CredTypeDomainPassword, NativeCredentialManager.CredTypeGeneric })
        {
            if (NativeCredentialManager.TrySave(target, username, password, type))
            {
                var reference = new CredentialRef(target, type);
                OwnedCredentials[reference] = 0;
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
        foreach (var reference in OwnedCredentials.Keys)
            ReleaseCredential(reference);

        foreach (var path in PendingTempFiles.Keys)
            TryDeleteFile(path);
    }

    private static void ReleaseCredentials(IEnumerable<CredentialRef> references)
    {
        foreach (var reference in references)
            ReleaseCredential(reference);
    }

    private static void ReleaseCredential(CredentialRef reference)
    {
        if (OwnedCredentials.TryRemove(reference, out _))
            NativeCredentialManager.Delete(reference.Target, reference.Type);
    }

    private static void TryDeleteFile(string path)
    {
        PendingTempFiles.TryRemove(path, out _);

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup; a leftover temp file in %TEMP% is harmless.
        }
    }
}
