namespace CSharp_Bumblebee
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.headerPanel = new System.Windows.Forms.Panel();
            this.pBoxLogo = new System.Windows.Forms.PictureBox();
            this.cbDisparity = new System.Windows.Forms.CheckBox();
            this.imagePanel = new System.Windows.Forms.Panel();
            this.pBox = new System.Windows.Forms.PictureBox();
            this.headerPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pBoxLogo)).BeginInit();
            this.imagePanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pBox)).BeginInit();
            this.SuspendLayout();

            this.backgroundWorker1.WorkerReportsProgress = true;
            this.backgroundWorker1.WorkerSupportsCancellation = true;
            this.backgroundWorker1.DoWork += new System.ComponentModel.DoWorkEventHandler(this.backgroundWorker1_DoWork);

            // Header area. The logo is anchored independently on the right so
            // it cannot overlap the centered Connect / Start buttons.
            this.headerPanel.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.headerPanel.Controls.Add(this.pBoxLogo);
            this.headerPanel.Controls.Add(this.cbDisparity);
            this.headerPanel.Location = new System.Drawing.Point(31, 48);
            this.headerPanel.Name = "headerPanel";
            this.headerPanel.Size = new System.Drawing.Size(1400, 112);
            this.headerPanel.TabIndex = 5;

            this.cbDisparity.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.cbDisparity.AutoSize = true;
            this.cbDisparity.BackColor = System.Drawing.SystemColors.ButtonFace;
            this.cbDisparity.FlatAppearance.BorderColor = System.Drawing.Color.DarkBlue;
            this.cbDisparity.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.cbDisparity.Font = new System.Drawing.Font("中國龍特圓體", 14F);
            this.cbDisparity.ForeColor = System.Drawing.Color.DarkGoldenrod;
            this.cbDisparity.Location = new System.Drawing.Point(330, 43);
            this.cbDisparity.Name = "cbDisparity";
            this.cbDisparity.Size = new System.Drawing.Size(96, 23);
            this.cbDisparity.TabIndex = 2;
            this.cbDisparity.Text = "Disparity";
            this.cbDisparity.UseVisualStyleBackColor = false;

            this.pBoxLogo.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.pBoxLogo.InitialImage = null;
            this.pBoxLogo.Location = new System.Drawing.Point(1040, 4);
            this.pBoxLogo.Name = "pBoxLogo";
            this.pBoxLogo.Size = new System.Drawing.Size(190, 100);
            this.pBoxLogo.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pBoxLogo.TabIndex = 3;
            this.pBoxLogo.TabStop = false;

            this.imagePanel.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.imagePanel.BackColor = System.Drawing.Color.Black;
            this.imagePanel.Controls.Add(this.pBox);
            this.imagePanel.Location = new System.Drawing.Point(31, 170);
            this.imagePanel.Name = "imagePanel";
            this.imagePanel.Size = new System.Drawing.Size(1400, 797);
            this.imagePanel.TabIndex = 4;

            this.pBox.BackColor = System.Drawing.Color.Black;
            this.pBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pBox.Location = new System.Drawing.Point(0, 0);
            this.pBox.Name = "pBox";
            this.pBox.Size = new System.Drawing.Size(1400, 797);
            this.pBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pBox.TabIndex = 1;
            this.pBox.TabStop = false;

            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1484, 1061);
            this.Controls.Add(this.headerPanel);
            this.Controls.Add(this.imagePanel);
            this.Name = "Form1";
            this.Text = "Form1";
            this.headerPanel.ResumeLayout(false);
            this.headerPanel.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pBoxLogo)).EndInit();
            this.imagePanel.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.pBox)).EndInit();
            this.ResumeLayout(false);
        }

        #endregion

        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private System.Windows.Forms.Panel headerPanel;
        private System.Windows.Forms.Panel imagePanel;
        private System.Windows.Forms.PictureBox pBox;
        private System.Windows.Forms.CheckBox cbDisparity;
        private System.Windows.Forms.PictureBox pBoxLogo;
    }
}
