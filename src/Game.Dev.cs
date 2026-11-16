using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyMP;

/// <summary>Self-test hook: commands from BepInEx/config/grannymp.cmd (the file does not exist in normal play).</summary>
public sealed partial class Game
{
    float _nextDevPoll;
    int _forceExecScene;

    void UpdateDev()
    {
        UpdateRoleSound();
        if (Time.unscaledTime < _nextDevPoll) return;
        _nextDevPoll = Time.unscaledTime + 0.5f;
        var path = Path.Combine(BepInEx.Paths.ConfigPath, Plugin.TestId > 0 ? $"grannymp{Plugin.TestId}.cmd" : "grannymp.cmd");
        if (!File.Exists(path)) return;
        string[] lines;
        try { lines = File.ReadAllLines(path); File.Delete(path); } catch { return; }
        foreach (var line in lines)
        {
            var a = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) continue;
            Plugin.L.LogInfo("[dev] " + line);
            try { DevRun(a); } catch (Exception e) { Plugin.L.LogError("[dev] " + e); }
        }
    }

    /// <summary>Writes every skinned model of the scene, in its current pose, to OBJ files (source of the role icons).</summary>
    void DevBake(string dir)
    {
        Directory.CreateDirectory(dir);
        var n = 0;
        foreach (var r in GameRefs.FindAll<SkinnedMeshRenderer>(true))
        {
            if (r.sharedMesh == null) continue;
            var path = r.name; for (var q = r.transform.parent; q != null; q = q.parent) path = q.name + "/" + path;
            var m = new Mesh(); r.BakeMesh(m);
            var v = m.vertices; var t = m.triangles;
            var w = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
            var sb = new System.Text.StringBuilder("# " + path + (char)10);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            for (var i = 0; i < v.Length; i++) { var p = w.MultiplyPoint3x4(v[i]); sb.Append("v ").Append(p.x.ToString(c)).Append(' ').Append(p.y.ToString(c)).Append(' ').Append(p.z.ToString(c)).Append((char)10); }
            for (var i = 0; i + 2 < t.Length; i += 3) sb.Append("f ").Append(t[i] + 1).Append(' ').Append(t[i + 1] + 1).Append(' ').Append(t[i + 2] + 1).Append((char)10);
            File.WriteAllText(Path.Combine(dir, $"{n:D2}_{r.name}.obj".Replace(' ', '_')), sb.ToString());
            Plugin.L.LogInfo($"[dev] bake {n:D2} {path} v={v.Length} t={t.Length / 3} active={r.gameObject.activeInHierarchy} forward={r.transform.forward}");
            n++;
        }
    }

    static IEnumerable<string> _devAnimNames(GameObject g)
    {
        var an = g.GetComponentInChildren<Animation>(true);
        if (an == null) yield break;
        foreach (var o in an) { var st = o.Cast<AnimationState>(); yield return st.name + (an.IsPlaying(st.name) ? "*" : ""); }
    }

    Camera _photoCam;
    bool _devPhoto;

    /// <summary>Puts a camera in front of Granny: only her (with the real bat) on green, for tools/shot.py.</summary>
    void DevPhoto(float yaw, float dist, float height, float fov)
    {
        var g = R.Granny.Go;
        if (_photoCam == null)
        {
            var go = new GameObject("GrannyMP_Photo");
            _photoCam = go.AddComponent<Camera>();
            _photoCam.depth = 1000; _photoCam.clearFlags = CameraClearFlags.SolidColor;
            _photoCam.backgroundColor = Color.green; _photoCam.cullingMask = 1 << 31;
            _photoCam.nearClipPlane = 0.05f; _photoCam.farClipPlane = 2000f;
        }
        foreach (var r in g.GetComponentsInChildren<Renderer>(true))
        {
            r.gameObject.layer = 31; r.enabled = true;
            var path = r.name; for (var q = r.transform.parent; q != null && q != g.transform; q = q.parent) path = q.name + "/" + path;
            Plugin.L.LogInfo($"[dev] photo {r.GetIl2CppType().Name} {path} active={r.gameObject.activeInHierarchy} pos={r.transform.position}");
        }
        var body = g.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var dir = Quaternion.Euler(0, yaw, 0) * Flat(g.transform.forward);
        var look = body.bounds.center + Vector3.up * height;
        Plugin.L.LogInfo($"[dev] photo bounds {body.bounds.center} size {body.bounds.size} fwd {g.transform.forward} anim {string.Join(",", _devAnimNames(g))}");
        _photoCam.transform.position = look + dir * dist;
        _photoCam.transform.LookAt(look);
        _photoCam.fieldOfView = fov;
        _devPhoto = true;
    }

    void DevCameras()
    {
        foreach (var c in GameRefs.FindAll<Camera>(true))
            if (c.gameObject.activeInHierarchy) Plugin.L.LogInfo($"[dev] camera {c.name} enabled={c.enabled} depth={c.depth} pos={c.transform.position} mask={c.cullingMask}");
    }

    /// <summary>Test kit on the floor in front of the player: rifle + ammo, crossbow + arrows, gas can, freeze trap.</summary>
    void DevBring()
    {
        var pu = R.Player.GetComponentInChildren<PickUp>(true);
        var gas = pu.gascan != null && pu.gascan.activeInHierarchy ? pu.gascan : _objs.Where(o => o.Go != null && o.Go.activeInHierarchy && o.Go.name.StartsWith("Bensindunk")).Select(o => o.Go).FirstOrDefault();
        // world copies only: the PickUp fields point at the models shown in the hand
        GameObject World(string name)
        {
            foreach (var t in GameRefs.FindAll<Transform>(true))
            {
                if (t.name != name || t.GetComponent<Collider>() == null) continue;
                var inPlayer = false;
                for (var q = t; q != null; q = q.parent) if (q == R.Player.transform || q.name.Contains("Hand")) inPlayer = true;
                if (!inPlayer) return t.gameObject;
            }
            return null;
        }
        var items = new List<GameObject> { World("ShotgunBord"), World("Ammo (3)"), World("Armborst"), World("StunnSyringe"), gas, World("FreezeTrap"), World("Ammo (4)"), World("StunnSyringe (1)"), World("StunnSyringe (2)") };
        var fwd = Quaternion.Euler(0, R.Yaw, 0) * Vector3.forward;
        var right = Quaternion.Euler(0, R.Yaw, 0) * Vector3.right;
        var i = 0;
        foreach (var go in items)
        {
            Plugin.L.LogInfo("[dev] bring " + (go != null ? go.name : "null"));
            if (go == null) continue;
            for (var t = go.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            var side = (i % 3 - 1) * 0.42f * U;
            var row = (0.9f + (i / 3) * 0.32f) * U;
            var spot = R.Feet() + fwd * row + right * side;
            go.transform.position = Ground(spot + Vector3.up * U) + Vector3.up * (0.12f * U);
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            i++;
        }
        pu.oldShotgunLoaded = true;
    }

    readonly HashSet<IntPtr> _seenClones = new();
    float _nextCloneScan;

    void DevWatchClones()
    {
        if (Plugin.TestId == 0 || R == null || Time.unscaledTime < _nextCloneScan) return;
        _nextCloneScan = Time.unscaledTime + 0.2f;
        var first = _seenClones.Count == 0;
        foreach (var rb in GameRefs.FindAll<Rigidbody>())
        {
            if (rb == null || !_seenClones.Add(rb.Pointer) || first) continue;
            var t = rb.transform;
            if (t.name.StartsWith("TempNav") || t.name.StartsWith("Noice") || t.name.StartsWith("Vas")) continue;
            var c = t.GetComponent<Collider>();
            Plugin.L.LogInfo($"[clone] NEW {t.name} pos={t.position} scale={t.lossyScale} kin={rb.isKinematic} grav={rb.useGravity} col={(c != null ? c.enabled + "/" + c.isTrigger : "no")} childCols={t.GetComponentsInChildren<Collider>(true).Length} rend={t.GetComponentsInChildren<Renderer>(true).Length} mb={t.GetComponents<MonoBehaviour>().Length} layer={t.gameObject.layer}");
            DevTrack.Add(t);
            DevTrackT = Time.unscaledTime;
        }
        if (Time.unscaledTime - DevTrackT < 4f && Time.unscaledTime >= DevTrackNext)
        {
            DevTrackNext = Time.unscaledTime + 0.4f;
            foreach (var t in DevTrack) Plugin.L.LogInfo(t == null ? "[clone] destroyed" : $"[clone] {t.name} pos={t.position} self={t.gameObject.activeSelf}");
            DevTrack.RemoveAll(t => t == null);
        }
    }
    readonly List<Transform> DevTrack = new();
    float DevTrackT, DevTrackNext;

    void DevRun(string[] a)
    {
        switch (a[0])
        {
            case "host": StartHost(); break;
            case "house": SceneManager.LoadScene("Scene"); break;
            case "soloG": AdminSoloRound(true); break;
            case "soloS": AdminSoloRound(false); break;
            case "bot": AdminAddBot(); break;
            case "swing": _batReadyAt = 0; TryAttack(); break;
            case "gback": Cfg.GrannyBodyBack.Value = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); break;
            case "reloc": _relocateT0 = Now; break;
            case "again": EnterHouse(); break;
            case "check": _checkReadyAt = 0; TryCheckSpot(); break;
            case "boost": _boostReadyAt = 0; TryBoost(); break;
            case "car":
                foreach (var bt in GameRefs.FindAll<bedTrigger>(true))
                {
                    var c = bt.GetComponent<Collider>();
                    if (bt.button == null || c == null || !bt.button.name.StartsWith("Car")) continue;
                    R.Teleport(GroundBelow(c.bounds.center), R.Yaw);
                    Plugin.L.LogInfo($"[dev] at the car trigger {c.bounds.center}");
                    break;
                }
                break;
            case "punch": _punchReadyAt = 0; if (a.Length > 1) Cfg.PunchChance.Value = int.Parse(a[1]); TryPunch(); break;
            case "punches": _punchesLeft = int.Parse(a[1]); break;
            case "fists": _punchT0 = Now; ShowPunchArms(); break;
            case "bowfire": OnBowFired("dev"); break;
            case "spots": _spots = null; Plugin.L.LogInfo($"[dev] in wardrobe={InHidingSpot()} hidden={IsHiding()} feet={R.Feet()}"); break;
            case "speeds": Plugin.L.LogInfo($"[dev] walk {R.Fpc.m_WalkSpeed:F2} run {R.Fpc.m_RunSpeed:F2} base {R.BaseWalk:F2}/{R.BaseRun:F2} boost={Boosting} roll={GrannySpeedRoll:F2}"); break;
            case "doors":
                foreach (var d in _doors)
                {
                    if (d.Comp == null) continue;
                    var dist = Vector3.Distance(d.Comp.transform.position, R.CamPos);
                    if (dist > 8f * U && d.GetLocked?.Invoke() != true && !d.LastLocked) continue;
                    Plugin.L.LogInfo($"[dev] door {d.Comp.name} {d.Comp.GetIl2CppType().Name} dist={dist / U:F1} locked={d.GetLocked?.Invoke()} last={d.LastLocked} open={d.GetOpen()} anim={(d.Anim != null ? d.Anim.isPlaying.ToString() : "none")}");
                    foreach (var mb in d.Comp.GetComponents<MonoBehaviour>()) Plugin.L.LogInfo("[dev]   mb " + mb.GetIl2CppType().Name + " on=" + mb.enabled);
                }
                break;
            case "dump": Plugin.L.LogInfo($"[dev] hiding={IsHiding()} spot={InHidingSpot()} active={R.Player.activeInHierarchy} feet={R.Feet()}"); break;
            case "aimat":
            {
                var o = _objs.Where(x => x.Go != null && x.Go.activeInHierarchy && x.Go.name.StartsWith(a[1])).OrderBy(x => (x.Go.transform.position - R.CamPos).sqrMagnitude).First();
                var dir = o.Go.transform.position - R.CamPos;
                var yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                R.Teleport(R.Feet(), yaw);
                R.Fpc.m_MouseLook.m_CameraTargetRot = Quaternion.Euler(Mathf.Atan2(-dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg, 0, 0);
                Plugin.L.LogInfo($"[dev] aimat {o.Go.name} dist {dir.magnitude / U:F1}");
                break;
            }
            case "hold":
            {
                var l = HeldItems();
                Plugin.L.LogInfo("[dev] held list: " + string.Join(",", l.Select(g => g.name)));
                _devHeld = l.FindIndex(g => g.name.StartsWith(a[1]));
                break;
            }
            case "shot": ReportGrannyStun(20f, 2); break;
            case "blast": ReportExplosion(R.Granny.Pos); break;
            case "spawn": { var go = UnityEngine.Object.Instantiate(DropTemplates()[a[1]], R.Feet() + Vector3.up * U + Quaternion.Euler(0, R.Yaw, 0) * Vector3.forward * U, Quaternion.identity); Plugin.L.LogInfo("[dev] spawned " + go.name); break; }
            case "tmpl":
            {
                var pu = R.Player.GetComponentInChildren<PickUp>(true);
                foreach (var prop in pu.GetType().GetProperties())
                {
                    if (prop.PropertyType != typeof(Transform) || !prop.CanRead) continue;
                    var t = prop.GetValue(pu) as Transform;
                    if (t == null) { Plugin.L.LogInfo($"[tmpl] {prop.Name} = null"); continue; }
                    Plugin.L.LogInfo($"[tmpl] {prop.Name} = {t.name} scene={t.gameObject.scene.IsValid()} self={t.gameObject.activeSelf} hier={t.gameObject.activeInHierarchy} path={PathOf(t)}");
                }
                break;
            }
            case "bring": DevBring(); break;
            case "arrow": R.GrannyAi.StartCoroutine(R.GrannyAi.grannyHitByArrow()); break;
            case "aim":
            {
                var o = _objs.First(x => x.Go != null && x.Go.activeInHierarchy && ItemSpot.IsMatch(x.Go.name) && (a.Length < 2 || x.Go.name.StartsWith(a[1])));
                Plugin.L.LogInfo("[dev] aim at " + o.Go.name + " " + o.Go.transform.position);
                var p = o.Go.transform.position;
                var feet = p + new Vector3(1.6f, 0, 0); feet.y = Ground(p + Vector3.up).y;
                R.Teleport(feet, -90f);
                var eye = feet + Vector3.up * R.EyeHeight;
                var dir = p - eye;
                R.Fpc.m_MouseLook.m_CameraTargetRot = Quaternion.Euler(Mathf.Atan2(-dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg, 0, 0);
                break;
            }
            case "items":
            {
                var on = _objs.Where(o => o.Go != null && o.Go.activeInHierarchy && ItemSpot.IsMatch(o.Go.name)).Select(o => o.Go.name).OrderBy(n => n).ToList();
                Plugin.L.LogInfo($"[dev] items {on.Count}/{_objs.Count}: " + string.Join(",", on));
                break;
            }
            case "take":
            {
                var o = _objs.FirstOrDefault(x => x.Go != null && x.Go.activeInHierarchy && ItemSpot.IsMatch(x.Go.name) && x.Go.name.StartsWith(a[1]));
                Plugin.L.LogInfo("[dev] take " + (o?.Go?.name ?? "none"));
                if (o != null) R.Teleport(o.Go.transform.position, R.Yaw); // pick it up from where it lies
                o?.Go.SetActive(false);
                break;
            }
            case "held": foreach (var p in GameRefs.FindAll<Transform>(true)) if (p.name.EndsWith("Hand") && p.gameObject.activeInHierarchy && p.GetComponent<Renderer>() != null) Plugin.L.LogInfo("[dev] held " + p.name); break;
            case "listen": _listenReadyAt = 0; TryListen(); break;
            case "players":
                foreach (var p in Players.Values) Plugin.L.LogInfo($"[dev] player {p.Name} role={p.Role} flags={p.Flags} hidden={p.Hidden} state={p.HasState} pos={p.Pos} avatar={(p.Avatar?.Go != null ? p.Avatar.Go.activeSelf.ToString() : "none")}");
                break;
            case "tpp":
            {
                var t = Players.Values.First(p => p.Id != Net.LocalId && p.HasState);
                R.Teleport(t.Pos - Quaternion.Euler(0, R.Yaw, 0) * Vector3.forward * (2.2f * U), R.Yaw);
                break;
            }
            case "noise": OnNoise(R.Feet() + Quaternion.Euler(0, R.Yaw, 0) * Vector3.forward * (6f * U)); break;
            case "mouse": { var ml = R.Fpc.m_MouseLook; Plugin.L.LogInfo($"[dev] mouse yaw={R.Yaw:F1} menuFlag={R.Fpc.menuEnabled} normal={_menuFlagNormal} x={ml.XSensitivity} y={ml.YSensitivity} sens={R.Fpc.mouseSens} fpc={R.Fpc.enabled} cc={R.Cc.enabled} lock={Cursor.lockState} focus={Application.isFocused} ts={Time.timeScale} pause={IsPauseMenuOpen()} menu={MenuOpen} lockUntil={_lockUntil - Now}"); break; }
            case "join": StartClient(a[1]); break;
            case "uihost": NameField ??= Cfg.PlayerName.Value; UiHost(); break;
            case "enter": StartRoundHost(0); break;
            case "cellar": R.Teleport(R.SpiderStart + Vector3.up * (0.3f * U), R.Yaw); break;
            case "enters": StartRoundHost(1); break;
            case "endround": UiEndRound(); break;
            case "help": _devHelp = a[1] == "1"; break;
            case "pitch": R.Fpc.m_MouseLook.m_CameraTargetRot = Quaternion.Euler(float.Parse(a[1]), 0, 0); break;
            case "tpbot":
            {
                var b = Players.Values.First(p => _bots.ContainsKey(p.Id) && p.ActiveSurvivor);
                R.Teleport(b.Pos, R.Yaw);
                break;
            }
            case "vmt": SwingTime = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); break;
            case "vm":
            {
                var f = a.Skip(1).Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                ViewBatPos = new Vector3(f[0], f[1], f[2]); ViewBatRot = new Vector3(f[3], f[4], f[5]);
                break;
            }
            case "bake": DevBake(a[1]); break;
            case "photo": DevPhoto(float.Parse(a[1]), float.Parse(a[2]) * U, float.Parse(a[3]) * U, float.Parse(a[4])); break;
            case "photoff": if (_photoCam != null) UnityEngine.Object.Destroy(_photoCam.gameObject); _devPhoto = false; break;
            case "gplay": R.Granny.Go.GetComponentInChildren<Animation>(true).Play(a[1]); break;
            case "batfix":
            {
                foreach (var t in R.Granny.Go.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != "BatBloody") continue;
                    // turn around the grip (the point of the bat's axis inside the fist), not the wrist bone
                    var mb = t.GetComponent<MeshFilter>().sharedMesh.bounds; var sz = mb.size;
                    var local = sz.x >= sz.y && sz.x >= sz.z ? Vector3.right : sz.y >= sz.z ? Vector3.up : Vector3.forward;
                    var axis = t.TransformDirection(local).normalized;
                    var mid = t.GetComponent<Renderer>().bounds.center; var hand = t.parent.position;
                    var grip = mid + axis * Vector3.Dot(hand - mid, axis);
                    var sc = float.Parse(a[2]);
                    t.RotateAround(grip, _photoCam.transform.forward, float.Parse(a[1]));
                    t.position = grip + (t.position - grip) * sc; t.localScale *= sc;
                    var scr = _photoCam.WorldToScreenPoint(grip);
                    Plugin.L.LogInfo($"[dev] bat grip screen {scr} axis {t.TransformDirection(local).normalized}");
                }
                break;
            }
            case "photobat":
                foreach (var r in R.Granny.Go.GetComponentsInChildren<Renderer>(true)) r.gameObject.layer = (a[1] == "1" && r.name != "BatBloody") || (a[1] == "2" && r.name == "BatBloody") ? 0 : 31;
                break;
            case "ts": Time.timeScale = float.Parse(a[1]); break;
            case "rolecard": ShowRoleCard(float.Parse(a[1])); break;
            case "cams": DevCameras(); Plugin.L.LogInfo($"[dev] pixelLights={QualitySettings.pixelLightCount} screen={Screen.width}x{Screen.height} ambient={RenderSettings.ambientLight} mode={RenderSettings.ambientMode} fog={RenderSettings.fog} shader={R?.BaseMaterial?.shader?.name}"); break;
            case "lamp": { var l = GameObject.Find("GrannyMP_Sight")?.GetComponent<Light>(); if (l != null) { l.intensity = float.Parse(a[1]); l.range = float.Parse(a[2]) * U; } break; }
            case "amb": { var v = float.Parse(a[1]); RenderSettings.ambientLight = new Color(v, v, v); break; }
            case "days": Cfg.Days.Value = int.Parse(a[1]); break;
            case "exec": _forceExecScene = int.Parse(a[1]); break;
            case "hitbots": foreach (var id in _bots.Keys.ToList()) TryCatchBot(id); break;
            case "catchme": AdminCatchMe(); break;
            case "step": ExecSend(byte.Parse(a[1])); break;
            case "menu": Menu.Show(a[1] == "1"); break;
            case "lang": Tr.Set(a[1] == "en"); Menu.Rebuild(); break;
            case "pause": R.Fpc.inGameOptionMenu.SetActive(a[1] == "1"); break;
            case "unmenuflag": _menuFlagNormal = !_menuFlagNormal; break;
            case "crouch": foreach (var c in GameRefs.FindAll<playerCrawl>(true)) c.crouching(); break;
            case "tp": R.Teleport(new Vector3(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3])), a.Length > 4 ? float.Parse(a[4]) : 0); break;
        }
    }
}
