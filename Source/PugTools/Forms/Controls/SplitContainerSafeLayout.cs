using System;
using System.Windows.Forms;

namespace PugTools {
  /// <summary>
  /// Applies SplitContainer geometry only after its owner has a real WinForms client size.
  /// Constructor-time SplitterDistance/PanelMin assignments can throw on high-DPI/small initial layouts.
  /// </summary>
  internal static class SplitContainerSafeLayout {
    internal static void ApplyOnLoad(UserControl owner, SplitContainer split, Int32 desiredDistance,
                                     Int32 panel1MinSize = 0, Int32 panel2MinSize = 0) {
      if (owner == null || split == null) return;

      EventHandler handler = null;
      handler = delegate {
        owner.Load -= handler;
        Apply(split, desiredDistance, panel1MinSize, panel2MinSize);
      };
      owner.Load += handler;
    }

    internal static void ApplyOnLoad(Form owner, SplitContainer split, Int32 desiredDistance,
                                     Int32 panel1MinSize = 0, Int32 panel2MinSize = 0) {
      if (owner == null || split == null) return;

      EventHandler handler = null;
      handler = delegate {
        owner.Load -= handler;
        Apply(split, desiredDistance, panel1MinSize, panel2MinSize);
      };
      owner.Load += handler;
    }

    internal static void Apply(SplitContainer split, Int32 desiredDistance,
                               Int32 panel1MinSize = 0, Int32 panel2MinSize = 0) {
      if (split == null || split.IsDisposed) return;

      Int32 total = split.Orientation == Orientation.Vertical
        ? split.ClientSize.Width
        : split.ClientSize.Height;
      Int32 available = total - Math.Max(1, split.SplitterWidth);
      if (available <= 2) return;

      Int32 min1 = Math.Max(split.Panel1MinSize, Math.Max(0, Math.Min(panel1MinSize, available - 1)));
      Int32 min2 = Math.Max(split.Panel2MinSize, Math.Max(0, Math.Min(panel2MinSize, available - 1)));
      if (min1 + min2 >= available) {
        // Keep the control usable rather than throwing when the host is smaller than the requested minima.
        min1 = Math.Min(min1, Math.Max(0, available / 2 - 1));
        min2 = Math.Min(min2, Math.Max(0, available - min1 - 1));
      }

      Int32 low = Math.Max(1, min1);
      Int32 high = Math.Max(low, available - Math.Max(1, min2));
      Int32 distance = Math.Max(low, Math.Min(desiredDistance, high));

      try {
        // Set the distance first while the default minima are still permissive. Then tighten minima
        // only to values that are guaranteed to fit the actual laid-out control.
        split.SplitterDistance = distance;
        if (panel1MinSize > 0) split.Panel1MinSize = Math.Min(panel1MinSize, split.SplitterDistance);
        if (panel2MinSize > 0) split.Panel2MinSize = Math.Min(panel2MinSize, available - split.SplitterDistance);
      } catch (InvalidOperationException) {
        // A parent can still resize during Load. One failed cosmetic layout must never abort a browser.
      }
    }
  }
}
