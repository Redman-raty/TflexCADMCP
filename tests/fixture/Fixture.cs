using System;
using System.IO;
using System.Runtime.InteropServices;
using TFlex;
using Timer = System.Windows.Forms.Timer;

namespace TFlexMcpFixture
{
    public sealed class Factory : PluginFactory
    {
        public override Guid ID => new Guid("6225DF8B-FE2D-4AF8-8CED-F726662EE57E");
        public override string Name => "T-FLEX MCP Test Fixture";
        public override Plugin CreateInstance() => new Fixture(this);
    }

    internal sealed class Fixture : Plugin
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr window);

        private Timer timer;

        public Fixture(Factory factory) : base(factory) { }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            timer = new Timer { Interval = 300 };
            timer.Tick += CreateWhenReady;
            timer.Start();
        }

        protected override void OnExited()
        {
            timer?.Dispose();
            base.OnExited();
        }

        private void CreateWhenReady(object sender, EventArgs args)
        {
            var main = Application.ActiveMainWindow;
            if (main == null || !IsWindowEnabled(main.Handle)) return;
            timer.Dispose();
            var directory = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "fixtures");
            Directory.CreateDirectory(directory);
            try
            {
                var first = Path.Combine(directory, "mcp-block.grs");
                var second = Path.Combine(directory, "mcp-block-b.grs");
                var working = Path.Combine(directory, "mcp-block-working.grs");
                if (File.Exists(first) || File.Exists(second) || File.Exists(working))
                    throw new IOException("Fixture paths already exist. Open mcp-block-working.grs or use a fresh output directory.");
                FixtureModel.Create(second, 45, false);
                FixtureModel.Create(first, 40, false);
                FixtureModel.Create(working, 40, true);
                File.WriteAllText(Path.Combine(directory, "fixture-result.txt"), "OK");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "fixture-result.txt"), ex.ToString());
                System.Windows.Forms.MessageBox.Show(ex.Message, "T-FLEX MCP Test Fixture");
            }
        }

    }
}
