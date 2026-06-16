// Form1.cs  –  DS2 Tank Editor with WebView2 frontend
// NuGet: Microsoft.Web.WebView2

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DS2_Tank_Viewer
{
    public partial class DS2TankEditor : Form
    {
        private WebView2 webView;
        private TankReader currentTank = null;
        private string currentTankFilePath = "";

        public DS2TankEditor()
        {
            InitializeComponent();
            InitWebView();
        }

        // ── WebView2 Setup ────────────────────────────────────────────────────

        private async void InitWebView()
        {
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 17, 23) // matches --bg
            };
            this.Controls.Add(webView);
            this.Controls.SetChildIndex(webView, 0); // behind any designer controls

            // Environment — optional: set a user data folder
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Path.GetTempPath(), "DS2TankEditor_WebView2"));
            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true; // set false for release
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Load the embedded HTML
            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ds2_tank_editor.html");
            if (File.Exists(htmlPath))
                webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            else
                webView.CoreWebView2.NavigateToString(GetEmbeddedHtml()); // fallback to embedded resource

            // Listen for messages from JavaScript
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }

        // Call this to get the HTML if you prefer to embed it as a resource
        private string GetEmbeddedHtml()
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("DS2_Tank_Viewer.Resources.ds2_tank_editor.html");
            if (stream == null) return "<h1>HTML resource missing</h1>";
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }

        // ── Message Router (JS → C#) ─────────────────────────────────────────

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // WebMessageAsJson works for both postMessage(object) and postMessage(string).
            // TryGetWebMessageAsString() throws an ArgumentException when the JS side
            // sends an object literal via postMessage({...}) — use WebMessageAsJson instead.
            var raw = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var cmd = root.GetProperty("cmd").GetString();

            switch (cmd)
            {
                case "loadTank": await HandleLoadTank(); break;
                case "extractAll": await HandleExtractAll(); break;
                case "extractSelected":
                    await HandleExtractSelected(
                                            root.TryGetProperty("selectedPath", out var sp)
                                                ? sp.GetString() : null); break;
                case "createTankRtc": await HandleCreateTankRtc(root); break;
                case "browseSourceDir": HandleBrowseSourceDir(); break;
                case "browseOutFile": HandleBrowseOutFile(); break;
                case "previewFile":     /* add preview logic here */     break;
            }
        }

        // ── Send helper: C# → JS ─────────────────────────────────────────────

        private void JsCall(string js)
        {
            if (webView?.CoreWebView2 == null) return;
            if (this.InvokeRequired)
                this.Invoke(() => webView.CoreWebView2.ExecuteScriptAsync(js));
            else
                _ = webView.CoreWebView2.ExecuteScriptAsync(js);
        }

        private void ShowToast(string type, string title, string msg)
            => JsCall($"window.ds2.showToast({Json(type)},{Json(title)},{Json(msg)})");

        private static string Json(string s) => JsonSerializer.Serialize(s);

        // ── Load Tank ────────────────────────────────────────────────────────

        private async Task HandleLoadTank()
        {
            // Must hop to UI thread for dialogs
            OpenFileDialog ofd = null;
            DialogResult dr = DialogResult.Cancel;

            this.Invoke(() =>
            {
                ofd = new OpenFileDialog { Filter = "DS2 Resource Files (*.ds2res)|*.ds2res" };
                dr = ofd.ShowDialog(this);
            });

            if (dr != DialogResult.OK) return;

            try
            {
                currentTank = new TankReader();
                currentTank.Load(ofd.FileName);
                currentTankFilePath = ofd.FileName;

                var fileList = currentTank.GetFileList();
                string jsonPaths = JsonSerializer.Serialize(fileList);

                JsCall($"window.ds2.populateTree({jsonPaths}, {currentTank.FileCount})");
                JsCall($"window.ds2.setFileName({Json(Path.GetFileName(ofd.FileName))})");
            }
            catch (Exception ex)
            {
                ShowToast("error", "Load Failed", ex.Message);
            }
        }

        // ── Extract All ──────────────────────────────────────────────────────

        private async Task HandleExtractAll()
        {
            if (currentTank == null) { ShowToast("warning", "No Tank", "Load a tank file first."); return; }

            FolderBrowserDialog fbd = null;
            DialogResult dr = DialogResult.Cancel;

            this.Invoke(() =>
            {
                fbd = new FolderBrowserDialog { Description = "Choose output folder" };
                dr = fbd.ShowDialog(this);
            });

            if (dr != DialogResult.OK) return;

            try
            {
                JsCall($"window.ds2.setProgress(5, 'Extracting…')");
                await Task.Run(() => currentTank.ExtractAll(fbd.SelectedPath));
                JsCall($"window.ds2.setProgress(100, 'Done')");
                ShowToast("success", "Extracted", $"All files extracted to {fbd.SelectedPath}");
            }
            catch (Exception ex)
            {
                ShowToast("error", "Extract Failed", ex.Message);
            }
        }

        // ── Extract Selected ─────────────────────────────────────────────────

        private async Task HandleExtractSelected(string selectedPath)
        {
            if (currentTank == null) { ShowToast("warning", "No Tank", "Load a tank file first."); return; }
            if (string.IsNullOrEmpty(selectedPath)) { ShowToast("warning", "Nothing Selected", "Click a file or folder in the tree first."); return; }

            FolderBrowserDialog fbd = null;
            DialogResult dr = DialogResult.Cancel;

            this.Invoke(() =>
            {
                fbd = new FolderBrowserDialog { Description = "Choose output folder" };
                dr = fbd.ShowDialog(this);
            });

            if (dr != DialogResult.OK) return;

            try
            {
                int count = await Task.Run(() => currentTank.ExtractSelected(selectedPath, fbd.SelectedPath));
                ShowToast("success", "Extracted", $"{count} file{(count == 1 ? "" : "s")} extracted to {fbd.SelectedPath}");
            }
            catch (Exception ex)
            {
                ShowToast("error", "Extract Failed", ex.Message);
            }
        }

        // ── Browse dialogs (triggered from modal) ────────────────────────────

        private void HandleBrowseSourceDir()
        {
            FolderBrowserDialog fbd = null;
            DialogResult dr = DialogResult.Cancel;

            this.Invoke(() =>
            {
                fbd = new FolderBrowserDialog { Description = "Select source folder" };
                dr = fbd.ShowDialog(this);
            });

            if (dr == DialogResult.OK)
                JsCall($"window.ds2.setBrowsedSource({Json(fbd.SelectedPath)})");
        }

        private void HandleBrowseOutFile()
        {
            SaveFileDialog sfd = null;
            DialogResult dr = DialogResult.Cancel;

            this.Invoke(() =>
            {
                sfd = new SaveFileDialog
                {
                    Filter = "Tank Files (*.ds2res;*.ds2map)|*.ds2res;*.ds2map|All files (*.*)|*.*",
                    Title = "Save Tank File"
                };
                dr = sfd.ShowDialog(this);
            });

            if (dr == DialogResult.OK)
                JsCall($"window.ds2.setBrowsedOut({Json(sfd.FileName)})");
        }

        // ── Create Tank via RTC ──────────────────────────────────────────────

        private async Task HandleCreateTankRtc(JsonElement root)
        {
            string sourceDir = root.GetProperty("source").GetString();
            string outFile = root.GetProperty("out").GetString();
            string title = root.GetProperty("title").GetString();
            string author = root.GetProperty("author").GetString();
            string copyright = root.GetProperty("copyright").GetString();
            string build = root.GetProperty("build").GetString();
            string priorityStr = root.GetProperty("priority").GetString();
            bool dev = root.GetProperty("flagDev").GetBoolean();
            bool mpXfer = root.GetProperty("flagMpXfer").GetBoolean();
            bool protect = root.GetProperty("flagProtected").GetBoolean();
            bool wait = root.GetProperty("flagWait").GetBoolean();

            // Parse priority
            uint priority = 0x4000;
            if (!string.IsNullOrEmpty(priorityStr))
            {
                if (priorityStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    uint.TryParse(priorityStr[2..], System.Globalization.NumberStyles.HexNumber, null, out priority);
                else
                    uint.TryParse(priorityStr, out priority);
            }

            string rtcPath = ExtractRtcExe();
            if (string.IsNullOrEmpty(rtcPath))
            {
                JsCall($"window.ds2.onRtcError('Failed to extract RTC.exe')");
                return;
            }

            var args = new List<string>
            {
                $"-source \"{sourceDir}\"",
                $"-out \"{outFile}\""
            };
            if (!string.IsNullOrEmpty(title)) args.Add($"-title \"{title}\"");
            if (!string.IsNullOrEmpty(author)) args.Add($"-author \"{author}\"");
            if (!string.IsNullOrEmpty(copyright)) args.Add($"-copyright \"{copyright}\"");
            if (!string.IsNullOrEmpty(build)) args.Add($"-build \"{build}\"");
            args.Add($"-priority {priority}");
            if (dev) args.Add("-flagdev");
            if (mpXfer) args.Add("-flagmpxfer");
            if (protect) args.Add("-flagprotected");
            if (wait) args.Add("-waitonexit");

            JsCall($"window.ds2.setProgress(30, 'Running RTC.exe…')");

            var psi = new ProcessStartInfo
            {
                FileName = rtcPath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using var process = Process.Start(psi);
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                JsCall($"window.ds2.setProgress(80, 'Patching header…')");

                if (process.ExitCode == 0)
                {
                    PatchToDs2(outFile);
                    JsCall($"window.ds2.setProgress(100, 'Complete!')");
                    JsCall($"window.ds2.onRtcSuccess('Tank created and patched: {Json(Path.GetFileName(outFile))[1..^1]}')");
                }
                else
                {
                    JsCall($"window.ds2.onRtcError({Json(error.Trim())})");
                }
            }
            catch (Exception ex)
            {
                JsCall($"window.ds2.onRtcError({Json(ex.Message)})");
            }

            CleanupRtcExe();
        }

        // ── DS2 Patch ────────────────────────────────────────────────────────

        private static void PatchToDs2(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            bool modified = false;

            if (data.Length >= 4 && data[0] == 'D' && data[1] == 'S' && data[2] == 'i' && data[3] == 'g')
            {
                data[2] = (byte)'g';
                data[3] = (byte)'2';
                modified = true;
            }

            if (data.Length >= 12)
            {
                data[8] = 0x00; data[9] = 0x01; data[10] = 0x01; data[11] = 0x00;
                modified = true;
            }

            if (data.Length > 54 && data[54] != 0x00)
            {
                data[54] = 0x00;
                modified = true;
            }

            if (modified) File.WriteAllBytes(filePath, data);
        }

        // ── RTC.exe helpers ──────────────────────────────────────────────────

        private static string ExtractRtcExe()
        {
            string exePath = Path.Combine(Path.GetTempPath(), "RTC.exe");
            if (!File.Exists(exePath))
            {
                using var resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("DS2_Tank_Viewer.Resources.RTC.exe");
                if (resource == null) return null;
                using var output = new FileStream(exePath, FileMode.Create, FileAccess.Write);
                resource.CopyTo(output);
            }
            return exePath;
        }

        private static void CleanupRtcExe()
        {
            string exePath = Path.Combine(Path.GetTempPath(), "RTC.exe");
            if (File.Exists(exePath)) File.Delete(exePath);
        }

        // ── Designer stubs (keep if you had designer-generated code) ─────────

        private void InitializeComponent()
        {
            this.Text = "DS2 Tank Editor";
            this.Size = new System.Drawing.Size(900, 600);
            this.MinimumSize = new System.Drawing.Size(700, 480);
            this.BackColor = System.Drawing.Color.FromArgb(15, 17, 23);
            this.StartPosition = FormStartPosition.CenterScreen;
        }
    }
}