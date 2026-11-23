using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>
    /// Saved state of the model diagram for one model. Stored per user, outside the model,
    /// so arranging tables never changes the model or its files.
    /// </summary>
    internal sealed class DiagramLayoutFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public string ModelKey { get; set; }
        public string SelectedLayout { get; set; }
        public List<DiagramLayout> Layouts { get; set; } = new List<DiagramLayout>();

        /// <summary>The "All tables" layout, created if missing. Always first.</summary>
        [JsonIgnore]
        public DiagramLayout AllTablesLayout
        {
            get
            {
                var layout = Layouts.FirstOrDefault(l => l.IsAllTables);
                if (layout == null)
                {
                    layout = new DiagramLayout();
                    Layouts.Insert(0, layout);
                }
                return layout;
            }
        }

        public DiagramLayout Find(string name) =>
            Layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>Adds a custom layout named "Layout 1", "Layout 2", ... whichever is free.</summary>
        public DiagramLayout AddLayout()
        {
            _ = AllTablesLayout; // creates it first if missing, so it stays the first layout
            var n = 1;
            while (Find("Layout " + n) != null) n++;
            var layout = new DiagramLayout { Name = "Layout " + n };
            Layouts.Add(layout);
            return layout;
        }

        /// <summary>
        /// Returns null if the name is acceptable for the layout, otherwise the reason it is not.
        /// </summary>
        public string ValidateName(DiagramLayout layout, string name)
        {
            if (layout.IsAllTables) return "The \"All tables\" layout cannot be renamed.";
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
            var other = Find(name.Trim());
            if (other != null && other != layout) return $"A layout named \"{other.Name}\" already exists.";
            return null;
        }

        public bool Remove(DiagramLayout layout) => !layout.IsAllTables && Layouts.Remove(layout);
    }

    internal sealed class DiagramLayout
    {
        public const string AllTables = "All tables";

        public string Name { get; set; } = AllTables;
        public float Zoom { get; set; } = 1f;
        public float ScrollX { get; set; }
        public float ScrollY { get; set; }
        /// <summary>
        /// Saved tables, in drawing order. For "All tables" these are positions only; for a
        /// custom layout they are also the list of tables it contains.
        /// </summary>
        public List<DiagramNodeLayout> Nodes { get; set; } = new List<DiagramNodeLayout>();

        [JsonIgnore]
        public bool IsAllTables => Name == AllTables;

        /// <summary>Whether this layout shows the table. "All tables" shows every table.</summary>
        public bool Includes(string tableName, string lineageTag) => IsAllTables || Find(tableName, lineageTag) != null;

        /// <summary>
        /// Adds a table without a position (the diagram places it automatically). Returns false
        /// if it is already in the layout.
        /// </summary>
        public bool Add(string tableName, string lineageTag)
        {
            if (Find(tableName, lineageTag) != null) return false;
            Nodes.Add(new DiagramNodeLayout
            {
                Table = tableName,
                LineageTag = string.IsNullOrEmpty(lineageTag) ? null : lineageTag,
                X = float.NaN,
                Y = float.NaN,
            });
            return true;
        }

        /// <summary>Removes a table from a custom layout. The table itself is not touched.</summary>
        public bool Remove(string tableName, string lineageTag)
        {
            if (IsAllTables) return false;
            var node = Find(tableName, lineageTag);
            return node != null && Nodes.Remove(node);
        }

        /// <summary>
        /// Finds the saved node for a table: by lineage tag first, so a renamed table keeps its
        /// place, then by name.
        /// </summary>
        public DiagramNodeLayout Find(string tableName, string lineageTag)
        {
            if (!string.IsNullOrEmpty(lineageTag))
            {
                var byTag = Nodes.FirstOrDefault(n => string.Equals(n.LineageTag, lineageTag, StringComparison.Ordinal));
                if (byTag != null) return byTag;
            }
            return Nodes.FirstOrDefault(n => string.Equals(n.Table, tableName, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class DiagramNodeLayout
    {
        public string Table { get; set; }
        public string LineageTag { get; set; }
        /// <summary>NaN when the table has not been placed yet.</summary>
        public float X { get; set; }
        public float Y { get; set; }
        /// <summary>Card size set by the user; 0 means "fit the fields".</summary>
        public float Width { get; set; }
        public float Height { get; set; }
        public bool Collapsed { get; set; }

        [JsonIgnore]
        public bool IsPlaced => !float.IsNaN(X) && !float.IsNaN(Y);
    }

    internal static class DiagramLayoutStore
    {
        internal static string DefaultFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabularEditor", "Layouts");

        /// <summary>
        /// Identifies a model by where it lives: the server and database for a connected model,
        /// the full path for a model loaded from disk. Returns null for a model that has not
        /// been saved anywhere yet, which has nothing stable to key a layout on.
        /// </summary>
        internal static string GetModelKey(TabularModelHandler handler)
        {
            if (handler?.Model == null) return null;
            if (handler.IsConnected && handler.Database != null)
                return "server:" + handler.Database.Server?.Name + "|" + handler.Database.ID;
            if (string.IsNullOrEmpty(handler.Source) || handler.SourceType == ModelSourceType.UnsavedFile) return null;
            try
            {
                return "file:" + Path.GetFullPath(handler.Source).TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return "file:" + handler.Source;
            }
        }

        internal static string GetPath(string modelKey, string folder = null)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(modelKey.ToLowerInvariant()));
                var name = string.Concat(hash.Take(12).Select(b => b.ToString("x2")));
                return Path.Combine(folder ?? DefaultFolder, name + ".json");
            }
        }

        /// <summary>
        /// Returns the saved layout, or null when there is none or it cannot be read. A missing or
        /// damaged file is never an error for the user: the diagram falls back to auto-layout.
        /// </summary>
        internal static DiagramLayoutFile Load(string modelKey, string folder = null)
        {
            if (modelKey == null) return null;
            try
            {
                var path = GetPath(modelKey, folder);
                if (!File.Exists(path)) return null;
                var file = JsonConvert.DeserializeObject<DiagramLayoutFile>(File.ReadAllText(path));
                // A hash collision would hand us another model's layout. Ignore it rather than use it.
                if (file == null || file.Version > DiagramLayoutFile.CurrentVersion || !string.Equals(file.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase))
                    return null;
                file.Layouts = file.Layouts?.Where(l => l != null).ToList() ?? new List<DiagramLayout>();
                foreach (var layout in file.Layouts)
                    layout.Nodes = layout.Nodes?.Where(n => n != null && !string.IsNullOrEmpty(n.Table)).ToList() ?? new List<DiagramNodeLayout>();
                return file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes the layout through a temporary file, so a crash mid-write cannot leave a
        /// truncated file behind. Returns false if the folder is not writable.
        /// </summary>
        internal static bool Save(string modelKey, DiagramLayoutFile file, string folder = null)
        {
            if (modelKey == null || file == null) return false;
            try
            {
                file.ModelKey = modelKey;
                file.Version = DiagramLayoutFile.CurrentVersion;
                var path = GetPath(modelKey, folder);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
