using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TabularEditor.TOMWrapper;
using TabularEditor.UI.ModelDiagram;

namespace TabularEditor
{
    [TestClass]
    public class ModelDiagramTests
    {
        private string folder;

        [TestInitialize]
        public void Init()
        {
            folder = Path.Combine(Path.GetTempPath(), "te-diagram-tests-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        private static DiagramGraph LoadGraph(out TabularModelHandler handler)
        {
            handler = new TabularModelHandler("AdventureWorks.bim");
            var graph = DiagramGraph.Build(handler.Model);
            foreach (var node in graph.Nodes)
                node.FitHeight();
            return graph;
        }

        [TestMethod]
        public void GraphHasOneEdgePerRelationship()
        {
            var graph = LoadGraph(out var handler);
            var model = handler.Model;

            Assert.AreEqual(model.Tables.Count, graph.Nodes.Count);
            Assert.AreEqual(model.Relationships.Count, graph.Edges.Count);
            foreach (var edge in graph.Edges)
            {
                Assert.AreSame(edge.Relationship.FromColumn.Table, edge.From.Table);
                Assert.AreSame(edge.Relationship.ToColumn.Table, edge.To.Table);
            }
        }

        [TestMethod]
        public void RelationshipColumnsArePinnedToTheTop()
        {
            var graph = LoadGraph(out _);
            foreach (var node in graph.Nodes)
            {
                var firstNonKey = node.Fields.TakeWhile(f => f.IsKey).Count();
                Assert.IsFalse(node.Fields.Skip(firstNonKey).Any(f => f.IsKey), $"Key fields of {node.Table.Name} are not all at the top.");
            }
            var keyColumns = graph.Edges.SelectMany(e => new[] { e.Relationship.FromColumn, e.Relationship.ToColumn });
            foreach (var column in keyColumns)
                Assert.IsTrue(graph.Find(column.Table).Fields.Single(f => f.Object == column).IsKey);
        }

        [TestMethod]
        public void AutoLayoutIsDeterministicAndHasNoOverlaps()
        {
            var first = LoadGraph(out _);
            AutoLayout.Arrange(first, first.Nodes.ToList());
            var second = LoadGraph(out _);
            AutoLayout.Arrange(second, second.Nodes.ToList());

            var positions = first.Nodes.ToDictionary(n => n.Table.Name, n => n.Location);
            foreach (var node in second.Nodes)
                Assert.AreEqual(positions[node.Table.Name], node.Location, node.Table.Name);

            var nodes = first.Nodes.ToList();
            for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                    Assert.IsFalse(nodes[i].Bounds.IntersectsWith(nodes[j].Bounds),
                        $"{nodes[i].Table.Name} overlaps {nodes[j].Table.Name}");
        }

        [TestMethod]
        public void AutoLayoutPutsNewTablesBelowPlacedOnes()
        {
            var graph = LoadGraph(out _);
            var placed = graph.Nodes.Take(3).ToList();
            for (var i = 0; i < placed.Count; i++) placed[i].Location = new PointF(i * 300, 0);
            var rest = graph.Nodes.Skip(3).ToList();

            AutoLayout.Arrange(graph, rest);

            Assert.AreEqual(new PointF(0, 0), placed[0].Location, "Placed tables must not move.");
            var bottom = placed.Max(n => n.Bounds.Bottom);
            Assert.IsTrue(rest.All(n => n.Location.Y >= bottom), "New tables must go below the placed ones.");
        }

        [TestMethod]
        public void LayoutRoundTrips()
        {
            const string key = "file:C:\\models\\Sales.bim";
            var file = new DiagramLayoutFile();
            file.Layouts.Add(new DiagramLayout
            {
                Zoom = 0.75f, ScrollX = 10, ScrollY = -20,
                Nodes = { new DiagramNodeLayout { Table = "Sales", LineageTag = "abc", X = 1.5f, Y = 2.5f } },
            });

            Assert.IsTrue(DiagramLayoutStore.Save(key, file, folder));
            var loaded = DiagramLayoutStore.Load(key, folder);

            Assert.IsNotNull(loaded);
            var layout = loaded.Layouts.Single();
            Assert.AreEqual(0.75f, layout.Zoom);
            Assert.AreEqual(-20f, layout.ScrollY);
            var node = layout.Nodes.Single();
            Assert.AreEqual("Sales", node.Table);
            Assert.AreEqual(2.5f, node.Y);
        }

        [TestMethod]
        public void EachModelGetsItsOwnFile()
        {
            Assert.AreNotEqual(DiagramLayoutStore.GetPath("file:C:\\a.bim", folder), DiagramLayoutStore.GetPath("file:C:\\b.bim", folder));
            // Windows paths are case-insensitive, so the same model opened with different casing shares its layout.
            Assert.AreEqual(DiagramLayoutStore.GetPath("file:C:\\A.bim", folder), DiagramLayoutStore.GetPath("file:c:\\a.bim", folder));
        }

        [TestMethod]
        public void MissingDamagedOrForeignFilesFallBackToNull()
        {
            Assert.IsNull(DiagramLayoutStore.Load("file:C:\\missing.bim", folder));

            const string key = "file:C:\\damaged.bim";
            Directory.CreateDirectory(folder);
            File.WriteAllText(DiagramLayoutStore.GetPath(key, folder), "{ not json");
            Assert.IsNull(DiagramLayoutStore.Load(key, folder));

            // A file whose stored key is a different model (a hash collision) must not be used.
            const string other = "file:C:\\other.bim";
            DiagramLayoutStore.Save(other, new DiagramLayoutFile(), folder);
            File.Copy(DiagramLayoutStore.GetPath(other, folder), DiagramLayoutStore.GetPath(key, folder), overwrite: true);
            Assert.IsNull(DiagramLayoutStore.Load(key, folder));
        }

        [TestMethod]
        public void CustomLayoutShowsOnlyItsTablesAndTheirRelationships()
        {
            var handler = new TabularModelHandler("AdventureWorks.bim");
            var layout = new DiagramLayout { Name = "Sales" };
            layout.Add("Internet Sales", null);
            layout.Add("Customer", null);
            layout.Add("Product Category", null);

            var graph = DiagramGraph.Build(handler.Model, t => layout.Includes(t.Name, t.LineageTag));

            CollectionAssert.AreEquivalent(new[] { "Customer", "Internet Sales", "Product Category" }, graph.Nodes.Select(n => n.Table.Name).ToList());
            Assert.IsTrue(graph.Edges.Count > 0);
            Assert.IsTrue(graph.Edges.All(e => graph.Nodes.Contains(e.From) && graph.Nodes.Contains(e.To)),
                "Relationships to tables outside the layout must be left out.");
            Assert.IsTrue(new DiagramLayout().Includes("Anything", null), "\"All tables\" includes every table.");
        }

        [TestMethod]
        public void AddingAndRemovingTablesFromALayout()
        {
            var layout = new DiagramLayout { Name = "Custom" };
            Assert.IsTrue(layout.Add("Sales", "tag"));
            Assert.IsFalse(layout.Add("Sales renamed", "tag"), "The same table (by lineage tag) is not added twice.");
            Assert.IsFalse(layout.Find("Sales", "tag").IsPlaced, "New tables wait for auto-layout.");

            Assert.IsTrue(layout.Remove("Sales", "tag"));
            Assert.AreEqual(0, layout.Nodes.Count);

            var all = new DiagramLayout();
            all.Nodes.Add(new DiagramNodeLayout { Table = "Sales" });
            Assert.IsFalse(all.Remove("Sales", null), "Tables cannot be removed from \"All tables\".");
        }

        [TestMethod]
        public void LayoutNamesAreUniqueAndAllTablesIsProtected()
        {
            var file = new DiagramLayoutFile();
            var first = file.AddLayout();
            var second = file.AddLayout();

            Assert.AreEqual(DiagramLayout.AllTables, file.Layouts[0].Name, "\"All tables\" is created first.");
            Assert.AreEqual("Layout 1", first.Name);
            Assert.AreEqual("Layout 2", second.Name);

            Assert.IsNotNull(file.ValidateName(second, "layout 1"), "Names are unique, ignoring case.");
            Assert.IsNotNull(file.ValidateName(second, "  "));
            Assert.IsNotNull(file.ValidateName(file.AllTablesLayout, "Renamed"));
            Assert.IsNull(file.ValidateName(second, "Layout 2"), "Keeping its own name is fine.");
            Assert.IsNull(file.ValidateName(second, "Finance"));

            Assert.IsFalse(file.Remove(file.AllTablesLayout));
            Assert.IsTrue(file.Remove(first));
            Assert.AreEqual("Layout 1", file.AddLayout().Name, "Freed names are reused.");
        }

        [TestMethod]
        public void UnplacedTablesSurviveSaving()
        {
            const string key = "file:C:\\models\\Unplaced.bim";
            var file = new DiagramLayoutFile();
            var custom = file.AddLayout();
            custom.Add("Sales", null);
            custom.Nodes[0].Collapsed = true;
            custom.Nodes[0].Width = 300;

            DiagramLayoutStore.Save(key, file, folder);
            var loaded = DiagramLayoutStore.Load(key, folder).Find("Layout 1").Nodes.Single();

            Assert.IsFalse(loaded.IsPlaced);
            Assert.IsTrue(loaded.Collapsed);
            Assert.AreEqual(300f, loaded.Width);
        }

        [TestMethod]
        public void RelatedTablesFollowRelationshipsBothWays()
        {
            var handler = new TabularModelHandler("AdventureWorks.bim");
            var geography = handler.Model.Tables["Geography"];

            var related = DiagramGraph.RelatedTables(geography).Select(t => t.Name).ToList();

            CollectionAssert.Contains(related, "Customer", "Many side pointing at Geography.");
            CollectionAssert.Contains(related, "Sales Territory", "One side Geography points at.");
            CollectionAssert.DoesNotContain(related, "Geography");
            Assert.AreEqual(related.Count, related.Distinct().Count());
        }

        [TestMethod]
        public void CollapsedCardsShowKeyFieldsAndKeepTheCustomHeight()
        {
            var graph = LoadGraph(out _);
            var node = graph.Nodes.First(n => n.Fields.Count(f => f.IsKey) >= 1 && n.Fields.Count > 5);
            var keys = node.Fields.Count(f => f.IsKey);

            node.HasCustomSize = true;
            node.CustomHeight = 400;
            node.FitHeight();
            Assert.AreEqual(400f, node.Size.Height);

            node.Collapsed = true;
            node.FitHeight();
            Assert.AreEqual(keys, node.DisplayFields.Count);
            Assert.AreEqual(CardMetrics.HeightFor(keys), node.Size.Height);

            node.Collapsed = false;
            node.FitHeight();
            Assert.AreEqual(400f, node.Size.Height, "Expanding restores the height the user chose.");
        }

        [TestMethod]
        public void ShortCardsShowAMoreRow()
        {
            var graph = LoadGraph(out _);
            var node = graph.Nodes.First(n => n.Fields.Count > 6);
            node.HasCustomSize = true;
            node.CustomHeight = CardMetrics.HeaderHeight + 4 * CardMetrics.RowHeight + CardMetrics.Padding;
            node.FitHeight();

            var rows = node.VisibleFieldRows(out var hasMore);

            Assert.IsTrue(hasMore);
            Assert.AreEqual(3, rows, "Four rows fit: three fields and the \"+ N more\" row.");
        }

        [TestMethod]
        public void SavedNodeIsFoundByLineageTagThenName()
        {
            var layout = new DiagramLayout
            {
                Nodes =
                {
                    new DiagramNodeLayout { Table = "Old name", LineageTag = "tag-1", X = 1 },
                    new DiagramNodeLayout { Table = "Customer", X = 2 },
                },
            };

            Assert.AreEqual(1f, layout.Find("New name", "tag-1").X, "A renamed table keeps its position.");
            Assert.AreEqual(2f, layout.Find("customer", null).X, "Names match case-insensitively.");
            Assert.IsNull(layout.Find("Product", "tag-2"));
        }
    }
}
