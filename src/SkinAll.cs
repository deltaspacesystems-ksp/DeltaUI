using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Motyw ΔUI dla wszystkich okien IMGUI w grze: styly kazdego GUISkin (domyslny Unity, HighLogic.Skin, skiny KSP
    /// i skiny modow) podmieniane w miejscu na style ze skina ΔUI. Mody rysujace przez GUI.skin / HighLogic.Skin dostaja
    /// wtedy nowy wyglad bez zmian we wlasnym kodzie. Oryginaly sa zapamietane, wiec wylaczenie przywraca stary wyglad.
    /// Nie dotyka uGUI (pasek aplikacji, dymki) - to osobna warstwa.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class SkinAllAddon : MonoBehaviour
    {
        public const string Key = "mods.style";        // domyslnie wylaczone: inne mody zostaja jak byly
        private static readonly PropertyInfo[] styleProps = FindStyleProps();
        private static readonly Dictionary<GUISkin, Dictionary<PropertyInfo, GUIStyle>> originals = new Dictionary<GUISkin, Dictionary<PropertyInfo, GUIStyle>>();
        private static bool applied;
        private static Font modFont;
        private static Font ModFont
        {
            get
            {
                if (modFont == null) modFont = Theme.OsFont(12);
                return modFont;
            }
        }
        private static int recheckIn = 3;

        public static bool Enabled
        {
            get { return Dock.GetNum(Key, 0f) > 0.5f; }
            set { Dock.SetNum(Key, value ? 1f : 0f); recheckIn = 1; }
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            GameEvents.onLevelWasLoaded.Add(_ => recheckIn = 60);      // mody tworza swoje skiny przy wejsciu w scene
            PatchStyleFonts();
        }

        // Style skopiowane przez mody przed podmiana skina (np. etykieta "ACTIONS" w KSP-AVC) trzymaja stara czcionke.
        // Przy pierwszym uzyciu podmieniamy im ja na czcionke ΔUI - rozmiar zostaje ich wlasny.
        private static void PatchStyleFonts()
        {
            try
            {
                var h = new HarmonyLib.Harmony("deltaui.stylefonts");
                var pre = new HarmonyLib.HarmonyMethod(typeof(SkinAllAddon).GetMethod("FontPrefix", BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0;
                foreach (MethodInfo m in typeof(GUIStyle).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "Draw" || m.Name == "CalcSize" || m.Name == "CalcHeight" || m.Name == "CalcSizeWithConstraints") { h.Patch(m, prefix: pre); n++; }
                Debug.Log("[DeltaUI] style fonts: patched " + n + " GUIStyle methods");
            }
            catch (System.Exception e) { Debug.LogWarning("[DeltaUI] style fonts disabled: " + e.Message); }
        }

        private static readonly HashSet<GUIStyle> seen = new HashSet<GUIStyle>();
        private static Texture2D bNormal, bHover, bActive;

        private static void FontPrefix(GUIStyle __instance)
        {
            if (!applied) return;
            Font f = __instance.font;
            if (f != null && f != modFont && f != Theme.Font) __instance.font = ModFont;
            if (!seen.Add(__instance)) return;

            // Przyciski moda z wlasnymi teksturami (Luna: szare gradienty, czerwony X): styl z ramka 9-slice i inna
            // tekstura po najechaniu to przycisk - dostaje tla ΔUI, a zawartosc (ikony) zostaje. Ikony-tla (bez ramki) sie nie ruszaja.
            try
            {
                Texture2D n = __instance.normal.background, h = __instance.hover.background;
                RectOffset b = __instance.border;
                if (n != null && n.name != "dui" && h != null && h != n && b != null && b.left + b.right + b.top + b.bottom > 0)
                {
                    if (bNormal == null) { bNormal = Theme.Rounded(Theme.Button); bHover = Theme.Rounded(Theme.ButtonHover); bActive = Theme.Rounded(Theme.ButtonActive); }
                    __instance.normal.background = bNormal; __instance.focused.background = bNormal;
                    __instance.hover.background = bHover;
                    __instance.active.background = bActive;
                    if (__instance.onNormal.background != null) __instance.onNormal.background = bActive;
                    if (__instance.onHover.background != null) __instance.onHover.background = bHover;
                    if (__instance.onActive.background != null) __instance.onActive.background = bActive;
                    __instance.border = new RectOffset(1, 1, 1, 1);
                }
            }
            catch (System.Exception) { }
        }

        private static PropertyInfo[] FindStyleProps()
        {
            var l = new List<PropertyInfo>();
            foreach (PropertyInfo p in typeof(GUISkin).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.PropertyType == typeof(GUIStyle) && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0) l.Add(p);
            return l.ToArray();
        }

        // Skin ΔUI powstaje z GUI.skin, a ta dostepna jest tylko w OnGUI - dlatego praca idzie stad.
        private void OnGUI()
        {
            if (Event.current.type != EventType.Layout) return;
            if (recheckIn > 0 && --recheckIn == 0) Sync();
        }

        private static void Sync()
        {
            bool want = Enabled;
            if (!want && !applied) return;
            GUISkin theme = Theme.Skin;
            int n = 0;
            foreach (GUISkin s in Resources.FindObjectsOfTypeAll<GUISkin>())
            {
                if (s == null || s == theme) continue;
                Dictionary<PropertyInfo, GUIStyle> orig;
                bool known = originals.TryGetValue(s, out orig);
                if (want)
                {
                    if (!known) { orig = new Dictionary<PropertyInfo, GUIStyle>(); originals[s] = orig; }
                    foreach (PropertyInfo p in styleProps)
                    {
                        if (p.Name == "toggle") continue;                    // mody uzywaja toggla jako duzego przycisku; tekstura pola by sie rozciagala
                        GUIStyle src = (GUIStyle)p.GetValue(theme, null);
                        if (src == null) continue;
                        if (!known) { GUIStyle cur = (GUIStyle)p.GetValue(s, null); if (cur != null) orig[p] = new GUIStyle(cur); }
                        var g = new GUIStyle(src) { font = ModFont, fontSize = 12 };
                        // okna modow maja sztywne wysokosci wierszy - mniejsza czcionka i ciasniejsze wypelnienie, zeby nie ucinalo tekstu
                        if (p.Name == "button" || p.Name == "textField" || p.Name == "textArea" || p.Name == "box")
                            g.padding = new RectOffset(src.padding.left, src.padding.right, 1, 1);
                        if (p.Name == "button") { g.margin = new RectOffset(2, 2, 1, 1); g.overflow = new RectOffset(0, 0, 3, 3); }   // tlo siega poza sztywna wysokosc przycisku moda, zeby tekst nie wystawal
                        p.SetValue(s, g, null);
                    }
                    n++;
                }
                else if (known)
                {
                    foreach (KeyValuePair<PropertyInfo, GUIStyle> kv in orig) kv.Key.SetValue(s, new GUIStyle(kv.Value), null);
                    n++;
                }
            }
            if (!want) originals.Clear();
            applied = want;
            Debug.Log("[DeltaUI] IMGUI theme " + (want ? "applied to " : "restored on ") + n + " skins");
            if (want) recheckIn = 600;                                 // co ~10 s dobierz skiny utworzone pozniej
        }
    }
}
