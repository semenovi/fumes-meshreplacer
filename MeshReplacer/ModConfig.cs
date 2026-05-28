using System;
using System.IO;
using System.Text.Json;

public class MeshEntry
{
    public string   Bundle           { get; set; } = "";
    public string[] Candidates       { get; set; } = Array.Empty<string>();
    public string   Target           { get; set; } = "";
    public bool     IsBody           { get; set; }
    public bool     FixMaterialSlots { get; set; }
}

public class LampConfig
{
    public bool    Front { get; set; }
    public float[] Bulb  { get; set; } = new float[3];
}

public class ShaftConfig
{
    public float[]? Position { get; set; }
    public float[]? Scale    { get; set; }
}

public class HardpointPatch
{
    public int     Index    { get; set; }
    public float[] Position { get; set; } = new float[3];
}

public class VehicleBodyConfig
{
    public float[]?     GrillPosition         { get; set; }
    public float[]?     InteriorCameraPosition { get; set; }
    public float[]?     EnginePosition         { get; set; }
    public float[]?      FrontLampsColor         { get; set; }
    public float[][]?    CargoStrapsHooks        { get; set; }
    public LampConfig[]? Lamps                   { get; set; }
    public float[]?      FrontLampsLightPosition { get; set; }
    public ShaftConfig[]? FrontLampsShafts       { get; set; }
    public string?       RoofLampPartGo          { get; set; }
    public int[]?        RoofLampIndices         { get; set; }
    public string?       FrontLampBulbGoName     { get; set; }
    public float[]?      FrontLampBulbPosition   { get; set; }
    public float[]?      FrontLampBulbScale      { get; set; }
}

public class CustomVehicleDef
{
    public string             Id               { get; set; } = "";
    public string             BaseBodyId       { get; set; } = "";
    public string             DisplayName      { get; set; } = "";
    public string             VehicleMarker    { get; set; } = "CaroModel";
    public MeshEntry[]        MeshReplacements { get; set; } = Array.Empty<MeshEntry>();
    public VehicleBodyConfig? VehicleBody      { get; set; }
    public float[]?           AntennaPosition  { get; set; }
    public HardpointPatch[]?  Hardpoints       { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FolderPath { get; set; } = "";
}

public class ModConfig
{
    public CustomVehicleDef[] CustomVehicles { get; set; } = Array.Empty<CustomVehicleDef>();

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    public static ModConfig Load(string pluginDir)
    {
        var modDir = Path.Combine(pluginDir, "meshreplacer");
        if (!Directory.Exists(modDir))
        {
            Plugin.L.LogWarning($"[CFG] Mod directory not found: {modDir}");
            return new ModConfig();
        }

        var defs = new System.Collections.Generic.List<CustomVehicleDef>();
        foreach (var subdir in Directory.GetDirectories(modDir))
        {
            var vehicleJson = Path.Combine(subdir, "vehicle.json");
            if (!File.Exists(vehicleJson)) continue;
            try
            {
                var def = JsonSerializer.Deserialize<CustomVehicleDef>(File.ReadAllText(vehicleJson), Opts);
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                def.FolderPath = subdir;
                defs.Add(def);
                Plugin.L.LogInfo($"[CFG] Loaded vehicle '{def.Id}' from '{Path.GetFileName(subdir)}'");
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"[CFG] Failed to load '{vehicleJson}': {e.Message}");
            }
        }

        var cfg = new ModConfig { CustomVehicles = defs.ToArray() };
        Plugin.L.LogInfo($"[CFG] Loaded {cfg.CustomVehicles.Length} custom vehicle(s)");
        return cfg;
    }
}
