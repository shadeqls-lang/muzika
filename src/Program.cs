// muzika — WebView2 host. Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Emopunk
{
    static class Program
    {
        public static string Root;
        public static string Data;
        public static EventWaitHandle ShowSignal;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        public const string Version = "0.4.1";
        public static Mutex Single;
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            // one copy only: a second launch just brings the running player to front
            bool created;
            bool restarted = args != null && args.Any(a => a == "--updated" || a == "--restart");
            JustUpdated = args != null && args.Contains("--updated");
            Mutex single = new Mutex(true, "muzika-player-single-instance", out created);
            if (!created && restarted)
            {
                // the old copy is closing right now: wait for it
                try { created = single.WaitOne(10000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                try { EventWaitHandle.OpenExisting("muzika-player-show").Set(); } catch { }
                return;
            }
            Single = single;
            try { string prevExe = Application.ExecutablePath + ".old"; if (File.Exists(prevExe)) File.Delete(prevExe); } catch { }
            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "muzika-player-show");

            Root = AppDomain.CurrentDomain.BaseDirectory;
            Data = Path.Combine(Root, "muzika_data");
            string old = Path.Combine(Root, "emopunk_data");          // keep the library from the previous build
            if (!Directory.Exists(Data) && Directory.Exists(old))
            {
                try { Directory.Move(old, Data); } catch { Data = old; }
            }
            try { Directory.CreateDirectory(Data); }
            catch (Exception ex) { MessageBox.Show("can't create folder:\n" + Data + "\n\n" + ex.Message, "muzika"); return; }

            // videos (intro + about) ship inside the exe and are unpacked once
            try
            {
                string ad = Path.Combine(Data, "assets");
                Directory.CreateDirectory(ad);
                foreach (string n in new[] { "intro.webm", "about.webm", "ring.webm", "disc.jpg" })
                {
                    byte[] vb = ReadRes(n);
                    string f = Path.Combine(ad, n);
                    if (!File.Exists(f) || new FileInfo(f).Length != vb.Length) File.WriteAllBytes(f, vb);
                }
            }
            catch { }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            try
            {
                string loader = Path.Combine(Data, "WebView2Loader.dll");
                byte[] lb = ReadRes("wv2loader");
                if (!File.Exists(loader) || new FileInfo(loader).Length != lb.Length) File.WriteAllBytes(loader, lb);
                LoadLibrary(loader);
            }
            catch (Exception ex) { MessageBox.Show("Can't unpack WebView2Loader.dll\n\n" + ex.Message, "muzika"); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Run();
            GC.KeepAlive(single);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run() { Application.Run(new MainForm()); }

        static readonly Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>();
        static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            string res = null;
            if (name == "Microsoft.Web.WebView2.Core") res = "wv2core";
            else if (name == "Microsoft.Web.WebView2.WinForms") res = "wv2wf";
            if (res == null) return null;
            lock (loaded)
            {
                Assembly a;
                if (loaded.TryGetValue(res, out a)) return a;
                a = Assembly.Load(ReadRes(res));
                loaded[res] = a;
                return a;
            }
        }

        public static byte[] ReadRes(string n)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(n))
            {
                if (s == null) throw new Exception("resource missing: " + n);
                using (MemoryStream m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
            }
        }

        public static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }

    class MainForm : Form
    {
        const string Origin = "https://emopunk.example";
        const int Edge = 5;   // invisible resize border around the page
        WebView2 web;
        Library lib;
        byte[] uiHtml;
        JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        bool ready, exiting;
        NotifyIcon tray;
        Updater upd;
        string settingsScript;
        bool fsOn; Rectangle fsBounds; FormWindowState fsState;
        bool restarting, noFlush;
        DiscordRpc rpc;
        SoundCloud sc;
        ScStream scs;
        ImgCache imgs;
        CoreWebView2Environment envRef;
        string rpcState = "off";
        ToolStripMenuItem miPlay;

        // ---------- win32 ----------
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr hmon, [In, Out] MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MONITORINFO { public int cbSize = Marshal.SizeOf(typeof(MONITORINFO)); public RECT rcMonitor = new RECT(); public RECT rcWork = new RECT(); public int dwFlags = 0; }

        public MainForm()
        {
            Text = "muzika";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(10, 10, 11);
            Padding = new Padding(Edge);
            MinimumSize = new Size(1100, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1360, 860);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            RestoreBounds_();

            lib = new Library(Program.Data);
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(10, 10, 11);
            Controls.Add(web);
            BuildTray();
            sc = new SoundCloud(Program.Data);
            Log.File_ = Path.Combine(Program.Data, "log.txt");
            scs = new ScStream(sc, Program.Data);
            imgs = new ImgCache(Path.Combine(Program.Data, "img"));
            scs.Tools = Path.Combine(Program.Data, "tools");
            sc.OnLoggedOut = delegate { UI(delegate { PostRaw("{\"type\":\"scMe\",\"me\":null}"); Post(new { type = "toast", text = "soundcloud login expired" }); }); };
            rpc = new DiscordRpc();
            upd = new Updater(Program.Data);
            upd.OnState = delegate(string st, string ver, double pct) { UI(delegate { Post(new { type = "update", state = st, ver = ver, pct = pct, cur = Program.Version, repo = upd.Repo, auto = upd.Auto, notes = upd.Notes }); }); };
            rpc.OnState = delegate(string st)
            {
                rpcState = st;
                try { if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (ready) Post(new { type = "discord", state = rpcState }); }); } catch { }
            };

            Load += OnLoad;
            Resize += delegate { OnResized(); };
            FormClosing += OnClosing;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000 | 0x00010000 | 0x00080000;   // minimize + maximize box + system menu (taskbar behaviour)
                cp.ClassStyle |= 0x00020000;                          // drop shadow
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);   // dark mode for system bits
                int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); // rounded corners on Win11
            }
            catch { }
            try { RegisterHotKey(Handle, 1, 0, 0xB3); RegisterHotKey(Handle, 2, 0, 0xB0); RegisterHotKey(Handle, 3, 0, 0xB1); } catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2); UnregisterHotKey(Handle, 3); } catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)   // global media keys
            {
                int id = m.WParam.ToInt32();
                string k = id == 1 ? "toggle" : id == 2 ? "next" : id == 3 ? "prev" : null;
                if (k != null && ready) Post(new { type = "key", k = k });
                return;
            }
            if (m.Msg == 0x0084 && WindowState == FormWindowState.Normal && !fsOn)   // resize from the thin border
            {
                base.WndProc(ref m);
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int g = Edge + 2;
                bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
                if (t && l) m.Result = (IntPtr)13;
                else if (t && r) m.Result = (IntPtr)14;
                else if (b && l) m.Result = (IntPtr)16;
                else if (b && r) m.Result = (IntPtr)17;
                else if (l) m.Result = (IntPtr)10;
                else if (r) m.Result = (IntPtr)11;
                else if (t) m.Result = (IntPtr)12;
                else if (b) m.Result = (IntPtr)15;
                return;
            }
            if (m.Msg == 0x0024)   // maximize to the work area, not over the taskbar
            {
                MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(Handle, 2);
                MONITORINFO mi = new MONITORINFO();
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, mi))
                {
                    mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                    mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                    mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                    mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                }
                mmi.ptMinTrackSize.x = MinimumSize.Width;
                mmi.ptMinTrackSize.y = MinimumSize.Height;
                Marshal.StructureToPtr(mmi, m.LParam, true);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void OnResized()
        {
            bool max = WindowState == FormWindowState.Maximized;
            Padding = new Padding(max || fsOn ? 0 : Edge);
            SetLowMemory(WindowState == FormWindowState.Minimized);
            if (ready) Post(new { type = "window", max = max });
        }

        void UI(MethodInvoker a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void PostRaw(string json)
        {
            try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

        // runs a soundcloud request off the ui thread, hands raw json to the page
        async void ScCall(int req, Func<string[]> work)
        {
            string[] res = null; string err = null;
            try { res = await Task.Run(work); }
            catch (Exception ex) { err = ex.Message; }
            if (err != null || res == null || res.Length < 2 || string.IsNullOrEmpty(res[1]))
            {
                Post(new { type = "scError", req = req, text = err ?? "empty answer" });
                return;
            }
            PostRaw("{\"type\":\"scData\",\"req\":" + req + ",\"kind\":\"" + res[0] + "\",\"data\":" + res[1] + "}");
        }

        async void SendMe(bool fresh = false)
        {
            string me = null;
            try { me = await Task.Run(() => sc.Me()); } catch { }
            PostRaw("{\"type\":\"scMe\",\"fresh\":" + (fresh ? "true" : "false") + ",\"me\":" + (string.IsNullOrEmpty(me) ? "null" : me) + "}");
        }

        void ScLogin()
        {
            string flag = Path.Combine(Program.Data, "sc_logged_out");
            bool fresh = File.Exists(flag);
            using (ScLoginForm f = new ScLoginForm(Path.Combine(Program.Data, "webview_login"), fresh, Icon))
            {
                if (f.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(f.Token))
                {
                    sc.SetToken(f.Token);
                    try { File.Delete(flag); } catch { }
                    SendMe(true);
                }
            }
        }

        async void ScLike(string id, bool on)
        {
            string err = null;
            try { await Task.Run(() => sc.Like(id, on)); } catch (Exception ex) { err = ex.Message; }
            if (err != null) Post(new { type = "scLikeFail", id = id, on = on, text = err });
        }

        // ---------- updates / restart ----------
        void Restart(string arg)
        {
            restarting = true;
            // the new copy waits until this one has fully exited (the mutex is freed with the process)
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, arg) { UseShellExecute = false, WorkingDirectory = Program.Root }); } catch { }
            ExitApp();
        }

        void ApplyUpdate()
        {
            if (!upd.Ready) return;
            try { upd.Swap(); }
            catch (Exception ex)
            {
                Post(new { type = "toast", text = "update failed: " + ex.Message });
                Post(new { type = "update", state = "error", ver = ex.Message, pct = 0, cur = Program.Version, repo = upd.Repo, auto = upd.Auto, notes = upd.Notes });
                return;
            }
            Restart("--updated");
        }

        // this version's notes ship inside the exe (notes.md), other versions come from github
        static string OwnNotes()
        {
            try { return Encoding.UTF8.GetString(Program.ReadRes("notes")).TrimStart('\uFEFF'); } catch { return null; }
        }

        async void SendNotes(string ver)
        {
            if (string.IsNullOrEmpty(ver)) ver = Program.Version;
            string text = null, err = null;
            if (!Updater.Newer(ver, Program.Version) && !Updater.Newer(Program.Version, ver)) text = OwnNotes();
            if (text == null)
            {
                try { text = await Task.Run(() => upd.FetchNotes(ver)); } catch (Exception ex) { err = ex.Message; Log.Write("notes: " + ex.Message); }
            }
            Post(new { type = "notes", ver = ver, text = text, err = err });
        }

        // ---------- backup: one .muzika file (a zip), never the soundcloud login ----------
        static readonly string[] BackupFiles = { "library.json", "history.json", "stats.json", "playlists.json" };

        void BackupExport(string ls)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "save muzika backup";
                d.Filter = "muzika backup (*.muzika)|*.muzika";
                d.FileName = "muzika-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".muzika";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    lib.Flush();
                    string tmp = d.FileName + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                    int n = 0;
                    using (ZipArchive z = ZipFile.Open(tmp, ZipArchiveMode.Create))
                    {
                        foreach (string f in BackupFiles)
                        {
                            string a = Path.Combine(Program.Data, f);
                            if (File.Exists(a)) { z.CreateEntryFromFile(a, f); n++; }
                        }
                        WriteEntry(z, "settings.json", string.IsNullOrEmpty(ls) ? "{}" : ls);
                        WriteEntry(z, "muzika.json", new JavaScriptSerializer().Serialize(new { app = "muzika", version = Program.Version, made = DateTime.Now.ToString("s") }));
                    }
                    if (File.Exists(d.FileName)) File.Delete(d.FileName);
                    File.Move(tmp, d.FileName);
                    Post(new { type = "toast", text = "backup saved" });
                }
                catch (Exception ex) { Post(new { type = "toast", text = "backup failed: " + ex.Message }); }
            }
        }

        static void WriteEntry(ZipArchive z, string name, string text)
        {
            using (Stream s = z.CreateEntry(name).Open()) { byte[] b = new UTF8Encoding(false).GetBytes(text); s.Write(b, 0, b.Length); }
        }

        void BackupImport()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "restore muzika backup";
                d.Filter = "muzika backup (*.muzika)|*.muzika";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (ZipArchive z = ZipFile.OpenRead(d.FileName))
                    {
                        if (z.GetEntry("muzika.json") == null) { Post(new { type = "toast", text = "that's not a muzika backup" }); return; }
                        // only known names, written by us: nothing else in the archive is touched
                        foreach (string f in BackupFiles)
                        {
                            ZipArchiveEntry e = z.GetEntry(f);
                            if (e == null) continue;
                            string to = Path.Combine(Program.Data, f), tmp = to + ".imp";
                            e.ExtractToFile(tmp, true);
                            if (File.Exists(to)) File.Delete(to);
                            File.Move(tmp, to);
                        }
                        ZipArchiveEntry st = z.GetEntry("settings.json");
                        if (st != null && st.Length < 4000000) st.ExtractToFile(Path.Combine(Program.Data, "settings_import.json"), true);
                    }
                }
                catch (Exception ex) { Post(new { type = "toast", text = "restore failed: " + ex.Message }); return; }
                noFlush = true;   // the in-memory library must not overwrite the restored one
                Restart("--restart");
            }
        }

        // ---------- fullscreen: the whole monitor, taskbar included ----------
        void SetFullscreen(bool on)
        {
            if (on == fsOn) return;
            if (on)
            {
                fsState = WindowState; fsBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Rectangle scr = Screen.FromControl(this).Bounds;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                fsOn = true;
                Bounds = scr; OnResized();
            }
            else
            {
                fsOn = false;
                Bounds = fsBounds; OnResized();
                if (fsState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
            }
            Post(new { type = "fs", on = fsOn });
        }

        // bring the library, stats and soundcloud login over from another muzika folder
        void ImportData()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "pick your old muzika folder (the one with muzika_data inside)";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string src = d.SelectedPath;
                if (Directory.Exists(Path.Combine(src, "muzika_data"))) src = Path.Combine(src, "muzika_data");
                else if (Directory.Exists(Path.Combine(src, "emopunk_data"))) src = Path.Combine(src, "emopunk_data");
                if (!File.Exists(Path.Combine(src, "library.json")) && !File.Exists(Path.Combine(src, "sc_auth.bin")))
                { Post(new { type = "toast", text = "no muzika data in that folder" }); return; }
                if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(Program.Data).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                { Post(new { type = "toast", text = "that's this folder already" }); return; }
                int n = 0;
                foreach (string f in new[] { "library.json", "history.json", "stats.json", "playlists.json", "sc_auth.bin", "sc_key.txt", "window.txt", "update.json", "tray_hint_shown" })
                {
                    string a = Path.Combine(src, f);
                    if (File.Exists(a)) { try { File.Copy(a, Path.Combine(Program.Data, f), true); n++; } catch { } }
                }
                foreach (string dir in new[] { "img", "cache" })
                {
                    string a = Path.Combine(src, dir);
                    if (!Directory.Exists(a)) continue;
                    Directory.CreateDirectory(Path.Combine(Program.Data, dir));
                    foreach (string f in Directory.GetFiles(a)) try { string to = Path.Combine(Program.Data, dir, Path.GetFileName(f)); if (!File.Exists(to)) File.Copy(f, to); } catch { }
                }
                if (n == 0) { Post(new { type = "toast", text = "nothing could be copied" }); return; }
                noFlush = true;   // don't write the old in-memory library over what was just copied
                Restart("--restart");
            }
        }

        void BeginDrag()
        {
            if (WindowState == FormWindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        }

        void ToggleMax()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        // ---------- tray ----------
        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkRenderer();
            menu.ShowImageMargin = false;
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.Items.Add("show muzika", null, delegate { ShowFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            miPlay = new ToolStripMenuItem("play", null, delegate { if (ready) Post(new { type = "key", k = "toggle" }); });
            menu.Items.Add(miPlay);
            menu.Items.Add("next", null, delegate { if (ready) Post(new { type = "key", k = "next" }); });
            menu.Items.Add("previous", null, delegate { if (ready) Post(new { type = "key", k = "prev" }); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("exit", null, delegate { ExitApp(); });
            foreach (ToolStripItem it in menu.Items) it.Padding = new Padding(6, 5, 6, 5);

            tray = new NotifyIcon();
            tray.Icon = Icon != null ? Icon : SystemIcons.Application;
            tray.Text = "muzika";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (Visible && WindowState != FormWindowState.Minimized) HideToTray(); else ShowFromTray();
            };
        }

        void HideToTray()
        {
            Hide();
            SetLowMemory(true);
            string flag = Path.Combine(Program.Data, "tray_hint_shown");
            if (!File.Exists(flag))
            {
                try { File.WriteAllText(flag, "1"); } catch { }
                tray.ShowBalloonTip(3000, "muzika", "still playing. click the tray icon to bring me back, right-click for controls.", ToolTipIcon.None);
            }
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            SetLowMemory(false);
        }

        void ExitApp()
        {
            exiting = true;
            if (upd.Ready && !restarting) { try { upd.Swap(); } catch { } }   // a downloaded update goes in on the way out
            try { tray.Visible = false; tray.Dispose(); } catch { }
            Close();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            SaveBounds();
            if (!noFlush) try { lib.Flush(); } catch { }
            try { rpc.Shutdown(); } catch { }
            try { tray.Visible = false; } catch { }
        }

        void SetLowMemory(bool low)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) return;
                object c = web.CoreWebView2;
                PropertyInfo pi = c.GetType().GetProperty("MemoryUsageTargetLevel");
                if (pi != null) pi.SetValue(c, Enum.Parse(pi.PropertyType, low ? "Low" : "Normal"), null);
            }
            catch { }
        }

        // ---------- startup ----------
        async void OnLoad(object sender, EventArgs e)
        {
            Thread waiter = new Thread(delegate()
            {
                while (true)
                {
                    Program.ShowSignal.WaitOne();
                    try { BeginInvoke((MethodInvoker)ShowFromTray); } catch { return; }
                }
            });
            waiter.IsBackground = true;
            waiter.Start();

            try
            {
                uiHtml = Program.ReadRes("uihtml");
                try { CoreWebView2Environment.SetLoaderDllFolderPath(Program.Data); } catch { }
                CoreWebView2EnvironmentOptions opt = new CoreWebView2EnvironmentOptions();
                opt.AdditionalBrowserArguments =
                    "--autoplay-policy=no-user-gesture-required --disable-background-networking --disable-component-update " +
                    "--disable-sync --disable-default-apps --no-first-run --disable-extensions --renderer-process-limit=1 " +
                    "--disable-site-isolation-trials --disable-features=msSmartScreenProtection,Translate,msEdgeShoppingAssistant,OptimizationHints,MediaRouter,SpareRendererForSitePerProcess";
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.Data, "webview"), opt);
                envRef = env;
                await web.EnsureCoreWebView2Async(env);
                CoreWebView2 c = web.CoreWebView2;
                c.Settings.AreDefaultContextMenusEnabled = false;
                c.Settings.IsStatusBarEnabled = false;
                c.Settings.IsZoomControlEnabled = false;
                try { c.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
                c.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                c.WebResourceRequested += OnRequest;
                c.WebMessageReceived += OnMessage;
                c.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; };
                string imp = Path.Combine(Program.Data, "settings_import.json");
                if (File.Exists(imp))
                {
                    try
                    {
                        string json = File.ReadAllText(imp, Encoding.UTF8);
                        if (new JavaScriptSerializer().DeserializeObject(json) is Dictionary<string, object>)
                            settingsScript = await c.AddScriptToExecuteOnDocumentCreatedAsync(
                                "(function(){try{var d=" + json + ";for(var k in d){if(/^ep_/.test(k)&&typeof d[k]==='string')localStorage.setItem(k,d[k]);}}catch(e){}})();");
                    }
                    catch { }
                    try { File.Delete(imp); } catch { }
                }
                c.Navigate(Origin + "/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("webview2 didn't start.\n\nIf Windows says it's missing, install \"Microsoft Edge WebView2 Runtime\":\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" + ex.Message, "muzika");
                ExitApp();
            }
        }

        // ---------- local "server" ----------
        void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            CoreWebView2Environment env = web.CoreWebView2.Environment;
            try
            {
                string p = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
                if (p == "/" || p == "/index.html")
                {
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(uiHtml), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                    return;
                }
                if (p.StartsWith("/cover/"))
                {
                    string f = lib.CoverPath(p.Substring(7));
                    if (f != null && File.Exists(f))
                    {
                        byte[] b = File.ReadAllBytes(f);
                        string mime = (b.Length > 3 && b[0] == 0x89 && b[1] == 0x50) ? "image/png" : "image/jpeg";
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(b), 200, "OK", "Content-Type: " + mime + "\r\nCache-Control: max-age=604800");
                        return;
                    }
                }
                if (p.StartsWith("/scstream/"))
                {
                    string sid = p.Substring(10);
                    if (!Regex.IsMatch(sid, "^[0-9]+$")) { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                    string hit = scs.Cached(sid);
                    if (hit != null) { e.Response = Media(env, hit, e.Request.Headers); return; }
                    CoreWebView2Deferral def = e.GetDeferral();
                    CoreWebView2HttpRequestHeaders hdrs = e.Request.Headers;
                    Task.Run(() => scs.Ensure(sid)).ContinueWith(t =>
                    {
                        UI(delegate
                        {
                            try
                            {
                                string f = t.Result;
                                if (f != null) e.Response = Media(env, f, hdrs);
                                else
                                {
                                    e.Response = env.CreateWebResourceResponse(null, 502, "Bad Gateway", "");
                                    Post(new { type = "streamErr", id = sid, text = scs.LastError(sid) ?? "no connection" });
                                }
                            }
                            catch { }
                            finally { try { def.Complete(); } catch { } }
                        });
                    });
                    return;
                }
                if (p == "/img")
                {
                    string q = new Uri(e.Request.Uri).Query, src = null;
                    foreach (string kv in q.TrimStart('?').Split('&')) if (kv.StartsWith("u=")) src = Uri.UnescapeDataString(kv.Substring(2));
                    Uri iu;
                    if (src == null || !Uri.TryCreate(src, UriKind.Absolute, out iu) || iu.Scheme != "https" || !iu.Host.EndsWith(".sndcdn.com"))
                    { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                    string cached = imgs.Path_(src);
                    if (File.Exists(cached)) { e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(cached)), 200, "OK", "Content-Type: " + Mime(src.Split('?')[0]) + "\r\nCache-Control: max-age=31536000"); return; }
                    CoreWebView2Deferral idef = e.GetDeferral();
                    Task.Run(() => imgs.Get(src)).ContinueWith(t =>
                    {
                        UI(delegate
                        {
                            try
                            {
                                if (t.IsFaulted || t.Result == null) e.Response = env.CreateWebResourceResponse(null, 502, "Bad Gateway", "");
                                else e.Response = env.CreateWebResourceResponse(new MemoryStream(t.Result), 200, "OK", "Content-Type: " + Mime(src.Split('?')[0]) + "\r\nCache-Control: max-age=31536000");
                            }
                            catch { }
                            finally { try { idef.Complete(); } catch { } }
                        });
                    });
                    return;
                }
                if (p.StartsWith("/scseg/"))
                {
                    string[] parts = p.Substring(7).Split('/');
                    int idx;
                    if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9]+$") || !(parts[1] == "init" || int.TryParse(parts[1], out idx)))
                    { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                    string sid = parts[0]; int si = parts[1] == "init" ? -1 : int.Parse(parts[1]);
                    CoreWebView2Deferral def = e.GetDeferral();
                    Task.Run(() => scs.Segment(sid, si)).ContinueWith(t =>
                    {
                        UI(delegate
                        {
                            try
                            {
                                if (t.IsFaulted || t.Result == null) e.Response = env.CreateWebResourceResponse(null, 502, "Bad Gateway", "");
                                else e.Response = env.CreateWebResourceResponse(new MemoryStream(t.Result), 200, "OK", "Content-Type: application/octet-stream\r\nCache-Control: no-store\r\nContent-Length: " + t.Result.Length);
                            }
                            catch { }
                            finally { try { def.Complete(); } catch { } }
                        });
                    });
                    return;
                }
                if (p.StartsWith("/pcover/") || p == "/avatar")
                {
                    string f = p == "/avatar" ? Path.Combine(Program.Data, "avatar.jpg") : lib.PlCoverPath(p.Substring(8));
                    if (f != null && File.Exists(f))
                    {
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(f)), 200, "OK", "Content-Type: image/jpeg\r\nCache-Control: max-age=31536000");
                        return;
                    }
                }
                if (p.StartsWith("/asset/"))
                {
                    string n = Path.GetFileName(p.Substring(7));
                    string f = Path.Combine(Program.Data, "assets", n);
                    if (File.Exists(f)) { e.Response = Media(env, f, e.Request.Headers); return; }
                }
                if (p.StartsWith("/media/"))
                {
                    string path = lib.PathOf(p.Substring(7));
                    if (path != null && File.Exists(path)) { e.Response = Media(env, path, e.Request.Headers); return; }
                }
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            }
            catch (Exception)
            {
                try { e.Response = env.CreateWebResourceResponse(null, 500, "Error", ""); } catch { }
            }
        }

        static string Mime(string path)
        {
            string x = Path.GetExtension(path).ToLowerInvariant();
            if (x == ".mp3") return "audio/mpeg";
            if (x == ".m4a" || x == ".aac" || x == ".mp4") return "audio/mp4";
            if (x == ".flac") return "audio/flac";
            if (x == ".wav") return "audio/wav";
            if (x == ".ogg" || x == ".opus" || x == ".oga") return "audio/ogg";
            if (x == ".m4a") return "audio/mp4";
            if (x == ".aac") return "audio/aac";
            if (x == ".jpg" || x == ".jpeg") return "image/jpeg";
            if (x == ".png") return "image/png";
            if (x == ".webm") return "video/webm";
            if (x == ".jpg" || x == ".jpeg") return "image/jpeg";
            return "application/octet-stream";
        }

        CoreWebView2WebResourceResponse Media(CoreWebView2Environment env, string path, CoreWebView2HttpRequestHeaders h)
        {
            long len = new FileInfo(path).Length;
            string mime = Mime(path);
            string range = null;
            try { if (h.Contains("Range")) range = h.GetHeader("Range"); } catch { }

            if (string.IsNullOrEmpty(range) || !range.StartsWith("bytes="))
            {
                FileStream all = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return env.CreateWebResourceResponse(all, 200, "OK",
                    "Content-Type: " + mime + "\r\nAccept-Ranges: bytes\r\nContent-Length: " + len);
            }

            long start = 0, end = len - 1, a, b;
            string[] parts = range.Substring(6).Split(',')[0].Trim().Split('-');
            if (parts[0].Length > 0 && long.TryParse(parts[0], out a))
            {
                start = a;
                if (parts.Length > 1 && parts[1].Length > 0 && long.TryParse(parts[1], out b)) end = Math.Min(b, len - 1);
            }
            else if (parts.Length > 1 && long.TryParse(parts[1], out b)) start = Math.Max(0, len - b);

            if (start >= len || start > end)
                return env.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", "Content-Range: bytes */" + len);

            const long Chunk = 2L * 1024 * 1024;
            if (end - start + 1 > Chunk) end = start + Chunk - 1;

            byte[] buf = new byte[end - start + 1];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(start, SeekOrigin.Begin);
                int r = 0;
                while (r < buf.Length) { int n = fs.Read(buf, r, buf.Length - r); if (n <= 0) break; r += n; }
            }
            return env.CreateWebResourceResponse(new MemoryStream(buf), 206, "Partial Content",
                "Content-Type: " + mime + "\r\nAccept-Ranges: bytes\r\nContent-Length: " + buf.Length +
                "\r\nContent-Range: bytes " + start + "-" + end + "/" + len);
        }

        // ---------- messages from the page ----------
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            if (m == null) return;
            string cmd = Str(m, "cmd");

            if (cmd == "ready")
            {
                ready = true;
                if (settingsScript != null) { try { web.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(settingsScript); } catch { } settingsScript = null; }
                bool just = upd.JustVer != null && !Updater.Newer(upd.JustVer, Program.Version) && !Updater.Newer(Program.Version, upd.JustVer);
                Post(new { type = "update", state = upd.Ready ? "ready" : (upd.Auto ? "checking" : "idle"), ver = upd.NewVersion, pct = 0, cur = Program.Version, repo = upd.Repo, auto = upd.Auto, notes = upd.Notes,
                    boot = true, just = just || Program.JustUpdated, justNotes = just || Program.JustUpdated ? (OwnNotes() ?? upd.JustNotes) : null });
                if (!just) upd.SeenNotes();
                upd.Start();
                SendLibrary(); Post(new { type = "window", max = WindowState == FormWindowState.Maximized }); Post(new { type = "discord", state = rpcState }); }
            else if (cmd == "discord") { rpc.Configure(Str(m, "on") == "True", Str(m, "id"), Str(m, "gif")); Post(new { type = "discord", state = rpcState }); }
            else if (cmd == "drag") BeginDrag();
            else if (cmd == "max") ToggleMax();
            else if (cmd == "min") WindowState = FormWindowState.Minimized;
            else if (cmd == "close") HideToTray();
            else if (cmd == "now")
            {
                string t = Str(m, "artist") + " · " + Str(m, "title");
                bool playing = Str(m, "playing") == "True";
                try { tray.Text = t.Length > 63 ? t.Substring(0, 62) + "…" : t; } catch { }
                miPlay.Text = playing ? "pause" : "play";
                Text = "muzika · " + Str(m, "title");
                rpc.SetTrack(Str(m, "title"), Str(m, "artist"), playing, Num(m, "pos"), Num(m, "dur"));
            }
            else if (cmd == "played") lib.Played(Str(m, "id"));
            else if (cmd == "scState") { Post(new { type = "scState", logged = sc.Token != null }); if (sc.Token != null) SendMe(); }
            else if (cmd == "scLogin") BeginInvoke((MethodInvoker)ScLogin);   // not inside the webview callback
            else if (cmd == "scLogout")
            {
                sc.SetToken(null);
                try { File.WriteAllText(Path.Combine(Program.Data, "sc_logged_out"), "1"); } catch { }
                PostRaw("{\"type\":\"scMe\",\"me\":null}");
            }
            else if (cmd == "scMine")
            {
                string what = Str(m, "what");
                ScCall((int)Num(m, "req"), delegate { return new[] { what == "playlists" || what == "liked_playlists" ? "playlists" : what == "feed" ? "feed" : "likes", sc.Mine(what) }; });
            }
            else if (cmd == "scPl")
            {
                string pid = Str(m, "id");
                ScCall((int)Num(m, "req"), delegate { return new[] { "playlist", sc.Playlist(pid) }; });
            }
            else if (cmd == "scLikedIds") ScCall((int)Num(m, "req"), delegate { return new[] { "likedids", sc.LikedIds() }; });
            else if (cmd == "scLike") ScLike(Str(m, "id"), Str(m, "on") == "True");
            else if (cmd == "scAdd")
            {
                object items; m.TryGetValue("items", out items);
                List<Dictionary<string, object>> add = new List<Dictionary<string, object>>();
                System.Collections.IEnumerable il = items as System.Collections.IEnumerable;
                if (il != null) foreach (object o in il) { Dictionary<string, object> it = o as Dictionary<string, object>; if (it != null) add.Add(it); }
                int n = lib.AddStream(add);
                if (Str(m, "silent") != "True")
                    Post(new { type = "toast", text = n > 0 ? (n == 1 ? "added to your collection" : n + " tracks added to your collection") : "already in your collection" });
                SendLibrary();
            }
            else if (cmd == "scUser")
            {
                string uid = Str(m, "id");
                ScCall((int)Num(m, "req"), delegate { return new[] { "user", sc.User(uid) }; });
            }
            else if (cmd == "scUserList")
            {
                string uid = Str(m, "id"), what = Str(m, "what");
                ScCall((int)Num(m, "req"), delegate { string k; string raw = sc.UserList(uid, what, out k); return new[] { k, raw }; });
            }
            else if (cmd == "scTrack")
            {
                string tid = Str(m, "id");
                ScCall((int)Num(m, "req"), delegate { return new[] { "trackinfo", sc.Track(tid) }; });
            }
            else if (cmd == "cacheSet")
            {
                double mb = Num(m, "mb");
                if (mb >= 50 && mb <= 20000) { scs.Limit = (long)(mb * 1024 * 1024); ThreadPool.QueueUserWorkItem(delegate { scs.TrimNow(); }); }
                Post(new { type = "cache", bytes = scs.Size(), limit = scs.Limit });
            }
            else if (cmd == "cacheInfo") Post(new { type = "cache", bytes = scs.Size(), limit = scs.Limit });
            else if (cmd == "cacheClear")
            {
                scs.Clear();
                Post(new { type = "cache", bytes = scs.Size(), limit = scs.Limit });
                Post(new { type = "toast", text = "cache cleared" });
            }
            else if (cmd == "scResolve")
            {
                string rid = Str(m, "id"), tc = Str(m, "tc"), au = Str(m, "auth");
                if (Regex.IsMatch(rid, "^[0-9]+$"))
                    ScCall((int)Num(m, "req"), delegate { return new[] { "resolve", scs.ResolveJson(rid, tc, au) }; });
            }
            else if (cmd == "scWarm")
            {
                string wid = Str(m, "id"), tc = Str(m, "tc"), au = Str(m, "auth"); int n = (int)Num(m, "n");
                if (Regex.IsMatch(wid, "^[0-9]+$")) ThreadPool.QueueUserWorkItem(delegate { scs.Warm(wid, tc, au, Math.Max(1, Math.Min(n, 4))); });
            }
            else if (cmd == "scRadio")
            {
                string rid = Str(m, "id");
                if (Regex.IsMatch(rid, "^[0-9]+$")) ScCall((int)Num(m, "req"), delegate { return new[] { "radio", sc.Related(rid) }; });
            }
            else if (cmd == "plSave")
            {
                object pls; m.TryGetValue("playlists", out pls);
                lib.SavePlaylists(pls, null);
            }
            else if (cmd == "updCheck") upd.CheckNow();
            else if (cmd == "notesSeen") upd.SeenNotes();
            else if (cmd == "updNotes") SendNotes(Str(m, "ver"));
            else if (cmd == "backupExport") { string ls = Str(m, "ls"); BeginInvoke((MethodInvoker)delegate { BackupExport(ls); }); }
            else if (cmd == "backupImport") BeginInvoke((MethodInvoker)BackupImport);
            else if (cmd == "fs") SetFullscreen(Str(m, "on") == "True");
            else if (cmd == "updCfg") upd.Configure(Str(m, "repo"), Str(m, "auto") == "True");
            else if (cmd == "updApply") BeginInvoke((MethodInvoker)ApplyUpdate);
            else if (cmd == "importData") BeginInvoke((MethodInvoker)ImportData);
            else if (cmd == "scPrefetch")
            {
                string pid = Str(m, "id");
                if (Regex.IsMatch(pid, "^[0-9]+$") && scs.Cached(pid) == null)
                {
                    Thread pt = new Thread(delegate() { try { scs.Ensure(pid); } catch { } });
                    pt.IsBackground = true; pt.Start();
                }
            }
            else if (cmd == "scSearch")
            {
                string q = Str(m, "q");
                ScCall((int)Num(m, "req"), delegate { return new[] { "search", sc.Search(q) }; });
            }
            else if (cmd == "scMore")
            {
                string next = Str(m, "next"); string kind = Str(m, "kind");
                ScCall((int)Num(m, "req"), delegate { return new[] { kind, sc.More(next) }; });
            }
            else if (cmd == "scTracks")
            {
                string[] ids = Str(m, "ids").Split(',');
                ScCall((int)Num(m, "req"), delegate { return new[] { "tracks", sc.Tracks(ids) }; });
            }
            else if (cmd == "scOpen")
            {
                string url = Str(m, "url");
                ScCall((int)Num(m, "req"), delegate { string k; string raw = sc.Open(url, out k); return new[] { k, raw }; });
            }

            else if (cmd == "listen") lib.Listen(Str(m, "id"), Num(m, "sec"), Str(m, "day"), (int)Num(m, "hour"));
            else if (cmd == "open")
            {
                string url = Str(m, "url");
                if (url.StartsWith("https://t.me/") || url.StartsWith("https://soundcloud.com/") || url.StartsWith("https://www.soundcloud.com/"))
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
            else if (cmd == "duration") lib.SetDuration(Str(m, "id"), Num(m, "dur"));
            else if (cmd == "remove") lib.Remove(Str(m, "id"));
        }

        static string Str(Dictionary<string, object> m, string k)
        {
            object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }
        static double Num(Dictionary<string, object> m, string k)
        {
            object v; if (!m.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }

        void Post(object o)
        {
            try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json.Serialize(o)); } catch { }
        }
        void SendLibrary() { Post(lib.Snapshot()); }

        void PickImage(string kind, string id)
        {
            string target;
            if (kind == "avatar") target = Path.Combine(Program.Data, "avatar.jpg");
            else { target = lib.PlCoverPath(id); if (target == null) return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = kind == "avatar" ? "pick an avatar" : "pick a playlist cover";
                d.Filter = "images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|all files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SquareJpeg(d.FileName, target, kind == "avatar" ? 400 : 600);
                    Post(new { type = "imageSet", kind = kind, id = id, v = Program.Now() });
                }
                catch (Exception ex) { Post(new { type = "toast", text = "can't open that image: " + ex.Message }); }
            }
        }

        // center-crop to a square, scale down, save as jpeg
        static void SquareJpeg(string src, string dst, int size)
        {
            using (Image img = Image.FromFile(src))
            {
                int side = Math.Min(img.Width, img.Height);
                Rectangle crop = new Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side);
                int outSize = Math.Min(size, side);
                using (Bitmap bmp = new Bitmap(outSize, outSize))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.Clear(Color.Black);
                        g.DrawImage(img, new Rectangle(0, 0, outSize, outSize), crop, GraphicsUnit.Pixel);
                    }
                    ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    EncoderParameters ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                    string tmp = dst + ".tmp";
                    bmp.Save(tmp, jpg, ep);
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                }
            }
        }

        void PickFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "add tracks";
                d.Multiselect = true;
                d.Filter = "Audio|*.mp3;*.m4a;*.aac;*.flac;*.wav;*.ogg;*.opus|All files|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) Import(new List<string>(d.FileNames));
            }
        }

        void PickFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "pick a folder with music, subfolders are included";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) == DialogResult.OK) Import(new List<string> { d.SelectedPath });
            }
        }

        async void Import(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            Post(new { type = "toast", text = "importing…" });
            int added = 0;
            try { added = await Task.Run(() => lib.Import(paths)); }
            catch (Exception ex) { Post(new { type = "toast", text = "import error: " + ex.Message }); }
            Post(new { type = "added", count = added });
            SendLibrary();
        }

        // ---------- window position ----------
        string BoundsFile { get { return Path.Combine(Program.Data, "window.txt"); } }
        void RestoreBounds_()
        {
            try
            {
                if (!File.Exists(BoundsFile)) return;
                string[] p = File.ReadAllText(BoundsFile).Split(',');
                Rectangle r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                bool visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r));
                if (!visible || r.Width < 400 || r.Height < 300) return;
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
                if (p.Length > 4 && p[4] == "1") WindowState = FormWindowState.Maximized;
            }
            catch { }
        }
        void SaveBounds()
        {
            try
            {
                Rectangle r = fsOn ? fsBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                bool mx = fsOn ? fsState == FormWindowState.Maximized : WindowState == FormWindowState.Maximized;
                File.WriteAllText(BoundsFile, r.X + "," + r.Y + "," + r.Width + "," + r.Height + "," + (mx ? "1" : "0"));
            }
            catch { }
        }
    }

    // ---------- dark tray menu ----------
    class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(22, 22, 24), Hi = Color.FromArgb(46, 46, 50), Ln = Color.FromArgb(58, 58, 62);
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color MenuItemSelected { get { return Hi; } }
        public override Color MenuItemBorder { get { return Hi; } }
        public override Color MenuBorder { get { return Ln; } }
        public override Color SeparatorDark { get { return Ln; } }
        public override Color SeparatorLight { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
    }
    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(236, 236, 236);
            base.OnRenderItemText(e);
        }
    }

    // ---------- library (emopunk_data\library.json + history.json) ----------
    class Track
    {
        public string id { get; set; }
        public string path { get; set; }
        public string title { get; set; }
        public string artist { get; set; }
        public string album { get; set; }
        public long size { get; set; }
        public double dur { get; set; }
        public int plays { get; set; }
        public long added { get; set; }
        public long last { get; set; }
        public bool cover { get; set; }
        public double secs { get; set; }
        public string sc { get; set; }
        public string scUrl { get; set; }
        public string art { get; set; }
    }

    class Hist
    {
        public string id { get; set; }
        public long t { get; set; }
    }

    class Stats
    {
        public Dictionary<string, double> days { get; set; }
        public double[] hours { get; set; }
        public double total { get; set; }
        public double[] week { get; set; }      // 7 x 24: [weekday * 24 + hour], sunday = 0
    }

    class Library
    {
        static readonly string[] Exts = { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus" };
        readonly string file, histFile, statsFile, plFile, covers, plCovers;
        object plData;
        Stats stats = new Stats { days = new Dictionary<string, double>(), hours = new double[24], total = 0 };
        DateTime lastSave = DateTime.MinValue;
        readonly object gate = new object();
        List<Track> tracks = new List<Track>();
        List<Hist> history = new List<Hist>();
        readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Library(string data)
        {
            file = Path.Combine(data, "library.json");
            histFile = Path.Combine(data, "history.json");
            statsFile = Path.Combine(data, "stats.json");
            plFile = Path.Combine(data, "playlists.json");
            plCovers = Path.Combine(data, "pcovers");
            Directory.CreateDirectory(plCovers);
            try { if (File.Exists(plFile)) plData = js.DeserializeObject(File.ReadAllText(plFile, Encoding.UTF8)); } catch { try { File.Copy(plFile, plFile + ".broken", true); } catch { } }
            covers = Path.Combine(data, "covers");
            Directory.CreateDirectory(covers);
            try
            {
                if (File.Exists(file))
                {
                    List<Track> l = js.Deserialize<List<Track>>(File.ReadAllText(file, Encoding.UTF8));
                    if (l != null) tracks = l.Where(t => t != null && !string.IsNullOrEmpty(t.id)).ToList();
                }
            }
            catch { try { File.Copy(file, file + ".broken", true); } catch { } }
            try
            {
                if (File.Exists(histFile))
                {
                    List<Hist> h = js.Deserialize<List<Hist>>(File.ReadAllText(histFile, Encoding.UTF8));
                    if (h != null) history = h;
                }
            }
            catch { }
            bool haveStats = false;
            try
            {
                if (File.Exists(statsFile))
                {
                    Stats st = js.Deserialize<Stats>(File.ReadAllText(statsFile, Encoding.UTF8));
                    if (st != null)
                    {
                        if (st.days == null) st.days = new Dictionary<string, double>();
                        if (st.hours == null || st.hours.Length != 24) st.hours = new double[24];
                        stats = st; haveStats = true;
                        if (st.week == null || st.week.Length != 168) SeedWeek();
                    }
                }
            }
            catch { }
            if (!haveStats && history.Count > 0) SeedStats();
        }

        void WriteJson(string path, object o)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, js.Serialize(o), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }
        void Save() { WriteJson(file, tracks); }

        // the weekday map is new: rebuild it roughly from the play history
        void SeedWeek()
        {
            stats.week = new double[168];
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (Hist h in history)
            {
                Track t = Find(h.id);
                double sec = t != null && t.dur > 0 ? Math.Min(t.dur, 600) : 150;
                DateTime d = epoch.AddMilliseconds(h.t).ToLocalTime();
                stats.week[(int)d.DayOfWeek * 24 + d.Hour] += sec;
            }
            WriteJson(statsFile, stats);
        }

        // first run after the update: estimate listening time from older play history
        void SeedStats()
        {
            stats.week = new double[168];
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (Hist h in history)
            {
                Track t = Find(h.id);
                double sec = t != null && t.dur > 0 ? Math.Min(t.dur, 600) : 150;
                DateTime d = epoch.AddMilliseconds(h.t).ToLocalTime();
                string k = d.ToString("yyyy-MM-dd");
                double v; stats.days.TryGetValue(k, out v); stats.days[k] = v + sec;
                stats.hours[d.Hour] += sec;
                stats.week[(int)d.DayOfWeek * 24 + d.Hour] += sec;
                stats.total += sec;
                if (t != null) t.secs += sec;
            }
            WriteJson(statsFile, stats);
            Save();
        }

        public void Listen(string id, double sec, string day, int hour)
        {
            if (sec <= 0 || sec > 600 || string.IsNullOrEmpty(day) || day.Length != 10) return;
            if (hour < 0 || hour > 23) hour = DateTime.Now.Hour;
            lock (gate)
            {
                Track t = Find(id); if (t != null) t.secs += sec;
                double v; stats.days.TryGetValue(day, out v); stats.days[day] = v + sec;
                stats.hours[hour] += sec;
                if (stats.week == null || stats.week.Length != 168) stats.week = new double[168];
                DateTime dd;
                if (DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dd))
                    stats.week[(int)dd.DayOfWeek * 24 + hour] += sec;
                stats.total += sec;
                WriteJson(statsFile, stats);
                if ((DateTime.Now - lastSave).TotalSeconds > 60) { Save(); lastSave = DateTime.Now; }
            }
        }

        public void SavePlaylists(object playlists, object profile)
        {
            lock (gate)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["playlists"] = playlists ?? new object[0];
                d["profile"] = profile ?? new Dictionary<string, object>();
                plData = d;
                WriteJson(plFile, d);
                // drop covers of deleted playlists
                try
                {
                    HashSet<string> keep = new HashSet<string>();
                    System.Collections.IEnumerable list = playlists as System.Collections.IEnumerable;
                    if (list != null)
                        foreach (object o in list)
                        {
                            Dictionary<string, object> pd = o as Dictionary<string, object>;
                            object idv; if (pd != null && pd.TryGetValue("id", out idv) && idv != null) keep.Add(idv.ToString() + ".jpg");
                        }
                    foreach (string f in Directory.GetFiles(plCovers, "*.jpg"))
                        if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
                }
                catch { }
            }
        }

        public string PlCoverPath(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40) return null;
            foreach (char ch in id) if (!Uri.IsHexDigit(ch)) return null;
            return Path.Combine(plCovers, id + ".jpg");
        }

        // a finished download: if it came from soundcloud and that track is already in the collection,
        // the same entry just becomes offline (plays and stats stay with it)
        public string ImportOne(string path, string scId)
        {
            if (!string.IsNullOrEmpty(scId))
            {
                lock (gate)
                {
                    Track ex = tracks.FirstOrDefault(t => t.sc == scId);
                    if (ex != null)
                    {
                        if (string.IsNullOrEmpty(ex.path) || !File.Exists(ex.path))
                        {
                            ex.path = Path.GetFullPath(path);
                            ex.size = new FileInfo(path).Length;
                            try { Track tmp = new Track(); Tags.Read(path, tmp, CoverPath(ex.id)); if (tmp.cover) ex.cover = true; } catch { }
                            Save();
                        }
                        return ex.id;
                    }
                }
            }
            Import(new List<string> { path });
            string id = IdOf(path);
            lock (gate)
            {
                Track t = Find(id);
                if (t == null) return null;
                if (!string.IsNullOrEmpty(scId) && string.IsNullOrEmpty(t.sc)) { t.sc = scId; Save(); }
                return id;
            }
        }

        public int AddStream(List<Dictionary<string, object>> items)
        {
            int n = 0; long now = Program.Now();
            lock (gate)
            {
                foreach (Dictionary<string, object> it in items)
                {
                    object v;
                    string scId = it.TryGetValue("scId", out v) && v != null ? v.ToString() : "";
                    if (!System.Text.RegularExpressions.Regex.IsMatch(scId, "^[0-9]+$")) continue;
                    if (tracks.Any(t => t.sc == scId)) continue;
                    Track tr = new Track
                    {
                        id = "sc" + scId, path = "", sc = scId,
                        scUrl = it.TryGetValue("url", out v) && v != null ? v.ToString() : "",
                        title = it.TryGetValue("title", out v) && v != null ? v.ToString() : "untitled",
                        artist = it.TryGetValue("artist", out v) && v != null ? v.ToString() : "",
                        art = it.TryGetValue("art", out v) && v != null ? v.ToString() : "",
                        added = now - n
                    };
                    double d; if (it.TryGetValue("dur", out v) && v != null && double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) tr.dur = d;
                    tracks.Add(tr);
                    n++;
                }
                if (n > 0) Save();
            }
            return n;
        }

        public void Flush() { lock (gate) { Save(); WriteJson(statsFile, stats); } }

        public object Snapshot()
        {
            lock (gate)
            {
                long since = Program.Now() - 9L * 86400000L;
                return new
                {
                    type = "library",
                    tracks = tracks.Select(t => new
                    {
                        id = t.id, title = t.title, artist = t.artist, size = t.size, dur = t.dur,
                        plays = t.plays, added = t.added, last = t.last, cover = t.cover, secs = t.secs,
                        sc = t.sc, art = t.art, scUrl = t.scUrl, stream = string.IsNullOrEmpty(t.path),
                        missing = string.IsNullOrEmpty(t.path) ? string.IsNullOrEmpty(t.sc) : !File.Exists(t.path)
                    }).ToList(),
                    stats = stats,
                    playlists = PlField("playlists"),
                    profile = PlField("profile"),
                    history = history.Where(h => h.t > since).ToList()
                };
            }
        }

        object PlField(string k)
        {
            Dictionary<string, object> d = plData as Dictionary<string, object>;
            object v; if (d != null && d.TryGetValue(k, out v)) return v;
            return null;
        }

        Track Find(string id) { return tracks.FirstOrDefault(t => t.id == id); }
        public string PathOf(string id) { lock (gate) { Track t = Find(id); return t == null ? null : t.path; } }
        public string CoverPath(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40) return null;
            foreach (char ch in id) if (ch > 127 || !char.IsLetterOrDigit(ch)) return null;
            return Path.Combine(covers, id + ".jpg");
        }

        public void Played(string id)
        {
            lock (gate)
            {
                Track t = Find(id); if (t == null) return;
                t.plays++; t.last = Program.Now(); Save();
                history.Add(new Hist { id = id, t = t.last });
                if (history.Count > 50000) history.RemoveRange(0, history.Count - 50000);
                WriteJson(histFile, history);
            }
        }
        public void SetDuration(string id, double d)
        {
            lock (gate) { Track t = Find(id); if (t == null || d <= 0 || Math.Abs(t.dur - d) < 1) return; t.dur = d; Save(); }
        }
        public void Remove(string id)
        {
            lock (gate)
            {
                Track t = Find(id); if (t == null) return;
                tracks.Remove(t);
                try { string c = CoverPath(id); if (c != null && File.Exists(c)) File.Delete(c); } catch { }
                Save();
            }
        }

        public static string IdOf(string path)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static bool IsAudio(string f) { return Exts.Contains(Path.GetExtension(f).ToLowerInvariant()); }

        static void Walk(string dir, List<string> outp, int depth)
        {
            if (depth > 12) return;
            try { foreach (string f in Directory.GetFiles(dir)) if (IsAudio(f)) outp.Add(f); } catch { }
            try { foreach (string d in Directory.GetDirectories(dir)) Walk(d, outp, depth + 1); } catch { }
        }

        public int Import(List<string> paths)
        {
            List<string> files = new List<string>();
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) { List<string> sub = new List<string>(); Walk(p, sub, 0); sub.Sort(StringComparer.OrdinalIgnoreCase); files.AddRange(sub); }
                else if (File.Exists(p) && IsAudio(p)) files.Add(p);
            }

            List<Track> fresh = new List<Track>();
            HashSet<string> seen;
            lock (gate) seen = new HashSet<string>(tracks.Select(t => t.id));
            long now = Program.Now();
            int i = 0;
            foreach (string f in files)
            {
                string id = IdOf(f);
                if (seen.Contains(id)) continue;
                seen.Add(id);
                Track t = new Track { id = id, path = Path.GetFullPath(f), size = new FileInfo(f).Length, added = now - i };
                i++;
                try { Tags.Read(f, t, CoverPath(id)); } catch { }
                string bare = Path.GetFileNameWithoutExtension(f);
                int dash = bare.LastIndexOf(" - ");
                if (string.IsNullOrEmpty(t.title)) t.title = dash > 0 ? bare.Substring(0, dash) : bare;
                if (string.IsNullOrEmpty(t.artist)) t.artist = dash > 0 ? bare.Substring(dash + 3) : "Unknown";
                fresh.Add(t);
            }
            if (fresh.Count == 0) return 0;
            lock (gate) { tracks.AddRange(fresh); Save(); }
            return fresh.Count;
        }
    }

    // ---------- ID3v2.3 / 2.4 reader: title, artist, album, cover ----------
    static class Tags
    {
        static int Syncsafe(byte[] b, int o) { return (b[o] << 21) | (b[o + 1] << 14) | (b[o + 2] << 7) | b[o + 3]; }
        static int BE(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        static bool IsFrameId(byte[] b, int o)
        {
            for (int i = 0; i < 4; i++) { byte c = b[o + i]; if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false; }
            return true;
        }

        static string Text(byte[] b, int start, int len)
        {
            if (len < 2) return null;
            int enc = b[start];
            int o = start + 1, n = len - 1;
            string s;
            if (enc == 0) s = Encoding.GetEncoding(28591).GetString(b, o, n);
            else if (enc == 3) s = Encoding.UTF8.GetString(b, o, n);
            else if (enc == 2) s = Encoding.BigEndianUnicode.GetString(b, o, n - (n % 2));
            else
            {
                if (n >= 2 && b[o] == 0xFE && b[o + 1] == 0xFF) { o += 2; n -= 2; s = Encoding.BigEndianUnicode.GetString(b, o, n - (n % 2)); }
                else { if (n >= 2 && b[o] == 0xFF && b[o + 1] == 0xFE) { o += 2; n -= 2; } s = Encoding.Unicode.GetString(b, o, n - (n % 2)); }
            }
            s = s.TrimEnd('\0').Replace('\0', '/').Trim();
            return s.Length == 0 ? null : s;
        }

        public static void Read(string path, Track t, string coverOut)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] h = new byte[10];
                if (fs.Read(h, 0, 10) < 10) return;
                if (h[0] != 'I' || h[1] != 'D' || h[2] != '3') return;
                int v = h[3];
                if (v < 3) return;
                int size = Syncsafe(h, 6);
                if (size <= 10 || size > 64 * 1024 * 1024) return;
                byte[] b = new byte[size];
                int r = 0;
                while (r < size) { int n = fs.Read(b, r, size - r); if (n <= 0) break; r += n; }

                int p = 0;
                if ((h[5] & 0x40) != 0) p = v == 4 ? Syncsafe(b, 0) : BE(b, 0) + 4;
                while (p >= 0 && p + 10 <= r)
                {
                    if (!IsFrameId(b, p)) break;
                    string id = Encoding.ASCII.GetString(b, p, 4);
                    int fsz = v == 4 ? Syncsafe(b, p + 4) : BE(b, p + 4);
                    int start = p + 10;
                    if (fsz <= 0 || start + fsz > r) break;

                    if (id == "TIT2") t.title = Text(b, start, fsz);
                    else if (id == "TPE1") t.artist = Text(b, start, fsz);
                    else if (id == "TALB") t.album = Text(b, start, fsz);
                    else if (id == "APIC" && !t.cover && coverOut != null)
                    {
                        int end = start + fsz, enc = b[start], q = start + 1;
                        while (q < end && b[q] != 0) q++;   // mime
                        q += 2;                              // terminator + picture type
                        if (enc == 1 || enc == 2) { while (q + 1 < end && !(b[q] == 0 && b[q + 1] == 0)) q += 2; q += 2; }
                        else { while (q < end && b[q] != 0) q++; q++; }
                        if (q < end - 16)
                        {
                            File.WriteAllBytes(coverOut, b.Skip(q).Take(end - q).ToArray());
                            t.cover = true;
                        }
                    }
                    p = start + fsz;
                }
            }
        }
    }
}
