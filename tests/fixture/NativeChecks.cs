namespace TFlexMcpFixture
{
    // Paste after FixtureModel.cs in a temporary CAD macro project.
    public class NativeChecks
    {
        public static void Run()
        {
            var directory = @"__NATIVE_CHECK_DIRECTORY__";
            Directory.CreateDirectory(directory);
            var log = new System.Collections.Generic.List<string>();
            try
            {
                string first = Path.Combine(directory, "mcp-block.grs");
                string second = Path.Combine(directory, "mcp-block-b.grs");
                string working = Path.Combine(directory, "mcp-block-working.grs");
                if (File.Exists(first) || File.Exists(second) || File.Exists(working))
                    throw new IOException("Choose a fresh test output directory.");
                FixtureModel.Create(first, 40, false);
                FixtureModel.Create(second, 45, false);
                var model = FixtureModel.Create(working, 40, true);
                bool passed = Check(log, "Geometry", () =>
                {
                    CheckLength(model, 100, log);
                    string firstUpdate = Change(model, "120", null);
                    Require(firstUpdate == "OK", "L=120 failed: " + firstUpdate);
                    CheckLength(model, 120, log);
                    string bad = Change(model, "(", "777");
                    log.Add("Controlled error: " + bad);
                    Require(bad != "OK", "CAD accepted the invalid expression.");
                    CheckLength(model, 120, log);
                    Require(model.FindVariable("CHECK_PROBE").RealValue == 1, "An earlier change in the failed block survived.");
                    Require(!model.IsChanging(), "A change block remained open.");
                    string nextUpdate = Change(model, "130", null);
                    Require(nextUpdate == "OK", "Next update failed: " + nextUpdate);
                    CheckLength(model, 130, log);
                });

                string exportDirectory = Path.Combine(directory, "exports");
                Directory.CreateDirectory(exportDirectory);
                foreach (string path in new[] { first, second })
                {
                    passed &= Check(log, "STL " + Path.GetFileName(path), () =>
                    {
                        var source = Application.OpenDocument(path,
                            new OpenDocumentOptions { ReadOnly = true, Visible = false, ThrowOnError = true });
                        if (source == null) throw new IOException("Cannot open " + path);
                        try
                        {
                            Require(source.IsReadOnly, "The source is writable.");
                            string output = Path.Combine(exportDirectory, Path.GetFileNameWithoutExtension(path) + ".stl");
                            var stl = source.ExportToSTL;
                            stl.Binary = true;
                            Require(stl.Export(output) && File.Exists(output), "STL export failed: " + path);
                            log.Add("STL: " + Path.GetFileName(output) + " " + new FileInfo(output).Length + " bytes");
                        }
                        finally { source.Close(); }
                    });
                }

                passed &= Check(log, "3D PNG", () =>
                {
                    var view = model.ActiveView as View3D;
                    if (view == null) view = (View3D)model.Open3DView();
                    view.ZoomAll();
                    var raster3d = new ExportToBitmap3D(view) { PixelSize = true, Width = 1024, Height = 768 };
                    string bmp = Path.Combine(directory, "viewport.bmp");
                    Require(raster3d.Export(bmp), "3D bitmap export failed.");
                    using (var bitmap = System.Drawing.Image.FromFile(bmp))
                        bitmap.Save(Path.Combine(directory, "viewport.png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.Delete(bmp);
                });
                passed &= Check(log, "Sheet PNG", () =>
                {
                    var sheet = model.GetPages().First(page => page.Name == "MCP check sheet");
                    var raster2d = model.ExportToBitmap;
                    raster2d.Page = sheet;
                    raster2d.Resolution = 96;
                    Require(raster2d.Export(Path.Combine(directory, "sheet.png"), ImageExport.None, ImageExportFormat.Png),
                        "Sheet PNG export failed.");
                });
                log.Add(passed ? "OK" : "FAILED");
            }
            catch (Exception ex)
            {
                log.Add(ex.ToString());
            }
            finally
            {
                File.WriteAllLines(Path.Combine(directory, "native-check.txt"), log);
            }
        }

        private static bool Check(System.Collections.Generic.List<string> log, string name, Action run)
        {
            try
            {
                run();
                log.Add(name + ": OK");
                return true;
            }
            catch (Exception ex)
            {
                log.Add(name + ": " + ex);
                return false;
            }
        }

        private static string Change(Document doc, string expression, string probe)
        {
            Require(!doc.IsChanging(), "There is an active change block.");
            var previousDiagnostics = TFlexMcp.VariableExpressions.ReadDiagnosticIds(doc);
            doc.BeginChanges("MCP native check");
            try
            {
                var changed = new System.Collections.Generic.List<Variable>();
                if (probe != null)
                {
                    var probeVariable = doc.FindVariable("CHECK_PROBE");
                    TFlexMcp.VariableExpressions.Set(probeVariable, probe);
                    changed.Add(probeVariable);
                }
                var length = doc.FindVariable("L");
                TFlexMcp.VariableExpressions.Set(length, expression);
                changed.Add(length);
                string stage;
                return TFlexMcp.VariableExpressions.RebuildAndCommit(doc, changed, previousDiagnostics, out stage).ToString();
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                if (doc.IsChanging()) doc.CancelChanges();
            }
        }

        private static void CheckLength(Document doc, double expected, System.Collections.Generic.List<string> log)
        {
            var variable = doc.FindVariable("L");
            var operations = Document3D.GetOperations(doc);
            var solid = operations.OfType<Block>().SingleOrDefault();
            Require(variable != null && solid != null, "Fixture " + doc.Title + " has " +
                operations.Count + " 3D operations. Expected one block and the variable L.");
            var box = solid.Geometry.AABoundBox;
            Require(box != null && box.Valid, "No solid bounding box.");
            var min = box.Minimum;
            var max = box.Maximum;
            var lengthMillimeters = (max.X - min.X) * 1000.0;
            log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "L={0}, evaluated={1}, bounds={2} x {3} x {4} mm, regenerated={5}",
                variable.Expression, variable.RealValue, lengthMillimeters,
                (max.Y - min.Y) * 1000.0, (max.Z - min.Z) * 1000.0, solid.GetRegenerationResult(false)));
            Require(Math.Abs(variable.RealValue - expected) < 1e-6, "Unexpected evaluated variable.");
            Require(Math.Abs(lengthMillimeters - expected) < 1e-6, "Unexpected solid length in mm: " + lengthMillimeters);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
