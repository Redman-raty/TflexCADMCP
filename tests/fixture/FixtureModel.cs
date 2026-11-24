using System;
using System.IO;
using System.Linq;
using TFlex;
using TFlex.Model;
using TFlex.Model.Model2D;
using TFlex.Model.Model3D;

namespace TFlexMcpFixture
{
    public static class FixtureModel
    {
        public static Document Create(string path, double width, bool keepOpen)
        {
            var doc = Application.NewDocument(new NewDocumentOptions { Use3DPrototype = true, Visible = true, ThrowOnError = true });
            if (doc == null || doc.IsChanging()) throw new InvalidOperationException("Could not create an editable 3D fixture.");
            var saved = false;
            try
            {
                doc.BeginChanges("Create MCP fixture");
                try
                {
                    var length = new Variable(doc, "L", 100.0) { External = true };
                    new Variable(doc, "CHECK_PROBE", 1.0);
                    new Block(doc)
                    {
                        Name = "MCP test block", Cube = false,
                        XSize = new Parameter(length), YSize = width, ZSize = 30.0
                    };

                    var sheet = new Page(doc, PageType.Normal) { Name = "MCP check sheet" };
                    Rectangle(doc, sheet, 20, 20, 120, 65);
                }
                catch
                {
                    if (doc.IsChanging()) doc.CancelChanges();
                    throw;
                }
                var rebuild = doc.EndChanges(new EndChangesOptions
                {
                    Regenerate = true, Unconditionally = false, ShowLastErrorDialog = false
                });
                if (rebuild != ChangesResult.OK) throw new InvalidOperationException("Fixture rebuild: " + rebuild);
                if (!doc.SaveAs(path)) throw new IOException("Could not save " + path);
                saved = true;
                if (keepOpen && !(doc.ActiveView is View3D)) doc.Open3DView();
                return keepOpen ? doc : null;
            }
            finally
            {
                if (doc.IsChanging()) doc.CancelChanges();
                if (!keepOpen || !saved) doc.Close();
            }
        }

        private static void Rectangle(Document doc, Page page, double x, double y, double right, double top)
        {
            var points = new[]
            {
                new FreeNode(doc, x, y) { Page = page }, new FreeNode(doc, right, y) { Page = page },
                new FreeNode(doc, right, top) { Page = page }, new FreeNode(doc, x, top) { Page = page }
            };
            for (int i = 0; i < points.Length; i++)
            {
                var edge = new SegmentOutline(doc, points[i], points[(i + 1) % points.Length]) { Page = page };
                edge.Regenerate(true);
            }
        }
    }
}
