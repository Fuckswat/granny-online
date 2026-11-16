using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyMP;

public sealed partial class Game
{
    sealed class ToastMsg { public string Text; public float Until; public Color Color; public bool Big; }
    readonly List<ToastMsg> _toasts = new();

    MpMenu _menu;
    public MpMenu Menu => _menu ??= new MpMenu(this);
    public bool MenuOpen => _menu != null && _menu.Open;
    public string NameField, IpField;
    public string SceneName => _scene;
    GUIStyle _sBig, _sMid, _sSmall, _sCenter, _sItem;

    public void Toast(string text, float seconds, Color color, bool big = false)
    {
        Plugin.L.LogInfo("[msg] " + text.Replace("\n", " | "));
        _toasts.Add(new ToastMsg { Text = text, Until = Time.time + seconds, Color = Color.white, Big = big });
        while (_toasts.Count > 5) _toasts.RemoveAt(0);
    }

    void ToggleMenu() => Menu.Toggle();

    bool _resChecked;

    /// <summary>The game remembers 1024x768 in the registry: frames on the sides and a blurry picture.</summary>
    void EnsureResolution()
    {
        if (_resChecked || !Cfg.NativeResolution.Value || Plugin.Windowed) return;
        _resChecked = true;
        try
        {
            var d = Display.main;
            int w = d.systemWidth, h = d.systemHeight;
            if (w <= 0 || h <= 0 || (Screen.width == w && Screen.height == h)) return;
            Plugin.L.LogInfo($"[screen] {Screen.width}x{Screen.height} -> {w}x{h}");
            Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        }
        catch (Exception e) { Plugin.L.LogWarning("[screen] " + e.Message); }
    }

    float _goodSens, _mouseDeadSince;
    bool _menuFlagNormal;

    /// <summary>The game's pause and hide scripts zero the mouse look; if a round starts meanwhile nothing brings it back.</summary>
    void KeepMouseAlive()
    {
        if (!InRound || R?.Fpc == null || MenuOpen || IsPauseMenuOpen() || Executing || _spectating || IsHiding()) { _mouseDeadSince = 0; return; }
        var ml = R.Fpc.m_MouseLook;
        if (ml == null) return;
        if (AmGranny && !R.Fpc.enabled && _lockUntil <= Now) { R.Fpc.enabled = true; Plugin.L.LogWarning("[mouse] controller was off"); }
        if (ml.XSensitivity > 0.01f) { _goodSens = ml.XSensitivity; _mouseDeadSince = 0; return; }
        if (_mouseDeadSince <= 0) { _mouseDeadSince = Now; return; }
        if (Now - _mouseDeadSince < 0.5f) return;
        var s = _goodSens > 0.01f ? _goodSens : R.Fpc.mouseSens > 0.01f ? R.Fpc.mouseSens : 2f;
        ml.XSensitivity = s; ml.YSensitivity = s;
        Plugin.L.LogWarning($"[mouse] sensitivity was 0, restored {s:F2}");
    }

    int _pauseGuardFrames;

    /// <summary>Esc that closes our menu also reaches the game, which would open its pause menu.</summary>
    public void ArmPauseGuard() { if (!IsPauseMenuOpen()) _pauseGuardFrames = 3; }

    void UpdatePauseGuard()
    {
        if (_pauseGuardFrames <= 0) return;
        _pauseGuardFrames--;
        if (IsPauseMenuOpen()) { _pauseGuardFrames = 0; CloseNativePause(); }
    }

    bool _pauseHeld;

    /// <summary>Online the pause menu must not stop time: the character just stands still while it is open.</summary>
    void UpdateSessionPause()
    {
        if (!Session || !InHouse) return;
        var open = IsPauseMenuOpen();
        if (open)
        {
            if (Time.timeScale < 1f) Time.timeScale = 1f;
            if (!_pauseHeld) { _pauseHeld = true; try { R.Fpc.enabled = false; } catch { } }
        }
        else if (_pauseHeld)
        {
            _pauseHeld = false;
            if (_lockUntil <= Now && !_spectating && !Executing) try { R.Fpc.enabled = true; } catch { }
        }
    }

    public void CloseNativePause()
    {
        try
        {
            if (!IsPauseMenuOpen()) return;
            try { foreach (var c in GameRefs.FindAll<closeOptionMenuIG>(true)) { c.TaskOnClick(); break; } }
            catch (Exception e) { Plugin.L.LogWarning("[pause] native close: " + e.Message); }
            if (IsPauseMenuOpen()) R.Fpc.inGameOptionMenu.SetActive(false);
            R.Fpc.menuEnabled = _menuFlagNormal; // the game's close handler failed before resetting it
            Time.timeScale = 1f;
            R.Fpc.enableMouse();
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
        catch (Exception e) { Plugin.L.LogWarning("[pause] " + e.Message); }
    }

    public void OnMenuShown(bool open)
    {
        NameField ??= Cfg.PlayerName.Value;
        IpField ??= Cfg.LastIp.Value;
        try { if (R?.Fpc != null) { if (open) R.Fpc.disableMouse(); else R.Fpc.enableMouse(); } } catch { }
        if (!open && InHouse && !IsPauseMenuOpen()) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    }

    bool IsPauseMenuOpen()
    {
        try { return R?.Fpc?.inGameOptionMenu != null && R.Fpc.inGameOptionMenu.activeInHierarchy; } catch { return false; }
    }

    public void UiHost()
    {
        Cfg.PlayerName.Value = Sanitize(NameField);
        StartHost();
        if (Session) EnterHouse();
    }

    /// <summary>One button is enough: no need to go through the game's own new-game screens.</summary>
    public void EnterHouse()
    {
        if (Menu.Open) Menu.Show(false);
        if (!IsHouse) SceneManager.LoadScene("Scene");
    }
    public void UiJoin()
    {
        Cfg.PlayerName.Value = Sanitize(NameField);
        if (string.IsNullOrWhiteSpace(IpField)) { Toast(Tr.L("Введи IP хоста", "Enter the host IP"), 3, Color.yellow); return; }
        StartClient(IpField.Trim());
    }
    public void UiEndRound() => EndRoundHost(0, Tr.Both("Хост остановил раунд", "The host stopped the round"));

    public string LobbyText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in Players.Values.OrderBy(p => p.Id))
        {
            var role = !InRound ? "" : p.Role == Role.Granny ? Tr.L("  —  <color=#ff5050>ГРЕННИ</color>", "  -  <color=#ff5050>GRANNY</color>") : p.Escaped ? Tr.L("  —  сбежал", "  -  escaped") : !p.Alive ? Tr.L("  —  выбыл", "  -  out") : Tr.L($"  —  выживший, день {p.Day}/{Cfg.Days.Value}", $"  -  survivor, day {p.Day}/{Cfg.Days.Value}");
            var where = p.InGame ? "" : Tr.L("  <color=#998>[в меню]</color>", "  <color=#998>[in menu]</color>");
            sb.Append("• ").Append(p.Name).Append(p.Id == Net.LocalId ? Tr.L(" (ты)", " (you)") : "").Append(where).Append(role).Append('\n');
        }
        if (InRound && GrannyId < 0) sb.Append(Tr.L("• <color=#ff5050>Гренни — ИИ (компьютер)</color>\n", "• <color=#ff5050>Granny: AI (computer)</color>\n"));
        return sb.ToString();
    }

    bool _inputStringBroken;

    /// <summary>GUI.TextField is broken in this build (TextEditor can't be unstripped), so text input is done by hand.</summary>
    void UpdateTextInput()
    {
        if (!MenuOpen || Menu.Focus == 0) return;
        var field = Menu.Focus;
        var s = field == 1 ? NameField ?? "" : IpField ?? "";
        var max = field == 1 ? 20 : 64;
        string typed = null;
        if (!_inputStringBroken)
        {
            try { typed = Input.inputString; }
            catch (Exception e) { _inputStringBroken = true; Plugin.L.LogWarning("[gui] inputString: " + e.Message); }
        }
        if (_inputStringBroken)
        {
            var sb = new System.Text.StringBuilder();
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            for (var k = KeyCode.A; k <= KeyCode.Z; k++) if (Input.GetKeyDown(k)) sb.Append(shift ? (char)('A' + (k - KeyCode.A)) : (char)('a' + (k - KeyCode.A)));
            for (var k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) if (Input.GetKeyDown(k)) sb.Append((char)('0' + (k - KeyCode.Alpha0)));
            for (var k = KeyCode.Keypad0; k <= KeyCode.Keypad9; k++) if (Input.GetKeyDown(k)) sb.Append((char)('0' + (k - KeyCode.Keypad0)));
            if (Input.GetKeyDown(KeyCode.Period) || Input.GetKeyDown(KeyCode.KeypadPeriod)) sb.Append('.');
            if (Input.GetKeyDown(KeyCode.Minus)) sb.Append(shift ? '_' : '-');
            if (Input.GetKeyDown(KeyCode.Space)) sb.Append(' ');
            if (Input.GetKeyDown(KeyCode.Backspace)) sb.Append('\b');
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) sb.Append('\n');
            typed = sb.ToString();
        }
        if (string.IsNullOrEmpty(typed)) return;
        foreach (var ch in typed)
        {
            if (ch == '\b') { if (s.Length > 0) s = s[..^1]; }
            else if (ch == '\n' || ch == '\r') { Menu.Focus = 0; break; }
            else if (!char.IsControl(ch) && s.Length < max) s += ch;
        }
        if (field == 1) NameField = s; else IpField = s;
    }

    void UpdateMenuCursor()
    {
        if (!MenuOpen) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void InitStyles()
    {
        if (_sBig != null) return;
        GUIStyle Make(int size, TextAnchor a, bool wrap, FontStyle fs = FontStyle.Normal)
        {
            var s = new GUIStyle { fontSize = size, alignment = a, wordWrap = wrap, fontStyle = fs };
            s.normal.textColor = Color.white;
            s.font = Menu.UiFont;
            return s;
        }
        _sBig = Make(38, TextAnchor.MiddleCenter, true, FontStyle.BoldAndItalic);
        _sMid = Make(25, TextAnchor.MiddleCenter, true, FontStyle.Italic);
        _sSmall = Make(18, TextAnchor.UpperLeft, true, FontStyle.Italic);
        _sCenter = Make(20, TextAnchor.MiddleCenter, false, FontStyle.Italic);
        _sItem = Make(25, TextAnchor.MiddleLeft, false, FontStyle.Italic);
    }

    static void Shadow(Rect r, string text, GUIStyle s, Color c)
    {
        var old = GUI.color;
        GUI.color = new Color(0, 0, 0, c.a * 0.85f);
        GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, s);
        GUI.color = c;
        GUI.Label(r, text, s);
        GUI.color = old;
    }

    static void Fill(Rect r, Color c)
    {
        var old = GUI.color; GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    Camera ViewCam => Camera.main ?? R?.MainCam;

    bool ToScreen(Vector3 world, out Vector2 gui)
    {
        gui = default;
        var cam = ViewCam;
        if (cam == null) return false;
        var sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0) return false;
        gui = new Vector2(sp.x, Screen.height - sp.y) / UiScale;
        return true;
    }

    public void OnGUI()
    {
        InitStyles();
        var w = Screen.width / UiScale; var h = Screen.height / UiScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(UiScale, UiScale, 1f));

        if (FadeAlpha > 0.01f) Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, FadeAlpha));
        if (FlashAlpha > 0.01f) Fill(new Rect(0, 0, w, h), new Color(0.7f, 0, 0, FlashAlpha * 0.6f));
        if (Session && AmGranny && Listening) Fill(new Rect(0, 0, w, h), new Color(1f, 0.2f, 0.7f, 0.07f));

        if (Executing) { DrawExecution(w, h); return; }
        if (AmGranny && _blindUntil > Now && _downUntil <= Now)
        {
            Fill(new Rect(0, 0, w, h), Color.black);
            Line(0, h * 0.45f, w, Tr.L($"Перец в глазах! Ты ничего не видишь ещё {Mathf.Ceil(_blindUntil - Now):F0} с", $"Pepper in your eyes! You see nothing for {Mathf.Ceil(_blindUntil - Now):F0} s"), _sBig, 1f, 48);
            DrawToasts(w, h);
            return;
        }
        if (AmGranny && _downUntil > Now)
        {
            var back = Tr.L($"Ты лежишь. Встанешь через {Mathf.Ceil(_downUntil - Now):F0} с", $"You are down. Back in {Mathf.Ceil(_downUntil - Now):F0} s");
            if (!DownViewOn)
            {
                Fill(new Rect(0, 0, w, h), Color.black);
                Line(0, h * 0.45f, w, back, _sBig, 1f, 48);
                return;
            }
            Line(0, h * 0.05f, w, back, _sBig, 1f, 48);
            Line(0, h * 0.05f + 50, w, DownFlying
                ? Tr.L("Полёт: WASD, мышь, пробел / Ctrl — вверх / вниз, Shift — быстрее", "Flying: WASD, mouse, Space / Ctrl - up / down, Shift - faster")
                : Tr.L($"Глазами игрока: {DownWatchName}", $"Through the eyes of {DownWatchName}"), _sMid, 1f, 34);
            Line(0, h * 0.94f, w, DownFlying
                ? Tr.L("ПКМ — смотреть глазами игрока", "RMB - watch through a player's eyes")
                : Tr.L("ЛКМ / пробел — следующий игрок · ПКМ — летать по карте", "LMB / Space - next player · RMB - fly around the map"), _sMid, 0.85f, 34);
            DrawToasts(w, h);
            return;
        }
        if (IsPauseMenuOpen()) return; // the game's pause menu stays clean
        if (Session && InHouse) DrawWorldMarkers();
        DrawStatus(w, h);
        DrawToasts(w, h);
        if (MenuOpen) UpdateMenuCursor();
    }

    /// <summary>The HUD is laid out for 1080 lines and scaled to the screen.</summary>
    static float UiScale => Mathf.Max(0.5f, Screen.height / 1080f);

    void DrawWorldMarkers()
    {
        var showNames = !InRound || !AmGranny;
        foreach (var p in Players.Values)
        {
            if (p.Id == Net.LocalId || p.Avatar?.Go == null || !p.HasState) continue;
            var xray = AmGranny && p.Avatar.XrayOn;
            if (xray && !p.Avatar.HasXrayMesh && ToScreen(p.SmoothPos + Vector3.up * (1.0f * U), out var g))
            {
                Fill(new Rect(g.x - 14, g.y - 40, 28, 80), new Color(1f, 0.25f, 0.75f, 0.55f));
                Shadow(new Rect(g.x - 80, g.y - 70, 160, 26), "● " + p.Name, _sCenter, new Color(1f, 0.4f, 0.85f));
            }
            if (showNames && !p.OutOfSight && p.NameVisible && p.Avatar.Go.activeSelf && ToScreen(p.SmoothPos + Vector3.up * (AvatarHeight * 1.12f), out var n))
            {
                var dist = R != null ? Vector3.Distance(R.CamPos, p.SmoothPos) / U : 0;
                if (dist < 25) Shadow(new Rect(n.x - 100, n.y - 12, 200, 24), p.Name, _sCenter, new Color(1, 1, 1, Mathf.Clamp01(1.4f - dist / 25f)));
            }
        }
        if (AmGranny) DrawNoise(Screen.width / UiScale, Screen.height / UiScale);
    }

    // ---- HUD: goal on top, abilities at the bottom, cards for lobby / role / result ----

    float _roleCardUntil, _roleCardFrom, _resultUntil;
    string _resultText, _grannyLabel;
    bool _devHelp;
    public string ResultLine => _resultText;

    static void Box(Rect r, float a = 0.6f)
    {
        Fill(r, new Color(0.1f, 0.1f, 0.1f, a));
        var c = new Color(1, 1, 1, 0.8f);
        Fill(new Rect(r.x, r.y, r.width, 2), c); Fill(new Rect(r.x, r.yMax - 2, r.width, 2), c);
        Fill(new Rect(r.x, r.y, 2, r.height), c); Fill(new Rect(r.xMax - 2, r.y, 2, r.height), c);
    }

    void Line(float x, float y, float w, string text, GUIStyle s, float a = 1f, float h = 34) =>
        Shadow(new Rect(x, y, w, h), text, s, new Color(1, 1, 1, a));

    void DrawStatus(float sw, float sh)
    {
        if (_devPhoto) return;
        var mode = Net.IsHost ? Tr.L("ХОСТ", "HOST") : Session ? Tr.L($"клиент, пинг {Net.Ping} мс", $"client, ping {Net.Ping} ms") : Net.Connecting ? Tr.L("подключение...", "connecting...") : Tr.L("оффлайн", "offline");
        Shadow(new Rect(14, 8, 1000, 26), $"GrannyMP {Plugin.Version} | {mode} | {Cfg.MenuKey.Value} - " + Tr.L("меню", "menu"), _sSmall, new Color(1, 1, 1, 0.55f));
        if (!Session) return;
        if (IsHouse && R == null) { Line(0, sh - 60, sw, Tr.L("Просыпаешься...", "Waking up..."), _sCenter, 0.6f); return; }
        if (!InHouse) return;
        if (!InRound) { DrawLobby(sw, sh); return; }

        DrawGoal(sw);
        if (AmGranny) DrawAbilities(sw, sh);
        else if (AmSurvivor) { DrawDays(sw, sh); DrawSurvivorAbilities(sw, sh); }
        DrawRoster(sw);
        if (Now < _roleCardUntil) DrawRoleCard(sw, sh);
        if (Input.GetKey(KeyCode.Tab) || _devHelp) DrawHelp(sw, sh);
    }

    void DrawLobby(float sw, float sh)
    {
        if (Now < _resultUntil) Line(0, 40, sw, _resultText, _sBig, 1f, 48);
        var list = 34f;
        foreach (var p in Players.Values.OrderBy(p => p.Id))
        {
            var text = p.Name + (p.Id == Net.LocalId ? Tr.L(" (ты)", " (you)") : "") + (p.InGame ? "" : Tr.L(" - загружается", " - loading"));
            Shadow(new Rect(sw - 290, list, 280, 24), text, _sSmall, new Color(1, 1, 1, 0.8f));
            list += 24;
        }
        Line(0, sh - 92, sw, Net.IsHost ? Tr.L("Enter: начать раунд", "Enter: start the round") : Tr.L("Ждём, когда хост начнёт раунд", "Waiting for the host to start"), _sMid);
        if (!Net.IsHost) return;
        var ip = NetSession.FriendIp();
        Line(0, sh - 54, sw, Tr.L($"Твой IP для друзей: {ip}", $"Your IP for friends: {ip}"), _sCenter, 0.8f, 28);
        if (!NetSession.FriendIpIsOverlay) Line(0, sh - 28, sw, Tr.L("Друг в другой сети? Установите оба Radmin VPN", "Friend on another network? Both install Radmin VPN"), _sCenter, 0.5f, 26);
    }

    void DrawGoal(float sw)
    {
        var left = Players.Values.Count(p => p.ActiveSurvivor);
        var text = AmGranny ? Tr.L($"Цель: поймай всех выживших (осталось {left})", $"Goal: catch all survivors ({left} left)")
            : !AmSurvivor || !Me.Alive ? Tr.L("Ты выбыл. ЛКМ — следующий игрок", "You are out. LMB - next player")
            : Me.Escaped ? Tr.L("Ты сбежал!", "You escaped!")
            : Tr.L("Цель: найди предметы, открой дверь и сбеги из дома", "Goal: find the items, unlock the door and escape");
        Fill(new Rect(sw / 2 - 400, 14, 800, 36), new Color(0, 0, 0, 0.4f));
        Line(0, 16, sw, text, _sCenter);
        Line(0, 54, sw, Tr.L("Tab — подсказка", "Tab - help"), _sCenter, 0.5f, 26);
    }

    void Ability(float x, float y, string key, string name, float cd, float total, bool active, float activeLeft = 0, string stateOverride = null)
    {
        const float w = 190, h = 74;
        var r = new Rect(x, y, w, h);
        Box(r, 0.55f);
        if (active) Fill(new Rect(x + 2, y + 2, w - 4, h - 4), new Color(1f, 0.25f, 0.7f, 0.35f));
        else if (cd > 0) Fill(new Rect(x + 2, y + 2, (w - 4) * Mathf.Clamp01(cd / Mathf.Max(0.01f, total)), h - 4), new Color(0, 0, 0, 0.6f));
        var a = cd > 0 && !active ? 0.55f : 1f;
        Line(x, y + 6, w, key, _sMid, a, 32);
        var state = stateOverride ?? (active ? $"{activeLeft:F1}" : cd > 0 ? $"{Mathf.Ceil(cd):F0}" : Tr.L("готово", "ready"));
        Line(x, y + 40, w, name + ": " + state, _sCenter, a, 28);
    }

    void DrawAbilities(float sw, float sh)
    {
        var count = Possess ? 2 : 4;
        var x = sw / 2 - (count * 190f + (count - 1) * 12f) / 2; var y = sh - 94f;
        Ability(x, y, Tr.L("ЛКМ", "LMB"), Possess ? Tr.L("Укус", "Bite") : Tr.L("Бита", "Bat"), BatCd, Cfg.BatCooldown.Value, false);
        x += 202;
        if (!Possess)
        {
            Ability(x, y, Cfg.TrapKey.Value.ToString(), Tr.L($"Капкан {MyTrapCount()}/{Cfg.MaxTraps.Value}", $"Trap {MyTrapCount()}/{Cfg.MaxTraps.Value}"), TrapCd, Cfg.TrapCooldown.Value, false);
            x += 202;
            Ability(x, y, Cfg.CheckKey.Value.ToString(), Tr.L("Укрытие", "Check"), CheckCd, CheckCooldown, false);
            x += 202;
        }
        Ability(x, y, Cfg.ListenKey.Value.ToString(), Tr.L("Слух", "Listen"), ListenCd, Cfg.ListenCooldown.Value, Listening, ListenLeft);
        if (Possess) Line(0, y - 44, sw, Tr.L("Ты — Анжела: кто-то в подвале", "You are Angela: someone is in the cellar"), _sMid);
        if (SomeoneWaking) Line(0, y - 80, sw, Tr.L("Игрок просыпается, подожди...", "A player is waking up, wait..."), _sMid);
    }

    void DrawSurvivorAbilities(float sw, float sh)
    {
        if (!Me.Alive || Me.Escaped || _spectating) return;
        float x = 24, y = sh - 94f;
        Ability(x, y, Cfg.BoostKey.Value.ToString(), Tr.L("Скорость", "Speed"), BoostCd, Cfg.BoostDuration.Value + Cfg.BoostCooldown.Value, Boosting, BoostLeft);
        x += 202;
        var max = Mathf.Max(0, Cfg.PunchCharges.Value);
        var none = PunchesLeft <= 0;
        Ability(x, y, Cfg.PunchKey.Value.ToString(), Tr.L($"Кулаки {PunchesLeft}/{max}", $"Fists {PunchesLeft}/{max}"), none ? 1f : PunchCd, none ? 1f : 1.2f, false, 0, none ? Tr.L("нет", "none") : null);
    }

    void DrawDays(float sw, float sh)
    {
        if (!Me.Alive || Me.Escaped) return;
        var days = Cfg.Days.Value;
        const float pip = 30, gap = 10;
        var x = sw / 2 - (days * pip + (days - 1) * gap) / 2; var y = sh - 62f;
        for (var i = 1; i <= days; i++)
        {
            var r = new Rect(x, y, pip, pip);
            Box(r, 0.35f);
            if (i >= Me.Day) Fill(new Rect(x + 5, y + 5, pip - 10, pip - 10), new Color(1, 1, 1, 0.85f));
            x += pip + gap;
        }
        var line = Tr.L($"День {Me.Day} из {days}", $"Day {Me.Day} of {days}");
        if (_lockUntil > Now && _caughtT0 <= 0 && _wakeUntil <= 0) line = Tr.L($"Ты в капкане: {_lockUntil - Now:F1} с", $"You are trapped: {_lockUntil - Now:F1} s");
        Line(0, y - 38, sw, line, _sMid);
    }

    void DrawRoster(float sw)
    {
        var ry = 34f;
        foreach (var p in Players.Values.OrderBy(p => p.Id))
        {
            var st = p.Role == Role.Granny ? Tr.L("Гренни", "Granny") : !p.InGame ? Tr.L("не в доме", "not in the house") : p.Escaped ? Tr.L("сбежал", "escaped") : !p.Alive ? Tr.L("выбыл", "out") : Tr.L($"день {p.Day}", $"day {p.Day}");
            Shadow(new Rect(sw - 290, ry, 280, 24), $"{p.Name}: {st}", _sSmall, new Color(1, 1, 1, 0.75f));
            ry += 24;
        }
        if (GrannyId < 0) Shadow(new Rect(sw - 290, ry, 280, 24), Tr.L("Гренни: ИИ", "Granny: AI"), _sSmall, new Color(1, 1, 1, 0.75f));
    }

    static Texture2D _iconGranny, _iconSurvivor, _cardBack;
    bool _rolePlaying;
    System.Runtime.InteropServices.GCHandle _rolePin;

    /// <summary>Role art lives inside the DLL as raw RGBA (src/art, made by tools/roleart): LoadImage is span-broken in this Unity.</summary>
    public static unsafe Texture2D RoleIcon(string file, int width = 768, int height = 768)
    {
        var raw = new byte[width * height * 4];
        using (var src = typeof(Game).Assembly.GetManifestResourceStream(file))
        using (var z = new System.IO.Compression.DeflateStream(src, System.IO.Compression.CompressionMode.Decompress))
        {
            var at = 0;
            while (at < raw.Length) { var n = z.Read(raw, at, raw.Length - at); if (n <= 0) break; at += n; }
        }
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        fixed (byte* p = raw) tex.LoadRawTextureData((IntPtr)p, raw.Length);
        tex.Apply();
        tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
        tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        UnityEngine.Object.DontDestroyOnLoad(tex);
        return tex;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);
    const uint SndAsyncMemory = 0x1 | 0x2 | 0x4; // SND_ASYNC | SND_NODEFAULT | SND_MEMORY
    const int WavHeader = 44;

    /// <summary>The whole WAV from the DLL. AudioClip.SetData and the audio web loader do not exist in this Unity build, so Windows plays it.</summary>
    static byte[] RoleWav(string file)
    {
        using var src = typeof(Game).Assembly.GetManifestResourceStream(file);
        using var z = new System.IO.Compression.DeflateStream(src, System.IO.Compression.CompressionMode.Decompress);
        using var mem = new MemoryStream();
        z.CopyTo(mem);
        return mem.ToArray();
    }

    void StopRoleSound()
    {
        if (!_rolePlaying) return;
        _rolePlaying = false;
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_rolePin.IsAllocated) _rolePin.Free();
    }

    void StartRoleSound()
    {
        StopRoleSound();
        var wav = RoleWav(AmGranny ? "granny_sound.bin" : "survivor_sound.bin");
        var volume = Mathf.Clamp01(AudioListener.volume) * Mathf.Clamp01(Cfg.RoleSoundVolume.Value); // the game's volume slider x our own (the music was too loud)
        for (var i = WavHeader; i + 1 < wav.Length; i += 2)
        {
            var v = (int)(BitConverter.ToInt16(wav, i) * volume);
            wav[i] = (byte)v; wav[i + 1] = (byte)(v >> 8);
        }
        _rolePin = System.Runtime.InteropServices.GCHandle.Alloc(wav, System.Runtime.InteropServices.GCHandleType.Pinned);
        _rolePlaying = PlaySound(_rolePin.AddrOfPinnedObject(), IntPtr.Zero, SndAsyncMemory);
        if (!_rolePlaying) _rolePin.Free();
        Plugin.L.LogInfo($"[role] sound {wav.Length} bytes, volume {volume:F2}, started={_rolePlaying}");
    }

    const float RoleCardTime = 9.6f; // as long as the role sounds

    void ShowRoleCard(float time = RoleCardTime)
    {
        _roleCardFrom = Now; _roleCardUntil = Now + time;
        if (AmGranny || (AmSurvivor && Me.Alive))
            try { StartRoleSound(); } catch (Exception e) { Plugin.L.LogWarning("[role] " + e.Message); }
    }

    /// <summary>The sound leaves with the card.</summary>
    void UpdateRoleSound()
    {
        if (_rolePlaying && (Now >= _roleCardUntil || !Session)) StopRoleSound();
    }

    /// <summary>Grey screen behind the role art: the black scribbles need something lighter than the dark house.</summary>
    static Texture2D CardBack()
    {
        if (_cardBack != null) return _cardBack;
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x - size / 2f + 0.5f, (y - size * 0.62f) * 1.1f).magnitude / (size * 0.7f);
                tex.SetPixel(x, y, Color.Lerp(new Color(0.36f, 0.38f, 0.43f), new Color(0.07f, 0.07f, 0.09f), Mathf.SmoothStep(0, 1, d)));
            }
        tex.Apply(); tex.wrapMode = TextureWrapMode.Clamp;
        UnityEngine.Object.DontDestroyOnLoad(tex);
        return _cardBack = tex;
    }

    void DrawRoleCard(float sw, float sh)
    {
        var shown = Now - _roleCardFrom;
        var ev = Event.current.type;
        if (shown > 1.5f && (ev == EventType.KeyDown || ev == EventType.MouseDown)) _roleCardUntil = Mathf.Min(_roleCardUntil, Now + 0.5f);
        var a = Mathf.Min(Mathf.Clamp01(_roleCardUntil - Now), Mathf.Clamp01(shown / 0.3f));
        _iconGranny ??= RoleIcon("granny.bin");
        _iconSurvivor ??= RoleIcon("survivor.bin");
        string title; var items = new List<string>(); var pip = Color.white; var both = false;
        if (AmGranny)
        {
            title = Tr.L("ТЫ ГРЕННИ", "YOU ARE GRANNY");
            pip = new Color(1f, 0.1f, 0.1f);
            items.Add(Tr.L("Поймай всех выживших: удар битой на ЛКМ отнимает день", "Catch all survivors: a bat hit on LMB costs a day"));
            items.Add(Tr.L($"{Cfg.TrapKey.Value}: поставить капкан, раз в {Cfg.TrapCooldown.Value:F0} с", $"{Cfg.TrapKey.Value}: place a bear trap, every {Cfg.TrapCooldown.Value:F0} s"));
            items.Add(Tr.L($"{Cfg.ListenKey.Value}: слух, {Cfg.ListenDuration.Value:F0} с видишь всех сквозь стены, раз в {Cfg.ListenCooldown.Value:F0} с", $"{Cfg.ListenKey.Value}: listen, see everyone through walls for {Cfg.ListenDuration.Value:F0} s, every {Cfg.ListenCooldown.Value:F0} s"));
            items.Add(Tr.L($"{Cfg.CheckKey.Value}: заглянуть в кровать, гроб или машину, где прячутся на R", $"{Cfg.CheckKey.Value}: look into a bed, coffin or the car (R hiding places)"));
        }
        else if (AmSurvivor && Me.Alive)
        {
            title = Tr.L("ТЫ ВЫЖИВШИЙ", "YOU ARE A SURVIVOR");
            items.Add(Tr.L("Найди предметы и открой дверь, чтобы сбежать из дома", "Find the items and unlock the door to escape the house"));
            items.Add(Tr.L("Не шуми: Гренни слышит скрип пола, упавшие вещи и выстрелы", "Stay quiet: Granny hears creaky floors, dropped items and shots"));
            items.Add(Tr.L("Гренни рядом? Прячься под кровать или в шкаф: клавиша R", "Granny is close? Hide under a bed or in a wardrobe: R key"));
            items.Add(Tr.L($"Удар битой отнимает день. Всего дней: {Cfg.Days.Value}", $"A bat hit costs a day. Days in total: {Cfg.Days.Value}"));
            items.Add(Tr.L($"{Cfg.BoostKey.Value}: гиперскорость x{Cfg.BoostMul.Value:0.#} на {Cfg.BoostDuration.Value:F0} с,  {Cfg.PunchKey.Value}: кулаки ({Cfg.PunchCharges.Value} за раунд, {Cfg.PunchChance.Value}% свалить Гренни)",
                           $"{Cfg.BoostKey.Value}: hyper speed x{Cfg.BoostMul.Value:0.#} for {Cfg.BoostDuration.Value:F0} s,  {Cfg.PunchKey.Value}: fists ({Cfg.PunchCharges.Value} per round, {Cfg.PunchChance.Value}% to knock Granny down)"));
        }
        else
        {
            title = Tr.L("ТЫ ЗРИТЕЛЬ", "YOU ARE SPECTATING");
            both = true;
        }
        var back = GUI.color; GUI.color = new Color(1, 1, 1, a);
        GUI.DrawTexture(new Rect(0, 0, sw, sh), CardBack());
        var size = Mathf.Min(sh * 0.6f, 650f); var y = sh * 0.02f;
        if (both)
        {
            var half = size * 0.8f;
            GUI.DrawTexture(new Rect(sw / 2 - half - 20, y + size - half, half, half), _iconSurvivor);
            GUI.DrawTexture(new Rect(sw / 2 + 20, y + size - half, half, half), _iconGranny);
        }
        else GUI.DrawTexture(new Rect((sw - size) / 2, y, size, size), AmGranny ? _iconGranny : _iconSurvivor);
        GUI.color = back;
        y += size - 6;
        Line(0, y, sw, title, _sBig, a, 56); y += 66;
        const float w = 860;
        var x = (sw - w) / 2;
        foreach (var item in items)
        {
            pip.a = a; Fill(new Rect(x, y + 14, 10, 10), pip);
            Line(x + 26, y, w - 26, item, _sItem, a, 38);
            y += 42;
        }
        if (!AmGranny) { y += 6; Line(0, y, sw, Tr.L($"Гренни: {_grannyLabel}, скорость x{GrannySpeedRoll:0.00}", $"Granny: {_grannyLabel}, speed x{GrannySpeedRoll:0.00}"), _sCenter, 0.75f * a, 30); y += 32; }
        else { y += 6; Line(0, y, sw, Tr.L($"Твоя скорость в этом раунде: x{GrannySpeedRoll:0.00}", $"Your speed this round: x{GrannySpeedRoll:0.00}"), _sCenter, 0.75f * a, 30); y += 32; }
        Line(0, sh - 42, sw, Tr.L("Нажми любую клавишу, чтобы закрыть", "Press any key to close"), _sCenter, 0.5f * a, 30);
    }

    void DrawHelp(float sw, float sh)
    {
        var lines = AmGranny
            ? new[]
            {
                Tr.L("ЛКМ — удар битой. Попал: выживший теряет день и просыпается в кровати", "LMB - bat. A hit costs the survivor a day, they wake up in bed"),
                Tr.L($"{Cfg.TrapKey.Value} — поставить капкан под ноги", $"{Cfg.TrapKey.Value} - place a bear trap at your feet"),
                Tr.L($"{Cfg.ListenKey.Value} — слух: выжившие подсвечиваются розовым через стены", $"{Cfg.ListenKey.Value} - listen: survivors glow pink through walls"),
                Tr.L($"{Cfg.CheckKey.Value} — встань у кровати, гроба или машины и посмотри на них: найдёшь того, кто спрятался на R", $"{Cfg.CheckKey.Value} - stand at a bed, coffin or the car and look at it: finds whoever hid there with R"),
                Tr.L("E — открыть запертую дверь", "E - open a locked door"),
                Tr.L($"{Cfg.UnstuckKey.Value} — застряла? вытащить себя (раз в 15 с, само срабатывает через 2,5 с)", $"{Cfg.UnstuckKey.Value} - stuck? get yourself out (every 15 s, also automatic after 2.5 s)"),
                Tr.L("♪ на экране — кто-то шумит в этом месте", "♪ on the screen - someone made a noise there"),
                Tr.L("Зашёл в подвал с пауком — ты управляешь Анжелой", "Someone in the spider cellar - you control Angela"),
                Tr.L("Когда все выжившие выбыли, ты сама казнишь последнего", "When all survivors are out, you execute the last one yourself"),
            }
            : new[]
            {
                Tr.L("WASD — идти,  Shift — бежать (шумно),  C — присесть", "WASD - walk,  Shift - run (loud),  C - crouch"),
                Tr.L("E — взять предмет, открыть дверь или ящик,  Пробел — бросить", "E - take an item, open a door or drawer,  Space - drop"),
                Tr.L("R — спрятаться под кровать или в шкаф, когда рядом появится значок", "R - hide under a bed or in a wardrobe when the icon appears"),
                Tr.L("F — снять капкан", "F - remove a bear trap"),
                Tr.L($"{Cfg.BoostKey.Value} — гиперскорость x{Cfg.BoostMul.Value:0.#} на {Cfg.BoostDuration.Value:F0} с (перезарядка {Cfg.BoostCooldown.Value:F0} с)", $"{Cfg.BoostKey.Value} - hyper speed x{Cfg.BoostMul.Value:0.#} for {Cfg.BoostDuration.Value:F0} s (cooldown {Cfg.BoostCooldown.Value:F0} s)"),
                Tr.L($"{Cfg.PunchKey.Value} — удар кулаком вплотную: {Cfg.PunchChance.Value}% свалить Гренни на {Cfg.PunchDownTime.Value:F0} с, {Cfg.PunchCharges.Value} удара за раунд. Нашла в укрытии — бьёшь сам", $"{Cfg.PunchKey.Value} - punch up close: {Cfg.PunchChance.Value}% to knock Granny down for {Cfg.PunchDownTime.Value:F0} s, {Cfg.PunchCharges.Value} per round. Found in hiding: you punch by yourself"),
                Tr.L("Спрятался на R — Гренни не видит тебя даже слухом (F)", "Hidden with R: Granny can't see you even when listening (F)"),
                Tr.L("Гренни слышит упавшие вещи, скрипучий пол и выстрелы", "Granny hears dropped items, creaky floors and shots"),
                Tr.L("В подвале живёт Анжела: туда лучше не заходить без причины", "Angela lives in the cellar: stay out unless you must"),
            };
        var w = 980f; var h = 60f + lines.Length * 38f; var x = (sw - w) / 2; var y = (sh - h) / 2;
        Box(new Rect(x, y, w, h), 0.8f);
        Line(x, y + 12, w, Tr.L("Подсказка", "Help"), _sMid);
        y += 54;
        foreach (var l in lines) { Line(x, y, w, l, _sCenter, 1f, 32); y += 38; }
    }

    void DrawToasts(float w, float h)
    {
        _toasts.RemoveAll(t => Time.time > t.Until);
        var y = h * 0.66f;
        foreach (var t in _toasts)
        {
            var s = t.Big ? _sBig : _sMid;
            var lines = t.Text.Split('\n').Length;
            var hh = (t.Big ? 44 : 30) * lines;
            var a = Mathf.Clamp01(t.Until - Time.time);
            var c = t.Color; c.a = a;
            Shadow(new Rect(w * 0.1f, y, w * 0.8f, hh), t.Text, s, c);
            y += hh + 6;
        }
    }
}
