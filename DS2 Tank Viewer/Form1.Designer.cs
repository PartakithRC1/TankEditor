namespace DS2_Tank_Viewer
{
    partial class DS2TankEditor
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(DS2TankEditor));
            btnLoad = new Button();
            dataGridView1 = new DataGridView();
            panel1 = new Panel();
            btnExtractSelected = new Button();
            btnExtractAll = new Button();
            label1 = new Label();
            panel2 = new Panel();
            ((System.ComponentModel.ISupportInitialize)dataGridView1).BeginInit();
            panel1.SuspendLayout();
            panel2.SuspendLayout();
            SuspendLayout();
            // 
            // btnLoad
            // 
            btnLoad.BackColor = Color.FromArgb(54, 54, 54);
            btnLoad.FlatAppearance.BorderSize = 0;
            btnLoad.FlatAppearance.MouseDownBackColor = Color.FromArgb(39, 39, 39);
            btnLoad.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 64, 64);
            btnLoad.FlatStyle = FlatStyle.Flat;
            btnLoad.ForeColor = Color.Silver;
            btnLoad.Location = new Point(12, 50);
            btnLoad.Name = "btnLoad";
            btnLoad.Size = new Size(160, 50);
            btnLoad.TabIndex = 0;
            btnLoad.Text = "Load DS2 Tank File";
            btnLoad.UseVisualStyleBackColor = false;
            btnLoad.Click += btnLoad_Click;
            // 
            // dataGridView1
            // 
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dataGridView1.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            dataGridView1.BackgroundColor = Color.FromArgb(33, 33, 33);
            dataGridView1.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridView1.Dock = DockStyle.Fill;
            dataGridView1.GridColor = Color.FromArgb(22, 22, 22);
            dataGridView1.Location = new Point(0, 0);
            dataGridView1.Name = "dataGridView1";
            dataGridView1.Size = new Size(616, 450);
            dataGridView1.TabIndex = 1;
            // 
            // panel1
            // 
            panel1.Controls.Add(btnExtractSelected);
            panel1.Controls.Add(btnExtractAll);
            panel1.Controls.Add(btnLoad);
            panel1.Controls.Add(label1);
            panel1.Dock = DockStyle.Left;
            panel1.Location = new Point(0, 0);
            panel1.Name = "panel1";
            panel1.Size = new Size(184, 450);
            panel1.TabIndex = 2;
            // 
            // btnExtractSelected
            // 
            btnExtractSelected.BackColor = Color.FromArgb(54, 54, 54);
            btnExtractSelected.FlatAppearance.BorderSize = 0;
            btnExtractSelected.FlatAppearance.MouseDownBackColor = Color.FromArgb(39, 39, 39);
            btnExtractSelected.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 64, 64);
            btnExtractSelected.FlatStyle = FlatStyle.Flat;
            btnExtractSelected.ForeColor = Color.Silver;
            btnExtractSelected.Location = new Point(12, 162);
            btnExtractSelected.Name = "btnExtractSelected";
            btnExtractSelected.Size = new Size(160, 50);
            btnExtractSelected.TabIndex = 3;
            btnExtractSelected.Text = "Extract Selected";
            btnExtractSelected.UseVisualStyleBackColor = false;
            btnExtractSelected.Click += btnExtractSelected_Click;
            // 
            // btnExtractAll
            // 
            btnExtractAll.BackColor = Color.FromArgb(54, 54, 54);
            btnExtractAll.FlatAppearance.BorderSize = 0;
            btnExtractAll.FlatAppearance.MouseDownBackColor = Color.FromArgb(39, 39, 39);
            btnExtractAll.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 64, 64);
            btnExtractAll.FlatStyle = FlatStyle.Flat;
            btnExtractAll.ForeColor = Color.Silver;
            btnExtractAll.Location = new Point(12, 106);
            btnExtractAll.Name = "btnExtractAll";
            btnExtractAll.Size = new Size(160, 50);
            btnExtractAll.TabIndex = 2;
            btnExtractAll.Text = "Extract All";
            btnExtractAll.UseVisualStyleBackColor = false;
            btnExtractAll.Click += btnExtractAll_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Bahnschrift SemiBold", 14.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.ForeColor = Color.Coral;
            label1.Location = new Point(22, 13);
            label1.Name = "label1";
            label1.Size = new Size(143, 23);
            label1.TabIndex = 1;
            label1.Text = "DS2 Tank Editor";
            // 
            // panel2
            // 
            panel2.Controls.Add(dataGridView1);
            panel2.Dock = DockStyle.Fill;
            panel2.Location = new Point(184, 0);
            panel2.Name = "panel2";
            panel2.Size = new Size(616, 450);
            panel2.TabIndex = 2;
            // 
            // DS2TankEditor
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(39, 39, 39);
            ClientSize = new Size(800, 450);
            Controls.Add(panel2);
            Controls.Add(panel1);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "DS2TankEditor";
            Text = "DS2 Tank Editor";
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)dataGridView1).EndInit();
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            panel2.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private Button btnLoad;
        private DataGridView dataGridView1;
        private Panel panel1;
        private Panel panel2;
        private Label label1;
        private Button btnExtractSelected;
        private Button btnExtractAll;
    }
}
