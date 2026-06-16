using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace DS2_Tank_Viewer
{
    public partial class DS2TankEditor : Form
    {
        public DS2TankEditor()
        {
            InitializeComponent();
        }

        private TankReader currentTank = null;
        private string currentTankFilePath = "";

        /* private void btnLoad_Click(object sender, EventArgs e)
         {
             OpenFileDialog ofd = new OpenFileDialog { Filter = "DS2 Resource Files (*.ds2res)|*.ds2res" };
             if (ofd.ShowDialog() != DialogResult.OK) return;

             try
             {
                 currentTank = new TankReader();
                 currentTank.Load(ofd.FileName);
                 currentTankFilePath = ofd.FileName;

                 var fileList = currentTank.GetFileList();
                 dataGridView1.AutoGenerateColumns = true;
                 dataGridView1.DataSource = fileList.Select(f => new { FullPath = f }).ToList();

                 MessageBox.Show($"✅ Loaded {currentTank.FileCount} files!", "Success");
             }
             catch (Exception ex)
             {
                 MessageBox.Show($"Error: {ex.Message}");
             }
         }*/

        private void btnLoad_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "DS2 Resource Files (*.ds2res)|*.ds2res" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                currentTank = new TankReader();
                currentTank.Load(ofd.FileName);

                // 1. Clear the old data
                treeViewExplorer.Nodes.Clear();
                treeViewExplorer.BeginUpdate(); // Prevents flickering while building

                // 2. Get your raw list
                var fileList = currentTank.GetFileList();

                // 3. Build the tree
                foreach (string path in fileList)
                {
                    // Remove leading slash if it exists, then split
                    string[] pathParts = path.TrimStart('\\').Split('\\');

                    TreeNodeCollection currentNodes = treeViewExplorer.Nodes;

                    // Loop through each folder level in the path
                    for (int i = 0; i < pathParts.Length; i++)
                    {
                        string part = pathParts[i];

                        // Check if this node exists at the current level
                        TreeNode foundNode = null;
                        foreach (TreeNode node in currentNodes)
                        {
                            if (node.Text == part)
                            {
                                foundNode = node;
                                break;
                            }
                        }

                        // If it doesn't exist, create it
                        if (foundNode == null)
                        {
                            foundNode = new TreeNode(part);
                            currentNodes.Add(foundNode);
                        }

                        // Move our reference deeper into the tree
                        currentNodes = foundNode.Nodes;
                    }
                }

                treeViewExplorer.EndUpdate();
                MessageBox.Show($"✅ Loaded {currentTank.FileCount} files!", "Success");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }


        private void btnExtractAll_Click(object sender, EventArgs e)
        {
            if (currentTank == null) { MessageBox.Show("Load a tank first!"); return; }

            FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Choose output folder" };
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                currentTank.ExtractAll(fbd.SelectedPath);
            }
        }

        private void btnExtractSelected_Click(object sender, EventArgs e)
        {
           // MessageBox.Show("Not yet implemented"); return;
            if (currentTank == null) { MessageBox.Show("Load a tank first!"); return; }

            FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Choose output folder" };
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                currentTank.ExtractSelected(treeViewExplorer, fbd.SelectedPath);
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }
        private void btnPackTank_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Having trouble getting pakcing correct in C#. Please use RTC still for now."); return;
            using (var folderBrowser = new FolderBrowserDialog())
            {
                folderBrowser.Description = "Select the folder containing files to pack into a Tank.";
                if (folderBrowser.ShowDialog() != DialogResult.OK) return;

                using (var saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "Tank Files (*.ds2res)|*.ds2res";
                    saveDialog.Title = "Save new Tank file";
                    if (saveDialog.ShowDialog() != DialogResult.OK) return;

                    try
                    {
                        var packer = new TankWriter();
                        string sourcePath = folderBrowser.SelectedPath;

                        var allFiles = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);

                        foreach (string file in allFiles)
                        {
                            string relativePath = file.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                            byte[] fileData = File.ReadAllBytes(file);

                            // Don't compress already compressed formats
                            bool compress = ShouldCompressFile(file);
                            packer.AddFile(relativePath, fileData, compress);
                        }

                        packer.Save(saveDialog.FileName);
                        MessageBox.Show($"Successfully packed {allFiles.Length} files.", "Pack Complete");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to pack tank: {ex.Message}", "Error");
                    }
                }
            }
        }

        private bool ShouldCompressFile(string filePath)
        {
            // DS2 compresses everything including .raw textures.
            // Only skip formats that are already compressed at the container level.
            string ext = Path.GetExtension(filePath).ToLower();
            string[] noCompress = { ".zip", ".rar", ".7z" };
            return !noCompress.Contains(ext);
        }


        private static string ExtractRtcExe()
        {
            string tempPath = Path.GetTempPath();
            string exePath = Path.Combine(tempPath, "RTC.exe");

            if (!File.Exists(exePath))
            {
                using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DS2_Tank_Viewer.Resources.RTC.exe"))
                using (FileStream output = new FileStream(exePath, FileMode.Create, FileAccess.Write))
                {
                    resource.CopyTo(output);
                }
            }
            return exePath;
        }

        private static string CleanupRtcExe()
        {
            string tempPath = Path.GetTempPath();
            string exePath = Path.Combine(tempPath, "RTC.exe");

            if (!File.Exists(exePath))
            {
                File.Delete(exePath);
            }
            return exePath;
        }

        private async void btnRTCCreateTank_Click(object sender, EventArgs e)
        {
            // Extract RTC.exe (if not already present)
            string rtcPath = ExtractRtcExe();
            if (string.IsNullOrEmpty(rtcPath))
            {
                MessageBox.Show("Failed to extract RTC.exe.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Show the options dialog
            using (var dialog = new Form2())
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                // Build command line arguments (same as before, but we'll capture output file)
                string sourceDir = dialog.GetSourceDir();   // you need to expose these properties
                string outFile = dialog.GetOutFile();
                string title = dialog.GetTitle();
                string author = dialog.GetAuthor();
                string copyright = dialog.GetCopyright();
                string build = dialog.GetBuild();
                uint priority = dialog.GetPriority();
                bool dev = dialog.GetDevFlag();
                bool mpXfer = dialog.GetMpXferFlag();
                bool protect = dialog.GetProtectedFlag();
                bool wait = dialog.GetWaitFlag();

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

                string arguments = string.Join(" ", args);

                // Run RTC.exe
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
                            // Patch the generated file to DS2 format
                            PatchToDs2(outFile);
                            MessageBox.Show($"DS2 tank created and patched successfully!\n\n{output}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"RTC.exe failed (exit code {process.ExitCode}):\n{error}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Exception: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            // Optional: cleanup RTC.exe if you don't want to keep it
             CleanupRtcExe();
        }

        private static void PatchToDs2(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            bool modified = false;

            // 1. Change "DSig" to "DSg2" at the start (Bytes 0-3)
            if (data.Length >= 4 && data[0] == 'D' && data[1] == 'S' && data[2] == 'i' && data[3] == 'g')
            {
                data[2] = (byte)'g';
                data[3] = (byte)'2';
                modified = true;
            }

            // 2. Fix Header Version and Flags to match WorkingMod.txt
            // Using the verified bytes: 00 01 01 00 at offset 8
            if (data.Length >= 12)
            {
                // Update version bytes
                data[8] = 0x00;
                data[9] = 0x01;
                data[10] = 0x01;
                data[11] = 0x00;
                modified = true;
            }

            // 3. Fix the flag/alignment byte at offset 54
            if (data.Length > 54 && data[54] != 0x00)
            {
                data[54] = 0x00;
                modified = true;
            }

            if (modified)
                File.WriteAllBytes(filePath, data);
        }
    }
}