using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using KSP.UI.Screens;
using UnityEngine;

namespace DeltaUI
{
    internal sealed class PerfStat
    {
        public string Asm, Name;
        public long Ticks, MaxTicks;
        public int Calls;
    }

    internal sealed class PerfRow
    {
        public string Name, Asm;
        public double Ms, Calls, MaxMs;
    }

    /// <summary>
    /// Liczniki profilera. Zapisuje tylko watek glowny (Update, FixedUpdate i OnGUI chodza na nim),
    /// wiec nie trzeba blokad - a tak jest tez czytany przez okno.
    ///
    /// Mierzy CALOSC czasu metody wraz z tym, co ona wola (czas "inclusive"). Metoda klasy
    /// pochodnej, ktora wola metode bazowa, tez moze byc podpieta - wtedy jej czas liczy sie
    /// dwa razy w sumie, ale kazdy wiersz z osobna jest prawdziwy.
    /// </summary>
    public static class Perf
    {
        public static volatile bool Recording;
        public static bool Hooked;
        public static int HookedMethods, HookedAssemblies, HookFailed;
        public static int Frames;
        public static double SumDt;
        public static int MainThread;

        internal static readonly Dictionary<MethodBase, PerfStat> Stats = new Dictionary<MethodBase, PerfStat>();
        internal static List<PerfRow> Methods = new List<PerfRow>();
        internal static List<PerfRow> Assemblies = new List<PerfRow>();
        public static double FrameMs, HookedMs;

        /// <summary>Wolane z postfiksu Harmony po kazdym zmierzonym wywolaniu.</summary>
        public static void Record(MethodBase m, long ticks)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThread) return;
            PerfStat s;
            if (!Stats.TryGetValue(m, out s))
            {
                s = new PerfStat
                {
                    Asm = m.DeclaringType != null ? m.DeclaringType.Assembly.GetName().Name : "?",
                    Name = (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name
                };
                Stats[m] = s;
            }
            s.Ticks += ticks;
            s.Calls++;
            if (ticks > s.MaxTicks) s.MaxTicks = ticks;
        }

        public static void Reset()
        {
            foreach (PerfStat s in Stats.Values) { s.Ticks = 0; s.MaxTicks = 0; s.Calls = 0; }
            Frames = 0;
            SumDt = 0;
        }

        /// <summary>Przelicza liczniki na ms/klatke i sortuje - nie co klatke, tylko gdy okno tego chce.</summary>
        public static void Refresh()
        {
            double perMs = Stopwatch.Frequency / 1000.0;
            int frames = Math.Max(1, Frames);
            var byAsm = new Dictionary<string, PerfRow>();
            var rows = new List<PerfRow>(Stats.Count);
            foreach (PerfStat s in Stats.Values)
            {
                if (s.Calls == 0) continue;
                var r = new PerfRow { Name = s.Name, Asm = s.Asm, Ms = s.Ticks / perMs / frames, Calls = (double)s.Calls / frames, MaxMs = s.MaxTicks / perMs };
                rows.Add(r);
                PerfRow a;
                if (!byAsm.TryGetValue(s.Asm, out a)) { a = new PerfRow { Name = s.Asm, Asm = s.Asm }; byAsm[s.Asm] = a; }
                a.Ms += r.Ms; a.Calls += r.Calls;
                if (r.MaxMs > a.MaxMs) a.MaxMs = r.MaxMs;
            }
            rows.Sort((x, y) => y.Ms.CompareTo(x.Ms));
            var asms = new List<PerfRow>(byAsm.Values);
            asms.Sort((x, y) => y.Ms.CompareTo(x.Ms));
            double sum = 0;
            foreach (PerfRow a in asms) sum += a.Ms;
            Methods = rows;
            Assemblies = asms;
            HookedMs = sum;
            FrameMs = Frames > 0 ? SumDt * 1000.0 / Frames : 0;
        }

        public static string ReportPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/DeltaUI/PluginData/perf-report.txt"); }
        }

        public static string BuildReport()
        {
            Refresh();
            var sb = new StringBuilder();
            sb.AppendFormat("DeltaUI Perf  {0:yyyy-MM-dd HH:mm:ss}\n", DateTime.Now);
            sb.AppendFormat("scene {0}, {1} frames, {2:F1} s\n", HighLogic.LoadedScene, Frames, SumDt);
            sb.AppendFormat("frame {0:F1} ms avg ({1:F1} FPS); measured in mods (inclusive) {2:F1} ms = {3:P0}; the rest {4:F1} ms is engine / physics / threads\n",
                            FrameMs, FrameMs > 0 ? 1000.0 / FrameMs : 0, HookedMs, FrameMs > 0 ? HookedMs / FrameMs : 0, Math.Max(0, FrameMs - HookedMs));
            sb.AppendFormat("hooks: {0} methods in {1} assemblies, {2} failed\n\n", HookedMethods, HookedAssemblies, HookFailed);
            sb.AppendLine("BY ASSEMBLY   ms/frame   calls/frame   worst single call ms");
            for (int i = 0; i < Math.Min(30, Assemblies.Count); i++)
            {
                PerfRow r = Assemblies[i];
                sb.AppendFormat("{0,-40} {1,8:F3} {2,8:F1} {3,10:F2}\n", r.Name, r.Ms, r.Calls, r.MaxMs);
            }
            sb.AppendLine("\nBY METHOD   ms/frame   calls/frame   worst single call ms");
            for (int i = 0; i < Math.Min(50, Methods.Count); i++)
            {
                PerfRow r = Methods[i];
                sb.AppendFormat("{0,-50} {1,-28} {2,8:F3} {3,8:F1} {4,10:F2}\n", r.Name, r.Asm, r.Ms, r.Calls, r.MaxMs);
            }
            return sb.ToString();
        }

        public static bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, BuildReport(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[DeltaUI] perf report: " + ex.Message); return false; }
        }
    }

    /// <summary>
    /// Okno "Perf": podpina pomiary pod metody Update / FixedUpdate / OnGUI wszystkich modow i pokazuje,
    /// ktore zjadaja najwiecej milisekund klatki. Przycisk na pasku aplikacji albo Ctrl+Alt+P.
    /// Raport co 20 s laduje tez w GameData/DeltaUI/PluginData/perf-report.txt.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class PerfAddon : MonoBehaviour
    {
        private const string PanelId = "deltaui.perf";
        private static readonly HashSet<string> Names = new HashSet<string>
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI",
            "OnUpdate", "OnFixedUpdate",                       // PartModule, ScenarioModule, VesselModule
            "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage", "OnRenderObject"
        };

        private LauncherButton button;
        private GUIStyle nameStyle, numStyle, dimNum, note;
        private bool includeStock, patching;
        private string status = "";
        private float nextRefresh, nextSave;
        private string savedNote = "";

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Perf.MainThread = Thread.CurrentThread.ManagedThreadId;
        }

        private void Start()
        {
            Dock.Register(PanelId, "Perf", Draw, new Rect(60, 80, 470, 560), true, PerfIcon, 40);
            Dock.OpenChanged += OnOpenChanged;
            button = new LauncherButton(PerfIcon,
                () => Dock.SetOpen(PanelId, true), () => Dock.SetOpen(PanelId, false),
                ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT |
                ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.VAB |
                ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.TRACKSTATION);
            button.Sync(Dock.IsOpen(PanelId));
        }

        private void OnDestroy()
        {
            Dock.OpenChanged -= OnOpenChanged;
            if (button != null) button.Destroy();
        }

        private void OnOpenChanged(string id, bool open)
        {
            if (id == PanelId && button != null) button.Sync(open);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.P) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKey(KeyCode.LeftControl))
            {
                Dock.Toggle(PanelId);
                button.Sync(Dock.IsOpen(PanelId));
            }

            if (Perf.Hooked && Perf.Recording)
            {
                Perf.Frames++;
                Perf.SumDt += Time.unscaledDeltaTime;
                if (Time.unscaledTime > nextSave) { nextSave = Time.unscaledTime + 20f; Perf.Save(); }
            }
            if (Dock.IsOpen(PanelId) && Time.unscaledTime > nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 0.5f;
                if (Perf.Hooked) Perf.Refresh();
            }
        }

        // ------------------------------------------------------------------
        //  Podpinanie
        // ------------------------------------------------------------------

        private static bool Skip(string n, bool stock)
        {
            if (n == "DeltaUI" || n == "0Harmony" || n == "mscorlib") return true;
            if (n.StartsWith("System") || n.StartsWith("Mono.") || n.StartsWith("UnityEngine") || n.StartsWith("Unity.")) return true;
            if (n.StartsWith("ICSharpCode") || n.StartsWith("Newtonsoft") || n.StartsWith("nunit") || n.StartsWith("SharpDX")) return true;
            if (n.StartsWith("Accessibility") || n.StartsWith("Boo.") || n.StartsWith("UnityScript") || n.StartsWith("I18N")) return true;
            if (!stock && (n == "Assembly-CSharp" || n == "Assembly-CSharp-firstpass" || n.StartsWith("KSPAssets"))) return true;
            return false;
        }

        private static void Collect(Type t, List<MethodBase> into)
        {
            if (t.IsInterface || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) return;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!Names.Contains(m.Name) || m.IsAbstract || m.IsGenericMethod || m.ContainsGenericParameters) continue;
                if ((m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0) continue;
                MethodBody body = null;
                try { body = m.GetMethodBody(); } catch { }
                if (body == null) continue;
                into.Add(m);
            }
        }

        private IEnumerator PatchRoutine(bool stock)
        {
            patching = true;
            status = "scanning assemblies ...";
            yield return null;

            var list = new List<MethodBase>();
            int asms = 0;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                string n = a.GetName().Name;
                if (Skip(n, stock)) continue;
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                int before = list.Count;
                foreach (Type t in types)
                {
                    if (t == null) continue;
                    try { Collect(t, list); } catch { }
                }
                if (list.Count > before) asms++;
            }

            bool ok = true;
            try { PerfPatcher.Init(); }
            catch (Exception e) { status = "Harmony unavailable: " + e.Message; ok = false; }
            if (!ok) { patching = false; yield break; }

            var sw = new Stopwatch();
            int i = 0, failed = 0;
            while (i < list.Count)
            {
                sw.Reset(); sw.Start();
                while (i < list.Count && sw.ElapsedMilliseconds < 10)      // ~10 ms na klatke - gra dalej sie rusza
                {
                    try { PerfPatcher.Hook(list[i]); }
                    catch { failed++; }
                    i++;
                }
                status = string.Format("hooking {0} / {1} ...", i, list.Count);
                yield return null;
            }

            Perf.HookedMethods = list.Count - failed;
            Perf.HookedAssemblies = asms;
            Perf.HookFailed = failed;
            Perf.Reset();
            Perf.Hooked = true;
            Perf.Recording = true;
            patching = false;
            status = "";
        }

        // ------------------------------------------------------------------
        //  Okno
        // ------------------------------------------------------------------

        private void Styles()
        {
            if (nameStyle != null) return;
            nameStyle = new GUIStyle(Theme.Skin.label) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(2, 2, 0, 0) };
            numStyle = new GUIStyle(nameStyle) { alignment = TextAnchor.MiddleRight };
            dimNum = new GUIStyle(numStyle);
            dimNum.normal.textColor = Theme.TextDim;
            note = new GUIStyle(Theme.Skin.label) { wordWrap = true };
            note.normal.textColor = Theme.TextDim;
        }

        private void Draw()
        {
            Styles();

            if (!Perf.Hooked)
            {
                GUILayout.Label("Measures how long every mod's Update / FixedUpdate / OnGUI takes per frame and ranks them, " +
                                "so you can see who is eating your FPS.", note);
                GUILayout.Space(4);
                includeStock = GUILayout.Toggle(includeStock, "also KSP stock code (Assembly-CSharp) - more hooks");
                GUILayout.Space(4);
                GUI.enabled = !patching;
                if (Widgets.WideButton(patching ? status : "Start profiling")) StartCoroutine(PatchRoutine(includeStock));
                GUI.enabled = true;
                GUILayout.Space(4);
                if (status.Length > 0 && !patching) GUILayout.Label(status, note);
                GUILayout.Label("Hooking takes a few seconds and the game keeps running. Each measured call costs a few tens of " +
                                "nanoseconds; Pause stops the measuring. Coroutines and threads are not measured.", note);
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Perf.Recording ? "Pause" : "Resume")) Perf.Recording = !Perf.Recording;
            if (GUILayout.Button("Reset")) Perf.Reset();
            if (GUILayout.Button("Save report")) savedNote = Perf.Save() ? "saved: PluginData/perf-report.txt" : "save failed";
            if (GUILayout.Button("Copy")) { try { GUIUtility.systemCopyBuffer = Perf.BuildReport(); savedNote = "copied to clipboard"; } catch { } }
            GUILayout.EndHorizontal();
            if (savedNote.Length > 0) Widgets.Dim(savedNote);

            double fps = Perf.FrameMs > 0 ? 1000.0 / Perf.FrameMs : 0;
            GUILayout.Label(string.Format("frame {0:F1} ms  ({1:F1} FPS)  over {2} frames", Perf.FrameMs, fps, Perf.Frames));
            GUILayout.Label(string.Format("measured in mods: {0:F1} ms = {1:P0}", Perf.HookedMs, Perf.FrameMs > 0 ? Perf.HookedMs / Perf.FrameMs : 0));
            GUILayout.Label(string.Format("not measured: {0:F1} ms  (engine, physics, rendering, threads)", Math.Max(0, Perf.FrameMs - Perf.HookedMs)), note);
            Widgets.Dim(string.Format("{0} methods hooked in {1} assemblies, {2} failed", Perf.HookedMethods, Perf.HookedAssemblies, Perf.HookFailed));

            double scale = Math.Max(0.001, Perf.FrameMs);
            if (Widgets.Header("perf.asm", "By mod (assembly)"))
            {
                Widgets.Dim("ms per frame  /  calls per frame");
                for (int i = 0; i < Math.Min(14, Perf.Assemblies.Count); i++)
                    Row(Perf.Assemblies[i], scale, false);
            }
            if (Widgets.Header("perf.meth", "Top methods"))
            {
                Widgets.Dim("ms per frame  /  worst single call");
                for (int i = 0; i < Math.Min(24, Perf.Methods.Count); i++)
                    Row(Perf.Methods[i], scale, true);
            }
        }

        private void Row(PerfRow r, double scale, bool method)
        {
            Rect rc = GUILayoutUtility.GetRect(10, 18, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                Theme.Fill(new Rect(rc.x, rc.y + 1, rc.width * Mathf.Clamp01((float)(r.Ms / scale)), rc.height - 2),
                           new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.30f));
            GUI.Label(new Rect(rc.x, rc.y, rc.width - 150, rc.height), r.Name, nameStyle);
            GUI.Label(new Rect(rc.xMax - 148, rc.y, 70, rc.height), r.Ms.ToString("F2") + " ms", numStyle);
            GUI.Label(new Rect(rc.xMax - 76, rc.y, 74, rc.height),
                      method ? "max " + r.MaxMs.ToString("F1") : r.Calls.ToString("F1") + "x", dimNum);
        }

        private static Texture2D PerfIcon()
        {
            const int S = 38;
            var px = new Color32[S * S];
            int[] h = { 12, 22, 17, 30, 9 };                     // slupki wykresu
            for (int b = 0; b < h.Length; b++)
                for (int x = 4 + b * 6; x < 4 + b * 6 + 4; x++)
                    for (int y = 4; y < 4 + h[b]; y++)
                        px[y * S + x] = new Color32(255, 255, 255, 255);
            for (int x = 3; x < 35; x++) { px[3 * S + x] = new Color32(255, 255, 255, 220); px[2 * S + x] = new Color32(255, 255, 255, 120); }
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }

    /// <summary>Przycisk na pasku aplikacji KSP (prawy gorny rog) - jeden na panel, przezywa zmiany scen.</summary>
    public sealed class LauncherButton
    {
        private readonly Func<Texture2D> icon;
        private readonly Action onOpen, onClose;
        private readonly ApplicationLauncher.AppScenes scenes;
        private ApplicationLauncherButton button;

        public LauncherButton(Func<Texture2D> icon, Action onOpen, Action onClose, ApplicationLauncher.AppScenes scenes)
        {
            this.icon = icon; this.onOpen = onOpen; this.onClose = onClose; this.scenes = scenes;
            GameEvents.onGUIApplicationLauncherReady.Add(OnReady);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnUnready);
            if (ApplicationLauncher.Ready) OnReady();
        }

        private void OnReady()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            button = ApplicationLauncher.Instance.AddModApplication(() => onOpen(), () => onClose(), null, null, null, null, scenes, icon());
        }

        private void OnUnready(GameScenes scene) { Remove(); }

        /// <summary>Ustawia wyglad przycisku bez wywolywania akcji.</summary>
        public void Sync(bool open)
        {
            if (button == null) return;
            if (open) button.SetTrue(false); else button.SetFalse(false);
        }

        private void Remove()
        {
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        public void Destroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnReady);
            GameEvents.onGUIApplicationLauncherUnreadifying.Remove(OnUnready);
            Remove();
        }
    }
}
