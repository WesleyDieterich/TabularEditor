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
                node.Size = new SizeF(DiagramCanvas.CardWidth, DiagramCanvas.CardHeight(node.Fields.Count));
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
