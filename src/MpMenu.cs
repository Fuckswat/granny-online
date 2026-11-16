using UnityEngine;
using UnityEngine.UI;

namespace GrannyMP;

/// <summary>
/// Multiplayer menu built from the game's own UI system (uGUI Canvas/Image/Text).
/// Clicks are hit-tested by hand (no UnityEvent delegates), a GraphicRaycaster blocks clicks to the game UI below.
/// </summary>
public sealed class MpMenu
{
    static readonly Color Red = new(0.82f, 0.82f, 0.82f);   // menu word, like PLAY / QUIT
    static readonly Color RedHi = Color.white;
    static readonly Color Bg = new(0, 0, 0, 0.0f);
    static readonly Color BtnBg = new(0.3f, 0.3f, 0.3f, 0.38f);
    static readonly Color BtnHi = new(0.55f, 0.55f, 0.55f, 0.55f);
    static readonly Color BtnOff = new(0.15f, 0.15f, 0.15f, 0.35f);
    static readonly Color TextCol = new(0.96f, 0.96f, 0.96f);
    static readonly Color Dim = new(0.72f, 0.72f, 0.72f);
    static readonly Color Line = new(1f, 1f, 1f, 0.85f);

    sealed class Btn
    {
        public RectTransform Rt; public Image Bg; public Text Label;
        public Action Click; public Func<bool> Visible; public Func<bool> Enabled; public Func<string> Dyn;
        public Color Normal = BtnBg, Hover = BtnHi; public bool Plain; public Camera Cam;
        public RawImage Icon;
    }
    sealed class Lbl { public Text T; public Func<string> Dyn; public Func<bool> Visible; }

    enum Tab { Play, Settings, Admin }

    readonly Game G;
    GameObject _root;
    RectTransform _panel;
    Font _font;
    Tab _page = Tab.Play;
    readonly List<Btn> _btns = new();
    readonly List<Lbl> _lbls = new();
    readonly Dictionary<Tab, GameObject> _pages = new();
    Btn _menuButton;        // "МУЛЬТИПЛЕЕР" in the main menu
    Btn _pauseButton;       // "МУЛЬТИПЛЕЕР" in the in-game pause menu
    GameObject _menuButtonHost;
    RectTransform _back; Image _backImg; bool _backSprite;
    public bool Open { get; private set; }
    public int Focus; // 0 none, 1 name, 2 ip
    bool _built, _failed;
    float _nextBuildTry;
    readonly HashSet<string> _logged = new();
    void LogOnce(string m) { if (_logged.Add(m)) Plugin.L.LogWarning("[menu] " + m); }

    public MpMenu(Game g) { G = g; }

    public Font UiFont => GetFont();
    Btn _langButton;
    readonly List<Btn> _social = new();

    // links of the main menu icons; an empty one says "coming soon"
    const string GithubUrl = "", YoutubeUrl = "", TelegramUrl = "";

    static Texture2D IconTexture(string base64)
    {
        const int size = 96;
        var a = Convert.FromBase64String(base64);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                tex.SetPixel(x, y, new Color(1, 1, 1, a[y * size + x] / 255f));
        tex.Apply();
        return tex;
    }

    void OpenLink(string url)
    {
        if (string.IsNullOrEmpty(url)) { G.Toast(Tr.L("Ссылка скоро появится", "Link coming soon"), 2, Color.white); return; }
        Plugin.L.LogInfo("[menu] open " + url);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Plugin.L.LogWarning("[menu] link: " + e.Message); }
    }

    void SocialButton(int index, string icon, string url)
    {
        const float size = 60, gap = 22;
        var rt = MakeRect(_root.transform, "social", 0, 0, size, size);
        rt.anchorMin = rt.anchorMax = new Vector2(1, 0); rt.pivot = new Vector2(1, 0);
        rt.anchoredPosition = new Vector2(-28 - (2 - index) * (size + gap), 22);
        var clear = new Color(0, 0, 0, 0.001f);
        var b = new Btn { Rt = rt, Plain = true, Normal = clear, Hover = clear, Click = () => OpenLink(url) };
        b.Bg = MakeImage(rt, clear, true);
        var img = MakeRect(rt, "icon", 0, 0, size, size).gameObject.AddComponent<RawImage>();
        img.texture = IconTexture(icon); img.raycastTarget = false; img.color = Red;
        b.Icon = img;
        _btns.Add(b); _social.Add(b);
    }

    /// <summary>Language changed: rebuild every text from scratch.</summary>
    public void Rebuild()
    {
        var wasOpen = Open;
        if (_root != null) UnityEngine.Object.Destroy(_root);
        if (_menuButton?.Rt != null) UnityEngine.Object.Destroy(_menuButton.Rt.gameObject);
        _btns.Clear(); _lbls.Clear(); _pages.Clear();
        _menuButton = null; _menuButtonHost = null; _langButton = null; _backSprite = false;
        _built = false; _nextBuildTry = 0; Open = false;
        Build();
        if (wasOpen) Show(true);
    }


    Font GetFont()
    {
        if (_font != null) return _font;
        try
        {
            // fonts the game has already loaded (dynamic Arial-like fonts render Cyrillic through OS fallback)
            var fonts = GameRefs.FindAll<Font>(true);
            _font = fonts.FirstOrDefault(f => f.name.Contains("Arial") || f.name.Contains("LegacyRuntime")) ?? fonts.FirstOrDefault(f => f.dynamic) ?? fonts.FirstOrDefault();
        }
        catch (Exception e) { LogOnce("fonts: " + e.Message); }
        if (_font != null && _font.dynamic)
            try { _font.fontNames = new[] { "Georgia", "Times New Roman", "Arial" }; }  // Granny's serif look, has Cyrillic
            catch (Exception e) { LogOnce("fontNames: " + e.Message); }
        if (_font == null)
            try { foreach (var t in GameRefs.FindAll<Text>(true)) if (t.font != null) { _font = t.font; break; } } catch (Exception e) { LogOnce("text fonts: " + e.Message); }
        return _font;
    }

    static RectTransform MakeRect(Transform parent, string name, float x, float y, float w, float h, bool topLeft = true)
    {
        var go = new GameObject(name) { layer = 5 };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        if (topLeft) { rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1); rt.anchoredPosition = new Vector2(x, -y); }
        else { rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = new Vector2(x, y); }
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    Image MakeImage(RectTransform rt, Color c, bool raycast = false)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.color = c; img.raycastTarget = raycast;
        return img;
    }

    static Texture2D _grain, _soft;

    /// <summary>The label's letters become a window on the grainy gradient of the game's PLAY and QUIT.</summary>
    RawImage GradientLetters(RectTransform label, bool worn = false)
    {
        var mask = label.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;
        _grain ??= Game.RoleIcon("button_grain.bin", 512, 128);
        _soft ??= Game.RoleIcon("button_soft.bin", 512, 128);
        var rt = MakeRect(label, "grain", 0, 0, 0, 0);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
        var img = rt.gameObject.AddComponent<RawImage>();
        img.texture = worn ? _grain : _soft; img.raycastTarget = false; img.color = Red;
        return img;
    }

    Text MakeText(RectTransform rt, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft, bool outline = false, bool bold = false)
    {
        var t = rt.gameObject.AddComponent<Text>();
        t.font = GetFont(); t.fontSize = size; t.color = c; t.alignment = a; t.text = s;
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false; t.supportRichText = true;
        t.fontStyle = bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
        if (outline)
        {
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0, 0, 0, 0.9f); o.effectDistance = new Vector2(2, -2);
        }
        return t;
    }

    Lbl Label(Transform parent, float x, float y, float w, float h, string text, int size = 24, Color? c = null, TextAnchor a = TextAnchor.MiddleLeft, Func<string> dyn = null, Func<bool> vis = null)
    {
        var rt = MakeRect(parent, "lbl", x, y, w, h);
        var l = new Lbl { T = MakeText(rt, text, size, c ?? TextCol, a), Dyn = dyn, Visible = vis };
        _lbls.Add(l);
        return l;
    }

    Btn Button(Transform parent, float x, float y, float w, float h, string text, Action click, int size = 24, Func<bool> vis = null, Func<bool> enabled = null, Func<string> dyn = null)
    {
        var rt = MakeRect(parent, "btn", x, y, w, h);
        var b = new Btn { Rt = rt, Click = click, Visible = vis, Enabled = enabled, Dyn = dyn };
        b.Bg = MakeImage(rt, BtnBg, true);
        var lrt = MakeRect(rt, "t", 0, 0, w, h);
        b.Label = MakeText(lrt, text, size, TextCol, TextAnchor.MiddleCenter, false, true);
        b.Icon = GradientLetters(lrt);
        // thin frame
        foreach (var (fx, fy, fw, fh) in new[] { (0f, 0f, w, 2f), (0f, h - 2, w, 2f), (0f, 0f, 2f, h), (w - 2, 0f, 2f, h) })
            MakeImage(MakeRect(rt, "f", fx, fy, fw, fh), Line);
        _btns.Add(b);
        return b;
    }

    /// <summary>"name:  [-]  value  [+]" row; editable only by the host.</summary>
    void ValueRow(Transform p, float y, string name, Func<string> value, Action minus, Action plus)
    {
        Label(p, 40, y, 470, 40, name, 23);
        Button(p, 520, y, 50, 40, "–", minus, 28, enabled: () => !G.Session || G.Net.IsHost);
        Label(p, 575, y, 170, 40, "", 24, Color.white, TextAnchor.MiddleCenter, value);
        Button(p, 750, y, 50, 40, "+", plus, 28, enabled: () => !G.Session || G.Net.IsHost);
    }

    void ToggleRow(Transform p, float y, string name, Func<bool> get, Action<bool> set, Func<bool> enabled = null)
    {
        Label(p, 40, y, 470, 40, name, 23);
        Button(p, 520, y, 280, 40, "", () => set(!get()), 22, enabled: enabled, dyn: () => get() ? Tr.L("ВКЛ", "ON") : Tr.L("ВЫКЛ", "OFF"));
    }

    void Build()
    {
        if (Time.realtimeSinceStartup < _nextBuildTry) return;
        _nextBuildTry = Time.realtimeSinceStartup + 1f;
        if (GetFont() == null) { LogOnce("шрифт пока не найден, жду загрузки меню"); return; }
        _built = true;

        _root = new GameObject("GrannyMP_UI") { layer = 5 };
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000;
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;
        canvas.pixelPerfect = true;
        _root.AddComponent<GraphicRaycaster>();

        // ---- in-game pause menu button (bottom centre) ----
        var pb = MakeRect(_root.transform, "pause", 0, -380, 420, 80, false);
        _pauseButton = new Btn { Rt = pb, Click = () => Show(true), Plain = true };
        _pauseButton.Bg = MakeImage(pb, new Color(0, 0, 0, 0.001f), true);
        _pauseButton.Label = MakeText(MakeRect(pb, "t", 0, 0, 420, 80), Tr.L("Мультиплеер", "Multiplayer"), 46, Red, TextAnchor.MiddleCenter, true, false);
        _pauseButton.Normal = _pauseButton.Hover = new Color(0, 0, 0, 0.001f);
        _btns.Add(_pauseButton);

        // ---- main panel ----
        // full-screen backdrop: the game's own options background (dark room with Granny's eyes)
        _back = MakeRect(_root.transform, "backdrop", 0, 0, 100, 100, false);
        _back.anchorMin = Vector2.zero; _back.anchorMax = Vector2.one; _back.sizeDelta = Vector2.zero; _back.anchoredPosition = Vector2.zero;
        _backImg = MakeImage(_back, new Color(0, 0, 0, 0.93f), true);
        _back.gameObject.SetActive(false);

        _panel = MakeRect(_root.transform, "panel", 0, 0, 1000, 800, false);
        MakeImage(_panel, Bg, false);
        var title = MakeRect(_panel, "title", 0, 14, 1000, 76);
        MakeText(title, Tr.L("Мультиплеер", "Multiplayer"), 64, Color.white, TextAnchor.MiddleCenter, true, false);
        MakeImage(MakeRect(_panel, "titleline", 300, 90, 400, 3), Line);
        Label(_panel, 0, 94, 1000, 26, "", 18, Dim, TextAnchor.MiddleCenter, () => Tr.L($"мод {Plugin.Version}   •   игра {Plugin.GameBuildLabel}", $"mod {Plugin.Version}   •   game {Plugin.GameBuildLabel}"));
        Button(_panel, 940, 14, 46, 46, "X", () => Show(false), 26);

        // tabs
        Button(_panel, 40, 124, 300, 52, Tr.L("Игра", "Game"), () => { _page = Tab.Play; RefreshVisibility(); }, 26);
        Button(_panel, 350, 124, 300, 52, Tr.L("Настройки", "Settings"), () => { _page = Tab.Settings; RefreshVisibility(); }, 26);
        Button(_panel, 660, 124, 300, 52, Tr.L("Админ", "Admin"), () => { _page = Tab.Admin; RefreshVisibility(); }, 26, enabled: () => G.AdminAllowed);
        MakeImage(MakeRect(_panel, "sep", 40, 186, 920, 2), Line);

        BuildPlayPage(Page(Tab.Play));
        BuildSettingsPage(Page(Tab.Settings));
        BuildAdminPage(Page(Tab.Admin));

        _panel.gameObject.SetActive(false);

        // ---- language, bottom left corner ----
        var lr = MakeRect(_root.transform, "lang", 28, 0, 330, 56);
        lr.anchorMin = lr.anchorMax = Vector2.zero; lr.pivot = Vector2.zero; lr.anchoredPosition = new Vector2(28, 22);
        _langButton = new Btn { Rt = lr, Plain = true, Normal = new Color(0, 0, 0, 0.001f), Hover = new Color(0, 0, 0, 0.001f), Click = () => { Tr.Set(!Tr.En); Rebuild(); } };
        _langButton.Bg = MakeImage(lr, _langButton.Normal, true);
        _langButton.Label = MakeText(MakeRect(lr, "t", 0, 0, 330, 56), Tr.En ? "Language: English" : "Язык: Русский", 34, Red, TextAnchor.MiddleLeft, true, false);
        _btns.Add(_langButton);

        // ---- GitHub, YouTube, Telegram, bottom right corner (main menu only) ----
        _social.Clear();
        SocialButton(0, IconData.Github, GithubUrl);
        SocialButton(1, IconData.Youtube, YoutubeUrl);
        SocialButton(2, IconData.Telegram, TelegramUrl);

        Plugin.L.LogInfo("[menu] built, font " + _font.name + (_font.dynamic ? " (dynamic)" : ""));
    }

    Transform Page(Tab p)
    {
        var rt = MakeRect(_panel, "page_" + p, 0, 196, 1000, 600);
        _pages[p] = rt.gameObject;
        return rt;
    }

    void BuildPlayPage(Transform p)
    {
        bool Off() => !G.Session && !G.Net.Connecting;
        bool On() => G.Session;

        // ---- not connected ----
        Label(p, 40, 20, 200, 48, Tr.L("Ник:", "Nick:"), 26, vis: Off);
        Button(p, 240, 20, 720, 48, "", () => Focus = 1, 24, Off, dyn: () => FieldText(G.NameField, 1));
        Label(p, 40, 80, 200, 48, Tr.L("IP хоста:", "Host IP:"), 26, vis: Off);
        Button(p, 240, 80, 720, 48, "", () => Focus = 2, 24, Off, dyn: () => FieldText(G.IpField, 2));
        Button(p, 40, 150, 450, 70, Tr.L("СОЗДАТЬ ИГРУ", "CREATE GAME"), () => { Focus = 0; G.UiHost(); }, 30, Off);
        Button(p, 510, 150, 450, 70, Tr.L("ПРИСОЕДИНИТЬСЯ", "JOIN"), () => { Focus = 0; G.UiJoin(); }, 30, Off);
        Label(p, 40, 240, 920, 300,
            Tr.L("• Создать игру: ты хост, друзья заходят по твоему IP.\n• Присоединиться: впиши IP хоста и нажми кнопку.\n• Не в одной сети? Всем поставить <b>Radmin VPN</b>, зайти в одну сеть и взять IP оттуда.\n• Роли выпадают случайно: Гренни может стать любой.",
                 "• Create game: you are the host, friends join with your IP.\n• Join: type the host IP and press the button.\n• Not on one network? Everyone installs <b>Radmin VPN</b>, joins one network and uses that IP.\n• Roles are random: anyone can become Granny."),
            21, Dim, TextAnchor.UpperLeft, vis: Off);

        Label(p, 40, 20, 920, 48, Tr.L("Подключение...", "Connecting..."), 28, vis: () => G.Net.Connecting);
        Button(p, 40, 80, 400, 56, Tr.L("ОТМЕНА", "CANCEL"), () => G.EndSession(), 26, () => G.Net.Connecting);

        // ---- connected ----
        Label(p, 40, 10, 920, 34, "", 22, Dim, dyn: () => G.Net.IsHost ? Tr.L($"Ты ХОСТ. Твой IP: {NetSession.FriendIp()}   порт {Cfg.Port.Value}", $"You are the HOST. Your IP: {NetSession.FriendIp()}   port {Cfg.Port.Value}") : Tr.L($"Подключено, пинг {G.Net.Ping} мс", $"Connected, ping {G.Net.Ping} ms"), vis: On);
        Label(p, 40, 46, 920, 40, "", 28, Color.white, dyn: () => G.InRound ? Tr.L($"Идёт раунд {G.RoundNo}", $"Round {G.RoundNo} in progress") : G.ResultLine ?? Tr.L("Ожидание раунда", "Waiting for a round"), vis: On);
        Label(p, 40, 92, 920, 300, "", 23, TextCol, TextAnchor.UpperLeft, dyn: G.LobbyText, vis: On);
        Button(p, 40, 420, 560, 70, Tr.L("НАЧАТЬ РАУНД", "START ROUND"), () => { G.StartRoundHost(); Show(false); }, 30, () => On() && G.Net.IsHost && !G.InRound && G.InHouse);
        Button(p, 40, 420, 560, 70, Tr.L("ИГРАТЬ ЕЩЁ", "PLAY AGAIN"), () => G.EnterHouse(), 30, () => On() && !G.InRound && !G.InHouse);
        Button(p, 40, 420, 560, 70, Tr.L("ЗАКОНЧИТЬ РАУНД", "END ROUND"), () => G.UiEndRound(), 28, () => On() && G.Net.IsHost && G.InRound);
        Button(p, 620, 420, 340, 70, "", () => G.EndSession(), 26, On, dyn: () => G.Net.IsHost ? Tr.L("ОСТАНОВИТЬ СЕРВЕР", "STOP SERVER") : Tr.L("ОТКЛЮЧИТЬСЯ", "DISCONNECT"));
        Label(p, 40, 500, 920, 80, "", 19, Dim, TextAnchor.UpperLeft,
            dyn: () => G.InHouse ? Tr.L("Закрыть меню — Esc или X.  В игре меню открывается кнопкой «МУЛЬТИПЛЕЕР» в паузе (Esc) или клавишей ", "Close the menu: Esc or X.  In the game open it with the MULTIPLAYER button in the pause menu (Esc) or the key ") + Cfg.MenuKey.Value + "." : Tr.L("Ты в главном меню. Нажми «Играть ещё», чтобы вернуться в дом.", "You are in the main menu. Press Play again to return to the house."), vis: On);
    }

    void BuildSettingsPage(Transform p)
    {
        Label(p, 40, 0, 920, 30, "", 19, Dim, dyn: () => G.Session && !G.Net.IsHost ? Tr.L("Правила задаёт хост (у тебя только просмотр)", "The host sets the rules (view only)") : Tr.L("Правила раунда (у всех игроков применяются настройки хоста)", "Round rules (the host's settings apply to all players)"));
        var y = 36f;
        ValueRow(p, y, Tr.L("Дней у выжившего", "Survivor days"), () => Cfg.Days.Value.ToString(), () => G.Rule(() => Cfg.Days.Value = Math.Max(1, Cfg.Days.Value - 1)), () => G.Rule(() => Cfg.Days.Value = Math.Min(10, Cfg.Days.Value + 1))); y += 46;
        ValueRow(p, y, Tr.L("Перезарядка биты, с", "Bat cooldown, s"), () => Cfg.BatCooldown.Value.ToString("0.0"), () => G.Rule(() => Cfg.BatCooldown.Value = Mathf.Max(0.4f, Cfg.BatCooldown.Value - 0.2f)), () => G.Rule(() => Cfg.BatCooldown.Value = Mathf.Min(5f, Cfg.BatCooldown.Value + 0.2f))); y += 46;
        ValueRow(p, y, Tr.L("Дальность биты", "Bat range"), () => Cfg.BatRange.Value.ToString("0.0"), () => G.Rule(() => Cfg.BatRange.Value = Mathf.Max(1.5f, Cfg.BatRange.Value - 0.2f)), () => G.Rule(() => Cfg.BatRange.Value = Mathf.Min(8f, Cfg.BatRange.Value + 0.2f))); y += 46;
        ValueRow(p, y, Tr.L("Капкан раз в, с", "Trap every, s"), () => Cfg.TrapCooldown.Value.ToString("0"), () => G.Rule(() => Cfg.TrapCooldown.Value = Mathf.Max(3f, Cfg.TrapCooldown.Value - 1f)), () => G.Rule(() => Cfg.TrapCooldown.Value = Mathf.Min(120f, Cfg.TrapCooldown.Value + 1f))); y += 46;
        ValueRow(p, y, Tr.L("Капканов одновременно", "Traps at once"), () => Cfg.MaxTraps.Value.ToString(), () => G.Rule(() => Cfg.MaxTraps.Value = Math.Max(1, Cfg.MaxTraps.Value - 1)), () => G.Rule(() => Cfg.MaxTraps.Value = Math.Min(20, Cfg.MaxTraps.Value + 1))); y += 46;
        ValueRow(p, y, Tr.L("Время в капкане, с", "Time in a trap, s"), () => Cfg.TrapStunSeconds.Value.ToString("0"), () => G.Rule(() => Cfg.TrapStunSeconds.Value = Mathf.Max(1f, Cfg.TrapStunSeconds.Value - 1f)), () => G.Rule(() => Cfg.TrapStunSeconds.Value = Mathf.Min(20f, Cfg.TrapStunSeconds.Value + 1f))); y += 46;
        ValueRow(p, y, Tr.L("Слух раз в, с", "Listen every, s"), () => Cfg.ListenCooldown.Value.ToString("0"), () => G.Rule(() => Cfg.ListenCooldown.Value = Mathf.Max(5f, Cfg.ListenCooldown.Value - 5f)), () => G.Rule(() => Cfg.ListenCooldown.Value = Mathf.Min(180f, Cfg.ListenCooldown.Value + 5f))); y += 46;
        ValueRow(p, y, Tr.L("Слух длится, с", "Listen lasts, s"), () => Cfg.ListenDuration.Value.ToString("0.0"), () => G.Rule(() => Cfg.ListenDuration.Value = Mathf.Max(1f, Cfg.ListenDuration.Value - 0.5f)), () => G.Rule(() => Cfg.ListenDuration.Value = Mathf.Min(10f, Cfg.ListenDuration.Value + 0.5f))); y += 46;
        ValueRow(p, y, Tr.L("Скорость Гренни", "Granny speed"), () => $"{Cfg.GrannyWalkMul.Value:0.00} / {Cfg.GrannyRunMul.Value:0.00}", () => G.Rule(() => { Cfg.GrannyWalkMul.Value = Mathf.Max(0.5f, Cfg.GrannyWalkMul.Value - 0.05f); Cfg.GrannyRunMul.Value = Mathf.Max(0.4f, Cfg.GrannyRunMul.Value - 0.05f); }), () => G.Rule(() => { Cfg.GrannyWalkMul.Value = Mathf.Min(2f, Cfg.GrannyWalkMul.Value + 0.05f); Cfg.GrannyRunMul.Value = Mathf.Min(2f, Cfg.GrannyRunMul.Value + 0.05f); })); y += 46;
        ToggleRow(p, y, Tr.L("ИИ-Гренни в жеребьёвке", "AI Granny in the draw"), () => Cfg.AllowAiGranny.Value, v => G.Rule(() => Cfg.AllowAiGranny.Value = v), () => !G.Session || G.Net.IsHost); y += 46;
        ToggleRow(p, y, Tr.L("Шаги других выживших (звук)", "Footsteps of other survivors (sound)"), () => Cfg.AvatarFootsteps.Value, v => Cfg.AvatarFootsteps.Value = v); y += 46;
        ToggleRow(p, y, Tr.L("Админ-режим (для хоста)", "Admin mode (host only)"), () => Cfg.AdminPanel.Value, v => Cfg.AdminPanel.Value = v, () => !G.Session || G.Net.IsHost);
    }

    void BuildAdminPage(Transform p)
    {
        bool House() => G.InHouse;
        bool Round() => G.InHouse && G.InRound;
        Label(p, 40, 0, 920, 30, Tr.L("Тесты для хоста. Горячие клавиши работают, пока включён админ-режим.", "Host tests. Hotkeys work while admin mode is on."), 19, Dim);
        Label(p, 40, 40, 920, 60, Tr.L("Зайди в дом (новая игра), чтобы пользоваться админкой.", "Enter the house (new game) to use the admin tools."), 26, Color.yellow, vis: () => !House());
        Button(p, 40, 40, 450, 58, Tr.L("Соло-раунд: я ГРЕННИ", "Solo round: I am GRANNY"), () => { G.AdminSoloRound(true); Show(false); }, 24, House);
        Button(p, 510, 40, 450, 58, Tr.L("Соло: я ВЫЖИВШИЙ (ИИ)", "Solo: I am a SURVIVOR (AI)"), () => { G.AdminSoloRound(false); Show(false); }, 24, House);
        Button(p, 40, 108, 450, 58, Tr.L("[F2] Сменить роль", "[F2] Switch role"), () => { G.AdminSwitchRole(); Show(false); }, 24, Round);
        Button(p, 510, 108, 450, 58, Tr.L("[F3] Анжела вкл/выкл", "[F3] Angela on/off"), () => { G.AdminTogglePossess(); Show(false); }, 24, Round);
        Button(p, 40, 176, 450, 58, Tr.L("[F4] Добавить бота", "[F4] Add a bot"), () => { G.AdminAddBot(); Show(false); }, 24, House);
        Button(p, 510, 176, 450, 58, Tr.L("Убрать ботов", "Remove bots"), () => G.AdminRemoveBots(), 24, () => House() && G.BotCount > 0);
        Button(p, 40, 244, 450, 58, Tr.L("Боты: присесть/встать", "Bots: crouch/stand"), () => G.AdminBotCrouchToggle(), 24, () => House() && G.BotCount > 0);
        Button(p, 510, 244, 450, 58, Tr.L("Боты: спрятаться/выйти", "Bots: hide/come out"), () => G.AdminBotHideToggle(), 24, () => House() && G.BotCount > 0);
        Button(p, 40, 312, 450, 58, Tr.L("[F5] Капкан перед собой", "[F5] Trap ahead"), () => { G.AdminTrapAhead(); Show(false); }, 24, Round);
        Button(p, 510, 312, 450, 58, Tr.L("Поймать меня (тест дня)", "Catch me (day test)"), () => { G.AdminCatchMe(); Show(false); }, 24, () => Round() && G.AmSurvivor);
        Button(p, 40, 380, 450, 58, Tr.L("[F6] Телепорт в подвал Анжелы", "[F6] Teleport to Angela's cellar"), () => { G.AdminTeleportCellar(); Show(false); }, 22, House);
        Button(p, 510, 380, 450, 58, Tr.L("[F7] Телепорт на кровать", "[F7] Teleport to the bed"), () => { G.AdminTeleportSpawn(); Show(false); }, 22, House);
    }

    string FieldText(string v, int id)
    {
        var caret = Focus == id && (int)(Time.realtimeSinceStartup * 2) % 2 == 0 ? "|" : "";
        if (Focus != id && string.IsNullOrEmpty(v)) return Tr.L("<color=#887>нажми, чтобы ввести</color>", "<color=#887>click to type</color>");
        return (v ?? "") + caret;
    }


    void EnsureMenuButton()
    {
        if (_menuButton != null && _menuButtonHost != null) return;
        _menuButton = null;
        var holder = GameObject.Find("MenuButtonsHolder");
        if (holder == null) return;
        var rt = MakeRect(holder.transform, "GrannyMP_MultiplayerButton", -572f, -55f, 520f, 110f, false);
        var b = new Btn { Rt = rt, Click = () => Show(true), Plain = true, Normal = new Color(0, 0, 0, 0.001f), Hover = new Color(0, 0, 0, 0.001f) };
        b.Bg = MakeImage(rt, b.Normal, true);
        var label = MakeRect(rt, "t", 0, 0, 520, 110);
        b.Label = MakeText(label, Tr.L("Мультиплеер", "Multiplayer"), 70, Red, TextAnchor.MiddleCenter, false, false);
        b.Icon = GradientLetters(label, true);
        var hc = holder.GetComponent<Canvas>();
        if (hc != null && hc.renderMode != RenderMode.ScreenSpaceOverlay) b.Cam = hc.worldCamera;
        Plugin.L.LogInfo($"[menu] holder canvas mode {(hc != null ? hc.renderMode.ToString() : "?")}, cam {(b.Cam != null ? b.Cam.name : "none")}");
        _btns.RemoveAll(x => x.Rt == null);
        _btns.Add(b);
        _menuButton = b; _menuButtonHost = holder;
        Plugin.L.LogInfo("[menu] main menu button added");
    }


    public void Show(bool open)
    {
        if (!_built) Build();
        if (!_built || _failed) { G.Toast(Tr.L("Меню ещё загружается...", "The menu is still loading..."), 2, Color.yellow); return; }
        Open = open;
        Focus = 0;
        G.OnMenuShown(open);
        if (open) { RefreshVisibility(); FindBackdrop(); } // lay the panel out before its first rendered frame
        _back.gameObject.SetActive(open);
        _panel.gameObject.SetActive(open);
    }

    void FindBackdrop()
    {
        if (_backSprite) return;
        try
        {
            var s = GameRefs.FindAll<Sprite>(true).FirstOrDefault(x => x.name == "OptionMenuBackground2");
            if (s != null) { _backImg.sprite = s; _backImg.color = Color.white; _backSprite = true; }
        }
        catch (Exception e) { LogOnce("backdrop: " + e.Message); }
    }

    void RefreshVisibility()
    {
        if (_page == Tab.Admin && !G.AdminAllowed) _page = Tab.Play;
        foreach (var kv in _pages) if (kv.Value.activeSelf != (kv.Key == _page)) kv.Value.SetActive(kv.Key == _page);
        foreach (var b in _btns)
        {
            if (b.Rt == null || b == _pauseButton || b == _menuButton || b == _langButton || _social.Contains(b)) continue;
            var vis = b.Visible?.Invoke() ?? true;
            if (b.Rt.gameObject.activeSelf != vis) b.Rt.gameObject.SetActive(vis);
            if (vis && b.Dyn != null) b.Label.text = b.Dyn();
        }
        foreach (var l in _lbls)
        {
            var vis = l.Visible?.Invoke() ?? true;
            if (l.T.gameObject.activeSelf != vis) l.T.gameObject.SetActive(vis);
            if (vis && l.Dyn != null) l.T.text = l.Dyn();
        }
    }

    public void Toggle() => Show(!Open);

    public void Update()
    {
        if (!_built) Build();
        if (!_built || _failed) return;

        var scene = G.SceneName;
        if (scene == "Menu") { EnsureMenuButton(); FindBackdrop(); }

        // pause-menu button
        var pauseOpen = false;
        try { pauseOpen = G.InHouse && G.R.Fpc.inGameOptionMenu != null && G.R.Fpc.inGameOptionMenu.activeInHierarchy; } catch { }
        if (_pauseButton.Rt.gameObject.activeSelf != (pauseOpen && !Open)) _pauseButton.Rt.gameObject.SetActive(pauseOpen && !Open);

        if (Open)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { G.ArmPauseGuard(); if (Focus != 0) Focus = 0; else Show(false); }
            foreach (var kv in _pages) if (kv.Value.activeSelf != (kv.Key == _page)) kv.Value.SetActive(kv.Key == _page);
            if (_page == Tab.Admin && !G.AdminAllowed) _page = Tab.Play;
        }

        var langVis = Open || scene == "Menu";
        if (_langButton.Rt.gameObject.activeSelf != langVis) _langButton.Rt.gameObject.SetActive(langVis);

        var mouse = (Vector2)Input.mousePosition;
        var click = Input.GetMouseButtonDown(0);
        Btn clicked = null;
        foreach (var b in _btns)
        {
            if (b.Rt == null) continue;
            var inPanel = b != _pauseButton && b != _menuButton && b != _langButton && !_social.Contains(b);
            var vis = (!inPanel || Open) && (b.Visible?.Invoke() ?? true);
            if (b.Rt.gameObject.activeSelf != vis && inPanel) b.Rt.gameObject.SetActive(vis);
            if (!vis || !b.Rt.gameObject.activeInHierarchy) continue;
            var en = b.Enabled?.Invoke() ?? true;
            if (b.Dyn != null) b.Label.text = b.Dyn();
            var hover = en && RectTransformUtility.RectangleContainsScreenPoint(b.Rt, mouse, b.Cam);
            b.Bg.color = !en ? BtnOff : hover ? b.Hover : b.Normal;
            if (b.Icon != null) b.Icon.color = !en ? Dim : hover ? RedHi : Red;
            else if (b.Plain) b.Label.color = hover ? RedHi : Red;
            else b.Label.color = en ? TextCol : Dim;
            if (hover && click && clicked == null) clicked = b;
        }
        // the main menu button dies with its scene, so the icons have their own check
        var socialShow = scene == "Menu" && !Open;
        try { socialShow = socialShow && GameObject.Find("ContinueButton") == null; } catch { }
        foreach (var soc in _social) if (soc.Rt.gameObject.activeSelf != socialShow) soc.Rt.gameObject.SetActive(socialShow);
        if (_menuButton != null && _menuButton.Rt != null)
        {
            // hide our main menu button while the game's options screen (difficulty etc.) is shown
            var optionsOpen = false;
            try { optionsOpen = GameObject.Find("ContinueButton") != null; } catch { }
            var show = scene == "Menu" && !Open && !optionsOpen;
            if (_menuButton.Rt.gameObject.activeSelf != show) _menuButton.Rt.gameObject.SetActive(show);
        }
        if (Open)
            foreach (var l in _lbls)
            {
                var vis = l.Visible?.Invoke() ?? true;
                if (l.T.gameObject.activeSelf != vis) l.T.gameObject.SetActive(vis);
                if (vis && l.Dyn != null) l.T.text = l.Dyn();
            }

        if (clicked != null)
        {
            Focus = 0; // text fields re-take focus inside their own click handler
            try { clicked.Click?.Invoke(); }
            catch (Exception e) { Plugin.L.LogError("[menu] click: " + e); }
        }
    }
}
