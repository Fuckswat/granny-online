using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyMP;

public sealed partial class Game
{
    // one-shot ids inside BodyState
    const byte OsAttack = 1, OsLook = 2, OsStun = 3, OsCheck = 4;

    float _batReadyAt, _trapReadyAt, _listenReadyAt, _listenUntil, _nextBodySend;
    Vector3 _frozenGrannyPos; float _frozenGrannyYaw;
    int _trapCounter;
    Vector3 _noisePos; float _noiseUntil;
    float _stunUntil;

    public float ListenLeft => Mathf.Max(0, _listenUntil - Now);
    public float BatCd => Mathf.Max(0, _batReadyAt - Now);
    public float TrapCd => Mathf.Max(0, _trapReadyAt - Now);
    public float ListenCd => Mathf.Max(0, _listenReadyAt - Now);
    public bool Listening => Now < _listenUntil;

    Body MyBody => Possess ? R?.Spider : R?.Granny;
    bool _wasHolding;
    bool SomeoneWaking => Players.Values.Any(p => p.Id != Net.LocalId && p.ActiveSurvivor && p.Waking) || (AmSurvivor && GettingUp);

    bool IControlBodies => InRound && (GrannyId >= 0 ? Net.LocalId == GrannyId : Net.IsHost);

    void BecomeGranny()
    {
        StopSpectating();
        if (R?.Granny == null) { Toast(Tr.L("Не найдена Гренни в сцене!", "Granny not found in the scene!"), 5, Color.red); return; }
        try { R.Fpc.enabled = true; R.Cc.enabled = true; } catch { }
        R.Granny.SetAgent(false);
        R.Teleport(R.Granny.Pos, R.Granny.Yaw);
        R.Granny.SetVisible(true); // first person: her body and arms stay visible
        R.Granny.SetCollidersEnabled(false);
        R.Spider?.SetCollidersEnabled(false);
        ApplyGrannySpeeds();
        SetGrannyVision(true);
    }

    // ---- Granny needs no light: the held lantern/flashlight is off, she sees the house as it is ----

    sealed class Vision { public Color Ambient; public UnityEngine.Rendering.AmbientMode Mode; public float Intensity; public bool Fog; public GameObject Lamp; public List<GameObject> Held = new(); public GameObject[] Hand; public GameObject HideButtons; public List<Collider> JumpTriggers = new(); public List<Behaviour> JumpScripts = new(); }
    Vision _vision;

    void SetGrannyVision(bool on)
    {
        try
        {
            if (on && _vision == null && R != null)
            {
                var v = new Vision { Ambient = RenderSettings.ambientLight, Mode = RenderSettings.ambientMode, Intensity = RenderSettings.ambientIntensity, Fog = RenderSettings.fog };
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.68f);
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = false;
                v.Lamp = new GameObject("GrannyMP_Sight");
                v.Lamp.transform.SetParent(R.MainCam.transform, false);
                var l = v.Lamp.AddComponent<Light>();
                l.type = LightType.Point; l.range = 30f * U; l.intensity = 3f; l.shadows = LightShadows.None; l.color = new Color(0.9f, 0.92f, 1f);
                l.renderMode = LightRenderMode.ForcePixel;
                // the game keeps several copies of the held light, under the player and beside him
                v.Hand = GameRefs.FindAll<Transform>(true)
                    .Where(t => t.name.StartsWith("LanternLightHand") || t.name.StartsWith("LanternHand") || t.name.StartsWith("FlashlightHand") || t.name.StartsWith("FicklampaHand"))
                    .Select(t => t.gameObject).ToArray();
                Plugin.L.LogInfo("[vision] held lights: " + string.Join(", ", v.Hand.Select(g => g.name + (g.activeInHierarchy ? "*" : ""))));
                v.HideButtons = Find("AllhidePictures"); // beds, sofa, coffins, car: hiding is for survivors
                // the jump through the kitchen window is a scripted move of the game: not for Granny
                foreach (var c in GameRefs.FindAll<CanJumpOut>(true)) { var t = c.GetComponent<Collider>(); if (t != null && t.enabled) { t.enabled = false; v.JumpTriggers.Add(t); } if (c.enabled) { c.enabled = false; v.JumpScripts.Add(c); } }
                foreach (var c in GameRefs.FindAll<CanJumpIn>(true)) { var t = c.GetComponent<Collider>(); if (t != null && t.enabled) { t.enabled = false; v.JumpTriggers.Add(t); } if (c.enabled) { c.enabled = false; v.JumpScripts.Add(c); } }
                foreach (var c in GameRefs.FindAll<PlayerWindowController>(true)) if (c.enabled) { c.enabled = false; v.JumpScripts.Add(c); }
                _vision = v;
            }
            else if (!on && _vision != null)
            {
                var v = _vision; _vision = null;
                RenderSettings.ambientMode = v.Mode; RenderSettings.ambientLight = v.Ambient; RenderSettings.ambientIntensity = v.Intensity; RenderSettings.fog = v.Fog;
                if (v.Lamp != null) UnityEngine.Object.Destroy(v.Lamp);
                foreach (var g in v.Held) if (g != null) g.SetActive(true);
                On(v.HideButtons);
                foreach (var c in v.JumpTriggers) if (c != null) c.enabled = true;
                foreach (var c in v.JumpScripts) if (c != null) c.enabled = true;
                RestoreDoorLocks();
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[vision] " + e.Message); }
    }

    float _nextHeldCheck;

    /// <summary>The picked-up lantern/flashlight follows the player: keep it off while playing Granny.</summary>
    void HideHeldLights()
    {
        if (_vision == null) return;
        On(_vision.HideButtons, false);
        foreach (var g in _vision.Hand)
            if (g != null && g.activeSelf) { g.SetActive(false); if (!_vision.Held.Contains(g)) _vision.Held.Add(g); }
        if (Now < _nextHeldCheck) return;
        _nextHeldCheck = Now + 0.25f;
        try
        {
            foreach (var f in GameRefs.FindAll<flashlightController>(true))
                if (f.flashlightPlayer != null && f.flashlightPlayer.activeSelf) { f.flashlightPlayer.SetActive(false); _vision.Held.Add(f.flashlightPlayer); }
            foreach (var f in GameRefs.FindAll<lanternController>(true))
                if (f.lanternPlayer != null && f.lanternPlayer.activeSelf) { f.lanternPlayer.SetActive(false); _vision.Held.Add(f.lanternPlayer); }
        }
        catch (Exception e) { Plugin.L.LogWarning("[vision] held: " + e.Message); }
    }

    void KeepStanding()
    {
        try
        {
            if (!R.Fpc.playerCrouch) return;
            foreach (var c in GameRefs.FindAll<playerCrawl>(true)) c.standUp();
            R.Fpc.playerCrouch = false;
        }
        catch { }
    }

    /// <summary>Granny is ALWAYS slower than a survivor: her walk and run never reach theirs (hyper speed aside).</summary>
    public const float GrannyMaxFactor = 0.95f;

    /// <summary>Granny speed as a share of the survivor's: the round's random roll, never above the settings or the cap.</summary>
    float GrannyFactor(float settingMul) => Mathf.Min(GrannySpeedRoll, Mathf.Min(settingMul, GrannyMaxFactor));

    void ApplyGrannySpeeds()
    {
        if (R == null) return;
        try
        {
            if (Possess)
            {
                R.Fpc.m_WalkSpeed = R.BaseRun * Cfg.SpiderSpeedMul.Value * 0.8f;
                R.Fpc.m_RunSpeed = R.BaseRun * Cfg.SpiderSpeedMul.Value;
            }
            else
            {
                R.Fpc.m_WalkSpeed = R.BaseWalk * GrannyFactor(Cfg.GrannyWalkMul.Value);
                R.Fpc.m_RunSpeed = R.BaseRun * GrannyFactor(Cfg.GrannyRunMul.Value);
            }
        }
        catch { }
    }

    // first person: Granny's own bat hangs on the camera like a shooter's weapon and swings on LMB
    GameObject _viewBat;
    float _swingT0 = -9f;
    public Vector3 ViewBatPos = new(0.55f, -0.85f, 1.1f), ViewBatRot = new(-72f, -12f, 0f);
    public float SwingTime = 0.42f;

    Transform _headBone;
    Vector3 _headScale;
    Renderer[] _bodyBats = Array.Empty<Renderer>();

    /// <summary>On Granny's own screen only: no face above the camera and no second bat in the model's hand.</summary>
    void SetOwnBodyParts(bool firstPerson)
    {
        if (_headBone != null) _headBone.localScale = firstPerson ? Vector3.one * 0.001f : _headScale;
        foreach (var r in _bodyBats) if (r != null && r.enabled == firstPerson) r.enabled = !firstPerson;
    }

    void EnsureViewBat()
    {
        if (_viewBat != null || R?.Granny == null) return;
        var bats = new List<Renderer>();
        foreach (var t in R.Granny.Go.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Bip01 Head") { _headBone = t; _headScale = t.localScale; }
            if (t.name.StartsWith("BatBloody")) bats.Add(t.GetComponent<Renderer>());
        }
        _bodyBats = bats.ToArray();
        foreach (var t in R.Granny.Go.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("BatBloody")) continue;
            _viewBat = UnityEngine.Object.Instantiate(t.gameObject);
            _viewBat.name = "GrannyMP_ViewBat";
            _viewBat.transform.SetParent(R.MainCam.transform, false);
            var want = t.lossyScale; var cam = R.MainCam.transform.lossyScale;
            _viewBat.transform.localScale = new Vector3(want.x / cam.x, want.y / cam.y, want.z / cam.z);
            foreach (var r in _viewBat.GetComponentsInChildren<Renderer>(true)) { r.enabled = true; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
            break;
        }
    }

    void UpdateViewBat()
    {
        var show = Session && InRound && AmGranny && !Possess && !Executing && R != null && _spectating == false && _dvMode == 0;
        if (show) EnsureViewBat();
        if (_viewBat == null) return;
        SetOwnBodyParts(show);
        if (_viewBat.activeSelf != show) _viewBat.SetActive(show);
        if (!show) return;
        var p = (Now - _swingT0) / SwingTime;
        var rest = ViewBatPos;
        var restRot = Quaternion.Euler(ViewBatRot);
        var raised = rest + new Vector3(0.1f, 0.3f, -0.15f);
        var raisedRot = restRot * Quaternion.Euler(-35f, 0f, -20f);
        var struck = rest + new Vector3(-0.5f, 0.1f, 0.25f);
        var struckRot = restRot * Quaternion.Euler(88f, 0f, 30f);
        Vector3 pos; Quaternion rot;
        if (p >= 1f) { pos = rest; rot = restRot; }
        else if (p < 0.25f) { var k = p / 0.25f; pos = Vector3.Lerp(rest, raised, k); rot = Quaternion.Slerp(restRot, raisedRot, k); }
        else if (p < 0.5f) { var k = (p - 0.25f) / 0.25f; k *= k; pos = Vector3.Lerp(raised, struck, k); rot = Quaternion.Slerp(raisedRot, struckRot, k); }
        else { var k = (p - 0.5f) / 0.5f; pos = Vector3.Lerp(struck, rest, k); rot = Quaternion.Slerp(struckRot, restRot, k); }
        // a little sway while walking
        if (MyBody != null && MyBody.AnimState == 1) pos += new Vector3(Mathf.Sin(Now * 6f) * 0.015f, Mathf.Abs(Mathf.Cos(Now * 6f)) * 0.02f, 0);
        _viewBat.transform.localPosition = pos;
        _viewBat.transform.localRotation = rot;
    }

    void UpdateGrannyLocal()
    {
        var holding = SomeoneWaking;
        if (holding != _wasHolding) { _wasHolding = holding; Plugin.L.LogInfo("[wake] Granny " + (holding ? "waits" : "free")); }
        if (holding) LockMovement(0.3f);
        UpdateLock();
        KeepInsideHouse();
        KeepGravity();
        WatchStuck();
        KeepDoorsUnlocked();
        UpdateGrannyDoor();
        HideHeldLights();
        KeepStanding();
        var body = MyBody;
        if (body == null) return;

        // the body follows our first-person controller
        var feet = R.Feet();
        var moved = feet - _lastBodyFeet; moved.y = 0; _lastBodyFeet = feet;
        var spd = moved.magnitude / Mathf.Max(0.001f, Time.deltaTime);
        var back = Possess ? 0f : Cfg.GrannyBodyBack.Value * U;
        body.Place(feet - R.Player.transform.forward * back, R.Yaw);
        body.AnimState = (byte)(spd > 0.25f * U ? 1 : 0);
        if (!Possess) body.PlayLoop(body.AnimState == 1 ? body.WalkClip : body.IdleClip);
        UpdateDown(body);
        UpdateRelocate();
        if (Possess && R.Granny != null) { R.Granny.Place(_frozenGrannyPos, _frozenGrannyYaw); R.Granny.AnimState = 0; R.Granny.PlayLoop(R.Granny.IdleClip); }

        // inputs
        if (!MenuOpen && !IsPauseMenuOpen() && _lockUntil <= Now)
        {
            if (Input.GetMouseButtonDown(0)) TryAttack();
            if (Input.GetKeyDown(Cfg.TrapKey.Value)) TryPlaceTrap();
            if (Input.GetKeyDown(Cfg.ListenKey.Value)) TryListen();
            if (Input.GetKeyDown(Cfg.CheckKey.Value) && !Possess) TryCheckSpot();
            if (Input.GetKeyDown(KeyCode.E) && !Possess) GrannyDoorKey();
        }

        // pink x-ray while listening (and briefly for trapped survivors)
        foreach (var p in Players.Values)
            p.Avatar?.SetXray(p.Id != Net.LocalId && p.ActiveSurvivor && !p.Hidden && (Listening || Now < p.RevealUntil)); // hidden in a bed/coffin/car: listening does not show them

        if (Now >= _nextBodySend) { _nextBodySend = Now + 1f / 15f; SendBodies(); }
    }

    Vector3 _lastBodyFeet;
    float _relocateT0; bool _relocated;

    /// <summary>After a kill the day ends for Granny too: black screen, she wakes up somewhere else.</summary>
    void UpdateRelocate()
    {
        if (_relocateT0 <= 0 || Now < _relocateT0) return;
        var t = Now - _relocateT0;
        if (t < 0.02f) LockMovement(2.2f);
        FadeAlpha = t < 0.8f ? t / 0.8f : t < 1.3f ? 1f : Mathf.Clamp01(1f - (t - 1.3f));
        if (t >= 0.9f && !_relocated)
        {
            _relocated = true;
            var pts = R.GrannyStarts.Length > 0 ? R.GrannyStarts : R.GrannyNav.Select(n => n.position).ToArray();
            if (pts.Length > 0)
            {
                var feet = R.Feet();
                var p = pts.OrderByDescending(x => (x - feet).sqrMagnitude).Take(Math.Max(1, pts.Length - 1)).ElementAt(UnityEngine.Random.Range(0, Math.Max(1, pts.Length - 1)));
                R.Teleport(p, UnityEngine.Random.Range(0f, 360f));
                _lastBodyFeet = p;
            }
        }
        if (t >= 2.3f) { _relocateT0 = 0; _relocated = false; FadeAlpha = 0; }
    }

    void SendBodies()
    {
        if (R?.Granny != null) SendBody(0, R.Granny);
        if (Possess && R?.Spider != null) SendBody(1, R.Spider);
    }

    void SendBody(byte id, Body b)
    {
        var w = Net.Begin(Msg.BodyState);
        w.Put(id); w.Put(b.Pos); w.Put(b.Yaw); w.Put(b.AnimState); w.Put(b.OneShotSeq); w.Put(b.OneShotId);
        Net.Send(false);
    }

    void ReadBodyState(byte sender, NetDataReader r)
    {
        var id = r.GetByte(); var pos = r.GetVec3(); var yaw = r.GetFloat(); var anim = r.GetByte(); var seq = r.GetByte(); var os = r.GetByte();
        if (IControlBodies || R == null) return;
        var b = id == 0 ? R.Granny : R.Spider;
        if (b == null) return;
        if (!b.HasTarget || (b.Pos - pos).sqrMagnitude > 64f * U * U) b.Place(pos, yaw);
        b.TargetPos = pos; b.TargetYaw = yaw; b.AnimState = anim; b.HasTarget = true;
        if (seq != b.OneShotSeq)
        {
            b.OneShotSeq = seq; b.OneShotId = os;
            PlayBodyOneShot(b, os);
        }
    }

    void PlayBodyOneShot(Body b, byte os)
    {
        var spider = b == R?.Spider;
        switch (os)
        {
            case OsAttack: b.PlayOneShot(spider ? "eat" : "Hit", spider ? 1.2f : 0.9f); break;
            case OsLook: if (!spider) b.PlayOneShot("Look", 2.2f); break;
            case OsStun: b.PlayOneShot(spider ? "gethit" : "arrowHit", 1.4f); break;
            case OsCheck: if (!spider) b.PlayOneShot("lookBed", 2f); break;
        }
    }

    void LocalOneShot(Body b, byte os)
    {
        b.OneShotSeq++; b.OneShotId = os;
        PlayBodyOneShot(b, os);
    }

    /// <summary>Non-controllers: interpolate the NPC bodies toward the network state.</summary>
    void UpdateRemoteBodies()
    {
        if (R == null || IControlBodies) return;
        foreach (var b in new[] { R.Granny, R.Spider })
        {
            if (b == null || !b.HasTarget) continue;
            var k = Mathf.Clamp01(Time.deltaTime * 12f);
            var pos = Vector3.Lerp(b.Pos, b.TargetPos, k);
            var yaw = Mathf.LerpAngle(b.Yaw, b.TargetYaw, k);
            b.Place(pos, yaw);
            SetBodyDown(b, b.AnimState == 2);
            b.PlayLoop(b.AnimState == 1 ? b.WalkClip : b.IdleClip);
        }
    }


    void TryAttack()
    {
        if (Now < _batReadyAt) return;
        _batReadyAt = Now + Cfg.BatCooldown.Value;
        var body = MyBody;
        LocalOneShot(body, OsAttack);
        if (!Possess) _swingT0 = Now;
        var range = Possess ? Cfg.BiteRange.Value : Cfg.BatRange.Value;
        var target = FindAttackTarget(body.Pos, R.CamFwd, range, Cfg.BatAngle.Value, R.CamPos);
        if (target != null)
        {
            var left = Cfg.Days.Value - target.Day;
            Toast(left > 0 ? Tr.L($"Поймала: {target.Name}. Осталось дней: {left}", $"Caught {target.Name}. Days left: {left}") : Tr.L($"{target.Name} выбыл", $"{target.Name} is out"), 3, Color.white);
            SendCaught(target.Id, (byte)(Possess ? 1 : 0));
            if (Possess) { if (R.GrannyStarts.Length > 0) _frozenGrannyPos = R.GrannyStarts[UnityEngine.Random.Range(0, R.GrannyStarts.Length)]; }
            else if (!Executing) _relocateT0 = Now + 0.5f; // the last catch of the round goes straight to the death scene
        }
    }

    PlayerInfo FindAttackTarget(Vector3 origin, Vector3 fwd, float range, float angle, Vector3 eye)
    {
        fwd.y = 0; fwd.Normalize();
        PlayerInfo best = null; var bestDist = float.MaxValue;
        foreach (var p in Players.Values)
        {
            if (p.Id == Net.LocalId || !p.ActiveSurvivor || !p.HasState || Now - p.LastSeen > 2f || p.Spectating || p.Hidden || p.Waking) continue;
            var d = p.Pos - origin;
            if (Mathf.Abs(d.y) > 2.5f * U) continue;
            d.y = 0;
            var dist = d.magnitude;
            var r = p.Hidden ? range * 0.85f : range;
            if (dist > r) continue;
            if (dist > 0.8f * U && Vector3.Angle(fwd, d) > angle * 0.5f) continue;
            if (!p.Hidden)
            {
                var chest = p.Pos + Vector3.up * (1.2f * U);
                var dir = chest - eye;
                if (Physics.Raycast(eye, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore) && hit.distance < dir.magnitude - 0.9f * U)
                    continue; // wall in between
            }
            if (dist < bestDist) { bestDist = dist; best = p; }
        }
        return best;
    }

    void SendCaught(byte target, byte body)
    {
        var w = Net.Begin(Msg.Caught); w.Put(target); w.Put(body); Net.Send();
        if (target == Net.LocalId) OnCaughtLocal(body);
        else if (Net.IsHost) TryCatchBot(target);
    }

    void TryPlaceTrap()
    {
        if (Possess) { Toast(Tr.L("Анжела не умеет ставить капканы", "Angela cannot place traps"), 2, Color.yellow); return; }
        if (Now < _trapReadyAt) { Toast(Tr.L($"Капкан через {TrapCd:F0} с", $"Trap in {TrapCd:F0} s"), 1.5f, Color.yellow); return; }
        if (MyTrapCount() >= Cfg.MaxTraps.Value) { Toast(Tr.L($"Уже стоит {Cfg.MaxTraps.Value} капканов", $"{Cfg.MaxTraps.Value} traps already placed"), 2, Color.yellow); return; }
        if (R.BearTrapTemplate == null) { Toast(Tr.L("В сцене нет модели капкана", "No trap model in the scene"), 2, Color.red); return; }
        _trapReadyAt = Now + Cfg.TrapCooldown.Value;
        PlaceTrapAt(GroundBelow(R.Feet()), R.Yaw);
        Toast(Tr.L("Капкан поставлен", "Trap placed"), 1.5f, Color.white);
    }

    void PlaceTrapAt(Vector3 pos, float yaw)
    {
        var id = (Net.LocalId << 24) | (++_trapCounter & 0xFFFFFF);
        SpawnTrap(id, pos, yaw);
        var w = Net.Begin(Msg.TrapPlace); w.Put(id); w.Put(pos); w.Put(yaw); Net.Send();
    }

    Vector3 GroundBelow(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * (0.5f * U), Vector3.down, out var hit, 3f * U, ~0, QueryTriggerInteraction.Ignore)) return hit.point;
        return p;
    }

    void TryListen()
    {
        if (Now < _listenReadyAt) { Toast(Tr.L($"Слух через {ListenCd:F0} с", $"Listen in {ListenCd:F0} s"), 1.5f, Color.yellow); return; }
        _listenReadyAt = Now + Cfg.ListenCooldown.Value;
        _listenUntil = Now + Cfg.ListenDuration.Value;
        if (!Possess) LocalOneShot(R.Granny, OsLook);
        Toast(Tr.L("Ты прислушиваешься...", "You are listening..."), 2, new Color(1f, 0.45f, 0.8f));
    }

    void OnNoise(Vector3 pos)
    {
        if (AmGranny) { _noisePos = pos; _noiseUntil = Now + 3f; ShowNoise(pos); }
        if (AiGranny && Net.IsHost) { _aiNoise = pos; _aiNoiseAt = Now; }
    }

    public bool NoiseActive(out Vector3 pos) { pos = _noisePos; return Now < _noiseUntil; }

    // ---- noise: a pink flash at the place, seen through the walls ----

    sealed class NoiseFx { public Vector3 Pos; public float T0; }
    readonly List<NoiseFx> _noiseFx = new();
    const float NoiseFxTime = 3f;
    Texture2D _ringTex;

    void ShowNoise(Vector3 pos)
    {
        foreach (var n in _noiseFx)
            if (Now - n.T0 < 0.8f && (n.Pos - pos).sqrMagnitude < 2.25f * U * U) return;
        _noiseFx.Add(new NoiseFx { Pos = pos, T0 = Now });
    }

    void UpdateNoiseFx() => _noiseFx.RemoveAll(n => Now - n.T0 >= NoiseFxTime || !AmGranny);

    Texture2D RingTex()
    {
        if (_ringTex != null) return _ringTex;
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x - size / 2f, y - size / 2f).magnitude / (size / 2f);
                var a = Mathf.Clamp01(1f - Mathf.Abs(d - 0.85f) / 0.13f);
                tex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        tex.Apply();
        UnityEngine.Object.DontDestroyOnLoad(tex);
        return _ringTex = tex;
    }

    /// <summary>Pink ripples over the place of the noise: an overlay, so walls don't hide it.</summary>
    void DrawNoise(float sw, float sh)
    {
        var pink = new Color(1f, 0.3f, 0.8f);
        foreach (var n in _noiseFx)
        {
            var t = (Now - n.T0) / NoiseFxTime;
            var a = 1f - t;
            var c = pink; c.a = a;
            var world = n.Pos + Vector3.up * (0.6f * U);
            var cam = R.MainCam.transform;
            var local = cam.InverseTransformPoint(world);
            if (local.z > 0.1f && ToScreen(world, out var g) && g.x > 0 && g.x < sw && g.y > 0 && g.y < sh)
            {
                var dist = Vector3.Distance(cam.position, world) / U;
                var size = Mathf.Clamp(420f / Mathf.Max(1f, dist), 70f, 300f);
                var old = GUI.color;
                for (var i = 0; i < 2; i++)
                {
                    var k = (t * 3f + i * 0.5f) % 1f;
                    var r = size * (0.4f + k);
                    GUI.color = new Color(pink.r, pink.g, pink.b, a * (1f - k));
                    GUI.DrawTexture(new Rect(g.x - r, g.y - r, r * 2, r * 2), RingTex());
                }
                GUI.color = old;
                Shadow(new Rect(g.x - 90, g.y - 14, 180, 28), Tr.L("♪ ШУМ", "♪ NOISE"), _sCenter, c);
            }
            else
            {
                var side = local.z < 0 && Mathf.Abs(local.x) < Mathf.Abs(local.z) ? Tr.L("сзади", "behind") : local.x < 0 ? Tr.L("слева", "on the left") : Tr.L("справа", "on the right");
                Shadow(new Rect(sw / 2f - 200, sh * 0.2f, 400, 30), Tr.L("♪ шум ", "♪ noise ") + side, _sMid, c);
            }
        }
    }

    /// <summary>Granny was hit by pepper spray / gun / crossbow / freeze trap on some survivor's machine.</summary>
    void OnStun(float seconds, byte kind)
    {
        if (!IControlBodies || R == null) return;
        if (kind == StunSurvived)
        {
            if (AmGranny) Toast(Tr.L("В тебя попали, но ты выжила!", "You got hit, but you survived!"), 3, new Color(1f, 0.6f, 0.3f), big: true);
            return;
        }
        if (kind == StunPunchMiss)
        {
            // a punch that did not knock her down: she only feels it
            if (AmGranny) Toast(Tr.L("Тебя ударили кулаком, но ты устояла", "Somebody punched you, but you stood firm"), 2.5f, new Color(1f, 0.6f, 0.3f));
            return;
        }
        var down = IsKnockDown(kind); // bullet, blast, car, fists: she drops, the rest only stop her
        if (down && (GrannyProtectLeft > 0 || (AmGranny ? _downUntil > Now : Now < _stunUntil && _aiDown)))
        {
            // she has just stood up (or still lies): the survivor's machine should have refused already, this is the last word
            Plugin.L.LogInfo($"[down] knockdown kind {kind} ignored: protected {GrannyProtectLeft:F1} s, down {(AmGranny ? _downUntil > Now : _aiDown && Now < _stunUntil)}");
            return;
        }
        var b = MyBody ?? R.Granny;
        if (b != null) LocalOneShot(b, OsStun);
        if (AmGranny)
        {
            LockMovement(seconds);
            if (kind == 1) _blindUntil = Now + seconds; // pepper spray: she sees nothing at all
            if (down) { _downUntil = Now + seconds; _dvFrom = Now + DownBlackout; _relocateT0 = 0; _relocated = false; } // the wake-up after a catch waits until she stands up
            var what = kind switch
            {
                1 => Tr.L("Тебе брызнули в глаза перцем!", "Pepper spray in your eyes!"),
                2 => Tr.L("В тебя выстрелили!", "You got shot!"),
                3 => Tr.L("В тебя попала стрела!", "You got hit by an arrow!"),
                4 => Tr.L("Ты попала в ледяную ловушку!", "You stepped into a freeze trap!"),
                5 => Tr.L("Тебя взорвали!", "You got blown up!"),
                6 => Tr.L("Тебя сбила машина!", "You got hit by a car!"),
                StunPunch => Tr.L("Тебя отпиздили кулаками!", "You got knocked out with fists!"),
                _ => Tr.L("Тебя оглушили!", "You are stunned!"),
            };
            Toast($"{what} ({seconds:F0} " + Tr.L("с", "s") + ")", 3, new Color(1f, 0.5f, 0.2f), big: true);
        }
        else { _stunUntil = Now + seconds; _aiDown = down; }
    }

    float _downUntil;
    public float _blindUntil;
    bool _aiDown;

    /// <summary>Knocked out: her screen is black, then she gets up somewhere else.</summary>
    void UpdateDown(Body body)
    {
        if (_downUntil <= 0) return;
        body.AnimState = 2;
        FadeAlpha = Now < _dvFrom ? 1f : 0f; // black while she falls, then she watches or flies (UpdateDownView)
        if (Now < _downUntil) return;
        _downUntil = 0;
        body.AnimState = 0;
        _relocateT0 = Now - 0.85f; // same wake-up in another place as after a catch
        GrannyStoodUp();
        Toast(Tr.L($"Ты встала! {ProtectAfterUp:F0} с тебя нельзя свалить", $"You are up! Nobody can knock you down for {ProtectAfterUp:F0} s"), 4f, new Color(0.5f, 1f, 0.6f), big: true);
    }

    /// <summary>Like the game: the body falls apart as a ragdoll and is gone, nothing is left to walk around.</summary>
    void SetBodyDown(Body b, bool down)
    {
        if (b.IsDown == down) return;
        if (b.IsDown && b == R?.Granny) GrannyStoodUp(); // remote machines and the AI: she gets up
        b.IsDown = down;
        b.SetVisible(!down);
        b.SetCollidersEnabled(!down);
        if (!down) { b.DisableTriggers(); return; }
        try
        {
            var tpl = R.GrannyAi?.grannyRagdoll;
            if (b == R.Granny && tpl != null)
            {
                var go = UnityEngine.Object.Instantiate(tpl.gameObject, b.Pos, b.Go.transform.rotation);
                go.SetActive(true);
                UnityEngine.Object.Destroy(go, 7f);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[down] ragdoll: " + e.Message); }
    }

    // ---- checking beds and wardrobes ----

    float _checkReadyAt;
    public float CheckCd => Mathf.Max(0, _checkReadyAt - Now);
    const float CheckCooldown = 30f, CheckRange = 2.4f, CheckHeight = 1.4f;

    /// <summary>
    /// Q works ONLY on places where a survivor hides with the R key (beds, coffins, the car): the player must be hidden
    /// that way (InSpot = hidden by R) and Granny must stand right at that place, on the same floor, facing it.
    /// Wardrobes you walk into, crawl spaces and corners are never checked.
    /// </summary>
    void TryCheckSpot()
    {
        if (Now < _checkReadyAt) { Toast(Tr.L($"Проверка через {CheckCd:F0} с", $"Check in {CheckCd:F0} s"), 1.5f, Color.white); return; }
        _checkReadyAt = Now + CheckCooldown;
        LocalOneShot(R.Granny, OsCheck);
        var feet = R.Feet();
        foreach (var p in Players.Values)
        {
            if (p.Id == Net.LocalId || !p.ActiveSurvivor || !p.HasState || !p.Hidden || !p.InSpot || p.Waking) continue;
            var d = p.Pos - feet;
            if (Mathf.Abs(d.y) > CheckHeight * U) continue; // another floor, on top of a wardrobe, in the attic hole...
            d.y = 0;
            if (d.magnitude > CheckRange * U) continue;
            var look = R.CamFwd; look.y = 0;
            if (d.magnitude > 1f * U && look.sqrMagnitude > 0.0001f && Vector3.Angle(look.normalized, d) > 70f) continue; // she has to look at the bed
            Toast(Tr.L($"Нашла: {p.Name}!", $"Found {p.Name}!"), 3, Color.white);
            SendCaught(p.Id, 3);
            _relocateT0 = Now + 2f;
            return;
        }
        _checkReadyAt = Now + 8f; // an empty bed costs less than a find
        Toast(Tr.L("Здесь никого нет", "Nobody here"), 2, Color.white);
    }

    // ---- falls: no fall damage for Granny, but gravity must stay ----

    float _baseGravity = float.NaN, _baseGravMul = float.NaN;
    bool _gravityWarned, _baseTempGravity;

    /// <summary>Captured once per scene, before anyone falls.</summary>
    void CaptureGravity()
    {
        if (!float.IsNaN(_baseGravity) || R?.Fpc == null) return;
        try { _baseGravity = R.Fpc.gravity; _baseGravMul = R.Fpc.m_GravityMultiplier; _baseTempGravity = R.Fpc.tempGravity; Plugin.L.LogInfo($"[fall] gravity {_baseGravity}, multiplier {_baseGravMul}, temp {_baseTempGravity}"); } catch { }
    }

    /// <summary>
    /// The game's fall script (blocked for Granny, she can't die from a fall) switches gravity off for the fall and
    /// back on at its end. Without the end she flew for the rest of the game after jumping from the 3rd floor.
    /// </summary>
    void KeepGravity()
    {
        if (R?.Fpc == null || float.IsNaN(_baseGravity)) return;
        try
        {
            var f = R.Fpc;
            if (f.gravity != _baseGravity || f.m_GravityMultiplier != _baseGravMul || f.tempGravity != _baseTempGravity)
            {
                if (!_gravityWarned) { _gravityWarned = true; Plugin.L.LogWarning($"[fall] gravity was {f.gravity}/{f.m_GravityMultiplier} temp={f.tempGravity}, restored"); }
                f.gravity = _baseGravity; f.m_GravityMultiplier = _baseGravMul; f.tempGravity = _baseTempGravity;
            }
            if (R.Cc.isGrounded && (f.PlayerIsFalling || f.fallTimerStarted || f.playerLandHard))
            {
                f.PlayerIsFalling = false; f.fallTimerStarted = false; f.playerLandHard = false;
            }
        }
        catch (Exception e) { if (!_gravityWarned) { _gravityWarned = true; Plugin.L.LogWarning("[fall] " + e.Message); } }
    }

    // ---- getting stuck: she walks, but does not move ----

    Vector3 _stuckFrom; float _stuckSince = -1f, _unstuckReadyAt;

    void WatchStuck()
    {
        if (Possess || R?.Cc == null || !R.Fpc.enabled || _lockUntil > Now || _downUntil > Now || MenuOpen || IsPauseMenuOpen()) { _stuckSince = -1f; return; }
        if (Input.GetKeyDown(Cfg.UnstuckKey.Value))
        {
            if (Now < _unstuckReadyAt) Toast(Tr.L($"Можно через {_unstuckReadyAt - Now:F0} с", $"Again in {_unstuckReadyAt - Now:F0} s"), 1.5f, Color.white);
            else { _unstuckReadyAt = Now + 15f; Unstick("key"); }
            return;
        }
        var walking = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D);
        var feet = R.Feet();
        if (!walking) { _stuckSince = -1f; return; }
        if (_stuckSince < 0 || (feet - _stuckFrom).sqrMagnitude > 0.25f * U * U) { _stuckFrom = feet; _stuckSince = Now; return; }
        if (Now - _stuckSince > 2.5f) { _stuckSince = -1f; Unstick("auto"); }
    }

    /// <summary>Back onto Granny's navmesh: the nearest walkable point, a bit further if that is where she already is.</summary>
    void Unstick(string why)
    {
        try
        {
            var feet = R.Feet();
            Vector3? to = null;
            if (NavMesh.SamplePosition(feet, out var hit, 3f * U, NavMesh.AllAreas) && (hit.position - feet).sqrMagnitude > 0.04f * U * U) to = hit.position;
            if (to == null)
                for (var i = 0; i < 12 && to == null; i++)
                {
                    var probe = feet + Quaternion.Euler(0, i * 30f, 0) * Vector3.forward * (1.2f * U);
                    if (NavMesh.SamplePosition(probe, out var h2, 1.2f * U, NavMesh.AllAreas) && Mathf.Abs(h2.position.y - feet.y) < 1.5f * U) to = h2.position;
                }
            if (to == null && _hasNav) to = _lastNav;
            if (to == null) { Plugin.L.LogWarning($"[stuck] {why}: nowhere to go from {feet}"); return; }
            Plugin.L.LogInfo($"[stuck] {why}: {feet} -> {to.Value}");
            R.Teleport(to.Value + Vector3.up * (0.1f * U), R.Yaw);
            _lastBodyFeet = to.Value;
            if (why == "key") Toast(Tr.L("Вытащила тебя", "Unstuck"), 1.5f, Color.white);
        }
        catch (Exception e) { Plugin.L.LogWarning("[stuck] " + e.Message); }
    }

    // ---- staying inside; doors that are locked for survivors ----

    Vector3 _lastNav; bool _hasNav; float _nextNavCheck;

    /// <summary>Granny walks where her AI walks: stepping or falling off her navmesh (out of a window) brings her back.</summary>
    void KeepInsideHouse()
    {
        if (Possess || Now < _nextNavCheck) return;
        _nextNavCheck = Now + 0.15f;
        try
        {
            if (!R.Cc.enabled || !R.Cc.isGrounded) return;
            var f = R.Feet();
            if (NavMesh.SamplePosition(f, out var hit, 1.4f * U, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - f.y) < 1.5f * U) { _lastNav = f; _hasNav = true; }
            else if (_hasNav) { Plugin.L.LogInfo($"[nav] outside at {f}, back to {_lastNav}"); R.Teleport(_lastNav, R.Yaw); }
        }
        catch (Exception e) { Plugin.L.LogWarning("[nav] " + e.Message); }
    }

    DoorRef _eDoor; bool _eWasOpen; float _eAt;

    /// <summary>Granny's E: the game gets the first try; a door it refuses (locked some other way) she opens herself.</summary>
    void GrannyDoorKey()
    {
        DoorRef best = null; var bestD = 3.2f * U;
        foreach (var d in _doors)
        {
            if (d.Comp == null || d.OpenClip == null) continue;
            var to = d.Comp.transform.position - R.CamPos;
            if (to.magnitude >= bestD || Vector3.Dot(to.normalized, R.CamFwd) < 0.35f) continue;
            best = d; bestD = to.magnitude;
        }
        if (best == null) return;
        _eDoor = best; _eWasOpen = best.GetOpen(); _eAt = Now + 0.2f;
    }

    void UpdateGrannyDoor()
    {
        if (_eDoor == null || Now < _eAt) return;
        var d = _eDoor; _eDoor = null;
        try
        {
            if (d.Comp == null || d.GetOpen() != _eWasOpen) return; // the game opened it
            var open = !_eWasOpen;
            d.SetOpen(open);
            if (d.Anim != null) { d.Anim.Stop(); d.Anim.Play(open ? d.OpenClip : d.CloseClip); }
            var clip = d.Sound?.Invoke(open);
            if (clip != null && d.Audio != null) d.Audio.PlayOneShot(clip);
            if (d.Obstacle != null) d.Obstacle.enabled = !open;
            d.LastOpen = open;
            var w = Net.Begin(Msg.Door); WriteDoor(w, d); Net.Send();
            Plugin.L.LogInfo($"[door] Granny {(open ? "opened" : "closed")} {d.Comp.name}");
        }
        catch (Exception e) { Plugin.L.LogWarning("[door] " + e.Message); }
    }

    float _nextDoorKeys;

    /// <summary>
    /// Locked doors stay locked for survivors, but not for Granny: on her machine the lock is simply off, so the game
    /// opens them in its normal way. The lock state on the network does not change.
    /// </summary>
    void KeepDoorsUnlocked()
    {
        if (Now < _nextDoorKeys) return;
        _nextDoorKeys = Now + 0.5f;
        foreach (var d in _doors)
            try { if (d.Comp != null && d.GetLocked != null && d.SetLocked != null && d.GetLocked()) d.SetLocked(false); } catch { }
    }

    void RestoreDoorLocks()
    {
        foreach (var d in _doors)
            try { if (d.Comp != null && d.SetLocked != null) d.SetLocked(d.LastLocked); } catch { }
    }

    public const byte StunPunch = 7, StunPunchMiss = 8;
    static bool IsKnockDown(byte kind) => kind == 2 || kind == 3 || kind == 5 || kind == 6 || kind == StunPunch; // the arrow kills her in the original too
    float _lastArrowStun = -99;

    public void ReportGrannyStun(float seconds, byte kind)
    {
        if (!Session || !InRound) return;
        if (IsKnockDown(kind))
        {
            if (R?.Granny?.IsDown == true) { Toast(Tr.L("Гренни и так лежит", "Granny is already down"), 1.6f, Color.white); return; }
            var safe = GrannyProtectLeft;
            if (safe > 0)
            {
                Toast(Tr.L($"Попал, но Гренни только встала: её нельзя свалить ещё {Mathf.Ceil(safe):F0} с", $"Hit, but Granny has just got up: she can't be knocked down for {Mathf.Ceil(safe):F0} s"), 2.5f, new Color(1f, 0.6f, 0.3f));
                return;
            }
        }
        if (IsWeaponKill(kind))
        {
            // the game's own hit and our own check can both see the same shot / arrow
            if (_lastStunAt.TryGetValue(kind, out var last) && Now - last < 1.5f) return;
            _lastStunAt[kind] = Now;
            seconds = kind == 3 ? KnockArrow : KnockGun; // as long as in the original game (host's difficulty)
            if (UnityEngine.Random.Range(0, 100) < Mathf.Clamp(Cfg.GrannySurviveChance.Value, 0, 100))
            {
                Plugin.L.LogInfo($"[knock] kind {kind}: Granny survived");
                Toast(Tr.L("Попал, но Гренни выжила!", "Hit, but Granny survived!"), 3, new Color(1f, 0.5f, 0.3f), big: true);
                var ws = Net.Begin(Msg.Stun); ws.Put(0f); ws.Put(StunSurvived); Net.Send();
                OnStun(0f, StunSurvived);
                return;
            }
        }
        var fell = IsKnockDown(kind);
        if (kind != StunPunchMiss)
            Toast(kind == StunPunch ? Tr.L($"НОКАУТ! Гренни упала, встанет через {seconds:F0} с", $"KNOCKOUT! Granny is down, back in {seconds:F0} s")
                : fell ? Tr.L($"Попал! Гренни упала, встанет через {seconds:F0} с", $"Hit! Granny is down, back in {seconds:F0} s") : Tr.L($"Попал! Гренни оглушена на {seconds:F0} с", $"Hit! Granny is stunned for {seconds:F0} s"), 4, Color.white, big: kind == StunPunch);
        var w = Net.Begin(Msg.Stun); w.Put(seconds); w.Put(kind); Net.Send();
        OnStun(seconds, kind);
    }

    public void ReportNoise(Vector3 pos)
    {
        if (!Session || !InRound || !AmSurvivor) return;
        var w = Net.Begin(Msg.Noise); w.Put(pos); Net.Send();
        OnNoise(pos);
    }


    void RecomputePossess()
    {
        if (!Net.IsHost || !InRound) return;
        var any = Players.Values.Any(p => p.ActiveSurvivor && p.InZone);
        if (any == Possess) return;
        var w = Net.Begin(Msg.Possess); w.Put(any); Net.Send();
        SetPossess(any);
    }

    void SetPossess(bool active)
    {
        if (Possess == active) return;
        Possess = active;
        if (R == null) return;
        if (AmGranny && Me.Alive && R.Spider != null)
        {
            if (active)
            {
                _frozenGrannyPos = R.Feet(); _frozenGrannyYaw = R.Yaw;
                R.Granny.Place(_frozenGrannyPos, _frozenGrannyYaw);
                R.Granny.SetVisible(true);
                var start = IsInSpiderCellar(R.Spider.Pos) ? R.Spider.Pos : R.SpiderStart;
                R.Teleport(start, R.Spider.Yaw);
                R.Spider.Place(start, R.Spider.Yaw);
                R.Spider.SetVisible(false);
                R.Spider.SetCollidersEnabled(false);
                Toast(Tr.L("Кто-то залез в подвал! Ты управляешь АНЖЕЛОЙ.\nЛКМ — укус", "Someone entered the cellar! You control ANGELA.\nLMB - bite"), 5, new Color(1f, 0.4f, 0.8f), big: true);
            }
            else
            {
                R.Teleport(_frozenGrannyPos, _frozenGrannyYaw);
                R.Granny.SetVisible(false);
                R.Spider.SetVisible(true);
                Toast(Tr.L("В подвале никого — ты снова Гренни", "Nobody in the cellar: you are Granny again"), 3, Color.white);
            }
            ApplyGrannySpeeds();
        }
        else if (AmSurvivor && active && _myInZone)
            Toast(Tr.L("Анжела проснулась...", "Angela woke up..."), 3, new Color(1f, 0.4f, 0.8f));

        if (AiGranny && Net.IsHost)
        {
            R.Spider?.SetAgent(active);
            if (active && R.Spider != null && !IsInSpiderCellar(R.Spider.Pos))
            {
                R.Spider.Place(R.SpiderStart, R.Spider.Yaw);
                try { if (R.Spider.Agent != null && R.Spider.Agent.enabled) R.Spider.Agent.Warp(R.SpiderStart); } catch { }
            }
        }
    }


    sealed class AiState
    {
        public byte TargetId = 255;
        public Vector3 LastSeen; public bool HasLastSeen;
        public float NextThink, NextTrap, AttackReady, NextDoor;
        public Vector3 Dest; public bool HasDest;
    }
    readonly AiState _aiG = new(), _aiS = new();
    Vector3 _aiNoise; float _aiNoiseAt = -99;

    void StartAi()
    {
        if (R?.Granny == null) return;
        R.Granny.SetVisible(true);
        R.Granny.SetAgent(true);
        try { if (R.Granny.Agent != null && R.Granny.Agent.enabled && R.Granny.Agent.isOnNavMesh) R.Granny.Agent.Warp(R.Granny.Pos); } catch { }
        _aiG.NextTrap = Now + 30f; _aiG.HasDest = false; _aiG.HasLastSeen = false;
        _aiG.AttackReady = Now + 5f; _aiS.AttackReady = Now + 5f; // grace period
    }

    void UpdateAi()
    {
        if (R == null) return;
        if (R.Granny != null) AiTick(R.Granny, _aiG, false);
        if (Possess && R.Spider != null) AiTick(R.Spider, _aiS, true);
        if (Now >= _nextBodySend) { _nextBodySend = Now + 1f / 15f; SendBodies(); }
    }

    IEnumerable<(byte id, Vector3 pos, bool hidden, bool crouch, float speed)> AiTargets(bool spiderOnly)
    {
        if (AmSurvivor && Me.Alive && !Me.Escaped && _caughtT0 <= 0 && (!spiderOnly || _myInZone))
            yield return (Net.LocalId, R.Feet(), IsHiding() || InClosedWardrobe(), R.Fpc.playerCrouch, _speed);
        foreach (var p in Players.Values)
            if (p.Id != Net.LocalId && p.ActiveSurvivor && p.HasState && !p.Spectating && (!spiderOnly || p.InZone))
                yield return (p.Id, p.Pos, p.OutOfSight, p.Crouch, p.Speed);
    }

    void AiTick(Body b, AiState s, bool spider)
    {
        var agent = b.Agent;
        if (agent == null) return;
        if (!agent.enabled) b.SetAgent(true);
        if (!agent.enabled || !agent.isOnNavMesh) return;

        var stunned = Now < _stunUntil || SomeoneWaking;
        agent.isStopped = stunned;
        var moving = agent.velocity.magnitude > 0.3f * U;
        b.AnimState = (byte)(moving ? 1 : 0);
        b.PlayLoop(moving ? b.WalkClip : b.IdleClip);
        var aiDown = Now < _stunUntil && _aiDown;
        if (aiDown) b.AnimState = 2;
        SetBodyDown(b, aiDown);
        if (stunned || Now < s.NextThink) return;
        s.NextThink = Now + 0.25f;

        var eye = (spider ? b.Pos + Vector3.up * (1f * U) : R.GrannyHead.position);
        var sight = (spider ? 10f : 14f) * U;
        (byte id, Vector3 pos, bool hidden, bool crouch, float speed)? best = null; var bestD = float.MaxValue;
        foreach (var t in AiTargets(spider))
        {
            var d = Vector3.Distance(b.Pos, t.pos);
            var heard = d < 5f * U && !t.crouch && t.speed > R.BaseWalk * 0.6f;
            var seen = false;
            if (!t.hidden && d < sight)
            {
                var chest = t.pos + Vector3.up * (1.2f * U);
                var dir = chest - eye;
                var fwd = b.Go.transform.forward;
                var inFov = Vector3.Angle(fwd, dir) < 75f || d < 2.5f * U;
                seen = inFov && (!Physics.Raycast(eye, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore) || hit.distance > dir.magnitude - 1.2f * U);
            }
            if ((seen || heard) && d < bestD) { bestD = d; best = t; }
        }

        if (best != null)
        {
            s.TargetId = best.Value.id; s.LastSeen = best.Value.pos; s.HasLastSeen = true;
            agent.speed = R.BaseRun * (spider ? 0.9f : GrannyFactor(0.8f));
            SetDest(agent, s, best.Value.pos);
            var range = (spider ? Cfg.BiteRange.Value : Cfg.BatRange.Value) * 0.85f;
            if (bestD < range && Now >= s.AttackReady)
            {
                s.AttackReady = Now + Cfg.BatCooldown.Value + 0.6f;
                LocalOneShot(b, OsAttack);
                SendCaught(best.Value.id, (byte)(spider ? 1 : 0));
                s.HasLastSeen = false;
                s.AttackReady = Now + 6f; // let the victim respawn before the next swing
                if (!spider && R.GrannyNav.Length > 0) AiResetAfterCatch(b);
            }
        }
        else
        {
            agent.speed = R.BaseWalk * (spider ? 1.0f : GrannyFactor(0.9f));
            if (!spider && Now - _aiNoiseAt < 8f) { SetDest(agent, s, _aiNoise); _aiNoiseAt = -99; }
            else if (s.HasLastSeen)
            {
                SetDest(agent, s, s.LastSeen);
                if (Vector3.Distance(b.Pos, s.LastSeen) < 1.5f * U) s.HasLastSeen = false;
            }
            else if (!s.HasDest || (!agent.pathPending && agent.remainingDistance < 1.2f * U))
            {
                if (spider)
                {
                    var rnd = R.SpiderStart + new Vector3(UnityEngine.Random.Range(-8f, 8f), 0, UnityEngine.Random.Range(-8f, 8f)) * U;
                    if (NavMesh.SamplePosition(rnd, out var nh, 4f * U, NavMesh.AllAreas)) SetDest(agent, s, nh.position);
                }
                else if (R.GrannyNav.Length > 0)
                    SetDest(agent, s, R.GrannyNav[UnityEngine.Random.Range(0, R.GrannyNav.Length)].position);
            }
        }

        if (!spider)
        {
            if (Now >= s.NextTrap && MyTrapCount() < Cfg.MaxTraps.Value && R.BearTrapTemplate != null && best == null)
            {
                s.NextTrap = Now + UnityEngine.Random.Range(35f, 55f);
                PlaceTrapAt(GroundBelow(b.Pos), b.Yaw);
            }
            if (Now >= s.NextDoor && agent.velocity.magnitude < 0.1f * U && s.HasDest)
            {
                s.NextDoor = Now + 1f;
                AiOpenNearbyDoor(b.Pos);
            }
        }
    }

    /// <summary>Like the original game: after a catch Granny goes back far away from the bed.</summary>
    void AiResetAfterCatch(Body b)
    {
        Transform best = null; var bestD = -1f;
        for (var i = 0; i < 4; i++)
        {
            var t = R.GrannyNav[UnityEngine.Random.Range(0, R.GrannyNav.Length)];
            var d = (t.position - R.PlayerSpawn).sqrMagnitude;
            if (d > bestD) { bestD = d; best = t; }
        }
        if (best == null) return;
        try { if (b.Agent != null && b.Agent.enabled) b.Agent.Warp(best.position); else b.Place(best.position, b.Yaw); } catch { }
        _aiG.HasDest = false; _aiG.HasLastSeen = false;
    }

    void SetDest(NavMeshAgent agent, AiState s, Vector3 p)
    {
        try { agent.SetDestination(p); s.Dest = p; s.HasDest = true; } catch { }
    }
}
