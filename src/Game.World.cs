using System.Text;
using Il2CppInterop.Runtime;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyMP;

public sealed partial class Game
{

    sealed class Trap
    {
        public int Id; public GameObject Go; public Animation Anim; public AudioSource Audio;
        public Vector3 Pos; public bool Sprung; public float RemoveAt;
    }
    readonly Dictionary<int, Trap> _traps = new();

    int MyTrapCount() => _traps.Values.Count(t => !t.Sprung && (t.Id >> 24) == Net.LocalId);

    void SpawnTrap(int id, Vector3 pos, float yaw)
    {
        if (R?.BearTrapTemplate == null || _traps.ContainsKey(id)) return;
        var tpl = R.BearTrapTemplate;
        var go = UnityEngine.Object.Instantiate(tpl, pos, Quaternion.Euler(0, yaw, 0) * tpl.transform.rotation);
        go.name = "GrannyMP_Trap_" + id;
        go.transform.localScale = tpl.transform.lossyScale;
        // strip the original single-player logic; we run our own trigger check
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) mb.enabled = false;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) if (c != null) c.enabled = false;
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
        go.SetActive(true);
        var t = new Trap { Id = id, Go = go, Pos = pos, Anim = go.GetComponentInChildren<Animation>(true), Audio = go.GetComponentInChildren<AudioSource>(true) };
        try { if (t.Anim != null) t.Anim.Stop(); } catch { }
        _traps[id] = t;
    }

    void CheckTrapsUnderMe()
    {
        if (_traps.Count == 0) return;
        var feet = R.Feet();
        var rad = Cfg.TrapRadius.Value;
        foreach (var t in _traps.Values)
        {
            if (t.Sprung) continue;
            var d = t.Pos - feet;
            if (Mathf.Abs(d.y) > 1.2f * U) continue;
            d.y = 0;
            if (d.magnitude > rad) continue;
            SpringTrap(t.Id, Net.LocalId);
            var w = Net.Begin(Msg.TrapSprung); w.Put(t.Id); w.Put(Net.LocalId); Net.Send();
            LockMovement(Cfg.TrapStunSeconds.Value);
            FlashAlpha = 0.6f;
            Toast(Tr.L($"Ты попал в капкан! ({Cfg.TrapStunSeconds.Value:F0} с)", $"You stepped into a trap! ({Cfg.TrapStunSeconds.Value:F0} s)"), 3, new Color(1f, 0.3f, 0.3f), big: true);
            break;
        }
    }

    void SpringTrap(int id, byte victim)
    {
        if (!_traps.TryGetValue(id, out var t) || t.Sprung) return;
        t.Sprung = true; t.RemoveAt = Now + 8f;
        if (victim == TrapNoVictim)
        {
            if (AmGranny) Toast(Tr.L("В твой капкан бросили вещь: он захлопнулся впустую", "Something was thrown into your trap: it snapped on nothing"), 3, new Color(1f, 0.6f, 0.3f));
            Plugin.L.LogInfo($"[trap] {id} disarmed by an item");
        }
        try { if (t.Anim != null) t.Anim.Play("Beartrap"); } catch { }
        try
        {
            if (R?.TrapSound != null)
            {
                if (t.Audio != null) { t.Audio.enabled = true; t.Audio.PlayOneShot(R.TrapSound, 1f); }
                else AudioSource.PlayClipAtPoint(R.TrapSound, t.Pos, 1f);
            }
        }
        catch { }
        if (AmGranny && Players.TryGetValue(victim, out var p))
        {
            p.RevealUntil = Now + 3f;
            Toast(Tr.L($"{p.Name} попал в капкан!", $"{p.Name} stepped into a trap!"), 3, new Color(1f, 0.45f, 0.8f), big: true);
        }
    }

    const byte TrapNoVictim = 254;

    /// <summary>Like the original game: an item thrown into a bear trap makes it snap, and the trap is gone.</summary>
    bool TrapUnderItem(Drop d)
    {
        if (_traps.Count == 0 || d.T == null || Now - d.Born > 8f || !InRound) return false;
        var p = d.T.position;
        foreach (var t in _traps.Values)
        {
            if (t.Sprung) continue;
            var dd = p - t.Pos;
            if (dd.y < -0.3f * U || dd.y > 1.0f * U) continue;
            dd.y = 0;
            if (dd.magnitude > Mathf.Max(0.5f * U, Cfg.TrapRadius.Value)) continue;
            SpringTrap(t.Id, TrapNoVictim);
            var w = Net.Begin(Msg.TrapSprung); w.Put(t.Id); w.Put(TrapNoVictim); Net.Send();
            Toast(Tr.L("Капкан захлопнулся на вещи. Путь свободен!", "The trap snapped on the item. The way is clear!"), 3, Color.white);
            ReportNoise(t.Pos);
            return true;
        }
        return false;
    }

    void UpdateTraps()
    {
        foreach (var t in _traps.Values.Where(t => t.Sprung && Now >= t.RemoveAt).ToList())
        {
            if (t.Go != null) UnityEngine.Object.Destroy(t.Go);
            _traps.Remove(t.Id);
        }
    }

    void ClearTraps()
    {
        foreach (var t in _traps.Values) if (t.Go != null) try { UnityEngine.Object.Destroy(t.Go); } catch { }
        _traps.Clear();
    }


    sealed class DoorRef
    {
        public string Key; public MonoBehaviour Comp; public Animation Anim; public AudioSource Audio; public NavMeshObstacle Obstacle;
        public Func<bool> GetOpen; public Action<bool> SetOpen; public Func<bool> GetLocked; public Action<bool> SetLocked;
        public string OpenClip, CloseClip; public Func<bool, AudioClip> Sound;
        public bool LastOpen, LastLocked; public float SuppressUntil;
    }

    sealed class ObjRef
    {
        public Renderer Rend; public bool Gone;
        public string Key; public GameObject Go; public Rigidbody Rb;
        public bool LastActive; public Vector3 LastPos; public Quaternion LastRot;
        public float SuppressUntil, RestoreKinAt; public bool KinOverridden, WasKinematic;
    }

    readonly List<DoorRef> _doors = new();
    readonly Dictionary<string, DoorRef> _doorByKey = new();
    readonly List<ObjRef> _objs = new();
    readonly Dictionary<string, ObjRef> _objByKey = new();
    float _nextWorldScan;
    int _lastExitMask = -1; float _exitSuppressUntil;

    void ClearWorld()
    {
        _doors.Clear(); _doorByKey.Clear(); _objs.Clear(); _objByKey.Clear(); _lastExitMask = -1;
    }

    static string PathOf(Transform t)
    {
        var sb = new StringBuilder();
        while (t != null)
        {
            sb.Insert(0, "/" + t.name + "#" + t.GetSiblingIndex());
            t = t.parent;
        }
        return sb.ToString();
    }

    void AddDoors<T>(string open, string close, Func<T, bool> getO, Action<T, bool> setO,
                     Func<T, bool> getL = null, Action<T, bool> setL = null, Func<T, bool, AudioClip> sound = null) where T : MonoBehaviour
    {
        foreach (var c in GameRefs.FindAll<T>())
        {
            var key = PathOf(c.transform) + "|" + typeof(T).Name;
            if (_doorByKey.ContainsKey(key)) continue;
            var d = new DoorRef
            {
                Key = key, Comp = c, Anim = c.GetComponent<Animation>(), Audio = c.GetComponent<AudioSource>(), Obstacle = c.GetComponent<NavMeshObstacle>(),
                GetOpen = () => getO(c), SetOpen = v => setO(c, v),
                GetLocked = getL != null ? () => getL(c) : null, SetLocked = setL != null ? v => setL(c, v) : null,
                OpenClip = open, CloseClip = close, Sound = sound != null ? v => sound(c, v) : null,
            };
            d.LastOpen = d.GetOpen(); d.LastLocked = d.GetLocked?.Invoke() ?? false;
            _doors.Add(d); _doorByKey[key] = d;
        }
    }

    void BuildWorld()
    {
        ClearWorld();
        try
        {
            AddDoors<OpenCloseDoors>("InnerdoorOpen", "InnerdoorClose", c => c.DoorOpen, (c, v) => { c.DoorOpen = v; c.DoorMoving = false; }, c => c.doorLocked, (c, v) => c.doorLocked = v, (c, v) => v ? c.doorOpen : c.doorClose);
            AddDoors<OpenCloseDoorVind>("InnerdoorOpen", "InnerdoorClose", c => c.DoorOpen, (c, v) => { c.DoorOpen = v; c.DoorMoving = false; }, c => c.doorLocked, (c, v) => c.doorLocked = v, (c, v) => v ? c.doorOpen : c.doorClose);
            AddDoors<OpenCloseSmallDoors>("SmallDoorOpen", "SmallDoorClose", c => c.DoorOpen, (c, v) => { c.DoorOpen = v; c.DoorMoving = false; }, c => c.doorLocked, (c, v) => c.doorLocked = v, (c, v) => v ? c.doorOpen : c.doorClose);
            AddDoors<OpenCloseGarde1>("Garde1Open", "Garde1Close", c => c.DoorOpen, (c, v) => c.DoorOpen = v, sound: (c, v) => v ? c.doorLjud : c.doorLjud2);
            AddDoors<OpenCloseKitchenDoor1>("KitchenDoor1Open", "KitchenDoor1Close", c => c.DoorOpen, (c, v) => c.DoorOpen = v, sound: (c, v) => c.doorLjud);
            AddDoors<OpenCloseKitchenDoor2>("KitchenDoor2Open", "KitchenDoor2Close", c => c.DoorOpen, (c, v) => c.DoorOpen = v);
            AddDoors<OpenCloseKyl>("KylOpen", "KylClose", c => c.DoorOpen, (c, v) => c.DoorOpen = v, sound: (c, v) => c.doorLjud);
            AddDoors<OpenCloseLoda1>("Loda1Open", "Loda1Close", c => c.DoorOpen, (c, v) => c.DoorOpen = v, sound: (c, v) => v ? c.LodaUt : c.LodaIn);
            AddDoors<OpenCloseLoda2>("Loda2Open", "Loda2Close", c => c.DoorOpen, (c, v) => { c.DoorOpen = v; c.LodaMoving = false; }, sound: (c, v) => v ? c.LodaUt : c.LodaIn);
            AddDoors<openCloseVitrindoorL>("VitrindoorHallLOpen", "VitrindoorHallLClose", c => c.DoorOpen, (c, v) => c.DoorOpen = v);
            AddDoors<openCloseVitrindoorR>("VitrindoorHallOpen", "VitrindoorHallClose", c => c.DoorOpen, (c, v) => c.DoorOpen = v);
            AddDoors<openCloseVitrindoorVRL>("VitrindoorVRLOpen", "VitrindoorVRLClose", c => c.DoorOpen, (c, v) => c.DoorOpen = v);
            AddDoors<openCloseVitrindoorVRR>("VitrindoorVRROpen", "VitrindoorVRRClose", c => c.DoorOpen, (c, v) => c.DoorOpen = v);
            AddDoors<prisonDoorOpenClose>("prisondoorOpen", "prisondoorClose", c => c.DoorOpen, (c, v) => { c.DoorOpen = v; c.DoorMoving = false; }, c => c.doorLocked, (c, v) => c.doorLocked = v);
        }
        catch (Exception e) { Plugin.L.LogError("[world] doors: " + e); }

        try { BuildObjects(); }
        catch (Exception e) { Plugin.L.LogError("[world] objects: " + e); }
        Plugin.L.LogInfo($"[world] sync: {_doors.Count} doors/drawers, {_objs.Count} objects");
    }

    static readonly System.Text.RegularExpressions.Regex ItemSpot = new(@"PL_?\d+( \(\d+\))?$");

    void BuildObjects()
    {
        var gos = new List<GameObject>();
        var seen = new HashSet<IntPtr>();
        void Add(GameObject go)
        {
            if (go == null || !seen.Add(go.Pointer)) return;
            if (IsExcluded(go.transform)) return;
            gos.Add(go);
        }
        // all GameObject fields of PickUp (items + "in place" variants) and CheckExitDoor (locks, planks, batteries)
        foreach (var comp in new Il2CppSystem.Object[] { R.Player.GetComponentInChildren<PickUp>(true), R.ExitDoor })
        {
            if (comp == null) continue;
            foreach (var prop in comp.GetType().GetProperties())
            {
                if (prop.PropertyType != typeof(GameObject) || !prop.CanRead) continue;
                try { Add(prop.GetValue(comp) as GameObject); } catch { }
            }
        }
        // the groups of spots switched on by the day script
        if (R.Day != null)
            foreach (var prop in R.Day.GetType().GetProperties())
                if (prop.PropertyType == typeof(GameObject) && prop.Name.StartsWith("OP") && prop.CanRead)
                    try { Add(prop.GetValue(R.Day) as GameObject); } catch { }
        // every possible spot of every item: each copy of the game rolls its own, the host's roll wins at round start
        foreach (var t in GameRefs.FindAll<Transform>(true))
            if (ItemSpot.IsMatch(t.name)) Add(t.gameObject);
        // loose physics props (vases, books, items lying around)
        foreach (var rb in GameRefs.FindAll<Rigidbody>())
        {
            if (rb == null || rb.isKinematic) continue;
            if (rb.GetComponent<Renderer>() == null) continue;
            Add(rb.gameObject);
        }
        foreach (var go in gos.OrderBy(g => PathOf(g.transform)))
        {
            var key = PathOf(go.transform);
            if (_objByKey.ContainsKey(key)) continue;
            var o = new ObjRef { Key = key, Go = go, Rb = go.GetComponent<Rigidbody>(), LastActive = go.activeSelf, LastPos = go.transform.position, LastRot = go.transform.rotation };
            _objs.Add(o); _objByKey[key] = o;
        }
    }

    bool IsExcluded(Transform t)
    {
        var n = t.name;
        if (n.Contains("Hand") || n.Contains("Button") || n.Contains("Text") || n.Contains("Ragdoll") || n.Contains("rat_") || n.Contains("Raven") || n.StartsWith("GrannyMP"))
            return true;
        if (t.GetComponent<RectTransform>() != null) return true;
        for (var p = t; p != null; p = p.parent)
        {
            if (R.Player != null && p == R.Player.transform) return true;
            if (R.Granny != null && p == R.Granny.Go.transform) return true;
            if (R.Spider != null && p == R.Spider.Go.transform) return true;
        }
        return false;
    }

    void UpdateWorldSync()
    {
        if (Now < _nextWorldScan || R == null) return;
        _nextWorldScan = Now + 0.1f;
        var me = R.CamPos;
        var rad = Cfg.SyncRadius.Value * U / 1.75f;
        var canBroadcast = !_spectating;

        foreach (var d in _doors)
        {
            if (d.Comp == null) continue;
            bool open, locked;
            try { open = d.GetOpen(); locked = AmGranny ? d.LastLocked : d.GetLocked?.Invoke() ?? false; } catch { continue; }
            if (open == d.LastOpen && locked == d.LastLocked) continue;
            d.LastOpen = open; d.LastLocked = locked;
            if (!canBroadcast || Now < d.SuppressUntil) continue;
            if ((d.Comp.transform.position - me).sqrMagnitude > rad * rad) continue;
            var w = Net.Begin(Msg.Door); WriteDoor(w, d); Net.Send();
            // opening doors, wardrobes and drawers is an ordinary action: no noise mark for Granny
        }

        foreach (var o in _objs)
        {
            if (o.Go == null)
            {
                // some items are destroyed by the game when taken
                if (!o.Gone && canBroadcast) { o.Gone = true; o.LastActive = false; var wg = Net.Begin(Msg.Obj); WriteObj(wg, o); Net.Send(); Plugin.L.LogInfo("[world] gone " + o.Key); }
                continue;
            }
            if (o.KinOverridden && Now >= o.RestoreKinAt && o.Rb != null)
            {
                o.KinOverridden = false;
                try { o.Rb.isKinematic = o.WasKinematic; } catch { }
            }
            // some items are only hidden when taken: the object stays on with its renderer off
            var rend = o.Rend ??= o.Go.GetComponent<Renderer>();
            var active = o.Go.activeSelf && (rend == null || rend.enabled);
            var t = o.Go.transform;
            var pos = t.position; var rot = t.rotation;
            var moved = (pos - o.LastPos).sqrMagnitude > 0.05f * 0.05f * U * U || Quaternion.Angle(rot, o.LastRot) > 3f;
            if (active == o.LastActive && !(active && moved)) continue;
            var wasPos = o.LastPos;
            if (active != o.LastActive) Plugin.L.LogInfo($"[world] {o.Go.name} active={active} pos={pos} canSend={canBroadcast}");
            o.LastActive = active; o.LastPos = pos; o.LastRot = rot;
            if (!canBroadcast || Now < o.SuppressUntil) continue;
            if ((pos - me).sqrMagnitude > rad * rad && (wasPos - me).sqrMagnitude > rad * rad) continue;
            var w = Net.Begin(Msg.Obj); WriteObj(w, o); Net.Send();
        }

        if (R.ExitDoor != null)
        {
            var mask = ExitMask();
            if (mask != _lastExitMask)
            {
                var first = _lastExitMask < 0;
                _lastExitMask = mask;
                if (!first && Now >= _exitSuppressUntil && canBroadcast)
                {
                    var w = Net.Begin(Msg.Exit); w.Put(mask); w.Put(R.ExitDoor.counter); Net.Send();
                }
            }
        }
    }

    static void WriteDoor(NetDataWriter w, DoorRef d) { w.Put(d.Key); w.Put(d.LastOpen); w.Put(d.LastLocked); }
    static void WriteObj(NetDataWriter w, ObjRef o) { w.Put(o.Key); w.Put(o.LastActive); w.Put(o.LastPos); w.Put(o.LastRot); }

    void ReadDoor(NetDataReader r, bool animate)
    {
        var key = r.GetString(); var open = r.GetBool(); var locked = r.GetBool();
        if (!_doorByKey.TryGetValue(key, out var d) || d.Comp == null) return;
        d.SuppressUntil = Now + 0.6f;
        try
        {
            if (d.SetLocked != null && d.GetLocked() != locked) d.SetLocked(locked);
            if (d.GetOpen() != open)
            {
                d.SetOpen(open);
                if (d.Anim != null) d.Anim.Play(open ? d.OpenClip : d.CloseClip);
                if (animate && d.Audio != null && d.Sound != null)
                {
                    var clip = d.Sound(open);
                    if (clip != null) d.Audio.PlayOneShot(clip);
                }
                if (d.Obstacle != null) d.Obstacle.enabled = !open;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[world] door apply: " + e.Message); }
        d.LastOpen = open; d.LastLocked = locked;
    }

    void ReadObj(NetDataReader r, bool live)
    {
        var key = r.GetString(); var active = r.GetBool(); var pos = r.GetVec3(); var rot = r.GetQuat();
        if (!_objByKey.TryGetValue(key, out var o) || o.Go == null) return;
        o.SuppressUntil = Now + 0.6f;
        try
        {
            if (o.Go.activeSelf != active) o.Go.SetActive(active);
            if (active)
            {
                o.Go.transform.SetPositionAndRotation(pos, rot);
                if (o.Rb != null)
                {
                    if (!o.KinOverridden) { o.WasKinematic = o.Rb.isKinematic; o.KinOverridden = true; }
                    o.Rb.isKinematic = true;
                    o.RestoreKinAt = Now + 1.5f;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[world] obj apply: " + e.Message); }
        o.LastActive = active; o.LastPos = pos; o.LastRot = rot;
    }

    int ExitMask()
    {
        var e = R.ExitDoor;
        var m = 0;
        if (e.lampa1ok) m |= 1; if (e.lampa2ok) m |= 2; if (e.planka1Bort) m |= 4; if (e.planka2Bort) m |= 8;
        if (e.hangLockBort) m |= 16; if (e.DpadlockBort) m |= 32; if (e.batteryLockOk) m |= 64; if (e.extremeLockOk) m |= 128;
        if (e.extraLockOK) m |= 256; if (e.extraLockPlaced) m |= 512;
        return m;
    }

    void ReadExit(NetDataReader r)
    {
        var mask = r.GetInt(); var counter = r.GetFloat();
        if (R?.ExitDoor == null) return;
        var e = R.ExitDoor;
        _exitSuppressUntil = Now + 0.6f;
        // only ever add progress, never undo someone else's
        if ((mask & 1) != 0) e.lampa1ok = true; if ((mask & 2) != 0) e.lampa2ok = true;
        if ((mask & 4) != 0) e.planka1Bort = true; if ((mask & 8) != 0) e.planka2Bort = true;
        if ((mask & 16) != 0) e.hangLockBort = true; if ((mask & 32) != 0) e.DpadlockBort = true;
        if ((mask & 64) != 0) e.batteryLockOk = true; if ((mask & 128) != 0) e.extremeLockOk = true;
        if ((mask & 256) != 0) e.extraLockOK = true; if ((mask & 512) != 0) e.extraLockPlaced = true;
        if (counter > e.counter) e.counter = counter;
        _lastExitMask = ExitMask();
    }

    /// <summary>Host: push the whole world state (round start / late join). target 255 = everyone.</summary>
    void SendSnapshotTo(byte target)
    {
        if (!Net.IsHost || R == null) return;
        void Flush(Action<NetDataWriter> fill)
        {
            fill(Net.Writer);
            if (target == 255) Net.Send(); else Net.SendTo(target);
        }
        for (var i = 0; i < _doors.Count; i += 40)
        {
            var chunk = _doors.Skip(i).Take(40).Where(d => d.Comp != null).ToList();
            Net.Begin(Msg.SnapDoors);
            Flush(w => { w.Put((ushort)chunk.Count); foreach (var d in chunk) { d.LastOpen = d.GetOpen(); if (!AmGranny) d.LastLocked = d.GetLocked?.Invoke() ?? false; WriteDoor(w, d); } });
        }
        for (var i = 0; i < _objs.Count; i += 40)
        {
            var chunk = _objs.Skip(i).Take(40).Where(o => o.Go != null).ToList();
            Net.Begin(Msg.SnapObjs);
            Flush(w =>
            {
                w.Put((ushort)chunk.Count);
                foreach (var o in chunk) { o.LastActive = o.Go.activeSelf; o.LastPos = o.Go.transform.position; o.LastRot = o.Go.transform.rotation; WriteObj(w, o); }
            });
        }
        if (R.ExitDoor != null)
        {
            Net.Begin(Msg.Exit);
            Flush(w => { w.Put(ExitMask()); w.Put(R.ExitDoor.counter); });
        }
    }

    void AiOpenNearbyDoor(Vector3 pos)
    {
        foreach (var d in _doors)
        {
            if (d.Comp == null) continue;
            if ((d.Comp.transform.position - pos).sqrMagnitude > 3.5f * 3.5f * U * U) continue;
            bool open, locked;
            try { open = d.GetOpen(); locked = AmGranny ? d.LastLocked : d.GetLocked?.Invoke() ?? false; } catch { continue; }
            if (open) continue;
            try
            {
                if (locked) d.SetLocked?.Invoke(false);
                d.SetOpen(true);
                if (d.Anim != null) d.Anim.Play(d.OpenClip);
                if (d.Obstacle != null) d.Obstacle.enabled = false;
                var clip = d.Sound?.Invoke(true);
                if (clip != null && d.Audio != null) d.Audio.PlayOneShot(clip);
            }
            catch { }
            d.LastOpen = true; d.LastLocked = false;
            var w = Net.Begin(Msg.Door); WriteDoor(w, d); Net.Send();
            return;
        }
    }

    // ---- dropped items: the game destroys an item when taken and creates a new copy from a prefab when dropped ----

    sealed class Drop
    {
        public Transform T; public Rigidbody Rb; public Vector3 Sent; public Vector3 To; public Quaternion ToRot; public bool Mine, Settled;
        public string Name; public Vector3 SafePos, CamAtDrop; public float Born; public bool WallChecked, Sounded; public int Rescues;
    }
    readonly Dictionary<int, Drop> _drops = new();
    readonly HashSet<IntPtr> _knownDrops = new();
    Dictionary<string, GameObject> _dropTemplates;
    int _dropCounter;
    Transform _dropTrace; float _dropTraceUntil; GameObject _dropDebug;
    float _nextDropScan;

    Dictionary<string, GameObject> DropTemplates()
    {
        if (_dropTemplates != null) return _dropTemplates;
        _dropTemplates = new();
        var pu = R.Player.GetComponentInChildren<PickUp>(true);
        foreach (var prop in pu.GetType().GetProperties())
            if (prop.PropertyType == typeof(Transform) && prop.CanRead)
                try { if (prop.GetValue(pu) is Transform t && t != null) _dropTemplates[t.name] = t.gameObject; } catch { }
        return _dropTemplates;
    }

    Transform _dropPoint, _dropPointTrap;
    bool _dropLogged;

    /// <summary>
    /// The game spawns a dropped item at DropPoint, and in a session that point stays far from the player:
    /// the item appears in another room and seems to vanish. Keep it right in front of the camera.
    /// </summary>
    void KeepDropPoint()
    {
        if (R == null || R.MainCam == null || !AmSurvivor || !R.Player.activeInHierarchy) return;
        try
        {
            if (_dropPoint == null)
            {
                var pu = R.Player.GetComponentInChildren<PickUp>(true);
                _dropPoint = pu?.dropPoint?.transform;
                _dropPointTrap = GameObject.Find("DropPointFreezeTrap")?.transform;
            }
            if (_dropPoint == null) return;
            var fwd = R.CamFwd; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = R.Player.transform.forward;
            fwd.Normalize();
            // at the height the item appears, with room for long things (crossbow, shotgun): never inside a wall
            var from = R.CamPos - Vector3.up * (0.2f * U);
            var dist = 0.55f * U;
            if (Physics.SphereCast(from, 0.12f * U, fwd, out var hit, dist + 0.45f * U, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(R.Player.transform))
                dist = Mathf.Max(0f, hit.distance - 0.45f * U);
            var pos = from + fwd * dist;
            if (!_dropLogged) { _dropLogged = true; Plugin.L.LogInfo($"[drop] point was {_dropPoint.position}, now {pos}"); }
            _dropPoint.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd));
            if (_dropPointTrap != null) _dropPointTrap.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd));
        }
        catch (Exception e) { Plugin.L.LogWarning("[drop] point: " + e.Message); }
    }

    void ClearDrops()
    {
        foreach (var d in _drops.Values) if (!d.Mine && d.T != null) UnityEngine.Object.Destroy(d.T.gameObject);
        _drops.Clear();
    }

    void UpdateDrops()
    {
        foreach (var d in _drops.Values)
            if (!d.Mine && d.Settled && d.T != null)
            {
                var k = Mathf.Clamp01(Time.deltaTime * 12f);
                d.T.position = Vector3.Lerp(d.T.position, d.To, k);
                d.T.rotation = Quaternion.Slerp(d.T.rotation, d.ToRot, k);
            }
        if (!InRound || Now < _nextDropScan || R == null) return;
        _nextDropScan = Now + 0.15f;
        if (_dropTrace != null && Now < _dropTraceUntil) Plugin.L.LogInfo($"[drop] trace {_dropTrace.position}");

        // copies that were picked up again
        foreach (var kv in _drops.ToList())
        {
            var d = kv.Value;
            if (d.T == null)
            {
                _drops.Remove(kv.Key);
                var w = Net.Begin(Msg.ItemGone); w.Put(kv.Key); Net.Send();
            }
            else if (RescueIfLost(d)) { }
            else if (d.Mine && TrapUnderItem(d)) { }
            else if (d.Mine && d.Rb != null && d.Rb.linearVelocity.sqrMagnitude < 0.01f && (d.T.position - d.Sent).sqrMagnitude > 0.0025f)
            {
                d.Sent = d.T.position; // it came to rest: this is where everybody should see it
                var w = Net.Begin(Msg.ItemMove); w.Put(kv.Key); w.Put(d.T.position); w.Put(d.T.rotation); Net.Send(false);
            }
        }

        // copies this player just dropped
        foreach (var rb in GameRefs.FindAll<Rigidbody>())
        {
            if (rb == null || !_knownDrops.Add(rb.Pointer)) continue;
            var name = rb.name;
            if (!name.EndsWith("(Clone)")) continue;
            var prefab = name[..^7];
            if (!DropTemplates().ContainsKey(prefab)) continue;
            // only what this player has just dropped: the game also creates copies by itself (a vase at the start)
            var deathDrop = DeathDropPending;
            if (!AmSurvivor || (!deathDrop && (rb.transform.position - R.CamPos).sqrMagnitude > 16f * U * U)) continue;
            var id = (Net.LocalId << 24) | (++_dropCounter & 0xFFFFFF);
            var t = rb.transform;
            var ffwd = R.CamFwd; ffwd.y = 0; ffwd = ffwd.sqrMagnitude > 0.001f ? ffwd.normalized : R.Player.transform.forward;
            var safe = R.Feet() + ffwd * (0.3f * U) + Vector3.up * (0.6f * U); // the player stands on a floor: in front of the feet is always inside
            if (deathDrop)
            {
                // the game drops the held item only after we have moved the player to the bed: it belongs where they were caught
                Plugin.L.LogInfo($"[drop] {prefab} dropped at {t.position}, left at the place of the catch {_deathDropPos}");
                t.position = _deathDropPos; safe = _deathDropPos;
                rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                _deathDropUntil = 0;
            }
            _drops[id] = new Drop
            {
                T = t, Rb = rb, Sent = t.position, Mine = true, Name = prefab, Born = Now, CamAtDrop = deathDrop ? _deathDropPos : R.CamPos,
                SafePos = safe, WallChecked = deathDrop,
            };
            var w = Net.Begin(Msg.ItemDrop); w.Put(id); w.Put(prefab); w.Put(t.position); w.Put(t.rotation); w.Put(rb.linearVelocity); Net.Send();
            Plugin.L.LogInfo($"[drop] {prefab} #{id} sent");
            try
            {
                var c = t.GetComponent<Collider>();
                var dp = _dropDebug ??= GameObject.Find("DropPoint");
                Plugin.L.LogInfo($"[drop] at {t.position} vel {rb.linearVelocity} scale {t.lossyScale} kin {rb.isKinematic} col {(c != null ? c.enabled + "/trigger " + c.isTrigger + "/" + c.GetType().Name : "none")} children {t.GetComponentsInChildren<Collider>(true).Length} dropPoint {(dp != null ? dp.transform.position.ToString() : "?")} feet {R.Feet()}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[drop] info: " + e.Message); }
            _dropTrace = t; _dropTraceUntil = Now + 3f;
        }
    }

    void ReadItemDrop(NetDataReader r)
    {
        var id = r.GetInt(); var prefab = r.GetString(); var pos = r.GetVec3(); var rot = r.GetQuat(); var vel = r.GetVec3();
        if (R == null || _drops.ContainsKey(id) || !DropTemplates().TryGetValue(prefab, out var tpl)) return;
        var go = UnityEngine.Object.Instantiate(tpl, pos, rot);
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null) { rb.linearVelocity = vel; _knownDrops.Add(rb.Pointer); }
        _drops[id] = new Drop { T = go.transform, Rb = rb, To = pos, ToRot = rot, Name = prefab, Born = Now, SafePos = pos, WallChecked = true };
        Plugin.L.LogInfo($"[drop] {prefab} #{id} received at {pos}");
    }

    AudioClip _fallClip; bool _fallClipTried;

    /// <summary>The game's own "object falls on the floor" sound (objectFallFloor.ObjectLjud).</summary>
    void PlayFallSound(Vector3 pos)
    {
        try
        {
            if (!_fallClipTried)
            {
                _fallClipTried = true;
                foreach (var f in GameRefs.FindAll<objectFallFloor>(true)) if (f != null && f.ObjectLjud != null) { _fallClip = f.ObjectLjud; break; }
                _fallClip ??= R?.HitClip;
                Plugin.L.LogInfo("[drop] fall sound: " + (_fallClip != null ? _fallClip.name : "none"));
            }
            if (_fallClip != null) AudioSource.PlayClipAtPoint(_fallClip, pos, 1f);
        }
        catch (Exception e) { Plugin.L.LogWarning("[drop] sound: " + e.Message); }
    }

    float? _voidY;

    // ---- the item in hand at a catch stays where the player was caught ----
    Vector3 _deathDropPos; float _deathDropUntil, _deathCaughtAt;

    /// <summary>A drop right after a catch (until the player is up again) is the item that was in the hands.</summary>
    bool DeathDropPending => _deathDropUntil > Now && (GettingUp || !R.Player.activeInHierarchy || Now - _deathCaughtAt < 5f || !Me.Alive);

    void RememberDeathDrop()
    {
        try
        {
            if (MyHeldItem() < 0) { _deathDropUntil = 0; return; }
            var fwd = R.CamFwd; fwd.y = 0; fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : R.Player.transform.forward;
            _deathDropPos = GroundBelow(R.Feet() + fwd * (0.35f * U)) + Vector3.up * (0.35f * U);
            _deathCaughtAt = Now;
            _deathDropUntil = Now + 40f; // the game's own under-bed scene keeps the player off for ~26 s
            Plugin.L.LogInfo($"[drop] caught holding an item at {_deathDropPos}");
        }
        catch (Exception e) { Plugin.L.LogWarning("[drop] death: " + e.Message); }
    }

    /// <summary>Lower than anything in the house and around it: an item down there has fallen out of the world.</summary>
    float VoidY()
    {
        if (_voidY != null) return _voidY.Value;
        var ys = new List<float> { R.PlayerSpawn.y, R.GrannySpawn.y, R.SpiderStart.y };
        try { ys.AddRange(R.GrannyNav.Where(n => n != null).Select(n => n.position.y)); } catch { }
        ys.AddRange(R.GrannyStarts.Select(p => p.y));
        _voidY = ys.Min() - 10f * U;
        return _voidY.Value;
    }

    /// <summary>
    /// A dropped item that went through a wall (spawned inside it) or fell through the floor out of the world comes
    /// back in front of the place where it was dropped. Items thrown out of a window still land on the ground.
    /// </summary>
    bool RescueIfLost(Drop d)
    {
        try
        {
            string why = null;
            var pos = d.T.position;
            if (pos.y < VoidY()) why = "below the world";
            else if (d.Rb != null && !d.Rb.isKinematic && d.Rb.linearVelocity.y < -2f * U
                     && !Physics.Raycast(pos + Vector3.up * (0.1f * U), Vector3.down, 80f * U, ~0, QueryTriggerInteraction.Ignore))
                why = "falling, no floor under it";
            else if (d.Mine && !d.WallChecked && Now - d.Born > 0.1f)
            {
                d.WallChecked = true;
                if (WallBetween(d.CamAtDrop, pos, d.T)) why = "behind a wall";
            }
            if (why == null || d.Rescues >= 5) return false;
            d.Rescues++;
            var to = d.Mine ? d.SafePos : (d.Settled ? d.To : d.SafePos);
            d.T.position = to;
            if (d.Rb != null) { d.Rb.linearVelocity = Vector3.zero; d.Rb.angularVelocity = Vector3.zero; }
            d.WallChecked = true;
            Plugin.L.LogWarning($"[drop] {d.Name} rescued ({why}): {pos} -> {to}");
            return true;
        }
        catch (Exception e) { Plugin.L.LogWarning("[drop] rescue: " + e.Message); return false; }
    }

    /// <summary>A collider between two points that is neither the item itself nor the player.</summary>
    bool WallBetween(Vector3 from, Vector3 to, Transform item)
    {
        var dir = to - from;
        var left = dir.magnitude;
        if (left < 0.01f) return false;
        dir /= left;
        var start = from;
        for (var i = 0; i < 4 && left > 0.05f * U; i++)
        {
            if (!Physics.Raycast(start, dir, out var hit, left, ~0, QueryTriggerInteraction.Ignore)) return false;
            var t = hit.transform;
            var ignore = t == item || t.IsChildOf(item) || (R.Player != null && t.IsChildOf(R.Player.transform)) || t.name.StartsWith("GrannyMP");
            if (!ignore) return hit.distance < left - 0.15f * U;
            var step = hit.distance + 0.02f * U;
            start += dir * step; left -= step;
        }
        return false;
    }

    void ReadItemMove(NetDataReader r)
    {
        var id = r.GetInt(); var pos = r.GetVec3(); var rot = r.GetQuat();
        if (!_drops.TryGetValue(id, out var d)) return;
        d.To = pos; d.ToRot = rot; d.Settled = true;
        if (d.Rb != null) d.Rb.isKinematic = true;
        if (!d.Sounded && Now - d.Born < 6f) { d.Sounded = true; PlayFallSound(pos); } // someone else's item hit the floor: everybody hears it
    }

    void ReadItemGone(NetDataReader r)
    {
        var id = r.GetInt();
        if (!_drops.TryGetValue(id, out var d)) return;
        _drops.Remove(id);
        if (d.T != null) UnityEngine.Object.Destroy(d.T.gameObject);
        Plugin.L.LogInfo($"[drop] #{id} gone");
    }
}
