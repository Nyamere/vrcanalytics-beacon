using System.Linq;
using System.Reflection;
using System.Text;
using System.Net.Http;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using VRC.Core;
using VRC.SDKBase;
using VRC.SDKBase.Editor.BuildPipeline;

// Runs just before VRChat builds the world's asset bundle. Reads the scene's
// PipelineManager.blueprintId, calls /v1/register-build to bind it to the user's
// account, then bakes the beacon URLs containing only ?w=wrld_xxx — the setup key
// is never embedded in the world.
public class VRCAnalyticsBuildHook : IVRCSDKBuildRequestedCallback
{
    // Run late so URL patches land after UdonSharpBuildCompile (order 100).
    public int callbackOrder => 1000;

    const string DefaultApiBase   = "https://api.vrcanalytics.com";
    const string ApiBasePrefKey   = "VRCAnalytics.ApiBase";
    const string SetupKeyPrefKey  = "VRCAnalytics.SetupKey";

    static readonly HttpClient HttpClient = new HttpClient();

    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType != VRCSDKRequestedBuildType.Scene) return true;

        var beacon = FindBeaconComponent();
        if (beacon == null) return true; // no beacon in scene, nothing to bake

        var key = EditorPrefs.GetString(SetupKeyPrefKey, "");
        if (string.IsNullOrEmpty(key))
        {
            EditorUtility.DisplayDialog("VRCAnalytics",
                "No setup key found. Open VRCAnalytics → Enter Setup Key and paste your dashboard key first.",
                "OK");
            return false; // abort the build — they need to set up first
        }

        var pm = Object.FindObjectsByType<PipelineManager>(FindObjectsSortMode.None).FirstOrDefault();
        string blueprintId = pm != null ? pm.blueprintId : null;

        if (string.IsNullOrEmpty(blueprintId))
        {
            Debug.LogWarning("[VRCAnalytics] PipelineManager has no blueprintId yet — first-time upload will run without world binding. Re-upload after VRChat assigns an ID.");
            // Don't bake URLs without a wrld_id — they'd be useless. Continue the build silently.
            return true;
        }

        var apiBase = EditorPrefs.GetString(ApiBasePrefKey, DefaultApiBase);

        // Register the build with the API. Server binds wrld_id ↔ key (first time)
        // or verifies they match (subsequent builds). 409 = key bound to a different world.
        var regResult = TryRegisterBuild(apiBase, key, blueprintId);
        if (!regResult.ok)
        {
            EditorUtility.DisplayDialog("VRCAnalytics",
                $"Build registration failed:\n\n{regResult.message}\n\n" +
                "Check your setup key in VRCAnalytics → Enter Setup Key and your dashboard.",
                "OK");
            return false;
        }
        Debug.Log($"[VRCAnalytics] Build registered: world={blueprintId} ({regResult.message})");

        // Bake URL arrays. Udon can't build URLs at runtime, so we pre-compute every
        // (platform × bucket) combo we might want to fire.
        var platforms = new string[] { "pcvr", "android", "desktop" };

        // Join URLs: 3 platforms × 5 instance-size buckets = 15 URLs.
        // `n` param carries a representative player count that lands cleanly in the
        // server-side bucket query (<=4, <=8, <=16, <=32, else 33+).
        var nBucketValues = new string[] { "4", "8", "16", "32", "40" };
        var joinUrls = new VRCUrl[15];
        for (int p = 0; p < platforms.Length; p++)
        {
            for (int b = 0; b < nBucketValues.Length; b++)
            {
                var url = $"{apiBase}/v1/beacon?w={blueprintId}&e=join&p={platforms[p]}&n={nBucketValues[b]}";
                joinUrls[p * 5 + b] = new VRCUrl(url);
            }
        }
        SetField(beacon, "_joinUrls", joinUrls);

        // Leave URLs: one per platform.
        var leaveUrls = new VRCUrl[platforms.Length];
        for (int p = 0; p < platforms.Length; p++)
            leaveUrls[p] = new VRCUrl($"{apiBase}/v1/beacon?w={blueprintId}&e=leave&p={platforms[p]}");
        SetField(beacon, "_leaveUrls", leaveUrls);

        // FPS URLs: 3 platforms × 8 FPS buckets = 24 URLs.
        // Values are bucket midpoints so AVG(fps_avg) server-side stays meaningful.
        var fpsMidpoints = new string[] { "10", "25", "37.5", "52.5", "67.5", "82.5", "105", "130" };
        var fpsUrls = new VRCUrl[24];
        for (int p = 0; p < platforms.Length; p++)
        {
            for (int b = 0; b < fpsMidpoints.Length; b++)
            {
                var url = $"{apiBase}/v1/beacon?w={blueprintId}&e=fps&p={platforms[p]}&fps={fpsMidpoints[b]}";
                fpsUrls[p * 8 + b] = new VRCUrl(url);
            }
        }
        SetField(beacon, "_fpsUrls", fpsUrls);

        // Sync proxy fields → UdonBehaviour heap so the baked URLs survive the build.
        CopyProxyToUdon(beacon);

        EditorUtility.SetDirty(beacon);
        EditorSceneManager.MarkSceneDirty(beacon.gameObject.scene);

        Debug.Log($"[VRCAnalytics] Baked {joinUrls.Length + leaveUrls.Length + fpsUrls.Length} beacon URLs for world={blueprintId}.");

        // Zone heatmap: bake per-zone URLs if a Zone Beacon is present.
        BakeZoneUrls(apiBase, blueprintId);

        return true;
    }

    // Synchronously POSTs to /v1/register-build. Returns ok=true on 2xx.
    static (bool ok, string message) TryRegisterBuild(string apiBase, string key, string blueprintId)
    {
        try
        {
            var json = $"{{\"key\":\"{key}\",\"vrchat_world_id\":\"{blueprintId}\"}}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var task = HttpClient.PostAsync($"{apiBase}/v1/register-build", content);
            task.Wait(15_000); // 15s timeout — registration is fast in normal cases
            var res = task.Result;
            var body = res.Content.ReadAsStringAsync().Result;
            if (res.IsSuccessStatusCode) return (true, body);
            return (false, $"HTTP {(int)res.StatusCode}: {body}");
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }

    static void BakeZoneUrls(string apiBase, string blueprintId)
    {
        var zoneBeacon = VRCAnalyticsMenu.FindZoneBeaconComponent();
        if (zoneBeacon == null) return;

        var zonesField = zoneBeacon.GetType().GetField("zones", BindingFlags.Public | BindingFlags.Instance);
        var zones = zonesField?.GetValue(zoneBeacon) as GameObject[];
        if (zones == null || zones.Length == 0)
        {
            Debug.LogWarning("[VRCAnalytics] Zone Beacon is in scene but has no zones configured — skipping.");
            return;
        }

        // Filter valid zones (non-null, has a collider), derive unique zone names from GameObject names.
        var validZones = new System.Collections.Generic.List<GameObject>();
        var names      = new System.Collections.Generic.List<string>();
        var seenNames  = new System.Collections.Generic.HashSet<string>();
        foreach (var go in zones)
        {
            if (go == null) continue;
            var col = go.GetComponent<Collider>();
            if (col == null)
            {
                Debug.LogWarning($"[VRCAnalytics] Zone '{go.name}' has no Collider — skipped. Add a BoxCollider.");
                continue;
            }
            if (!col.isTrigger)
            {
                col.isTrigger = true; // polling doesn't strictly need this, but matches user expectation
                EditorUtility.SetDirty(col);
            }
            var name = go.name;
            if (seenNames.Contains(name))
            {
                Debug.LogWarning($"[VRCAnalytics] Duplicate zone name '{name}' — skipped. Zone names must be unique.");
                continue;
            }
            seenNames.Add(name);
            validZones.Add(go);
            names.Add(name);
        }

        if (validZones.Count == 0)
        {
            Debug.LogWarning("[VRCAnalytics] No valid zones to bake.");
            return;
        }

        var enterUrls = new VRCUrl[validZones.Count];
        var exitUrls  = new VRCUrl[validZones.Count];
        for (int i = 0; i < validZones.Count; i++)
        {
            var zName = System.Uri.EscapeDataString(names[i]);
            // Keyless: only wrld_id and zone name in the URL.
            var baseUrl = $"{apiBase}/v1/beacon?w={blueprintId}&z={zName}";
            enterUrls[i] = new VRCUrl(baseUrl + "&e=zone_enter");
            exitUrls[i]  = new VRCUrl(baseUrl + "&e=zone_exit");
        }

        // Write the derived arrays onto the proxy via reflection.
        SetField(zoneBeacon, "zones",           validZones.ToArray());
        SetField(zoneBeacon, "_zoneNames",      names.ToArray());
        SetField(zoneBeacon, "_zoneEnterUrls",  enterUrls);
        SetField(zoneBeacon, "_zoneExitUrls",   exitUrls);

        CopyProxyToUdon(zoneBeacon);
        EditorUtility.SetDirty(zoneBeacon);
        EditorSceneManager.MarkSceneDirty(zoneBeacon.gameObject.scene);

        Debug.Log($"[VRCAnalytics] Baked {validZones.Count} zone(s): {string.Join(", ", names)}");
    }

    static void SetField(Component target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field == null)
        {
            Debug.LogWarning($"[VRCAnalytics] Field '{fieldName}' not found on {target.GetType().Name}.");
            return;
        }
        field.SetValue(target, value);
    }

    static Component FindBeaconComponent()
    {
        var scriptGuids = AssetDatabase.FindAssets("VRCAnalyticsBeacon t:MonoScript");
        if (scriptGuids.Length == 0) return null;
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(
            AssetDatabase.GUIDToAssetPath(scriptGuids[0]));
        if (script == null) return null;

        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (MonoScript.FromMonoBehaviour(mb) == script)
                return mb;
        return null;
    }

    static void SetVRCUrl(Component beacon, string fieldName, string urlValue)
    {
        var field = beacon.GetType().GetField(fieldName,
            BindingFlags.Public | BindingFlags.Instance);
        if (field == null) return;
        field.SetValue(beacon, new VRCUrl(urlValue));
    }

    static void CopyProxyToUdon(Component beacon)
    {
        System.Type utilType = null;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            try { utilType = asm.GetType("UdonSharpEditor.UdonSharpEditorUtility"); }
            catch { }
            if (utilType != null) break;
        }
        if (utilType == null) return;

        var method = utilType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CopyProxyToUdon" && m.GetParameters().Length == 1);
        method?.Invoke(null, new object[] { beacon });
    }
}
