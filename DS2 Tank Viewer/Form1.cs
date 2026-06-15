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
    }
}