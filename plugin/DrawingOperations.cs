using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TFlex;
using TFlex.Model;
using TFlex.Model.Model2D;
using Application = TFlex.Application;

namespace TFlexMcp
{
    internal static class DrawingOperations
    {
        public static object Create(Dictionary<string, object> request)
        {
            object folder;
            object raw;
            request.TryGetValue("output_dir", out folder);
            if (!CadOperations.IsAbsoluteLocalPath(folder as string))
                throw new CadError("invalid_argument", "output_dir must be an absolute local path.");
            if (!request.TryGetValue("drawing", out raw))
                throw new CadError("invalid_argument", "A drawing specification is required.");
            var spec = new JavaScriptSerializer().ConvertToType<DrawingSpec>(raw);
            Validate(spec);

            string output = Path.Combine(Path.GetFullPath((string)folder), "drawing-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(output, "drawing.grs");
            Directory.CreateDirectory(output);
            var doc = Application.NewDocument(new NewDocumentOptions
                { Use3DPrototype = false, Visible = true, ThrowOnError = true });
            if (doc == null || doc.IsChanging())
                throw new CadError("new_document_failed", "Could not start an editable drawing.");
            bool saved = false;
            try
            {
                var built = new List<BuiltSheet>();
                doc.BeginChanges("MCP: create drawing");
                try
                {
                    var defaultPage = doc.GetPages().FirstOrDefault(page => page.PageType == PageType.Normal);
                    foreach (var sheet in spec.sheets)
                    {
                        var page = built.Count == 0 && defaultPage != null ? defaultPage : new Page(doc, PageType.Normal);
                        page.Name = sheet.name;
                        page.Rectangle = new TFlex.Drawing.Rectangle(0, 0, sheet.width, sheet.height);
                        built.Add(new BuiltSheet(doc, page, sheet));
                    }
                    var result = doc.EndChanges(new EndChangesOptions
                        { Regenerate = true, Unconditionally = false, ShowLastErrorDialog = false });
                    if (result != ChangesResult.OK)
                        throw new CadError("drawing_rebuild_failed", result.ToString());
                }
                finally
                {
                    if (doc.IsChanging()) doc.CancelChanges();
                }
                if (!doc.SaveAs(path) || !File.Exists(path))
                    throw new CadError("drawing_save_failed", "T-FLEX did not save the drawing.", new { path });
                saved = true;
                var sheets = new List<object>();
                for (int i = 0; i < built.Count; i++)
                {
                    string preview = Path.Combine(output, "sheet-" + (i + 1).ToString("D2", CultureInfo.InvariantCulture) + ".png");
                    var sheet = built[i];
                    string previewError = null;
                    try
                    {
                        var exporter = doc.ExportToBitmap;
                        exporter.Page = sheet.Page;
                        exporter.Resolution = 120;
                        exporter.PageRectangle = sheet.Page.Rectangle;
                        if (!exporter.Export(preview, ImageExport.None, ImageExportFormat.Png) || !File.Exists(preview))
                            previewError = "T-FLEX did not export the sheet preview.";
                    }
                    catch (Exception ex) { previewError = ex.Message; }
                    sheets.Add(new { name = sheet.Page.Name, view_name = "sheet:" + sheet.Page.ObjectId,
                        entities = sheet.Created, dimension_values_mm = sheet.Dimensions.Select(dim => dim.Value).ToArray(),
                        preview = previewError == null ? preview : null, preview_error = previewError });
                }
                return new { path, output_dir = output, rebuild = "OK", sheets };
            }
            finally
            {
                if (!saved && !doc.IsDisposed) doc.Close();
            }
        }

        private static void Validate(DrawingSpec spec)
        {
            if (spec == null || spec.sheets == null || spec.sheets.Count < 1 || spec.sheets.Count > 12)
                throw new CadError("invalid_drawing", "Supply 1–12 sheets.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int primitiveCount = 0;
            foreach (var sheet in spec.sheets)
            {
                if (sheet == null || string.IsNullOrWhiteSpace(sheet.name) || sheet.name.Length > 60 ||
                    !names.Add(sheet.name) || !(sheet.width >= 10 && sheet.width <= 1200) ||
                    !(sheet.height >= 10 && sheet.height <= 1200) || sheet.entities == null || sheet.entities.Count == 0)
                    throw new CadError("invalid_drawing", "Expected a unique sheet name, page size in mm, and entities.");
                foreach (var entity in sheet.entities)
                {
                    if (entity == null || entity.points == null || entity.points.Any(p => p == null ||
                        p.Length != 2 || !(p[0] >= 0 && p[0] <= sheet.width && p[1] >= 0 && p[1] <= sheet.height)))
                        throw new CadError("invalid_drawing", "Points must be inside the named sheet.");
                    int count = entity.points.Count;
                    switch (entity.kind)
                    {
                        case "line": if (count != 2) throw InvalidEntity(entity); break;
                        case "polyline": if (count < 2 || (entity.closed && count < 3)) throw InvalidEntity(entity); break;
                        case "circle": if (count != 1 || !(entity.radius > 0 && entity.radius <= 600)) throw InvalidEntity(entity); break;
                        case "arc": if (count != 3) throw InvalidEntity(entity); break;
                        case "text": if (count != 1 || string.IsNullOrWhiteSpace(entity.text) || entity.text.Length > 240) throw InvalidEntity(entity); break;
                        case "dimension": if (count != 2 || !(Math.Abs(entity.offset) <= 600) ||
                                !(entity.align == "horizontal" || entity.align == "vertical" || entity.align == "distance"))
                                throw InvalidEntity(entity); break;
                        default: throw InvalidEntity(entity);
                    }
                    if (entity.kind == "line" || entity.kind == "polyline" || entity.kind == "arc" || entity.kind == "dimension")
                    {
                        for (int i = 1; i < count; i++)
                            if (entity.points[i].SequenceEqual(entity.points[i - 1])) throw InvalidEntity(entity);
                        if (entity.closed && entity.points[0].SequenceEqual(entity.points[count - 1])) throw InvalidEntity(entity);
                    }
                    primitiveCount += entity.kind == "polyline" ? count - (entity.closed ? 0 : 1) : 1;
                    if (primitiveCount > 6000)
                        throw new CadError("invalid_drawing", "The drawing exceeds 6000 generated entities.");
                }
            }
        }

        private static CadError InvalidEntity(DrawingEntity entity)
        {
            return new CadError("invalid_entity", "Invalid geometry for " + entity.kind + ".");
        }

        private sealed class BuiltSheet
        {
            private readonly Document doc;
            private readonly Dictionary<Tuple<double, double>, Node> nodes = new Dictionary<Tuple<double, double>, Node>();
            public Page Page { get; private set; }
            public int Created { get; private set; }
            public readonly List<LinearDimension> Dimensions = new List<LinearDimension>();

            public BuiltSheet(Document doc, Page page, SheetSpec spec)
            {
                this.doc = doc;
                Page = page;
                foreach (var entity in spec.entities) Draw(entity);
            }

            private Node Point(double[] point)
            {
                var key = Tuple.Create(point[0], point[1]);
                Node node;
                if (!nodes.TryGetValue(key, out node))
                {
                    node = new FreeNode(doc, point[0], point[1]) { Page = Page };
                    nodes.Add(key, node);
                }
                return node;
            }

            private void Add(Outline outline, DrawingEntity entity)
            {
                outline.Page = Page;
                if (entity.width > 0) outline.LineWidth = entity.width;
                Created++;
            }

            private void Draw(DrawingEntity entity)
            {
                var points = entity.points;
                switch (entity.kind)
                {
                    case "line":
                    case "polyline":
                        for (int i = 1; i < points.Count; i++)
                            Add(new SegmentOutline(doc, Point(points[i - 1]), Point(points[i])), entity);
                        if (entity.closed)
                            Add(new SegmentOutline(doc, Point(points[points.Count - 1]), Point(points[0])), entity);
                        break;
                    case "circle":
                        Add(new CircleOutline(doc, Point(points[0]), entity.radius), entity);
                        break;
                    case "arc":
                        Add(new ThreePointArcOutline(doc, Point(points[0]), Point(points[1]), Point(points[2])), entity);
                        break;
                    case "text":
                        new LineText(doc) { Page = Page, X = points[0][0], Y = points[0][1], TextValue = entity.text };
                        Created++;
                        break;
                    case "dimension":
                        var dim = new LinearDimension(doc) { Page = Page };
                        var align = entity.align == "horizontal" ? DimensionAlignType.Horizontal :
                            entity.align == "vertical" ? DimensionAlignType.Vertical : DimensionAlignType.Distance;
                        dim.SetTwoNodes(Point(points[0]), Point(points[1]), align);
                        dim.Offset1 = entity.offset;
                        Dimensions.Add(dim);
                        Created++;
                        break;
                }
            }
        }
    }

    public sealed class DrawingSpec { public List<SheetSpec> sheets { get; set; } }
    public sealed class SheetSpec
    {
        public string name { get; set; }
        public double width { get; set; }
        public double height { get; set; }
        public List<DrawingEntity> entities { get; set; }
    }
    public sealed class DrawingEntity
    {
        public string kind { get; set; }
        public List<double[]> points { get; set; }
        public bool closed { get; set; }
        public double width { get; set; }
        public double radius { get; set; }
        public double offset { get; set; }
        public string align { get; set; }
        public string text { get; set; }
    }
}
