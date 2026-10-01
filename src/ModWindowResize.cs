using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Kazde okno GUI.Window / GUILayout.Window w grze dostaje uchwyt zmiany rozmiaru w prawym dolnym rogu.
    /// Harmony podmienia funkcje okna na opakowanie (rysuje uchwyt po tresci moda) i po zwroceniu prostokata nadaje mu
    /// rozmiar wybrany przez uzytkownika. Okna samego ΔUI (Dock) sa pomijane - one maja wlasne uchwyty.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class ModWindowResizeAddon : MonoBehaviour
    {
        private sealed class Size { public float W, H; }

        private static readonly Dictionary<int, Size> sizes = new Dictionary<int, Size>();
        private static readonly Dictionary<int, Rect> last = new Dictionary<int, Rect>();
        private static int resizing = int.MinValue;
        private static Vector2 startMouse, startSize, startPos;
        private const float Grip = 16f, MinW = 140f, MinH = 80f;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            try
            {
                var h = new Harmony("deltaui.modwindows");
                var pre = new HarmonyMethod(typeof(ModWindowResizeAddon).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(ModWindowResizeAddon).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0;
                foreach (Type t in new[] { typeof(GUI), typeof(GUILayout) })
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "Window" || m.ReturnType != typeof(Rect)) continue;
                        h.Patch(m, prefix: pre, postfix: post);
                        n++;
                    }
                Debug.Log("[DeltaUI] mod window resize: patched " + n + " Window overloads");
            }
            catch (Exception e) { Debug.LogWarning("[DeltaUI] mod window resize disabled: " + e.Message); }
        }

        /// <summary>Uchwyt zmiany rozmiaru i X w oknach innych modow - wlaczane w Settings (domyslnie wylaczone).</summary>
        public static bool Enabled { get { return SkinAllAddon.Enabled; } }

        private static bool Ours(GUI.WindowFunction f)
        {
            return f == null || f.Method.DeclaringType == null || f.Method.DeclaringType.Assembly == typeof(ModWindowResizeAddon).Assembly;
        }

        private static readonly HashSet<int> hidden = new HashSet<int>();
        private static readonly Dictionary<int, float> lastCall = new Dictionary<int, float>();

        private static bool Prefix(object[] __args, ref Rect __result)
        {
            if (!Enabled) return true;
            try
            {
                if (!(__args[0] is int)) return true;
                int id = (int)__args[0];
                float now = Time.unscaledTime, prev;
                bool had = lastCall.TryGetValue(id, out prev);
                lastCall[id] = now;
                // Ukryte X-em: okno wraca, gdy mod przestal je wolac na chwile (zamknal i otworzyl po swojemu)
                if (hidden.Contains(id) && had && now - prev > 0.3f) hidden.Remove(id);
                if (hidden.Contains(id))
                {
                    for (int i = 0; i < __args.Length; i++) if (__args[i] is Rect) { __result = (Rect)__args[i]; break; }
                    return false;
                }
                int ri = -1, fi = -1;
                for (int i = 0; i < __args.Length; i++)
                {
                    if (ri < 0 && __args[i] is Rect) ri = i;
                    if (__args[i] is GUI.WindowFunction) fi = i;
                }
                if (ri < 0 || fi < 0) return true;
                var orig = (GUI.WindowFunction)__args[fi];
                if (Ours(orig)) return true;

                Size sz;
                if (sizes.TryGetValue(id, out sz))
                {
                    var r = (Rect)__args[ri];
                    r.width = sz.W; r.height = sz.H;
                    __args[ri] = r;
                }
                __args[fi] = (GUI.WindowFunction)(wid => { orig(wid); DrawGrip(id); });
            }
            catch (Exception) { }
            return true;
        }

        private static void Postfix(object[] __args, ref Rect __result)
        {
            if (!Enabled) return;
            try
            {
                if (!(__args[0] is int)) return;
                int id = (int)__args[0];
                if (!wrapped(id)) { }
                Size sz;
                if (resizing == id) { __result.x = startPos.x; __result.y = startPos.y; }
                if (sizes.TryGetValue(id, out sz)) { __result.width = sz.W; __result.height = sz.H; }
                last[id] = __result;
            }
            catch (Exception) { }
        }

        private static bool wrapped(int id) { return true; }

        private static void DrawGrip(int id)
        {
            Rect win;
            if (!last.TryGetValue(id, out win)) return;
            Event e = Event.current;
            if (e.type != EventType.Repaint) return;

            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

            // X w pasku tytulu: chowa okno (mod moze je potem otworzyc po swojemu)
            var xr = new Rect(win.width - 22f, 4f, 16f, 16f);
            var xs = new Rect(win.x + xr.x, win.y + xr.y, xr.width, xr.height);
            bool xo = xs.Contains(mouse);
            Theme.Fill(xr, xo ? Theme.CloseHover : Theme.WinBtn);
            Color xc = Color.white;
            for (int k = 0; k < 8; k++)
            {
                Theme.Fill(new Rect(xr.x + 4f + k, xr.y + 4f + k, 1.6f, 1.6f), xc);
                Theme.Fill(new Rect(xr.x + 11f - k, xr.y + 4f + k, 1.6f, 1.6f), xc);
            }
            if (xo && Input.GetMouseButtonDown(0)) hidden.Add(id);

            var grip = new Rect(win.xMax - Grip, win.yMax - Grip, Grip, Grip);
            bool over = grip.Contains(mouse);

            if (resizing == int.MinValue && over && Input.GetMouseButtonDown(0))
            {
                resizing = id; startMouse = mouse; startSize = win.size; startPos = win.position;
            }
            if (resizing == id)
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 d = mouse - startMouse;
                    float w = Mathf.Clamp(startSize.x + d.x, MinW, Screen.width - startPos.x);
                    float h = Mathf.Clamp(startSize.y + d.y, MinH, Screen.height - startPos.y);
                    Size s;
                    if (!sizes.TryGetValue(id, out s)) { s = new Size(); sizes[id] = s; }
                    s.W = w; s.H = h;
                }
                else resizing = int.MinValue;
            }

            // trzy ukosne kreski w rogu, jak w oknach Docka
            Color c = resizing == id || over ? Theme.Accent : Theme.TextDim;
            float x1 = win.width - 3f, y1 = win.height - 3f;
            for (int k = 0; k < 3; k++)
                for (int j = 0; j <= 2 - k; j++)
                    Theme.Fill(new Rect(x1 - 3f - k * 4f - j * 0f + 0f, y1 - 1f - (2 - k - j) * 4f - 0f, 2f, 2f), c);
        }
    }
}
