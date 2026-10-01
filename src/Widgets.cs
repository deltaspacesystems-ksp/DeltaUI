using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeltaUI
{
    /// <summary>
    /// Kontrolki, ktorych nie da sie uzyskac samym skinem - wzorowane na ImGui z ReShade:
    ///   Slider  - szare pole, niebieski uchwyt, wartosc w srodku, &lt; &gt; obok, etykieta z prawej;
    ///   Header  - zwijana sekcja z trojkatem (stan pamietany po kluczu);
    ///   Title   - wysrodkowany napis sekcji z linia pod spodem ("DEPTH");
    ///   WideButton - przycisk na cala szerokosc ("Reload").
    /// Wszystko dziala w GUILayout, wiec miesza sie ze zwyklymi kontrolkami.
    /// </summary>
    public static class Widgets
    {
        private static readonly Dictionary<string, bool> open = new Dictionary<string, bool>();
        private static GUIStyle centered, dim, arrow, title, header;

        private static void Styles()
        {
            if (centered != null) return;
            centered = new GUIStyle(Theme.Skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
            dim = new GUIStyle(Theme.Skin.label) { wordWrap = true };     // dluzsze opisy maja sie zawijac, nie rozpychac okna
            dim.normal.textColor = Theme.TextDim;
            arrow = new GUIStyle(Theme.Skin.button) { padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(1, 1, 3, 3), fixedWidth = 16, fixedHeight = 16 };
            title = new GUIStyle(Theme.Skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            header = new GUIStyle(Theme.Skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false, padding = new RectOffset(4, 4, 2, 2) };
        }

        /// <summary>Suwak ImGui. step &gt; 0: strzalki &lt; &gt; zmieniaja o krok, wartosc jest do niego zaokraglana.</summary>
        public static float Slider(string label, float value, float min, float max, string format = "0.000", float step = 0f)
        {
            Styles();
            GUILayout.BeginHorizontal();
            Rect r = GUILayoutUtility.GetRect(60, 2000, 16, 16, GUILayout.ExpandWidth(true));
            r.y += 3;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            float t = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition) && e.button == 0) { GUIUtility.hotControl = id; e.Use(); goto case EventType.MouseDrag; }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        float k = Mathf.Clamp01((e.mousePosition.x - r.x - 5f) / Mathf.Max(1f, r.width - 10f));
                        value = min + (max - min) * k;
                        if (step > 0f) value = Mathf.Round(value / step) * step;
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
                case EventType.Repaint:
                    bool hot = GUIUtility.hotControl == id, over = r.Contains(e.mousePosition);
                    Theme.Fill(r, hot ? Theme.FrameActive : over ? Theme.FrameHover : Theme.Frame);
                    float gx = r.x + 1f + (r.width - 12f) * t;
                    Theme.Fill(new Rect(gx, r.y + 2f, 10f, r.height - 4f), hot ? Theme.Accent : Theme.Grab);
                    centered.Draw(r, new GUIContent(value.ToString(format)), false, false, false, false);
                    break;
            }

            if (step > 0f)
            {
                if (GUILayout.Button("<", arrow)) { value -= step; GUI.changed = true; }
                if (GUILayout.Button(">", arrow)) { value += step; GUI.changed = true; }
            }
            value = Mathf.Clamp(value, min, max);
            if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, dim, GUILayout.Width(Mathf.Max(120f, dim.CalcSize(new GUIContent(label)).x)));
            GUILayout.EndHorizontal();
            return value;
        }

        public static int Slider(string label, int value, int min, int max)
        {
            return Mathf.RoundToInt(Slider(label, value, min, max, "0", 1f));
        }

        /// <summary>Zwijana sekcja. Zwraca true, gdy rozwinieta. Klucz musi byc unikalny w obrebie moda.</summary>
        public static bool Header(string key, string text, bool defaultOpen = true)
        {
            Styles();
            bool isOpen;
            if (!open.TryGetValue(key, out isOpen)) isOpen = defaultOpen;
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), header, GUILayout.ExpandWidth(true), GUILayout.Height(18));
            Event e = Event.current;
            bool over = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                if (over) Theme.Fill(r, Theme.HeaderHover);
                Triangle(new Rect(r.x + 4, r.y + 5, 8, 8), isOpen, Theme.Text);
                header.Draw(new Rect(r.x + 14, r.y, r.width - 14, r.height), new GUIContent(text), false, false, false, false);
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && over)
            {
                isOpen = !isOpen;
                e.Use();
            }
            open[key] = isOpen;
            return isOpen;
        }

        /// <summary>Tytul sekcji wysrodkowany, z linia pod spodem - jak "DEPTH" w ReShade.</summary>
        public static void Title(string text)
        {
            Styles();
            GUILayout.Space(4);
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), title, GUILayout.ExpandWidth(true), GUILayout.Height(18));
            if (Event.current.type == EventType.Repaint)
            {
                Theme.Fill(new Rect(r.x, r.y, r.width, 1), Theme.Border);
                title.Draw(r, new GUIContent(text), false, false, false, false);
            }
        }

        /// <summary>Etykieta w kolorze przygaszonym (opisy, jednostki).</summary>
        public static void Dim(string text)
        {
            Styles();
            GUILayout.Label(text, dim);
        }

        public static bool WideButton(string text)
        {
            return GUILayout.Button(text, GUILayout.ExpandWidth(true));
        }

        /// <summary>Wiersz przyciskow-zakladek (np. tryby) - zwraca wybrany indeks.</summary>
        public static int Choice(int selected, params string[] items)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < items.Length; i++)
            {
                bool on = GUILayout.Toggle(i == selected, items[i], Theme.Skin.button);
                if (on && i != selected) selected = i;
            }
            GUILayout.EndHorizontal();
            return selected;
        }

        /// <summary>Trojkat rozwiniecia: w dol (otwarte) albo w prawo (zwiniete).</summary>
        internal static void Triangle(Rect r, bool down, Color c)
        {
            // rysowany paskami 1 px - bez tekstur i bez zaleznosci od czcionki
            int n = (int)r.height;
            for (int i = 0; i < n; i++)
            {
                float w = (n - i) * (r.width / n);
                if (down) Theme.Fill(new Rect(r.x + (r.width - w) * 0.5f, r.y + i * 0.75f, w, 1), c);
                else Theme.Fill(new Rect(r.x + i * 0.75f, r.y + (r.height - w) * 0.5f, 1, w), c);
            }
        }
    }
}
