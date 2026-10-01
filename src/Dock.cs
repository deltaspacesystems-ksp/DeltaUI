using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>Jedna zakladka: tresc rysowana przez mod, ktory ja zarejestrowal.</summary>
    public sealed class Panel
    {
        public readonly string Id;
        public string Title;
        public Action Draw;
        public Rect DefaultRect;
        public bool Scroll;
        public Vector2 ScrollPos;
        internal bool Open;
        internal bool Available;
        internal Func<Texture2D> IconFactory;
        internal Texture2D IconTex;
        internal int Order = 100;

        internal Panel(string id) { Id = id; }
    }

    internal sealed class DockWindow
    {
        public int Id;
        public Rect Rect;
        public readonly List<Panel> Tabs = new List<Panel>();
        public Panel Active;
        public bool Maximized;
        public bool Hidden;                 // okno schowane (hub zminimalizowany): panele w nim zyja dalej, ale nic nie jest rysowane
        public Rect RestoreRect;
        // podzial WEWNATRZ okna: indeks ukladu (0 = brak podzialu), panel w kazdej strefie i strefa, do ktorej trafi kliknieta zakladka
        public int Split;
        public List<string> Zones = new List<string>();
        public int ActiveZone;

        public List<Panel> Visible()
        {
            var v = new List<Panel>();
            if (Hidden) return v;
            foreach (Panel p in Tabs) if (p.Open && p.Available) v.Add(p);
            return v;
        }
    }

    /// <summary>
    /// Okna z zakladkami w stylu ImGui (ReShade / SDR++).
    ///
    ///   - kilka paneli (tez z roznych modow) moze siedziec w jednym oknie jako taby;
    ///   - tab przeciagniety poza pasek zakladek staje sie osobnym oknem;
    ///   - okno upuszczone na pasek zakladek innego okna dolacza do niego;
    ///   - "_" chowa okno (mod gasi swoj przycisk na pasku aplikacji),
    ///     "x" zamyka i przywraca domyslny uklad jego paneli;
    ///   - uklad (pozycje, rozmiary, ktore taby gdzie) zapisuje sie w PluginData/layout.cfg.
    ///
    /// Przeciaganie jest liczone w Update z Input.mousePosition, bo musi dzialac
    /// takze wtedy, gdy mysz wyjedzie poza okno, z ktorego zaczeto.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class Dock : MonoBehaviour
    {
        /// <summary>Panel otwarty albo schowany - przez uzytkownika ("_", "x") albo przez API.
        /// Mody synchronizuja tym swoje przyciski na pasku aplikacji.</summary>
        public static event Action<string, bool> OpenChanged;

        /// <summary>Id panelu-huba: okno z paskiem skrotow do wszystkich okien wszystkich modow.</summary>
        public const string HubId = "deltaui.hub";

        private const int HeaderH = 24, Grip = 18, BaseId = 0x0DE17A00, TabX = 16, LogoW = 38, FlyoutId = 0x0DE17AFF;
        private static readonly Dictionary<string, Panel> panels = new Dictionary<string, Panel>();
        private static readonly List<DockWindow> windows = new List<DockWindow>();
        private static ConfigNode saved;
        private static int nextId;
        private static bool dirty;

        private enum DragKind { None, Tab, Move, Resize }
        private static DragKind drag;
        private static DockWindow dragWin;
        private static Panel dragPanel;
        private static Vector2 dragStart, grab;

        private static GUIStyle winStyle, tabText, btnStyle, closeStyle, flyLabel;

        // przyciaganie okien (jak w Windows 11): podglad strefy przy przeciaganiu do krawedzi i wysuwane uklady
        private static bool hasSnap;
        private static Rect snapZone;
        private static DockWindow flyoutWin, hoverWin;
        private static Rect flyoutRect, flyoutBtn;
        private static float hoverSince, leaveSince = -1f;
        private static bool snapFull;
        private static Rect dragOrigRect;

        /// <summary>Uklady jak w Windows 11, we wspolrzednych wzglednych ekranu (0..1).</summary>
        private static readonly Rect[][] Layouts =
        {
            new[] { new Rect(0f, 0f, 1f, 1f) },
            new[] { new Rect(0f, 0f, .5f, 1f), new Rect(.5f, 0f, .5f, 1f) },
            new[] { new Rect(0f, 0f, 2f / 3f, 1f), new Rect(2f / 3f, 0f, 1f / 3f, 1f) },
            new[] { new Rect(0f, 0f, 1f / 3f, 1f), new Rect(1f / 3f, 0f, 2f / 3f, 1f) },
            new[] { new Rect(0f, 0f, 1f / 3f, 1f), new Rect(1f / 3f, 0f, 1f / 3f, 1f), new Rect(2f / 3f, 0f, 1f / 3f, 1f) },
            new[] { new Rect(0f, 0f, .5f, 1f), new Rect(.5f, 0f, .5f, .5f), new Rect(.5f, .5f, .5f, .5f) },
            new[] { new Rect(0f, 0f, .5f, .5f), new Rect(.5f, 0f, .5f, .5f), new Rect(0f, .5f, .5f, .5f), new Rect(.5f, .5f, .5f, .5f) },
        };
        private static ControlTypes lockedMask;
        private static DockWindow focusWin;                 // okno z fokusem: przechwytuje klawisze, kolko i kamere
        private const string LockId = "DeltaUI_Dock";

        // Dziennik zdarzen myszy (do PluginData/dock-debug.txt) - pozwala sprawdzic, gdzie ginie klikniecie.
        private static readonly List<string> dbg = new List<string>();
        private static bool dbgDirty;
        private static float dbgSaved;
        internal static void Dbg(string s)
        {
            dbg.Add(string.Format("{0,8:F2}  {1}", Time.unscaledTime, s));
            if (dbg.Count > 60) dbg.RemoveAt(0);
            dbgDirty = true;
        }

        private static string LayoutPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/DeltaUI/PluginData/layout.cfg"); }
        }

        // ------------------------------------------------------------------
        //  API dla modow
        // ------------------------------------------------------------------

        /// <summary>
        /// Rejestruje panel (albo odswieza go po zmianie sceny - ten sam id zachowuje miejsce w ukladzie).
        /// defaultRect: gdzie i jak duze jest okno, gdy panel nie ma zapisanego ukladu albo po "x".
        /// </summary>
        public static Panel Register(string id, string title, Action draw, Rect defaultRect, bool scroll = true,
                                     Func<Texture2D> icon = null, int order = 100)
        {
            Load();
            Panel p;
            if (!panels.TryGetValue(id, out p))
            {
                p = new Panel(id);
                panels[id] = p;
                p.Open = SavedOpen(id);
                PlaceFromSaved(p, defaultRect);
            }
            p.Title = title;
            p.Draw = draw;
            p.DefaultRect = defaultRect;
            p.Scroll = scroll;
            p.Available = true;
            if (icon != null) p.IconFactory = icon;          // ikona w pasku skrotow okna hubu (biala maska, barwiona przy rysowaniu)
            if (order != 100) p.Order = order;
            return p;
        }

        /// <summary>Mod znika ze sceny - panel zostaje w ukladzie, ale nie jest rysowany.</summary>
        public static void Unregister(string id)
        {
            Panel p;
            if (panels.TryGetValue(id, out p)) { p.Available = false; p.Draw = null; }
        }

        public static bool IsOpen(string id)
        {
            Panel p;
            return panels.TryGetValue(id, out p) && p.Open;
        }

        public static void SetOpen(string id, bool open)
        {
            Panel p;
            if (!panels.TryGetValue(id, out p) || p.Open == open) return;
            p.Open = open;
            if (id == HubId)
            {
                DockWindow hw = WindowOf(p);
                if (hw != null)
                {
                    if (open) hw.Hidden = false;
                    else { foreach (Panel o in hw.Tabs) if (o != p && o.Open && o.Available) { hw.Hidden = true; break; } }   // w hubie sa zminimalizowane panele - chowamy okno, nie zamykamy ich
                }
            }
            if (open)
            {
                DockWindow w = WindowOf(p);
                if (w != null) { w.Active = p; ClampToScreen(w); }
            }
            dirty = true;
            Fire(id, open);
        }

        public static void Toggle(string id) { SetOpen(id, !IsOpen(id)); }

        /// <summary>Prostokat okna (w pikselach ekranu), w ktorym siedzi panel - razem z jego sasiadami-zakladkami.</summary>
        public static Rect GetRect(string id)
        {
            Panel p;
            DockWindow w;
            return panels.TryGetValue(id, out p) && (w = WindowOf(p)) != null ? w.Rect : new Rect();
        }

        /// <summary>Ustawia polozenie i rozmiar okna panelu (np. "maksymalizuj").</summary>
        public static void SetRect(string id, Rect r)
        {
            Panel p;
            DockWindow w;
            if (!panels.TryGetValue(id, out p) || (w = WindowOf(p)) == null) return;
            r.width = Mathf.Clamp(r.width, 180, Screen.width);
            r.height = Mathf.Clamp(r.height, 90, Screen.height);
            w.Rect = r;
            ClampToScreen(w);
            dirty = true;
        }

        /// <summary>Lewy gorny rog obszaru tresci okna panelu we wspolrzednych ekranu (GUI) - do przeliczania pozycji myszy.</summary>
        public static Vector2 ContentOrigin(string id)
        {
            Panel p;
            DockWindow w;
            if (!panels.TryGetValue(id, out p) || (w = WindowOf(p)) == null) return Vector2.zero;
            if (w.Split > 0)
            {
                int zi = w.Zones.IndexOf(id);
                if (zi >= 0 && zi < Layouts[w.Split].Length)
                {
                    var content = new Rect(6f, HeaderH + 4f, w.Rect.width - 12f, w.Rect.height - HeaderH - 8f);
                    Rect z = ZoneRect(content, Layouts[w.Split][zi]);
                    return w.Rect.position + new Vector2(z.x + 3f, z.y + 3f);
                }
            }
            return w.Rect.position + new Vector2(6f, HeaderH + 4f);
        }

        /// <summary>Czy w tym punkcie ekranu na wierzchu jest okno z tym panelem (aktywnym) - a nie inne okno ani menu ukladow.</summary>
        /// <summary>Czy okno z tym panelem ma fokus (i panel jest w nim widoczny).</summary>
        public static bool IsFocused(string id)
        {
            Panel p;
            if (focusWin == null || !panels.TryGetValue(id, out p)) return false;
            return focusWin.Tabs.Contains(p) && (focusWin.Active == p || (focusWin.Split > 0 && focusWin.Zones.Contains(id)));
        }

        public static bool IsTopAt(string id, Vector2 screen)
        {
            Panel p;
            if (!panels.TryGetValue(id, out p)) return false;
            if (flyoutWin != null && flyoutRect.Contains(screen)) return false;
            DockWindow top = TopWindowAt(screen);
            return top != null && top.Tabs.Contains(p) && (top.Active == p || (top.Split > 0 && top.Zones.Contains(p.Id)));
        }

        /// <summary>Trwa przeciaganie albo zmiana rozmiaru okna.</summary>
        public static bool IsDragging { get { return drag != DragKind.None; } }

        /// <summary>Trwa zmiana rozmiaru okna - tresc moze wtedy wstrzymac kosztowne przerysowania.</summary>
        public static bool Resizing { get { return drag == DragKind.Resize; } }

        // Male liczby ustawien (np. powiekszenie przegladarki) zapisywane razem z ukladem okien.
        private static readonly Dictionary<string, float> nums = new Dictionary<string, float>();

        public static float GetNum(string key, float def)
        {
            Load();
            float f;
            if (nums.TryGetValue(key, out f)) return f;
            ConfigNode n = saved.GetNode("NUM");
            if (n != null && float.TryParse(n.GetValue(key), System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out f))
            { nums[key] = f; return f; }
            return def;
        }

        public static void SetNum(string key, float v)
        {
            Load();
            nums[key] = v;
            dirty = true;
        }

        /// <summary>Czy mysz jest nad ktorymkolwiek oknem (np. zeby mod nie reagowal na klik w swiat).</summary>
        public static bool MouseOver { get; private set; }

        // ------------------------------------------------------------------
        //  Cykl zycia
        // ------------------------------------------------------------------

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            Save();
            if (lockedMask != 0) InputLockManager.RemoveControlLock(LockId);
        }

        private void Update()
        {
            Vector2 m = MouseGui();
            MouseOver = false;
            foreach (DockWindow w in windows)
                if (w.Visible().Count > 0 && w.Rect.Contains(m)) { MouseOver = true; break; }
            if (drag != DragKind.None) MouseOver = true;
            if (flyoutWin != null)
            {
                bool near = flyoutRect.Contains(m) || flyoutBtn.Contains(m);
                if (flyoutRect.Contains(m)) MouseOver = true;
                if (near) leaveSince = -1f; else if (leaveSince < 0f) leaveSince = Time.unscaledTime;
                // klik poza menu albo chwila poza nim i poza przyciskiem zamyka je (jak w Windows 11)
                if ((Input.GetMouseButtonDown(0) && !near) || (leaveSince > 0f && Time.unscaledTime - leaveSince > 0.35f))
                { flyoutWin = null; leaveSince = -1f; }
            }
            else leaveSince = -1f;

            // Fokus: klik w okno daje mu fokus (i przynosi je na wierzch), klik poza oknami go odbiera.
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
            {
                DockWindow hit = flyoutWin != null && flyoutRect.Contains(m) ? focusWin : TopWindowAt(m);
                Dbg(string.Format("Update  down  screen=({0:F0},{1:F0})  okno={2}  aktywny={3}", m.x, m.y,
                                  hit != null ? hit.Id.ToString("X") : "-", hit != null && hit.Active != null ? hit.Active.Id : "-"));
                focusWin = hit;
                if (hit != null && windows[windows.Count - 1] != hit) { windows.Remove(hit); windows.Add(hit); }
            }
            if (focusWin != null && (!windows.Contains(focusWin) || focusWin.Visible().Count == 0)) focusWin = null;

            // Okno z fokusem przechwytuje wszystko: klawisze, kolko, kamere, sterowanie (poza Esc/pauza).
            // Bez fokusu wystarcza kamera, gdy mysz jest nad oknem.
            ControlTypes want = focusWin != null ? (ControlTypes.All & ~ControlTypes.PAUSE)
                              : MouseOver ? ControlTypes.CAMERACONTROLS : (ControlTypes)0;
            if (want != lockedMask)
            {
                if (lockedMask != 0) InputLockManager.RemoveControlLock(LockId);
                if (want != 0) InputLockManager.SetControlLock(want, LockId);
                lockedMask = want;
            }

            if (dbgDirty && Time.unscaledTime - dbgSaved > 1f)
            {
                dbgDirty = false;
                dbgSaved = Time.unscaledTime;
                try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(LayoutPath), "dock-debug.txt"), string.Join("\n", dbg.ToArray())); } catch (Exception) { }
            }

            if (drag != DragKind.None)
            {
                if (!Input.GetMouseButton(0)) EndDrag(m);
                else StepDrag(m);
            }

            if (dirty && drag == DragKind.None) { Save(); dirty = false; }
        }

        private void OnGUI()
        {
            if (windows.Count == 0) return;
            Styles();
            GUISkin old = GUI.skin;
            GUI.skin = Theme.Skin;
            for (int i = 0; i < windows.Count; i++)
            {
                DockWindow w = windows[i];
                if (w.Visible().Count == 0) continue;
                w.Rect = GUI.Window(w.Id, w.Rect, id => DrawWindow(w), GUIContent.none, winStyle);
            }

            // podglad strefy przyciagania: polprzezroczysty prostokat z ramka
            if (drag == DragKind.Move && hasSnap)
            {
                Theme.Fill(snapZone, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.22f));
                Frame(snapZone, Theme.Accent);
            }

            if (flyoutWin != null)
            {
                flyoutRect = FlyoutRectFor(flyoutWin);
                flyoutRect = GUI.Window(FlyoutId, flyoutRect, id => DrawFlyout(), GUIContent.none, winStyle);
                GUI.BringWindowToFront(FlyoutId);
            }
            GUI.skin = old;
        }

        // ------------------------------------------------------------------
        //  Rysowanie okna
        // ------------------------------------------------------------------

        private static void DrawWindow(DockWindow w)
        {
            Event e = Event.current;
            List<Panel> all = w.Visible();
            var tabs = new List<Panel>();
            foreach (Panel p in all) if (p.Id != HubId) tabs.Add(p);          // hub nie ma zakladki - to samo "puste okno"
            bool hub = HasHub(w);
            if (w.Active == null || !all.Contains(w.Active)) w.Active = tabs.Count > 0 ? tabs[0] : all[0];
            float width = w.Rect.width, height = w.Rect.height;

            // Uchwyt zmiany rozmiaru obslugujemy PIERWSZY - inaczej kontrolka lezaca w rogu tresci (etykieta,
            // pasek przewijania) przechwyci klikniecie i okno nie da sie zmienic.
            if (e.type == EventType.MouseDown && e.button == 0 && new Rect(width - Grip, height - Grip, Grip, Grip).Contains(e.mousePosition))
            {
                BeginDrag(DragKind.Resize, w, null, e.mousePosition + w.Rect.position);
                e.Use();
                return;
            }

            // --- pasek tytulu ---
            var head = new Rect(0, 0, width, HeaderH);
            if (e.type == EventType.Repaint)
            {
                Theme.Fill(head, focusWin == w ? Theme.TitleActive : Theme.TitleBg);      // jak w ImGui: aktywne okno ma niebieski pasek
                Theme.Fill(new Rect(0, HeaderH - 1, width, 1), Theme.Border);
                // cel upuszczenia: przeciagane okno nad tym paskiem
                if (drag == DragKind.Move && dragWin != w && HeaderScreen(w).Contains(MouseGui()))
                    Theme.Fill(new Rect(0, 0, width, HeaderH), new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.35f));
            }

            // uklad od prawej: [ikony skrotow] [_] [okno] [x]; zakladki zajmuja reszte z lewej
            float bw = HeaderH - 3;
            float btnX = width - 3 * (bw + 2) - 2;
            bool showStrip = width >= 400f;                      // w waskim oknie ikony sie nie mieszcza
            List<Panel> shortcuts = showStrip ? Shortcuts() : null;
            float stripW = showStrip ? shortcuts.Count * (bw + 2) : 0f;
            float stripX = btnX - (showStrip ? 8f : 4f) - stripW;
            float tabsLimit = stripX - 4f;

            // logo: bialy napis ΔUI w lewym gornym rogu paska tytulu; klik = puste okno hubu
            var logoR = new Rect(2, 2, LogoW, HeaderH - 3);
            if (e.type == EventType.MouseDown && e.button == 0 && logoR.Contains(e.mousePosition))
            {
                Panel hp;
                if (hub && panels.TryGetValue(HubId, out hp)) w.Active = hp; else SetOpen(HubId, true);
                e.Use();
                return;
            }
            if (e.type == EventType.Repaint)
            {
                Color oldC = GUI.contentColor;
                GUI.contentColor = Color.white;
                tabText.Draw(new Rect(2, 2, LogoW, HeaderH - 3), new GUIContent("\u0394UI"), false, false, false, false);
                GUI.contentColor = oldC;
            }

            float x = 2 + LogoW + 2;
            foreach (Panel p in tabs)
            {
                float tw = Mathf.Min(tabText.CalcSize(new GUIContent(p.Title)).x + 16 + TabX, Mathf.Max(40 + TabX, tabsLimit - x));
                var r = new Rect(x, 2, tw, HeaderH - 3);
                var xr = new Rect(r.xMax - TabX, r.y, TabX, r.height);         // krzyzyk na kazdej zakladce
                bool over = r.Contains(e.mousePosition), overX = xr.Contains(e.mousePosition);
                if (e.type == EventType.Repaint)
                {
                    bool shown = w.Split > 0 ? w.Zones.Contains(p.Id) : p == w.Active;
                    Color c = shown ? Theme.TabActive : over ? Theme.TabHover : Theme.Tab;
                    Theme.FillRounded(r, c, new Vector4(4, 4, 0, 0));
                    tabText.Draw(new Rect(r.x, r.y, r.width - TabX, r.height), new GUIContent(p.Title), false, false, false, false);
                    if (overX) Theme.Fill(xr, Theme.Hex(0xB04040));
                    tabText.Draw(xr, new GUIContent("\u00D7"), false, false, false, false);
                }
                else if (e.type == EventType.MouseDown && e.button == 0 && over)
                {
                    if (overX) { ClosePanel(p); e.Use(); return; }             // x na zakladce zamyka tylko ja
                    w.Active = p;
                    if (w.Split > 0) AssignZone(w, p);                       // w podziale zakladka trafia do wybranej strefy
                    BeginDrag(DragKind.Tab, w, p, e.mousePosition + w.Rect.position);
                    e.Use();
                }
                x += tw + 2;
            }

            // --- ikony skrotow do wszystkich okien: niebieskie przyciski jak "_" i "x", w kazdym oknie ---
            string tip = null;
            float tipX = 0f;
            if (showStrip)
            {
                float ix = stripX;
                foreach (Panel p in shortcuts)
                {
                    var ir = new Rect(ix, 2, bw, bw);
                    ix += bw + 2;
                    GUI.enabled = p.Available;
                    bool on = GUI.Toggle(ir, p.Open, GUIContent.none, btnStyle);
                    GUI.enabled = true;
                    if (e.type == EventType.Repaint)
                    {
                        DrawPanelIcon(ir, p, p.Available ? Color.white : Theme.TextDim);
                        if (ir.Contains(e.mousePosition)) { tip = p.Title; tipX = ir.center.x; }
                    }
                    if (on != p.Open)
                    {
                        // otwarte: na wierzch; zamkniete: otwieramy w tym oknie jako zakladke
                        if (p.Open) FocusPanel(p); else OpenInto(p, w);
                        return;
                    }
                }
            }
            // --- przyciski okna: minimalizuj, pelny ekran (menu ukladow po najechaniu), zamknij ---
            var minR = new Rect(btnX, 2, bw, bw);
            var maxR = new Rect(btnX + bw + 2, 2, bw, bw);
            var closeR = new Rect(btnX + 2 * (bw + 2), 2, bw, bw);
            if (GUI.Button(minR, "_", btnStyle)) { Minimize(w); return; }
            bool maxClick = GUI.Button(maxR, GUIContent.none, btnStyle);
            bool overMax = maxR.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                DrawMaxIcon(maxR, w.Maximized, overMax);
                if (overMax)
                {
                    // jak w Windows 11: chwila nad przyciskiem pelnego ekranu wysuwa uklady
                    if (hoverWin != w) { hoverWin = w; hoverSince = Time.unscaledTime; }
                    else if (flyoutWin != w && Time.unscaledTime - hoverSince > 0.45f) flyoutWin = w;
                }
                else if (hoverWin == w) hoverWin = null;
                if (flyoutWin == w) flyoutBtn = new Rect(w.Rect.x + maxR.x, w.Rect.y + maxR.y, maxR.width, maxR.height);
            }
            if (maxClick) { ToggleMaximize(w); return; }
            if (GUI.Button(closeR, "x", closeStyle)) { Close(w); return; }

            // puste miejsce paska = przesuwanie okna, dwuklik = pelny ekran
            if (e.type == EventType.MouseDown && e.button == 0 && head.Contains(e.mousePosition))
            {
                if (e.clickCount == 2) { ToggleMaximize(w); e.Use(); return; }
                BeginDrag(DragKind.Move, w, null, e.mousePosition + w.Rect.position);
                e.Use();
            }

            // --- tresc: jeden panel albo podzial okna na strefy ---
            var content = new Rect(6, HeaderH + 4, width - 12, height - HeaderH - 8);
            if (w.Split > 0 && tabs.Count > 0) DrawSplit(w, content, tabs);
            else DrawPanelContent(w.Active, content);

            // --- uchwyt zmiany rozmiaru: rysowanie (klik obsluzony na poczatku okna) ---
            if (e.type == EventType.Repaint)
            {
                bool hot = drag == DragKind.Resize && dragWin == w;
                bool over = new Rect(width - Grip, height - Grip, Grip, Grip).Contains(e.mousePosition);
                Color c = hot ? Theme.Accent : over ? Theme.Text : Theme.TextDim;
                for (int i = 0; i < 4; i++)
                    for (int k = 0; k <= i; k++)
                        Theme.Fill(new Rect(width - 5 - 4 * k, height - 5 - 4 * (i - k), 2, 2), c);

                if (focusWin == w) Frame(new Rect(0, 0, width, height), Theme.Accent);        // okno z fokusem

                // podpowiedz z nazwa okna pod ikona - na wierzchu tresci
                if (tip != null)
                {
                    float tw = tabText.CalcSize(new GUIContent(tip)).x + 14;
                    var tr = new Rect(Mathf.Clamp(tipX - tw / 2f, 2f, width - tw - 2f), HeaderH + 3, tw, 18);
                    Theme.Fill(tr, Theme.TitleBg);
                    Frame(tr, Theme.Border);
                    tabText.Draw(tr, new GUIContent(tip), false, false, false, false);
                }
            }
        }
        private static void Styles()
        {
            if (winStyle != null) return;
            winStyle = new GUIStyle(Theme.Skin.box);
            winStyle.normal.background = Theme.Rounded(Theme.WindowBg, Theme.Border);
            winStyle.onNormal.background = winStyle.normal.background;
            winStyle.padding = new RectOffset(0, 0, 0, 0);
            winStyle.border = new RectOffset(3, 3, 3, 3);
            tabText = new GUIStyle(Theme.Skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(4, 4, 0, 0) };
            btnStyle = new GUIStyle(Theme.Skin.button) { padding = new RectOffset(0, 0, 0, 2), margin = new RectOffset(0, 0, 0, 0) };
            // przyciski okna: niebieskie (jak w ΔUI), a "x" po najechaniu czerwony
            var blue = Theme.Rounded(Theme.WinBtn); var blueH = Theme.Rounded(Theme.WinBtnHover); var blueA = Theme.Rounded(Theme.Accent);
            foreach (GUIStyleState st in new[] { btnStyle.normal, btnStyle.focused, btnStyle.onNormal }) st.background = blue;
            foreach (GUIStyleState st in new[] { btnStyle.hover, btnStyle.onHover }) st.background = blueH;
            foreach (GUIStyleState st in new[] { btnStyle.active, btnStyle.onActive }) st.background = blueA;
            closeStyle = new GUIStyle(btnStyle);
            var red = Theme.Rounded(Theme.CloseHover); var redA = Theme.Rounded(Theme.Hex(0x962D22));
            closeStyle.hover.background = red; closeStyle.onHover.background = red;
            closeStyle.active.background = redA; closeStyle.onActive.background = redA;
            flyLabel = new GUIStyle(Theme.Skin.label) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
            flyLabel.normal.textColor = Theme.TextDim;
        }

        // ------------------------------------------------------------------
        //  Przeciaganie
        // ------------------------------------------------------------------

        private static void BeginDrag(DragKind kind, DockWindow w, Panel p, Vector2 mouse)
        {
            if (kind != DragKind.Tab) w.Maximized = false;
            dragOrigRect = w.Rect;
            drag = kind;
            dragWin = w;
            dragPanel = p;
            dragStart = mouse;
            grab = kind == DragKind.Resize ? w.Rect.size - (mouse - w.Rect.position) : mouse - w.Rect.position;
            windows.Remove(w);
            windows.Add(w);          // na wierzch w kolejnosci rysowania
        }

        private static void StepDrag(Vector2 m)
        {
            switch (drag)
            {
                case DragKind.Tab:
                    if ((m - dragStart).magnitude < 8f || HeaderScreen(dragWin).Contains(m)) return;
                    if (TabCount(dragWin) > 1)
                    {
                        // wyrwanie zakladki: nowe okno pod kursorem, dalej ciagniete jak zwykle okno
                        dragWin.Tabs.Remove(dragPanel);
                        var nw = NewWindow(new Rect(m.x - 40, m.y - 10, dragWin.Rect.width, dragWin.Rect.height));
                        nw.Tabs.Add(dragPanel);
                        nw.Active = dragPanel;
                        dragWin = nw;
                        grab = new Vector2(40, 10);
                    }
                    drag = DragKind.Move;
                    break;
                case DragKind.Move:
                    dragWin.Rect.position = m - grab;
                    ClampToScreen(dragWin);
                    Rect norm;
                    hasSnap = TryZone(m, out norm);                       // przy krawedzi ekranu: podglad strefy
                    if (hasSnap) { snapZone = ScreenZone(norm); snapFull = norm.width >= 1f && norm.height >= 1f; }
                    break;
                case DragKind.Resize:
                    Vector2 size = m - dragWin.Rect.position + grab;
                    dragWin.Rect.width = Mathf.Clamp(size.x, 180, Screen.width);
                    dragWin.Rect.height = Mathf.Clamp(size.y, 90, Screen.height);
                    break;
            }
        }

        private static void EndDrag(Vector2 m)
        {
            if (drag == DragKind.Move && hasSnap)
            {
                if (snapFull) { dragWin.RestoreRect = dragOrigRect; dragWin.Maximized = true; }
                dragWin.Rect = snapZone;                                  // upuszczone przy krawedzi = przyciagniecie
            }
            else if (drag == DragKind.Move)
            {
                // upuszczone na pasek zakladek innego okna = dolacz do niego
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    DockWindow t = windows[i];
                    if (t == dragWin || t.Visible().Count == 0 || !HeaderScreen(t).Contains(m)) continue;
                    Panel act = dragWin.Active;
                    t.Tabs.AddRange(dragWin.Tabs);
                    t.Active = act;
                    windows.Remove(dragWin);
                    break;
                }
            }
            hasSnap = false;
            drag = DragKind.None;
            dragWin = null;
            dragPanel = null;
            dirty = true;
        }

        // ------------------------------------------------------------------
        //  "_" i "x"
        // ------------------------------------------------------------------

        /// <summary>Okno z panelem-hubem (glowne okno ΔUI), o ile hub jest otwarty.</summary>
        private static DockWindow HubWindow()
        {
            Panel hp;
            if (!panels.TryGetValue(HubId, out hp) || !hp.Open || !hp.Available) return null;
            return WindowOf(hp);
        }

        /// <summary>
        /// "_": inne okno wraca do glownego okna ΔUI jako zakladki - bez fokusu (aktywna zakladka hubu sie nie zmienia).
        /// Glowne okno ΔUI (albo brak otwartego huba) po prostu sie chowa.
        /// </summary>
        private sealed class Snap
        {
            public readonly List<string> Ids = new List<string>();
            public string Active;
            public Rect Rect, Restore;
            public bool Maximized;
        }
        private static readonly List<Snap> snaps = new List<Snap>();

        private static void Minimize(DockWindow w)
        {
            if (!HasHub(w))
            {
                // glowne okno ΔUI musi byc, zeby bylo dokad wrocic
                if (HubWindow() == null) SetOpen(HubId, true);
                DockWindow home = HubWindow();
                if (home != null && home != w)
                {
                    var snap = new Snap { Rect = w.Rect, Restore = w.RestoreRect, Maximized = w.Maximized, Active = w.Active != null ? w.Active.Id : null };
                    foreach (Panel p in new List<Panel>(w.Tabs))
                    {
                        if (!p.Open || !p.Available) continue;
                        snap.Ids.Add(p.Id);
                        w.Tabs.Remove(p);
                        home.Tabs.Add(p);                      // panel zostaje otwarty i dziala (np. przegladarka gra dalej)
                    }
                    if (snap.Ids.Count > 0) snaps.Add(snap);
                    if (w.Tabs.Count == 0) windows.Remove(w);
                    dirty = true;
                    return;
                }
            }
            // glowne okno ΔUI: chowa sie samo; zminimalizowane w nim panele zostaja otwarte w tle
            SetOpen(HubId, false);
            dirty = true;
        }

        /// <summary>Po klikniecu ΔUI: zminimalizowane okna wracaja dokladnie tam i takie, jakie byly (rozmiar, zakladki, pelny ekran).</summary>
        public static void RestoreMinimized()
        {
            foreach (Snap sn in snaps)
            {
                var back = new List<Panel>();
                foreach (string id in sn.Ids)
                {
                    Panel p;
                    if (panels.TryGetValue(id, out p) && p.Open && p.Available && p.Id != HubId) back.Add(p);
                }
                if (back.Count == 0) continue;
                DockWindow nw = NewWindow(sn.Rect);
                foreach (Panel p in back)
                {
                    DockWindow from = WindowOf(p);
                    if (from != null) { from.Tabs.Remove(p); if (from.Tabs.Count == 0) windows.Remove(from); }
                    nw.Tabs.Add(p);
                }
                nw.Active = back.Find(x => x.Id == sn.Active) ?? back[0];
                nw.Maximized = sn.Maximized;
                nw.RestoreRect = sn.Restore;
                nw.Rect = sn.Rect;
                focusWin = nw;
            }
            if (snaps.Count > 0) dirty = true;
            snaps.Clear();
        }

        private static void Close(DockWindow w)
        {
            // kazdy panel wraca do wlasnego okna w domyslnym miejscu
            var list = new List<Panel>(w.Tabs);
            windows.Remove(w);
            foreach (Panel p in list)
            {
                var nw = NewWindow(p.DefaultRect);
                nw.Tabs.Add(p);
                nw.Active = p;
                if (p.Open) { p.Open = false; Fire(p.Id, false); }
            }
            dirty = true;
        }

        // ------------------------------------------------------------------
        //  Krzyzyk zakladki, pasek skrotow (hub) i przyciaganie
        // ------------------------------------------------------------------

        /// <summary>"x" na zakladce: panel wraca do wlasnego okna w domyslnym miejscu i sie zamyka; reszta zakladek zostaje.</summary>
        private static void ClosePanel(Panel p)
        {
            DockWindow w = WindowOf(p);
            if (w != null)
            {
                w.Tabs.Remove(p);
                if (w.Tabs.Count == 0) windows.Remove(w);
            }
            var nw = NewWindow(p.DefaultRect);
            nw.Tabs.Add(p);
            nw.Active = p;
            if (p.Open) { p.Open = false; Fire(p.Id, false); }
            dirty = true;
        }

        /// <summary>Ile zakladek widac w oknie (sam hub nie ma zakladki).</summary>
        private static int TabCount(DockWindow w)
        {
            int n = 0;
            foreach (Panel p in w.Visible()) if (p.Id != HubId) n++;
            return n;
        }

        private static bool HasHub(DockWindow w)
        {
            foreach (Panel p in w.Tabs) if (p.Id == HubId) return true;
            return false;
        }

        /// <summary>Panel przechodzi do danego okna jako zakladka i sie otwiera.</summary>
        private static void OpenInto(Panel p, DockWindow target)
        {
            DockWindow from = WindowOf(p);
            if (from != target)
            {
                if (from != null)
                {
                    from.Tabs.Remove(p);
                    if (from.Tabs.Count == 0) windows.Remove(from);
                }
                target.Tabs.Add(p);
            }
            target.Active = p;
            if (!p.Open) { p.Open = true; Fire(p.Id, true); }
            dirty = true;
        }

        private static void FocusPanel(Panel p)
        {
            DockWindow w = WindowOf(p);
            if (w == null) return;
            w.Active = p;
            if (w.Split > 0) AssignZone(w, p);
            windows.Remove(w);
            windows.Add(w);                       // na wierzch
        }

        /// <summary>Skroty w pasku okna hubu: wszystkie panele wszystkich modow (bez samego hubu), wg kolejnosci i nazwy.</summary>
        private static List<Panel> Shortcuts()
        {
            var list = new List<Panel>();
            foreach (Panel p in panels.Values) if (p.Id != HubId) list.Add(p);
            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Ikona panelu (biala maska z alfa) barwiona na kolor stanu; bez ikony - pierwsza litera nazwy.</summary>
        private static void DrawPanelIcon(Rect r, Panel p, Color c)
        {
            if (p.IconTex == null && p.IconFactory != null)
            {
                try { p.IconTex = p.IconFactory(); } catch (Exception) { p.IconFactory = null; }
            }
            Color old = GUI.color, oldC = GUI.contentColor;
            GUI.color = c;
            if (p.IconTex != null) GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), p.IconTex, ScaleMode.ScaleToFit, true);
            else { GUI.contentColor = c; tabText.Draw(r, new GUIContent(p.Title.Length > 0 ? p.Title.Substring(0, 1) : "?"), false, false, false, false); }
            GUI.color = old;
            GUI.contentColor = oldC;
        }

        private static void DrawMaxIcon(Rect r, bool maximized, bool over)
        {
            Color c = over ? Theme.Accent : Theme.Text;
            if (!maximized) Frame(new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8), c);
            else
            {
                Frame(new Rect(r.x + 4, r.y + 6, r.width - 10, r.height - 10), c);       // dwa nakladajace sie kwadraty = przywroc
                Frame(new Rect(r.x + 6, r.y + 4, r.width - 10, r.height - 10), c);
            }
        }

        /// <summary>Pelny ekran / przywrocenie poprzedniego rozmiaru.</summary>
        private static void ToggleMaximize(DockWindow w)
        {
            if (!w.Maximized)
            {
                w.RestoreRect = w.Rect;
                w.Rect = ScreenZone(new Rect(0f, 0f, 1f, 1f));
                w.Maximized = true;
            }
            else
            {
                w.Rect = w.RestoreRect;
                w.Maximized = false;
            }
            flyoutWin = null;
            dirty = true;
        }

        /// <summary>Przywraca domyslny uklad wszystkich okien (przycisk w ustawieniach).</summary>
        public static void ResetLayout()
        {
            foreach (Panel p in new List<Panel>(panels.Values))
            {
                DockWindow w = WindowOf(p);
                if (w != null) { w.Tabs.Remove(p); if (w.Tabs.Count == 0) windows.Remove(w); }
                var nw = NewWindow(p.DefaultRect);
                nw.Tabs.Add(p);
                nw.Active = p;
                if (p.Open) { p.Open = false; Fire(p.Id, false); }
            }
            dirty = true;
        }
        private static void Frame(Rect r, Color c)
        {
            Theme.Fill(new Rect(r.x, r.y, r.width, 1), c);
            Theme.Fill(new Rect(r.x, r.yMax - 1, r.width, 1), c);
            Theme.Fill(new Rect(r.x, r.y, 1, r.height), c);
            Theme.Fill(new Rect(r.xMax - 1, r.y, 1, r.height), c);
        }

        /// <summary>Strefa przyciagania pod kursorem przy krawedzi ekranu: polowa, cwiartka (rog) albo cale okno (gora).</summary>
        private static bool TryZone(Vector2 m, out Rect norm)
        {
            norm = new Rect();
            float W = Screen.width, H = Screen.height, E = 8f, C = 110f;
            bool l = m.x <= E, r = m.x >= W - E, t = m.y <= E, b = m.y >= H - E;
            if (!(l || r || t || b)) return false;
            bool nl = m.x <= C, nr = m.x >= W - C, nt = m.y <= C, nb = m.y >= H - C;

            if ((l || r) && nt) { norm = new Rect(l ? 0f : .5f, 0f, .5f, .5f); return true; }
            if ((l || r) && nb) { norm = new Rect(l ? 0f : .5f, .5f, .5f, .5f); return true; }
            if (t && (nl || nr)) { norm = new Rect(nl ? 0f : .5f, 0f, .5f, .5f); return true; }
            if (b && (nl || nr)) { norm = new Rect(nl ? 0f : .5f, .5f, .5f, .5f); return true; }
            if (l) { norm = new Rect(0f, 0f, .5f, 1f); return true; }
            if (r) { norm = new Rect(.5f, 0f, .5f, 1f); return true; }
            if (t) { norm = new Rect(0f, 0f, 1f, 1f); return true; }
            norm = new Rect(0f, .5f, 1f, .5f);
            return true;
        }

        /// <summary>Strefa wzgledna (0..1) na piksele ekranu, z niewielkim odstepem jak w Windows 11.</summary>
        private static Rect ScreenZone(Rect n)
        {
            var r = new Rect(n.x * Screen.width, n.y * Screen.height, n.width * Screen.width, n.height * Screen.height);
            return new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8);
        }

        private static void SnapTo(DockWindow w, Rect norm)
        {
            bool full = norm.width >= 1f && norm.height >= 1f;
            if (full && !w.Maximized) w.RestoreRect = w.Rect;
            w.Maximized = full;
            w.Rect = ScreenZone(norm);
            dirty = true;
        }

        private const float FlyTileW = 88f, FlyTileH = 54f, FlyGap = 8f;

        private const float FlySec = 142f;                 // jedna sekcja menu: etykieta + dwa rzedy kafli

        private static Rect FlyoutRectFor(DockWindow w)
        {
            float wpx = 4 * FlyTileW + 5 * FlyGap, hpx = 2 * FlySec + 2 * FlyGap;
            float x = Mathf.Clamp(w.Rect.xMax - wpx, 0f, Screen.width - wpx);
            float y = Mathf.Min(w.Rect.y + HeaderH + 2, Screen.height - hpx);
            return new Rect(x, y, wpx, hpx);
        }

        /// <summary>
        /// Wysuwane menu ukladow, dwa rodzaje jak w Windows 11:
        ///   1) "Snap window to screen" - klik w strefe uklada CALE okno w tej strefie ekranu;
        ///   2) "Split inside this window" - klik w uklad dzieli TRESC okna na strefy, w kazdej inny panel.
        /// </summary>
        private static void DrawFlyout()
        {
            Event e = Event.current;
            for (int sec = 0; sec < 2; sec++)
            {
                float y0 = FlyGap * 0.5f + sec * (FlySec + FlyGap);
                if (e.type == EventType.Repaint)
                    flyLabel.Draw(new Rect(FlyGap, y0, 300, 16), new GUIContent(sec == 0 ? "Snap window to screen" : "Split inside this window"), false, false, false, false);

                for (int i = 0; i < Layouts.Length; i++)
                {
                    var tile = new Rect(FlyGap + (i % 4) * (FlyTileW + FlyGap), y0 + 18 + (i / 4) * (FlyTileH + FlyGap), FlyTileW, FlyTileH);
                    bool current = sec == 1 && flyoutWin != null && flyoutWin.Split == i;
                    bool tileOver = tile.Contains(e.mousePosition);
                    if (e.type == EventType.Repaint) { Theme.Fill(tile, Theme.TitleBg); Frame(tile, current ? Theme.Accent : Theme.Border); }

                    foreach (Rect z in Layouts[i])
                    {
                        var zr = new Rect(tile.x + 3 + z.x * (tile.width - 6) + 1.5f, tile.y + 3 + z.y * (tile.height - 6) + 1.5f,
                                          z.width * (tile.width - 6) - 3f, z.height * (tile.height - 6) - 3f);
                        bool over = sec == 0 ? zr.Contains(e.mousePosition) : tileOver;     // ekran: pojedyncza strefa; podzial: caly uklad
                        if (e.type == EventType.Repaint) Theme.Fill(zr, over ? Theme.Accent : Theme.FrameActive);
                        if (sec == 0 && e.type == EventType.MouseDown && e.button == 0 && over && flyoutWin != null)
                        {
                            SnapTo(flyoutWin, z);
                            flyoutWin = null;
                            e.Use();
                            return;
                        }
                    }
                    if (sec == 1 && e.type == EventType.MouseDown && e.button == 0 && tileOver && flyoutWin != null)
                    {
                        SetSplit(flyoutWin, i);
                        flyoutWin = null;
                        e.Use();
                        return;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        //  Podzial okna na strefy
        // ------------------------------------------------------------------

        private static void DrawPanelContent(Panel a, Rect area)
        {
            GUILayout.BeginArea(area);
            if (a.Scroll) a.ScrollPos = GUILayout.BeginScrollView(a.ScrollPos);
            try { if (a.Draw != null) a.Draw(); }
            catch (Exception ex) { GUILayout.Label("Error: " + ex.Message); }
            if (a.Scroll) GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static Rect ZoneRect(Rect content, Rect n)
        {
            var r = new Rect(content.x + n.x * content.width, content.y + n.y * content.height, n.width * content.width, n.height * content.height);
            return new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f);
        }

        /// <summary>Zgrywa strefy z ukladem i zakladkami: znikajace panele zwalniaja strefy, nowe wypelniaja puste (aktywny pierwszy).</summary>
        private static void EnsureZones(DockWindow w, List<Panel> tabs)
        {
            int count = Layouts[w.Split].Length;
            while (w.Zones.Count < count) w.Zones.Add("");
            if (w.Zones.Count > count) w.Zones.RemoveRange(count, w.Zones.Count - count);
            for (int i = 0; i < count; i++)
            {
                bool known = false, dup = false;
                foreach (Panel t in tabs) if (t.Id == w.Zones[i]) { known = true; break; }
                for (int j = 0; j < i; j++) if (w.Zones[j] == w.Zones[i]) dup = true;
                if (!known || dup) w.Zones[i] = "";
            }
            var order = new List<Panel>();
            if (w.Active != null && tabs.Contains(w.Active)) order.Add(w.Active);
            foreach (Panel t in tabs) if (!order.Contains(t)) order.Add(t);
            foreach (Panel t in order)
            {
                if (w.Zones.Contains(t.Id)) continue;
                int slot = w.Zones.IndexOf("");
                if (slot < 0) break;
                w.Zones[slot] = t.Id;
            }
            if (w.ActiveZone >= count) w.ActiveZone = 0;
        }

        /// <summary>Klik w zakladke w podziale: panel trafia do wybranej strefy (zamiana miejscami, jesli byl juz w innej).</summary>
        private static void AssignZone(DockWindow w, Panel p)
        {
            if (w.Split <= 0) return;
            int count = Layouts[w.Split].Length;
            while (w.Zones.Count < count) w.Zones.Add("");
            int z = Mathf.Clamp(w.ActiveZone, 0, count - 1);
            int j = w.Zones.IndexOf(p.Id);
            if (j == z) return;
            if (j >= 0) { string old = w.Zones[z]; w.Zones[z] = p.Id; w.Zones[j] = old; }
            else w.Zones[z] = p.Id;
            dirty = true;
        }

        private static void SetSplit(DockWindow w, int layout)
        {
            w.Split = layout <= 0 ? 0 : layout;
            w.Zones.Clear();
            w.ActiveZone = 0;
            dirty = true;
        }

        private static void DrawSplit(DockWindow w, Rect content, List<Panel> tabs)
        {
            Event e = Event.current;
            EnsureZones(w, tabs);
            Rect[] zs = Layouts[w.Split];
            for (int i = 0; i < zs.Length; i++)
            {
                Rect z = ZoneRect(content, zs[i]);
                // klik w strefe wybiera ja jako cel dla zakladki; zdarzenia nie zuzywamy - tresc tez je dostaje
                if (e.type == EventType.MouseDown && e.button == 0 && z.Contains(e.mousePosition)) w.ActiveZone = i;
                Panel zp = null;
                foreach (Panel t in tabs) if (t.Id == w.Zones[i]) { zp = t; break; }
                if (e.type == EventType.Repaint)
                {
                    Theme.Fill(z, Theme.ChildBg);
                    Frame(z, i == w.ActiveZone ? Theme.Accent : Theme.Border);
                    if (zp == null) flyLabel.Draw(new Rect(z.x + 8, z.y + 6, z.width - 16, 18), new GUIContent("empty - click a tab to put it here"), false, false, false, false);
                }
                if (zp != null) DrawPanelContent(zp, new Rect(z.x + 3, z.y + 3, z.width - 6, z.height - 6));
            }
        }
        // ------------------------------------------------------------------
        //  Pomocnicze
        // ------------------------------------------------------------------

        private static Vector2 MouseGui()
        {
            Vector3 m = Input.mousePosition;
            return new Vector2(m.x, Screen.height - m.y);
        }

        private static Rect HeaderScreen(DockWindow w)
        {
            return new Rect(w.Rect.x, w.Rect.y, w.Rect.width, HeaderH);
        }

        private static DockWindow NewWindow(Rect r)
        {
            var w = new DockWindow { Id = BaseId + nextId++, Rect = r };
            windows.Add(w);
            ClampToScreen(w);
            return w;
        }

        private static DockWindow TopWindowAt(Vector2 m)
        {
            for (int i = windows.Count - 1; i >= 0; i--)
                if (windows[i].Visible().Count > 0 && windows[i].Rect.Contains(m)) return windows[i];
            return null;
        }

        private static DockWindow WindowOf(Panel p)
        {
            foreach (DockWindow w in windows) if (w.Tabs.Contains(p)) return w;
            return null;
        }

        private static void ClampToScreen(DockWindow w)
        {
            w.Rect.x = Mathf.Clamp(w.Rect.x, -w.Rect.width + 60, Screen.width - 60);
            w.Rect.y = Mathf.Clamp(w.Rect.y, 0, Screen.height - HeaderH);
        }

        private static void Fire(string id, bool open)
        {
            if (OpenChanged == null) return;
            try { OpenChanged(id, open); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        // ------------------------------------------------------------------
        //  Zapis ukladu
        // ------------------------------------------------------------------

        private static void Load()
        {
            if (saved != null) return;
            saved = File.Exists(LayoutPath) ? ConfigNode.Load(LayoutPath) : null;
            if (saved == null) saved = new ConfigNode();
        }

        private static bool SavedOpen(string id)
        {
            ConfigNode o = saved.GetNode("OPEN");
            bool b;
            return o != null && bool.TryParse(o.GetValue(id), out b) && b;
        }

        /// <summary>Panel trafia do okna, w ktorym byl przy ostatnim zapisie - razem z sasiadami.</summary>
        private static void PlaceFromSaved(Panel p, Rect def)
        {
            foreach (ConfigNode n in saved.GetNodes("WINDOW"))
            {
                bool mine = false;
                foreach (string t in n.GetValues("tab")) if (t == p.Id) mine = true;
                if (!mine) continue;

                // okno z tego wpisu moglo juz powstac dla wczesniej zarejestrowanego sasiada
                DockWindow w = null;
                foreach (string t in n.GetValues("tab"))
                {
                    Panel q;
                    if (t != p.Id && panels.TryGetValue(t, out q) && (w = WindowOf(q)) != null) break;
                }
                if (w == null)
                {
                    w = NewWindow(ParseRect(n.GetValue("rect"), def));
                    int sp;
                    if (int.TryParse(n.GetValue("split"), out sp) && sp > 0 && sp < Layouts.Length)
                    {
                        w.Split = sp;
                        string zs = n.GetValue("zones");
                        if (!string.IsNullOrEmpty(zs)) w.Zones.AddRange(zs.Split(new[] { char.Parse("|") }));
                    }
                }
                // kolejnosc zakladek jak w zapisie
                int pos = 0;
                foreach (string t in n.GetValues("tab"))
                {
                    if (t == p.Id) break;
                    foreach (Panel q in w.Tabs) if (q.Id == t) pos++;
                }
                w.Tabs.Insert(Mathf.Min(pos, w.Tabs.Count), p);
                if (n.GetValue("active") == p.Id || w.Active == null) w.Active = p;
                return;
            }
            var nw = NewWindow(def);
            nw.Tabs.Add(p);
            nw.Active = p;
        }

        private static void Save()
        {
            if (saved == null) return;
            var root = new ConfigNode();
            var open = root.AddNode("OPEN");
            foreach (Panel p in panels.Values) open.AddValue(p.Id, p.Open);
            // panele, ktore w tej sesji sie nie zarejestrowaly, zachowuja stary stan
            ConfigNode oldOpen = saved.GetNode("OPEN");
            if (oldOpen != null)
                foreach (ConfigNode.Value v in oldOpen.values)
                    if (!panels.ContainsKey(v.name)) open.AddValue(v.name, v.value);

            foreach (DockWindow w in windows)
            {
                if (w.Tabs.Count == 0) continue;
                var n = root.AddNode("WINDOW");
                n.AddValue("rect", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                                 "{0:F0},{1:F0},{2:F0},{3:F0}", w.Rect.x, w.Rect.y, w.Rect.width, w.Rect.height));
                if (w.Active != null) n.AddValue("active", w.Active.Id);
                if (w.Split > 0) { n.AddValue("split", w.Split); n.AddValue("zones", string.Join("|", w.Zones.ToArray())); }
                foreach (Panel p in w.Tabs) n.AddValue("tab", p.Id);
            }
            // okna paneli niezarejestrowanych w tej sesji - bez zmian
            foreach (ConfigNode n in saved.GetNodes("WINDOW"))
            {
                bool known = false;
                foreach (string t in n.GetValues("tab")) if (panels.ContainsKey(t)) known = true;
                if (!known) root.AddNode(n);
            }

            var numNode = root.AddNode("NUM");
            ConfigNode oldNum = saved.GetNode("NUM");
            if (oldNum != null)
                foreach (ConfigNode.Value v in oldNum.values)
                    if (!nums.ContainsKey(v.name)) numNode.AddValue(v.name, v.value);
            foreach (KeyValuePair<string, float> kv in nums)
                numNode.AddValue(kv.Key, kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath));
                root.Save(LayoutPath);
                saved = root;
            }
            catch (Exception ex) { Debug.LogWarning("[DeltaUI] layout: " + ex.Message); }
        }

        private static Rect ParseRect(string s, Rect def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            string[] f = s.Split(',');
            float x, y, w, h;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (f.Length == 4 && float.TryParse(f[0], System.Globalization.NumberStyles.Float, ci, out x) &&
                float.TryParse(f[1], System.Globalization.NumberStyles.Float, ci, out y) &&
                float.TryParse(f[2], System.Globalization.NumberStyles.Float, ci, out w) &&
                float.TryParse(f[3], System.Globalization.NumberStyles.Float, ci, out h))
                return new Rect(x, y, Mathf.Max(180, w), Mathf.Max(90, h));
            return def;
        }
    }
}
