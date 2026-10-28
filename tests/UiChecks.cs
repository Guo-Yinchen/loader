using System.Drawing;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Chispa.Launcher;

internal static class UiChecks
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    internal static void ChangeDpi(LauncherForm form, int dpi)
    {
        var ratio = dpi / (float)form.DeviceDpi;
        var fonts = Descendants(form).Select(control => (Control: control, Font: control.Font,
            Points: control.Font.SizeInPoints)).ToArray();
        var rectangle = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new[] { form.Left, form.Top, form.Left + (int)Math.Round(form.Width * ratio),
                form.Top + (int)Math.Round(form.Height * ratio) }, 0, rectangle, 4);
            SendMessage(form.Handle, 0x02E0, (IntPtr)((dpi << 16) | dpi), rectangle);
            Application.DoEvents();
            Require(form.DeviceDpi == dpi, "DPI change was not applied");
            // Synthetic WM_DPICHANGED does not change the monitor's GDI DC DPI. Inflate
            // explicitly assigned fonts as well, so the clipping check still exercises the
            // larger glyph metrics rather than testing only larger control rectangles.
            foreach (var item in fonts)
            {
                var points = item.Points * ratio;
                if (Math.Abs(item.Control.Font.SizeInPoints - points) > .02f)
                    item.Control.Font = new Font(item.Font.FontFamily, points, item.Font.Style, GraphicsUnit.Point);
            }
            form.PerformLayout();
            Application.DoEvents();
        }
        finally { Marshal.FreeHGlobal(rectangle); }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls) foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Flags)!.GetValue(instance)!;
    private static void Invoke(object instance, string name) => instance.GetType().GetMethod(name, Flags)!.Invoke(instance, null);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void CheckChrome(LauncherForm form)
    {
        foreach (var name in new[] { "windowMinimize", "windowClose" })
        {
            var button = form.Controls.Find(name, true).Single();
            Require(button.Visible && button.Width > 0 && button.Height > 0, name + " not visible");
            var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            Require(form.ClientRectangle.Contains(bounds), name + " outside window");
            var surface = form.Controls.Find("windowSurface", true).Single();
            Require(button.Parent!.Parent == surface, name + " is outside the main glass rectangle");
            Require(bounds.Left > 0 && bounds.Right < form.ClientSize.Width && bounds.Top > 0,
                name + " touches the outside of the main rectangle");
        }
    }

    internal static void CheckLibrary(LauncherForm form)
    {
        CheckChrome(form);
        var labels = new[] { "welcome", "accountTime", "accountExpiry" }.Select(name => Field<Label>(form, name)).ToArray();
        var panel = form.Controls.Find("accountPanel", true).Single();
        foreach (var label in labels)
        {
            Require(panel.ClientRectangle.Contains(label.Bounds), "account text outside its panel");
            Require(label.GetPreferredSize(label.Size).Height <= label.Height, "account text clips vertically: " + label.Text);
            foreach (var name in new[] { "logout", "redeemCode", "syncCatalog" })
                Require(!label.RectangleToScreen(label.ClientRectangle).IntersectsWith(
                    Field<Button>(form, name).RectangleToScreen(Field<Button>(form, name).ClientRectangle)), "account text overlaps " + name);
        }
        for (var i = 0; i < labels.Length; i++) for (var j = i + 1; j < labels.Length; j++)
            Require(!labels[i].Bounds.IntersectsWith(labels[j].Bounds), "account rows overlap");
        var minimize = form.Controls.Find("windowMinimize", true).Single();
        typeof(Control).GetMethod("OnClick", Flags)!.Invoke(minimize, [EventArgs.Empty]);
        Require(form.WindowState == FormWindowState.Minimized, "minimize action failed");
        form.WindowState = FormWindowState.Normal;
        Application.DoEvents();
        CheckPartialRepaint(Field<GlassLabel>(form, "accountTime"));
        CheckPartialRepaint(Field<GlassLabel>(form, "accountExpiry"));
    }

    private static void CheckPartialRepaint(GlassLabel label)
    {
        using var complete = new Bitmap(label.Width, label.Height);
        using var partial = new Bitmap(label.Width, label.Height);
        var paint = typeof(GlassLabel).GetMethod("OnPaint", Flags)!;
        using (var graphics = Graphics.FromImage(complete))
            paint.Invoke(label, [new PaintEventArgs(graphics, label.ClientRectangle)]);
        // Split through glyphs, as happens when a sibling exposes only part of the label.
        for (var y = 0; y < label.Height; y += 5)
        {
            var clip = new Rectangle(0, y, label.Width, Math.Min(5, label.Height - y));
            using var graphics = Graphics.FromImage(partial);
            graphics.SetClip(clip);
            paint.Invoke(label, [new PaintEventArgs(graphics, clip)]);
        }
        for (var y = 0; y < label.Height; y++) for (var x = 0; x < label.Width; x++)
            Require(complete.GetPixel(x, y) == partial.GetPixel(x, y), "partial repaint clipped text or left stale pixels");
    }

    internal static void CheckQuietRefresh(LauncherForm form, ProgramInfo[] programs)
    {
        var api = Field<LauncherApi>(form, "api");
        var oldHttp = Field<HttpClient>(api, "http");
        using var fakeHttp = new HttpClient(new CatalogHandler(api.Account!, programs)) { BaseAddress = new Uri("http://127.0.0.1") };
        typeof(LauncherApi).GetField("http", Flags)!.SetValue(api, fakeHttp);
        typeof(LauncherApi).GetField("token", Flags)!.SetValue(api, "ui-test");
        var changes = 0;
        var paints = 0;
        var install = Field<Button>(form, "install");
        var cards = Field<FlowLayoutPanel>(form, "programCards");
        EventHandler changed = (_, _) => changes++;
        PaintEventHandler paint = (_, _) => paints++;
        install.EnabledChanged += changed;
        install.Paint += paint;
        var existing = cards.Controls.Cast<Control>().ToArray();
        try
        {
            var task = (Task)typeof(LauncherForm).GetMethod("BackgroundSyncAsync", Flags)!.Invoke(form, null)!;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                Require(!Field<bool>(form, "busy") && install.Enabled, "background sync disabled foreground controls");
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Require(task.IsCompleted, "background sync timed out");
            task.GetAwaiter().GetResult();
            for (var i = 0; i < 3; i++) { Invoke(form, "UpdateAccountTime"); Application.DoEvents(); }
            Require(changes == 0, "background sync toggled install button");
            Require(paints == 0, "background sync repainted install button");
            Require(cards.Controls.Cast<Control>().SequenceEqual(existing), "unchanged catalog rebuilt tiles");
        }
        finally
        {
            install.EnabledChanged -= changed;
            install.Paint -= paint;
            typeof(LauncherApi).GetField("http", Flags)!.SetValue(api, oldHttp);
        }
        Console.WriteLine("UI background refresh: enabled state, button paint and card identity preserved.");
    }

    internal static void CheckProgramExpiry(LauncherForm form)
    {
        var selectedField = typeof(LauncherForm).GetField("selected", Flags)!;
        var previous = selectedField.GetValue(form);
        var api = Field<LauncherApi>(form, "api");
        var account = api.Account;
        try
        {
            typeof(LauncherApi).GetProperty("Account")!.SetValue(api,
                new AccountInfo("test", api.ServerNow.AddDays(60), api.ServerNow));
            foreach (var (deadline, expired) in new[] {
                ((DateTimeOffset?)api.ServerNow.AddDays(-1), true),
                ((DateTimeOffset?)api.ServerNow.AddDays(30), false),
                ((DateTimeOffset?)null, false) })
            {
                selectedField.SetValue(form, new ProgramInfo("chispa", "A", "Preview.exe",
                    HasRelease: true, HasEntitlement: true, EntitlementExpiresAt: deadline));
                Invoke(form, "MarkSelection");
                Require(Field<Button>(form, "refresh").Enabled == !expired,
                    "refresh button follows aggregate expiry instead of selected program");
                var label = Field<Label>(form, "accountTime").Text;
                Require(label.StartsWith("此程序"), "per-program expiry label missing");
                Require(expired ? label.Contains("已到期") : deadline is null ? label.Contains("永久") : !label.Contains("已到期"),
                    "selected program expiry display incorrect");
                CheckLibrary(form);
            }
            typeof(LauncherApi).GetProperty("Account")!.SetValue(api,
                new AccountInfo("passport", DateTimeOffset.MinValue, DateTimeOffset.UtcNow, new()));
            Invoke(form, "UpdateAccountTime");
            Require(Field<Label>(form, "accountTime").Text.Contains("尚未开通"), "passport-only account shows invalid deadline");
            Console.WriteLine("UI individual deadlines: expired, active and permanent selections passed.");
        }
        finally
        {
            typeof(LauncherApi).GetProperty("Account")!.SetValue(api, account);
            selectedField.SetValue(form, previous);
            Invoke(form, "MarkSelection");
        }
    }

    internal static void CheckRegistration(LauncherForm form, string output)
    {
        foreach (var (method, file) in new[] { ("ShowRegistration", "ui-register.png"), ("ShowRedemption", "ui-redeem.png") })
        {
            Invoke(form, method);
            Application.DoEvents();
            CheckChrome(form);
            CheckText(form);
            var page = Field<Panel>(form, "registrationPage");
            foreach (var name in new[] { "registrationEmail", "registrationPassword", "registrationCode", "submitRegistration", "registrationBack" })
            {
                if (method == "ShowRedemption" && name == "registrationPassword") continue;
                var control = Field<Control>(form, name);
                Require(control.Visible, name + " hidden");
                Require(page.RectangleToScreen(page.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), name + " outside page");
            }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, file));
        }
        Invoke(form, "LeaveRegistration");
        CheckChrome(form);
        Console.WriteLine("UI chrome, account layout, registration and renewal pages passed.");
    }

    internal static void CheckText(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (!control.Visible) continue;
            if (control is GlassLabel label && label.Text.Length > 0 && !label.AutoEllipsis)
                Require(label.GetPreferredSize(label.Size).Height <= label.Height,
                    $"Text clips: {label.Text} requires {label.GetPreferredSize(label.Size).Height}, has {label.Height}");
            if (control is TextBox edit)
                Require(edit.Height >= edit.PreferredHeight && edit.Parent!.ClientRectangle.Contains(edit.Bounds), "input text clips vertically");
            CheckText(control);
        }
    }

    private sealed class CatalogHandler(AccountInfo account, ProgramInfo[] programs) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(150, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith("session")
                ? JsonContent.Create(account) : JsonContent.Create(programs) };
        }
    }
}
