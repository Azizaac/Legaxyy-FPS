using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace OverlayDataBridge
{
    public class OverlayWindow : Form
    {
        private WebView2 _webView;
        private string _url = "http://127.0.0.1:8766";

        public OverlayWindow()
        {
            this.Text = "Overlay Native Window";
            this.Width = 1280;
            this.Height = 720;
            this.FormBorderStyle = FormBorderStyle.Sizable; // User can resize it and it scales!
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.Black;
            // Removed TopMost so they can put it on a second monitor behind other things if they want.

            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };
            this.Controls.Add(_webView);

            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            // Disable background throttling in Chromium to prevent freezing when minimized or occluded
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding"
            );
            string userDataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegaxyyFPS", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            
            await _webView.EnsureCoreWebView2Async(environment);
            
            _webView.CoreWebView2.Navigate(_url);
        }
    }
}
