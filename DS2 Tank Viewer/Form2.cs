using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace DS2_Tank_Viewer
{
    public partial class Form2 : Form
    {
        // ---------------------------------------------------------------------
        // Designer controls (create these manually or via designer)
        // ---------------------------------------------------------------------
        private TextBox txtSourceDir;
        private TextBox txtOutFile;
        private TextBox txtTitle;
        private TextBox txtAuthor;
        private TextBox txtCopyright;
        private TextBox txtBuild;
        private TextBox txtPriority;
        private CheckBox chkDev;
        private CheckBox chkMpXfer;
        private CheckBox chkProtected;
        private CheckBox chkWait;
        private Button btnOK;
        private Button btnCancel;
        private Button btnBrowseSource;
        private Button btnBrowseOut;
        private FolderBrowserDialog fbdSource;
        private SaveFileDialog sfdOut;

        public Form2()
        {
            InitializeComponent();
            SetupDefaultValues();
        }

        private void SetupDefaultValues()
        {
            txtPriority.Text = "0x4000";   // default priority (user tank)
        }

        public string GetSourceDir() => txtSourceDir.Text.Trim();
        public string GetOutFile() => txtOutFile.Text.Trim();
        public string GetTitle() => txtTitle.Text.Trim();
        public string GetAuthor() => txtAuthor.Text.Trim();
        public string GetCopyright() => txtCopyright.Text.Trim();
        public string GetBuild() => txtBuild.Text.Trim();
        public uint GetPriority()
        {
            string p = txtPriority.Text.Trim();
            if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(p.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint val) ? val : 0x4000;
            else
                return uint.TryParse(p, out uint val) ? val : 0x4000;
        }
        public bool GetDevFlag() => chkDev.Checked;
        public bool GetMpXferFlag() => chkMpXfer.Checked;
        public bool GetProtectedFlag() => chkProtected.Checked;
        public bool GetWaitFlag() => chkWait.Checked;

        private void InitializeComponent()
        {
            // Instantiate controls
            txtSourceDir = new TextBox();
            txtOutFile = new TextBox();
            txtTitle = new TextBox();
            txtAuthor = new TextBox();
            txtCopyright = new TextBox();
            txtBuild = new TextBox();
            txtPriority = new TextBox();
            chkDev = new CheckBox();
            chkMpXfer = new CheckBox();
            chkProtected = new CheckBox();
            chkWait = new CheckBox();
            btnOK = new Button();
            btnCancel = new Button();
            btnBrowseSource = new Button();
            btnBrowseOut = new Button();
            fbdSource = new FolderBrowserDialog();
            sfdOut = new SaveFileDialog();

            // Form
            this.Text = "Create Tank with RTC.exe";
            this.Size = new System.Drawing.Size(500, 450);
            this.StartPosition = FormStartPosition.CenterParent;

            // Layout (simple manual positioning – adjust as needed)
            int y = 20;
            int labelWidth = 100;
            int controlWidth = 250;
            int buttonWidth = 75;
            int leftLabel = 20;
            int leftControl = leftLabel + labelWidth + 10;
            int leftButton = leftControl + controlWidth + 5;

            // Source directory
            AddLabel("Source dir:", leftLabel, y);
            txtSourceDir.Location = new System.Drawing.Point(leftControl, y);
            txtSourceDir.Size = new System.Drawing.Size(controlWidth, 23);
            btnBrowseSource.Text = "Browse";
            btnBrowseSource.Location = new System.Drawing.Point(leftButton, y);
            btnBrowseSource.Size = new System.Drawing.Size(buttonWidth, 23);
            btnBrowseSource.Click += BtnBrowseSource_Click;
            this.Controls.Add(txtSourceDir);
            this.Controls.Add(btnBrowseSource);
            y += 30;

            // Output file
            AddLabel("Output file:", leftLabel, y);
            txtOutFile.Location = new System.Drawing.Point(leftControl, y);
            txtOutFile.Size = new System.Drawing.Size(controlWidth, 23);
            btnBrowseOut.Text = "Browse";
            btnBrowseOut.Location = new System.Drawing.Point(leftButton, y);
            btnBrowseOut.Size = new System.Drawing.Size(buttonWidth, 23);
            btnBrowseOut.Click += BtnBrowseOut_Click;
            this.Controls.Add(txtOutFile);
            this.Controls.Add(btnBrowseOut);
            y += 35;

            // Title
            AddLabel("Title:", leftLabel, y);
            txtTitle.Location = new System.Drawing.Point(leftControl, y);
            txtTitle.Size = new System.Drawing.Size(controlWidth, 23);
            this.Controls.Add(txtTitle);
            y += 30;

            // Author
            AddLabel("Author:", leftLabel, y);
            txtAuthor.Location = new System.Drawing.Point(leftControl, y);
            txtAuthor.Size = new System.Drawing.Size(controlWidth, 23);
            this.Controls.Add(txtAuthor);
            y += 30;

            // Copyright
            AddLabel("Copyright:", leftLabel, y);
            txtCopyright.Location = new System.Drawing.Point(leftControl, y);
            txtCopyright.Size = new System.Drawing.Size(controlWidth, 23);
            this.Controls.Add(txtCopyright);
            y += 30;

            // Build
            AddLabel("Build:", leftLabel, y);
            txtBuild.Location = new System.Drawing.Point(leftControl, y);
            txtBuild.Size = new System.Drawing.Size(controlWidth, 23);
            this.Controls.Add(txtBuild);
            y += 30;

            // Priority
            AddLabel("Priority (hex/dec):", leftLabel, y);
            txtPriority.Location = new System.Drawing.Point(leftControl, y);
            txtPriority.Size = new System.Drawing.Size(controlWidth, 23);
            this.Controls.Add(txtPriority);
            y += 35;

            // Checkboxes
            chkDev.Text = "-flagdev (development only)";
            chkDev.Location = new System.Drawing.Point(leftLabel, y);
            chkDev.AutoSize = true;
            this.Controls.Add(chkDev);
            y += 25;

            chkMpXfer.Text = "-flagmpxfer (allow multiplayer transfer)";
            chkMpXfer.Location = new System.Drawing.Point(leftLabel, y);
            chkMpXfer.AutoSize = true;
            this.Controls.Add(chkMpXfer);
            y += 25;

            chkProtected.Text = "-flagprotected (protected content)";
            chkProtected.Location = new System.Drawing.Point(leftLabel, y);
            chkProtected.AutoSize = true;
            this.Controls.Add(chkProtected);
            y += 25;

            chkWait.Text = "-waitonexit (pause on exit)";
            chkWait.Location = new System.Drawing.Point(leftLabel, y);
            chkWait.AutoSize = true;
            this.Controls.Add(chkWait);
            y += 40;

            // Buttons
            btnOK.Text = "Create Tank";
            btnOK.Location = new System.Drawing.Point(leftControl, y);
            btnOK.Size = new System.Drawing.Size(100, 30);
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK);

            btnCancel.Text = "Cancel";
            btnCancel.Location = new System.Drawing.Point(leftControl + 110, y);
            btnCancel.Size = new System.Drawing.Size(75, 30);
            btnCancel.Click += (s, e) => this.DialogResult = DialogResult.Cancel;
            this.Controls.Add(btnCancel);
        }

        private void AddLabel(string text, int x, int y)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.Location = new System.Drawing.Point(x, y);
            lbl.AutoSize = true;
            this.Controls.Add(lbl);
        }

        private void BtnBrowseSource_Click(object sender, EventArgs e)
        {
            if (fbdSource.ShowDialog() == DialogResult.OK)
                txtSourceDir.Text = fbdSource.SelectedPath;
        }

        private void BtnBrowseOut_Click(object sender, EventArgs e)
        {
            sfdOut.Filter = "Tank files (*.ds2res;*.ds2map)|*.ds2res;*.ds2map|All files (*.*)|*.*";
            if (sfdOut.ShowDialog() == DialogResult.OK)
                txtOutFile.Text = sfdOut.FileName;
        }

        private async void BtnOK_Click(object sender, EventArgs e)
        {
            // Validation
            if (string.IsNullOrWhiteSpace(txtSourceDir.Text) || !Directory.Exists(txtSourceDir.Text))
            {
                MessageBox.Show("Source directory does not exist.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(txtOutFile.Text))
            {
                MessageBox.Show("Output file path is required.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Build arguments
            var args = new List<string>
            {
                $"-source \"{txtSourceDir.Text.Trim()}\"",
                $"-out \"{txtOutFile.Text.Trim()}\""
            };

            if (!string.IsNullOrWhiteSpace(txtTitle.Text))
                args.Add($"-title \"{txtTitle.Text.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(txtAuthor.Text))
                args.Add($"-author \"{txtAuthor.Text.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(txtCopyright.Text))
                args.Add($"-copyright \"{txtCopyright.Text.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(txtBuild.Text))
                args.Add($"-build \"{txtBuild.Text.Trim()}\"");

            // Parse priority (hex or decimal)
            string priorityStr = txtPriority.Text.Trim();
            uint priorityValue = 0x4000; // default
            if (!string.IsNullOrEmpty(priorityStr))
            {
                if (priorityStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(priorityStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out priorityValue))
                        MessageBox.Show("Invalid hex priority. Using default 0x4000.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    if (!uint.TryParse(priorityStr, out priorityValue))
                        MessageBox.Show("Invalid decimal priority. Using default 16384 (0x4000).", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            args.Add($"-priority {priorityValue}");

            if (chkDev.Checked) args.Add("-flagdev");
            if (chkMpXfer.Checked) args.Add("-flagmpxfer");
            if (chkProtected.Checked) args.Add("-flagprotected");
            if (chkWait.Checked) args.Add("-waitonexit");

            string arguments = string.Join(" ", args);

            // Extract RTC.exe to temp folder
            string rtcPath = ExtractRtcExe();
            if (string.IsNullOrEmpty(rtcPath))
            {
                MessageBox.Show("Failed to extract RTC.exe.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Run process
            var psi = new ProcessStartInfo
            {
                FileName = rtcPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode == 0)
                    {
                        MessageBox.Show($"Tank created successfully!\n\n{output}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        this.DialogResult = DialogResult.OK;
                        this.Close();
                    }
                    else
                    {
                        MessageBox.Show($"RTC.exe failed (exit code {process.ExitCode}):\n{error}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Exception while running RTC.exe:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string ExtractRtcExe()
        {
            string tempPath = Path.GetTempPath();
            string exePath = Path.Combine(tempPath, "RTC.exe");

            if (!File.Exists(exePath))
            {
                // Resource name must match your project's embedded resource path
                // Example: "DS2_Tank_Viewer.RTC.exe" if the file is in the root of the project
                string resourceName = "DS2_Tank_Viewer.RTC.exe";
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        MessageBox.Show($"Embedded resource '{resourceName}' not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return null;
                    }
                    using (FileStream fs = new FileStream(exePath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fs);
                    }
                }
            }
            return exePath;
        }
    }
}