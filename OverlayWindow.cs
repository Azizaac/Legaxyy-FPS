using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OverlayDataBridge
{
    public class OverlayWindow : Form
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_TOGGLE_SHOW = 9001;
        private const uint VK_F11 = 0x7A; // F11: Toggle Dashboard Window

        private readonly WebView2 _webView;
        private readonly string _url;
        private bool _isClosing = false;

        public event Action<bool>? VisibilityChanged;

        public OverlayWindow(int httpPort = 8766)
        {
            _url = $"http://127.0.0.1:{httpPort}";

            this.Text = "LegaxyyFPS - Performance Dashboard";
            this.Width = 1280;
            this.Height = 720;
            this.MinimumSize = new Size(800, 480);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(6, 8, 16);
            this.DoubleBuffered = true;

            // Load app icon if available
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    var icon = Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null) this.Icon = icon;
                }
            }
            catch { }

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(6, 8, 16)
            };
            this.Controls.Add(_webView);

            InitializeAsync();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                RegisterHotKey(this.Handle, HOTKEY_TOGGLE_SHOW, 0, VK_F11);
            }
            catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                UnregisterHotKey(this.Handle, HOTKEY_TOGGLE_SHOW);
            }
            catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_TOGGLE_SHOW)
            {
                ToggleVisibility();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isClosing && e.CloseReason == CloseReason.UserClosing)
            {
                // User clicked [X]: hide the window instead of destroying, so it can be re-shown instantly
                e.Cancel = true;
                this.Hide();
                VisibilityChanged?.Invoke(false);
                return;
            }
            base.OnFormClosing(e);
        }

        public void ForceClose()
        {
            _isClosing = true;
            this.Close();
        }

        public void ToggleVisibility()
        {
            if (this.Visible)
            {
                this.Hide();
                VisibilityChanged?.Invoke(false);
            }
            else
            {
                this.Show();
                if (this.WindowState == FormWindowState.Minimized)
                    this.WindowState = FormWindowState.Normal;
                this.Activate();
                VisibilityChanged?.Invoke(true);
            }
        }

        private async void InitializeAsync()
        {
            try
            {
                var chromiumArgs = "--disable-background-timer-throttling " +
                                   "--disable-backgrounding-occluded-windows " +
                                   "--disable-renderer-backgrounding " +
                                   "--disable-features=Translate,MediaRouter,OptimizationHints,DialMediaRouteProvider " +
                                   "--disable-component-update " +
                                   "--no-pings";

                var options = new CoreWebView2EnvironmentOptions(chromiumArgs);
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegaxyyFPS", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsStatusBarEnabled = false;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = false;
                settings.IsBuiltInErrorPageEnabled = true;

                _webView.CoreWebView2.Navigate(_url);
            }
            catch { }
        }
    }
}
