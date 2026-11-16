using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AI;
using UnityStandardAssets.Characters.FirstPerson;

namespace GrannyMP;

/// <summary>References to objects of the house scene ("Scene"). Rebuilt on every scene load.</summary>
public sealed class GameRefs
{
    public GameObject Player;
    public FirstPersonController_Egen Fpc;
    public CharacterController Cc;
    public Camera MainCam;
    public Camera RenderCam;   // "Main Camera" stays disabled, its child does the rendering
    public Animation MainCamAnim;
    public float BaseWalk = 4f, BaseRun = 7f;

    public Body Granny;
    public Body Spider;
    public EnemyAIGranny GrannyAi;
    public MomSpiderHead SpiderAi;
    public Transform GrannyHead;
    public Transform[] GrannyNav = Array.Empty<Transform>();

    public GameObject BearTrapTemplate;
    public AudioClip TrapSound;
    public CheckExitDoor ExitDoor;
    public AudioClip[] StepClips = Array.Empty<AudioClip>();
    public AudioClip HitClip;
    public Material BaseMaterial;

    public Vector3 PlayerSpawn;
    public float PlayerSpawnYaw;
    public Vector3 GrannySpawn;
    public float GrannySpawnYaw;
    public Vector3 SpiderStart;
    public startNewDay Day;
    public Vector3[] GrannyStarts = Array.Empty<Vector3>();

    public bool Valid => Player != null && Fpc != null;

    public static T[] FindAll<T>(bool includeInactive = false) where T : UnityEngine.Object
    {
        var arr = includeInactive
            ? Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>())
            : UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>());
        var res = new List<T>();
        if (arr == null) return res.ToArray();
        foreach (var o in arr)
        {
            var c = o?.TryCast<T>();
            if (c == null) continue;
            if (includeInactive && c is Component comp && !comp.gameObject.scene.IsValid()) continue; // skip prefabs/assets
            res.Add(c);
        }
        return res.ToArray();
    }

    public static GameRefs TryFind()
    {
        var fpcs = FindAll<FirstPersonController_Egen>();
        if (fpcs.Length == 0) return null;
        var r = new GameRefs { Fpc = fpcs[0] };
        r.Player = r.Fpc.gameObject;
        r.Cc = r.Player.GetComponent<CharacterController>();
        if (r.Cc == null) return null;
        r.MainCam = r.Player.GetComponentInChildren<Camera>();
        if (r.Fpc.mainCam != null) r.MainCam = r.Fpc.mainCam.GetComponent<Camera>() ?? r.MainCam;
        r.MainCamAnim = r.MainCam != null ? r.MainCam.GetComponent<Animation>() : null;
        r.RenderCam = r.MainCam;
        foreach (var c in r.Player.GetComponentsInChildren<Camera>(true)) if (c != r.MainCam) { r.RenderCam = c; break; }
        r.BaseWalk = r.Fpc.m_WalkSpeed > 0.01f ? r.Fpc.m_WalkSpeed : 4f;
        r.BaseRun = r.Fpc.m_RunSpeed > 0.01f ? r.Fpc.m_RunSpeed : r.BaseWalk * 1.6f;
        r.HitClip = r.Fpc.playerHit;
        r.PlayerSpawn = r.Feet();
        r.PlayerSpawnYaw = r.Player.transform.eulerAngles.y;

        try
        {
            var fs = r.MainCam != null ? r.MainCam.GetComponent<Footsteps>() : null;
            if (fs != null && fs.footstepConcrete != null) r.StepClips = fs.footstepConcrete.ToArray();
        }
        catch (Exception e) { Plugin.L.LogWarning("[refs] footsteps: " + e.Message); }

        var ais = FindAll<EnemyAIGranny>(true);
        if (ais.Length > 0)
        {
            r.GrannyAi = ais[0];
            var gp = r.GrannyAi.gameObject;
            r.Granny = new Body(gp, "Granny", r.GrannyAi.navComponent ?? gp.GetComponent<NavMeshAgent>(), "idle", "Walk");
            r.GrannySpawn = gp.transform.position;
            r.GrannySpawnYaw = gp.transform.eulerAngles.y;
            r.GrannyHead = gp.transform.Find("GrannyHeadPos") ?? gp.transform;
            r.BearTrapTemplate = r.GrannyAi.bearTrap;
            var nav = new List<Transform>();
            var ai = r.GrannyAi;
            foreach (var t in new[] { ai.nav1, ai.nav2, ai.nav3, ai.nav4, ai.nav5, ai.nav6, ai.nav7, ai.nav8, ai.nav9, ai.nav10, ai.nav11, ai.nav12, ai.nav13, ai.nav14, ai.nav15, ai.nav16 })
                if (t != null) nav.Add(t);
            r.GrannyNav = nav.ToArray();
            try
            {
                var smr = gp.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null) r.BaseMaterial = smr.sharedMaterial;
            }
            catch { }
        }
        if (r.BearTrapTemplate == null)
        {
            var traps = FindAll<BearTrap>(true);
            if (traps.Length > 0) r.BearTrapTemplate = traps[0].gameObject;
        }
        if (r.BearTrapTemplate != null)
        {
            var bt = r.BearTrapTemplate.GetComponent<BearTrap>();
            if (bt != null) r.TrapSound = bt.ObjectLjud ?? bt.BeartrapOnFloor;
        }

        try
        {
            // the original game puts Granny at one of these at the start of every day
            var days = FindAll<startNewDay>(true);
            if (days.Length > 0)
            {
                r.Day = days[0];
                var d = days[0];
                r.GrannyStarts = new[] { d.GrannyStartPos1, d.GrannyStartPos2, d.GrannyStartPos3, d.GrannyStartPos4 }.Where(t => t != null).Select(t => t.position).ToArray();
            }
            if (days.Length > 0 && days[0].GrannyStartPos1 != null)
            {
                r.GrannySpawn = days[0].GrannyStartPos1.position;
                r.GrannySpawnYaw = days[0].GrannyStartPos1.eulerAngles.y;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[refs] granny start: " + e.Message); }
        if (NavMesh.SamplePosition(r.GrannySpawn, out var nh, 10f, NavMesh.AllAreas)) r.GrannySpawn = nh.position;

        var spiders = FindAll<MomSpiderHead>(true);
        if (spiders.Length > 0)
        {
            r.SpiderAi = spiders[0];
            var sp = r.SpiderAi.gameObject;
            r.Spider = new Body(sp, "Spider", r.SpiderAi.agent ?? sp.GetComponent<NavMeshAgent>(), "idle", "walk");
            r.SpiderStart = sp.transform.position;
            var start = GameObject.Find("SpiderMomStartPosition");
            if (start != null) r.SpiderStart = start.transform.position;
        }

        var exits = FindAll<CheckExitDoor>();
        if (exits.Length > 0) r.ExitDoor = exits[0];

        Plugin.L.LogInfo($"[refs] player ok (walk {r.BaseWalk:F1} run {r.BaseRun:F1}, eye {r.EyeHeight:F2}), granny={(r.Granny != null)}, spider={(r.Spider != null)}, trap={(r.BearTrapTemplate != null)}, exit={(r.ExitDoor != null)}, steps={r.StepClips.Length}, nav={r.GrannyNav.Length}");
        return r;
    }

    public float Scale => Player.transform.lossyScale.y;

    public Vector3 Feet()
    {
        var s = Scale;
        return Player.transform.position + Vector3.up * (Cc.center.y * s - Cc.height * s * 0.5f);
    }

    public float EyeHeight => MainCam != null ? Mathf.Max(0.5f, MainCam.transform.position.y - Feet().y) : 2.8f;
    /// <summary>World units per metre (Granny's house is built ~2.3x larger than real life).</summary>
    public float U => (StandEye > 0 ? StandEye : EyeHeight) / 1.6f;
    /// <summary>Eye height captured standing: the live value drops when the player crouches.</summary>
    public float StandEye;

    public void Teleport(Vector3 feet, float yaw)
    {
        var s = Scale;
        var pos = feet - Vector3.up * (Cc.center.y * s - Cc.height * s * 0.5f) + Vector3.up * 0.05f;
        var wasCc = Cc.enabled;
        Cc.enabled = false;
        Player.transform.position = pos;
        Player.transform.rotation = Quaternion.Euler(0, yaw, 0);
        Cc.enabled = wasCc;
        try { var ml = Fpc.m_MouseLook; if (ml != null) ml.m_CharacterTargetRot = Quaternion.Euler(0, yaw, 0); } catch { }
    }

    public float Yaw => Player.transform.eulerAngles.y;
    public Vector3 CamPos => MainCam != null ? MainCam.transform.position : Player.transform.position;
    public Vector3 CamFwd => MainCam != null ? MainCam.transform.forward : Player.transform.forward;
}

/// <summary>An NPC body (Granny or the mom spider "Angela") that a player or our AI drives.</summary>
public sealed class Body
{
    public readonly GameObject Go;
    public readonly string Name;
    public readonly NavMeshAgent Agent;
    public readonly Animation Anim;
    public readonly string IdleClip, WalkClip;
    readonly Renderer[] _renderers;
    readonly Collider[] _colliders;
    readonly bool[] _colliderWasEnabled;

    public Vector3 TargetPos;
    public float TargetYaw;
    public byte AnimState;          // 0 idle, 1 walk
    public byte OneShotSeq;         // incremented for each one-shot (hit/look/eat)
    public byte OneShotId;
    public float OneShotUntil;
    public bool HasTarget;
    string _curLoop;

    public Body(GameObject go, string name, NavMeshAgent agent, string idle, string walk)
    {
        Go = go; Name = name; Agent = agent; IdleClip = idle; WalkClip = walk;
        Anim = go.GetComponentInChildren<Animation>(true);
        _renderers = go.GetComponentsInChildren<Renderer>(true).ToArray();
        _colliders = go.GetComponentsInChildren<Collider>(true).ToArray();
        _colliderWasEnabled = _colliders.Select(c => c != null && c.enabled).ToArray();
        TargetPos = go.transform.position; TargetYaw = go.transform.eulerAngles.y;
    }

    public Vector3 Pos => Go.transform.position;
    public float Yaw => Go.transform.eulerAngles.y;

    public void SetVisible(bool v)
    {
        foreach (var r in _renderers) if (r != null) r.enabled = v;
    }

    public void SetCollidersEnabled(bool on)
    {
        for (var i = 0; i < _colliders.Length; i++)
            if (_colliders[i] != null) _colliders[i].enabled = on && _colliderWasEnabled[i];
    }

    /// <summary>Disable trigger colliders: disabled MonoBehaviours still get OnTrigger* calls.</summary>
    public void DisableTriggers()
    {
        foreach (var c in _colliders) if (c != null && c.isTrigger) c.enabled = false;
    }

    public void RestoreColliders() => SetCollidersEnabled(true);

    public void SetAgent(bool on)
    {
        if (Agent == null) return;
        try
        {
            if (on && !Agent.enabled)
            {
                // an agent can only be enabled on the navmesh: snap the body onto it first
                if (NavMesh.SamplePosition(Go.transform.position, out var hit, 25f, NavMesh.AllAreas)) Go.transform.position = hit.position;
                else return;
                Agent.enabled = true;
                if (Agent.isOnNavMesh) Agent.Warp(Go.transform.position);
            }
            else if (!on && Agent.enabled) Agent.enabled = false;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[body] {Name} agent: {e.Message}"); }
    }

    public void Place(Vector3 pos, float yaw)
    {
        Go.transform.position = pos;
        Go.transform.rotation = Quaternion.Euler(0, yaw, 0);
    }

    public bool IsDown;

    public void PlayLoop(string clip)
    {
        if (Anim == null || clip == null || Time.time < OneShotUntil) return;
        if (_curLoop == clip && Anim.IsPlaying(clip)) return;
        try { Anim.CrossFade(clip, 0.2f); _curLoop = clip; } catch { }
    }

    public void PlayOneShot(string clip, float duration)
    {
        if (Anim == null || clip == null) return;
        try { Anim.CrossFade(clip, 0.1f); _curLoop = null; OneShotUntil = Time.time + duration; } catch { }
    }
}
