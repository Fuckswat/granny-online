using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyMP;

public enum Role : byte { None = 0, Survivor = 1, Granny = 2 }

public sealed class PlayerInfo
{
    public byte Id;
    public string Name = "?";
    public bool InGame;
    public Role Role;
    public int Day = 1;
    public bool Alive = true;
    public bool Escaped;

    // runtime, from PlayerState
    public bool HasState;
    public Vector3 Pos;
    public float Yaw, Speed, LastSeen;
    public byte Flags;
    public Vector3 SmoothPos;
    public float SmoothYaw;
    public RemoteAvatar Avatar;
    public bool InZone;          // host only
    public float RevealUntil;    // granny: highlight after trap
    public int Held = -1;        // index in HeldItems()
    public bool NameVisible;
    public float NextLosCheck;
    public bool WasPunching;

    public bool Crouch => (Flags & 1) != 0;
    public bool Hidden => (Flags & 2) != 0;
    public bool Spectating => (Flags & 4) != 0;
    public bool Waking => (Flags & 16) != 0;
    /// <summary>Hidden with the R key: under a bed, in a coffin, in the car. The only thing Granny's check finds.</summary>
    public bool InSpot => (Flags & 32) != 0;
    /// <summary>Bit 32 without bit 2: standing in a wardrobe with the doors shut (not an R hide, Q does not find it).</summary>
    public bool InWardrobe => (Flags & 32) != 0 && (Flags & 2) == 0;
    /// <summary>Nobody outside sees this player's model.</summary>
    public bool OutOfSight => Hidden || InWardrobe;
    public bool InHouse => (Flags & 8) != 0;
    public bool Boost => (Flags & 64) != 0;
    public bool Punching => (Flags & 128) != 0;
    public bool ActiveSurvivor => Role == Role.Survivor && Alive && !Escaped;
}

public sealed partial class Game
{
    public static readonly Game I = new();

    public readonly NetSession Net = new();
    public readonly Dictionary<byte, PlayerInfo> Players = new();
    public GameRefs R;

    public bool InRound;
    public sbyte GrannyId = -2;  // -1 = AI granny
    public int RoundNo;
    public bool Possess;          // a survivor is in the spider cellar -> granny controls Angela

    string _scene = "";
    float _nextRefsTry, _nextStateSend, _nextAiEnforce;
    bool _rosterDirty, _reportedInGame;
    readonly System.Random _rng = new();
    readonly PlayerInfo _offlineMe = new() { Id = 0, Name = Tr.L("я", "me") };

    public PlayerInfo Me => Players.TryGetValue(Net.LocalId, out var p) ? p : _offlineMe;
    public bool Session => Net.Active;
    public bool IsHouse => _scene == "Scene";
    public bool InHouse => IsHouse && R != null;
    public bool AmGranny => InRound && Me.Role == Role.Granny;
    public bool AmSurvivor => InRound && Me.Role == Role.Survivor;
    public bool AiGranny => InRound && GrannyId < 0;
    public float U => R?.U ?? 1.75f;
    public float Now => Time.time;

    Game()
    {
        Net.OnMessage = OnMessage;
        Net.OnPeerJoined = id => { Players[id] = new PlayerInfo { Id = id, Name = Tr.L("игрок ", "player ") + id, Role = Role.None }; _rosterDirty = true; };
        Net.OnPeerLeft = OnPeerLeft;
        Net.OnConnected = () =>
        {
            var w = Net.Begin(Msg.Hello);
            w.Put(Plugin.TestId > 0 ? Tr.L("Игрок ", "Player ") + Plugin.TestId : Cfg.PlayerName.Value); w.Put(Plugin.Version); w.Put(InHouse); w.Put(Plugin.GameVersion + " " + Plugin.GameBuild); w.Put(Plugin.ModBuild);
            Net.Send();
        };
        Net.OnDisconnected = reason => { Toast(Tr.L("Связь с хостом потеряна (", "Connection to the host lost (") + reason + ")", 6, Color.red); EndSession(); };
    }


    public void StartHost()
    {
        var err = Net.Host(Cfg.Port.Value);
        if (err != null) { Toast(err, 6, Color.red); return; }
        Players.Clear();
        Players[0] = new PlayerInfo { Id = 0, Name = Plugin.TestId > 0 ? "Хост" : Cfg.PlayerName.Value, InGame = IsHouse };
        _rosterDirty = true;
        OnSessionStarted();
    }

    public void StartClient(string ip)
    {
        Cfg.LastIp.Value = ip;
        Players.Clear();
        var err = Net.Connect(ip, Cfg.Port.Value);
        if (err != null) { Toast(err, 6, Color.red); return; }
        Toast(Tr.L($"Подключаюсь к {ip}:{Cfg.Port.Value}...", $"Connecting to {ip}:{Cfg.Port.Value}..."), 4, Color.white);
    }

    public void EndSession()
    {
        if (InRound) EndRoundLocal();
        X = null;
        Net.Stop();
        foreach (var p in Players.Values) p.Avatar?.Destroy();
        Players.Clear();
        GrannyId = -2;
        LeaveLobby();
        if (R != null) SetOriginalAi(true);
        if (IsHouse) ReturnToMainMenu();
    }

    void OnSessionStarted()
    {
        if (R != null) SetOriginalAi(false);
        _reportedInGame = !IsHouse; // force a SceneInfo
    }

    void OnPeerLeft(byte id)
    {
        if (Players.TryGetValue(id, out var p))
        {
            Toast(Tr.L($"{p.Name} вышел", $"{p.Name} left"), 4, Color.yellow);
            p.Avatar?.Destroy();
            Players.Remove(id);
        }
        _rosterDirty = true;
        if (InRound && id == GrannyId)
        {
            GrannyId = -1;
            var w = Net.Begin(Msg.GrannyChange); w.Put(GrannyId); Net.Send();
            BroadcastToast(Tr.Both("Гренни вышла из игры — теперь её ведёт ИИ", "Granny left the game, the AI takes over"));
            OnGrannyChanged();
        }
        CheckRoundEnd();
    }


    public void Update()
    {
        Net.Poll();

        var scene = SceneManager.GetActiveScene().name ?? "";
        if (scene != _scene) OnSceneChanged(_scene, scene);
        else if (_reloading && (R == null || R.Player == null)) { _reloading = false; OnSceneChanged(_scene, scene); }
        EnsureResolution();
        UpdateDev();
        UpdateSessionPause();

        if (IsHouse && (R == null || !R.Valid) && Now >= _nextRefsTry)
        {
            _nextRefsTry = Now + 0.5f;
            if (R != null && !R.Valid) R = null;
            var r = GameRefs.TryFind();
            if (r != null && r.Valid) OnHouseReady(r);
        }

        Menu.Update();
        if (_openMenuInMainMenu && _scene == "Menu") { _openMenuInMainMenu = false; Menu.Show(true); }
        if (Input.GetKeyDown(Cfg.MenuKey.Value) && Menu.Focus == 0 && !Executing) ToggleMenu();
        AdminHotkeys();
        UpdateTextInput();
        UpdatePauseGuard();
        KeepMouseAlive();
        if (Session && Net.IsHost && InHouse && !InRound && !MenuOpen && Input.GetKeyDown(KeyCode.Return)) StartRoundHost();
        if (_reloadHouseAt > 0 && Now >= _reloadHouseAt)
        {
            // doors, items and the bed are as the last round left them: load the house again
            _reloadHouseAt = 0;
            if (IsHouse && Session) { _reloading = true; SceneManager.LoadScene("Scene"); }
        }
        if (_returnToHouseAt > 0 && Now >= _returnToHouseAt)
        {
            ReturnToMainMenu();
        }
        if (!Executing && _caughtT0 <= 0 && _relocateT0 <= 0 && FadeAlpha > 0) FadeAlpha = Mathf.Max(0, FadeAlpha - Time.deltaTime);
        if (!InRound && FlashAlpha > 0) FlashAlpha = Mathf.Max(0, FlashAlpha - Time.deltaTime);

        if (Session)
        {
            // by scene, not by refs: the game switches the player off for a moment after a fall death
            if (_reportedInGame != IsHouse)
            {
                _reportedInGame = IsHouse;
                var w = Net.Begin(Msg.SceneInfo); w.Put(IsHouse); ToHost(Msg.SceneInfo);
            }
            if (Net.IsHost && _rosterDirty) { _rosterDirty = false; SendRoster(); }

            if (InHouse)
            {
                if (Now >= _nextAiEnforce)
                {
                    _nextAiEnforce = Now + 1f;
                    SetOriginalAi(false);
                    if (InRound && !AmGranny && !Executing && R.Granny?.IsDown == false) { R.Granny?.SetVisible(true); R.Spider?.SetVisible(true); } // the game's own scripts must not hide them
                }
                if (Now >= _nextStateSend) { _nextStateSend = Now + 1f / 15f; SendPlayerState(); }
                if (Executing) { UpdateExecution(); return; }
                UpdateLobby();
                UpdateBots();
                UpdateAvatars();
                UpdateWorldSync();
                UpdateDrops();
                if (InRound)
                {
                    UpdateTraps();
                    if (AmSurvivor) UpdateSurvivor();
                    UpdateSurvivorAbilities();
                    if (AmGranny) UpdateGrannyLocal();
                    if (AiGranny && Net.IsHost) UpdateAi();
                }
                UpdateNoiseFx();
                UpdateRemoteBodies();
            }
        }
        UpdateMenuCursor();
    }

    public void LateUpdate()
    {
        if (Session && InHouse && InRound) UpdateSpectatorCamera();
        if (R != null) UpdateDownView(); // before the bat: it hides while Granny looks around lying
        if (R != null && IsHouse) UpdateViewBat();
        if (Session && InRound && IsHouse) KeepDropPoint();
        if (Session && InRound && IsHouse) UpdatePunchArms();
    }

    public void Shutdown() { try { Net.Stop(); } catch { } }

    void OnSceneChanged(string old, string now)
    {
        Plugin.L.LogInfo($"[scene] {old} -> {now}");
        _scene = now;
        if (R != null) { foreach (var p in Players.Values) { p.Avatar?.Destroy(); p.Avatar = null; } }
        R = null; _lobby = null; _lobbyOn = false;
        _sceneBase = null; _worldBuiltForScene = null; // captured once per scene load
        ClearWorld();
        ClearTraps();
        _spectating = false; _lockUntil = 0; _caughtT0 = 0;
        _noiseFx.Clear();
        _dropPoint = null; _dropPointTrap = null;
        _graceUntil = 0; _nativeBedEnd = false;
        _spots = null; _hasNav = false;
        _heldItems = null;
        _downUntil = 0; _gun = null; _pickUp = null;
        ForgetDownView();
        _drops.Clear(); _dropTemplates = null;
        _wakeUntil = 0;
        OnSceneChangedAbilities();
        X = null; _resChecked = false; _vision = null; _viewBat = null; _headBone = null; _bodyBats = Array.Empty<Renderer>(); _relocateT0 = 0; _relocated = false;

        if (Session && InRound && now == "EndScene" && Me.Role == Role.Survivor && Me.Alive && !Me.Escaped)
        {
            Me.Escaped = true;
            Net.Begin(Msg.Escaped); ToHost(Msg.Escaped);
        }
    }

    (float walk, float run, Vector3 spawn, float yaw)? _sceneBase;
    string _worldBuiltForScene;

    void OnHouseReady(GameRefs r)
    {
        // refs can be re-found mid-scene (e.g. after the game's own fall death): keep the original base values
        if (_sceneBase == null) _sceneBase = (r.BaseWalk, r.BaseRun, r.PlayerSpawn, r.PlayerSpawnYaw);
        else { r.BaseWalk = _sceneBase.Value.walk; r.BaseRun = _sceneBase.Value.run; r.PlayerSpawn = _sceneBase.Value.spawn; r.PlayerSpawnYaw = _sceneBase.Value.yaw; }
        var firstTime = R == null && _worldBuiltForScene != _scene;
        r.StandEye = r.EyeHeight;
        R = r;
        if (firstTime) _menuFlagNormal = r.Fpc.menuEnabled;
        EnsureAvatarTemplate();
        CaptureGravity();
        if (firstTime) { BuildWorld(); _worldBuiltForScene = _scene; }
        if (Session)
        {
            SetOriginalAi(false);
            if (firstTime) R.Granny?.Place(R.GrannySpawn, R.GrannySpawnYaw);
            if (InRound)
            {
                // re-entered the house mid-round (new game): spectate until next round
                if (Me.Role == Role.Granny) BecomeGranny();
                else if (Me.Role == Role.Survivor && Me.Alive && firstTime) BecomeSurvivor();
            }
        }
    }

    /// <summary>Original single-player AI must never run while a session is active.</summary>
    void SetOriginalAi(bool on)
    {
        if (R == null) return;
        try
        {
            if (R.GrannyAi != null && R.GrannyAi.enabled != on) R.GrannyAi.enabled = on;
            if (R.SpiderAi != null && R.SpiderAi.enabled != on) R.SpiderAi.enabled = on;
            if (!on)
            {
                var aiAgentWanted = InRound && AiGranny && Net.IsHost;
                R.Granny?.SetAgent(aiAgentWanted);
                R.Spider?.SetAgent(aiAgentWanted && Possess);
                R.Granny?.DisableTriggers();
                R.Spider?.DisableTriggers();
                if (R.SpiderAi != null && !R.Spider.Go.activeSelf) R.Spider.Go.SetActive(true);
                if (R.GrannyAi != null && !R.Granny.Go.activeSelf) R.Granny.Go.SetActive(true);
            }
            else
            {
                R.Granny?.SetAgent(true);
                R.Spider?.SetAgent(true);
                R.Granny?.RestoreColliders();
                R.Spider?.RestoreColliders();
                R.Granny?.SetVisible(true);
                R.Spider?.SetVisible(true);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[ai] toggle: " + e.Message); }
    }


    /// <summary>Send the message currently in the writer to the host, or handle it locally if we are the host.</summary>
    void ToHost(Msg m)
    {
        if (Net.IsHost)
        {
            var data = Net.Writer.CopyData();
            OnMessage(m, Net.LocalId, new NetDataReader(data, 2, data.Length));
        }
        else Net.Send();
    }

    void SendRoster()
    {
        var w = Net.Begin(Msg.Roster);
        w.Put((byte)Players.Count);
        foreach (var p in Players.Values)
        {
            w.Put(p.Id); w.Put(p.Name ?? "?"); w.Put(p.InGame); w.Put((byte)p.Role); w.Put((byte)p.Day); w.Put(p.Alive); w.Put(p.Escaped);
        }
        Net.Send();
    }

    void BroadcastToast(string text)
    {
        var w = Net.Begin(Msg.Toast); w.Put(text); Net.Send();
        Toast(Tr.Pick(text), 5, Color.white);
    }

    void SendPlayerState()
    {
        if (R == null) return;
        byte flags = 8;
        if (R.Fpc.playerCrouch) flags |= 1;
        if (IsHiding()) flags |= 2;
        if (_spectating) flags |= 4;
        if (GettingUp) flags |= 16;
        if (HiddenByR || InClosedWardrobe()) flags |= 32; // with bit 2 = hidden with R, without = closed wardrobe
        if (Boosting) flags |= 64;
        if (PunchShowing) flags |= 128;
        var feet = R.Feet();
        var dt = Mathf.Max(0.01f, Now - _lastStateTime);
        var flat = feet - _lastFeet; flat.y = 0;
        _speed = Mathf.Lerp(_speed, flat.magnitude / dt, 0.5f);
        _lastFeet = feet; _lastStateTime = Now;

        var w = Net.Begin(Msg.PlayerState);
        w.Put(feet); w.Put(R.Yaw); w.Put(_speed); w.Put(flags); w.Put(MyHeldItem());
        Net.Send(false);
    }
    Vector3 _lastFeet; float _lastStateTime, _speed, _returnToHouseAt, _reloadHouseAt;

    bool IsHiding()
    {
        try { return !R.Player.activeInHierarchy || (R.RenderCam != null && (!R.RenderCam.enabled || !R.RenderCam.gameObject.activeInHierarchy)); }
        catch { return false; }
    }


    void OnMessage(Msg type, byte sender, NetDataReader r)
    {
        switch (type)
        {
            case Msg.Hello when Net.IsHost:
            {
                if (!Players.TryGetValue(sender, out var p)) Players[sender] = p = new PlayerInfo { Id = sender };
                p.Name = Sanitize(r.GetString()); var ver = r.GetString(); p.InGame = r.GetBool();
                var build = r.AvailableBytes > 0 ? r.GetString() : "?";
                var modBuild = r.AvailableBytes > 0 ? r.GetString() : "?";
                if (modBuild != Plugin.ModBuild)
                    BroadcastToast(Tr.Both($"ВНИМАНИЕ: у {p.Name} другая сборка мода. Обновите архив, иначе возможны ошибки", $"WARNING: {p.Name} has a different mod build. Update the archive or expect errors"));
                if (!build.EndsWith(Plugin.GameBuild))
                    Toast(Tr.L($"ВНИМАНИЕ: у {p.Name} другая версия игры ({build}), у тебя {Plugin.GameVersion} {Plugin.GameBuild}. Возможны рассинхроны!", $"WARNING: {p.Name} has a different game version ({build}), you have {Plugin.GameVersion} {Plugin.GameBuild}. Desync is possible!"), 10, Color.yellow);
                if (InRound) { p.Role = Role.Survivor; p.Alive = false; } // late joiner spectates
                var w = Net.Begin(Msg.Welcome);
                w.Put(sender); w.Put(InRound); w.Put(GrannyId); w.Put(RoundNo); w.Put(Possess);
                Net.SendTo(sender);
                SendSettings(sender);
                _rosterDirty = true;
                Toast(Tr.L($"{p.Name} подключился", $"{p.Name} connected") + (ver != Plugin.Version ? Tr.L($" (версия мода {ver} ≠ {Plugin.Version}!)", $" (mod version {ver} ≠ {Plugin.Version}!)") : ""), 5, Color.green);
                if (InRound) SendSnapshotTo(sender);
                break;
            }
            case Msg.Welcome:
            {
                Net.LocalId = r.GetByte();
                InRound = r.GetBool(); GrannyId = r.GetSByte(); RoundNo = r.GetInt(); Possess = r.GetBool();
                OnSessionStarted();
                EnterHouse();
                break;
            }
            case Msg.Roster when !Net.IsHost:
            {
                var n = r.GetByte();
                var seen = new HashSet<byte>();
                for (var i = 0; i < n; i++)
                {
                    var id = r.GetByte();
                    if (!Players.TryGetValue(id, out var p)) Players[id] = p = new PlayerInfo { Id = id };
                    p.Name = r.GetString(); p.InGame = r.GetBool(); p.Role = (Role)r.GetByte(); p.Day = r.GetByte(); p.Alive = r.GetBool(); p.Escaped = r.GetBool();
                    seen.Add(id);
                }
                foreach (var id in Players.Keys.Where(k => !seen.Contains(k)).ToList()) { Players[id].Avatar?.Destroy(); Players.Remove(id); }
                break;
            }
            case Msg.PlayerState:
            {
                if (sender == Net.LocalId || !Players.TryGetValue(sender, out var p)) break;
                p.Pos = r.GetVec3(); p.Yaw = r.GetFloat(); p.Speed = r.GetFloat(); p.Flags = r.GetByte(); p.Held = r.GetSByte();
                if (!p.HasState || (p.SmoothPos - p.Pos).sqrMagnitude > 100f * U * U) { p.SmoothPos = p.Pos; p.SmoothYaw = p.Yaw; }
                p.HasState = true; p.LastSeen = Now;
                break;
            }
            case Msg.BodyState: ReadBodyState(sender, r); break;
            case Msg.Caught:
            {
                var target = r.GetByte(); var body = r.GetByte();
                if (target == Net.LocalId) OnCaughtLocal(body);
                else if (Net.IsHost) TryCatchBot(target);
                break;
            }
            case Msg.TrapPlace: { var id = r.GetInt(); var pos = r.GetVec3(); var yaw = r.GetFloat(); SpawnTrap(id, pos, yaw); break; }
            case Msg.TrapSprung: { var id = r.GetInt(); var victim = r.GetByte(); SpringTrap(id, victim); break; }
            case Msg.Zone when Net.IsHost:
            {
                var v = r.GetBool();
                if (Players.TryGetValue(sender, out var p)) p.InZone = v;
                RecomputePossess();
                break;
            }
            case Msg.Possess: SetPossess(r.GetBool()); break;
            case Msg.Noise: OnNoise(r.GetVec3()); break;
            case Msg.Stun: { var sec = r.GetFloat(); var kind = r.GetByte(); OnStun(sec, kind); break; }
            case Msg.Door: ReadDoor(r, true); break;
            case Msg.Obj: ReadObj(r, true); break;
            case Msg.SnapDoors: { var n = r.GetUShort(); for (var i = 0; i < n; i++) ReadDoor(r, false); break; }
            case Msg.SnapObjs: { var n = r.GetUShort(); for (var i = 0; i < n; i++) ReadObj(r, false); break; }
            case Msg.RoundStart when !Net.IsHost:
            {
                var g = r.GetSByte(); var no = r.GetInt(); var sp = r.AvailableBytes >= 4 ? r.GetFloat() : 1f;
                if (r.AvailableBytes >= 8) { KnockGun = r.GetFloat(); KnockArrow = r.GetFloat(); Plugin.L.LogInfo($"[knock] from host: shotgun {KnockGun:F0} s, arrow {KnockArrow:F0} s"); }
                StartRoundLocal(g, no, sp);
                break;
            }
            case Msg.RoundEnd when !Net.IsHost: { var winner = r.GetByte(); var text = r.GetString(); EndRoundLocal(); ShowRoundResult(winner, text); break; }
            case Msg.DayUpdate when Net.IsHost:
            {
                var day = r.GetByte(); var elim = r.GetBool();
                if (Players.TryGetValue(sender, out var p))
                {
                    p.Day = day; if (elim && p.Alive) { p.Alive = false; _lastVictim = p.Id; BroadcastToast(Tr.Both($"{p.Name} выбыл (пойман {Cfg.Days.Value} раз)", $"{p.Name} is out (caught {Cfg.Days.Value} times)")); }
                    _rosterDirty = true;
                }
                CheckRoundEnd();
                break;
            }
            case Msg.Escaped when Net.IsHost:
            {
                if (InRound && Players.TryGetValue(sender, out var p))
                {
                    p.Escaped = true; _rosterDirty = true;
                    EndRoundHost(1, Tr.Both($"{p.Name} СБЕЖАЛ! Выжившие победили!", $"{p.Name} ESCAPED! Survivors win!"));
                }
                break;
            }
            case Msg.SceneInfo when Net.IsHost:
            {
                var inGame = r.GetBool();
                if (Players.TryGetValue(sender, out var p))
                {
                    p.InGame = inGame; _rosterDirty = true;
                    if (InRound && !inGame && p.ActiveSurvivor && !p.Escaped)
                    {
                        p.Alive = false;
                        BroadcastToast(Tr.Both($"{p.Name} покинул дом и выбыл", $"{p.Name} left the house and is out"));
                    }
                }
                CheckRoundEnd();
                break;
            }
            case Msg.Toast when !Net.IsHost: Toast(Tr.Pick(r.GetString()), 5, Color.white); break;
            case Msg.ExecStart when !Net.IsHost: { var v = r.GetByte(); var sc = r.GetByte(); StartExecutionLocal(v, sc); break; }
            case Msg.ExecStep: ApplyExecStep(r.GetByte()); break;
            case Msg.ItemDrop: ReadItemDrop(r); break;
            case Msg.ItemMove: ReadItemMove(r); break;
            case Msg.ItemGone: ReadItemGone(r); break;
            case Msg.GrannyChange when !Net.IsHost: GrannyId = r.GetSByte(); OnGrannyChanged(); break;
            case Msg.Exit: ReadExit(r); break;
            case Msg.Settings when !Net.IsHost: ReadSettings(r); break;
        }
    }

    static string Sanitize(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) s = Tr.L("игрок", "player");
        return s.Length > 20 ? s[..20] : s;
    }


    public void StartRoundHost(int? forcedPick = null)
    {
        if (!Net.IsHost) return;
        var ingame = Players.Values.Where(p => p.InGame).ToList();
        if (ingame.Count == 0) { Toast(Tr.L("Никто не находится в доме — все должны начать новую игру", "Nobody is in the house: everyone has to start a new game"), 6, Color.red); return; }
        var notReady = Players.Values.Where(p => !p.InGame).Select(p => p.Name).ToList();
        if (notReady.Count > 0) Toast(Tr.L("Не в доме (будут зрителями): ", "Not in the house (will spectate): ") + string.Join(", ", notReady), 6, Color.yellow);

        // bots are never Granny; admin "I am Granny" only works while the host plays alone
        var humans = ingame.Where(p => !_bots.ContainsKey(p.Id)).ToList();
        if (forcedPick != null && humans.Any(p => p.Id != Net.LocalId))
        {
            forcedPick = null;
            Toast(Tr.L("В игре есть другие игроки: Гренни выбирается случайно", "Other players are here: Granny is picked at random"), 4, Color.yellow);
        }
        var slots = humans.Select(p => (int)p.Id).ToList();
        if (Cfg.AllowAiGranny.Value || slots.Count == 0) slots.Add(-1);
        int pick;
        if (forcedPick != null) pick = forcedPick.Value;
        else
        {
            // random, but fair: only among those who were Granny the fewest times, so nobody gets long streaks
            var min = slots.Min(GrannyTimes);
            var pool = slots.Where(s => GrannyTimes(s) == min).ToList();
            pick = pool[_rng.Next(pool.Count)];
            Plugin.L.LogInfo($"[round] granny pool [{string.Join(", ", slots.Select(s => $"{s}:{GrannyTimes(s)}"))}] -> fair [{string.Join(", ", pool)}] -> {pick}");
        }
        _grannyTimes[pick] = GrannyTimes(pick) + 1;
        var lo = Mathf.Min(Cfg.GrannySpeedMin.Value, Cfg.GrannySpeedMax.Value); var hi = Mathf.Max(Cfg.GrannySpeedMin.Value, Cfg.GrannySpeedMax.Value);
        lo = Mathf.Min(lo, GrannyMaxFactor); hi = Mathf.Min(hi, GrannyMaxFactor); // always slower than a survivor
        var speed = Mathf.Round((float)(lo + _rng.NextDouble() * (hi - lo)) * 100f) / 100f;
        RoundNo++;
        foreach (var p in Players.Values)
        {
            p.Role = !p.InGame ? Role.Survivor : (p.Id == pick ? Role.Granny : Role.Survivor);
            p.Day = 1; p.Alive = p.InGame; p.Escaped = false; p.InZone = false;
        }
        SendRoster(); _rosterDirty = false;
        if (R != null) ComputeKnockouts();
        var w = Net.Begin(Msg.RoundStart); w.Put((sbyte)pick); w.Put(RoundNo); w.Put(speed); w.Put(KnockGun); w.Put(KnockArrow); Net.Send();
        StartRoundLocal((sbyte)pick, RoundNo, speed);
        SendSnapshotTo(255);
    }

    readonly Dictionary<int, int> _grannyTimes = new();
    int GrannyTimes(int slot) => _grannyTimes.TryGetValue(slot, out var n) ? n : 0;

    /// <summary>Granny's speed this round, rolled by the host (1 = the speeds from the settings).</summary>
    public float GrannySpeedRoll = 1f;

    void StartRoundLocal(sbyte grannyId, int no, float grannySpeed = 1f)
    {
        InRound = true; GrannyId = grannyId; RoundNo = no; Possess = false;
        GrannySpeedRoll = Mathf.Clamp(grannySpeed, 0.5f, GrannyMaxFactor);
        Plugin.L.LogInfo($"[round] {no}: granny {grannyId}, speed x{GrannySpeedRoll:F2}");
        _caughtT0 = 0; _lockUntil = 0; _spectating = false; _myInZone = false; _zoneSent = false; Me.Day = 1;
        _lastVictim = 255; _execText = null;
        _graceUntil = 0; _nativeBedEnd = false;
        ClearDrops();
        CloseNativePause();
        _listenUntil = 0; _trapReadyAt = Now + 5f; _listenReadyAt = Now + 10f; _batReadyAt = Now + 1f;
        ResetSurvivorAbilities();
        ClearTraps();
        foreach (var p in Players.Values) p.RevealUntil = 0;
        if (R != null)
        {
            LeaveLobby();
            MoveBots(id => R.PlayerSpawn + Quaternion.Euler(0, id * 70f, 0) * Vector3.forward * (2f * U));
            SetOriginalAi(false);
            R.Granny?.Place(R.GrannySpawn, R.GrannySpawnYaw);
            R.Granny?.SetVisible(true); R.Spider?.SetVisible(true);
            if (Me.Role == Role.Granny) BecomeGranny();
            else if (Me.Role == Role.Survivor && Me.Alive) BecomeSurvivor();
        }
        var grannyName = grannyId < 0 ? Tr.L("ИИ (компьютер)", "AI (computer)") : (Players.TryGetValue((byte)grannyId, out var gp) ? gp.Name : "?");
        _grannyLabel = grannyName;
        ShowRoleCard();
        _resultUntil = 0;
        if (AiGranny && Net.IsHost) StartAi();
    }

    void EndRoundHost(byte winner, string text)
    {
        if (!InRound) return;
        X = null;
        var w = Net.Begin(Msg.RoundEnd); w.Put(winner); w.Put(text); Net.Send();
        EndRoundLocal();
        foreach (var p in Players.Values) p.Role = Role.None;
        _rosterDirty = true;
        ShowRoundResult(winner, text);
    }

    void ShowRoundResult(byte winner, string text)
    {
        _resultText = Tr.Pick(text);
        _resultUntil = Now + 20f;
        if (winner == 1) _returnToHouseAt = Now + 12f; // the escaper is watching the game's ending
        else if (IsHouse) _reloadHouseAt = Now + 0.3f;
    }

    void EndRoundLocal()
    {
        InRound = false;
        Possess = false;
        EndBedScene();
        SetGrannyVision(false);
        ClearTraps();
        StopSpectating();
        StopBoost();
        HidePunchArms();
        StopDownView(); _grannyUpAt = -99f;
        _lockUntil = 0; _caughtT0 = 0; _listenUntil = 0; _blindUntil = 0; _relocateT0 = 0; _relocated = false; _downUntil = 0; _gun = null; _pickUp = null;
        foreach (var p in Players.Values) p.Avatar?.SetXray(false);
        if (R != null)
        {
            try
            {
                R.Fpc.enabled = true; R.Cc.enabled = true;
                R.Fpc.m_WalkSpeed = R.BaseWalk; R.Fpc.m_RunSpeed = R.BaseRun;
            }
            catch { }
            R.Granny?.SetVisible(true); R.Spider?.SetVisible(true);
            R.Granny?.RestoreColliders(); R.Spider?.RestoreColliders();
            R.Granny?.SetAgent(false); R.Spider?.SetAgent(false);
            if (Session) SetOriginalAi(false);
        }
    }

    void CheckRoundEnd()
    {
        if (!Net.IsHost || !InRound || Executing) return;
        var survivors = Players.Values.Where(p => p.Role == Role.Survivor).ToList();
        if (survivors.Count == 0) return;
        if (survivors.Any(p => p.Escaped)) return;
        if (survivors.All(p => !p.Alive))
        {
            var who = GrannyId >= 0 && Players.TryGetValue((byte)GrannyId, out var g) ? g.Name : null;
            BeginExecutionHost(who == null
                ? Tr.Both("ИИ-Гренни поймала всех! Гренни победила!", "AI Granny caught everyone! Granny wins!")
                : Tr.Both($"Гренни ({who}) поймала всех! Гренни победила!", $"Granny ({who}) caught everyone! Granny wins!"));
        }
    }

    void OnGrannyChanged()
    {
        if (AiGranny && Net.IsHost && InRound) StartAi();
    }
}
