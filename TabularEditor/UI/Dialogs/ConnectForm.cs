using Microsoft.AnalysisServices.Tabular;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.UIServices;

namespace TabularEditor.UI.Dialogs
{


    public partial class ConnectForm : Form
    {
        public ConnectForm()
        {
            InitializeComponent();
            var font = FormMain.CreateWorkspaceFont("Segoe UI Variable Text", "Segoe UI", 9.5f);
            var titleFont = new Font(font, FontStyle.Bold);
            Font = font;
            lblConnection.Font = titleFont;
            lblFavorites.Font = titleFont;
            BackColor = SystemColors.Window;
            favoritePane.BackColor = SystemColors.Control;
            favorites.BackColor = favoritePane.BackColor;
            favorites.DisplayMember = nameof(ConnectionFavorite.Address);
            favorites.ItemHeight = font.Height + 10;
            favorites.DrawItem += DrawFavorite;
            favorites.SelectedIndexChanged += FavoriteSelected;
            btnFavorite.Click += ToggleFavorite;
            Disposed += (sender, args) =>
            {
                titleFont.Dispose();
                font.Dispose();
            };
            RefreshFavorites();
        }

        private List<ConnectionFavorite> SavedFavorites =>
            Preferences.Current.ConnectionFavorites ?? (Preferences.Current.ConnectionFavorites = new List<ConnectionFavorite>());

        private bool updatingFavorites;

        private string FavoriteAddress
        {
            get
            {
                if (connectPage.LocalInstance != null) return null;

                var address = connectPage.ServerName.Trim();
                // A server field may also contain a connection string. Only save plain endpoints.
                return address.Length == 0 || address.Any(char.IsControl) || address.IndexOfAny(new[] { ';', '=', '?', '#', '@' }) >= 0
                    ? null : address;
            }
        }

        private void RefreshFavorites()
        {
            updatingFavorites = true;
            favorites.BeginUpdate();
            try
            {
                favorites.Items.Clear();
                favorites.Items.AddRange(SavedFavorites.Where(item => !string.IsNullOrWhiteSpace(item?.Address))
                    .OrderBy(FavoriteName, StringComparer.CurrentCultureIgnoreCase).ToArray());
                favorites.Visible = favorites.Items.Count > 0;
                emptyFavorites.Visible = favorites.Items.Count == 0;
            }
            finally
            {
                favorites.EndUpdate();
                updatingFavorites = false;
            }
            UpdateFavoriteSelection();
        }

        private static string FavoriteName(ConnectionFavorite favorite)
        {
            if (Uri.TryCreate(favorite.Address, UriKind.Absolute, out var uri) && uri.Scheme == "powerbi" && uri.Segments.Length > 1)
                return Uri.UnescapeDataString(uri.Segments.Last().TrimEnd('/'));
            return favorite.Address;
        }

        private void DrawFavorite(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0) return;

            var item = (ConnectionFavorite)favorites.Items[e.Index];
            var bounds = e.Bounds;
            bounds.Inflate(-8, 0);
            TextRenderer.DrawText(e.Graphics, FavoriteName(item), e.Font, bounds, e.ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        private void FavoriteSelected(object sender, EventArgs e)
        {
            if (!updatingFavorites && favorites.SelectedItem is ConnectionFavorite item)
                connectPage.SelectServer(item.Address, item.Authentication);
        }

        private void UpdateFavoriteSelection()
        {
            if (updatingFavorites) return;

            var address = FavoriteAddress;
            var match = favorites.Items.OfType<ConnectionFavorite>()
                .FirstOrDefault(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
            updatingFavorites = true;
            try
            {
                favorites.SelectedItem = match;
            }
            finally
            {
                updatingFavorites = false;
            }
            btnFavorite.Enabled = address != null;
            btnFavorite.Text = match == null ? "Add to favorites" : "Remove from favorites";
        }

        private void ToggleFavorite(object sender, EventArgs e)
        {
            var address = FavoriteAddress;
            if (address == null) return;

            if (SavedFavorites.RemoveAll(item => string.Equals(item?.Address, address, StringComparison.OrdinalIgnoreCase)) == 0)
                SavedFavorites.Add(new ConnectionFavorite { Address = address, Authentication = connectPage.AuthenticationMode });
            Preferences.Current.Save();
            RefreshFavorites();
        }

        public static string ConnectionString { get; private set; }
        public static Server Server { get; private set; }
        public static LocalInstance LocalInstance { get; private set; }

        public static DialogResult Show(string caption = "Connect to Tabular Server")
        {
            using (var form = new ConnectForm { Text = caption })
                return form.ShowDialog();
        }

        private void ConnectForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.Cancel) return;

            using (new Hourglass())
            {
                ConnectionString = connectPage.GetConnectionString();
                Server = connectPage.GetServer();
                LocalInstance = connectPage.LocalInstance;
                if (Server == null) e.Cancel = true;
            }
        }

        private void connectPage_Validation(object sender, ValidationEventArgs e)
        {
            btnOK.Enabled = e.IsValid;
            UpdateFavoriteSelection();
        }
    }
}
