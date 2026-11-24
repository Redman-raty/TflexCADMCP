using System;
using TFlex.Model.Model3D;

public class Variant14Repair
{
    public static void CenterBase()
    {
        var doc = TFlex.Application.ActiveDocument;
        if (doc == null)
            throw new InvalidOperationException("Откройте документ с корпусом.");

        BoundedExtrusion body = null;
        foreach (Operation operation in Document3D.GetOperations(doc))
        {
            if (operation.Name == "В14 — корпус, 140 мм")
                body = operation as BoundedExtrusion;
        }
        if (body == null)
            throw new InvalidOperationException("В документе нет выталкивания корпуса В14.");

        doc.BeginChanges("В14 — симметричное выталкивание корпуса");
        try
        {
            body.Bound1.Offset = 70.0;
            body.Bound2.Offset = 70.0;
        }
        catch
        {
            doc.CancelChanges();
            throw;
        }
        var result = doc.EndChanges(false, true);
        if (!string.Equals(result.ToString(), "OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Пересчёт корпуса: " + result);
    }
}
