using UnityEngine;

[assembly: KSPAssembly("DeltaUI", 1, 0)]

namespace DeltaUI
{
    /// <summary>
    /// Wyglad w stylu Dear ImGui z ReShade / SDR++: ciemnoszare tlo, szare pola,
    /// niebieskie przyciski i aktywny tab, czcionka o stalej szerokosci.
    ///
    /// Wszystkie kolory sa tutaj - zmiana motywu to zmiana tych stalych.
    /// Skin powstaje z kopii domyslnego skina Unity, wiec zwykle GUILayout.Button /
    /// Toggle / HorizontalSlider w cudzym kodzie od razu dostaja ten wyglad.
    /// </summary>
    public static class Theme
    {
        public static readonly Color WindowBg     = Hex(0x0F0F0F, 0.97f);
        public static readonly Color TitleBg      = Hex(0x0A0A0A);
        public static readonly Color ChildBg      = Hex(0x141414);
        public static readonly Color Border       = Hex(0x3E3E43);

        public static readonly Color Frame        = Hex(0x1D2F49);
        public static readonly Color FrameHover   = Hex(0x24456D);
        public static readonly Color FrameActive  = Hex(0x316AAC);

        public static readonly Color Button       = Hex(0x24456D);
        public static readonly Color ButtonHover  = Hex(0x4296FA);
        public static readonly Color ButtonActive = Hex(0x0F87FA);

        public static readonly Color Tab          = Hex(0x294F81);
        public static readonly Color TabHover     = Hex(0x3A82D8);
        public static readonly Color TabActive    = Hex(0x3369AD);

        public static readonly Color WinBtn       = Hex(0x24456D);      // przyciski paska tytulu
        public static readonly Color WinBtnHover  = Hex(0x4296FA);
        public static readonly Color CloseHover   = Hex(0xC0392B);
        public static readonly Color TitleActive  = Hex(0x294A7A);      // pasek tytulu okna z fokusem (ImGui TitleBgActive)
        public static readonly Color Accent       = Hex(0x4296FA);
        public static readonly Color Grab         = Hex(0x3D85E0);
        public static readonly Color HeaderHover  = Hex(0x387BCB);

        public static readonly Color Text         = Hex(0xF5F5F5);
        public static readonly Color TextDim      = Hex(0x808080);

        public const int Radius = 0;
        public const int FontSize = 14;

        private static GUISkin skin;
        private static Font font;
        private static Texture2D white;

        public static Texture2D White
        {
            get { if (white == null) white = Solid(Color.white); return white; }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int AddFontResourceEx(string file, uint flags, System.IntPtr reserved);
        private static bool fontsInstalled;

        /// <summary>Czcionka ΔUI o danym rozmiarze. JetBrains Mono z GameData/DeltaUI/Fonts jest rejestrowana tylko dla tego procesu (FR_PRIVATE), bez instalowania w systemie.</summary>
        public static Font OsFont(int size)
        {
            if (!fontsInstalled)
            {
                fontsInstalled = true;
                try
                {
                    string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(typeof(Theme).Assembly.Location)), "Fonts");
                    if (System.IO.Directory.Exists(dir))
                        foreach (string f in System.IO.Directory.GetFiles(dir, "*.ttf")) AddFontResourceEx(f, 0x10, System.IntPtr.Zero);
                }
                catch (System.Exception ex) { Debug.LogWarning("[DeltaUI] font: " + ex.Message); }
            }
            return Font.CreateDynamicFontFromOSFont(new[] { "JetBrains Mono", "Cascadia Mono", "Consolas", "Lucida Console" }, size);
        }

        public static Font Font
        {
            get
            {
                if (font == null)
                    font = OsFont(FontSize);
                return font;
            }
        }

        public static GUISkin Skin
        {
            get { if (skin == null) skin = Build(); return skin; }
        }

        /// <summary>Prostokat z zaokraglonymi rogami (gora-lewo, gora-prawo, dol-prawo, dol-lewo) - zakladki maja tylko gorne.</summary>
        public static void FillRounded(Rect r, Color c, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, White, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);
        }

        /// <summary>Prostokat w kolorze - tlo, linie, uchwyty.</summary>
        public static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White);
            GUI.color = old;
        }

        private static GUISkin Build()
        {
            GUISkin baseSkin = GUI.skin;
            var s = Object.Instantiate(baseSkin);
            s.name = "DeltaUI";
            s.font = Font;

            Texture2D frame = Rounded(Frame), frameH = Rounded(FrameHover), frameA = Rounded(FrameActive);
            Texture2D btn = Rounded(Button), btnH = Rounded(ButtonHover), btnA = Rounded(ButtonActive);

            s.label = Plain(baseSkin.label, Text);
            s.label.padding = new RectOffset(2, 2, 2, 2);
            s.label.wordWrap = true;

            s.box = Styled(baseSkin.box, Rounded(ChildBg, Border), Text);
            s.box.border = new RectOffset(3, 3, 3, 3);

            s.button = Styled(baseSkin.button, btn, Text, btnH, btnA);
            s.button.padding = new RectOffset(5, 5, 3, 3);
            s.button.margin = new RectOffset(2, 2, 2, 2);
            s.button.alignment = TextAnchor.MiddleCenter;
            // zaznaczony (wlaczony) przycisk: przyciemniony niebieski, zeby duze wlaczone pola nie krzyczaly
            s.button.onNormal.background = Rounded(Hex(0x2F5F9E));
            s.button.onHover.background = Rounded(Hex(0x3A73BD));
            s.button.onActive.background = btnA;

            s.textField = Styled(baseSkin.textField, frame, Text, frameH, frameA);
            s.textField.padding = new RectOffset(5, 5, 3, 3);
            s.textField.focused.background = frameA;
            s.textField.focused.textColor = Text;
            s.textArea = Styled(baseSkin.textArea, frame, Text, frameH, frameA);
            s.textArea.focused.background = frameA;

            // Pole wyboru ImGui: kwadrat pola, zaznaczony = niebieski srodek.
            s.toggle = new GUIStyle(baseSkin.toggle) { font = Font, fontSize = FontSize };
            s.toggle.normal.background = Check(Frame, false);
            s.toggle.hover.background = Check(FrameHover, false);
            s.toggle.active.background = Check(FrameActive, false);
            s.toggle.onNormal.background = Check(Frame, true);
            s.toggle.onHover.background = Check(FrameHover, true);
            s.toggle.onActive.background = Check(FrameActive, true);
            foreach (GUIStyleState st in new[] { s.toggle.normal, s.toggle.hover, s.toggle.active, s.toggle.onNormal, s.toggle.onHover, s.toggle.onActive })
                st.textColor = Text;
            s.toggle.border = new RectOffset(16, 0, 16, 0);
            s.toggle.overflow = new RectOffset(0, 0, 0, 0);
            s.toggle.padding = new RectOffset(22, 2, 2, 2);
            s.toggle.margin = new RectOffset(2, 2, 3, 3);
            s.toggle.fixedHeight = 16;
            s.toggle.imagePosition = ImagePosition.TextOnly;
            s.toggle.alignment = TextAnchor.MiddleLeft;

            // Suwak: szare pole, niebieski uchwyt.
            s.horizontalSlider = Styled(baseSkin.horizontalSlider, frame, Text);
            s.horizontalSlider.fixedHeight = 16;
            s.horizontalSlider.border = new RectOffset(3, 3, 3, 3);
            s.horizontalSlider.margin = new RectOffset(2, 2, 3, 3);
            s.horizontalSliderThumb = Styled(baseSkin.horizontalSliderThumb, Rounded(Grab), Text, Rounded(ButtonHover), Rounded(Accent));
            s.horizontalSliderThumb.fixedHeight = 12;
            s.horizontalSliderThumb.fixedWidth = 10;
            s.horizontalSliderThumb.margin = new RectOffset(0, 0, 2, 0);
            s.horizontalSliderThumb.border = new RectOffset(2, 2, 2, 2);

            // Paski przewijania: waskie, szary uchwyt.
            s.verticalScrollbar = Styled(baseSkin.verticalScrollbar, Solid(Hex(0x1A1A1A)), Text);
            s.verticalScrollbar.fixedWidth = 12;
            s.verticalScrollbar.border = new RectOffset(0, 0, 0, 0);
            s.verticalScrollbarThumb = Styled(baseSkin.verticalScrollbarThumb, Rounded(Hex(0x4F4F4F)), Text, Rounded(Hex(0x696969)), Rounded(Hex(0x828282)));
            s.verticalScrollbarThumb.fixedWidth = 8;
            s.verticalScrollbarThumb.margin = new RectOffset(2, 2, 2, 2);
            s.verticalScrollbarThumb.border = new RectOffset(3, 3, 3, 3);
            s.horizontalScrollbar = Styled(baseSkin.horizontalScrollbar, Solid(Hex(0x1A1A1A)), Text);
            s.horizontalScrollbar.fixedHeight = 12;
            s.horizontalScrollbarThumb = Styled(baseSkin.horizontalScrollbarThumb, Rounded(Hex(0x4F4F4F)), Text, Rounded(Hex(0x696969)), Rounded(Hex(0x828282)));
            s.horizontalScrollbarThumb.fixedHeight = 8;

            // Okno dla zwyklego GUILayout.Window (okna jeszcze nie przeniesione na doki).
            s.window = Styled(baseSkin.window, WindowTex(), Color.white);
            s.window.onNormal.background = s.window.normal.background;
            s.window.onNormal.textColor = Text;
            s.window.border = new RectOffset(3, 3, 24, 3);
            s.window.padding = new RectOffset(6, 6, 27, 6);
            s.window.alignment = TextAnchor.UpperLeft;
            s.window.contentOffset = new Vector2(8, -23);           // tytul w pasku (24 px) jak w oknach Docka
            s.scrollView = new GUIStyle(baseSkin.scrollView);

            return s;
        }

        private static GUIStyle Plain(GUIStyle from, Color text)
        {
            var g = new GUIStyle(from) { font = Font, fontSize = FontSize };
            g.normal.textColor = text;
            return g;
        }

        private static GUIStyle Styled(GUIStyle from, Texture2D bg, Color text, Texture2D hover = null, Texture2D active = null)
        {
            var g = new GUIStyle(from) { font = Font, fontSize = FontSize };
            g.normal.background = bg; g.normal.textColor = text;
            g.hover.background = hover ?? bg; g.hover.textColor = text;
            g.active.background = active ?? hover ?? bg; g.active.textColor = text;
            g.focused.background = bg; g.focused.textColor = text;
            g.onNormal.background = active ?? bg; g.onNormal.textColor = text;
            g.onHover.background = active ?? hover ?? bg; g.onHover.textColor = text;
            g.onActive.background = active ?? bg; g.onActive.textColor = text;
            g.border = new RectOffset(Radius + 1, Radius + 1, Radius + 1, Radius + 1);
            return g;
        }

        public static Color Hex(int rgb, float a = 1f)
        {
            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
        }

        public static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "dui" };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>8x8, zaokraglone rogi (wygladzone), opcjonalna 1-pikselowa ramka.
        /// Rozciagane jako 9-slice (border = Radius+1), wiec rogi zostaja ostre.</summary>
        /// <summary>Tlo okna moda: pasek tytulu (24 px, ciemny, z linia akcentu pod spodem) i korpus - jak w oknie Docka.</summary>
        private static Texture2D WindowTex()
        {
            const int W = 8, H = 32, Head = 24;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false)
            { name = "dui", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            float r = 3.5f;
            for (int y = 0; y < H; y++)                                  // y = 0 na dole tekstury
                for (int x = 0; x < W; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, W - r), cy = Mathf.Clamp(y + 0.5f, r, H - r);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    bool head = y >= H - Head;
                    Color c = head ? TitleActive : WindowBg;
                    if (y == H - Head - 1) c = Border;                   // cienka linia pod paskiem
                    else if (d > r - 1.5f || x == 0 || x == W - 1 || y == 0 || y == H - 1) c = Border;
                    c.a *= Mathf.Clamp01(r + 0.5f - d);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        public static Texture2D Rounded(Color fill, Color? edge = null)
        {
            const int N = 8;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false)
            { name = "dui", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            float r = Radius + 0.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, N - r), cy = Mathf.Clamp(y + 0.5f, r, N - r);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float cover = Mathf.Clamp01(r - d);
                    Color c = fill;
                    if (edge.HasValue && ((Radius > 0 && d > r - 1.5f) || x == 0 || y == 0 || x == N - 1 || y == N - 1)) c = edge.Value;
                    c.a *= cover;
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        /// <summary>Pole wyboru 16x16 jak w SDR++: szary kwadrat, zaznaczone = niebieski kwadrat w srodku.</summary>
        private static Texture2D Check(Color frame, bool on)
        {
            const int N = 16;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false)
            { name = "dui", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Color c = Color.clear;
                    if (x >= 1 && x <= 14 && y >= 1 && y <= 14) c = frame;
                    if (on && x >= 4 && x <= 11 && y >= 4 && y <= 11) c = Accent;      // niebieski kwadrat w szarej ramce
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        private static float Seg(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float k = Mathf.Clamp01(((px - ax) * vx + (py - ay) * vy) / (vx * vx + vy * vy));
            float dx = px - (ax + vx * k), dy = py - (ay + vy * k);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }
}
