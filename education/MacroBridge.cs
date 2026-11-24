using System;
using System.Diagnostics;
using System.Windows.Forms;
using TFlexMcp;

namespace TFlexEducation
{
    public class MacroBridge
    {
        private static PipeServer server;
        private static Timer lifetime;
        private static TFlex.Model.Document host;

        public static void Start()
        {
            if (server != null) return;
            host = TFlex.Application.ActiveDocument;
            var operations = new CadOperations(education: true);
            var name = Environment.GetEnvironmentVariable("TFLEX_MCP_PIPE_NAME");
            if (string.IsNullOrEmpty(name))
                name = "TFlexMcp.Edu.v1." + Process.GetCurrentProcess().SessionId;
            server = new PipeServer(name, operations.Execute);
            lifetime = new Timer { Interval = 1000 };
            lifetime.Tick += CheckHost;
            lifetime.Start();
        }

        public static void Stop()
        {
            if (lifetime != null)
            {
                lifetime.Dispose();
                lifetime = null;
            }
            if (server != null)
            {
                server.Dispose();
                server = null;
            }
            host = null;
        }

        private static void CheckHost(object sender, EventArgs args)
        {
            if (host == null || host.IsDisposed) Stop();
        }
    }
}
