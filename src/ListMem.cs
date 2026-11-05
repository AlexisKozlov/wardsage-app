// Scroll and infinity-grid reads from the game's memory, scroll math and the coast model (package P5a) (docs/specs/2026-11-05-work-packages.md).
// Passive reads only. What a pilot gets from here instead of the pictures and the constant 8 px:
//   * ScrollState   a ScrollRect: content position (m_PrevPosition 0xBC), velocity 0xB0, m_Dragging 0xB8, the content / viewport bounds (heights in the scroll's units);
//   * GridState     an InfinityGrid / InfinityGridV2: total, first / last cell on the screen, columns, row size, the content / viewport / scroll objects, the V2 height cache;
//   * the drag threshold of the game (EventSystem.m_DragThreshold 0x34, px) instead of a constant;
//   * ScrollMath    pure: row tops, the position that puts a row in the middle, the drag (px) that gets there, the coast after a release (fitted on the recorded series);
//   * ScrollStand   «the list stands»: !dragging and |v| ~ 0 and the same position on two reads.
// Lists: the hero grid (Form_CharactorMain.m_CharactorGrid_*), the gear sets (m_SuitFilterGrid_*, F6), the artifacts (grid's own ScrollRect, m_ListArtifact_* / m_List_*).
// The V2 height cache (m_HeightCache) is NOT checked against the live game yet: HeightCacheVerified is false, RowTops from it stay null - only the cells the grid has made
// (First..Last, UiCell rects) are trusted until it is.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  /// <summary>The scroll math, pure (no memory): positions are the content's anchored y in the scroll's units (0 = the top, growing as the list is scrolled down), px are client pixels
  /// (dy &lt; 0 = the pointer goes up = the content goes up = the position grows).</summary>
  public static class ScrollMath {
    /// <summary>Unity's ScrollRect zeroes the velocity under this (units per second).</summary>
    public const double StopV = 1.0;
    /// <summary>The game's DecelerationRate (velocity left after one second); the scroll components hold it at 0x30, this is the Unity default.</summary>
    public const double DefaultDecel = 0.135;
    /// <summary>The recorded coasts (24 releases, 54 samples of the inventory list, lab e7754bd3) are (|v| - 1) * 0.487 units, the physics says 1 / -ln(0.135) = 0.4994: the ratio.</summary>
    public const double CoastFit = 0.975;
    /// <summary>A list stands when |v| is under this and the position moved less than StandMoveU between two reads.</summary>
    public const double StandV = 0.5, StandMoveU = 0.25;

    public static double MaxPos(double contentH, double viewH) { return Math.Max(0, contentH - viewH); }

    /// <summary>The top of row <paramref name="row"/> (0-based) of an InfinityGrid: padTop + row * lineSize.</summary>
    public static double RowTop(int row, double padTop, double lineSize) { return padTop + row * lineSize; }
    public static int RowOf(int index, int cols) { return cols < 1 ? 0 : index / cols; }
    /// <summary>The content height of a grid: both pads + rows * lineSize (rows = ceil(total / cols)).</summary>
    public static double ContentHeight(int total, int cols, double padTop, double padBottom, double lineSize) {
      if (cols < 1 || total < 0) return 0;
      return padTop + padBottom + ((total + cols - 1) / cols) * lineSize;
    }
    /// <summary>What the pads add up to when the content height is read: contentH - rows * lineSize (the padding itself is a native RectOffset).</summary>
    public static double PadTotal(double contentH, int total, int cols, double lineSize) {
      if (cols < 1 || total < 0) return 0;
      return contentH - ((total + cols - 1) / cols) * lineSize;
    }

    /// <summary>The tops of the rows of a variable-height list (InfinityGridV2): tops[i] = padTop + the heights before i. contentH = padTop + all heights + padBottom.</summary>
    public static double[] RowTops(double[] heights, double padTop, double padBottom, out double contentH) {
      int n = heights == null ? 0 : heights.Length;
      var tops = new double[n]; double y = padTop;
      for (int i = 0; i < n; i++) { tops[i] = y; y += heights[i]; }
      contentH = y + padBottom;
      return tops;
    }

    /// <summary>The position that puts a row in the middle of the viewport, kept inside the content: clamp(rowTop + rowH/2 - viewH/2, 0, contentH - viewH).</summary>
    public static double TargetPos(double rowTop, double rowH, double viewH, double contentH) {
      return Math.Max(0, Math.Min(MaxPos(contentH, viewH), rowTop + rowH / 2 - viewH / 2));
    }

    /// <summary>The part (0..1) of the row inside the viewport at <paramref name="pos"/>.</summary>
    public static double RowInView(double pos, double rowTop, double rowH, double viewH) {
      if (rowH <= 0) return 0;
      double a = Math.Max(rowTop, pos), b = Math.Min(rowTop + rowH, pos + viewH);
      return Math.Max(0, b - a) / rowH;
    }

    /// <summary>One drag toward a row: Needed false = the row is already whole in the viewport (0 actions). DeltaUnits = Target - pos (> 0: the later rows come up); DyPx = the pointer's move
    /// (negative = up): -Delta * pxPerUnit / gain, at least minPx, at most maxPx (0 = no limit; the rest is the next drag), plus the drag threshold the game swallows at the start.</summary>
    public sealed class DragPlan {
      public bool Needed; public double Target, DeltaUnits, DyPx;
      public override string ToString() { return Needed ? "pos->" + Target.ToString("0.#", CultureInfo.InvariantCulture) + " delta=" + DeltaUnits.ToString("0.#", CultureInfo.InvariantCulture) + " dy=" + DyPx.ToString("0.#", CultureInfo.InvariantCulture) : "in view"; }
    }

    /// <summary><paramref name="thresholdPx"/>: the game's own drag threshold (ListMem.ReadDragThreshold; a pilot that cannot read it passes its fallback). <paramref name="gain"/>: the measured
    /// units the content went per unit asked (1 when not measured), see <see cref="Gain"/>.</summary>
    public static DragPlan Drag(double pos, double rowTop, double rowH, double viewH, double contentH, double thresholdPx, double pxPerUnit, double gain, double minPx, double maxPx) {
      var p = new DragPlan();
      if (RowInView(pos, rowTop, rowH, viewH) >= 0.999) return p;
      p.Needed = true;
      p.Target = TargetPos(rowTop, rowH, viewH, contentH);
      p.DeltaUnits = p.Target - pos;
      if (Math.Abs(p.DeltaUnits) < 0.5) { p.Needed = RowInView(pos, rowTop, rowH, viewH) < 0.999; if (!p.Needed) return p; }
      if (!(gain > 0.3 && gain < 2.5)) gain = 1;
      double dy = -p.DeltaUnits * pxPerUnit / gain;
      if (maxPx > 0) dy = Math.Max(-maxPx, Math.Min(maxPx, dy));
      if (Math.Abs(dy) < minPx) dy = dy < 0 || (dy == 0 && p.DeltaUnits > 0) ? -minPx : minPx;
      p.DyPx = dy + Math.Sign(dy) * Math.Max(0, thresholdPx);
      return p;
    }

    /// <summary>The units the content went per unit commanded (the drag minus its threshold) once the position stands; NaN when too small to say or not plausible (0.3..2.5).</summary>
    public static double Gain(double movedUnits, double commandedUnits) {
      if (Math.Abs(commandedUnits) < 5) return double.NaN;
      double g = movedUnits / commandedUnits;
      return g > 0.3 && g < 2.5 ? g : double.NaN;
    }

    // ---- the coast (inertia) after a release. Unity: every frame v *= decel^dt, the content moves by v * dt, v is set to 0 under 1.
    /// <summary>The way a released list still goes (units, signed like the velocity): (|v| - StopV) / -ln(decel), times the fit to the recorded data.</summary>
    public static double Coast(double v, double decel) {
      double a = Math.Abs(v);
      if (!(decel > 0.01 && decel < 0.99)) decel = DefaultDecel;
      if (a <= StopV) return 0;
      return Math.Sign(v) * (a - StopV) / -Math.Log(decel) * CoastFit;
    }
    /// <summary>Where a list that is not dragged ends: pos + coast, kept inside 0..contentH - viewH (the elastic overshoot springs back to it). NaN while it is dragged (the release is not known).</summary>
    public static double FinalPos(double pos, double velY, bool dragging, double decel, double contentH, double viewH) {
      if (dragging) return double.NaN;
      double e = pos + Coast(velY, decel);
      return Math.Max(0, Math.Min(MaxPos(contentH, viewH), e));
    }
    /// <summary>The velocity after <paramref name="seconds"/> of coasting (0 once it is under StopV).</summary>
    public static double VelocityAfter(double v, double decel, double seconds) {
      if (!(decel > 0.01 && decel < 0.99)) decel = DefaultDecel;
      double r = v * Math.Pow(decel, Math.Max(0, seconds));
      return Math.Abs(r) < StopV ? 0 : r;
    }
    /// <summary>How long a released list needs to stop from the velocity v (seconds): decel^t * |v| = StopV.</summary>
    public static double SecondsToStop(double v, double decel) {
      double a = Math.Abs(v);
      if (!(decel > 0.01 && decel < 0.99)) decel = DefaultDecel;
      return a <= StopV ? 0 : Math.Log(StopV / a) / Math.Log(decel);
    }
  }

  /// <summary>One ScrollRect as the game holds it.</summary>
  public sealed class ScrollState {
    public bool Valid;
    public double PosX, PosY, VelX, VelY;
    public bool Dragging, Scrolling, Inertia;
    public int Movement;                      // ScrollRect.MovementType: 0 unrestricted, 1 elastic, 2 clamped
    public double Decel = double.NaN, Sensitivity = double.NaN;
    public double ContentH, ViewH, ContentCenterY, ViewCenterY;   // bounds heights (extents * 2) in the scroll's units
    /// <summary>The content height the position and the bounds centres give (a cross-check of ContentH): 2 * (pos - centre.y), valid for a viewport whose pivot is at the top.</summary>
    public double ContentHFromCentres { get { return 2 * (PosY - ContentCenterY); } }
    public double MaxPos { get { return ScrollMath.MaxPos(ContentH, ViewH); } }
    public override string ToString() {
      var c = CultureInfo.InvariantCulture;
      return Valid ? "pos=" + PosY.ToString("0.#", c) + " v=" + VelY.ToString("0.#", c) + " drag=" + (Dragging ? 1 : 0) + " content=" + ContentH.ToString("0.#", c) + " view=" + ViewH.ToString("0.#", c) : "scroll=none";
    }
  }

  /// <summary>One InfinityGrid / InfinityGridV2 as the game holds it.</summary>
  public sealed class GridState {
    public bool Valid, V2;
    public int Total, First = -1, Last = -1, Cols = 1;
    public double LineSize, FinalLineSize, DefaultHeight;   // V1 rows / V2 default row height (units)
    public ulong ScrollRect, Content, Viewport;
    public double[] HeightX, HeightY;                       // V2: m_HeightCache by index (raw Vector2 parts), null = not read or not complete
    public string Why = "";
    /// <summary>One row in the scroll's units: the size the grid lays out with (V1), else the authored line size.</summary>
    public double RowUnits { get { return FinalLineSize > 1 ? FinalLineSize : LineSize; } }
    /// <summary>The cell is among the ones the grid has made now (First..Last): its UiCell rect can be read.</summary>
    public bool Live(int index) { return First >= 0 && Last >= First && index >= First && index <= Last; }
  }

  /// <summary>«The list stands»: not dragged, |v| ~ 0 and the same position on two reads in a row (a drag coasts after the release).</summary>
  public sealed class ScrollStand {
    double lastY = double.NaN; bool lastStill; int still;
    public bool Stands { get { return still >= 2; } }
    public void Reset() { lastY = double.NaN; lastStill = false; still = 0; }
    /// <summary>Feeds one read; returns true once the list stands on two reads.</summary>
    public bool Feed(ScrollState s) {
      if (s == null || !s.Valid) { Reset(); return false; }
      bool quiet = !s.Dragging && Math.Abs(s.VelX) < ScrollMath.StandV && Math.Abs(s.VelY) < ScrollMath.StandV;
      bool same = !double.IsNaN(lastY) && Math.Abs(s.PosY - lastY) < ScrollMath.StandMoveU;
      still = quiet && (still == 0 || (lastStill && same)) ? still + 1 : (quiet ? 1 : 0);
      lastY = s.PosY; lastStill = quiet;
      return Stands;
    }
  }

  /// <summary>A list read as a whole: its scroll, its grid, the game's drag threshold.</summary>
  public sealed class ListMemRead {
    public ScrollState Scroll; public GridState Grid; public double ThresholdPx = -1; public string Why = "";
    public bool Has { get { return Scroll != null && Scroll.Valid && Grid != null && Grid.Valid; } }
    /// <summary>The way to scroll to the cell <paramref name="index"/> (V1 rows: index / Cols). padTop = the top padding of the content in units (the pad total is Scroll.ContentH - rows * line, see
    /// ScrollMath.PadTotal; the padding object itself is native). thresholdFallbackPx is used only when the game's threshold was not read.</summary>
    public ScrollMath.DragPlan PlanTo(int index, double padTop, double pxPerUnit, double gain, double minPx, double maxPx, double thresholdFallbackPx) {
      if (!Has || Grid.V2) return null;
      double line = Grid.RowUnits;
      if (line <= 1) return null;
      return ScrollMath.Drag(Scroll.PosY, ScrollMath.RowTop(ScrollMath.RowOf(index, Grid.Cols), padTop, line), line, Scroll.ViewH, Scroll.ContentH, ThresholdPx > 0 ? ThresholdPx : thresholdFallbackPx, pxPerUnit, gain, minPx, maxPx);
    }
    public override string ToString() { return Has ? Scroll + " cells=" + Grid.First + ".." + Grid.Last + "/" + Grid.Total + " cols=" + Grid.Cols + (Grid.V2 ? " v2" : " row=" + Grid.RowUnits.ToString("0.#", CultureInfo.InvariantCulture)) + " thr=" + ThresholdPx : "list=none (" + Why + ")"; }
  }

  public static partial class RFX {
    // ScrollRect (checked by tools/app-offsets.mjs)
    const int SrContent = 0x18, SrMovement = 0x24, SrInertia = 0x2C, SrDecel = 0x30, SrSensitivity = 0x34, SrViewport = 0x38, SrContentBounds = 0x80, SrViewBounds = 0x98,
      SrVelocity = 0xB0, SrDragging = 0xB8, SrScrolling = 0xB9, SrPrevPosition = 0xBC, SrSize = 0xC8;
    // Bounds {center Vector3 +0, extents Vector3 +0xC}
    const int BoundsCenter = 0, BoundsExtents = 0xC;
    // InfinityGrid (V1)
    const int Ig1Cols = 0x30, Ig1Line = 0x78, Ig1Final = 0x7C, Ig1Total = 0x90, Ig1Scroll = 0xB8, Ig1Content = 0xC0, Ig1Viewport = 0xC8, Ig1First = 0xD0, Ig1Last = 0xD4, Ig1Size = 0xD8;
    // InfinityGridV2
    const int Ig2HeightCache = 0xA0, Ig2DefaultHeight = 0xA8, Ig2Total = 0xBC, Ig2Scroll = 0xE0, Ig2Content = 0xE8, Ig2Viewport = 0xF0, Ig2First = 0xF8, Ig2Last = 0xFC, Ig2Size = 0x100;
    // EventSystem
    const int EsDragThreshold = 0x34;

    /// <summary>False until the V2 m_HeightCache is compared with a live list (the element is Dictionary&lt;int, Vector2&gt;: what the two floats are is not known): the heights are then not offered.</summary>
    public const bool HeightCacheVerified = false;

    static float LmF(byte[] b, int o) { return o + 4 <= b.Length ? BitConverter.ToSingle(b, o) : float.NaN; }
    static int LmI(byte[] b, int o) { return o + 4 <= b.Length ? BitConverter.ToInt32(b, o) : 0; }
    static bool LmOk(double v, double lim) { return !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) <= lim; }

    /// <summary>The ScrollRect object at <paramref name="rect"/> (null = unreadable). Valid only for sane numbers: finite, the viewport has a height.</summary>
    public static ScrollState ReadScroll(ulong rect) {
      var s = new ScrollState();
      var b = rect != 0 ? Read(rect, SrSize) : null;
      if (b == null) return s;
      double px = LmF(b, SrPrevPosition), py = LmF(b, SrPrevPosition + 4), vx = LmF(b, SrVelocity), vy = LmF(b, SrVelocity + 4);
      double cch = LmF(b, SrContentBounds + BoundsCenter + 4), vch = LmF(b, SrViewBounds + BoundsCenter + 4);
      double ch = LmF(b, SrContentBounds + BoundsExtents + 4) * 2, vh = LmF(b, SrViewBounds + BoundsExtents + 4) * 2;
      if (!LmOk(px, 1e6) || !LmOk(py, 1e6) || !LmOk(vx, 1e6) || !LmOk(vy, 1e6) || !LmOk(ch, 1e7) || !LmOk(vh, 1e5) || !LmOk(cch, 1e7) || !LmOk(vch, 1e5) || vh <= 0 || ch < 0) return s;
      s.PosX = px; s.PosY = py; s.VelX = vx; s.VelY = vy;
      s.Dragging = b[SrDragging] == 1; s.Scrolling = b[SrScrolling] == 1; s.Inertia = b[SrInertia] == 1; s.Movement = LmI(b, SrMovement);
      float dr = LmF(b, SrDecel), se = LmF(b, SrSensitivity);
      if (dr > 0 && dr < 1) s.Decel = dr;
      if (LmOk(se, 1000)) s.Sensitivity = se;
      s.ContentH = ch; s.ViewH = vh; s.ContentCenterY = cch; s.ViewCenterY = vch;
      s.Valid = true;
      return s;
    }

    // a layout-file offset when the build has it (InfinityGrid.* keys), else the checked one
    static int LmOff(string key, int dflt) { var ly = Layout.Current; int o = ly != null ? ly.FieldOffset(key) : -1; return o > 0 ? o : dflt; }

    /// <summary>The grid object at <paramref name="grid"/>: V1 (UI.InfinityGrid) or V2 (UI.InfinityGridV2). Valid = the counts are in range and the first / last cells agree.</summary>
    public static GridState ReadGridMem(ulong grid, bool v2) {
      var g = new GridState { V2 = v2 };
      var b = grid != 0 ? Read(grid, v2 ? Ig2Size : Ig1Size) : null;
      if (b == null) { g.Why = "grid unreadable"; return g; }
      int oFirst = v2 ? Ig2First : LmOff("InfinityGrid.m_crtFirstInstanceIndex", Ig1First), oLast = v2 ? Ig2Last : LmOff("InfinityGrid.m_crtLastInstanceIndex", Ig1Last);
      g.Total = LmI(b, v2 ? Ig2Total : Ig1Total);
      g.First = LmI(b, oFirst); g.Last = LmI(b, oLast);
      if (g.Total < 0 || g.Total > 100000) { g.Why = "total " + g.Total; return g; }
      if (g.First < -1 || g.Last < -1 || g.Last >= g.Total && g.Total > 0 || g.First > g.Last + 1) { g.Why = "cells " + g.First + ".." + g.Last + " of " + g.Total; return g; }
      g.ScrollRect = BitConverter.ToUInt64(b, v2 ? Ig2Scroll : Ig1Scroll); g.Content = BitConverter.ToUInt64(b, v2 ? Ig2Content : Ig1Content); g.Viewport = BitConverter.ToUInt64(b, v2 ? Ig2Viewport : Ig1Viewport);
      if (v2) {
        g.Cols = 1;
        float dh = LmF(b, Ig2DefaultHeight);
        if (dh > 1 && dh < 5000) g.DefaultHeight = dh;
        ReadHeightCache(Ptr(grid + (ulong)Ig2HeightCache), g);
      } else {
        int cols = LmI(b, LmOff("InfinityGrid.CellPerLine", Ig1Cols)); float line = LmF(b, LmOff("InfinityGrid.LineSize", Ig1Line)), fin = LmF(b, LmOff("InfinityGrid.m_finalLineSize", Ig1Final));
        if (cols < 1 || cols > 20) { g.Why = "cols " + cols; return g; }
        if (!(line > 1 && line < 2000)) { g.Why = "line size"; return g; }
        g.Cols = cols; g.LineSize = line; if (fin > 1 && fin < 2000) g.FinalLineSize = fin;
      }
      g.Valid = true;
      return g;
    }

    // Dictionary<int, Vector2> {buckets 0x10, entries 0x18, count 0x20}; Entry {hashCode 0, next 4, key 8, value.x 0xC, value.y 0x10}, stride 0x14 (IL2CPP layout rules, not seen in a live process yet)
    static void ReadHeightCache(ulong dict, GridState g) {
      if (dict == 0 || g.Total <= 0) return;
      var db = Read(dict + 0x18, 12); if (db == null) return;
      ulong ents = BitConverter.ToUInt64(db, 0); int cnt = LmI(db, 8);
      if (ents == 0 || cnt < g.Total || cnt > 100000 || cnt > g.Total * 2 + 16) return;
      var eb = Read(ents + ArrData, cnt * 0x14); if (eb == null) return;
      var hx = new double[g.Total]; var hy = new double[g.Total]; var seen = new bool[g.Total]; int got = 0;
      for (int i = 0; i < cnt; i++) {
        int o = i * 0x14; if (LmI(eb, o) < 0) continue;
        int key = LmI(eb, o + 8);
        if (key < 0 || key >= g.Total || seen[key]) continue;
        float x = LmF(eb, o + 0xC), y = LmF(eb, o + 0x10);
        if (!LmOk(x, 1e5) || !LmOk(y, 1e5)) return;
        seen[key] = true; hx[key] = x; hy[key] = y; got++;
      }
      if (got == g.Total) { g.HeightX = hx; g.HeightY = hy; }
    }

    /// <summary>The row tops of a V2 list from its height cache, null unless <see cref="HeightCacheVerified"/> (until then only the cells made now are trusted).</summary>
    public static double[] RowTopsV2(GridState g, double padTop, double padBottom, out double contentH) {
      contentH = 0;
      if (!HeightCacheVerified || g == null || !g.V2 || g.HeightY == null) return null;
      return ScrollMath.RowTops(g.HeightY, padTop, padBottom, out contentH);
    }

    static bool LmIsGrid(ulong obj, out bool v2) {
      v2 = false;
      if (obj == 0) return false;
      if (IsA(obj, "UI.InfinityGrid")) return true;
      if (IsA(obj, "UI.InfinityGridV2")) { v2 = true; return true; }
      return false;
    }

    /// <summary>Reads a list whose owner table holds the grid under <paramref name="gridField"/> (V1 or V2) and its scroll under <paramref name="scrollField"/> (null or not readable: the
    /// ScrollRect the grid itself holds). Null-valued reads give a ListMemRead with Has false and Why.</summary>
    public static ListMemRead ReadListMem(ulong owner, string scrollField, string gridField) {
      var r = new ListMemRead();
      int pid = ListPid();
      if (owner == 0) { r.Why = "no owner"; return r; }
      if (pid == 0) { r.Why = "no game process"; return r; }
      ulong gobj = 0; bool v2 = false;
      ulong ud; int tt;
      if (!Field(owner, gridField, out ud, out tt) || (tt & 0x0F) != 7 || ud == 0) { r.Why = gridField + " not in the table"; return r; }
      ulong h = ScriptObjectAt(ud, "UI.InfinityGrid", pid);
      if (h != 0) gobj = h; else { h = ScriptObjectAt(ud, "UI.InfinityGridV2", pid); if (h != 0) { gobj = h; v2 = true; } }
      if (gobj == 0) { r.Why = gridField + " is not an InfinityGrid"; return r; }
      r.Grid = ReadGridMem(gobj, v2);
      if (!r.Grid.Valid) { r.Why = "grid: " + r.Grid.Why; return r; }
      ulong rect = scrollField != null ? ScriptObject(owner, scrollField, "UnityEngine.UI.ScrollRect", pid) : 0;
      if (rect == 0 && IsA(r.Grid.ScrollRect, "UnityEngine.UI.ScrollRect")) rect = r.Grid.ScrollRect;
      if (rect == 0) { r.Why = "scroll rect not found"; return r; }
      r.Scroll = ReadScroll(rect);
      if (!r.Scroll.Valid) { r.Why = "scroll unreadable"; return r; }
      r.ThresholdPx = ReadDragThreshold();
      return r;
    }

    /// <summary>The hero grid of Form_CharactorMain (m_CharactorGrid_ScrollRect / m_CharactorGrid_InfinityGrid).</summary>
    public static ListMemRead ReadHeroGridMem(ulong form) { return ReadListMem(form, "m_CharactorGrid_ScrollRect", "m_CharactorGrid_InfinityGrid"); }
    /// <summary>The gear-set list of the filter (F6): the owner holds m_SuitFilterGrid_ScrollRect / m_SuitFilterGrid_InfinityGrid.</summary>
    public static ListMemRead ReadSuitListMem(ulong owner) { return ReadListMem(owner, "m_SuitFilterGrid_ScrollRect", "m_SuitFilterGrid_InfinityGrid"); }
    /// <summary>An artifact list (Panel_Equip_Artifact / the inventory's artifact tab: m_ListArtifact_InfinityGrid, the hero screen's m_List_InfinityGrid): its scroll is the grid's own.</summary>
    public static ListMemRead ReadArtifactListMem(ulong panel, string gridField) { return ReadListMem(panel, null, gridField); }

    // ---- the game's drag threshold (EventSystem.m_DragThreshold, px the pointer must go before a drag starts: the content follows from there, so a computed drag is that much longer)
    /// <summary>The threshold of the EventSystem object (px), -1 = unreadable or not plausible (1..200).</summary>
    public static int DragThresholdOf(ulong eventSystem) {
      var b = eventSystem != 0 ? Read(eventSystem + (ulong)EsDragThreshold, 4) : null;
      if (b == null) return -1;
      int t = BitConverter.ToInt32(b, 0);
      return t >= 1 && t <= 200 ? t : -1;
    }
    /// <summary>The threshold of the first EventSystem of the static list m_EventSystems (List&lt;EventSystem&gt;: _items 0x10, _size 0x18).</summary>
    public static int DragThresholdFromList(ulong list) {
      var lb = list != 0 ? Read(list, 0x20) : null;
      if (lb == null || LmI(lb, 0x18) < 1) return -1;
      ulong arr = BitConverter.ToUInt64(lb, 0x10);
      return arr != 0 ? DragThresholdOf(Ptr(arr + (ulong)ArrData)) : -1;
    }
    static int dragThr = -1; static long dragThrAt;
    /// <summary>The game's drag threshold in px, -1 when it cannot be read (the pilot then keeps its constant and says so in the log). A good read is kept 30 s.</summary>
    public static int ReadDragThreshold() {
      long now = Environment.TickCount;
      if (dragThr > 0 && now - dragThrAt < 30000 && now >= dragThrAt) return dragThr;
      int pid = ListPid(); ulong ga = pid != 0 ? GameAssemblyOf(pid) : 0;
      if (ga == 0) return -1;
      ulong k = ClassAt(ga, "UnityEngine.EventSystems.EventSystem");
      if (k == 0) { RequestScan(ga, "UnityEngine.EventSystems.EventSystem"); return -1; }
      ulong sf = Ptr(k + 0xB8);   // static_fields; m_EventSystems is its first static
      int t = sf != 0 ? DragThresholdFromList(Ptr(sf)) : -1;
      if (t > 0) { dragThr = t; dragThrAt = now; }
      return t;
    }
    /// <summary>Forgets the cached threshold (tests, a new game process).</summary>
    public static void ForgetDragThreshold() { dragThr = -1; dragThrAt = 0; }
  }
}
