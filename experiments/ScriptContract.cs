using System;
using TFlex.Model;

namespace TrustedJob
{
    internal static class ScriptContract
    {
        // The compiler checks the signature before any file is opened in CAD.
        internal static Action<Document> Run => Script.Run;
    }
}
