using UnityEngine;

namespace GrannyMP;

/// <summary>
/// Survivor abilities (1.1): hyper speed and fists. Plus our own crossbow hit check: Granny's trigger colliders are
/// off in a session, so the game's arrow can't register a hit on her by itself (same story as the shotgun).
/// </summary>
public sealed partial class Game
{

    float _boostUntil, _boostReadyAt, _boostRestoreUntil;
    bool _boostApplied;

    public bool Boosting => AmSurvivor && Now < _boostUntil;
    public float BoostLeft => Mathf.Max(0, _boostUntil - Now);
    public float BoostCd => Mathf.Max(0, _boostReadyAt - Now);

    void TryBoost()
    {
        if (Boosting) return;
        if (Now < _boostReadyAt) { Toast(Tr.L($"Гиперскорость через {BoostCd:F0} с", $"Hyper speed in {BoostCd:F0} s"), 1.5f, Color.yellow); return; }
        _boostUntil = Now + Cfg.BoostDuration.Value;
        _boostReadyAt = _boostUntil + Cfg.BoostCooldown.Value;
        Toast(Tr.L($"ГИПЕРСКОРОСТЬ! x{Cfg.BoostMul.Value:0.#} на {Cfg.BoostDuration.Value:F0} с", $"HYPER SPEED! x{Cfg.BoostMul.Value:0.#} for {Cfg.BoostDuration.Value:F0} s"), 2.5f, new Color(0.4f, 0.9f, 1f), big: true);
        Plugin.L.LogInfo("[boost] on");
    }

    /// <summary>Speeds are written every frame while boosted: the game's own crouch/stand code may write them too.</summary>
    void UpdateBoost()
    {
        if (R?.Fpc == null) return;
        try
        {
            var crouch = R.Fpc.playerCrouch;
            if (Boosting && Me.Alive && !Me.Escaped && !_spectating)
            {
                if (!crouch)
                {
                    var mul = Mathf.Max(1f, Cfg.BoostMul.Value);
                    R.Fpc.m_WalkSpeed = R.BaseWalk * mul;
                    R.Fpc.m_RunSpeed = R.BaseRun * mul;
                }
                _boostApplied = true;
                return;
            }
            if (_boostApplied)
            {
                _boostApplied = false;
                if (_boostUntil > 0 && Now >= _boostUntil && AmSurvivor && Me.Alive) Toast(Tr.L("Гиперскорость закончилась", "Hyper speed is over"), 2f, Color.white);
                _boostUntil = 0;
                _boostRestoreUntil = Now + 1f; // keep the normal speed for a moment, in case the game writes its saved one back
            }
            if (_boostRestoreUntil > 0)
            {
                if (crouch) { _boostRestoreUntil = Mathf.Max(_boostRestoreUntil, Now + 1f); return; } // wait until the player stands up
                if (!AmGranny) { R.Fpc.m_WalkSpeed = R.BaseWalk; R.Fpc.m_RunSpeed = R.BaseRun; }
                if (Now >= _boostRestoreUntil) _boostRestoreUntil = 0;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[boost] " + e.Message); }
    }

    void StopBoost()
    {
        if (_boostUntil > Now) _boostUntil = Now - 0.001f;
        UpdateBoost();
    }


    Transform _hidePics;
    bool _hidePicsTried, _rHide, _wasHiding;
    float _rPressedAt = -99f, _hideIconAt = -99f;

    /// <summary>Hidden right now, and it was the R key (or the game's hide icon) that hid the player.</summary>
    bool HiddenByR => _rHide && IsHiding();

    /// <summary>
    /// Every frame: remember the R key and the game's hide icons (bed / coffin / car pictures under AllhidePictures).
    /// When the game switches the player off right after that, the player is hidden "with R".
    /// </summary>
    void TrackRHide()
    {
        if (R == null || !AmSurvivor) { _rHide = false; _wasHiding = false; return; }
        WatchCar();
        if (Input.GetKeyDown(KeyCode.R)) _rPressedAt = Now;
        try
        {
            if (_hidePics == null && !_hidePicsTried) { _hidePicsTried = true; _hidePics = Find("AllhidePictures")?.transform; }
            if (_hidePics != null)
                for (var i = 0; i < _hidePics.childCount; i++)
                {
                    var c = _hidePics.GetChild(i);
                    if (c != null && c.gameObject.activeInHierarchy) { _hideIconAt = Now; break; }
                }
        }
        catch { }
        var hiding = IsHiding() && !GettingUp;
        if (hiding && !_wasHiding)
        {
            _rHide = Now - _rPressedAt < 3f || Now - _hideIconAt < 3f;
            Plugin.L.LogInfo($"[hide] hidden, by R: {_rHide}");
        }
        if (!hiding) _rHide = false;
        _wasHiding = hiding;
    }

    // ---- the car: hiding in it reportedly does not work online; log every step to see where it breaks ----

    insideCarButton _carBtn;
    bool _carTried, _carIconWas, _carHidingWas, _carDrivingWas;
    float _carCheckAt;

    void WatchCar()
    {
        try
        {
            if (!_carTried) { _carTried = true; var a = GameRefs.FindAll<insideCarButton>(true); if (a.Length > 0) _carBtn = a[0]; Plugin.L.LogInfo("[car] hide button " + (_carBtn != null ? "found" : "NOT found")); }
            if (_carBtn == null) return;
            var icon = _carBtn.gameObject.activeInHierarchy;
            bool hiding = false, driving = false;
            try { hiding = _carBtn.PlayerHiding; driving = _carBtn.drivingCar; } catch { }
            string State() => $"icon={icon} hiding={hiding} driving={driving} player={R.Player.activeInHierarchy} cc={R.Cc.enabled} fpc={R.Fpc.enabled} feet={R.Feet()} granny={(R.Granny != null ? Vector3.Distance(R.Granny.Pos, R.Feet()) / U : -1):F1}m lock={_lockUntil > Now} hidingNow={IsHiding()}";
            if (icon != _carIconWas) { _carIconWas = icon; Plugin.L.LogInfo("[car] icon changed: " + State()); }
            if (hiding != _carHidingWas || driving != _carDrivingWas) { _carHidingWas = hiding; _carDrivingWas = driving; Plugin.L.LogInfo("[car] state changed: " + State()); }
            if (icon && Input.GetKeyDown(KeyCode.R)) { Plugin.L.LogInfo("[car] R pressed: " + State()); _carCheckAt = Now + 0.6f; }
            if (_carCheckAt > 0 && Now >= _carCheckAt) { _carCheckAt = 0; Plugin.L.LogInfo("[car] 0.6 s after R: " + State()); }
        }
        catch (Exception e) { Plugin.L.LogWarning("[car] " + e.Message); _carBtn = null; }
    }


    int _punchesLeft;
    float _punchReadyAt, _punchT0 = -9f;
    const float PunchCooldown = 1.2f, PunchShowTime = 0.85f;

    public int PunchesLeft => _punchesLeft;
    public float PunchCd => Mathf.Max(0, _punchReadyAt - Now);
    bool PunchShowing => AmSurvivor && Now - _punchT0 < PunchShowTime;

    void ResetSurvivorAbilities()
    {
        StopBoost();
        _boostUntil = 0; _boostReadyAt = Now + 5f;
        _punchesLeft = Mathf.Max(0, Cfg.PunchCharges.Value);
        _punchReadyAt = Now + 3f; _punchT0 = -9f;
        _arrows.Clear(); _bowLoaded = false; _bowRayPending = false;
        _deathDropUntil = 0;
        HidePunchArms();
    }

    void OnSceneChangedAbilities()
    {
        _boostApplied = false; _boostUntil = 0; _boostRestoreUntil = 0;
        _fists = null; _punchT0 = -9f;
        _bow = null; _arrowPrefab = null; _bowLoaded = false; _bowRayPending = false;
        _arrows.Clear(); _seenArrows.Clear();
        _voidY = null;
        _hidePics = null; _hidePicsTried = false; _rHide = false; _wasHiding = false;
        _carBtn = null; _carTried = false; _carCheckAt = 0; _deathDropUntil = 0;
        _fallClip = null; _fallClipTried = false;
        _baseGravity = float.NaN; _baseGravMul = float.NaN; _gravityWarned = false;
    }

    void UpdateSurvivorAbilities()
    {
        TrackRHide();
        UpdateBoost();
        if (!AmSurvivor || R == null || !Me.Alive || Me.Escaped || _spectating || GettingUp) return;
        if (MenuOpen || IsPauseMenuOpen() || IsHiding()) return;
        if (Input.GetKeyDown(Cfg.BoostKey.Value)) TryBoost();
        if (Input.GetKeyDown(Cfg.PunchKey.Value)) TryPunch();
    }

    void TryPunch()
    {
        if (Now < _punchReadyAt) return;
        if (_punchesLeft <= 0)
        {
            _punchReadyAt = Now + 1f;
            Toast(Tr.L($"Кулаки кончились: {Cfg.PunchCharges.Value} удара за раунд", $"No punches left: {Cfg.PunchCharges.Value} per round"), 2f, Color.yellow);
            return;
        }
        _punchReadyAt = Now + PunchCooldown;
        _punchT0 = Now;
        ShowPunchArms();

        var g = R.Granny;
        if (g == null || !GrannyInReach(g))
        {
            Toast(Tr.L("Мимо: Гренни слишком далеко (удар не потрачен)", "Missed: Granny is too far (punch not used)"), 1.6f, Color.white);
            return;
        }
        if (g.IsDown) { Toast(Tr.L("Гренни и так лежит", "Granny is already down"), 1.6f, Color.white); return; }
        if (GrannyProtectLeft > 0) { Toast(Tr.L($"Гренни только встала: её нельзя свалить ещё {Mathf.Ceil(GrannyProtectLeft):F0} с (удар не потрачен)", $"Granny has just got up: no knocking her down for {Mathf.Ceil(GrannyProtectLeft):F0} s (punch not used)"), 2f, Color.white); return; }

        _punchesLeft--;
        ReportNoise(R.Feet());
        try { if (R.HitClip != null) AudioSource.PlayClipAtPoint(R.HitClip, g.Pos + Vector3.up * U, 0.8f); } catch { }
        var roll = UnityEngine.Random.Range(0, 100);
        var ok = roll < Mathf.Clamp(Cfg.PunchChance.Value, 0, 100);
        Plugin.L.LogInfo($"[punch] roll {roll} < {Cfg.PunchChance.Value}: {ok}, left {_punchesLeft}");
        if (ok) ReportGrannyStun(Cfg.PunchDownTime.Value, StunPunch);
        else
        {
            ReportGrannyStun(0f, StunPunchMiss);
            Toast(Tr.L($"Гренни устояла! Осталось ударов: {_punchesLeft}", $"Granny stood firm! Punches left: {_punchesLeft}"), 2.5f, new Color(1f, 0.6f, 0.3f));
        }
    }

    /// <summary>
    /// Granny found us in a hiding place (Q): the fists go off by themselves if any are left.
    /// A lucky one knocks her down and we stay hidden; otherwise the catch goes on (the punch is spent).
    /// </summary>
    bool AutoPunchFromHiding()
    {
        if (_punchesLeft <= 0 || GrannyProtectLeft > 0) return false; // she has just got up: the fists can't stop her
        _punchesLeft--;
        var roll = UnityEngine.Random.Range(0, 100);
        var ok = roll < Mathf.Clamp(Cfg.PunchChance.Value, 0, 100);
        Plugin.L.LogInfo($"[punch] auto from hiding: roll {roll} < {Cfg.PunchChance.Value}: {ok}, left {_punchesLeft}");
        try { if (R.HitClip != null) AudioSource.PlayClipAtPoint(R.HitClip, R.CamPos, 0.8f); } catch { }
        if (ok)
        {
            ReportGrannyStun(Cfg.PunchDownTime.Value, StunPunch);
            Toast(Tr.L("Ты отбился кулаками прямо из укрытия! Гренни лежит", "You fought her off from your hiding place! Granny is down"), 4, new Color(0.4f, 1f, 0.5f), big: true);
            return true;
        }
        ReportGrannyStun(0f, StunPunchMiss);
        Toast(Tr.L($"Ты отбивался, но Гренни устояла (ударов осталось: {_punchesLeft})", $"You fought back, but Granny stood firm ({_punchesLeft} punches left)"), 3, new Color(1f, 0.6f, 0.3f));
        return false;
    }

    bool GrannyInReach(Body g)
    {
        var feet = R.Feet();
        var to = g.Pos - feet;
        var dy = Mathf.Abs(to.y);
        to.y = 0;
        var dist = to.magnitude;
        var fwd = R.CamFwd; fwd.y = 0;
        var angle = fwd.sqrMagnitude > 0.0001f && dist > 0.001f ? Vector3.Angle(fwd.normalized, to) : 0f;
        string why = null;
        if (dy > 1.6f * U) why = "another height";
        else if (dist > Mathf.Max(Cfg.PunchRange.Value, 4.2f)) why = "too far";
        else if (dist > 0.9f * U && angle > 70f) why = "not facing her";
        else
        {
            // a wall in between: Granny's own body, our own player and our models do not count
            var chest = g.Pos + Vector3.up * (1.1f * U);
            var dir = chest - R.CamPos;
            if (Physics.Raycast(R.CamPos, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(g.Go.transform) && !hit.transform.IsChildOf(R.Player.transform) && !hit.transform.name.StartsWith("GrannyMP")
                && hit.distance < dir.magnitude - 0.9f * U)
                why = "wall: " + hit.transform.name;
        }
        Plugin.L.LogInfo($"[punch] reach: dist {dist:F2} (max {Mathf.Max(Cfg.PunchRange.Value, 4.2f):F1}) dy {dy:F2} angle {angle:F0} -> {why ?? "ok"}");
        return why == null;
    }

    // ---- first-person fists: the player's own model, head hidden, hangs on the camera only while punching ----

    RemoteAvatar _fists;

    void ShowPunchArms()
    {
        try
        {
            if (_avatarTpl?.Template == null || R?.MainCam == null) return;
            if (_fists?.Go == null)
            {
                _fists = RemoteAvatar.Create(_avatarTpl, Net.LocalId, AvatarHeight, R.BaseMaterial);
                _fists.Go.name = "GrannyMP_Fists";
                _fists.SetShadows(false);
                foreach (var t in _fists.Go.GetComponentsInChildren<Transform>(true))
                    if (t.name == "DEF-head" || t.name == "DEF-neck") t.localScale = Vector3.one * 0.001f;
            }
            _fists.SetVisible(true);
            _fists.PlayFromStart("punch");
            PlaceFists();
        }
        catch (Exception e) { Plugin.L.LogWarning("[punch] arms: " + e.Message); }
    }

    void HidePunchArms()
    {
        if (_fists?.Go != null) _fists.SetVisible(false);
    }

    void PlaceFists()
    {
        var cam = R.MainCam.transform;
        var h = AvatarHeight;
        // the camera is at the model's eyes and a little in front of them: the punching arm comes into view
        var pos = cam.position + cam.rotation * new Vector3(0.04f * U, -0.92f * h, -0.26f * U);
        _fists.Go.transform.SetPositionAndRotation(pos, cam.rotation * Quaternion.Euler(0, Cfg.AvatarYawOffset.Value, 0));
    }

    /// <summary>LateUpdate: after the camera has moved.</summary>
    void UpdatePunchArms()
    {
        if (_fists?.Go == null) return;
        if (!PunchShowing || !Me.Alive || _spectating || R?.MainCam == null || IsHiding()) { HidePunchArms(); return; }
        if (!_fists.Go.activeSelf) _fists.SetVisible(true);
        try { PlaceFists(); } catch { }
    }


    ShootArrow _bow;
    string _arrowPrefab;
    bool _bowLoaded, _bowRayPending, _bowWarned;
    float _bowFiredAt = -9f, _nextArrowScan;
    Vector3 _bowFrom, _bowFwd;

    sealed class Flying { public Transform T; public Vector3 Last; public float Until; }
    readonly List<Flying> _arrows = new();
    readonly HashSet<IntPtr> _seenArrows = new();
    const float ArrowStun = 10f, ArrowRange = 40f, ArrowWidth = 0.9f;

    void WatchCrossbow()
    {
        try
        {
            _pickUp ??= R.Player.GetComponentInChildren<PickUp>(true);
            if (_pickUp == null) return;
            if (_bow == null)
            {
                var b = GameRefs.FindAll<ShootArrow>(true);
                if (b.Length > 0)
                {
                    _bow = b[0];
                    try { _arrowPrefab = _bow.arrow != null ? _bow.arrow.name : null; } catch { }
                    Plugin.L.LogInfo($"[bow] arrow prefab '{_arrowPrefab}'");
                }
            }
            var loadedGo = _pickUp.Armborstladdad; var emptyGo = _pickUp.ArmborstOladdad;
            var loaded = loadedGo != null && loadedGo.activeInHierarchy;
            var holding = loaded || (emptyGo != null && emptyGo.activeInHierarchy);
            if (_bowLoaded && !loaded && holding) OnBowFired("loaded model gone");
            _bowLoaded = loaded;

            // new arrows: every frame right after a shot, a few times a second while the crossbow is in hand
            if (Now - _bowFiredAt < 1.5f || (holding && Now >= _nextArrowScan)) { _nextArrowScan = Now + 0.2f; ScanArrows(); }
            TrackArrows();

            // no arrow seen hitting her: fall back to the aim line, like the shotgun
            if (_bowRayPending && Now - _bowFiredAt > 0.35f) { _bowRayPending = false; BowAimCheck(); }
        }
        catch (Exception e) { if (!_bowWarned) { _bowWarned = true; Plugin.L.LogWarning("[bow] " + e); } }
    }

    void OnBowFired(string why)
    {
        if (Now - _bowFiredAt < 0.3f) return;
        _bowFiredAt = Now; _bowRayPending = true;
        _bowFrom = R.CamPos; _bowFwd = R.CamFwd;
        ReportNoise(R.Feet());
        Plugin.L.LogInfo("[bow] fired (" + why + ")");
    }

    void ScanArrows()
    {
        foreach (var rb in GameRefs.FindAll<Rigidbody>())
        {
            if (rb == null || !_seenArrows.Add(rb.Pointer)) continue;
            var n = rb.name;
            if (!n.EndsWith("(Clone)")) continue;
            var isArrow = !string.IsNullOrEmpty(_arrowPrefab) ? n.StartsWith(_arrowPrefab) : n.IndexOf("arrow", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("pil", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isArrow || (rb.position - R.CamPos).sqrMagnitude > 225f * U * U) continue;
            _arrows.Add(new Flying { T = rb.transform, Last = rb.position, Until = Now + 4f });
            Plugin.L.LogInfo($"[bow] arrow {n} at {rb.position} vel {rb.linearVelocity}");
            if (Now - _bowFiredAt > 1f) OnBowFired("arrow seen");
        }
    }

    void TrackArrows()
    {
        if (_arrows.Count == 0) return;
        var g = R.Granny;
        for (var i = _arrows.Count - 1; i >= 0; i--)
        {
            var f = _arrows[i];
            if (f.T == null || Now > f.Until) { _arrows.RemoveAt(i); continue; }
            var cur = f.T.position;
            if (g != null && !g.IsDown && SegmentHitsBody(f.Last, cur, g.Pos))
            {
                Plugin.L.LogInfo("[bow] arrow hit Granny");
                _arrows.Clear(); _bowRayPending = false;
                ReportGrannyStun(ArrowStun, 3);
                return;
            }
            f.Last = cur;
        }
    }

    /// <summary>Does the path of the arrow pass through Granny's body (an upright cylinder at her feet)?</summary>
    bool SegmentHitsBody(Vector3 a, Vector3 b, Vector3 feet)
    {
        var len = (b - a).magnitude;
        var steps = Mathf.Clamp(Mathf.CeilToInt(len / (0.15f * U)), 1, 200);
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector3.Lerp(a, b, i / (float)steps);
            var y = p.y - feet.y;
            if (y < 0.15f * U || y > 1.95f * U) continue;
            var d = p - feet; d.y = 0;
            if (d.magnitude < 0.6f * U) return true;
        }
        return false;
    }

    void BowAimCheck()
    {
        var g = R.Granny;
        if (g == null || g.IsDown) return;
        var chest = g.Pos + Vector3.up * (1.1f * U);
        var to = chest - _bowFrom;
        var along = Vector3.Dot(to, _bowFwd);
        var miss = (to - _bowFwd * along).magnitude;
        Plugin.L.LogInfo($"[bow] aim check: along {along / U:F1} miss {miss / U:F2}");
        if (along <= 0 || along > ArrowRange * U || miss > ArrowWidth * U) return;
        if (Physics.Raycast(_bowFrom, to.normalized, out var hit, to.magnitude, ~0, QueryTriggerInteraction.Ignore)
            && !hit.transform.IsChildOf(g.Go.transform) && hit.distance < to.magnitude - 1.2f * U)
            return;
        ReportGrannyStun(ArrowStun, 3);
    }
}
