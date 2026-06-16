// Form1_WebView2.cs  –  DS2 Tank Editor · IDE Edition
// NuGet required: Microsoft.Web.WebView2

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DS2_Tank_Viewer
{
    public partial class DS2TankEditor : Form
    {
        // ── Fields ───────────────────────────────────────────────────────────

        private WebView2 webView;
        private TankReader currentTank = null;
        private string currentTankFilePath = "";

        // Projects: key = project GUID, value = root folder path
        private readonly Dictionary<string, string> _projects = new();

        // Text extensions we allow opening in the editor
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gas", ".skrit", ".gs", ".template", ".cfg", ".ini",
            ".txt", ".log", ".bat", ".lua", ".xml", ".json", ".md"
        };

        // ── Constructor ──────────────────────────────────────────────────────

        public DS2TankEditor()
        {
            InitializeComponent();
            InitWebView();
        }

        // ── WebView2 Setup ───────────────────────────────────────────────────

        private async void InitWebView()
        {
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 17, 23)
            };
            this.Controls.Add(webView);
            this.Controls.SetChildIndex(webView, 0);

            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Path.GetTempPath(), "DS2TankEditor_WebView2"));
            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true; // flip false for release
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ds2_tank_editor.html");
            if (File.Exists(htmlPath))
                webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            else
                webView.CoreWebView2.NavigateToString(GetEmbeddedHtml());

            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }

        private string GetEmbeddedHtml()
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("DS2_Tank_Viewer.Resources.ds2_tank_editor.html");
            if (stream == null) return "<h1>HTML resource missing</h1>";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // ── Message Router (JS → C#) ─────────────────────────────────────────

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Always use WebMessageAsJson — TryGetWebMessageAsString throws for object literals
            var raw = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var cmd = root.GetProperty("cmd").GetString();

            switch (cmd)
            {
                // ── Tank ──
                case "loadTank":
                    await HandleLoadTank();
                    break;
                case "extractAll":
                    await HandleExtractAll();
                    break;
                case "extractSelected":
                    await HandleExtractSelected(
                        root.TryGetProperty("selectedPath", out var sp) ? sp.GetString() : null);
                    break;

                // ── Editor ──
                case "openFile":
                    await HandleOpenFile(
                        root.GetProperty("path").GetString(),
                        root.TryGetProperty("source", out var src) ? src.GetString() : "tank",
                        root.TryGetProperty("projectId", out var pid) ? pid.GetString() : null);
                    break;
                case "saveFile":
                    await HandleSaveFile(
                        root.GetProperty("path").GetString(),
                        root.GetProperty("content").GetString());
                    break;
                case "newFile":
                    HandleNewFile(
                        root.GetProperty("folderPath").GetString(),
                        root.TryGetProperty("projectId", out var npid) ? npid.GetString() : null);
                    break;
                case "deleteFile":
                    HandleDeleteFile(
                        root.GetProperty("path").GetString(),
                        root.TryGetProperty("projectId", out var dpid) ? dpid.GetString() : null);
                    break;

                // ── Projects ──
                case "addProject":
                    HandleAddProject();
                    break;
                case "removeProject":
                    HandleRemoveProject(root.GetProperty("projectId").GetString());
                    break;
                case "refreshProjects":
                    HandleRefreshProjects();
                    break;

                // ── Shell ──
                case "revealInExplorer":
                    HandleRevealInExplorer(root.GetProperty("path").GetString());
                    break;

                // ── RTC / Pack ──
                case "createTankRtc":
                    await HandleCreateTankRtc(root);
                    break;
                case "browseSourceDir":
                    HandleBrowseSourceDir();
                    break;
                case "browseOutFile":
                    HandleBrowseOutFile();
                    break;
            }
        }

        // ── C# → JS helpers ─────────────────────────────────────────────────

        private void JsCall(string js)
        {
            if (webView?.CoreWebView2 == null) return;
            if (this.InvokeRequired)
                this.Invoke(() => _ = webView.CoreWebView2.ExecuteScriptAsync(js));
            else
                _ = webView.CoreWebView2.ExecuteScriptAsync(js);
        }

        private void ShowToast(string type, string title, string msg)
            => JsCall($"window.ds2.showToast({J(type)},{J(title)},{J(msg)})");

        // Safely JSON-encode a string for inline JS
        private static string J(string s) => JsonSerializer.Serialize(s ?? "");

        // ════════════════════════════════════════════════════════════════════
        // TANK HANDLERS
        // ════════════════════════════════════════════════════════════════════

        private async Task HandleLoadTank()
        {
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
                JsCall($"window.ds2.setFileName({J(Path.GetFileName(ofd.FileName))})");
            }
            catch (Exception ex) { ShowToast("error", "Load Failed", ex.Message); }
        }

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
                JsCall("window.ds2.setProgress(5,'Extracting…')");
                await Task.Run(() => currentTank.ExtractAll(fbd.SelectedPath));
                JsCall("window.ds2.setProgress(100,'Done')");
                ShowToast("success", "Extracted", $"All files extracted to {fbd.SelectedPath}");
            }
            catch (Exception ex) { ShowToast("error", "Extract Failed", ex.Message); }
        }

        private async Task HandleExtractSelected(string selectedPath)
        {
            if (currentTank == null) { ShowToast("warning", "No Tank", "Load a tank file first."); return; }
            if (string.IsNullOrEmpty(selectedPath)) { ShowToast("warning", "Nothing Selected", "Select a file or folder in the Explorer first."); return; }

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
            catch (Exception ex) { ShowToast("error", "Extract Failed", ex.Message); }
        }

        // ════════════════════════════════════════════════════════════════════
        // EDITOR HANDLERS
        // ════════════════════════════════════════════════════════════════════

        private async Task HandleOpenFile(string path, string source, string projectId)
        {
            // Only open text files in the editor; everything else gets a toast
            string ext = Path.GetExtension(path);
            if (!TextExtensions.Contains(ext))
            {
                ShowToast("warning", "Binary File",
                    $"{Path.GetFileName(path)} is a binary file and cannot be opened in the text editor.");
                return;
            }

            try
            {
                string content;

                if (source == "project")
                {
                    // Read directly from disk
                    if (!File.Exists(path))
                    {
                        ShowToast("error", "File Not Found", path);
                        return;
                    }
                    content = await File.ReadAllTextAsync(path, DetectEncoding(path));
                }
                else
                {
                    // Read from the loaded tank
                    if (currentTank == null) { ShowToast("warning", "No Tank", "Load a tank file first."); return; }
                    content = currentTank.ReadFileAsText(path)
                              ?? throw new InvalidOperationException("File not found in tank.");
                }

                string contentJson = JsonSerializer.Serialize(content);
                JsCall($"window.ds2.openFileInEditor({J(path)},{contentJson},{J(source)},{J(projectId ?? "")})");
            }
            catch (Exception ex) { ShowToast("error", "Open Failed", ex.Message); }
        }

        private async Task HandleSaveFile(string path, string content)
        {
            try
            {
                // Ensure the directory exists (handles new files)
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
                JsCall($"window.ds2.fileSaved({J(path)})");

                // Refresh the owning project tree if applicable
                var projId = _projects.FirstOrDefault(kv =>
                    path.StartsWith(kv.Value, StringComparison.OrdinalIgnoreCase)).Key;
                if (projId != null) RefreshProjectTree(projId);
            }
            catch (Exception ex)
            {
                JsCall($"window.ds2.fileSaveError({J(ex.Message)})");
                ShowToast("error", "Save Failed", ex.Message);
            }
        }

        private void HandleNewFile(string folderPath, string projectId)
        {
            // Prompt for filename on the UI thread
            string fileName = null;
            this.Invoke(() =>
            {
                // Simple input dialog via InputBox pattern
                using var frm = new Form
                {
                    Text = "New File",
                    Size = new System.Drawing.Size(360, 130),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    BackColor = System.Drawing.Color.FromArgb(22, 24, 32)
                };
                var lbl = new Label { Text = "File name:", ForeColor = System.Drawing.Color.FromArgb(205, 214, 244), Left = 12, Top = 14, AutoSize = true };
                var tb = new TextBox { Left = 12, Top = 34, Width = 320, Text = "new_file.gas", BackColor = System.Drawing.Color.FromArgb(30, 32, 48), ForeColor = System.Drawing.Color.FromArgb(205, 214, 244), BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "Create", Left = 172, Top = 62, Width = 80, DialogResult = DialogResult.OK, BackColor = System.Drawing.Color.FromArgb(37, 40, 64), ForeColor = System.Drawing.Color.FromArgb(137, 180, 250), FlatStyle = FlatStyle.Flat };
                var can = new Button { Text = "Cancel", Left = 260, Top = 62, Width = 72, DialogResult = DialogResult.Cancel, BackColor = System.Drawing.Color.FromArgb(30, 32, 48), ForeColor = System.Drawing.Color.FromArgb(108, 112, 134), FlatStyle = FlatStyle.Flat };
                frm.AcceptButton = ok; frm.CancelButton = can;
                frm.Controls.AddRange(new Control[] { lbl, tb, ok, can });
                tb.SelectAll();
                if (frm.ShowDialog(this) == DialogResult.OK)
                    fileName = tb.Text.Trim();
            });

            if (string.IsNullOrWhiteSpace(fileName)) return;

            string fullPath = Path.Combine(folderPath, fileName);
            try
            {
                if (!File.Exists(fullPath))
                    File.WriteAllText(fullPath, "");

                // Open it immediately
                _ = HandleOpenFile(fullPath, "project", projectId);
                if (projectId != null) RefreshProjectTree(projectId);
            }
            catch (Exception ex) { ShowToast("error", "Create Failed", ex.Message); }
        }

        private void HandleDeleteFile(string path, string projectId)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                ShowToast("success", "Deleted", Path.GetFileName(path));
                if (projectId != null) RefreshProjectTree(projectId);
            }
            catch (Exception ex) { ShowToast("error", "Delete Failed", ex.Message); }
        }

        // ════════════════════════════════════════════════════════════════════
        // PROJECT HANDLERS
        // ════════════════════════════════════════════════════════════════════

        private void HandleAddProject()
        {
            FolderBrowserDialog fbd = null;
            DialogResult dr = DialogResult.Cancel;
            this.Invoke(() =>
            {
                fbd = new FolderBrowserDialog
                {
                    Description = "Select your DS2 mod project folder (unpacked ds2res directory)"
                };
                dr = fbd.ShowDialog(this);
            });
            if (dr != DialogResult.OK) return;

            string rootPath = fbd.SelectedPath;

            // Avoid duplicates
            if (_projects.Values.Any(p => p.Equals(rootPath, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast("warning", "Already Added", "This folder is already in your projects.");
                return;
            }

            string id = Guid.NewGuid().ToString("N");
            _projects[id] = rootPath;

            SendProjectToJs(id, rootPath);
        }

        private void HandleRemoveProject(string projectId)
        {
            _projects.Remove(projectId);
            JsCall($"window.ds2.removeProject({J(projectId)})");
        }

        private void HandleRefreshProjects()
        {
            foreach (var kv in _projects)
                RefreshProjectTree(kv.Key);
        }

        private void RefreshProjectTree(string projectId)
        {
            if (!_projects.TryGetValue(projectId, out string rootPath)) return;
            string treeJson = BuildProjectTreeJson(rootPath);
            JsCall($"window.ds2.refreshProject({J(projectId)},{treeJson})");
        }

        private void SendProjectToJs(string id, string rootPath)
        {
            string name = Path.GetFileName(rootPath);
            string treeJson = BuildProjectTreeJson(rootPath);
            JsCall($"window.ds2.addProject({J(id)},{J(name)},{J(rootPath)},{treeJson})");
        }

        /// <summary>
        /// Returns a JSON array of relative file paths under rootPath,
        /// suitable for buildTreeFromPaths() in the JS side.
        /// </summary>
        private static string BuildProjectTreeJson(string rootPath)
        {
            if (!Directory.Exists(rootPath)) return "[]";

            try
            {
                var files = Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(rootPath.Length).TrimStart('\\', '/'))
                    .OrderBy(f => f)
                    .ToList();

                return JsonSerializer.Serialize(files);
            }
            catch { return "[]"; }
        }

        // ════════════════════════════════════════════════════════════════════
        // SHELL HANDLERS
        // ════════════════════════════════════════════════════════════════════

        private void HandleRevealInExplorer(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", $"\"{path}\"");
                else
                    ShowToast("warning", "Not Found", $"Path does not exist: {path}");
            }
            catch (Exception ex) { ShowToast("error", "Reveal Failed", ex.Message); }
        }

        // ════════════════════════════════════════════════════════════════════
        // RTC / PACK HANDLERS
        // ════════════════════════════════════════════════════════════════════

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
                JsCall($"window.ds2.setBrowsedSource({J(fbd.SelectedPath)})");
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
                JsCall($"window.ds2.setBrowsedOut({J(sfd.FileName)})");
        }

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
                JsCall("window.ds2.onRtcError('Failed to extract RTC.exe')");
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

            JsCall("window.ds2.setProgress(30,'Running RTC.exe…')");

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

                JsCall("window.ds2.setProgress(80,'Patching header…')");

                if (process.ExitCode == 0)
                {
                    PatchToDs2(outFile);
                    JsCall("window.ds2.setProgress(100,'Complete!')");
                    JsCall($"window.ds2.onRtcSuccess({J($"Tank created: {Path.GetFileName(outFile)}")})");
                }
                else
                {
                    JsCall($"window.ds2.onRtcError({J(error.Trim())})");
                }
            }
            catch (Exception ex)
            {
                JsCall($"window.ds2.onRtcError({J(ex.Message)})");
            }

            CleanupRtcExe();
        }

        // ════════════════════════════════════════════════════════════════════
        // UTILITY
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Detect encoding by BOM; fall back to UTF-8 without BOM.
        /// </summary>
        private static Encoding DetectEncoding(string path)
        {
            using var fs = File.OpenRead(path);
            var bom = new byte[4];
            int read = fs.Read(bom, 0, 4);
            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
            return new UTF8Encoding(false); // UTF-8 without BOM (DS2 default)
        }

        private static void PatchToDs2(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            bool modified = false;

            if (data.Length >= 4 && data[0] == 'D' && data[1] == 'S' && data[2] == 'i' && data[3] == 'g')
            {
                data[2] = (byte)'g'; data[3] = (byte)'2';
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

        // ── Designer stub ────────────────────────────────────────────────────

        private void InitializeComponent()
        {
            this.Text = "DS2 Tank Editor";
            this.Size = new System.Drawing.Size(1200, 720);
            this.MinimumSize = new System.Drawing.Size(800, 500);
            this.BackColor = System.Drawing.Color.FromArgb(15, 17, 23);
            this.StartPosition = FormStartPosition.CenterScreen;
        }
    }
}