// Wardsage - geometry and look helpers of the hint frames (read-only: where a game widget sits, what the picture shows). No input of any kind.
using System;
using System.Collections.Generic;

namespace RealmForge {
  /// <summary>Layout of the sell screen's list in UI units (Ui.Unit), measured on the live game 2026-09-26 (1920×1009 and
  /// 1600×1000): the list is centred horizontally, its top follows the window's top edge and the bottom of its view the
  /// bottom edge (a taller window shows more rows).</summary>
  public sealed class SellGeometry {
    public double X0 = -0.1764, PitchX = 0.1269, CellW = 0.1130;   // column 1's cell left edge from the centre, step, size
    public double PitchY = 0.1705;                                // item row step
    public double TitleRow = 0.5407, EmptyRow = 0.60;            // other row kinds relative to an item row (the «Все» row)
    public double ViewTop = 0.1030, ViewBottom = 0.8226;         // visible part: top from the top edge, bottom from the bottom
    public double TopPad = 0.0;                                   // view top → row 1's top at scroll 0
    public double BarTop = 0.1229, BarBottom = 0.1487;            // stat bar under the cells, from the row top
    public double SellBtnX = 0.3588, SellBtnY = 0.1229;           // «Продать» next to «1119/2300» on the gear list (top-left)
    // the left panel is centred in the window (x from the centre, y by Ui.FromMiddle): «Выбрать часть» icons (weapon,
    // armour, bracer, amulet, ring = the game's parts 0..4, the site's slots) and «Сбросить»
    public double PartX0 = -0.7968, PartPitch = 0.1115, PartY = 0.1863;
    public double ResetX = -0.7532, ResetY = 0.8484;
    // an empty spot right of the list (left of the tabs), centred: a click there closes the item card and «Быстрое
    // улучшение» and changes nothing else (live 2026-09-26: the choice stayed)
    public double CloseX = 0.674, CloseY = 0.4955;
    // where «Быстрое улучшение» sits over the list (x from the centre, y from the top, UI units; measured 1920×1009 with a
    // margin): only a click there needs it closed first
    public double PopupX0 = -0.21, PopupX1 = 0.34, PopupY0 = 0.48, PopupY1 = 0.92;
    public bool InPopup(double x, double y, double W, double U) { if (PopupX0 >= PopupX1) return false; double fx = (x - W / 2) / U, fy = y / U; return fx > PopupX0 && fx < PopupX1 && fy > PopupY0 && fy < PopupY1; }
    public int Columns = 6;
    public double CellH = double.NaN;                             // cell height when it differs from the width
    public double CellHeight { get { return double.IsNaN(CellH) ? CellW : CellH; } }

    /// <summary>The inventory's equipment list (Form_BackpackIntegration), measured on the live game 2026-09-26 at
    /// 1920×1009, 1600×1000 and 1300×1000: the rows follow the UI unit from the top (first row's bar at 0.335, step
    /// 0.1846); the columns fill the width between the left menu (0.205 from the left edge) and the item panel on the
    /// right (0.522 from the right edge), <paramref name="cols"/> of them (the game counts them from the width: 8 there).
    /// No part filter (a filter change clears the choice) and no pop-up.</summary>
    public static SellGeometry Inventory(double W, double U, int cols) {
      double pitchX = (W - 0.727 * U) / cols / U;
      return new SellGeometry {
        X0 = 0.205 - W / (2 * U), PitchX = pitchX, CellW = pitchX * 0.86, CellH = 0.127, Columns = cols,
        PitchY = 0.1846, TitleRow = 1, ViewTop = 0.190, TopPad = 0.012, ViewBottom = 0.83,
        BarTop = 0.133, BarBottom = 0.165,
        PartX0 = double.NaN, ResetX = double.NaN, PopupX0 = 1, PopupX1 = 0,
      };
    }

    /// <summary>The artifact tab's list (Panel_ArtifactPanel, «Массовая продажа»): cells of the grid are <paramref name="cols"/> per row across the list's width
    /// (left edge 0.195 U from the left, right edge 0.5231 U from the right, top 0.1922 U, bottom 0.14 U above the window's bottom), a row is 116 of the canvas's
    /// 640 units; the card in a cell is 80 x 110 units, centred. No part filter, no pop-up. <paramref name="list"/> = the list's rectangle from the site's
    /// geometry file (inventory.artList, client px) or null (then the numbers above). Measured on a live recording (test app 1.6.62, 1366x705 window): 8 columns at that width (the game takes floor((width - 6) / 90) of canvas units), a row 120 px = 116 units.</summary>
    public static SellGeometry Artifacts(double W, double H, double U, int cols, GeoRect list = null) {
      if (cols < 1) cols = 1;
      double left = 0.195, right = W / U - 0.5231, top = 0.1922, viewBottom = 0.86;   // ViewBottom: from the top edge's side, bottom edge = H - (1 - ViewBottom) U
      if (list != null && list.W > 10 && list.H > 10) { left = list.X / U; right = list.Right / U; top = list.Y / U; viewBottom = 1 - (H - list.Bottom) / U; }
      double pitchX = (right - left) / cols, cardW = 80.0 / 640, cardH = 110.0 / 640, row = 116.0 / 640;
      return new SellGeometry {
        X0 = left - W / (2 * U) + (pitchX - cardW) / 2, PitchX = pitchX, CellW = cardW, CellH = cardH, Columns = cols,
        PitchY = row, TitleRow = 1, ViewTop = top, TopPad = (row - cardH) / 2, ViewBottom = viewBottom,
        BarTop = 0, BarBottom = cardH,
        PartX0 = double.NaN, ResetX = double.NaN, PopupX0 = 1, PopupX1 = 0,
      };
    }

    /// <summary>Columns of the artifact list as the game counts them (Panel_ArtifactPanel: floor((list width in canvas units - 6) / 90)); <paramref name="fallback"/> when the rectangle is unknown.</summary>
    public static int ArtCols(GeoRect list, double U, int fallback) {
      if (list == null || list.W <= 10 || U <= 0) return fallback;
      int c = (int)System.Math.Floor((list.W / U * 640 - 6) / 90);
      return c < 1 || c > 20 ? fallback : c;
    }

    public double RowHeight(int type) { return type == 2 ? PitchY * TitleRow : type == 3 ? PitchY * EmptyRow : PitchY; }
    public double RowOffset(int[] types, int row) {
      double y = 0;
      for (int i = 1; i < row; i++) y += RowHeight(types != null && i < types.Length ? types[i] : 1);
      return y;
    }
    public double X(double fx, double W, double U) { return W / 2 + fx * U; }
    public double CellCentreX(int col, double W, double U) { return X(X0 + (col - 1) * PitchX + CellW / 2, W, U); }
    public double StripCentreX(double W, double U) { return X(X0 + (Columns * PitchX) / 2, W, U); }
    public double ViewTopPx(double U) { return ViewTop * U; }
    public double ViewBottomPx(double H, double U) { return H - (1 - ViewBottom) * U; }
    public double[] Part(int part, double W, double H, double U) { return new[] { W / 2 + (PartX0 + part * PartPitch) * U, Ui.FromMiddle(PartY, W, H) }; }
    public double[] Reset(double W, double H, double U) { return new[] { W / 2 + ResetX * U, Ui.FromMiddle(ResetY, W, H) }; }
    public double[] Close(double W, double H, double U) { return new[] { W / 2 + CloseX * U, Ui.FromMiddle(CloseY, W, H) }; }
  }


  /// <summary>The gear filter pop-up, measured on the live game (1920×1009 client): x from the window centre, y from the
  /// top, both in fractions of the client height.</summary>
  public sealed class FilterGeometry {
    public double MainCloseX = 0.3181, CloseY = 0.0902;      // red × of the filter panel
    public double SideCloseX = 0.8543;                        // red × of the side panel (sets / main stats)
    public double SetButtonsX = 0.7215, SetButtonsY = 0.1269; // the sets panel's gold grid/list buttons (only on that panel)
    public double SetArrowX = 0.2339, SetArrowY = 0.3538;     // «Выбрать комплект» ›
    public double StatArrowX = 0.2339, StatArrowY = 0.6194;   // «Выбрать основные хар-ки» ›
    public double ResetX = -0.3865, ResetY = 0.8285;          // «Сбросить» of the filter panel
    public double HideEquippedX = -0.3915, HideEquippedY = 0.9118;   // «Скрыть надетое»
    public double SetColX0 = 0.4876, SetColX1 = 0.7235, SetRow0 = 0.2497, SetPitch = 0.0656;   // sets: 2 columns
    public double StatColX0 = 0.4817, StatColX1 = 0.7185, StatRow0 = 0.2359, StatPitch = 0.0689; // main stats: 2 columns
    public double SubArrowX = 0.2339, SubArrowY = 0.7493;    // «Выбрать второстепенные хар-ки» › (its panel: the main stats' layout)
    // «Выбрать часть»: the pop-up's own row of the five slot icons (two weapon / armour slots, a divider, three accessory slots; the current one lit), measured on the owner's screenshot of the
    // 1.6.66 client (the icons' centres against the × and the set arrow): x from the window centre like the others, y in the same fraction of the client height
    public double[] PartX = { -0.343, -0.215, -0.028, 0.100, 0.230 }; public double PartY = 0.2042;
    // rows fully visible without scrolling the side panel: 10 (live 2026-09-26: all 19 weapon sets and all 20 bracer /
    // amulet / ring sets show at once, the 10th row fully)
    public const int SetRows = 10, StatRows = 9;

    // the pop-up is anchored to the window centre (x and y), sizes scale with the UI unit (Ui.Unit)
    public double X(double fx, double W, double H) { return W / 2 + fx * Ui.Unit(W, H); }
    public double Y(double fy, double W, double H) { return Ui.FromMiddle(fy, W, H); }
    /// <summary>The icon of part <paramref name="part"/> (0..4) in the pop-up's «Выбрать часть» row.</summary>
    public double[] PartCell(int part, double W, double H) { return new[] { X(PartX[part], W, H), Y(PartY, W, H) }; }
    public double[] SetCell(int index, double W, double H) {
      return new[] { X(index % 2 == 0 ? SetColX0 : SetColX1, W, H), Y(SetRow0 + index / 2 * SetPitch, W, H) };
    }
    /// <summary>The set is further down than the visible rows: it needs the list scrolled (no cell of it is on screen).</summary>
    public static bool SetBelowView(int setIndex) { return setIndex >= 2 * SetRows; }
    /// <summary>The whole visible sets list (both columns, all rows) as x, y, w, h around the cells of cell size cw x ch.</summary>
    public int[] SetListRect(double W, double H, int cw, int ch) {
      var c0 = SetCell(0, W, H); var c1 = SetCell(1, W, H); var cl = SetCell(2 * (SetRows - 1), W, H);
      return new[] { (int)c0[0] - cw / 2, (int)c0[1] - ch / 2, (int)(c1[0] - c0[0]) + cw, (int)(cl[1] - c0[1]) + ch };
    }
    public double[] StatCell(int index, double W, double H) {
      return new[] { X(index % 2 == 0 ? StatColX0 : StatColX1, W, H), Y(StatRow0 + index / 2 * StatPitch, W, H) };
    }
  }


  /// <summary>The hero screen, measured on the live game (1920×1009 client): x from the left edge (the grid) or the right
  /// edge (the tabs), y from the top, all in fractions of the client height.</summary>
  public sealed class HeroGeometry {
    public double Col1X = 0.0966, PitchX = 0.1279;     // centre of the grid's first column, column step
    public double CardH = 0.1625, PitchY = 0.1856;     // card height, row step (big cards)
    public double SmallCardH = 0.097, SmallPitchY = 0.1259;   // small cards (the same columns; LineSize 85 instead of 125)
    public double Row1Top = 0.1645;                    // row 1's top with the grid at the top
    public double ViewTop = 0.158, ViewBottom = 0.910; // visible part of the grid
    public double BackX = 0.0515, BackY = 0.0367;      // back arrow, top left (closes the gear list)
    public double GearTabDX = 0.0862, GearTabY = 0.334; // «Снаряжение» tab, x from the right edge
    // «Герои» of the main city's bottom bar (the bar is anchored to the bottom right; it wraps on narrow windows):
    // x from the right edge, y from the bottom (1600×1000 and 1920×1009 measured)
    public double HeroesBtnDX = 0.5222, HeroesBtnDY = 0.1389;
    // the grid's filter: its button under the grid (left edge, from the bottom) and the pop-up's class / faction columns
    // (left / top), rows from «Все» down, 7 visible without scrolling (1920×1009 measured)
    public double FunnelX = 0.0634, FunnelDY = 0.0505;
    // the hero grid's bottom bar (funnel, sort, direction): the game's m_BtnFilter, whole; measured by the live read at 1366x705 (lab 548: 3,637,275x40). The ONE layout of hero.filter (every hint path)
    public double GridBarX0 = 0.0043, GridBarW = 0.39, GridBarY0 = 0.9035, GridBarH = 0.0567;
    public GeoRect GridBarRect(double W, double H) {
      double U = Ui.Unit(W, H);
      return new GeoRect { X = Math.Round(GridBarX0 * U), Y = Math.Round(Ui.FromBottom(GridBarY0, W, H)), W = Math.Round(GridBarW * U), H = Math.Round(GridBarH * U) };
    }
    public double ClassX = 0.0942, FactionX = 0.321, FilterRow0 = 0.2379, FilterPitch = 0.1004;
    public const int FilterRows = 7;
    /// <summary>Class ids of the class column after «Все», in its order (by id; Tactician only when the game shows it).</summary>
    public static readonly long[] ClassOrder = { 1, 2, 4, 8, 16, 256 };
    public const int Columns = 3;
    public const int GearTab = 3;                      // CharactorMainPanelType.Equip

    public double ColX(int col) { return (Col1X + col * PitchX); }   // col 0-based
  }

  public sealed class StackForm {
    public string Name = ""; public bool Active = true, HasDelegate, Full, LuaBehav; public string Back = "default";
  }

  /// <summary>Lines the pure pilots want in the test log (the probe's lines carry them out: RFX.NavProbeLines drains this). At most 20 wait.</summary>
  /// <summary>Memory against pixels, told only when it lasts: a pair that differs is a disagreement only after HoldMs (1.5 s) without a break (the picture lags the game's flag by the window's fade, 0.3-0.8 s, and
  /// the probe samples once a second: a lone pair is no conflict). One line per episode: «disagree: &lt;name&gt; mem=.. pix=.. for=&lt;ms&gt;». Memory and picture are "yes" / "no"; anything else (unknown, n/a) is no comparison and ends the episode.</summary>
  public sealed class DisagreeTracker {
    public const long HoldMs = 1500;
    string key; long since; bool said;
    public void Reset() { key = null; said = false; }
    /// <summary>The line to log now, or null.</summary>
    public string Update(string name, string mem, string pix, long nowMs) {
      bool ok = (mem == "yes" || mem == "no") && (pix == "yes" || pix == "no");
      return Compare(name, ok ? mem : null, ok ? pix : null, nowMs);
    }
    /// <summary>True once the current disagreement has lasted HoldMs (the line was given).</summary>
    public bool Held { get { return said; } }
    /// <summary>The same for any two words (null = not known: no comparison).</summary>
    public string Compare(string name, string mem, string pix, long nowMs) {
      if (mem == null || pix == null || mem == pix) { Reset(); return null; }
      string k = mem + "/" + pix;
      if (k != key || nowMs < since) { key = k; since = nowMs; said = false; }
      if (said || nowMs - since < HoldMs) return null;
      said = true;
      return "disagree: " + name + " mem=" + mem + " pix=" + pix + " for=" + (nowMs - since);
    }
  }

  /// <summary>The item card's button kind (E4, plan A6): «Заменить» (two cards: m_CurrentEquipInfo set) or «Надеть» (one card). Memory leads; the picture's single blue button only checks it. A picture that contradicts
  /// memory for over 1.5 s (the fields were not cleared between items: a stale m_CurrentEquipInfo) takes over again, told once as «disagree: tip card ..». Kinds: 1 = replace, 2 = equip, 0 = not known / no single button.</summary>
  public sealed class TipKindTracker {
    readonly DisagreeTracker dis = new DisagreeTracker();
    static string Name(int k) { return k == 1 ? "replace" : k == 2 ? "equip" : null; }
    /// <summary>The kind to use (1 / 2) or 0 = memory does not decide, the pictures do (rep / eq as found). <paramref name="line"/>: a log line, or null.</summary>
    public int Decide(int memKind, int pixKind, long nowMs, out string line) {
      line = dis.Compare("tip card", Name(memKind), Name(pixKind), nowMs);
      if (memKind == 0) return 0;
      return dis.Held ? 0 : memKind;
    }
    public void Reset() { dis.Reset(); }
  }

  public static class NavNotes {
    static readonly System.Collections.Generic.Queue<string> q = new System.Collections.Generic.Queue<string>();
    public static void Add(string s) { lock (q) { q.Enqueue(s); while (q.Count > 20) q.Dequeue(); } }
    public static System.Collections.Generic.List<string> Take() { lock (q) { var r = new System.Collections.Generic.List<string>(q); q.Clear(); return r; } }
  }

  /// <summary>The hero grid's filter pop-up on a grabbed frame: a dark strip left of the class check boxes, the Faction|Filter
  /// divider, and a second mark that grid lines cannot fake: a blue selected cell of the class / faction columns or,
  /// on older layouts, the Class|Faction divider. Pure: the pixels come from <c>grab(x, y, w, h)</c> in client px.</summary>
  public static class HeroFilterLook {
    public static bool IsBlueRow(int c) {
      int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
      return b >= r + 25 && b >= g + 10 && b >= 70 && r < 140;
    }

    static bool IsStripDark(int c) {
      int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
      return (r + g + b < 170 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 45) || IsBlueRow(c);
    }

    static void StripBox(double U, out int x0, out int y0, out int w, out int h) {
      x0 = (int)(0.030 * U); int x1 = (int)(0.058 * U); y0 = (int)(0.30 * U); int y1 = (int)(0.87 * U); w = x1 - x0; h = y1 - y0;
    }

    public static bool Seen(Func<int, int, int, int, int[]> grab, double U) {
      int x0, y0, w, h; StripBox(U, out x0, out y0, out w, out h);
      int[] px = grab(x0, y0, w, h);
      if (px == null) return false;
      int dark = 0;
      foreach (int c in px) if (IsStripDark(c)) dark++;
      if (dark < 0.85 * px.Length) return false;
      return Marks(grab, U);
    }

    /// <summary>The same on a grab that may hold masked pixels (our own hint): the dark strip is judged on its known pixels (a few masked rows
    /// cannot make it «not known» when the rest already says no / yes; a strip that is no ends the look as a definite no); the marks keep the
    /// withhold rule of <see cref="OwnMask.Read"/> (a grab with a masked pixel is not used).</summary>
    /// <summary>The latest look's verdict and when (Environment.TickCount; 0 = none): the navigator's probe prints it next to the game's flag (mem vs pix in one recording).</summary>
    public static Seen3 LastLook = Seen3.Unknown; public static int LastLookAt;
    /// <summary>Is the grid's filter pop-up open: the game's flag when it answers (Yes / No), the pixel look only when memory is Unknown (E3, docs/specs/2026-11-04-memory-everywhere-plan.md A1).</summary>
    public static bool Open(Mem3 mem, Seen3 look) { return mem != Mem3.Unknown ? mem == Mem3.Yes : look == Seen3.Yes; }
    /// <summary>Whether the host still looks at the picture while memory answers: only to keep the probe's pix= (and the E2 comparison) fresh, once a second; never for a decision.</summary>
    public static bool LogLookDue(Mem3 mem, int lastLookAt, int nowTick) { return mem != Mem3.Unknown && (lastLookAt == 0 || unchecked(nowTick - lastLookAt) >= 1000); }
    public static Seen3 Look(Func<int, int, int, int, int[]> grab, double U) {
      Seen3 r = LookRaw(grab, U);
      LastLook = r; LastLookAt = Environment.TickCount | 1;
      return r;
    }

    static Seen3 LookRaw(Func<int, int, int, int, int[]> grab, double U) {
      int x0, y0, w, h; StripBox(U, out x0, out y0, out w, out h);
      int[] px = grab(x0, y0, w, h);
      if (px == null) return Seen3.No;
      Seen3 strip = OwnMask.ShareAtLeast(px, IsStripDark, 0.85);
      if (strip == Seen3.No) return Seen3.No;
      Seen3 marks = OwnMask.Read(g => Marks(g, U), grab);
      if (marks == Seen3.No) return Seen3.No;
      return strip == Seen3.Yes ? marks : Seen3.Unknown;
    }

    static bool Marks(Func<int, int, int, int, int[]> grab, double U) {
      // a dark strip is not enough: a filtered grid of not-owned heroes (dark empty cards) looks the same, and a click on the
      // funnel then OPENS the pop-up again. Its own marks, 2 of 3 (the divider moved between layouts: at 503x283 on game 1.6.47 the
      // Фракция | Фильтр line is at 0.7705·U and nothing at 0.4836·U; 1920×1009 and 1.6.46 at 526×296 have it at 0.4836·U):
      // (a) the blue «Все» / chosen cell of the class column, (b) the same in the faction column, (c) a thin light divider at
      // 0.2557, 0.4836 or 0.7705·U
      int marks = 0;
      if (BlueCellSeen(grab, U, 0)) marks++;
      if (BlueCellSeen(grab, U, 1)) marks++;
      if (marks < 2) {
        int lines = 0;   // (two different lines are the pop-up even without a blue cell)
        foreach (double fx in new[] { 0.2557, 0.4836, 0.7705 }) if (DividerSeen(grab, (int)(fx * U), (int)(0.16 * U), (int)(0.87 * U))) lines++;
        marks += lines >= 2 ? 2 : lines;
      }
      return marks >= 2;
    }

    /// <summary>Any column.</summary>
    public static bool BlueCellSeen(Func<int, int, int, int, int[]> grab, double U) { return BlueCellSeen(grab, U, 0) || BlueCellSeen(grab, U, 1); }

    // a selected cell: a box of the cell's blue between its check box and its text, in any row of one column (0 class, 1 faction)
    // («Все» when nothing is chosen, the chosen class / faction otherwise). Calibrated on the 1.6.46 recording at 526×296: the
    // pop-up open 0.87 and more, the closed grid 0.51 at most
    public static bool BlueCellSeen(Func<int, int, int, int, int[]> grab, double U, int column) {
      double[] col = column == 0 ? new[] { 0.12, 0.17 } : new[] { 0.35, 0.385 };
      for (int r = 0; r < 7; r++) {
        int x0 = (int)(col[0] * U), x1 = (int)(col[1] * U), yc = (int)((0.2379 + r * 0.1004) * U), hh = (int)(0.03 * U);
        int[] px = grab(x0, yc - hh, x1 - x0, 2 * hh);
        if (px == null || px.Length == 0) continue;
        int n = 0; foreach (int c in px) if (IsBlueRow(c)) n++;
        if (n >= 0.7 * px.Length) return true;
      }
      return false;
    }

    // a thin vertical line brighter than the pop-up around it, near x (a few pixels of play for rounding)
    public static bool DividerSeen(Func<int, int, int, int, int[]> grab, int x, int y0, int y1) {
      const int half = 12;
      int w = 2 * half + 1, h = y1 - y0;
      int[] px = h > 0 ? grab(x - half, y0, w, h) : null;
      if (px == null) return false;
      var mean = new double[w];
      for (int c = 0; c < w; c++) {
        long sum = 0; int n = 0;
        for (int r = 0; r < h; r += 3) { int v = px[r * w + c]; sum += ((v >> 16) & 255) + ((v >> 8) & 255) + (v & 255); n++; }
        mean[c] = n > 0 ? sum / (3.0 * n) : 0;
      }
      for (int c = 4; c < w - 4; c++) {
        double side = (mean[c - 4] + mean[c + 4]) / 2;
        if (mean[c] >= side + 30 && mean[c] >= mean[c - 1] && mean[c] >= mean[c + 1]) return true;
      }
      return false;
    }
  }

  /// <summary>What the host sees this tick for the filter.</summary>
  public sealed class FilterView {
    public long NowMs;
    public bool Foreground, UserBusy;
    public double W, H;
    public long Item;               // the item to find (0 = none)
    public bool Wanted;             // the guide says the item is far down or not in the list: filtering helps
    public long SetId, StatId;      // the item's set and main stat (0 = unknown: not filtered by it)
    public int SetIndex = -1, StatIndex = -1;   // their places in the game's lists (-1 = unknown)
    public long[] SetOrder;         // the sets list in the game's order (a wrongly clicked set tells where the list is)
    public long[] SubIds;           // the item's sub stats (null / empty = not filtered by them)
    public long[] SubOrder;         // the sub stats list of the filter, in its order
    public long[] SubAttrs;         // sub stats chosen in the filter now (null = none)
    public long[] Suits, MainAttrs; // the filter now; null = unreadable
    public int Part = -1, WantPart = -1;   // the slot the pop-up's list shows now (m_FilterConfig.Part) and the item's slot (-1 = not known: no part change)
    public int ItemRow;             // the item's row in the game's list now, from memory (m_EquipIdToIndex, 1-based; 0 = not in the list / not read): the filter stops at the set when it is in the first page
    public bool Foreign;            // other filters are on (level, sub stats): they could hide the item
    public bool ItemListed;         // the guide sees the item in the game's list (its verdict is not «hidden by worn»): the game applied the untick even if the read is stale
    public bool HideEquipped, OnOtherHero;   // OnOtherHero: the plan takes it from that hero on purpose (the guide skips others)
    public bool PanelOpen, SidePanelOpen, SetPanelOpen;   // from the screen (the pictures; the pilot replaces them with the game's flags where those are known: FilterPilot.Resolve)
    /// <summary>The same three from the game's memory (EquipScan.ReadGearMem): Yes / No are exact, Unknown = not readable (the pictures stand). The pop-up, its side panel, the sets panel (m_ButtonStates[1] = Suit, the set-vs-stats test).</summary>
    public Mem3 PanelMem = Mem3.Unknown, SideMem = Mem3.Unknown, SetMem = Mem3.Unknown;
    /// <summary>PanelMem is Unknown only because the panel prefab loads / m_Panels is nil (GearMem.Pending, at most 1.5 s): the filter bar is not pressed meanwhile.</summary>
    public bool MemPending;
    /// <summary>The side panel's list (sets / main stats / sub stats) is dragged or coasting, from the game's own ScrollRect (RFX.ReadGearSideListMem): no cell is clicked on it meanwhile (a coast moves the cell away). False = standing or not read.</summary>
    public bool SideListMoving;
    public bool Resolved;
  }

  /// <summary>The «replace» step's lines name the button the game shows: the page says «Заменить» for every selected item, an empty slot has «Надеть».</summary>
  public static class ConfirmText {
    public static string[] ForButton(string[] lines, bool equip) {
      if (!equip || lines == null) return lines;
      var l = new string[lines.Length];
      for (int i = 0; i < l.Length; i++) l[i] = (lines[i] ?? "").Replace("«Заменить»", "«Надеть»").Replace("“Replace”", "“Equip”");
      return l;
    }
  }

  /// <summary>The game's own flag of the hero grid's filter pop-up on a read hero screen.</summary>
  public static class HeroMem {
    public static Mem3 Of(HeroScreen s) { return s == null ? Mem3.Unknown : s.FilterMem; }
  }

  /// <summary>Which of the filter's picks a guide step wants (hint only).</summary>
  public static class FilterWant {
    public const int DirectMaxRow = 13;
    public static bool WantedFor(string kind, bool onScreen, int row = 0) {
      return kind == "filter" || kind == "hiddenEq" || kind == "hiddenFilter" || kind == "notIn" || ((kind == "pick" || kind == "rel") && !onScreen && (row <= 0 || row > DirectMaxRow));
    }
  }
}
