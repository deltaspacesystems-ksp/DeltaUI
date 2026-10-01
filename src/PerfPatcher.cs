using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace DeltaUI
{
    /// <summary>
    /// Jedyne miejsce, ktore dotyka Harmony. Reszta DeltaUI o nim nie wie, wiec gdyby Harmony
    /// zniknal z GameData, wysypie sie tylko profiler (wyjatek lapie PerfAddon), a okna dzialaja.
    ///
    /// Do kazdej metody Update / FixedUpdate / OnGUI ... doklejamy prefiks (zapisuje czas
    /// startu) i postfiks (dolicza roznice do licznika tej metody). Kosztuje to kilkadziesiat
    /// nanosekund na wywolanie; gdy Perf.Recording == false, prefiks nic nie mierzy.
    /// </summary>
    public static class PerfPatcher
    {
        private static Harmony harmony;
        private static HarmonyMethod pre, post;

        public static void Init()
        {
            if (harmony != null) return;
            harmony = new Harmony("deltaui.perf");
            pre = new HarmonyMethod(typeof(PerfPatcher).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public));
            post = new HarmonyMethod(typeof(PerfPatcher).GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public));
        }

        public static void Hook(MethodBase original)
        {
            harmony.Patch(original, prefix: pre, postfix: post);
        }

        public static void Prefix(out long __state)
        {
            __state = Perf.Recording ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void Postfix(long __state, MethodBase __originalMethod)
        {
            if (__state == 0L) return;
            Perf.Record(__originalMethod, Stopwatch.GetTimestamp() - __state);
        }
    }
}
