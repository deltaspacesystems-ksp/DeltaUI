using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KSP.UI.Screens;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Polaczenie z pomocnikiem DeltaBrowser (wlasny Chromium w osobnym procesie), wiele zakladek na jedno
    /// polaczenie TCP. Pomocnik przysyla klatki jako surowe piksele BGRA (kazda oznaczona numerem zakladki),
    /// wtyczka wgrywa je do Texture2D tej zakladki; w druga strone leca mysz, klawiatura i nawigacja -
    /// kazda komenda tez niesie numer zakladki. Protokol opisany w tools/DeltaBrowser/Program.cs.
    ///
    /// Odczyt z sieci chodzi na osobnym watku (czyta prosto do bufora klatki zakladki, bez kopii posrednich),
    /// a Pump() na watku glownym tylko podmienia gotowe bufory i robi LoadRawTextureData.
    /// </summary>
    public sealed class BrowserClient
    {
        public enum State { Stopped, Starting, Running, Failed }

        /// <summary>Stan jednej zakladki. Tlo (nieaktywna, ale otwarta) dalej dostaje dzwiek/JS - tylko nie renderuje.</summary>
        public sealed class TabState
        {
            public int Id;
            public string Url = "", Title = "";
            public bool Loading, CanBack, CanFwd;
            public Texture2D Tex;
            public int FrameCount;
            public int SentW, SentH;                 // ostatni rozmiar wyslany do tej zakladki (Resize)
            public bool VisibleSent, StreamSent;      // ostatnie V/F wyslane do helpera dla tej zakladki

            internal readonly object frameLock = new object();
            internal byte[] rx, ready;
            internal int readyW, readyH;
            internal bool fresh;
        }

        public volatile State Status = State.Stopped;
        public string Error = "";
        public string HelperStats = "";

        public readonly Dictionary<int, TabState> Tabs = new Dictionary<int, TabState>();
        public readonly List<int> Order = new List<int>();     // kolejnosc zakladek jak w pasku
        private int nextId = 1;

        public TabState Get(int id) { TabState t; return Tabs.TryGetValue(id, out t) ? t : null; }

        private Process proc;
        private TcpClient tcp;
        private BinaryWriter writer;
        private readonly object sendLock = new object();
        private bool stopping;

        public static string HelperDir { get { return Path.Combine(KSPUtil.ApplicationRootPath, "DeltaBrowser"); } }

        /// <summary>Startuje pomocnika z jedna zakladka (numer 1 - helper tworzy ja sam na starcie).</summary>
        public void Start(int w, int h, string url)
        {
            if (Status == State.Starting || Status == State.Running) return;
            string exe = Path.Combine(HelperDir, "DeltaBrowser.exe");
            if (!File.Exists(exe)) { Fail("Browser helper not installed: " + exe); return; }

            Status = State.Starting;
            Error = "";
            stopping = false;
            Tabs.Clear();
            Order.Clear();
            nextId = 1;
            Tabs[1] = new TabState { Id = 1, Url = url, SentW = w, SentH = h };
            Order.Add(1);

            var psi = new ProcessStartInfo(exe)
            {
                Arguments = string.Format("--cache \"{0}\" --url \"{1}\" --w {2} --h {3} --parent {4} --gpu {5}",
                                          Path.Combine(HelperDir, "profile"), url, w, h, Process.GetCurrentProcess().Id, Dock.GetNum("browser.gpu", 0f) > 0.5f ? 1 : 0),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = HelperDir
            };
            new Thread(() => Run(psi)) { IsBackground = true, Name = "DeltaBrowser reader" }.Start();
        }

        /// <summary>Nowa zakladka; helper ja tworzy przy najblizszym poleceniu 'A'. Zwraca jej numer.</summary>
        public int NewTab(string url, int w, int h)
        {
            int id = ++nextId;
            Tabs[id] = new TabState { Id = id, Url = url, SentW = w, SentH = h };
            Order.Add(id);
            if (Live) Send((byte)'A', wr => { wr.Write(id); wr.Write(url ?? ""); wr.Write(Mathf.Max(64, w)); wr.Write(Mathf.Max(64, h)); });
            return id;
        }

        /// <summary>Zamyka zakladke. Zostaje przynajmniej jedna - wywolujacy sam decyduje, ktora aktywowac dalej.</summary>
        public void CloseTab(int id)
        {
            TabState t;
            if (!Tabs.TryGetValue(id, out t)) return;
            Tabs.Remove(id);
            Order.Remove(id);
            if (t.Tex != null) UnityEngine.Object.Destroy(t.Tex);
            if (Live) Send((byte)'X', wr => wr.Write(id));
        }

        private void Fail(string msg)
        {
            if (stopping) return;
            Error = msg;
            Status = State.Failed;
            UnityEngine.Debug.LogWarning("[DeltaUI] browser: " + msg);
        }

        private void Run(ProcessStartInfo psi)
        {
            try
            {
                proc = Process.Start(psi);
                string line = proc.StandardOutput.ReadLine();          // "PORT n" - podawane, zanim CEF sie uruchomi
                if (line == null || !line.StartsWith("PORT ")) { Fail("helper did not report a port: " + line); return; }
                int port = int.Parse(line.Substring(5));

                tcp = new TcpClient { NoDelay = true };
                tcp.Connect(IPAddress.Loopback, port);
                Stream s = tcp.GetStream();
                writer = new BinaryWriter(s);
                var reader = new BinaryReader(s);

                while (true)
                {
                    byte type = reader.ReadByte();
                    int len = reader.ReadInt32();
                    if (type == (byte)'F') ReadFrame(s, reader, len);
                    else Message((char)type, reader.ReadBytes(len));
                }
            }
            catch (Exception e)
            {
                if (!stopping) Fail(e is EndOfStreamException || e is IOException ? "helper closed the connection" : e.Message);
            }
        }

        private void ReadFrame(Stream s, BinaryReader r, int len)
        {
            int id = r.ReadInt32();
            int w = r.ReadInt32(), h = r.ReadInt32(), n = w * h * 4;
            TabState t = Get(id);
            if (t == null)
            {
                // zakladka zamknieta w miedzyczasie po naszej stronie - wyciagnij bajty i odrzuc
                int skip = n; var junk = new byte[8192];
                while (skip > 0) { int k = s.Read(junk, 0, Math.Min(junk.Length, skip)); if (k <= 0) throw new EndOfStreamException(); skip -= k; }
                return;
            }
            if (t.rx == null || t.rx.Length != n) t.rx = new byte[n];
            int got = 0;
            while (got < n)
            {
                int k = s.Read(t.rx, got, n - got);
                if (k <= 0) throw new EndOfStreamException();
                got += k;
            }
            lock (t.frameLock)
            {
                byte[] tmp = t.ready; t.ready = t.rx; t.rx = tmp;     // gotowy bufor idzie do wglądu, stary wraca do czytania
                t.readyW = w; t.readyH = h; t.fresh = true;
            }
        }

        private void Message(char type, byte[] payload)
        {
            var r = new BinaryReader(new MemoryStream(payload));
            if (type == 'S') { if (r.ReadString() == "ready") Status = State.Running; return; }
            if (type == 'D') { HelperStats = r.ReadString(); return; }

            int id = r.ReadInt32();
            TabState t = Get(id);
            switch (type)
            {
                case 'U': { string v = r.ReadString(); if (t != null) t.Url = v; break; }
                case 'T': { string v = r.ReadString(); if (t != null) t.Title = v; break; }
                case 'L':
                    {
                        bool loading = r.ReadBoolean(), back = r.ReadBoolean(), fwd = r.ReadBoolean();
                        if (t != null) { t.Loading = loading; t.CanBack = back; t.CanFwd = fwd; }
                        break;
                    }
            }
        }

        /// <summary>Na watku glownym: nowe klatki wszystkich zakladek do ich tekstur.</summary>
        public void Pump()
        {
            foreach (TabState t in Tabs.Values)
            {
                lock (t.frameLock)
                {
                    if (!t.fresh) continue;
                    t.fresh = false;
                    if (t.Tex == null || t.Tex.width != t.readyW || t.Tex.height != t.readyH)
                    {
                        if (t.Tex != null) UnityEngine.Object.Destroy(t.Tex);
                        t.Tex = new Texture2D(t.readyW, t.readyH, TextureFormat.BGRA32, false)
                        { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                    }
                    t.Tex.LoadRawTextureData(t.ready);
                    t.Tex.Apply(false);
                    t.FrameCount++;
                }
            }
        }

        public void Stop()
        {
            stopping = true;
            try { Send((byte)'Q', w => { }); } catch { }
            try { if (tcp != null) tcp.Close(); } catch { }
            try { if (proc != null && !proc.HasExited && !proc.WaitForExit(1500)) proc.Kill(); } catch { }
            Status = State.Stopped;
            foreach (TabState t in Tabs.Values) if (t.Tex != null) UnityEngine.Object.Destroy(t.Tex);
            Tabs.Clear();
            Order.Clear();
        }

        // ------------------------------------------------------------------
        //  Polecenia (kazde niesie numer zakladki)
        // ------------------------------------------------------------------

        private void Send(byte type, Action<BinaryWriter> body)
        {
            if (writer == null) return;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            body(w);
            w.Flush();
            lock (sendLock)
            {
                try
                {
                    writer.Write(type);
                    writer.Write((int)ms.Length);
                    writer.Write(ms.GetBuffer(), 0, (int)ms.Length);
                    writer.Flush();
                }
                catch (Exception e) { Fail(e.Message); }
            }
        }

        public bool Live { get { return Status == State.Running; } }

        public void Navigate(int tab, string url) { if (Live) Send((byte)'N', w => { w.Write(tab); w.Write(url ?? ""); }); }
        public void Nav(int tab, int action) { if (Live) Send((byte)'B', w => { w.Write(tab); w.Write(action); }); }
        public void Resize(int tab, int wpx, int hpx) { if (Live) Send((byte)'R', w => { w.Write(tab); w.Write(wpx); w.Write(hpx); }); }
        public void SetVisible(int tab, bool v) { if (Live) Send((byte)'V', w => { w.Write(tab); w.Write(v); }); }
        public void SetStream(int tab, bool v) { if (Live) Send((byte)'F', w => { w.Write(tab); w.Write(v); }); }
        public void SetFocus(int tab, bool f) { if (Live) Send((byte)'G', w => { w.Write(tab); w.Write(f); }); }

        /// <summary>Adblock jest globalny (wszystkie zakladki), ale protokol niesie numer zakladki jak kazda
        /// komenda - helper go po prostu ignoruje dla 'O'.</summary>
        public void SetAdblock(bool on) { if (Live) Send((byte)'O', w => { w.Write(0); w.Write(on); }); }

        /// <summary>Powiekszenie strony w procentach (100 = normalnie). CEF liczy 1.2^poziom.</summary>
        public void Zoom(int tab, int percent)
        {
            double level = Math.Log(Math.Max(10, percent) / 100.0) / Math.Log(1.2);
            if (Live) Send((byte)'Z', w => { w.Write(tab); w.Write(level); });
        }

        /// <summary>kind: 0 ruch, 1 wcisniecie, 2 puszczenie, 3 wyjscie myszy.</summary>
        public void Mouse(int tab, int kind, int x, int y, int button, int clicks, int mods)
        {
            if (Live) Send((byte)'M', w => { w.Write(tab); w.Write(kind); w.Write(x); w.Write(y); w.Write(button); w.Write(clicks); w.Write(mods); });
        }

        public void Wheel(int tab, int x, int y, int dx, int dy, int mods)
        {
            if (Live) Send((byte)'W', w => { w.Write(tab); w.Write(x); w.Write(y); w.Write(dx); w.Write(dy); w.Write(mods); });
        }

        /// <summary>kind: 0 klawisz w dol, 1 w gore, 2 znak.</summary>
        public void Key(int tab, int kind, int vk, int mods)
        {
            if (Live) Send((byte)'K', w => { w.Write(tab); w.Write(kind); w.Write(vk); w.Write(mods); });
        }
    }

    /// <summary>Okno "Browser": pasek zakladek, pasek adresu, przyciski nawigacji i strona jako tekstura.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class BrowserAddon : MonoBehaviour
    {
        private const string PanelId = "deltaui.browser";
        private const string LockId = "DeltaUI_Browser";
        private const string StartUrl = "https://duckduckgo.com";

        // CefEventFlags
        private const int Caps = 1, Shift = 2, Ctrl = 4, Alt = 8, LeftBtn = 16, MidBtn = 32, RightBtn = 64;

        private readonly BrowserClient client = new BrowserClient();
        private LauncherButton button;
        private string urlText = "";
        private BrowserClient.State drawState;
        private int gDown, gUp, gDrag, gWheel, gKey;          // zdarzenia, ktore doszly do okna (diagnostyka)
        private string lastClick = "-";
        private bool pageFocus, urlFocus, locked;
        private Rect pageScreen;                        // pozycja strony na ekranie (do ruchu myszy bez klikniecia)
        private float lastDraw = -10f, sizeSince;
        private int wantW, wantH;
        private Vector2 lastHover = new Vector2(-1, -1);
        private bool hoverInside, wasVisible = true;
        private int activeTab = 1;

        // Powiekszenie strony (100% = 1 piksel CSS na piksel ekranu). Kroki jak w Chrome.
        private static readonly int[] ZoomSteps = { 25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500 };
        private int zoomPct = 100;
        private bool zoomApplied;
        private bool adblockSent = true;   // domyslnie wlaczony (patrz Adblock.cs w helperze) - wysylamy tylko przy zmianie

        private void SetZoom(int pct)
        {
            zoomPct = Mathf.Clamp(pct, 25, 500);
            Dock.SetNum("browser.zoom", zoomPct);
            client.Zoom(activeTab, zoomPct);
        }

        private void StepZoom(int dir)
        {
            int i = 0;
            for (int k = 0; k < ZoomSteps.Length; k++) if (ZoomSteps[k] <= zoomPct) i = k;
            SetZoom(ZoomSteps[Mathf.Clamp(i + dir, 0, ZoomSteps.Length - 1)]);
        }

        private void Awake() { DontDestroyOnLoad(gameObject); }

        private void Start()
        {
            zoomPct = (int)Dock.GetNum("browser.zoom", 100f);
            Dock.Register(PanelId, "Browser", Draw, new Rect(140, 100, 900, 640), false, GlobeIcon, 10);
            Dock.OpenChanged += OnOpenChanged;
            button = new LauncherButton(GlobeIcon, () => Dock.SetOpen(PanelId, true), () => Dock.SetOpen(PanelId, false),
                ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW |
                ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.TRACKSTATION);
            button.Sync(Dock.IsOpen(PanelId));
        }

        private void OnDestroy()
        {
            Dock.OpenChanged -= OnOpenChanged;
            if (button != null) button.Destroy();
            SetLock(false);
            client.Stop();
        }

        private void OnOpenChanged(string id, bool open)
        {
            if (id == PanelId && button != null) button.Sync(open);
        }

        // ------------------------------------------------------------------
        //  Zakladki
        // ------------------------------------------------------------------

        private void ActivateTab(int id)
        {
            if (!client.Tabs.ContainsKey(id) || id == activeTab) return;
            activeTab = id;
            zoomApplied = false;                            // odswiezy powiekszenie tej zakladki
            lastHover = new Vector2(-1, -1);
            hoverInside = false;
            BrowserClient.TabState t = client.Get(activeTab);
            if (t != null && (t.SentW != wantW || t.SentH != wantH) && wantW > 0)
            {
                t.SentW = wantW; t.SentH = wantH;
                client.Resize(activeTab, wantW, wantH);
            }
        }

        private void OpenNewTab()
        {
            int w = wantW > 0 ? wantW : 900, h = wantH > 0 ? wantH : 560;
            int id = client.NewTab(StartUrl, w, h);
            ActivateTab(id);
        }

        private void CloseTabUI(int id)
        {
            bool wasActive = id == activeTab;
            int idxHint = client.Order.IndexOf(id);
            client.CloseTab(id);
            if (client.Order.Count == 0) { OpenNewTab(); return; }
            if (wasActive)
            {
                int idx = Mathf.Clamp(idxHint, 0, client.Order.Count - 1);
                ActivateTab(client.Order[idx]);
            }
        }

        // ------------------------------------------------------------------
        //  Klatka po klatce
        // ------------------------------------------------------------------

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.B) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKey(KeyCode.LeftControl))
            {
                Dock.Toggle(PanelId);
                button.Sync(Dock.IsOpen(PanelId));
            }

            bool winFocusRaw = Dock.IsFocused(PanelId) && Dock.IsOpen(PanelId);
            if (winFocusRaw && Input.GetKeyDown(KeyCode.T) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) OpenNewTab();
            if (winFocusRaw && Input.GetKeyDown(KeyCode.W) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) CloseTabUI(activeTab);

            // zapamietane powiekszenie idzie do aktywnej zakladki, gdy tylko wstanie (i po przelaczeniu zakladki)
            if (client.Status == BrowserClient.State.Running)
            {
                if (!zoomApplied) { zoomApplied = true; client.Zoom(activeTab, zoomPct); }
                bool wantAdblock = Dock.GetNum("browser.adblock", 1f) > 0.5f;
                if (wantAdblock != adblockSent) { adblockSent = wantAdblock; client.SetAdblock(wantAdblock); }
            }
            else zoomApplied = false;

            bool visible = Time.unscaledTime - lastDraw < 0.3f && Dock.IsOpen(PanelId);
            bool alive = Dock.IsOpen(PanelId);

            if (client.Status == BrowserClient.State.Running)
            {
                // Aktywna zakladka: widoczna i strumieniuje klatki tylko gdy okno faktycznie sie rysuje.
                // Pozostale otwarte zakladki: strona zyje (dzwiek, JS), ale renderowanie pauzujemy (WasHidden) -
                // tak jak przegladarki chowaja karty w tle.
                foreach (BrowserClient.TabState t in client.Tabs.Values)
                {
                    bool isActive = t.Id == activeTab;
                    bool tVisible = isActive ? alive : false;
                    bool tStream = isActive ? visible : false;
                    if (t.VisibleSent != tVisible) { t.VisibleSent = tVisible; client.SetVisible(t.Id, tVisible); }
                    if (t.StreamSent != tStream) { t.StreamSent = tStream; client.SetStream(t.Id, tStream); }
                }
            }
            if (visible) client.Pump();

            // Fokus okna przegladarki = fokus strony: klawiatura i kolko ida do niej, dopoki nie klikniemy w pole adresu
            bool winFocus = visible && Dock.IsFocused(PanelId);
            if (winFocus != wasWinFocus)
            {
                wasWinFocus = winFocus;
                if (winFocus && !urlFocus && !pageFocus) { pageFocus = true; client.SetFocus(activeTab, true); }
                else if (!winFocus && pageFocus) { pageFocus = false; client.SetFocus(activeTab, false); }
            }

            HandleMouse(visible);
            HandleKeys(visible);
            SetLock(visible && (pageFocus || urlFocus));
        }

        private static KeyCode[] vkKeys;
        private bool wasWinFocus;

        /// <summary>
        /// Klawiatura -> strona z Input (jak mysz): zdarzenia klawiszy IMGUI nie docieraly do okna, gdy focus
        /// przejmowal Dock. Klawisze specjalne z GetKeyDown/Up, znaki z Input.inputString.
        /// </summary>
        private void HandleKeys(bool visible)
        {
            if (!visible || !pageFocus || urlFocus || client.Status != BrowserClient.State.Running) return;
            if (vkKeys == null)
            {
                var l = new System.Collections.Generic.List<KeyCode>();
                foreach (KeyCode k in System.Enum.GetValues(typeof(KeyCode))) if (Vk(k) != 0) l.Add(k);
                vkKeys = l.ToArray();
            }
            int mods = KeyMods();
            foreach (KeyCode k in vkKeys)
            {
                if (Input.GetKeyDown(k)) { client.Key(activeTab, 0, Vk(k), mods); gKey++; }
                if (Input.GetKeyUp(k)) client.Key(activeTab, 1, Vk(k), mods);
            }
            if ((mods & Ctrl) != 0) return;
            foreach (char c in Input.inputString)
            {
                if (c == (char)8) continue;
                if (c == (char)10 || c == (char)13) client.Key(activeTab, 2, 13, mods);
                else if (c == (char)9) client.Key(activeTab, 2, 9, mods);
                else if (c >= ' ' && c != 127) client.Key(activeTab, 2, c, mods);
            }
        }

        private readonly bool[] captured = new bool[3];
        private float lastClickT;
        private Vector2 lastClickP, lastSent = new Vector2(-1, -1);
        private int clickCount;
        private bool releaseUrl;

        private static int KeyMods()
        {
            int m = 0;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= Alt;
            return m;
        }

        /// <summary>
        /// Mysz nad strona: klik, przeciaganie, kolko i ruch - z Input, a nie ze zdarzen IMGUI. Wciskanie dochodzilo
        /// (65 razy), ale puszczenie ginelo (3 razy), wiec klik nigdy sie nie konczyl. Input dziala tak samo
        /// jak przy przeciaganiu samych okien. Okno musi byc na wierzchu w miejscu kursora (Dock.IsTopAt), a w trakcie
        /// przeciagania strona dostaje ruch takze poza swoim prostokatem.
        /// </summary>
        private void HandleMouse(bool visible)
        {
            BrowserClient.TabState tab = client.Get(activeTab);
            if (!visible || client.Status != BrowserClient.State.Running || tab == null || tab.Tex == null) return;
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = pageScreen.Contains(mouse) && Dock.IsTopAt(PanelId, mouse) && !Dock.IsDragging;
            Vector2 p = ToPage(mouse - pageScreen.position, tab);
            int mods = KeyMods();

            if (Input.GetMouseButtonDown(0) && !over && pageFocus && !Dock.IsFocused(PanelId)) { pageFocus = false; client.SetFocus(activeTab, false); }

            int held = 0;
            for (int b = 0; b < 3; b++)
            {
                int flag = b == 0 ? LeftBtn : b == 1 ? RightBtn : MidBtn;
                if (Input.GetMouseButtonDown(b) && over)
                {
                    captured[b] = true;
                    if (b == 0) { pageFocus = true; releaseUrl = true; client.SetFocus(activeTab, true); }
                    float now = Time.unscaledTime;
                    clickCount = now - lastClickT < 0.35f && (mouse - lastClickP).magnitude < 5f ? clickCount + 1 : 1;
                    lastClickT = now;
                    lastClickP = mouse;
                    client.Mouse(activeTab, 1, (int)p.x, (int)p.y, CefButton(b), clickCount, mods | flag);
                    gDown++;
                    lastClick = string.Format("({0:F0},{1:F0}) -> page ({2:F0},{3:F0}) of {4}x{5}", mouse.x - pageScreen.x, mouse.y - pageScreen.y,
                                              p.x, p.y, tab.Tex.width, tab.Tex.height);
                    Dock.Dbg("Browser down  " + lastClick);
                }
                if (captured[b])
                {
                    if (Input.GetMouseButtonUp(b))
                    {
                        captured[b] = false;
                        client.Mouse(activeTab, 2, (int)p.x, (int)p.y, CefButton(b), clickCount, mods);
                        gUp++;
                        Dock.Dbg("Browser up");
                    }
                    else held |= flag;
                }
            }

            if (held != 0)
            {
                if (p != lastSent) { client.Mouse(activeTab, 0, (int)p.x, (int)p.y, 0, 0, mods | held); lastSent = p; gDrag++; }
                hoverInside = true;
            }
            else if (over)
            {
                if (p != lastHover || !hoverInside) { client.Mouse(activeTab, 0, (int)p.x, (int)p.y, 0, 0, mods); lastHover = p; }
                hoverInside = true;
                float dy = Input.mouseScrollDelta.y;
                if (dy != 0f)
                {
                    if ((mods & Ctrl) != 0) StepZoom(dy > 0f ? 1 : -1);       // Ctrl + kolko = powiekszenie, jak w kazdej przegladarce
                    else client.Wheel(activeTab, (int)p.x, (int)p.y, 0, (int)(dy * 40f), mods);
                    gWheel++;
                }
            }
            else if (hoverInside)
            {
                client.Mouse(activeTab, 3, 0, 0, 0, 0, 0);
                hoverInside = false;
            }
        }
        private void SetLock(bool on)
        {
            if (on == locked) return;
            locked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        /// <summary>Wspolrzedne w oknie -> piksele strony (tekstura moze na chwile miec inny rozmiar niz okno).</summary>
        private Vector2 ToPage(Vector2 local, BrowserClient.TabState tab)
        {
            if (tab.Tex == null || pageScreen.width < 1) return local;
            return new Vector2(Mathf.Clamp(local.x * tab.Tex.width / pageScreen.width, 0, tab.Tex.width - 1),
                               Mathf.Clamp(local.y * tab.Tex.height / pageScreen.height, 0, tab.Tex.height - 1));
        }

        // ------------------------------------------------------------------
        //  Okno
        // ------------------------------------------------------------------

        private const float TabH = 26f, TabW = 160f, TabGap = 2f;

        private void Draw()
        {
            lastDraw = Time.unscaledTime;
            Event e = Event.current;
            if (releaseUrl) { releaseUrl = false; GUI.FocusControl(""); }            // klik w strone odbiera fokus polu adresu

            // --- pasek zakladek ---
            GUILayout.BeginHorizontal(GUILayout.Height(TabH));
            for (int i = 0; i < client.Order.Count; i++)
            {
                int id = client.Order[i];
                BrowserClient.TabState t = client.Get(id);
                if (t == null) continue;
                bool on = id == activeTab;
                string title = string.IsNullOrEmpty(t.Title) ? (string.IsNullOrEmpty(t.Url) ? "New tab" : t.Url) : t.Title;
                if (title.Length > 20) title = title.Substring(0, 19) + "…";

                GUILayout.BeginHorizontal(on ? Theme.Skin.box : GUIStyle.none, GUILayout.Width(TabW), GUILayout.Height(TabH));
                if (GUILayout.Button((t.Loading ? "… " : "") + title, Theme.Skin.label, GUILayout.ExpandWidth(true), GUILayout.Height(TabH)))
                    ActivateTab(id);
                if (GUILayout.Button("x", GUILayout.Width(20), GUILayout.Height(TabH))) CloseTabUI(id);
                GUILayout.EndHorizontal();
                GUILayout.Space(TabGap);
            }
            if (GUILayout.Button("+", GUILayout.Width(26), GUILayout.Height(TabH))) OpenNewTab();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            BrowserClient.TabState active = client.Get(activeTab);
            if (active == null) { GUILayout.Label("No tab."); return; }

            // --- pasek narzedzi ---
            // Stan raz na klatke (na zdarzeniu Layout): watek sieciowy moze go zmienic miedzy Layout a Repaint,
            // a rozne galezie GUILayout w tych dwoch przebiegach psuja uklad okna.
            if (e.type == EventType.Layout) drawState = client.Status;
            GUILayout.BeginHorizontal();
            bool live = drawState == BrowserClient.State.Running;
            GUI.enabled = live && active.CanBack;
            if (GUILayout.Button("<", GUILayout.Width(28))) client.Nav(activeTab, 0);
            GUI.enabled = live && active.CanFwd;
            if (GUILayout.Button(">", GUILayout.Width(28))) client.Nav(activeTab, 1);
            GUI.enabled = live;
            if (GUILayout.Button(active.Loading ? "x" : "R", GUILayout.Width(28))) client.Nav(activeTab, active.Loading ? 3 : 2);

            urlFocus = GUI.GetNameOfFocusedControl() == "dbUrl";
            if (!urlFocus) urlText = active.Url;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && urlFocus;
            GUI.SetNextControlName("dbUrl");
            urlText = GUILayout.TextField(urlText, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("-", GUILayout.Width(24))) StepZoom(-1);
            if (GUILayout.Button(zoomPct + "%", GUILayout.Width(52))) SetZoom(100);      // klik w procent = powrot do 100%
            if (GUILayout.Button("+", GUILayout.Width(24))) StepZoom(1);
            if (GUILayout.Button("Go", GUILayout.Width(40)) || enter)
            {
                client.Navigate(activeTab, urlText);
                if (enter) e.Use();
                GUI.FocusControl("");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // --- strona ---
            if (drawState == BrowserClient.State.Failed)
            {
                GUILayout.Label("Browser failed:\n" + client.Error);
                if (GUILayout.Button("Retry", GUILayout.Width(80))) client.Stop();
                return;
            }

            Rect r = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            int id2 = GUIUtility.GetControlID(FocusType.Passive);
            // Prawdziwy prostokat daje GetRect dopiero poza przebiegiem Layout (tam zwraca atrape 1x1) -
            // dlatego rozmiar i pozycje liczymy wylacznie w Repaint.
            if (e.type == EventType.Repaint)
            {
                Vector2 o = Dock.ContentOrigin(PanelId);                        // pozycja z okna Docka, a nie z GUIToScreenPoint
                pageScreen = new Rect(o.x + r.x, o.y + r.y, r.width, r.height);
                TrackSize(r, active);
                if (client.Status == BrowserClient.State.Stopped)
                    client.Start(Mathf.Max(320, (int)r.width), Mathf.Max(200, (int)r.height), StartUrl);
            }

            if (e.type == EventType.Repaint)
            {
                Theme.Fill(r, Theme.Hex(0x101010));
                if (active.Tex != null && live) GUI.DrawTexture(r, active.Tex, ScaleMode.StretchToFill, false);
                else
                {
                    var st = new GUIStyle(Theme.Skin.label) { alignment = TextAnchor.MiddleCenter };
                    GUI.Label(r, drawState == BrowserClient.State.Starting ? "Starting browser ..." : "Browser stopped", st);
                }
                if (pageFocus) Frame(r, Theme.Accent);
            }
            else if (live && active.Tex != null) PageInput(e);

            // --- stan ---
            GUILayout.Label((active.Loading ? "loading ... " : "") + active.Title);
            // Diagnostyka wejscia: "window" = zdarzenia, ktore dotarly do okna w grze, "helper" = to, co odebral Chromium.
            // Jesli window rosnie, a helper nie - ginie w drodze; jesli oba rosna, a strona nie reaguje - to strona.
            var dbg = new GUIStyle(Theme.Skin.label) { wordWrap = false };
            dbg.normal.textColor = Theme.TextDim;
            GUILayout.Label(string.Format("input  window: down {0} up {1} drag {2} wheel {3} key {4}  last {5}   helper: {6}   tabs: {7}",
                                          gDown, gUp, gDrag, gWheel, gKey, lastClick, client.HelperStats, client.Order.Count), dbg);
        }

        /// <summary>IMGUI: 0 lewy, 1 prawy, 2 srodkowy. CEF (MouseButtonType): 0 lewy, 1 srodkowy, 2 prawy.</summary>
        private static int CefButton(int b) { return b == 1 ? 2 : b == 2 ? 1 : 0; }

        private static void Frame(Rect r, Color c)
        {
            Theme.Fill(new Rect(r.x, r.y, r.width, 1), c);
            Theme.Fill(new Rect(r.x, r.yMax - 1, r.width, 1), c);
            Theme.Fill(new Rect(r.x, r.y, 1, r.height), c);
            Theme.Fill(new Rect(r.xMax - 1, r.y, 1, r.height), c);
        }

        /// <summary>Rozmiar strony = rozmiar okna, ale dopiero gdy przestanie sie zmieniac - zeby przy ciagnieciu za rog nie zasypac CEF.</summary>
        private void TrackSize(Rect r, BrowserClient.TabState active)
        {
            // limit: najwiekszy fizyczny monitor (pelny ekran) albo rozmiar okna gry (tryb okienkowy) - bez stalej liczby
            Vector2Int max = Monitors.MaxContent;
            int w = Mathf.Clamp((int)r.width, 200, Mathf.Max(200, max.x)), h = Mathf.Clamp((int)r.height, 150, Mathf.Max(150, max.y));
            if (w != wantW || h != wantH) { wantW = w; wantH = h; sizeSince = Time.unscaledTime; }
            if ((wantW != active.SentW || wantH != active.SentH) && Time.unscaledTime - sizeSince > 0.35f && client.Status == BrowserClient.State.Running)
            {
                active.SentW = wantW; active.SentH = wantH;
                client.Resize(activeTab, wantW, wantH);
            }
        }

        // ------------------------------------------------------------------
        //  Mysz i klawiatura -> strona
        // ------------------------------------------------------------------

        /// <summary>Tylko klawiatura; mysz obsluguje HandleMouse (Input).</summary>
        private void PageInput(Event e)
        {
            if ((e.type == EventType.KeyDown || e.type == EventType.KeyUp) && pageFocus && !urlFocus)
                e.Use();                                                        // znaki idą z HandleKeys (Input)
        }

        /// <summary>KeyCode Unity -> kod klawisza Windows (VK), jak w KeyEvent.WindowsKeyCode.</summary>
        private static int Vk(KeyCode k)
        {
            if (k >= KeyCode.A && k <= KeyCode.Z) return 'A' + (k - KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return '0' + (k - KeyCode.Alpha0);
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return 0x60 + (k - KeyCode.Keypad0);
            if (k >= KeyCode.F1 && k <= KeyCode.F12) return 0x70 + (k - KeyCode.F1);
            switch (k)
            {
                case KeyCode.Backspace: return 0x08;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Return: case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Space: return 0x20;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.End: return 0x23;
                case KeyCode.Home: return 0x24;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Semicolon: return 0xBA;
                case KeyCode.Equals: return 0xBB;
                case KeyCode.Comma: return 0xBC;
                case KeyCode.Minus: return 0xBD;
                case KeyCode.Period: return 0xBE;
                case KeyCode.Slash: return 0xBF;
                case KeyCode.BackQuote: return 0xC0;
                case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.Backslash: return 0xDC;
                case KeyCode.RightBracket: return 0xDD;
                case KeyCode.Quote: return 0xDE;
                default: return 0;                 // sam Shift / Ctrl / Alt: idzie w polu modyfikatorow
            }
        }

        private static Texture2D GlobeIcon()
        {
            const int S = 38;
            var px = new Color32[S * S];
            const float c = S / 2f, R = 15f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > R) continue;
                    bool ring = d > R - 1.8f, eq = Mathf.Abs(dy) < 0.8f, mer = Mathf.Abs(dx) < 0.8f;
                    float ex = dx / Mathf.Sqrt(Mathf.Max(0.01f, 1f - dy * dy / (R * R)));
                    bool side = Mathf.Abs(Mathf.Abs(ex) - 8f) < 1f;
                    byte a = (byte)((ring || eq || mer || side) ? 255 : 40);
                    px[y * S + x] = new Color32(255, 255, 255, a);
                }
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
