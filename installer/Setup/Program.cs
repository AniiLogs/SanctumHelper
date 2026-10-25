using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SanctumSetup;

/// <summary>Per-user installer for Sanctum Helper. The app's files are embedded as a zip.
///   SanctumHelper-Setup.exe            interactive install / upgrade
///   SanctumHelper-Setup.exe /S         silent install
///   SanctumHelper.exe --uninstall [/S] (the installed app removes itself; see Uninstaller.cs)</summary>
static class Program
{
    public const string AppName = "Sanctum Helper";
    public const string ExeName = "SanctumHelper.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SanctumHelper";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SanctumHelper");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (silent)
            {
                Install(InstalledDir() ?? DefaultDir, desktop: true, autostart: false, _ => { });
                return 0;
            }
            Application.Run(new SetupForm());
            return 0;
        }
        catch (Exception e)
        {
            if (!silent) MessageBox.Show("Setup failed:\n" + e.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    public static string InstalledDir()
    {
        using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return k?.GetValue("InstallLocation") as string;
    }

    /// <summary>Close a running copy so its files can be replaced.</summary>
    static void StopApp()
    {
        foreach (var p in Process.GetProcessesByName("SanctumHelper"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { /* already gone */ }
        }
    }

    public static void Install(string dir, bool desktop, bool autostart, Action<int> progress)
    {
        StopApp();
        Directory.CreateDirectory(dir);
        // Remove the previous version's files (keeps nothing user-made: settings live elsewhere).
        foreach (var old in new[] { "Assets" }.Select(d => Path.Combine(dir, d)).Where(Directory.Exists))
            Directory.Delete(old, true);

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            int i = 0, n = zip.Entries.Count;
            foreach (var e in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
                if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                e.ExtractToFile(target, true);
                progress(++i * 100 / n);
            }
        }

        var exe = Path.Combine(dir, ExeName);
        var oldUninstaller = Path.Combine(dir, "Uninstall.exe"); // left by early builds
        if (File.Exists(oldUninstaller)) File.Delete(oldUninstaller);

        Shortcut(StartMenuLink, exe, dir);
        if (desktop) Shortcut(DesktopLink, exe, dir);
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autostart) run.SetValue("SanctumHelper", $"\"{exe}\" --tray");
            else run.DeleteValue("SanctumHelper", false);
        }
        // Keep the app's own "Start with Windows" checkbox in sync with this choice.
        SetAppSetting("StartWithWindows", autostart);

        using var u = Registry.CurrentUser.CreateSubKey(UninstallKey);
        u.SetValue("DisplayName", AppName);
        u.SetValue("DisplayVersion", Version);
        u.SetValue("Publisher", "Sanctum Helper (unofficial Aniimo fan tool)");
        u.SetValue("DisplayIcon", exe);
        u.SetValue("InstallLocation", dir);
        u.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        u.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall /S");
        u.SetValue("NoModify", 1, RegistryValueKind.DWord);
        u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        u.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
    }

    static void SetAppSetting(string name, bool value)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SanctumHelper", "settings.json");
            if (!File.Exists(file)) return;
            var text = File.ReadAllText(file);
            var re = new System.Text.RegularExpressions.Regex($"\"{name}\"\\s*:\\s*(true|false)");
            if (re.IsMatch(text)) File.WriteAllText(file, re.Replace(text, $"\"{name}\": {(value ? "true" : "false")}"));
        }
        catch { /* the app falls back to its own setting */ }
    }

    static long DirSize(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    static void Shortcut(string link, string target, string workDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic sc = shell.CreateShortcut(link);
        sc.TargetPath = target;
        sc.WorkingDirectory = workDir;
        sc.Description = "Egg Heist companion overlay for Aniimo";
        sc.IconLocation = target + ",0";
        sc.Save();
    }
}

sealed class SetupForm : Form
{
    readonly TextBox path = new() { Width = 360 };
    readonly CheckBox desktop = new() { Text = "Create a desktop shortcut", Checked = true, AutoSize = true };
    readonly CheckBox autostart = new() { Text = "Start with Windows (runs quietly in the tray)", AutoSize = true };
    readonly CheckBox launch = new() { Text = "Launch Sanctum Helper when done", Checked = true, AutoSize = true };
    readonly ProgressBar bar = new() { Width = 460, Height = 14, Visible = false };
    readonly Button install = new() { Text = "Install", AutoSize = true, MinimumSize = new Size(110, 34), Padding = new Padding(10, 2, 10, 2) };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(157, 162, 188) };

    public SetupForm()
    {
        var existing = Program.InstalledDir();
        SuspendLayout();
        // Laid out in 96-DPI pixels and scaled up with Windows display scaling. Without the base
        // DPI WinForms never scaled the layout while the (point-sized) fonts grew, so at 125% and
        // up the text outgrew its boxes and the buttons were cut off.
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = Program.AppName + " Setup";
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 22, 31);
        ForeColor = Color.FromArgb(233, 234, 242);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 380);

        var title = new Label { Text = Program.AppName, Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Color.FromArgb(255, 211, 58), AutoSize = true, Location = new Point(28, 20) };
        var sub = new Label
        {
            Text = $"Version {Program.Version}. Egg Heist companion for Aniimo: the whole Lost Sanctum layout, " +
                   "egg nests, key rooms and your position, right on your map.",
            AutoSize = true, MaximumSize = new Size(460, 0), Location = new Point(30, 66), ForeColor = Color.FromArgb(200, 203, 220),
        };
        var where = new Label { Text = "Install to", AutoSize = true, Location = new Point(30, 124) };
        path.Text = existing ?? Program.DefaultDir;
        path.Location = new Point(30, 146);
        path.BackColor = Color.FromArgb(34, 38, 58); path.ForeColor = ForeColor; path.BorderStyle = BorderStyle.FixedSingle;
        var browse = new Button { Text = "Browse…", Location = new Point(400, 144), AutoSize = true, MinimumSize = new Size(90, 27), FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = path.Text };
            if (d.ShowDialog(this) == DialogResult.OK) path.Text = Path.Combine(d.SelectedPath, "SanctumHelper");
        };
        desktop.Location = new Point(30, 188);
        autostart.Location = new Point(30, 214);
        launch.Location = new Point(30, 240);
        var note = new Label
        {
            Text = "No admin rights needed. Sanctum Helper only looks at the screen; it never touches the game's files or memory.",
            AutoSize = true, MaximumSize = new Size(460, 0), Location = new Point(30, 272), ForeColor = Color.FromArgb(157, 162, 188),
        };
        bar.Location = new Point(30, 316);
        status.Location = new Point(30, 340);
        install.Location = new Point(380, 332);
        install.FlatStyle = FlatStyle.Flat;
        install.BackColor = Color.FromArgb(255, 211, 58); install.ForeColor = Color.FromArgb(20, 22, 31);
        install.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        if (existing != null) install.Text = "Update";
        install.Click += async (_, _) => await DoInstall();
        AcceptButton = install;
        Controls.AddRange(new Control[] { title, sub, where, path, browse, desktop, autostart, launch, note, bar, status, install });
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // After scaling: keep the buttons inside the window and the text above them, whatever
        // the display scaling or font fallback did to their sizes.
        int pad = (int)(30 * DeviceDpi / 96.0);
        install.Left = ClientSize.Width - pad - install.Width;
        install.Top = ClientSize.Height - pad / 2 - install.Height;
        foreach (Control c in Controls)
            if (c is Button b && b != install) b.Left = Math.Min(b.Left, ClientSize.Width - pad - b.Width);
        path.Width = Controls.OfType<Button>().First(b => b != install).Left - path.Left - pad / 3;
    }

    async Task DoInstall()
    {
        install.Enabled = false;
        bar.Visible = true;
        status.Text = "Installing…";
        var dir = path.Text.Trim();
        bool d = desktop.Checked, a = autostart.Checked;
        try
        {
            await Task.Run(() => Program.Install(dir, d, a, p => BeginInvoke(new Action(() => bar.Value = Math.Min(100, p)))));
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Install failed:\n" + e.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            install.Enabled = true;
            status.Text = "";
            return;
        }
        if (launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(dir, Program.ExeName)) { WorkingDirectory = dir, UseShellExecute = true });
        status.Text = "Done.";
        MessageBox.Show(this,
            $"{Program.AppName} is installed.\n\nIt lives in the system tray (bottom-right, near the clock). " +
            "In the Lost Sanctum, open the map and hold Left Ctrl. You can change the key in Settings.",
            Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
