using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using TFlex;
using TFlex.Command;
using TFlex.Model;
using Application = TFlex.Application;

namespace TFlexMcpTrusted
{
    public sealed class Factory : PluginFactory
    {
        public override Guid ID => new Guid("6F1767D7-FC69-4898-A52E-ED6D057CD8F0");
        public override string Name => "T-FLEX MCP Trusted Scripts";
        public override Plugin CreateInstance() => new TrustedRunner(this);
    }

    internal sealed class TrustedRunner : Plugin
    {
        private const int RunCommand = 1;

        public TrustedRunner(Factory factory) : base(factory) { }

        protected override void OnCreateTools()
        {
            RegisterCommand(RunCommand, "Run trusted CAD job", SystemIcons.Application, SystemIcons.Application);
            RibbonBar.ApplicationsTab.AddGroup("MCP experiments").AddButton(RunCommand, this);
        }

        protected override void OnUpdateCommand(CommandUI ui)
        {
            if (ui != null && ui.ID == RunCommand) ui.Enable();
        }

        protected override void OnCommand(Document document, int id)
        {
            if (id != RunCommand) return;
            using (var dialog = new OpenFileDialog { Filter = "Trusted CAD job (job.txt)|job.txt" })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try { RunJob(dialog.FileName); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Trusted CAD job"); }
            }
        }

        private static void RunJob(string manifest)
        {
            string directory = Path.GetDirectoryName(manifest);
            var files = File.ReadAllLines(manifest, Encoding.UTF8);
            if (files.Length < 1 || files.Length > 100 || files.Any(file =>
                file != Path.GetFileName(file) || !string.Equals(Path.GetExtension(file), ".grs", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Expected 1–100 local .grs copies beside job.txt.");
            using (File.Open(Path.Combine(directory, "started"), FileMode.CreateNew)) { }
            // Load a uniquely compiled assembly from memory so job paths are never reused by the CLR.
            var assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(directory, "TrustedJob.dll")));
            var method = assembly.GetType("TrustedJob.Script", true)
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Document) }, null);
            if (method == null || method.ReturnType != typeof(void))
                throw new ArgumentException("Expected public static void TrustedJob.Script.Run(Document).");
            var run = (Action<Document>)Delegate.CreateDelegate(typeof(Action<Document>), method);
            var report = new List<object>();
            foreach (string file in files)
                report.Add(RunFile(Path.Combine(directory, file), run));
            File.WriteAllText(Path.Combine(directory, "result.json"), new JavaScriptSerializer().Serialize(report), Encoding.UTF8);
        }

        private static object RunFile(string path, Action<Document> run)
        {
            Document document = null;
            string error = null;
            string closeError = null;
            try
            {
                var existing = new HashSet<Document>();
                foreach (var open in Application.Documents) existing.Add(open);
                var opened = Application.OpenDocument(path, new OpenDocumentOptions
                {
                    Visible = false, ThrowOnError = true, ForceNewObject = true,
                    FileLinksRefreshMode = Application.FileLinksRefreshMode.DoNotRefresh
                });
                if (opened == null || existing.Contains(opened))
                    throw new IOException("CAD did not open a separate copy.");
                document = opened;
                run(document);
                if (!document.IsDisposed && document.IsChanging())
                    throw new InvalidOperationException("The script left a change block open.");
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                if (document != null && !document.IsDisposed)
                {
                    try
                    {
                        if (document.IsChanging()) document.CancelChanges();
                        document.Close();
                    }
                    catch (Exception ex) { closeError = ex.Message; }
                }
            }
            return new { file = path, ok = error == null && closeError == null, error, close_error = closeError };
        }
    }
}
