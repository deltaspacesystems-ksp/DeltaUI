using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;

namespace DeltaBrowser
{
    /// <summary>
    /// Wlasny Chromium dla DeltaUI. Renderuje strony do bufora pikseli i oddaje je wtyczce w KSP
    /// przez TCP na localhoscie; od wtyczki przyjmuje mysz, klawiature i nawigacje.
    ///
    /// Protokol: [bajt typu][int32 dlugosc][tresc], liczby little-endian, napisy jak w BinaryWriter.
    /// Kazda wiadomosc dotyczaca strony zaczyna sie od numeru zakladki (int32) - na razie jest jedna,
    /// ale protokol jest gotowy na wiele.
    ///
    ///   wtyczka -> pomocnik:
    ///     'N' tab, url                   nawigacja
    ///     'B' tab, akcja                 0 wstecz, 1 dalej, 2 odswiez, 3 stop
    ///     'R' tab, szer, wys             rozmiar strony w pikselach
    ///     'M' tab, rodzaj, x, y, przycisk, liczbaKlikniec, modyfikatory
    ///                                    rodzaj: 0 ruch, 1 wcisniecie, 2 puszczenie, 3 wyjscie myszy
    ///     'W' tab, x, y, dx, dy, mod     kolko
    ///     'K' tab, rodzaj, vk, mod       rodzaj: 0 klawisz w dol, 1 w gore, 2 znak
    ///     'V' tab, widoczna (bajt)       ukryta strona nie renderuje
    ///     'G' tab, fokus (bajt)
    ///     'Z' tab, poziom (double)       powiekszenie: 1.2^poziom, czyli 0 = 100%
    ///     'A' tab, url, szer, wys        nowa zakladka o tym numerze (wtyczka go nadaje)
    ///     'X' tab                        zamyka zakladke
    ///     'O' wlaczony (bajt)            adblock (globalnie, wszystkie zakladki) - patrz Adblock.cs
    ///     'Q'                            koniec
    ///
    ///   pomocnik -> wtyczka:
    ///     'F' tab, szer, wys, piksele    klatka BGRA, wiersze OD DOLU (jak tekstury Unity)
    ///     'U' tab, adres    'T' tab, tytul    'L' tab, laduje, wstecz, dalej    'S' napis stanu
    /// </summary>
    internal static class Program
    {
        private static readonly object WriteLock = new object();
        private static BinaryWriter writer;
        private static readonly Dictionary<int, Tab> Tabs = new Dictionary<int, Tab>();
        private static readonly AutoResetEvent FrameSignal = new AutoResetEvent(false);
        private static volatile bool quit;

        private sealed class Tab
        {
            public int Id;
            public ChromiumWebBrowser Br;
            public readonly object Lock = new object();
            public byte[] Frame, Send;
            public int W, H;
            public bool Dirty;
            public volatile bool Stream = true;     // false: strona jest niewidoczna w grze - renderuje sie dalej (dzwiek, wideo), ale klatek nie kopiujemy ani nie wysylamy
            public double Zoom;          // poziom powiekszenia CEF; ustawiany od nowa po kazdym wczytaniu strony
            public bool ZoomSet;         // uzytkownik wybral powiekszenie (takze 100%) - wtedy nadpisujemy pamiec CEF per adres
        }

        // liczniki odebranych zdarzen - wtyczka pokazuje je w oknie, zeby bylo widac, gdzie ginie klikniecie
        private static int cDown, cUp, cMove, cKey, cWheel, cPaint, cSent;

        private static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        private static int Main(string[] args)
        {
            string cache = Arg(args, "--cache", Path.Combine(Path.GetTempPath(), "DeltaBrowser"));
            string url = Arg(args, "--url", "https://duckduckgo.com");
            int width = int.Parse(Arg(args, "--w", "1024")), height = int.Parse(Arg(args, "--h", "640"));
            int parent = int.Parse(Arg(args, "--parent", "0"));

            // Wtyczka czeka na numer portu - podajemy go od razu, zanim CEF sie uruchomi.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Console.WriteLine("PORT " + ((IPEndPoint)listener.LocalEndpoint).Port);
            Console.Out.Flush();

            if (parent > 0)
            {
                new Thread(() =>
                {
                    try { Process.GetProcessById(parent).WaitForExit(); } catch { }
                    Environment.Exit(0);
                }) { IsBackground = true }.Start();
            }

            Adblock.Load(AppDomain.CurrentDomain.BaseDirectory);
            Directory.CreateDirectory(cache);
            var settings = new CefSettings { CachePath = cache, LogSeverity = LogSeverity.Disable, PersistSessionCookies = true };
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
            settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            // Gdy GPU jest zajete przez gre (100%), proces GPU Chromium dostaje ochlapy i strona rysuje sie kilkanascie razy na sekunde.
            // Tryb programowy (--gpu 0) renderuje na procesorze, niezaleznie od karty.
            if (Arg(args, "--gpu", "0") == "0")
            {
                settings.CefCommandLineArgs.Add("disable-gpu");
                settings.CefCommandLineArgs.Add("disable-gpu-compositing");
            }
            if (!Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null))
            {
                Console.WriteLine("ERROR cef-init");
                return 2;
            }

            TcpClient client = listener.AcceptTcpClient();
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            writer = new BinaryWriter(stream);
            var reader = new BinaryReader(stream);

            NewTab(1, url, width, height);
            SendText('S', "ready");

            new Thread(FrameLoop) { IsBackground = true, Name = "frames" }.Start();
            new Thread(() =>
            {
                string last = "";
                while (!quit)
                {
                    Thread.Sleep(400);
                    string s = string.Format("down {0}  up {1}  move {2}  wheel {3}  key {4}  paint {5}  sent {6}", cDown, cUp, cMove, cWheel, cKey, cPaint, cSent);
                    if (s != last) { last = s; SendText('D', s); }
                }
            }) { IsBackground = true, Name = "stats" }.Start();

            try
            {
                while (!quit)
                {
                    byte type = reader.ReadByte();
                    int len = reader.ReadInt32();
                    byte[] payload = len > 0 ? reader.ReadBytes(len) : new byte[0];
                    Handle(type, new BinaryReader(new MemoryStream(payload)));
                }
            }
            catch (Exception) { /* wtyczka sie rozlaczyla */ }

            quit = true;
            try { Cef.Shutdown(); } catch { }
            return 0;
        }

        // ------------------------------------------------------------------
        //  Zakladki
        // ------------------------------------------------------------------

        private static void NewTab(int id, string url, int w, int h)
        {
            var bs = new BrowserSettings { WindowlessFrameRate = 60 };
            var br = new ChromiumWebBrowser(url, bs);
            br.LifeSpanHandler = new PopupToSameTab(br);
            br.JsDialogHandler = new AutoJsDialogs();
            br.RequestHandler = new AdblockRequestHandler();
            br.Size = new Size(w, h);
            var tab = new Tab { Id = id, Br = br };
            Tabs[id] = tab;

            br.Paint += (s, e) => OnPaint(tab, e);
            br.AddressChanged += (s, e) => SendTabText('U', id, e.Address);
            br.TitleChanged += (s, e) => SendTabText('T', id, e.Title);
            br.LoadingStateChanged += (s, e) => SendState(id, e.IsLoading, e.CanGoBack, e.CanGoForward);
            br.FrameLoadEnd += (s, e) =>
            {
                // CEF trzyma powiekszenie per adres - po nawigacji wracamy do wybranego przez uzytkownika
                if (e.Frame.IsMain && tab.ZoomSet) { try { e.Browser.GetHost().SetZoomLevel(tab.Zoom); } catch { } }
            };
        }

        private static void OnPaint(Tab tab, OnPaintEventArgs e)
        {
            if (e.IsPopup) return;                       // rozwijane listy - pozniej
            cPaint++;
            if (!tab.Stream) return;
            int w = e.Width, h = e.Height, stride = w * 4;
            lock (tab.Lock)
            {
                if (tab.Frame == null || tab.Frame.Length != stride * h) tab.Frame = new byte[stride * h];
                // Unity liczy wiersze od dolu, CEF od gory - odwracamy tutaj, raz.
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(e.BufferHandle, (h - 1 - y) * stride), tab.Frame, y * stride, stride);
                tab.W = w; tab.H = h; tab.Dirty = true;
            }
            FrameSignal.Set();
        }

        /// <summary>Wysyla najnowsza klatke kazdej zakladki; jak siec nie nadaza, klatki posrednie przepadaja.</summary>
        private static void FrameLoop()
        {
            while (!quit)
            {
                FrameSignal.WaitOne(200);
                foreach (Tab tab in new List<Tab>(Tabs.Values))
                {
                    int w, h;
                    lock (tab.Lock)
                    {
                        if (!tab.Dirty) continue;
                        byte[] swap = tab.Send; tab.Send = tab.Frame; tab.Frame = swap;      // zamiana zamiast kopiowania kilku MB na klatke
                        w = tab.W; h = tab.H; tab.Dirty = false;
                    }
                    try
                    {
                        lock (WriteLock)
                        {
                            writer.Write((byte)'F');
                            writer.Write(12 + tab.Send.Length);
                            writer.Write(tab.Id); writer.Write(w); writer.Write(h);
                            writer.Write(tab.Send, 0, w * h * 4);
                            writer.Flush();
                            cSent++;
                        }
                    }
                    catch (Exception) { quit = true; return; }
                }
            }
        }

        // ------------------------------------------------------------------
        //  Polecenia od wtyczki
        // ------------------------------------------------------------------

        private static void Handle(byte type, BinaryReader r)
        {
            if (type == (byte)'Q') { quit = true; return; }
            int id = r.ReadInt32();

            // Nowa/zamykana zakladka - przed wyszukaniem istniejacej, bo przy 'A' jej jeszcze nie ma.
            if (type == (byte)'A')
            {
                string url = r.ReadString();
                int nw = Math.Max(64, r.ReadInt32()), nh = Math.Max(64, r.ReadInt32());
                if (!Tabs.ContainsKey(id)) NewTab(id, url, nw, nh);
                return;
            }
            if (type == (byte)'X')
            {
                Tab dead;
                if (Tabs.TryGetValue(id, out dead))
                {
                    Tabs.Remove(id);
                    try { dead.Br.Dispose(); } catch { }
                }
                return;
            }
            if (type == (byte)'O')
            {
                Adblock.Enabled = r.ReadByte() != 0;
                return;
            }

            Tab tab;
            if (!Tabs.TryGetValue(id, out tab) || tab.Br.BrowserCore == null) return;
            IBrowser core = tab.Br.BrowserCore;
            IBrowserHost host = core.GetHost();

            switch ((char)type)
            {
                case 'N':
                    tab.Br.Load(Normalize(r.ReadString()));
                    break;
                case 'B':
                    switch (r.ReadInt32())
                    {
                        case 0: core.GoBack(); break;
                        case 1: core.GoForward(); break;
                        case 2: core.Reload(false); break;
                        case 3: core.StopLoad(); break;
                    }
                    break;
                case 'R':
                    {
                        int w = Math.Max(64, r.ReadInt32()), h = Math.Max(64, r.ReadInt32());
                        tab.Br.Size = new Size(w, h);
                        break;
                    }
                case 'M':
                    {
                        int kind = r.ReadInt32(), x = r.ReadInt32(), y = r.ReadInt32(), button = r.ReadInt32(), clicks = r.ReadInt32(), mods = r.ReadInt32();
                        var me = new MouseEvent(x, y, (CefEventFlags)mods);
                        if (kind == 0) cMove++; else if (kind == 1) cDown++; else if (kind == 2) cUp++;
                        if (kind == 0) host.SendMouseMoveEvent(me, false);
                        else if (kind == 3) host.SendMouseMoveEvent(me, true);
                        else host.SendMouseClickEvent(me, (MouseButtonType)button, kind == 2, Math.Max(1, clicks));
                        break;
                    }
                case 'W':
                    {
                        int x = r.ReadInt32(), y = r.ReadInt32(), dx = r.ReadInt32(), dy = r.ReadInt32(), mods = r.ReadInt32();
                        cWheel++;
                        host.SendMouseWheelEvent(new MouseEvent(x, y, (CefEventFlags)mods), dx, dy);
                        break;
                    }
                case 'K':
                    {
                        int kind = r.ReadInt32(), vk = r.ReadInt32(), mods = r.ReadInt32();
                        cKey++;
                        host.SendKeyEvent(new KeyEvent
                        {
                            Type = kind == 0 ? KeyEventType.RawKeyDown : kind == 1 ? KeyEventType.KeyUp : KeyEventType.Char,
                            WindowsKeyCode = vk,
                            Modifiers = (CefEventFlags)mods,
                            FocusOnEditableField = true
                        });
                        break;
                    }
                case 'V':
                    host.WasHidden(r.ReadByte() == 0);
                    break;
                case 'F':
                    tab.Stream = r.ReadByte() != 0;
                    if (tab.Stream) host.Invalidate(PaintElementType.View);
                    break;
                case 'G':
                    host.SetFocus(r.ReadByte() != 0);
                    break;
                case 'Z':
                    tab.Zoom = r.ReadDouble();
                    tab.ZoomSet = true;
                    host.SetZoomLevel(tab.Zoom);
                    break;
            }
        }

        /// <summary>To, co wpisano w pasek adresu: adres, adres bez schematu albo fraza do wyszukiwarki.</summary>
        private static string Normalize(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "about:blank";
            if (s.Contains("://") || s.StartsWith("about:") || s.StartsWith("data:") || s.StartsWith("file:")) return s;
            if (!s.Contains(" ") && (s.Contains(".") || s.StartsWith("localhost")))
                return "https://" + s;
            return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(s);
        }

        // ------------------------------------------------------------------
        //  Wysylanie
        // ------------------------------------------------------------------

        private static void Send(byte type, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            body(w);
            w.Flush();
            try
            {
                lock (WriteLock)
                {
                    writer.Write(type);
                    writer.Write((int)ms.Length);
                    writer.Write(ms.GetBuffer(), 0, (int)ms.Length);
                    writer.Flush();
                }
            }
            catch (Exception) { quit = true; }
        }

        private static void SendText(char type, string text) { Send((byte)type, w => w.Write(text ?? "")); }
        private static void SendTabText(char type, int tab, string text) { Send((byte)type, w => { w.Write(tab); w.Write(text ?? ""); }); }
        private static void SendState(int tab, bool loading, bool back, bool fwd)
        {
            Send((byte)'L', w => { w.Write(tab); w.Write(loading); w.Write(back); w.Write(fwd); });
        }
    }

    /// <summary>Okna otwierane przez strone (target=_blank, window.open) - na razie w tej samej zakladce.</summary>
    internal sealed class PopupToSameTab : CefSharp.Handler.LifeSpanHandler
    {
        private readonly ChromiumWebBrowser browser;
        public PopupToSameTab(ChromiumWebBrowser b) { browser = b; }

        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser b, IFrame frame, string targetUrl,
                                              string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture,
                                              IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
                                              ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null;
            browser.Load(targetUrl);
            return true;
        }
    }

    /// <summary>alert / confirm / prompt: bez okienek w grze przyjmujemy je po cichu, zeby strona sie nie zawiesila.</summary>
    internal sealed class AutoJsDialogs : CefSharp.Handler.JsDialogHandler
    {
        protected override bool OnJSDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType,
                                           string messageText, string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
        {
            suppressMessage = false;
            callback.Continue(true);
            return true;
        }
    }
}
