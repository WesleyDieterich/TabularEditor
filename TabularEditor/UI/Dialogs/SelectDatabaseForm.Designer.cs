namespace TabularEditor.UI.Dialogs
{
    partial class SelectDatabaseForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.btnCancel = new System.Windows.Forms.Button();
            this.btnOK = new System.Windows.Forms.Button();
            this.lblTitle = new System.Windows.Forms.Label();
            this.lblFilterHint = new System.Windows.Forms.Label();
            this.lblModelCount = new System.Windows.Forms.Label();
            this.databasePage1 = new TabularEditor.UI.Dialogs.Pages.DatabasePage();
            this.SuspendLayout();
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(850, 456);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(94, 30);
            this.btnCancel.TabIndex = 2;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // btnOK
            // 
            this.btnOK.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOK.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.btnOK.Enabled = false;
            this.btnOK.Location = new System.Drawing.Point(732, 456);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(110, 30);
            this.btnOK.TabIndex = 1;
            this.btnOK.Text = "Open model";
            this.btnOK.UseVisualStyleBackColor = true;
            // 
            // lblTitle
            // 
            this.lblTitle.AutoSize = true;
            this.lblTitle.Location = new System.Drawing.Point(16, 14);
            this.lblTitle.Text = "Semantic models";
            // 
            // lblFilterHint
            // 
            this.lblFilterHint.AutoSize = true;
            this.lblFilterHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblFilterHint.Location = new System.Drawing.Point(16, 45);
            this.lblFilterHint.Text = "Type below a column heading to filter the list.";
            //
            // lblModelCount
            //
            this.lblModelCount.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left;
            this.lblModelCount.AutoSize = true;
            this.lblModelCount.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblModelCount.Location = new System.Drawing.Point(16, 463);
            // 
            // databasePage1
            // 
            this.databasePage1.AllowNew = false;
            this.databasePage1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.databasePage1.Location = new System.Drawing.Point(16, 74);
            this.databasePage1.Name = "databasePage1";
            this.databasePage1.Server = null;
            this.databasePage1.Size = new System.Drawing.Size(928, 368);
            this.databasePage1.TabIndex = 0;
            this.databasePage1.Validation += new TabularEditor.UI.Dialogs.ValidationEventHandler(this.databasePage1_Validation);
            this.databasePage1.Accept += new System.EventHandler(this.databasePage1_Accept);
            // 
            // SelectDatabaseForm
            // 
            this.AcceptButton = this.btnOK;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(960, 502);
            this.MinimumSize = new System.Drawing.Size(675, 340);
            this.ControlBox = false;
            this.Controls.Add(this.databasePage1);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.lblTitle);
            this.Controls.Add(this.lblFilterHint);
            this.Controls.Add(this.lblModelCount);
            this.Name = "SelectDatabaseForm";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Choose a semantic model";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.Label lblFilterHint;
        private System.Windows.Forms.Label lblModelCount;
        private Pages.DatabasePage databasePage1;
    }
}
