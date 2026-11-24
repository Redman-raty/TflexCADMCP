using System;
using TFlex.Model;
using TFlex.Model.Model3D;

namespace Homework4
{
    public class Variant14Section
    {
        public static void PrepareHalfSection()
        {
            var doc = TFlex.Application.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Откройте 3D-модель варианта 14.");

            const string nodeName = "В14 — ось секущей плоскости Y=0";
            foreach (var obj in doc.GetObjects())
                if (obj is CoordinateNode3D && obj.Name == nodeName)
                    return;

            doc.BeginChanges("В14 — осевая точка разреза");
            try
            {
                new CoordinateNode3D(doc) { Name = nodeName, X = 0, Y = 0, Z = 80 };
            }
            catch
            {
                doc.CancelChanges();
                throw;
            }

            var result = doc.EndChanges(false, true);
            if (!string.Equals(result.ToString(), "OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Пересчёт: " + result);
        }
    }
}
