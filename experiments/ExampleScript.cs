using System;
using TFlex.Model;
using TFlex.Model.Model3D;

namespace TrustedJob
{
    public static class Script
    {
        public static void Run(Document document)
        {
            if (Document3D.GetOperations(document).Count == 0)
                throw new InvalidOperationException("No 3D operations.");
        }
    }
}
