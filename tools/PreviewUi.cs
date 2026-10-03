using System;
using System.IO;
using System.Windows.Forms;
using SafePaste.Detecting;
using SafePaste.Interop;
using SafePaste.Storage;
using SafePaste.Ui;

internal static class PreviewUi
{
    [STAThread]
    private static void Main()
    {
        Native.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string sandbox = Path.Combine(Path.GetTempPath(), "SafePaste-preview-" + Guid.NewGuid().ToString("N"));
        Paths.DataDirectory = sandbox;
        try
        {
            SafePasteDatabase database = new SafePasteDatabase();
            database.AddLearned("SRV-DB01", "HOST");
            database.Reserve("IP", "10.24.8.16", 7);
            database.AddAllowed("example.org");
            database.Save();
            string source = "# Incident report / production\r\n\r\nhostname: SRV-DB01\r\n"
                + "endpoint: https://api.example.com/v1/status\r\naddress: 10.24.8.16\r\n"
                + "user: operator@example.com\r\npassword: DemoSecret123!\r\n\r\n"
                + "Connection to SRV-DB01 failed.\r\nRetry from SW-CORE-01 in 30 seconds.\r\n";
            SafePasteSettings settings = new SafePasteSettings();
            SettingsActions actions = SettingsActions.Offline();
            actions.ShowRules = delegate { new DatabaseForm().Show(); };
            actions.DescribeBridge = delegate { return "Работает, агентов: 1"; };
            using (ReviewForm form = new ReviewForm(source, Detector.Scan(source, database, false), database,
                settings, "Рабочий отчёт.txt - Блокнот", true))
            {
                form.ShowSettings = delegate(Form owner) { new SettingsForm(actions).Show(owner); };
                ToolStripMenuItem status;
                using (ContextMenuStrip menu = TrayMenu.Create(delegate { form.Activate(); }, delegate { }, delegate { },
                    delegate { new SettingsForm(actions).Show(form); }, delegate { new AboutForm().Show(); }, form.Close,
                    delegate { return settings.Mode; }, delegate(ControlMode mode) { settings.Mode = mode; }, out status))
                using (NotifyIcon tray = new NotifyIcon())
                {
                    tray.Icon = AppIcon.Value;
                    tray.Text = "SafePaste: демонстрация интерфейса";
                    tray.ContextMenuStrip = menu;
                    tray.Visible = true;
                    status.Text = "Демонстрация на вымышленных данных";
                    // F8 displays the exact tray menu without touching the real clipboard.
                    form.KeyPreview = true;
                    form.KeyDown += delegate(object sender, KeyEventArgs e)
                    {
                        if (e.KeyCode == Keys.F8) menu.Show(form, new System.Drawing.Point(form.ClientSize.Width - menu.Width - 24, 90));
                    };
                    Application.Run(form);
                    tray.Visible = false;
                }
            }
        }
        finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); }
    }
}
