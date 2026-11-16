using UnityEngine;

namespace GrannyMP;

/// <summary>
/// Granny knocked out. Instead of a black screen she may watch through a survivor's eyes or fly around the house,
/// and once she stands up nobody can knock her down again for a few seconds.
/// </summary>
public sealed partial class Game
{

    const float DownBlackout = 1.2f; // the fall itself stays black
    int _dvMode;                     // 0 off, 1 through a survivor's eyes, 2 free flight
    float _dvFrom, _dvYaw, _dvPitch;
    byte _dvWatchId = 255;
    string _dvWatchName = "";
    Transform _dvCamParent;
    Vector3 _dvCamLocalPos;
    Quaternion _dvCamLocalRot;
    Body _dvHidBody; // the own body hidden for the view; Angela's possession may swap bodies meanwhile

    public bool DownViewOn => _dvMode != 0;
    public bool DownFlying => _dvMode == 2;
    public string DownWatchName => _dvWatchName;

    /// <summary>The survivor whose eyes Granny looks through: his own model must not block the view.</summary>
    bool DownWatching(PlayerInfo p) => _dvMode == 1 && p.Id == _dvWatchId;

    /// <summary>Whom she may watch. Hidden ones are left out: lying down must not show her where they hide.</summary>
    List<PlayerInfo> DownTargets() => Players.Values
        .Where(p => p.Id != Net.LocalId && p.ActiveSurvivor && p.HasState && !p.Spectating && !p.OutOfSight && Now - p.LastSeen < 3f)
        .OrderBy(p => p.Id).ToList();

    /// <summary>LateUpdate, after every script that moves the camera.</summary>
    void UpdateDownView()
    {
        var down = Session && InRound && AmGranny && !Executing && _downUntil > Now && R?.MainCam != null;
        if (!down) { StopDownView(); return; }
        if (Now < _dvFrom) return;
        var cam = R.MainCam.transform;
        if (_dvMode == 0)
        {
            _dvCamParent = cam.parent; _dvCamLocalPos = cam.localPosition; _dvCamLocalRot = cam.localRotation;
            _dvWatchId = 255;
            if (DownTargets().Count > 0) _dvMode = 1; else StartFlying(cam);
            _dvHidBody = MyBody;
            try { _dvHidBody?.SetVisible(false); } catch { } // her own body stands right where the camera was
            Plugin.L.LogInfo($"[down] view: {(_dvMode == 1 ? "watching a survivor" : "flying")}");
        }

        var input = !MenuOpen && !IsPauseMenuOpen();
        if (input && Input.GetMouseButtonDown(1))
        {
            if (_dvMode == 1) StartFlying(cam);
            else if (DownTargets().Count > 0) { _dvMode = 1; _dvWatchId = 255; }
            else Toast(Tr.L("Смотреть не за кем: все прячутся", "Nobody to watch: everyone is hiding"), 2f, Color.white);
        }

        if (_dvMode == 1) DownWatch(cam, input);
        if (_dvMode == 2) DownFly(cam, input);
    }

    void StartFlying(Transform cam)
    {
        _dvMode = 2; _dvWatchId = 255; _dvWatchName = "";
        var e = cam.eulerAngles;
        _dvYaw = e.y;
        _dvPitch = Mathf.Clamp(e.x > 180f ? e.x - 360f : e.x, -89f, 89f);
    }

    void DownWatch(Transform cam, bool input)
    {
        var list = DownTargets();
        if (list.Count == 0) { StartFlying(cam); return; } // the one we watched hid or was caught
        var i = list.FindIndex(p => p.Id == _dvWatchId);
        if (i < 0) i = 0;
        if (input && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space))) i = (i + 1) % list.Count;
        var t = list[i];
        _dvWatchId = t.Id; _dvWatchName = t.Name;
        // only the position and the yaw travel over the network: the view is level
        cam.position = t.SmoothPos + Vector3.up * (AvatarHeight * (t.Crouch ? 0.55f : 0.93f));
        cam.rotation = Quaternion.Euler(0f, t.SmoothYaw, 0f);
    }

    void DownFly(Transform cam, bool input)
    {
        if (input)
        {
            var sens = 2f;
            try { var ml = R.Fpc.m_MouseLook; if (ml != null && ml.XSensitivity > 0.01f) sens = ml.XSensitivity; } catch { }
            try
            {
                _dvYaw += Input.GetAxis("Mouse X") * sens;
                _dvPitch = Mathf.Clamp(_dvPitch - Input.GetAxis("Mouse Y") * sens, -89f, 89f);
            }
            catch { } // no such axis: the camera only moves
        }
        cam.rotation = Quaternion.Euler(_dvPitch, _dvYaw, 0f);
        if (!input) return;
        var move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += cam.forward;
        if (Input.GetKey(KeyCode.S)) move -= cam.forward;
        if (Input.GetKey(KeyCode.D)) move += cam.right;
        if (Input.GetKey(KeyCode.A)) move -= cam.right;
        if (Input.GetKey(KeyCode.Space)) move += Vector3.up;
        if (Input.GetKey(KeyCode.LeftControl)) move -= Vector3.up;
        if (move.sqrMagnitude < 0.0001f) return;
        var speed = (Input.GetKey(KeyCode.LeftShift) ? 15f : 5f) * U; // metres per second
        cam.position += move.normalized * (speed * Time.deltaTime); // through walls: it is a ghost camera
    }

    void StopDownView()
    {
        if (_dvMode == 0) return;
        _dvMode = 0; _dvWatchId = 255; _dvWatchName = "";
        try
        {
            var cam = R?.MainCam != null ? R.MainCam.transform : null;
            if (cam != null)
            {
                if (_dvCamParent != null && cam.parent != _dvCamParent) cam.SetParent(_dvCamParent, false);
                cam.localPosition = _dvCamLocalPos; cam.localRotation = _dvCamLocalRot;
            }
            // first person: her body and arms stay visible. If Angela's possession swapped the bodies meanwhile,
            // SetPossess has already set both: showing Angela's model would put it right into her own camera
            if (_dvHidBody != null && _dvHidBody == MyBody) _dvHidBody.SetVisible(true);
            _dvHidBody = null;
        }
        catch (Exception e) { Plugin.L.LogWarning("[down] view stop: " + e.Message); }
        Plugin.L.LogInfo("[down] view off");
    }

    /// <summary>Scene change: the camera is gone with the old scene, only forget the state.</summary>
    void ForgetDownView() { _dvMode = 0; _dvWatchId = 255; _dvWatchName = ""; _dvCamParent = null; _dvHidBody = null; _grannyUpAt = -99f; }


    const float ProtectAfterUp = 5f;
    const float RelocateTime = 2.3f; // a player Granny wakes up somewhere else first (UpdateRelocate)
    float _grannyUpAt = -99f;

    /// <summary>Seconds Granny still can't be knocked down: she has just stood up. Counted on every machine from the moment her body stopped lying.</summary>
    public float GrannyProtectLeft => Mathf.Max(0f, _grannyUpAt + (GrannyId >= 0 ? RelocateTime : 0f) + ProtectAfterUp - Now);

    void GrannyStoodUp()
    {
        _grannyUpAt = Now;
        Plugin.L.LogInfo($"[down] Granny stood up, protected {GrannyProtectLeft:F1} s");
    }
}
