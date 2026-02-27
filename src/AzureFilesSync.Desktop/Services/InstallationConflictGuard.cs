using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AzureFilesSync.Desktop.Services;

internal static class InstallationConflictGuard
{
    private const string ProductDisplayName = "Storage Zilla";
    private const string MsixPackageName = "StorageZilla.Desktop";

    public static bool TryGetConflictMessage(out string message)
    {
        message = string.Empty;
        var isPackagedProcess = IsCurrentProcessPackaged();
        var msiInstalled = IsMsiInstalled();
        var msixInstalled = IsMsixInstalledForCurrentUser();

        if (isPackagedProcess && msiInstalled)
        {
            message =
                "Both MSIX and MSI installations were detected. " +
                "Uninstall the MSI version from Apps & Features, then start Storage Zilla again.";
            return true;
        }

        if (!isPackagedProcess && msiInstalled && msixInstalled)
        {
            message =
                "Both MSI and MSIX installations were detected. " +
                "Uninstall one installation type (recommended: keep MSIX for packaged installs) before continuing.";
            return true;
        }

        return false;
    }

    private static bool IsMsiInstalled()
    {
        return HasDisplayNameUnderHive(RegistryHive.LocalMachine) || HasDisplayNameUnderHive(RegistryHive.CurrentUser);
    }

    private static bool HasDisplayNameUnderHive(RegistryHive hive)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null)
        {
            return false;
        }

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            using var subKey = uninstall.OpenSubKey(subKeyName);
            var displayName = subKey?.GetValue("DisplayName") as string;
            if (string.Equals(displayName, ProductDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMsixInstalledForCurrentUser()
    {
        try
        {
            var programFilesWindowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");

            if (Directory.Exists(programFilesWindowsApps))
            {
                var packageDirectories = Directory.EnumerateDirectories(
                    programFilesWindowsApps,
                    $"{MsixPackageName}_*",
                    SearchOption.TopDirectoryOnly);
                if (packageDirectories.Any())
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to PowerShell detection.
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"if (Get-AppxPackage -Name '{MsixPackageName}' -ErrorAction SilentlyContinue) {{ 'present' }}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output.Contains("present", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentProcessPackaged()
    {
        var length = 0U;
        var status = GetCurrentPackageFullName(ref length, null);
        if (status == 15700) // APPMODEL_ERROR_NO_PACKAGE
        {
            return false;
        }

        if (status != 122 || length == 0) // ERROR_INSUFFICIENT_BUFFER
        {
            return false;
        }

        var builder = new StringBuilder((int)length);
        status = GetCurrentPackageFullName(ref length, builder);
        return status == 0 && builder.Length > 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
