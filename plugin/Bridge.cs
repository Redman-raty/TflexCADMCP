using System;
using System.Diagnostics;
using TFlex;

namespace TFlexMcp
{
    public sealed class Factory : PluginFactory
    {
        public override Guid ID => new Guid("6F365D3C-E456-4EFF-92D5-EA8C1DA3FB28");
        public override string Name => "T-FLEX MCP Bridge";
        public override Plugin CreateInstance() => new Bridge(this);
    }


    internal sealed class Bridge : Plugin
    {
        private PipeServer server;

        public Bridge(Factory factory) : base(factory) { }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            var operations = new CadOperations();
            string name = Environment.GetEnvironmentVariable("TFLEX_MCP_PIPE_NAME");
            if (string.IsNullOrEmpty(name))
                name = "TFlexMcp.v1." + Process.GetCurrentProcess().SessionId;
            server = new PipeServer(name, operations.Execute);
        }

        protected override void OnExited()
        {
            server?.Dispose();
            base.OnExited();
        }
    }
}
