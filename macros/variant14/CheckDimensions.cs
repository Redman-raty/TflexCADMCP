using System;
using System.Collections.Generic;
using System.IO;
using TFlex.Model.Model3D;

public class Variant14Dimensions
{
    public static void CheckDimensions()
    {
        var document = TFlex.Application.ActiveDocument;
        if (document == null)
            throw new InvalidOperationException("Откройте документ с моделью.");

        var report = new List<string> { "Документ: " + document.Title };
        foreach (Operation operation in Document3D.GetOperations(document))
        {
            var box = operation.Geometry.AABoundBox;
            if (box == null || !box.Valid)
            {
                report.Add(operation.Name + ": габариты недоступны");
                continue;
            }

            var min = box.Minimum;
            var max = box.Maximum;
            report.Add(string.Format(
                "{0}: X [{1:0.###}; {2:0.###}], Y [{3:0.###}; {4:0.###}], Z [{5:0.###}; {6:0.###}]",
                operation.Name, min.X, max.X, min.Y, max.Y, min.Z, max.Z));
        }

        if (report.Count == 1)
            report.Add("В документе нет 3D-операций.");

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "TFlex-Variant14-dimensions.txt"), report);
    }
}
