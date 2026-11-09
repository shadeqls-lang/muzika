// muzika — log in to soundcloud in a small window with the real site; keeps only the login token.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Emopunk
{
    class ScLoginForm : Form
    {
        readonly string profileDir;
        readonly bool fresh;
        readonly WebView2 wv;
        readonly Timer poll;
        public string Token;

        // own browser profile, separate from the player window
        public ScLoginForm(string profileFolder, bool startFresh, Icon icon)
        {
            profileDir = profileFolder;
            fresh = startFresh;
            Text = "log in to soundcloud";
            Icon = icon;
            BackColor = Color.FromArgb(18, 18, 18);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(560, 800);
            MinimumSize = new Size(420, 560);
            ShowInTaskbar = false;
            wv = new WebView2();
            wv.Dock = DockStyle.Fill;
            wv.DefaultBackgroundColor = Color.FromArgb(18, 18, 18);
            Controls.Add(wv);
            poll = new Timer();
            poll.Interval = 1500;
            poll.Tick += delegate { Check(); };
            Load += OnLoad;
            FormClosed += delegate { poll.Stop(); poll.Dispose(); };
        }

        async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, profileDir, null);
                await wv.EnsureCoreWebView2Async(env);
                if (fresh) { try { wv.CoreWebView2.CookieManager.DeleteAllCookies(); } catch { } }
                // a plain chrome user agent: some sign-in pages refuse anything that looks embedded
                try { wv.CoreWebView2.Settings.UserAgent = Net.UA; } catch { }
                wv.CoreWebView2.NavigationCompleted += delegate { Check(); };
                wv.CoreWebView2.Navigate("https://soundcloud.com/signin");
                poll.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "couldn't open the login page:\n" + ex.Message, "muzika");
                Close();
            }
        }

        async void Check()
        {
            if (Token != null || wv.CoreWebView2 == null) return;
            try
            {
                List<CoreWebView2Cookie> cookies = await wv.CoreWebView2.CookieManager.GetCookiesAsync("https://soundcloud.com");
                foreach (CoreWebView2Cookie c in cookies)
                {
                    if (c.Name == "oauth_token" && !string.IsNullOrEmpty(c.Value) && c.Value.Length > 10)
                    {
                        Token = c.Value;
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }
                }
            }
            catch { }
        }

    }
}
