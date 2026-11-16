using UnityEngine;

namespace GrannyMP;

/// <summary>Host-only test tools: solo rounds, role switching, a walking test bot.</summary>
public sealed partial class Game
{
    const byte BotIdBase = 200;
    readonly Dictionary<byte, (Vector3 anchor, float phase, float stunUntil)> _bots = new();

    public bool AdminAllowed => Cfg.AdminPanel.Value && (!Session || Net.IsHost);
    public int BotCount => _bots.Count;
    public void AdminCatchMe() => OnCaughtLocal(0);

    /// <summary>Change a round rule: host only (offline too); broadcast to everyone.</summary>
    public void Rule(Action change)
    {
        if (Session && !Net.IsHost) return;
        change();
        if (Net.IsHost) SendSettings(255);
    }

    void SendSettings(byte target)
    {
        var w = Net.Begin(Msg.Settings);
        w.Put(Cfg.Days.Value); w.Put(Cfg.BatCooldown.Value); w.Put(Cfg.BatRange.Value); w.Put(Cfg.TrapCooldown.Value);
        w.Put(Cfg.MaxTraps.Value); w.Put(Cfg.TrapStunSeconds.Value); w.Put(Cfg.ListenCooldown.Value); w.Put(Cfg.ListenDuration.Value);
        w.Put(Cfg.GrannyWalkMul.Value); w.Put(Cfg.GrannyRunMul.Value); w.Put(Cfg.AllowAiGranny.Value); w.Put(Cfg.BiteRange.Value);
        w.Put(Cfg.SpiderSpeedMul.Value); w.Put(Cfg.TrapRadius.Value); w.Put(Cfg.BatAngle.Value);
        // 1.1: survivor abilities (an older client simply stops reading here)
        w.Put(Cfg.BoostMul.Value); w.Put(Cfg.BoostDuration.Value); w.Put(Cfg.BoostCooldown.Value);
        w.Put(Cfg.PunchChance.Value); w.Put(Cfg.PunchCharges.Value); w.Put(Cfg.PunchDownTime.Value); w.Put(Cfg.PunchRange.Value);
        w.Put(Cfg.GrannySurviveChance.Value);
        if (target == 255) Net.Send(); else Net.SendTo(target);
    }

    void ReadSettings(LiteNetLib.Utils.NetDataReader r)
    {
        Cfg.Days.Value = r.GetInt(); Cfg.BatCooldown.Value = r.GetFloat(); Cfg.BatRange.Value = r.GetFloat(); Cfg.TrapCooldown.Value = r.GetFloat();
        Cfg.MaxTraps.Value = r.GetInt(); Cfg.TrapStunSeconds.Value = r.GetFloat(); Cfg.ListenCooldown.Value = r.GetFloat(); Cfg.ListenDuration.Value = r.GetFloat();
        Cfg.GrannyWalkMul.Value = r.GetFloat(); Cfg.GrannyRunMul.Value = r.GetFloat(); Cfg.AllowAiGranny.Value = r.GetBool(); Cfg.BiteRange.Value = r.GetFloat();
        Cfg.SpiderSpeedMul.Value = r.GetFloat(); Cfg.TrapRadius.Value = r.GetFloat(); Cfg.BatAngle.Value = r.GetFloat();
        if (r.AvailableBytes >= 7 * 4)
        {
            Cfg.BoostMul.Value = r.GetFloat(); Cfg.BoostDuration.Value = r.GetFloat(); Cfg.BoostCooldown.Value = r.GetFloat();
            Cfg.PunchChance.Value = r.GetInt(); Cfg.PunchCharges.Value = r.GetInt(); Cfg.PunchDownTime.Value = r.GetFloat(); Cfg.PunchRange.Value = r.GetFloat();
        }
        if (r.AvailableBytes >= 4) Cfg.GrannySurviveChance.Value = r.GetInt();
        if (AmGranny) ApplyGrannySpeeds();
    }

    void EnsureHostForAdmin()
    {
        if (!Session) StartHost();
    }

    public void AdminSoloRound(bool meGranny)
    {
        EnsureHostForAdmin();
        if (!Net.IsHost) return;
        if (!InHouse) { Toast(Tr.L("Сначала начни игру (зайди в дом)", "Start a game first (enter the house)"), 4, Color.red); return; }
        if (InRound) EndRoundHost(0, Tr.Both("Перезапуск раунда (админ)", "Round restarted (admin)"));
        StartRoundHost(meGranny ? 0 : -1);
    }

    public void AdminSwitchRole()
    {
        if (!Net.IsHost || !InRound || R == null) return;
        if (Possess) { var wp = Net.Begin(Msg.Possess); wp.Put(false); Net.Send(); SetPossess(false); }
        if (Me.Role == Role.Granny)
        {
            Me.Role = Role.Survivor; Me.Alive = true; Me.Day = 1; Me.Escaped = false;
            GrannyId = -1;
            R.Granny.SetVisible(true); R.Granny.RestoreColliders();
            R.Granny.Place(R.GrannySpawn, R.GrannySpawnYaw);
            BecomeSurvivor();
            R.Teleport(R.PlayerSpawn, R.PlayerSpawnYaw);
            StartAi();
            Toast(Tr.L("Админ: теперь ты ВЫЖИВШИЙ, Гренни — ИИ", "Admin: you are now a SURVIVOR, Granny is AI"), 4, Color.cyan);
        }
        else
        {
            foreach (var p in Players.Values) if (p.Role == Role.Granny) p.Role = Role.Survivor;
            Me.Role = Role.Granny; Me.Alive = true;
            GrannyId = (sbyte)Net.LocalId;
            R.Granny.SetAgent(false); R.Spider?.SetAgent(false);
            _caughtT0 = 0; _lockUntil = 0; StopSpectating();
            BecomeGranny();
            Toast(Tr.L($"Админ: теперь ты ГРЕННИ (ЛКМ бита, {Cfg.TrapKey.Value} капкан, {Cfg.ListenKey.Value} слух)", $"Admin: you are now GRANNY (LMB bat, {Cfg.TrapKey.Value} trap, {Cfg.ListenKey.Value} listen)"), 5, Color.cyan);
        }
        _rosterDirty = true;
        var w = Net.Begin(Msg.GrannyChange); w.Put(GrannyId); Net.Send();
    }

    public void AdminTogglePossess()
    {
        if (!Net.IsHost || !InRound) return;
        var v = !Possess;
        var w = Net.Begin(Msg.Possess); w.Put(v); Net.Send();
        SetPossess(v);
        Toast(Tr.L("Админ: Анжела ", "Admin: Angela ") + (v ? Tr.L("ВКЛ", "ON") : Tr.L("ВЫКЛ", "OFF")), 3, Color.cyan);
    }

    public void AdminTeleportCellar()
    {
        if (R == null) return;
        var p = R.SpiderStart;
        if (AmGranny && Possess) return;
        R.Teleport(p + Vector3.up * (0.3f * U), R.Yaw);
        Toast(Tr.L("Админ: телепорт в паучий подвал", "Admin: teleported to the spider cellar"), 3, Color.cyan);
    }

    public void AdminTeleportSpawn()
    {
        if (R == null) return;
        R.Teleport(R.PlayerSpawn, R.PlayerSpawnYaw);
    }

    public void AdminAddBot()
    {
        EnsureHostForAdmin();
        if (!Net.IsHost || R == null) return;
        byte id = BotIdBase;
        while (Players.ContainsKey(id)) id++;
        var anchor = GroundBelow(R.Feet() + Flat(R.CamFwd) * (4f * U));
        var bot = new PlayerInfo
        {
            Id = id, Name = Tr.L("Бот ", "Bot ") + (id - BotIdBase + 1), InGame = true, Role = InRound ? Role.Survivor : Role.None,
            Alive = true, Day = 1, HasState = true, Pos = anchor, SmoothPos = anchor, Flags = 8, LastSeen = Now,
        };
        Players[id] = bot;
        _bots[id] = (anchor, UnityEngine.Random.value * 6f, 0f);
        _rosterDirty = true;
        Toast(Tr.L($"Админ: добавлен {bot.Name} (ходит туда-сюда). Бей его битой / ставь капкан на пути", $"Admin: added {bot.Name} (walks back and forth). Hit it with the bat / put a trap in its way"), 5, Color.cyan);
    }

    public void AdminRemoveBots()
    {
        foreach (var id in _bots.Keys.ToList())
        {
            if (Players.TryGetValue(id, out var p)) { p.Avatar?.Destroy(); Players.Remove(id); }
        }
        _bots.Clear();
        _rosterDirty = true;
    }

    public void AdminBotCrouchToggle()
    {
        foreach (var id in _bots.Keys) if (Players.TryGetValue(id, out var p)) p.Flags ^= 1;
    }

    public void AdminBotHideToggle()
    {
        foreach (var id in _bots.Keys) if (Players.TryGetValue(id, out var p)) p.Flags ^= 2;
    }

    public void AdminTrapAhead()
    {
        if (R == null) return;
        PlaceTrapAt(GroundBelow(R.Feet() + Flat(R.CamFwd) * (2.5f * U)), R.Yaw);
        Toast(Tr.L("Админ: капкан впереди", "Admin: trap ahead"), 2, Color.cyan);
    }

    void AdminHotkeys()
    {
        if (!Cfg.AdminPanel.Value || (Session && !Net.IsHost) || !InHouse) return;
        if (Input.GetKeyDown(KeyCode.F2)) { if (InRound) AdminSwitchRole(); else AdminSoloRound(true); }
        if (Input.GetKeyDown(KeyCode.F3)) AdminTogglePossess();
        if (Input.GetKeyDown(KeyCode.F4)) AdminAddBot();
        if (Input.GetKeyDown(KeyCode.F5)) AdminTrapAhead();
        if (Input.GetKeyDown(KeyCode.F6)) AdminTeleportCellar();
        if (Input.GetKeyDown(KeyCode.F7)) AdminTeleportSpawn();
    }

    static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward; }

    void MoveBots(Func<byte, Vector3> place)
    {
        foreach (var id in _bots.Keys.ToList())
        {
            if (!Players.TryGetValue(id, out var p)) continue;
            var at = GroundBelow(place(id)); var (_, phase, _) = _bots[id];
            _bots[id] = (at, phase, 0f); p.Pos = at; p.SmoothPos = at;
        }
    }

    /// <summary>Host-simulated bots: walk back and forth, can be caught and trapped.</summary>
    void UpdateBots()
    {
        if (_bots.Count == 0 || !Net.IsHost) return;
        foreach (var id in _bots.Keys.ToList())
        {
            if (!Players.TryGetValue(id, out var p)) { _bots.Remove(id); continue; }
            var (anchor, phase, stunUntil) = _bots[id];
            p.LastSeen = Now; p.InGame = true;
            if (InRound && p.Role == Role.None) { p.Role = Role.Survivor; p.Alive = true; p.Day = 1; }
            if (Now < stunUntil) { p.Speed = 0; continue; }
            phase += Time.deltaTime * 0.5f;
            var side = Quaternion.Euler(0, 90, 0) * Vector3.forward;
            var off = Mathf.Sin(phase) * 3f * U;
            var newPos = anchor + side * off;
            var dir = newPos - p.Pos; dir.y = 0;
            p.Speed = dir.magnitude / Mathf.Max(0.001f, Time.deltaTime);
            if (p.Crouch) p.Speed *= 0.5f;
            if (dir.sqrMagnitude > 1e-6f) p.Yaw = Quaternion.LookRotation(dir).eulerAngles.y;
            p.Pos = GroundBelow(newPos);
            _bots[id] = (anchor, phase, stunUntil);

            if (InRound && p.ActiveSurvivor)
            {
                foreach (var t in _traps.Values)
                {
                    if (t.Sprung) continue;
                    var d = t.Pos - p.Pos; d.y = 0;
                    if (d.magnitude > Cfg.TrapRadius.Value) continue;
                    SpringTrap(t.Id, id);
                    var w = Net.Begin(Msg.TrapSprung); w.Put(t.Id); w.Put(id); Net.Send();
                    _bots[id] = (anchor, phase, Now + Cfg.TrapStunSeconds.Value);
                    break;
                }
            }
        }
    }

    /// <summary>Caught message aimed at a bot (host only).</summary>
    bool TryCatchBot(byte target)
    {
        if (!_bots.ContainsKey(target) || !Players.TryGetValue(target, out var p)) return false;
        if (!p.ActiveSurvivor) return true;
        p.Day++;
        if (p.Day > Cfg.Days.Value) { p.Alive = false; _lastVictim = p.Id; BroadcastToast(Tr.Both($"{p.Name} выбыл", $"{p.Name} is out")); }
        else
        {
            Toast(Tr.L($"{p.Name} пойман! День {p.Day}/{Cfg.Days.Value}", $"{p.Name} caught! Day {p.Day}/{Cfg.Days.Value}"), 3, Color.cyan);
            var (anchor, phase, _) = _bots[target];
            _bots[target] = (anchor, phase, Now + 2f);
        }
        _rosterDirty = true;
        CheckRoundEnd();
        return true;
    }
}
