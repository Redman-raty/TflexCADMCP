using System;
using System.Collections.Generic;
using TFlex.Model;
using TFlex.Model.Model2D;
using TFlex.Model.Model3D;

namespace Homework4
{
    public class Variant14
    {
        private const string BaseName = "В14 — корпус, 140 мм";
        private const double Length = 140;

        public static void BuildBase()
        {
            var doc = TFlex.Application.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Откройте сохранённый документ .grs с включённой историей.");

            foreach (Operation operation in Document3D.GetOperations(doc))
                throw new InvalidOperationException("Запустите макрос на документе без 3D-операций.");

            doc.BeginChanges("ДЗ4, вариант 14 — торцевой профиль и корпус");
            try
            {
                StandardWorkplane front = null;
                foreach (Workplane plane in Document3D.GetWorkplanes(doc))
                {
                    var standard = plane as StandardWorkplane;
                    if (standard != null && standard.Standard == StandardWorkplane.StandardType.Front)
                    {
                        front = standard;
                        break;
                    }
                }

                if (front == null)
                    throw new InvalidOperationException("Рабочая плоскость «Вид спереди» отсутствует.");

                Page page = front.Page;
                if (page == null)
                    throw new InvalidOperationException("У плоскости «Вид спереди» нет страницы эскиза.");

                var outlines = new List<Outline>();

                Node leftArch = Point(doc, page, -45, 20);
                Node rightArch = Point(doc, page, 45, 20);
                Node rightGrooveIn = Point(doc, page, 50, 20);
                Node rightGrooveOut = Point(doc, page, 60, 20);
                Node rightOuterBottom = Point(doc, page, 60, 0);
                Node rightSlotBottom = Point(doc, page, 35, 0);
                Node rightSlotTop = Point(doc, page, 35, 20);
                Node leftSlotTop = Point(doc, page, -35, 20);
                Node leftSlotBottom = Point(doc, page, -35, 0);
                Node leftOuterBottom = Point(doc, page, -60, 0);
                Node leftGrooveOut = Point(doc, page, -60, 20);
                Node leftGrooveIn = Point(doc, page, -50, 20);

                outlines.Add(Arc(doc, page, leftArch, Point(doc, page, 0, 65), rightArch));
                outlines.Add(Line(doc, page, rightArch, rightGrooveIn));
                outlines.Add(Arc(doc, page, rightGrooveIn, Point(doc, page, 55, 15), rightGrooveOut));
                outlines.Add(Arc(doc, page, rightGrooveOut, Point(doc, page, 70, 10), rightOuterBottom));
                outlines.Add(Line(doc, page, rightOuterBottom, rightSlotBottom));
                outlines.Add(Line(doc, page, rightSlotBottom, rightSlotTop));
                outlines.Add(Line(doc, page, rightSlotTop, leftSlotTop));
                outlines.Add(Line(doc, page, leftSlotTop, leftSlotBottom));
                outlines.Add(Line(doc, page, leftSlotBottom, leftOuterBottom));
                outlines.Add(Arc(doc, page, leftOuterBottom, Point(doc, page, -70, 10), leftGrooveOut));
                outlines.Add(Arc(doc, page, leftGrooveOut, Point(doc, page, -55, 15), leftGrooveIn));
                outlines.Add(Line(doc, page, leftGrooveIn, leftArch));

                foreach (Outline outline in outlines)
                    outline.Regenerate(true);

                // The test point is inside the solid arch, above the open bottom slot.
                var section = new Area(doc);
                section.Page = page;
                bool found = section.FindContour(0.0, 40.0);
                bool applied = section.ApplyContours();
                if (!found || !applied)
                    throw new InvalidOperationException(
                        "Торец: поиск штриховки не нашёл замкнутую область в точке (0; 40).");

                var profile = new AreaProfile(doc);
                profile.Name = "В14 — торец R45, R10, R5";
                profile.WorkSurface = front;
                profile.Area = section;

                var baseBody = new BoundedExtrusion(doc);
                baseBody.Name = BaseName;
                baseBody.Profile.Add(profile.Geometry.SheetContour);
                baseBody.Bound1.Offset = -Length / 2;
                baseBody.Bound2.Offset = Length / 2;
            }
            catch
            {
                doc.CancelChanges();
                throw;
            }

            var result = doc.EndChanges(false, true);
            if (!string.Equals(result.ToString(), "OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Пересчёт корпуса завершился с результатом: " + result);
        }

        private static Node Point(Document doc, Page page, double x, double z)
        {
            var node = new FreeNode(doc, x, z);
            node.Page = page;
            return node;
        }

        private static Outline Line(Document doc, Page page, Node start, Node end)
        {
            var line = new SegmentOutline(doc, start, end);
            line.Page = page;
            return line;
        }

        private static Outline Arc(Document doc, Page page, Node start, Node middle, Node end)
        {
            var arc = new ThreePointArcOutline(doc, start, middle, end);
            arc.Page = page;
            return arc;
        }
    }
}
