using System;
using TFlex.Model;
using TFlex.Model.Model2D;
using TFlex.Model.Model3D;

namespace Homework4
{
    public class Variant14Annotations
    {
        private const string Title = "Домашнее задание № 4. Вариант № 14";

        public static void AnnotateSheet()
        {
            var doc = TFlex.Application.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Откройте документ варианта 14.");

            Projection main = null;
            Projection side = null;
            Projection top = null;
            foreach (var projection in Document3D.GetProjections(doc))
            {
                if (projection.Name == "Проекция — Главный вид") main = projection;
                if (projection.Name == "Проекция — Вид слева") side = projection;
                if (projection.Name == "Проекция — Вид сверху") top = projection;
            }
            if (main == null || side == null || top == null ||
                main.Page.Name != "Чертёж" || side.Page.Name != main.Page.Name || top.Page.Name != main.Page.Name)
                throw new InvalidOperationException("Подготовьте именованные проекции на листе «Чертёж».");

            var page = main.Page;
            foreach (var obj in doc.GetObjects())
            {
                var label = obj as LineText;
                if (label != null && label.Page.Name == page.Name && label.TextValue == Title)
                    throw new InvalidOperationException("Оформление уже нанесено.");
            }

            var mainBox = main.BoundRect;
            var sideBox = side.BoundRect;
            var topBox = top.BoundRect;
            double mx = (mainBox.Left + mainBox.Right) / 2;
            double sx = (sideBox.Left + sideBox.Right) / 2;
            double tx = (topBox.Left + topBox.Right) / 2;
            double ty = (topBox.Bottom + topBox.Top) / 2;
            // Bounds include the 7 mm standard frame margin of the source sheet.
            double mb = mainBox.Top - 87;
            double sb = sideBox.Top - 87;
            double mz = mb + 20;
            double sz = sb + 20;

            doc.BeginChanges("В14 — оформление листа");
            try
            {
                Axis(doc, page, mx, mb - 4, mx, mb + 85);
                Axis(doc, page, mx - 74, mz, mx + 74, mz);
                Axis(doc, page, sx, sb - 4, sx, sb + 85);
                Axis(doc, page, sx - 38, sz, sx + 38, sz);
                Axis(doc, page, tx - 74, ty, tx + 74, ty);
                Axis(doc, page, tx, ty - 74, tx, ty + 74);

                Size(doc, page, mx - 70, mb, mx + 70, mb, true, 12);
                Size(doc, page, sx - 35, sb, sx + 35, sb, true, 7);
                Size(doc, page, sx - 70, sz - 10, sx + 70, sz - 10, true, 25);
                Size(doc, page, sx - 55, sz, sx + 55, sz, true, 42);
                Size(doc, page, sx + 42, sb + 80, sx + 60, sb, false, -50);
                Size(doc, page, sx + 60, sb, sx + 60, sz, false, 22);
                Size(doc, page, mx + 23.376, mz + 35, mx + 23.376, mz + 60, false, -10);

                Note(doc, page, "R3", mx - 46, mz + 45.5, mx - 56, mz + 57);
                Note(doc, page, "R3", mx + 44, mz + 59, mx + 54, mz + 66);
                Note(doc, page, "M50×3", mx + 25, mz + 39, mx + 76, mz + 35);
                Note(doc, page, "M30×2", mx + 15, mz + 30, mx + 76, mz + 5);
                Note(doc, page, "M56×3", sx - 19.8, sz + 19.8, sx - 58, sz + 21);
                Note(doc, page, "R45", sx + 36, sz + 27, sx + 44, sz + 35);
                Note(doc, page, "R10", sx - 67.1, sb + 2.9, sx - 72, sb - 4);
                Note(doc, page, "2 канавки R5", sx + 58.54, sz - 3.54, sx + 85, sb - 9);
                double angle = Math.Atan2(36, 50);
                Note(doc, page, "%%c90", tx - 45 * Math.Cos(angle), ty - 45 * Math.Sin(angle), tx + 50, ty + 36);

                Label(doc, page, Title, 115, 282, 6);
                Label(doc, page, "М 1:1", 385, 282, 5);
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

        private static FreeNode Point(Document doc, Page page, double x, double y)
        {
            return new FreeNode(doc, x, y) { Page = page };
        }

        private static void Size(Document doc, Page page, double x1, double y1, double x2, double y2, bool horizontal, double offset)
        {
            var dim = new LinearDimension(doc) { Page = page };
            dim.SetTwoNodes(Point(doc, page, x1, y1), Point(doc, page, x2, y2),
                horizontal ? DimensionAlignType.Horizontal : DimensionAlignType.Vertical);
            dim.FontStyle.Size = 3.5;
            dim.FontStyle.Italic = true;
            dim.StartArrowSize = 2.5;
            dim.EndArrowSize = 2.5;
            dim.Accuracy = 0;
            dim.MinDigits = 0;
            dim.Offset1 = offset;
        }

        private static void Note(Document doc, Page page, string text, double x, double y, double shelfX, double shelfY)
        {
            var note = new LeaderNote(doc) { Page = page, TextOnLeader = text, ArrowSize = 2.5, ArrowType = 1 };
            note.Direction = shelfX >= x ? LeaderNoteDirection.ToRight : LeaderNoteDirection.ToLeft;
            note.FontStyle.Size = 3.5;
            note.FontStyle.Italic = true;
            note.SetArrowAbsolute(x, y);
            note.SetLeaderAbsolute(shelfX, shelfY);
        }

        private static void Label(Document doc, Page page, string text, double x, double y, double size)
        {
            var label = new LineText(doc) { Page = page, X = x, Y = y, TextValue = text };
            label.FontStyle.Size = size;
            label.FontStyle.Italic = true;
        }

        private static void Axis(Document doc, Page page, double x1, double y1, double x2, double y2)
        {
            var line = new SegmentOutline(doc, Point(doc, page, x1, y1), Point(doc, page, x2, y2))
            {
                Page = page, Style = OutlineStyle.Dashed, LineWidth = 0.25, PatternName = "CENTER", PatternScale = 25.4
            };
            line.Regenerate(true);
        }
    }
}
