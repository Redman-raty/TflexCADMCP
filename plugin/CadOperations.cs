using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TFlex;
using TFlex.Model;
using TFlex.Model.Model3D;
using Application = TFlex.Application;

namespace TFlexMcp
{
    internal sealed class CadOperations
    {
        private readonly Dictionary<Document, string> documentIds = new Dictionary<Document, string>();
        private readonly Dictionary<Document, string> lastRebuild = new Dictionary<Document, string>();
        private readonly string cadVersion = FileVersionInfo.GetVersionInfo(typeof(Document).Assembly.Location).FileVersion;
        private readonly bool education;

        public CadOperations(bool education = false)
        {
            this.education = education;
        }

        public object Execute(Dictionary<string, object> request)
        {
            foreach (var closed in documentIds.Keys.Where(doc => doc.IsDisposed).ToArray())
            {
                documentIds.Remove(closed);
                lastRebuild.Remove(closed);
            }
            switch (GetString(request, "method"))
            {
                case "get_document_state": return State(ActiveDocument(), true);
                case "set_variables": return SetVariables(request);
                case "batch_process_files": return Batch(request);
                case "capture_viewport": return Capture(GetString(request, "view_name"));
                case "create_drawing":
                    if (education) return DrawingOperations.Create(request);
                    break;
            }
            throw new CadError("unknown_method", "Unknown CAD bridge method.");
        }

        private static string GetString(Dictionary<string, object> request, string field)
        {
            object value;
            if (!request.TryGetValue(field, out value) || string.IsNullOrWhiteSpace(value as string))
                throw new CadError("invalid_argument", "Expected a non-empty string for " + field + ".");
            return (string)value;
        }

        private static Document ActiveDocument()
        {
            var doc = Application.ActiveDocument;
            if (doc == null || doc.IsDisposed)
                throw new CadError("no_document", "Open a document in T-FLEX CAD.");
            return doc;
        }

        private string DocumentId(Document doc)
        {
            string id;
            if (!documentIds.TryGetValue(doc, out id))
                documentIds.Add(doc, id = Guid.NewGuid().ToString("N"));
            return id;
        }

        private static double? Finite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
        }

        private static object ReadVariable(Variable variable)
        {
            object value = null;
            string unavailable = null;
            string error = variable.ErrorString;
            if (!string.IsNullOrWhiteSpace(error))
                unavailable = "variable_error";
            else if (variable.IsFunction)
                unavailable = "function_requires_arguments";
            else if (variable.IsText)
                value = variable.TextValue;
            else if (variable.IsReal)
            {
                value = Finite(variable.RealValue);
                if (value == null) unavailable = "non_finite_value";
            }
            else
                unavailable = "unsupported_variable_type";
            return new { name = variable.Name, expression = variable.Expression, value,
                value_unavailable = unavailable, error_state = variable.ErrorState,
                error = string.IsNullOrWhiteSpace(error) ? null : error };
        }

        private static object ReadBounds(Operation operation)
        {
            var geometry = operation.Geometry;
            var box = geometry == null ? null : geometry.AABoundBox;
            if (box == null || !box.Valid)
                return new { available = false, reason = "no_valid_geometry_bounds" };
            var min = box.Minimum;
            var max = box.Maximum;
            return new { available = true,
                minimum = new[] { Finite(min.X), Finite(min.Y), Finite(min.Z) },
                maximum = new[] { Finite(max.X), Finite(max.Y), Finite(max.Z) },
                size = new[] { Finite(max.X - min.X), Finite(max.Y - min.Y), Finite(max.Z - min.Z) },
                units = "m" };
        }

        private object Diagnostics(Document doc)
        {
            string result;
            lastRebuild.TryGetValue(doc, out result);
            var messages = new List<object>();
            foreach (var message in doc.Diagnostics)
                messages.Add(new { type = message.Type.ToString(), text = message.Message,
                    object_id = message.Object == null ? null : message.Object.ObjectId.ToString() });
            return new { messages, last_mcp_rebuild = result,
                last_rebuild_unavailable = result == null ? "no_mcp_rebuild_recorded" : null };
        }

        private object State(Document doc, bool interactive)
        {
            // The API collections support iteration but can throw from ICollection.CopyTo.
            var operations = new List<Operation>();
            foreach (var operation in Document3D.GetOperations(doc)) operations.Add(operation);
            var ids = new HashSet<string>(operations.Select(op => op.ObjectId.ToString()), StringComparer.Ordinal);
            var tree = operations.Select(op =>
            {
                var parents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parent in op.Parents)
                {
                    var dependency = parent.Object as Operation;
                    if (dependency != null && ids.Contains(dependency.ObjectId.ToString()))
                        parents.Add(dependency.ObjectId.ToString());
                }
                bool regenerated = op.GetRegenerationResult(false);
                return new { id = op.ObjectId.ToString(), name = op.Name, type = op.GetType().Name,
                    parent_ids = parents.OrderBy(id => id, StringComparer.Ordinal).ToArray(), regeneration_ok = regenerated,
                    bounds = ReadBounds(op) };
            }).ToArray();

            var views = new List<object>();
            if (interactive)
            {
                int i = 0;
                var active = doc.ActiveView as View3D;
                foreach (var view in doc.Views)
                    if (view is View3D)
                        views.Add(new { name = "3D:" + (++i), active = active != null && active.HWnd == view.HWnd });
            }
            var variables = new List<Variable>();
            foreach (var variable in doc.GetVariables()) variables.Add(variable);
            return new
            {
                document_id = interactive ? DocumentId(doc) : null, title = doc.Title,
                path = string.IsNullOrEmpty(doc.FilePath) ? null : Path.Combine(doc.FilePath, doc.FileName),
                cad_version = cadVersion, read_only = doc.IsReadOnly, in_change_block = doc.IsChanging(),
                variables = variables.OrderBy(v => v.Name, StringComparer.Ordinal).Select(ReadVariable).ToArray(),
                operations = tree,
                operation_tree = new { root_ids = tree.Where(op => op.parent_ids.Length == 0).Select(op => op.id).ToArray(),
                    presentation = "operation IDs and direct dependencies; CAD UI grouping is unavailable" },
                checks = new { variables_with_errors = variables.Where(v => !string.IsNullOrWhiteSpace(v.ErrorString)).Select(v => v.Name).ToArray(),
                    operations_with_regeneration_errors = tree.Where(op => !op.regeneration_ok).Select(op => op.id).ToArray() },
                sheets = doc.GetPages().Select(page => new { id = page.ObjectId.ToString(), name = page.Name,
                    type = page.PageType.ToString(), view_name = "sheet:" + page.ObjectId }).ToArray(),
                views, diagnostics = Diagnostics(doc)
            };
        }

        private object SetVariables(Dictionary<string, object> request)
        {
            string id = GetString(request, "document_id");
            var doc = ActiveDocument();
            string activeId;
            if (!documentIds.TryGetValue(doc, out activeId) || activeId != id)
                throw new CadError("document_mismatch", "The supplied ID does not identify the current active document.");
            if (doc.IsReadOnly)
                throw new CadError("read_only", "The active document is read-only.");
            if (doc.IsChanging())
                throw new CadError("document_busy", "The active document already has an open change block.");
            object raw;
            request.TryGetValue("variables", out raw);
            var expressions = raw as Dictionary<string, object>;
            if (expressions == null ||
                expressions.Count == 0 || expressions.Count > 200)
                throw new CadError("invalid_argument", "Supply between 1 and 200 named expressions.");
            var names = new List<string>();
            var changes = new List<KeyValuePair<Variable, string>>();
            var changedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in expressions)
            {
                var expression = entry.Value as string;
                if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096)
                    throw new CadError("invalid_expression", "Expected an expression of 1–4096 characters.", new { name = entry.Key });
                var variable = doc.FindVariable(entry.Key);
                if (variable == null)
                    throw new CadError("unknown_variable", "The variable does not exist.", new { name = entry.Key });
                if (!changedIds.Add(variable.ObjectId.ToString()))
                    throw new CadError("invalid_variable", "The variable appears more than once.", new { name = entry.Key });
                names.Add(variable.Name);
                changes.Add(new KeyValuePair<Variable, string>(variable, expression));
            }

            bool ownsBlock = false;
            bool canceledOpenBlock = false;
            ChangesResult rebuild = ChangesResult.UndefinedError;
            string stage = null;
            Exception failure = null;
            Exception cancelFailure = null;
            var previousDiagnostics = VariableExpressions.ReadDiagnosticIds(doc);
            try
            {
                doc.BeginChanges("MCP: set variables");
                ownsBlock = true;
                foreach (var change in changes)
                    VariableExpressions.Set(change.Key, change.Value);
                rebuild = VariableExpressions.RebuildAndCommit(doc, changes.Select(change => change.Key),
                    previousDiagnostics, out stage);
                lastRebuild[doc] = rebuild.ToString();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (ownsBlock && doc.IsChanging())
                {
                    try
                    {
                        doc.CancelChanges();
                        canceledOpenBlock = true;
                    }
                    catch (Exception ex) { cancelFailure = ex; }
                }
            }

            var result = new { document_id = id, cad_version = cadVersion,
                rebuild = failure == null ? rebuild.ToString() : null, rebuild_stage = stage,
                variables = names.Select(doc.FindVariable).Where(v => v != null).Select(ReadVariable).ToArray(),
                diagnostics = Diagnostics(doc), in_change_block = doc.IsChanging(),
                canceled_open_block = canceledOpenBlock };
            if (cancelFailure != null)
                throw new CadError("rollback_failed", cancelFailure.Message, result);
            if (failure != null)
                throw new CadError("change_failed", failure.Message, result);
            if (rebuild != ChangesResult.OK || canceledOpenBlock)
                throw new CadError("rebuild_failed", "T-FLEX rejected the changes: " + rebuild, result);
            return result;
        }

        private object Batch(Dictionary<string, object> request)
        {
            string scenario = GetString(request, "scenario");
            if (education && (scenario == "step" || scenario == "dxf"))
                throw new CadError("format_unavailable", "Use check or stl in the Student edition.");
            var extensions = new Dictionary<string, string>
                { { "check", ".json" }, { "step", ".step" }, { "dxf", ".dxf" }, { "stl", ".stl" } };
            string extension;
            if (!extensions.TryGetValue(scenario, out extension))
                throw new CadError("unknown_scenario", "Available scenarios: check, step, dxf, stl.");
            object raw;
            request.TryGetValue("files", out raw);
            var files = raw as System.Collections.IList;
            if (files == null ||
                files.Count == 0 || files.Count > 100)
                throw new CadError("invalid_argument", "Supply between 1 and 100 explicit .grs paths.");
            string root = GetString(request, "output_dir");
            if (!IsAbsoluteLocalPath(root))
                throw new CadError("invalid_argument", "output_dir must be an absolute local path.");
            string output = Path.Combine(Path.GetFullPath(root), "tflex-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            var results = new List<object>();
            for (int i = 0; i < files.Count; i++)
                results.Add(ProcessFile(files[i] as string, scenario, extension, output, i));
            return new { cad_version = cadVersion, scenario, output_dir = output, files = results };
        }

        internal static bool IsAbsoluteLocalPath(string path)
        {
            return path != null && path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/');
        }

        private object ProcessFile(string input, string scenario, string extension, string output, int index)
        {
            Document opened = null;
            string result = null;
            string error = null;
            string closeError = null;
            try
            {
                if (!IsAbsoluteLocalPath(input) || !string.Equals(Path.GetExtension(input), ".grs", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Expected an absolute local .grs file.");
                input = Path.GetFullPath(input);
                if (!File.Exists(input)) throw new FileNotFoundException("Input file not found.", input);
                var existing = new HashSet<Document>();
                foreach (var document in Application.Documents)
                    existing.Add(document);
                var doc = Application.OpenDocument(input, new OpenDocumentOptions
                {
                    ReadOnly = true, Visible = false, ForceNewObject = true, ThrowOnError = true,
                    FileLinksRefreshMode = Application.FileLinksRefreshMode.DoNotRefresh
                });
                if (doc == null || existing.Contains(doc))
                    throw new IOException("CAD did not open a separate document.");
                opened = doc;
                if (!doc.IsReadOnly) throw new IOException("CAD did not open the source read-only.");
                result = Path.Combine(output, (index + 1).ToString("D3") + "-" + Path.GetFileNameWithoutExtension(input) + extension);
                bool ok = true;
                switch (scenario)
                {
                    case "check":
                        File.WriteAllText(result, new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Serialize(State(doc, false)));
                        break;
                    case "step":
                        var step = new ExportToStep(doc) { ShowDialog = false, ExportSolidBodies = true };
                        ok = step.Export(result);
                        break;
                    case "dxf":
                        var dxf = doc.ExportToDXF;
                        dxf.ExportAllPages = true;
                        ok = dxf.Export(result);
                        break;
                    case "stl":
                        var stl = doc.ExportToSTL;
                        stl.Binary = true;
                        ok = stl.Export(result);
                        break;
                }
                if (!ok || !File.Exists(result) || new FileInfo(result).Length == 0)
                    throw new IOException("CAD did not produce the requested output. Check the format and the edition.");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (result != null && File.Exists(result))
                {
                    try { File.Delete(result); }
                    catch (IOException) { error += " An incomplete output remains at " + result; }
                    catch (UnauthorizedAccessException) { error += " An incomplete output remains at " + result; }
                }
                result = null;
            }
            finally
            {
                if (opened != null && !opened.IsDisposed)
                {
                    try { opened.Close(); }
                    catch (Exception ex) { closeError = ex.Message; }
                }
            }
            return new { input, ok = error == null && closeError == null, output = result, error, close_error = closeError };
        }

        private object Capture(string name)
        {
            var doc = ActiveDocument();
            string directory = Path.Combine(Path.GetTempPath(), "tflex-mcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = null;
            try
            {
                bool ok;
                string kind;
                string resolved;
                if (name == "3D" || name.StartsWith("3D:", StringComparison.Ordinal))
                {
                    var views = new List<View3D>();
                    foreach (var view in doc.Views)
                        if (view is View3D) views.Add((View3D)view);
                    View3D chosen;
                    int index;
                    if (name == "3D")
                        chosen = doc.ActiveView as View3D ?? (views.Count == 1 ? views[0] : null);
                    else
                        chosen = int.TryParse(name.Substring(3), out index) && index > 0 && index <= views.Count
                            ? views[index - 1] : null;
                    if (chosen == null) throw new CadError("view_not_found", "Choose an open 3D view from get_document_state.");
                    kind = "3d";
                    resolved = "3D:" + (views.FindIndex(v => v.HWnd == chosen.HWnd) + 1);
                    file = Path.Combine(directory, "viewport.bmp");
                    ok = new ExportToBitmap3D(chosen) { PixelSize = true, Width = 1024, Height = 768 }.Export(file);
                }
                else
                {
                    var pages = doc.GetPages().Where(page => name == "sheet:" + page.ObjectId ||
                        (!name.StartsWith("sheet:", StringComparison.Ordinal) && page.Name == name)).ToArray();
                    if (pages.Length != 1)
                        throw new CadError("view_not_found", "Choose a unique sheet name or sheet ID from get_document_state.");
                    kind = "sheet";
                    resolved = "sheet:" + pages[0].ObjectId;
                    file = Path.Combine(directory, "sheet.png");
                    var exporter = doc.ExportToBitmap;
                    exporter.Page = pages[0];
                    exporter.Resolution = 96;
                    ok = exporter.Export(file, ImageExport.None, ImageExportFormat.Png);
                }
                if (!ok || !File.Exists(file))
                    throw new CadError("capture_failed", "T-FLEX could not export the requested view.");
                if (new FileInfo(file).Length > 24 * 1024 * 1024)
                    throw new CadError("capture_too_large", "The exported PNG exceeds 24 MiB.");
                byte[] png;
                if (kind == "3d")
                {
                    // ExportToBitmap3D in 18.0.56 writes BMP even when the extension is .png.
                    using (var bitmap = System.Drawing.Image.FromFile(file))
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                        png = stream.ToArray();
                    }
                }
                else
                    png = File.ReadAllBytes(file);
                if (png.Length > 24 * 1024 * 1024)
                    throw new CadError("capture_too_large", "The exported PNG exceeds 24 MiB.");
                if (png.Length < 24 || !png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new CadError("capture_failed", "T-FLEX did not return a PNG.");
                return new { document_id = DocumentId(doc), cad_version = cadVersion, view_name = resolved, kind,
                    mime_type = "image/png", png_base64 = Convert.ToBase64String(png) };
            }
            finally
            {
                if (file != null) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
