using UnityEngine;

namespace GrannyMP;

public sealed partial class Game
{
    float _lockUntil, _nextRunNoise;
    float _caughtT0;
    bool _caughtTeleported;
    bool _spectating;
    int _specIndex;
    Vector3 _camLocalPos; Quaternion _camLocalRot; Transform _camParent;
    bool _myInZone, _zoneSent; float _zoneCandidateSince, _nextZoneCheck; bool _zoneCandidate;

    public float FadeAlpha;      // black screen (HUD)
    public float FlashAlpha;     // red flash (HUD)

    void BecomeSurvivor()
    {
        StopSpectating();
        EndBedScene();
        SetGrannyVision(false);
        try
        {
            R.Fpc.enabled = true; R.Cc.enabled = true;
            R.Fpc.m_WalkSpeed = R.BaseWalk; R.Fpc.m_RunSpeed = R.BaseRun;
        }
        catch { }
        R.Teleport(R.PlayerSpawn, R.PlayerSpawnYaw);
    }

    void LockMovement(float seconds)
    {
        _lockUntil = Mathf.Max(_lockUntil, Now + seconds);
        try { R.Fpc.enabled = false; } catch { }
    }

    void UpdateLock()
    {
        if (_lockUntil > 0 && Now >= _lockUntil)
        {
            _lockUntil = 0;
            if (!_spectating && R != null) try { R.Fpc.enabled = true; } catch { }
        }
    }

    void UpdateSurvivor()
    {
        UpdateLock();
        UpdateCaught();
        if (!Me.Alive || Me.Escaped)
        {
            if (!_spectating && !Me.Escaped) StartSpectating();
            return;
        }
        if (GettingUp) return;

        // ---- spider cellar zone ----
        if (Now >= _nextZoneCheck)
        {
            _nextZoneCheck = Now + 0.25f;
            var inZone = IsInSpiderCellar(R.Feet());
            if (inZone != _zoneCandidate) { _zoneCandidate = inZone; _zoneCandidateSince = Now; }
            if (_zoneCandidate != _myInZone && Now - _zoneCandidateSince > (_zoneCandidate ? 0.5f : 1.5f))
            {
                _myInZone = _zoneCandidate;
                var w = Net.Begin(Msg.Zone); w.Put(_myInZone); ToHost(Msg.Zone);
            }
        }

        // footsteps and running make no noise mark: Granny hears falling/breaking things and creaky boards

        WatchNativeNoise();
        WatchGun();
        WatchCrossbow();

        // ---- traps ----
        if (_lockUntil <= Now) CheckTrapsUnderMe();
    }

    sealed class Wardrobe { public Bounds Inside; public List<OpenCloseGarde1> Doors = new(); }
    Wardrobe[] _spots;

    /// <summary>
    /// Inside a wardrobe or a locker. The game's gardeTrigger/hideTrigger volumes are NOT hiding places: most of them
    /// are whole corners and crawl spaces where the original AI loses sight (some are 20 m wide). Only the small
    /// gardeTrigger right behind a wardrobe/locker door (OpenCloseGarde1) is the inside of the wardrobe.
    /// Beds, coffins and the car hide the player completely: that is IsHiding(), not this.
    /// </summary>
    bool InHidingSpot() => CurrentWardrobe() != null;

    /// <summary>Inside a wardrobe with its doors shut: nobody outside may see the player's model.</summary>
    bool InClosedWardrobe()
    {
        var w = CurrentWardrobe();
        if (w == null) return false;
        try { return w.Doors.All(d => d == null || !d.DoorOpen); } catch { return false; }
    }

    Wardrobe CurrentWardrobe()
    {
        if (R == null || !AmSurvivor) return null;
        try
        {
            if (_spots == null)
            {
                var doors = GameRefs.FindAll<OpenCloseGarde1>(true).Where(d => d != null).ToList();
                var list = new List<Wardrobe>();
                foreach (var g in GameRefs.FindAll<gardeTrigger>(true))
                {
                    var c = g.GetComponent<Collider>();
                    if (c == null) continue;
                    var b = c.bounds;
                    if (Mathf.Max(b.size.x, b.size.z) >= 3.5f * U) continue;
                    var near = doors.Where(d => { var f = d.transform.position - b.center; var dy = Mathf.Abs(f.y); f.y = 0; return f.magnitude < 1.5f * U && dy < 1.6f * U; }).ToList();
                    if (near.Count == 0) continue;
                    b.Expand(new Vector3(0.3f * U, 0.6f * U, 0.3f * U)); // the trigger is a little smaller than the inside
                    list.Add(new Wardrobe { Inside = b, Doors = near });
                }
                _spots = list.ToArray();
                Plugin.L.LogInfo($"[hide] wardrobe spots: {_spots.Length} (doors {doors.Count}) " + string.Join(" ", _spots.Select(s => $"[c {V1(s.Inside.center)} s {V1(s.Inside.size)}]")));
            }
            var feet = R.Feet();
            var p = feet + Vector3.up * (0.6f * U);
            // some gardeTriggers that pass the size filter are whole corridor pieces: the inside of a wardrobe is always
            // right behind its door, so also require the feet next to one of its doors (1.1.6 hid walking players)
            foreach (var w in _spots) if (w.Inside.Contains(p) && NextToDoor(w, feet)) return w;
        }
        catch (Exception e) { Plugin.L.LogWarning("[hide] " + e.Message); _spots = Array.Empty<Wardrobe>(); }
        return null;
    }

    static string V1(Vector3 v) => $"{v.x:F1},{v.y:F1},{v.z:F1}";

    bool NextToDoor(Wardrobe w, Vector3 feet)
    {
        foreach (var d in w.Doors)
        {
            if (d == null) continue;
            var f = d.transform.position - feet;
            var dy = Mathf.Abs(f.y); f.y = 0;
            if (f.magnitude < 1.0f * U && dy < 1.6f * U) return true;
        }
        return false;
    }

    shootGun _gun;
    PickUp _pickUp;
    bool _gunLoaded;
    const float GunRange = 45f, GunWidth = 0.8f, BlastRadius = 7f, GunDownTime = 20f, BlastDownTime = 30f;

    /// <summary>A shot the game can't register: Granny's colliders are triggers and those are off in a session.</summary>
    void WatchGun()
    {
        try
        {
            if (_gun == null) { var g = GameRefs.FindAll<shootGun>(true); if (g.Length > 0) _gun = g[0]; }
            _pickUp ??= R.Player.GetComponentInChildren<PickUp>(true);
            if (_gun == null || _pickUp == null) return;
            var fired = _gunLoaded && _gun.gameObject.activeInHierarchy && !MenuOpen && Input.GetMouseButtonDown(0);
            _gunLoaded = _pickUp.oldShotgunLoaded;
            if (!fired || R.Granny == null) return;
            var from = R.CamPos; var fwd = R.CamFwd;
            var chest = R.Granny.Pos + Vector3.up * (1.2f * U);
            var to = chest - from;
            var along = Vector3.Dot(to, fwd);
            var miss = (to - fwd * along).magnitude;
            Plugin.L.LogInfo($"[gun] fired: along {along / U:F1} miss {miss / U:F2}");
            if (along <= 0 || along > GunRange * U || miss > GunWidth * U) return;
            if (Physics.Raycast(from, to.normalized, out var hit, to.magnitude, ~0, QueryTriggerInteraction.Ignore) && hit.distance < to.magnitude - 1.2f * U) return;
            ReportGrannyStun(GunDownTime, 2);
        }
        catch (Exception e) { Plugin.L.LogWarning("[gun] " + e.Message); }
    }

    public void ReportExplosion(Vector3 pos)
    {
        ReportNoise(pos);
        var d = R?.Granny != null ? Vector3.Distance(R.Granny.Pos, pos) : float.MaxValue;
        Plugin.L.LogInfo($"[blast] distance to Granny {d / U:F1}");
        if (d <= BlastRadius * U) ReportGrannyStun(BlastDownTime, 5);
    }

    readonly HashSet<IntPtr> _seenNoise = new();
    float _nextNoiseScan;

    /// <summary>The game itself drops a "NoiceObject" wherever something falls or breaks: that is the noise Granny should hear.</summary>
    void WatchNativeNoise()
    {
        if (Now < _nextNoiseScan) return;
        _nextNoiseScan = Now + 0.2f;
        var alive = new HashSet<IntPtr>();
        foreach (var rb in GameRefs.FindAll<Rigidbody>())
        {
            if (rb == null || !rb.name.StartsWith("NoiceObject")) continue;
            alive.Add(rb.Pointer);
            if (_seenNoise.Add(rb.Pointer)) ReportNoise(rb.transform.position);
        }
        _seenNoise.IntersectWith(alive);
    }

    public bool IsInSpiderCellar(Vector3 feet)
    {
        try
        {
            if (Physics.Raycast(feet + Vector3.up * (0.5f * U), Vector3.down, out var hit, 4f * U, ~0, QueryTriggerInteraction.Ignore))
            {
                var n = hit.collider != null ? hit.collider.gameObject.name : "";
                return n.Contains("SpiderCellar");
            }
        }
        catch { }
        return false;
    }

    // ---- caught -> next day ----

    void OnCaughtLocal(byte body)
    {
        if (!AmSurvivor || !Me.Alive || Me.Escaped || GettingUp || R == null) return;
        if (body == 3 && AutoPunchFromHiding()) return; // fought her off from the bed: not caught
        CloseNativePause();
        _caughtT0 = Now; _caughtTeleported = false;
        StopBoost();
        HidePunchArms();
        RememberDeathDrop();
        DropHeldItem();
        LockMovement(3.4f);
        FlashAlpha = 0.85f;
        try { if (R.MainCamAnim != null) R.MainCamAnim.Play("playerHurt"); } catch { }
        try { if (R.HitClip != null) AudioSource.PlayClipAtPoint(R.HitClip, R.CamPos, 1f); } catch { }
        Toast(body == 3 ? Tr.L("Гренни нашла тебя!", "Granny found you!") : body == 1 ? Tr.L("Анжела тебя укусила!", "Angela bit you!") : body == 0 ? Tr.L("Гренни ударила тебя битой!", "Granny hit you with the bat!") : Tr.L("Тебя поймали!", "You got caught!"), 3, new Color(1f, 0.3f, 0.3f), big: true);
    }

    public void EnvironmentCaught() => OnCaughtLocal(2);

    /// <summary>The game's own "new day": blood, day title, wake up on the mattress. Falls back to a plain teleport.</summary>
    void WakeUpInBed()
    {
        try
        {
            Plugin.L.LogInfo($"[day] my day {Me.Day}, game's counter {R.Day?.daysCounter}");
            if (R.Day != null) R.Day.daysCounter = Me.Day - 1;
            // the game's under-bed scene below knows nothing about coffins and the car: get out of them first
            ClearNativeHiding(beds: false);
            if (!R.Player.activeInHierarchy)
            {
                // pulled out from under a bed: the game's own end-of-day scene shows the title and puts the player back
                foreach (var c in GameRefs.FindAll<playerCaughtUnderBed>(true)) { c.StartCoroutine(c.EndDayUnderBed()); _nativeBedEnd = true; _bedEndSeen = 0; _gotUpAt = Now; return; }
            }
            if (R.Day != null) R.Day.StartCoroutine(R.Day.newDay());
        }
        catch (Exception e) { Plugin.L.LogWarning("[day] native day: " + e.Message); }
        ClearNativeHiding(beds: true);
        On(R.Player); // hidden, but the game's under-bed scene was not there to switch the player back on
        R.Teleport(R.PlayerSpawn, R.PlayerSpawnYaw); // the game's newDay only shows the title and the blood
        StartBedScene();
    }

    /// <summary>
    /// Caught in a hiding place (bed, coffin, car): the game never ran its own "get out", so the hide button kept
    /// PlayerHiding = true and its picture stayed on (the bed's trigger never saw the player leave). After waking up
    /// the survivor pressed R and the game "got him out" at that bed, or hid him there again: a teleport back into
    /// the hiding place from across the house. Do the game's get-out by hand, without moving the player, and switch
    /// every hide picture off: the triggers turn them on again when the player really walks up to a bed.
    /// beds = false leaves the beds to the game's own under-bed scene (it is running right now).
    /// </summary>
    void ClearNativeHiding(bool beds)
    {
        var cleared = new List<string>();
        void Out(MonoBehaviour btn, bool hiding, GameObject inside, GameObject hideCam, GameObject playerCam, GameObject ring, UnityEngine.UI.Image img, Sprite tex)
        {
            if (btn == null) return;
            if (hiding)
            {
                On(inside, false); On(hideCam, false);
                On(playerCam); On(ring);
                try { if (img != null && tex != null) img.sprite = tex; } catch { }
                cleared.Add(btn.gameObject.name);
            }
            On(btn.gameObject, false);
        }
        try
        {
            if (beds)
            {
                foreach (var b in GameRefs.FindAll<bedButton>(true)) { Out(b, b.PlayerHiding, b.underBed, b.underBedCam, b.playerCam, b.mittenRing, b.button, b.hideTexture); b.PlayerHiding = false; }
                foreach (var b in GameRefs.FindAll<bedButton2>(true)) { Out(b, b.PlayerHiding, b.underBed, b.underBedCam, b.playerCam, b.mittenRing, b.button, b.hideTexture); b.PlayerHiding = false; }
                foreach (var b in GameRefs.FindAll<bedButton3>(true)) { Out(b, b.PlayerHiding, b.underBed, b.underBedCam, b.playerCam, b.mittenRing, b.button, b.hideTexture); b.PlayerHiding = false; }
            }
            foreach (var b in GameRefs.FindAll<coffinButton>(true)) { Out(b, b.PlayerHiding, b.inCoffin, b.underBedCam, b.playerCam, b.mittenRing, b.button, b.hideTexture); b.PlayerHiding = false; }
            foreach (var b in GameRefs.FindAll<coffinButtonBackyard>(true)) { Out(b, b.PlayerHiding, b.inCoffin, b.underBedCam, b.playerCam, b.mittenRing, b.button, b.hideTexture); b.PlayerHiding = false; }
            foreach (var b in GameRefs.FindAll<insideCarButton>(true)) { Out(b, b.PlayerHiding, b.inCoffin, b.underBedCam, b.playerCam, b.mittenRing, b.button, b.hideTexture); b.PlayerHiding = false; }
        }
        catch (Exception e) { Plugin.L.LogWarning("[hide] clear after catch: " + e.Message); }
        _rHide = false; _wasHiding = false;
        if (cleared.Count > 0) Plugin.L.LogInfo($"[hide] caught while hidden, game's hiding reset: {string.Join(", ", cleared)}");
    }

    bool _nativeBedEnd;
    float _wakeUntil;

    float _graceUntil, _bedEndSeen, _gotUpAt;
    const float GraceAfterWake = 2f;

    /// <summary>True while the player is down or getting up, and for a moment after: nobody may touch them, Granny waits.</summary>
    public bool GettingUp => _caughtT0 > 0 || _wakeUntil > 0 || _nativeBedEnd || Now < _graceUntil;

    void UpdateCaughtTail()
    {
        if (!_nativeBedEnd) return;
        // the game's scene turns the player on before it is over: wait until our own camera really shows the room
        var up = R.Player.activeInHierarchy && !IsHiding() && (R.Day?.playerInBed == null || !R.Day.playerInBed.activeInHierarchy);
        if (!up) { _bedEndSeen = 0; return; }
        if (_bedEndSeen <= 0) _bedEndSeen = Now;
        if (Now - _bedEndSeen < 0.5f) return;
        _nativeBedEnd = false;
        _graceUntil = Now + GraceAfterWake;
        ClearNativeHiding(beds: true); // the scene put the player back, but the bed's hide button still thinks he is under it
        Plugin.L.LogInfo($"[wake] up after the bed scene, {Now - _gotUpAt:F1} s");
    }

    void DropHeldItem()
    {
        try { foreach (var p in GameRefs.FindAll<PickUp>(true)) p.dropObject = true; }
        catch (Exception e) { Plugin.L.LogWarning("[day] drop: " + e.Message); }
    }

    /// <summary>Waking up: the camera lies on the bed under the black screen, then smoothly rises to the player's start.</summary>
    void StartBedScene()
    {
        try
        {
            var bed = R.Day?.playerInBed;
            var cam = bed != null ? bed.GetComponentInChildren<Camera>(true) : null;
            if (cam == null) return;
            SetPlayerView(false);
            On(bed);
            var anim = cam.GetComponent<Animation>();
            if (anim != null) { anim.Stop(); anim.enabled = false; }
            cam.enabled = true;
            _bedCam = cam.transform;
            _bedFrom = bed.transform.position; _bedFromRot = bed.transform.rotation;
            _bedCam.position = _bedFrom; _bedCam.rotation = _bedFromRot;
            _bedTo = R.PlayerSpawn + Vector3.up * R.EyeHeight;
            _bedToRot = Quaternion.Euler(0, R.PlayerSpawnYaw, 0);
            _bedLight = new GameObject("GrannyMP_BedLight");
            _bedLight.transform.SetParent(_bedCam, false);
            var l = _bedLight.AddComponent<Light>();
            l.type = LightType.Point; l.range = 12f * U; l.intensity = 1f; l.shadows = LightShadows.None; l.renderMode = LightRenderMode.ForcePixel;
            On(R.Day.kliverUrBedLjud);
            _bedStart = Now + BedHold;
            _wakeUntil = _bedStart + BedRise;
            LockMovement(BedHold + BedRise + 0.1f);
        }
        catch (Exception e) { Plugin.L.LogWarning("[day] bed: " + e.Message); EndBedScene(); }
    }

    const float BedHold = 1.6f, BedRise = 3.2f;
    float _bedStart;
    Transform _bedCam;
    Vector3 _bedFrom, _bedTo;
    Quaternion _bedFromRot, _bedToRot;
    GameObject _bedLight;

    void UpdateBedScene()
    {
        if (_wakeUntil <= 0) return;
        if (Now >= _wakeUntil) { EndBedScene(); return; }
        if (_bedCam == null || Now < _bedStart) return;
        var k = Mathf.SmoothStep(0, 1, (Now - _bedStart) / BedRise);
        _bedCam.position = Vector3.Lerp(_bedFrom, _bedTo, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.15f * U);
        _bedCam.rotation = Quaternion.Slerp(_bedFromRot, _bedToRot, k);
    }

    void EndBedScene()
    {
        if (_wakeUntil <= 0) return;
        _wakeUntil = 0;
        try
        {
            if (_bedLight != null) UnityEngine.Object.Destroy(_bedLight);
            On(R?.Day?.playerInBed, false);
            On(R?.Day?.kliverUrBedLjud, false);
            SetPlayerView(true);
            R?.Teleport(R.PlayerSpawn, R.PlayerSpawnYaw);
            _lockUntil = 0;
            if (R != null) { R.Fpc.enabled = true; R.Fpc.m_MouseLook.m_CameraTargetRot = Quaternion.identity; }
            _graceUntil = Now + GraceAfterWake;
        }
        catch (Exception e) { Plugin.L.LogWarning("[day] bed end: " + e.Message); }
    }

    void SetPlayerView(bool on)
    {
        R.RenderCam.enabled = on;
        var l = R.MainCam.GetComponent<AudioListener>();
        if (l != null) l.enabled = on;
    }

    void UpdateCaught()
    {
        UpdateCaughtTail();
        UpdateBedScene();
        if (FlashAlpha > 0) FlashAlpha = Mathf.Max(0, FlashAlpha - Time.deltaTime * 0.8f);
        if (_caughtT0 <= 0) { FadeAlpha = Mathf.Max(0, FadeAlpha - Time.deltaTime); return; }
        var t = Now - _caughtT0;
        FadeAlpha = t < 1.2f ? t / 1.2f : t < 2.4f ? 1f : Mathf.Clamp01(1f - (t - 2.4f));
        if (t >= 1.4f && !_caughtTeleported)
        {
            _caughtTeleported = true;
            Me.Day++;
            var eliminated = Me.Day > Cfg.Days.Value;
            if (eliminated)
            {
                Me.Alive = false;
                Toast(Tr.L("Ты выбыл. Теперь ты зритель (ЛКМ / пробел — следующий игрок)", "You are out. Now you spectate (LMB / space: next player)"), 6, Color.white, big: true);
            }
            else
            {
                var nativeDayTitle = R.Day != null;
                WakeUpInBed();
                if (_myInZone) { _myInZone = false; _zoneCandidate = false; var wz = Net.Begin(Msg.Zone); wz.Put(false); ToHost(Msg.Zone); }
                Toast(Tr.L($"Осталось дней: {Cfg.Days.Value - Me.Day + 1}", $"Days left: {Cfg.Days.Value - Me.Day + 1}"), 4, Color.white);
                if (!nativeDayTitle)
                Toast(Me.Day == Cfg.Days.Value ? Tr.L($"ДЕНЬ {Me.Day} — ПОСЛЕДНИЙ", $"DAY {Me.Day}: THE LAST ONE") : Tr.L($"ДЕНЬ {Me.Day}", $"DAY {Me.Day}"), 3, Color.white, big: true);
            }
            var w = Net.Begin(Msg.DayUpdate); w.Put((byte)Me.Day); w.Put(eliminated); ToHost(Msg.DayUpdate);
        }
        if (t >= 3.4f)
        {
            _caughtT0 = 0;
            if (Me.Alive && (R.Feet() - R.PlayerSpawn).sqrMagnitude > 9f * U * U) R.Teleport(R.PlayerSpawn, R.PlayerSpawnYaw);
        }
    }

    // ---- spectator ----

    void StartSpectating()
    {
        if (R == null || _spectating) return;
        _spectating = true;
        try
        {
            var cam = R.MainCam.transform;
            _camParent = cam.parent; _camLocalPos = cam.localPosition; _camLocalRot = cam.localRotation;
            R.Fpc.enabled = false; R.Cc.enabled = false;
        }
        catch { }
    }

    void StopSpectating()
    {
        if (!_spectating) return;
        _spectating = false;
        if (R == null) return;
        try
        {
            var cam = R.MainCam.transform;
            if (_camParent != null) cam.SetParent(_camParent, false);
            cam.localPosition = _camLocalPos; cam.localRotation = _camLocalRot;
            R.Cc.enabled = true; R.Fpc.enabled = true;
        }
        catch { }
    }

    void UpdateSpectatorCamera()
    {
        if (!_spectating || R == null || R.MainCam == null) return;
        var targets = new List<(Vector3 pos, float yaw)>();
        foreach (var p in Players.Values)
            if (p.Id != Net.LocalId && p.ActiveSurvivor && p.HasState && !p.Hidden) targets.Add((p.SmoothPos, p.SmoothYaw));
        if (R.Granny != null) targets.Add((R.Granny.Pos, R.Granny.Yaw));
        if (targets.Count == 0) return;
        if (!MenuOpen && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space))) _specIndex++;
        var (pos, yaw) = targets[Math.Abs(_specIndex) % targets.Count];
        var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        var cam = R.MainCam.transform;
        var desired = pos + Vector3.up * (2.6f * U) - fwd * (3.2f * U);
        // keep the camera out of walls
        var head = pos + Vector3.up * (1.6f * U);
        if (Physics.Raycast(head, (desired - head).normalized, out var hit, (desired - head).magnitude, ~0, QueryTriggerInteraction.Ignore))
            desired = hit.point - (desired - head).normalized * (0.3f * U);
        cam.position = Vector3.Lerp(cam.position, desired, Mathf.Clamp01(Time.deltaTime * 6f));
        cam.rotation = Quaternion.Slerp(cam.rotation, Quaternion.LookRotation(head - cam.position), Mathf.Clamp01(Time.deltaTime * 8f));
    }
}
