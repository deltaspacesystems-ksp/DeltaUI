using KSP.UI.Screens;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Hub ΔUI: przycisk na pasku aplikacji otwiera puste okno z ikonami skrotow w pasku tytulu (po prawej).
    /// Ikony rysuje Dock (dla kazdego okna, ktore ma w sobie ten panel), a lista skrotow to po prostu
    /// wszystkie panele zarejestrowane przez wszystkie mody - nowy mod z oknem pojawi sie tam sam.
    /// Klik w skrot otwiera okno jako zakladke tego okna; klik w otwarte przynosi je na wierzch.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class HubAddon : MonoBehaviour
    {
        private LauncherButton button;

        private void Awake() { DontDestroyOnLoad(gameObject); }

        private void Start()
        {
            Dock.Register(Dock.HubId, "ΔUI", Draw, new Rect(220, 140, 620, 420), false);
            Dock.OpenChanged += OnOpenChanged;
            button = new LauncherButton(Icon, () => { Dock.SetOpen(Dock.HubId, true); Dock.RestoreMinimized(); }, () => Dock.SetOpen(Dock.HubId, false),
                ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW |
                ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.TRACKSTATION);
            button.Sync(Dock.IsOpen(Dock.HubId));
        }

        private void OnDestroy()
        {
            Dock.OpenChanged -= OnOpenChanged;
            if (button != null) button.Destroy();
        }

        private void OnOpenChanged(string id, bool open)
        {
            if (id == Dock.HubId && button != null) button.Sync(open);
        }

        private void Draw()
        {
            // puste okno - skroty sa w pasku tytulu, a wybrane okno otwiera sie tu jako zakladka
        }

        /// <summary>Ikona 38x38: litera delta i napis UI, biale na przezroczystym tle jak ikony stockowe.</summary>
        private static Texture2D Icon()
        {
            const int S = 38;
            var px = new Color32[S * S];

            // odcinek o grubosci ~2 px (odleglosc punktu od odcinka)
            System.Action<float, float, float, float> line = (ax, ay, bx, by) =>
            {
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float vx = bx - ax, vy = by - ay, k = Mathf.Clamp01(((x + .5f - ax) * vx + (y + .5f - ay) * vy) / (vx * vx + vy * vy));
                        float dx = x + .5f - (ax + vx * k), dy = y + .5f - (ay + vy * k);
                        if (dx * dx + dy * dy <= 1.25f) px[y * S + x] = new Color32(255, 255, 255, 255);
                    }
            };
            System.Action<int, int, int, int> box = (x0, y0, x1, y1) =>
            {
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        px[y * S + x] = new Color32(255, 255, 255, 255);
            };

            // delta: trojkat z wierzcholkiem u gory
            line(2.5f, 10f, 15.5f, 10f);
            line(2.5f, 10f, 9f, 28f);
            line(15.5f, 10f, 9f, 28f);
            // U
            box(19, 12, 20, 28);
            box(26, 12, 27, 28);
            box(19, 11, 27, 12);
            // I z szeryfami
            box(32, 11, 33, 28);
            box(30, 11, 35, 12);
            box(30, 27, 35, 28);

            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
