using System;
using System.Collections.Generic;
using System.Windows.Forms;

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

        private void btnLoad_Click(object sender, EventArgs e)
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
            MessageBox.Show("Not yet implemented"); return;
            if (currentTank == null) { MessageBox.Show("Load a tank first!"); return; }

            FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Choose output folder" };
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                currentTank.ExtractSelected(dataGridView1, fbd.SelectedPath);
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }
        private void btnPackTank_Click(object sender, EventArgs e)
        {
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
    }
}