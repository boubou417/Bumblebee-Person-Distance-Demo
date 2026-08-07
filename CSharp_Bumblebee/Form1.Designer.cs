namespace CSharp_Bumblebee
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }
        #region Windows Form 設計工具產生的程式碼
        private void InitializeComponent()
        {
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.pBox = new System.Windows.Forms.PictureBox();
            this.cbDisparity = new System.Windows.Forms.CheckBox();
            this.pBoxLogo = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)(this.pBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pBoxLogo)).BeginInit();
            this.SuspendLayout();
            this.backgroundWorker1.WorkerReportsProgress = true;
            this.backgroundWorker1.WorkerSupportsCancellation = true;
            this.backgroundWorker1.DoWork += new System.ComponentModel.DoWorkEventHandler(this.backgroundWorker1_DoWork);
            this.pBox.Location = new System.Drawing.Point(31, 179);
            this.pBox.Name = "pBox";
            this.pBox.Size = new System.Drawing.Size(1400, 788);
            this.pBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
            this.pBox.TabIndex = 1;
            this.pBox.TabStop = false;
            this.cbDisparity.AutoSize = true;
            this.cbDisparity.BackColor = System.Drawing.SystemColors.ButtonFace;
            this.cbDisparity.FlatAppearance.BorderColor = System.Drawing.Color.DarkBlue;
            this.cbDisparity.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.cbDisparity.Font = new System.Drawing.Font("中國龍特圓體", 14F);
            this.cbDisparity.ForeColor = System.Drawing.Color.DarkGoldenrod;
            this.cbDisparity.Location = new System.Drawing.Point(370, 90);
            this.cbDisparity.Name = "cbDisparity";
            this.cbDisparity.Size = new System.Drawing.Size(96, 23);
            this.cbDisparity.TabIndex = 2;
            this.cbDisparity.Text = "Disparity";
            this.cbDisparity.UseVisualStyleBackColor = false;
            this.pBoxLogo.InitialImage = null;
            this.pBoxLogo.Location = new System.Drawing.Point(789, 43);
            this.pBoxLogo.Name = "pBoxLogo";
            this.pBoxLogo.Size = new System.Drawing.Size(196, 109);
            this.pBoxLogo.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pBoxLogo.TabIndex = 3;
            this.pBoxLogo.TabStop = false;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1484, 1061);
            this.Controls.Add(this.pBoxLogo);
            this.Controls.Add(this.cbDisparity);
            this.Controls.Add(this.pBox);
            this.Name = "Form1";
            this.Text = "Form1";
            ((System.ComponentModel.ISupportInitialize)(this.pBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pBoxLogo)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
        #endregion
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private System.Windows.Forms.PictureBox pBox;
        private System.Windows.Forms.CheckBox cbDisparity;
        private System.Windows.Forms.PictureBox pBoxLogo;
    }
}
