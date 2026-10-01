using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Okno "Settings" (kolo zebate w pasku hubu). Na razie: przywrocenie ukladu okien i lista skrotow;
    /// tu beda tez motyw, skrot klawiszowy huba i ustawienia przegladarki.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class SettingsAddon : MonoBehaviour
    {
        private const string PanelId = "deltaui.settings";
        private bool confirmReset;

        private void Awake() { DontDestroyOnLoad(gameObject); }

        private void Start()
        {
            Dock.Register(PanelId, "Settings", Draw, new Rect(260, 160, 420, 300), true, Cog, 90);
        }

        private void Draw()
        {
            var v = typeof(SettingsAddon).Assembly.GetName().Version;
            GUILayout.Label("ΔUI " + v.Major + "." + v.Minor + "." + v.Build);
            Widgets.Dim("Windows with tabs, snap layouts and a window shortcut bar for every mod that registers a window.");

            if (Widgets.Header("settings.layout", "Window layout"))
            {
                Widgets.Dim("Puts every window back where it started and closes it. Contents and mod settings stay.");
                if (!confirmReset)
                {
                    if (GUILayout.Button("Reset window layout")) confirmReset = true;
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Yes, reset")) { Dock.ResetLayout(); confirmReset = false; }
                    if (GUILayout.Button("Cancel")) confirmReset = false;
                    GUILayout.EndHorizontal();
                }
            }

            if (Widgets.Header("settings.browser", "Browser"))
            {
                bool gpu = Dock.GetNum("browser.gpu", 0f) > 0.5f;
                bool g2 = GUILayout.Toggle(gpu, "Use GPU in the browser (restart the browser tab)");
                if (g2 != gpu) Dock.SetNum("browser.gpu", g2 ? 1f : 0f);
                Widgets.Dim("Off = software rendering on the CPU. Faster when the game keeps the GPU at 100%.");

                bool ab = Dock.GetNum("browser.adblock", 1f) > 0.5f;
                bool ab2 = GUILayout.Toggle(ab, "Block ads and trackers");
                if (ab2 != ab) Dock.SetNum("browser.adblock", ab2 ? 1f : 0f);
                Widgets.Dim("Cancels known ad/tracker network requests by domain, like a hosts-file blocker. Applies live, no restart needed.");
            }

            if (Widgets.Header("settings.skin", "Appearance"))
            {
                bool on = SkinAllAddon.Enabled;
                bool now = GUILayout.Toggle(on, "Restyle other mods' windows (theme, resize grip, close button)");
                if (now != on) SkinAllAddon.Enabled = now;
                Widgets.Dim("Experimental. Off by default: other mods keep their own look. Turn off again and restart to be sure everything is restored.");
            }

            if (Widgets.Header("settings.keys", "Shortcuts"))
            {
                GUILayout.Label("Ctrl+Alt+B   Browser");
                GUILayout.Label("Ctrl+Alt+P   Perf monitor");
                Widgets.Dim("Drag a window to a screen edge or corner to snap it (half or quarter, top = full screen). " +
                            "Hover the square button in a title bar for layouts.");
            }
        }

        /// <summary>Kolo zebate: zab z 8 zebami i otworem, biala maska.</summary>
        private static Texture2D Cog()
        {
            const int S = 38;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x + .5f - S / 2f, dy = y + .5f - S / 2f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(dy, dx);
                    float body = 11.5f, tooth = 15.5f;
                    bool inTooth = r <= tooth && Mathf.Cos(8f * ang) > 0.25f;
                    if ((r <= body || inTooth) && r >= 5f) px[y * S + x] = new Color32(255, 255, 255, 255);
                }
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
