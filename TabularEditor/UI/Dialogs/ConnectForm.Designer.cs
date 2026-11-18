using TabularEditor.UI.Dialogs.Pages;

namespace TabularEditor.UI.Dialogs
{
    partial class ConnectForm
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
            this.btnOK = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.connectPage = new TabularEditor.UI.Dialogs.Pages.ConnectPage();
            this.favoritePane = new System.Windows.Forms.Panel();
            this.favorites = new System.Windows.Forms.ListBox();
            this.emptyFavorites = new System.Windows.Forms.Label();
            this.lblFavorites = new System.Windows.Forms.Label();
            this.lblConnection = new System.Windows.Forms.Label();
            this.btnFavorite = new System.Windows.Forms.Button();
            this.favoritePane.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnOK
            // 
            this.btnOK.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOK.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.btnOK.Enabled = false;
            this.btnOK.Location = new System.Drawing.Point(620, 308);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(94, 30);
            this.btnOK.TabIndex = 2;
            this.btnOK.Text = "Connect";
            this.btnOK.UseVisualStyleBackColor = true;
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(722, 308);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(90, 30);
            this.btnCancel.TabIndex = 3;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // connectPage
            // 
            this.connectPage.AllowLocalInstanceConnect = true;
            this.connectPage.AuthenticationMode = AuthenticationMode.Integrated;
            this.connectPage.Location = new System.Drawing.Point(276, 54);
            this.connectPage.Name = "connectPage";
            this.connectPage.Password = "";
            this.connectPage.ServerName = "";
            this.connectPage.Size = new System.Drawing.Size(536, 232);
            this.connectPage.TabIndex = 0;
            this.connectPage.UserName = "";
            this.connectPage.Validation += new TabularEditor.UI.Dialogs.ValidationEventHandler(this.connectPage_Validation);
            //
            // favoritePane
            //
            this.favoritePane.Controls.Add(this.favorites);
            this.favoritePane.Controls.Add(this.emptyFavorites);
            this.favoritePane.Controls.Add(this.btnFavorite);
            this.favoritePane.Controls.Add(this.lblFavorites);
            this.favoritePane.Location = new System.Drawing.Point(12, 12);
            this.favoritePane.Padding = new System.Windows.Forms.Padding(10, 0, 10, 10);
            this.favoritePane.Size = new System.Drawing.Size(244, 282);
            this.favoritePane.TabIndex = 1;
            //
            // favorites
            //
            this.favorites.AccessibleName = "Favorite servers";
            this.favorites.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.favorites.Dock = System.Windows.Forms.DockStyle.Fill;
            this.favorites.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed;
            this.favorites.IntegralHeight = false;
            this.favorites.TabIndex = 0;
            //
            // emptyFavorites
            //
            this.emptyFavorites.Dock = System.Windows.Forms.DockStyle.Fill;
            this.emptyFavorites.Padding = new System.Windows.Forms.Padding(8);
            this.emptyFavorites.Text = "No favorite servers yet.";
            //
            // btnFavorite
            //
            this.btnFavorite.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.btnFavorite.Height = 31;
            this.btnFavorite.TabIndex = 1;
            this.btnFavorite.Text = "Add to favorites";
            this.btnFavorite.UseVisualStyleBackColor = true;
            //
            // lblFavorites
            //
            this.lblFavorites.Dock = System.Windows.Forms.DockStyle.Top;
            this.lblFavorites.Height = 34;
            this.lblFavorites.Text = "Favorites";
            this.lblFavorites.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // lblConnection
            //
            this.lblConnection.Location = new System.Drawing.Point(276, 12);
            this.lblConnection.Size = new System.Drawing.Size(536, 34);
            this.lblConnection.Text = "Connection";
            this.lblConnection.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // ConnectForm
            // 
            this.AcceptButton = this.btnOK;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(824, 350);
            this.ControlBox = false;
            this.Controls.Add(this.connectPage);
            this.Controls.Add(this.lblConnection);
            this.Controls.Add(this.favoritePane);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Name = "ConnectForm";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Connect to Tabular Server";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.ConnectForm_FormClosing);
            this.favoritePane.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Button btnCancel;
        private Pages.ConnectPage connectPage;
        private System.Windows.Forms.Panel favoritePane;
        private System.Windows.Forms.ListBox favorites;
        private System.Windows.Forms.Label emptyFavorites;
        private System.Windows.Forms.Label lblFavorites;
        private System.Windows.Forms.Label lblConnection;
        private System.Windows.Forms.Button btnFavorite;
    }
}
