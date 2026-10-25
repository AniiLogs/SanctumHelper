using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace SanctumHelper;

/// <summary>"SanctumHelper.exe --uninstall [/S]", registered by the installer as the
/// Installed-apps uninstall command. Mirrors what installer/Setup/Program.cs creates.</summary>
public static class Uninstaller
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SanctumHelper";

    public static void Run(string[] args)
    {
        bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        if (!silent && MessageBox.Show("Remove Sanctum Helper from this PC?\n\nYour settings in %LocalAppData%\\SanctumHelper are kept.",
                "Sanctum Helper", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("SanctumHelper").Where(p => p.Id != me))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { /* already gone */ }
        }
        const string name = "Sanctum Helper.lnk";
        foreach (var link in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name),
                 })
            if (File.Exists(link)) File.Delete(link);
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            run?.DeleteValue("SanctumHelper", false);

        string? dir;
        using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) dir = k?.GetValue("InstallLocation") as string;
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        dir ??= AppContext.BaseDirectory.TrimEnd('\\');
        // Only ever delete a folder that really is our install (it holds our exe and data).
        if (File.Exists(Path.Combine(dir, "SanctumHelper.exe")) && File.Exists(Path.Combine(dir, "Assets", "layouts.json")))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        if (!silent) MessageBox.Show("Sanctum Helper was removed.", "Sanctum Helper");
    }
}
