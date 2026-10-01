using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CefSharp;

namespace DeltaBrowser
{
    /// <summary>
    /// Prosty adblock na poziomie zadan sieciowych: domena zadania (i kazda jej subdomena) na liscie ->
    /// zadanie anulowane, zanim cokolwiek pobierze. To poziom hosts-file blockera (jak Pi-hole), nie pelny
    /// silnik regul uBlock (bez blokowania kosmetycznego CSS ani reguł per-sciezka) - ale usuwa wiekszosc
    /// reklam, trackerow i popupow bez utrzymywania cudzej listy filtrow w kodzie.
    /// </summary>
    internal static class Adblock
    {
        public static volatile bool Enabled = true;
        private static HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Load(string baseDir)
        {
            try
            {
                string path = Path.Combine(baseDir, "adblock-hosts.txt");
                if (!File.Exists(path)) return;
                hosts = new HashSet<string>(
                    File.ReadAllLines(path)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0 && l[0] != '#'),
                    StringComparer.OrdinalIgnoreCase);
                Console.WriteLine("ADBLOCK " + hosts.Count + " domains");
            }
            catch (Exception e) { Console.WriteLine("ADBLOCK load failed: " + e.Message); }
        }

        /// <summary>Domena albo dowolna jej subdomena jest na liscie.</summary>
        public static bool IsBlocked(string url)
        {
            if (!Enabled || hosts.Count == 0) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            string host = u.Host;
            if (hosts.Contains(host)) return true;
            int dot = host.IndexOf('.');
            while (dot >= 0)
            {
                host = host.Substring(dot + 1);
                if (host.Length == 0) break;
                if (hosts.Contains(host)) return true;
                dot = host.IndexOf('.');
            }
            return false;
        }
    }

    /// <summary>Przypiety do kazdej zakladki - przed kazdym pobraniem sprawdza domene na liscie.</summary>
    internal sealed class AdblockRequestHandler : CefSharp.Handler.RequestHandler
    {
        private static readonly AdblockResourceHandler shared = new AdblockResourceHandler();

        protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
                                                                              IRequest request, bool isNavigation, bool isDownload,
                                                                              string requestInitiator, ref bool disableDefaultHandling)
        {
            return shared;
        }
    }

    internal sealed class AdblockResourceHandler : CefSharp.Handler.ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
                                                                IRequest request, IRequestCallback callback)
        {
            // Nigdy nie blokuj gownej strony (nawigacji ramki) - tylko zasoby, ktore ona doladowuje.
            if (!request.ResourceType.Equals(ResourceType.MainFrame) && Adblock.IsBlocked(request.Url))
                return CefReturnValue.Cancel;
            return CefReturnValue.Continue;
        }
    }
}
