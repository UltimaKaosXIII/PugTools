using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PugTools {
  /// <summary>
  /// Small local fallbacks for map-filter/service symbols that do not have an mpnIconAsset bitmap in the client
  /// map-note template. The five authored mapnote classes still use their bundled SWTOR PNGs; these drawings are
  /// only for world-derived NPC/placeable services (vendor, bank, mailbox, trainers, resources, GTN, ...).
  /// </summary>
  internal static class WorldMapIconFactory {
    internal static Bitmap Create(string key) {
      if (String.IsNullOrWhiteSpace(key)) return null;
      key = key.Trim().ToLowerInvariant();
      int w = 26, h = 26;
      var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      using Graphics g = Graphics.FromImage(bmp);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(Color.Transparent);
      Color gold = Color.FromArgb(245, 218, 142, 42);
      Color green = Color.FromArgb(245, 74, 205, 69);
      Color dark = Color.FromArgb(235, 48, 35, 12);
      using var outline = new Pen(dark, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
      using var pen = new Pen(gold, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
      using var greenPen = new Pen(green, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
      using var fill = new SolidBrush(gold);
      using var greenFill = new SolidBrush(green);

      void Stroke(Action<Pen> draw, Pen visible = null) { draw(outline); draw(visible ?? pen); }
      switch (key) {
        case "vendor":
          Stroke(p => g.DrawPolygon(p, new[] { new PointF(6,9), new PointF(20,9), new PointF(20,20), new PointF(6,20) }));
          Stroke(p => g.DrawArc(p, 8, 4, 10, 9, 190, 160));
          break;
        case "classtrainer":
        case "trainer":
          Stroke(p => { g.DrawLine(p,13,21,13,6); g.DrawLine(p,13,6,8,11); g.DrawLine(p,13,6,18,11); }, greenPen);
          Stroke(p => g.DrawArc(p,5,15,16,8,200,140), greenPen);
          break;
        case "crewtrainer":
          Stroke(p => { g.DrawLine(p,6,20,19,7); g.DrawEllipse(p,4,18,5,5); g.DrawEllipse(p,17,4,5,5); });
          Stroke(p => { g.DrawLine(p,6,6,20,20); g.DrawLine(p,5,5,9,6); g.DrawLine(p,5,5,6,9); });
          break;
        case "resource":
        case "harvest":
          Stroke(p => { g.DrawLine(p,13,3,13,23); g.DrawLine(p,3,13,23,13); g.DrawLine(p,6,6,20,20); g.DrawLine(p,20,6,6,20); });
          break;
        case "mailbox":
        case "mail":
          Stroke(p => g.DrawRectangle(p,4,7,18,13));
          Stroke(p => { g.DrawLine(p,5,8,13,15); g.DrawLine(p,21,8,13,15); });
          break;
        case "enhancement":
        case "modification":
          Stroke(p => { g.DrawLine(p,5,20,20,5); g.DrawLine(p,4,5,21,20); });
          break;
        case "bank":
        case "guildbank":
        case "cargohold":
          Stroke(p => { g.DrawLine(p,4,11,19,11); g.DrawEllipse(p,3,8,7,7); g.DrawLine(p,19,11,23,11); g.DrawLine(p,20,11,20,15); g.DrawLine(p,23,11,23,14); });
          break;
        case "auction":
        case "auctionhouse":
        case "galacticmarket":
          Stroke(p => g.DrawPolygon(p, new[] { new PointF(13,3), new PointF(22,10), new PointF(18,22), new PointF(8,22), new PointF(4,10) }));
          Stroke(p => { g.DrawLine(p,4,10,22,10); g.DrawLine(p,8,22,13,10); g.DrawLine(p,18,22,13,10); });
          break;
        case "explorationquest":
          Stroke(p => g.DrawPolygon(p, new[] { new PointF(13,3), new PointF(22,19), new PointF(4,19) }));
          g.FillEllipse(greenFill, 11, 10, 4, 4);
          break;
        case "missionboard":
          Stroke(p => g.DrawRectangle(p,6,4,14,18));
          Stroke(p => { g.DrawLine(p,9,9,17,9); g.DrawLine(p,9,13,17,13); g.DrawLine(p,9,17,15,17); });
          break;
        case "default":
          Stroke(p => g.DrawEllipse(p,6,6,14,14));
          g.FillEllipse(fill, 11, 11, 4, 4);
          break;
        default:
          bmp.Dispose();
          return null;
      }
      return bmp;
    }
  }
}
