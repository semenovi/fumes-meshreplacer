using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

static class VehicleFactory
{
    // Keyed by def.Id.
    static readonly Dictionary<string, CustomVehicleDef> _defs     = new();
    static readonly List<Game.BodyType>                  _clones   = new();
    // Maps clone body id → (def, cloneBodyType, originalBodyType) for the suspension patch.
    static readonly Dictionary<string, (CustomVehicleDef def, Game.BodyType clone, Game.BodyType original)> _bodyMap = new();
    static bool _bodiesInjected;

    // ── Debug toggle ──────────────────────────────────────────────────────────
    // When true: one item of each configured suspension is granted to the player
    // on every game load so all options are immediately testable in the garage.
    // Set to false before release to stop handing out free suspensions.
    public static bool GrantDebugSuspensions = true;
    // ─────────────────────────────────────────────────────────────────────────

    // Look up def by license-plate marker (Cars list) or by cloned body type id (Body list).
    public static CustomVehicleDef? GetDef(string? id)
        => id != null && _defs.TryGetValue(id, out var d) ? d : null;

    public static List<Game.BodyType> GetClones() => _clones;

    public static Dictionary<string, (CustomVehicleDef def, Game.BodyType clone, Game.BodyType original)> GetBodyMap()
        => _bodyMap;

    public static CustomVehicleDef? GetDefForVehicle(Game.Vehicle vehicle)
    {
        try
        {
            // Check license-plate marker (vehicle assembled from Cars slot).
            var plate = vehicle.config?.licensePlate;
            if (plate != null && _defs.TryGetValue(plate, out var d1)) return d1;
            // Check body type id (vehicle assembled from Body slot with cloned BodyType).
            var bodyId = vehicle.config?.body?.Type?.id;
            if (bodyId != null && _defs.TryGetValue(bodyId, out var d2)) return d2;
        }
        catch { }
        return null;
    }

    // Called from ItemDatabasePatch — just registers defs, no BodyType cloning.
    public static void RegisterDefs()
    {
        if (_bodiesInjected) return;
        _bodiesInjected = true;
        foreach (var def in Plugin.Cfg.CustomVehicles)
            if (!string.IsNullOrEmpty(def.Id)) _defs[def.Id] = def;
        Plugin.L.LogInfo($"[VF] Registered {_defs.Count} custom vehicle def(s)");
    }

    // Injects cloned BodyTypes into ItemDatabase so the Body list shows our custom vehicle.
    public static void InjectBodies()
    {
        if (_bodiesInjected) return;
        _bodiesInjected = true;

        if (Plugin.Cfg.CustomVehicles.Length == 0) return;
        var bodies = Game.ItemDatabase.Bodies;
        if (bodies == null) { Plugin.L.LogError("[VF] Bodies is null"); return; }

        int oldLen = bodies.Count;
        foreach (var def in Plugin.Cfg.CustomVehicles)
        {
            if (string.IsNullOrEmpty(def.Id) || string.IsNullOrEmpty(def.BaseBodyId)) continue;

            Game.BodyType? baseBody = null;
            for (int i = 0; i < oldLen; i++)
                try { if (bodies[i]?.id == def.BaseBodyId) { baseBody = bodies[i]; break; } } catch { }
            if (baseBody == null) { Plugin.L.LogWarning($"[VF] Base body '{def.BaseBodyId}' not found"); continue; }

            Game.BodyType clone;
            try { clone = UnityEngine.Object.Instantiate(baseBody).Cast<Game.BodyType>(); }
            catch (Exception e) { Plugin.L.LogError($"[VF] Clone failed: {e.Message}"); continue; }

            try { clone.id = def.Id; } catch (Exception e) { Plugin.L.LogError($"[VF] set id: {e.Message}"); continue; }
            if (!string.IsNullOrEmpty(def.DisplayName))
                try { clone.label = def.DisplayName; } catch { }

            // Call RuntimeInit to create a unique runtimePrefab for this clone.
            // Without it, clone.runtimePrefab points to the same scene instance as baseBody.
            try { clone.RuntimeInit(); Plugin.L.LogInfo($"[VF] RuntimeInit OK on '{def.Id}'"); }
            catch (Exception e) { Plugin.L.LogWarning($"[VF] RuntimeInit failed: {e.Message}"); }

            // Patch hardpoints on the clone — this is safe because it's a new object.
            PatchHardpoints(clone, def);

            // RegisterItemType adds the clone to ItemDatabase.Bodies AND the ItemsById dict.
            // Do NOT also call bodies.Add(clone) — the duplicate entry crashes
            // SkinIconBaker.SpawnBodies (Dictionary.Add duplicate key), which kills the
            // SkinBaker coroutine mid-bake → pixelated skins, broken compass/aiming.
            try { Game.ItemDatabase.RegisterItemType(clone); } catch (Exception e) { Plugin.L.LogWarning($"[VF] RegisterItemType: {e.Message}"); }

            int occurrences = 0;
            for (int i = 0; i < bodies.Count; i++)
                try { if (bodies[i]?.Pointer == clone.Pointer) occurrences++; } catch { }
            if (occurrences == 0)
            {
                try { bodies.Add(clone); occurrences = 1; }
                catch (Exception e) { Plugin.L.LogError($"[VF] bodies.Add: {e.Message}"); continue; }
            }
            Plugin.L.LogInfo($"[VF] clone '{def.Id}' present in Bodies x{occurrences}");

            _defs[def.Id] = def;
            _clones.Add(clone);
            _bodyMap[def.Id] = (def, clone, baseBody);
            Plugin.L.LogInfo($"[VF] Created body type '{def.Id}' (clone of '{def.BaseBodyId}')");
        }
    }

    // Injects save items (Body list) and save configs (Cars list) for custom vehicles.
    public static void InjectSaveConfigs(Save.PlayerSaveData save)
    {
        if (_defs.Count == 0) return;
        try
        {
            InjectItems(save);
            InjectConfigs(save);
            InjectSuspensionItems(save);
        }
        catch (Exception e) { Plugin.L.LogError($"[VF] InjectSaveConfigs: {e.Message}"); }
    }

    // ── [DEBUG] Grants one item of each configured suspension to the player ──────
    // Grants original suspension IDs so the player can install them from either
    // the Cars-list or Body-list garage. The Body-list garage uses a PopulateSuspensions
    // patch that temporarily matches original suspensions to the custom body type pointer,
    // so no clone IDs are needed. Set GrantDebugSuspensions = false before release.
    static void InjectSuspensionItems(Save.PlayerSaveData save)
    {
        if (!GrantDebugSuspensions) return;

        var items = save.items;
        if (items == null) return;

        var existing = new HashSet<string>();
        for (int i = 0; i < items.Length; i++)
            try { var id = items[i]?.id; if (id != null) existing.Add(id); } catch { }

        var toAdd = new List<Save.ItemSaveData>();
        foreach (var def in _defs.Values)
        {
            if (def.AvailableSuspensions == null) continue;
            foreach (var suspId in def.AvailableSuspensions)
                if (existing.Add(suspId))
                    toAdd.Add(new Save.ItemSaveData { id = suspId });
        }

        if (toAdd.Count == 0) return;

        int oldLen = items.Length;
        var newArr = new Il2CppReferenceArray<Save.ItemSaveData>(oldLen + toAdd.Count);
        for (int i = 0; i < oldLen; i++) newArr[i] = items[i];
        for (int i = 0; i < toAdd.Count; i++) newArr[oldLen + i] = toAdd[i];
        save.items = newArr;

        foreach (var e in toAdd)
            Plugin.L.LogInfo($"[VF] [DEBUG] Granted suspension '{e.id}'");
    }
    // ─────────────────────────────────────────────────────────────────────────

    // Adds an ItemSaveData entry to save.items so the Body list shows the custom body.
    static void InjectItems(Save.PlayerSaveData save)
    {
        var items = save.items;
        if (items == null) { Plugin.L.LogWarning("[VF] save.items is null"); return; }

        var toAdd = new List<Save.ItemSaveData>();
        foreach (var def in _defs.Values)
        {
            bool found = false;
            for (int i = 0; i < items.Length; i++)
                try { if (items[i]?.id == def.Id) { found = true; break; } } catch { }
            if (found) continue;

            // Copy stats from the base body's item entry if present.
            Save.ItemSaveData? baseItem = null;
            for (int i = 0; i < items.Length; i++)
                try { if (items[i]?.id == def.BaseBodyId) { baseItem = items[i]; break; } } catch { }

            var entry = new Save.ItemSaveData();
            entry.id = def.Id;
            try { if (baseItem?.stats != null) entry.stats = baseItem.stats; } catch { }
            toAdd.Add(entry);
        }

        if (toAdd.Count == 0) return;

        int oldLen = items.Length;
        var newArr = new Il2CppReferenceArray<Save.ItemSaveData>(oldLen + toAdd.Count);
        for (int i = 0; i < oldLen; i++) newArr[i] = items[i];
        for (int i = 0; i < toAdd.Count; i++) newArr[oldLen + i] = toAdd[i];
        save.items = newArr;

        foreach (var e in toAdd)
            Plugin.L.LogInfo($"[VF] Injected body item '{e.id}' into save.items");
    }

    // Adds a VehicleConfigSaveData entry to save.configs (Cars list) with a license-plate
    // marker so the vehicle appears ready-to-drive without needing the Body list.
    static void InjectConfigs(Save.PlayerSaveData save)
    {
        var configs = save.configs;
        if (configs == null) return;

        var toAdd    = new List<Save.VehicleConfigSaveData>();
        var toRepair = new Dictionary<int, Save.VehicleConfigSaveData>();

        // Fallback: any config with non-null skin+engine (used when base body not owned yet).
        Save.VehicleConfigSaveData? anyConfig = null;
        for (int i = 0; i < configs.Length; i++)
            try { if (configs[i]?.skin != null && configs[i]?.engine != null) { anyConfig = configs[i]; break; } } catch { }
        if (anyConfig == null) try { anyConfig = save.config; } catch { }

        foreach (var def in _defs.Values)
        {
            Save.VehicleConfigSaveData? baseConfig = null;
            for (int i = 0; i < configs.Length; i++)
                try { if (configs[i]?.body?.id == def.BaseBodyId) { baseConfig = configs[i]; break; } } catch { }
            if (baseConfig == null)
                try { if (save.config?.body?.id == def.BaseBodyId) baseConfig = save.config; } catch { }
            // If still null (player never owned that base body), use any valid config for equipment.
            if (baseConfig == null) baseConfig = anyConfig;

            int existingIdx = -1;
            for (int i = 0; i < configs.Length; i++)
                try { if (configs[i]?.licensePlate == def.Id) { existingIdx = i; break; } } catch { }

            var fresh = BuildConfig(def, baseConfig);
            if (existingIdx >= 0) toRepair[existingIdx] = fresh;
            else toAdd.Add(fresh);
        }

        foreach (var kv in toRepair) { configs[kv.Key] = kv.Value; Plugin.L.LogInfo($"[VF] Repaired Cars slot {kv.Key}"); }

        if (toAdd.Count == 0) return;

        int oldLen = configs.Length;
        var newArr = new Il2CppReferenceArray<Save.VehicleConfigSaveData>(oldLen + toAdd.Count);
        for (int i = 0; i < oldLen; i++) newArr[i] = configs[i];
        for (int i = 0; i < toAdd.Count; i++) newArr[oldLen + i] = toAdd[i];
        save.configs = newArr;

        foreach (var cfg in toAdd)
            Plugin.L.LogInfo($"[VF] Injected Cars slot for '{cfg.licensePlate}'");
    }

    static Save.VehicleConfigSaveData BuildConfig(CustomVehicleDef def, Save.VehicleConfigSaveData? src,
                                                   bool useCloneId = false)
    {
        var cfg  = new Save.VehicleConfigSaveData();
        var body = new Save.ItemSaveData();
        // useCloneId=true tests whether RuntimeInit() fixed the Body list crash:
        // clone body id is used, triggering the same code path as garage-assembled vehicle.
        body.id = useCloneId ? def.Id : def.BaseBodyId;
        try { if (src?.body?.stats != null) body.stats = src.body.stats; } catch { }
        cfg.body = body;
        cfg.licensePlate = def.Id; // marker for identification
        try { if (src?.engine     != null) cfg.engine     = src.engine;     } catch { }
        try { if (src?.suspension != null) cfg.suspension = src.suspension; } catch { }
        try { if (src?.wheels     != null) cfg.wheels     = src.wheels;     } catch { }
        // Do NOT copy skin — VehicleConfigSaveData.GetSkin() resolves skin by player index,
        // which can be out of range if src comes from a different save context (e.g. NPC config
        // or our own repaired config from the previous session). FixNullSkin assigns at Awake.
        try { if (src?.bodyColor  != null) cfg.bodyColor  = src.bodyColor;  } catch { }
        try { if (src?.modules    != null) cfg.modules    = src.modules;    } catch { }
        try { if (src?.fireGroups != null) cfg.fireGroups = src.fireGroups; } catch { }
        return cfg;
    }

    // Swap vehicle's BodyItem to the base body's BodyItem before Vehicle.Start runs.
    // Start looks up BakeData by BodyType object reference — our clone has no bake data,
    // so we borrow the base vehicle's BodyItem for the duration of Start.
    public static Game.BodyItem? SwapToBaseBodyItem(Game.Vehicle vehicle)
    {
        try
        {
            var def = GetDefForVehicle(vehicle);
            if (def == null) return null;

            var currentBodyItem = vehicle.config?.body;
            if (currentBodyItem?.Type?.id == def.BaseBodyId) return null; // already base, no swap needed

            // Find another Vehicle in the scene that uses the base body type.
            var all = UnityEngine.Object.FindObjectsOfType<Game.Vehicle>();
            foreach (var v in all)
            {
                try
                {
                    if (v.Pointer == vehicle.Pointer) continue;
                    if (v.config?.body?.Type?.id == def.BaseBodyId)
                    {
                        vehicle.config.body = v.config.body;
                        Plugin.L.LogInfo($"[VF] SwapBody: borrowed BodyItem from vehicle with '{def.BaseBodyId}'");
                        return currentBodyItem;
                    }
                }
                catch { }
            }
            Plugin.L.LogWarning($"[VF] SwapBody: no live vehicle with base body '{def.BaseBodyId}' found");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[VF] SwapBody: {e.Message}"); }
        return null;
    }

    public static void RestoreBodyItem(Game.Vehicle vehicle, Game.BodyItem? saved)
    {
        if (saved == null) return;
        try { vehicle.config.body = saved; Plugin.L.LogInfo("[VF] SwapBody: restored"); }
        catch (Exception e) { Plugin.L.LogWarning($"[VF] RestoreBody: {e.Message}"); }
    }

    // Assigns a fallback skin to custom vehicles whose config.skin is null.
    // Must run before Vehicle.Start, which NPEs if skin is null when RefreshSkin is called.
    public static void FixNullSkin(Game.Vehicle vehicle)
    {
        try
        {
            var cfg = vehicle?.config;
            if (cfg == null || cfg.skin != null) return;
            var def = GetDefForVehicle(vehicle);
            if (def == null) return;

            string baseKey = def.BaseBodyId.Replace("body-", "");
            var skins = Game.ItemDatabase.Skins;
            for (int i = 0; i < skins.Count; i++)
            {
                try
                {
                    var s = skins[i];
                    if (s?.id?.Contains(baseKey) == true)
                    {
                        cfg.skin = s;
                        Plugin.L.LogInfo($"[VF] FixNullSkin: assigned '{s.id}'");
                        return;
                    }
                }
                catch { }
            }
            try
            {
                if (skins.Count > 0) { cfg.skin = skins[0]; Plugin.L.LogInfo($"[VF] FixNullSkin: assigned first skin '{skins[0]?.id}'"); }
            }
            catch { }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[VF] FixNullSkin: {e.Message}"); }
    }

    public static void PatchHardpoints(Game.BodyType bt, CustomVehicleDef def)
    {
        if (def.Hardpoints == null || def.Hardpoints.Length == 0) return;
        try
        {
            var hp = bt.hardpoints;
            if (hp == null) return;
            foreach (var patch in def.Hardpoints)
            {
                if (patch.Index >= hp.Length) continue;
                var h = hp[patch.Index];
                h.position = V3(patch.Position);
                hp[patch.Index] = h;
            }
            Plugin.L.LogInfo($"[VF] Patched {def.Hardpoints.Length} hardpoint(s) on '{bt.id}'");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[VF] PatchHardpoints: {e.Message}"); }
    }

    static Vector3 V3(float[] v) => v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
}
