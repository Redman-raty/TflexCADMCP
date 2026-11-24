using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using TFlexMcp;

internal static class TransportCheck
{
    [STAThread]
    private static void Main(string[] args)
    {
        int uiThread = Thread.CurrentThread.ManagedThreadId;
        string imageDir = args.Length > 1 ? args[1] : null;
        using (var server = new PipeServer(args[0], request => Reply(request, uiThread, imageDir)))
            Application.Run();
    }

    private static object Reply(Dictionary<string, object> request, int uiThread, string imageDir)
    {
        if (Thread.CurrentThread.ManagedThreadId != uiThread)
            throw new InvalidOperationException("Request ran away from the UI thread.");
        switch ((string)request["method"])
        {
            case "get_document_state":
                return new { sample = new string('Д', 160000), transport_check = true };
            case "capture_viewport":
                string file = (string)request["view_name"] == "3D:1" ? "viewport.png" : "sheet.png";
                return new
                {
                    view_name = request["view_name"],
                    png_base64 = imageDir == null
                        ? "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aR2kAAAAASUVORK5CYII="
                        : Convert.ToBase64String(File.ReadAllBytes(Path.Combine(imageDir, file)))
                };
            case "set_variables":
                throw new CadError("transport_test_error", "Expected test response.", new { received = request["variables"] });
            case "batch_process_files":
                return new { request = request["files"], scenario = request["scenario"], output_dir = request["output_dir"] };
            default:
                throw new CadError("unknown_method", "Unexpected method.");
        }
    }
}
