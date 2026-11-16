using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyMP;

/// <summary>
/// The game's own death scenes (guillotine, car), played on every machine when Granny wins.
/// The player who is Granny performs the decisive steps; the victim watches through the original camera.
/// </summary>
public sealed partial class Game
{
    sealed class Exec
    {
        public byte Victim, Scene;
        public int Step;
        public float StepT;
        public playerDead Pd;
        public Animation Car, Blade, Figure;
        public RemoteAvatar Dummy;
        public Texture Over;
        public float Black, OverAlpha;
    }

    Exec X;
    byte _lastVictim = 255;
    string _execText;
    bool _reloading;
    public bool Executing => X != null;
    bool ExecActor => AmGranny || (GrannyId < 0 && Net.IsHost);

    const byte SceneGuillotine = 2, SceneCar = 3;
    // lengths of the game's clips: the victim's camera comes round before the blow may fall
    const float GuillotineReady = 10.5f, CarReady = 12f, GateTimeoutHuman = 30f, AiThink = 1.5f;

    static GameObject Find(string prefix)
    {
        foreach (var t in GameRefs.FindAll<Transform>(true))
            if (t.name.StartsWith(prefix)) return t.gameObject;
        return null;
    }

    static void On(GameObject go, bool v = true) { try { if (go != null && go.activeSelf != v) go.SetActive(v); } catch { } }

    void BeginExecutionHost(string text)
    {
        var pool = new[] { SceneGuillotine, SceneCar };
        var scene = _forceExecScene != 0 ? (byte)_forceExecScene : pool[_rng.Next(pool.Length)];
        var victim = _lastVictim != 255 ? _lastVictim : Players.Values.Last(p => p.Role == Role.Survivor).Id;
        _execText = text;
        var w = Net.Begin(Msg.ExecStart); w.Put(victim); w.Put(scene); Net.Send();
        StartExecutionLocal(victim, scene);
    }

    void StartExecutionLocal(byte victim, byte scene)
    {
        CloseNativePause();
        if (R == null || X != null) return;
        var pds = GameRefs.FindAll<playerDead>(true);
        if (pds.Length == 0) { Plugin.L.LogWarning("[exec] no playerDead"); if (Net.IsHost) EndRoundHost(2, _execText); return; }
        var x = new Exec { Victim = victim, Scene = scene, Pd = pds[0], StepT = Now };
        if (Menu.Open) Menu.Show(false);
        CloseNativePause();
        _toasts.Clear();
        StopSpectating();
        EndBedScene();
        SetGrannyVision(false);
        _lockUntil = 0; _caughtT0 = 0; _relocateT0 = 0; _relocated = false;
        FlashAlpha = 0;
        foreach (var p in Players.Values) { p.Avatar?.SetVisible(false); p.Avatar?.SetXray(false); }
        ClearTraps();
        R.Granny?.SetAgent(false); R.Spider?.SetAgent(false);
        R.Granny?.SetVisible(false);
        try { R.Fpc.enabled = false; R.Cc.enabled = false; } catch { }
        try { R.MainCam.gameObject.SetActive(false); } catch { }
        foreach (var m in new[] { x.Pd.musicHolder, x.Pd.musicHolderNM, x.Pd.musicHolderHalloween, x.Pd.musicHolderChristmas, x.Pd.BGnoiceHolder }) On(m, false);
        try { var img = x.Pd.gameOverTexture; if (img != null && img.sprite != null) x.Over = img.sprite.texture; } catch { }
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        X = x;
        try { if (scene == SceneCar) SetupCar(x); else SetupGuillotine(x); }
        catch (Exception e) { Plugin.L.LogError("[exec] setup: " + e); }
        Plugin.L.LogInfo($"[exec] scene {scene}, victim {victim}, I am {(AmGranny ? "granny" : "viewer")}");
        DevCameras();
    }

    /// <summary>The death scenes are lit by the player's own light, like the game's end cameras.</summary>
    void LightUp(Transform cam)
    {
        var go = new GameObject("GrannyMP_ExecLight");
        go.transform.SetParent(cam, false);
        var l = go.AddComponent<Light>();
        l.type = LightType.Point; l.range = 22f * U; l.intensity = 2.2f; l.shadows = LightShadows.None;
        l.renderMode = LightRenderMode.ForcePixel; l.color = new Color(1f, 0.93f, 0.8f);
    }

    /// <summary>Camera for the Granny player where the game has none.</summary>
    static Camera OwnCamera(Vector3 pos, Vector3 target)
    {
        var go = new GameObject("GrannyMP_ExecCam");
        var cam = go.AddComponent<Camera>();
        go.AddComponent<AudioListener>();
        cam.fieldOfView = 62f; cam.nearClipPlane = 0.05f;
        go.transform.position = pos;
        go.transform.rotation = Quaternion.LookRotation(target - pos);
        return cam;
    }

    RemoteAvatar SpawnDummy(Vector3 feet, float yaw, string clip)
    {
        if (_avatarTpl?.Template == null) return null;
        var d = RemoteAvatar.Create(_avatarTpl, X.Victim, AvatarHeight, R.BaseMaterial);
        d.Place(feet, yaw);
        d.Play(clip);
        return d;
    }

    Vector3 Ground(Vector3 from) =>
        Physics.Raycast(from, Vector3.down, out var hit, 10f * U, ~0, QueryTriggerInteraction.Ignore) ? hit.point : from;

    void SetupGuillotine(Exec x)
    {
        var root = Find("GameOverScene2");
        On(root);
        var camGo = Find("EndCamera2_1");
        var blade = Find("GiljotinParent2");
        x.Blade = blade != null ? blade.GetComponent<Animation>() : null;
        foreach (var a in root.GetComponentsInChildren<Animation>(true))
            if (a.gameObject.name.StartsWith("SlendrinaMom")) x.Figure = a;
        x.Figure?.CrossFade("GrannyLooking", 0.01f);
        if (AmGranny)
        {
            // the neck hole is at the blade's trigger, the victim's body is on the far side from Granny
            var head = Find("KlingTrigger").transform.position;
            On(camGo, false);
            var fwd = Find("GrannyEndParent2_2").transform.position - head; fwd.y = 0; fwd.Normalize();
            var side = Vector3.Cross(Vector3.up, fwd);
            var cam = OwnCamera(head + fwd * (Cfg.ExecCamBack.Value * U) - side * (Cfg.ExecCamSide.Value * U) + Vector3.up * (0.55f * U), head);
            LightUp(cam.transform);
            x.Dummy = SpawnDummy(head - fwd * (Cfg.ExecBodyBack.Value * U) - Vector3.up * (AvatarHeight * Cfg.ExecHeadFrac.Value), Quaternion.LookRotation(fwd).eulerAngles.y, "crouchIdle");
        }
        else
        {
            On(camGo);
            LightUp(camGo.transform);
            try { camGo.GetComponent<Animation>()?.CrossFade("PlayerInGiljo", 0.01f); } catch { }
        }
    }

    void SetupCar(Exec x)
    {
        var root = Find("GameOverScene3");
        On(root);
        var camGo = Find("EndCamera3_1");
        var pd = x.Pd;
        x.Car = pd.Car.GetComponent<Animation>();
        On(pd.carSensors, false); On(pd.carBumper, false);
        if (pd.CarStartPos != null) pd.Car.transform.position = pd.CarStartPos.position;
        On(pd.carEngineStart); On(pd.carEngineSound);
        if (AmGranny)
        {
            var feet = Ground(camGo.transform.position);
            var toCar = pd.Car.transform.position - feet; toCar.y = 0;
            var yaw = Quaternion.LookRotation(toCar).eulerAngles.y;
            On(camGo, false);
            On(Find("carHead"));
            var carCam = Find("carCam");
            var cc = carCam.GetComponent<Camera>(); if (cc != null) cc.enabled = true;
            LightUp(carCam.transform);
            x.Dummy = SpawnDummy(feet, yaw, "idle");
        }
        else
        {
            On(pd.grannyInCar);
            On(camGo);
            LightUp(camGo.transform);
            try { camGo.GetComponent<Animation>()?.CrossFade("PlayerSeeCar", 0.01f); } catch { }
        }
    }

    void ExecSend(byte step)
    {
        var w = Net.Begin(Msg.ExecStep); w.Put(step); Net.Send();
        ApplyExecStep(step);
    }

    void ApplyExecStep(byte step)
    {
        var x = X;
        if (x == null || step <= x.Step) return;
        x.Step = step; x.StepT = Now;
        var pd = x.Pd;
        try
        {
            if (x.Scene == SceneCar)
            {
                if (step == 1) { On(pd.carReverseSound); x.Car.CrossFade("CarReverse", 0.01f); }
                if (step == 3) { On(pd.carReverseSound, false); On(pd.carForwardSound); x.Car.CrossFade("CarForward", 0.01f); }
            }
            else if (step == 1) x.Figure?.CrossFade("GrannyPullGiljotin", 0.05f);
        }
        catch (Exception e) { Plugin.L.LogError("[exec] step: " + e); }
    }

    bool ExecGate(bool pressed, float ready)
    {
        if (!ExecActor) return false;
        var t = Now - X.StepT;
        if (t < ready) return false;
        return pressed || t > ready + (AmGranny ? GateTimeoutHuman : AiThink);
    }

    void UpdateExecution()
    {
        var x = X;
        var t = Now - x.StepT;
        if (x.Scene == SceneCar) UpdateCar(x, t); else UpdateGuillotine(x, t);
        if (x.Step >= FinalStep(x))
        {
            x.Black = 1f;
            x.OverAlpha = Mathf.Clamp01((t - 1.2f) / 1.2f);
            if (t > 6.5f) { FinishExecution(); return; }
        }
        FadeAlpha = x.Black;
        if (FlashAlpha > 0) FlashAlpha = Mathf.Max(0, FlashAlpha - Time.deltaTime * 0.7f);
    }

    static int FinalStep(Exec x) => x.Scene == SceneCar ? 4 : 3;

    void Impact(Exec x)
    {
        x.Step = FinalStep(x); x.StepT = Now;
        var pd = x.Pd;
        if (x.Scene == SceneCar) On(pd.carCrashSound); else On(Find("GiljotinLjud"));
        On(pd.grannyLaughSound);
        FlashAlpha = 1f;
    }

    void UpdateGuillotine(Exec x, float t)
    {
        if (x.Step == 0 && ExecGate(!MenuOpen && Input.GetMouseButtonDown(0), GuillotineReady)) ExecSend(1);
        if (x.Step == 1 && t > 0.35f)
        {
            x.Step = 2; x.StepT = Now;
            try { x.Blade?.CrossFade("giljotinOn", 0.01f); } catch { }
        }
        // the victim's sight ends with the blow, Granny watches the blade land
        if (x.Step == 2 && t > (AmGranny ? 1.3f : 0.55f)) Impact(x);
    }

    void UpdateCar(Exec x, float t)
    {
        if (x.Step == 0 && ExecGate(!MenuOpen && Input.GetKeyDown(KeyCode.S), CarReady)) ExecSend(1);
        if (x.Step == 1 && t > 0.3f && !x.Car.IsPlaying("CarReverse")) { x.Step = 2; x.StepT = Now; }
        if (x.Step == 2 && ExecGate(!MenuOpen && Input.GetKeyDown(KeyCode.W), 0f)) ExecSend(3);
        if (x.Step == 3 && t > 0.3f && !x.Car.IsPlaying("CarForward")) Impact(x);
    }

    /// <summary>Like the original game the round ends in the main menu; the session stays, the panel offers the next round.</summary>
    void ReturnToMainMenu()
    {
        _openMenuInMainMenu = true;
        _returnToHouseAt = 0; _reloadHouseAt = 0;
        SceneManager.LoadScene("Menu");
    }
    bool _openMenuInMainMenu;

    void FinishExecution()
    {
        var x = X; X = null;
        x?.Dummy?.Destroy();
        FadeAlpha = 1f; FlashAlpha = 0f;
        if (Net.IsHost) EndRoundHost(2, _execText ?? ""); else EndRoundLocal();
        ReturnToMainMenu();
    }

    void DrawExecution(float w, float h)
    {
        var x = X;
        if (x.OverAlpha > 0.01f && x.Over != null)
        {
            var ow = h * 0.6f * x.Over.width / x.Over.height;
            var old = GUI.color; GUI.color = new Color(1, 1, 1, x.OverAlpha);
            GUI.DrawTexture(new Rect((w - ow) / 2, h * 0.2f, ow, h * 0.6f), x.Over);
            GUI.color = old;
            return;
        }
        if (x.Step >= FinalStep(x)) return;
        string text = null;
        if (AmGranny)
        {
            var wait = (x.Scene == SceneCar ? CarReady : GuillotineReady) - (Now - x.StepT);
            if (x.Step == 0 && wait > 0) text = Tr.L($"Жертва приходит в себя... {wait:F0}", $"The victim is coming round... {wait:F0}");
            else if (x.Scene == SceneCar) text = x.Step == 0 ? Tr.L("S — сдать назад", "S - reverse") : x.Step == 2 ? Tr.L("W — газ!", "W - full throttle!") : null;
            else text = x.Step == 0 ? Tr.L("ЛКМ — дёрнуть рычаг", "LMB - pull the lever") : null;
        }
        else if (x.Step == 0) text = Tr.L("Гренни решает твою судьбу...", "Granny decides your fate...");
        if (text != null) Shadow(new Rect(0, h * 0.8f, w, 60), text, _sBig, Color.white);
    }
}
