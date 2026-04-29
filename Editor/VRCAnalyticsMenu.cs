using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Reflection;
using VRC.SDKBase;
using VRC.Udon;

public class VRCAnalyticsMenu : EditorWindow
{
    private string _key = "";
    private string _apiBase = DefaultApiBase;
    private bool   _focusDone;
    private bool   _showAdvanced;

    const string DefaultApiBase   = "https://api.vrcanalytics.com";
    const string ApiBasePrefKey   = "VRCAnalytics.ApiBase";
    internal const string SetupKeyPrefKey = "VRCAnalytics.SetupKey"; // editor-only, never embedded in builds
    const string LeftoverPrefab   = "Assets/VRCAnalytics/VRCAnalyticsBeacon.prefab";
    const string LeftoverAsmDef   = "Assets/VRCAnalytics/com.vrcanalytics.beacon.asset";

    // ── Menu entries ───────────────────────────────────────────────
    [MenuItem("VRCAnalytics/Enter Setup Key", false, 1)]
    static void OpenKeyWindow()
    {
        var win = GetWindow<VRCAnalyticsMenu>(true, "VRCAnalytics — Setup Key", true);
        // Compact when Advanced is collapsed; allow growth so the URL row fits when expanded.
        win.minSize = new Vector2(500, 200);
        win.maxSize = new Vector2(500, 320);

        win._key     = EditorPrefs.GetString(SetupKeyPrefKey, "");
        win._apiBase = EditorPrefs.GetString(ApiBasePrefKey, DefaultApiBase);
    }

    [MenuItem("VRCAnalytics/Add Zone Heatmap", false, 2)]
    static void AddZoneHeatmap()
    {
        var existing = FindZoneBeaconComponent();
        if (existing != null)
        {
            Selection.activeGameObject = existing.gameObject;
            EditorUtility.DisplayDialog("VRCAnalytics",
                "Zone Heatmap already exists in this scene — selected for you.\n\n" +
                "Drag zone GameObjects (each with a BoxCollider) into the Zones array, then Build & Upload.",
                "OK");
            return;
        }

        var script = LoadZoneBeaconScript();
        if (script == null)
        {
            EditorUtility.DisplayDialog("VRCAnalytics",
                "VRCAnalyticsZoneBeacon script not found. Try reimporting the package.", "OK");
            return;
        }

        var programAsset = FindZoneProgramAsset() ?? CreateProgramAssetFor(script,
            "VRCAnalyticsZoneBeacon", "d9a4b5c6e7f81920334455667788ab01");
        if (programAsset == null)
        {
            EditorUtility.DisplayDialog("VRCAnalytics",
                "Could not create Zone Beacon program asset. Try VRChat SDK → Utilities → Re-compile All Program Sources.",
                "OK");
            return;
        }

        var go = new GameObject("VRCAnalytics Zones");
        Undo.RegisterCreatedObjectUndo(go, "Add VRCAnalytics Zones");
        var ub = Undo.AddComponent<UdonBehaviour>(go);
        var so = new SerializedObject(ub);
        so.FindProperty("programSource").objectReferenceValue = programAsset;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(go);

        // Same UdonSharp proxy-creation dance as the main beacon.
        if (!TryResetSceneUpgradeFlag())
            UpgradeSceneBehavioursNow();

        Selection.activeGameObject = go;
        EditorUtility.DisplayDialog("VRCAnalytics",
            "Zone Heatmap added.\n\n" +
            "Next:\n" +
            "1. Create empty child GameObjects with BoxColliders\n" +
            "2. Name them (e.g. 'spawn', 'lobby', 'arena') — names become zone IDs\n" +
            "3. Drag each child into the Zones array on VRCAnalytics Zones\n" +
            "4. Build & Upload — URLs get baked per zone automatically",
            "Got it");
    }

    [MenuItem("VRCAnalytics/Documentation", false, 20)]
    static void OpenDocs() => Application.OpenURL("https://vrcanalytics.com/docs");

    // ── Window UI ──────────────────────────────────────────────────
    void OnGUI()
    {
        GUILayout.Space(14);
        EditorGUILayout.LabelField("Paste your setup key from the dashboard:", EditorStyles.boldLabel);
        GUILayout.Space(6);

        GUI.SetNextControlName("KeyField");
        _key = EditorGUILayout.TextField(_key, GUILayout.Height(24));
        if (!_focusDone) { EditorGUI.FocusTextInControl("KeyField"); _focusDone = true; }

        GUILayout.Space(12);

        // Advanced section — hidden by default. Most creators only ever paste a key
        // and never touch the API URL; we only expose it for self-hosters and dev
        // testing (e.g. cloudflared tunnels pointing at a local API).
        _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
        if (_showAdvanced)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("API base URL", EditorStyles.miniLabel);
            _apiBase = EditorGUILayout.TextField(_apiBase, GUILayout.Height(22));
            var hint = new GUIStyle(EditorStyles.miniLabel) {
                normal = { textColor = new Color(1, 1, 1, 0.4f) },
                wordWrap = true,
            };
            EditorGUILayout.LabelField(
                "Leave default unless you're self-hosting or testing against a tunnel.",
                hint);
            EditorGUI.indentLevel--;
            GUILayout.Space(6);
        }

        GUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save", GUILayout.Height(28)))
        {
            var k = _key.Trim();
            var baseUrl = string.IsNullOrWhiteSpace(_apiBase) ? DefaultApiBase : _apiBase.Trim().TrimEnd('/');
            EditorPrefs.SetString(SetupKeyPrefKey, k);
            EditorPrefs.SetString(ApiBasePrefKey, baseUrl);
            Close();
            ApplyKey(k, baseUrl);
        }
        if (GUILayout.Button("Cancel", GUILayout.Height(28))) Close();
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);
        var dim = new GUIStyle(EditorStyles.miniLabel) {
            normal = { textColor = new Color(1, 1, 1, 0.45f) },
            wordWrap = true,
        };
        EditorGUILayout.LabelField(
            "Stored locally in EditorPrefs only. Never embedded in your built world.\n" +
            "Used at build time to register your wrld_id with the API.",
            dim);
    }

    // ── Core logic ─────────────────────────────────────────────────
    static void ApplyKey(string key, string apiBase)
    {
        if (string.IsNullOrEmpty(key)) return;

        CleanupLeftovers();

        // If the program asset was deleted, any existing beacon UdonBehaviour in the scene
        // now points at nothing — nuke it so SpawnBeaconUdonBehaviour recreates everything.
        if (FindProgramAsset() == null)
        {
            var orphan = FindBeaconComponent();
            if (orphan != null)
            {
                Debug.LogWarning("[VRCAnalytics] Program asset missing — removing orphan beacon and re-spawning.");
                Undo.DestroyObjectImmediate(orphan.gameObject);
            }
            foreach (var ub in Object.FindObjectsByType<UdonBehaviour>(FindObjectsSortMode.None))
            {
                var prog = new SerializedObject(ub).FindProperty("programSource")?.objectReferenceValue;
                if (prog == null && ub.gameObject.name.StartsWith("VRCAnalytics"))
                    Undo.DestroyObjectImmediate(ub.gameObject);
            }
        }

        var beacon = FindBeaconComponent();
        if (beacon != null) { WriteKey(beacon, key, apiBase); return; }

        if (!SpawnBeaconUdonBehaviour()) return;

        // Reset U#'s _didSceneUpgrade so its own editor loop re-runs UpgradeSceneBehaviours
        // after the new program asset compiles — avoids the "outdated script" error that
        // occurs when we call UpgradeSceneBehaviours before compilation finishes.
        // If the flag can't be reset (API changed), fall back to calling it directly.
        if (!TryResetSceneUpgradeFlag())
            UpgradeSceneBehavioursNow();

        StartWaitForProxy(key, apiBase);
    }

    static string _pendingKey;
    static string _pendingApiBase;
    static double _waitStartTime;
    static EditorApplication.CallbackFunction _waitCallback;

    static void StartWaitForProxy(string key, string apiBase)
    {
        if (_waitCallback != null) EditorApplication.update -= _waitCallback;
        _pendingKey = key;
        _pendingApiBase = apiBase;
        _waitStartTime = EditorApplication.timeSinceStartup;
        _waitCallback = OnWaitTick;
        EditorApplication.update += _waitCallback;
    }

    static void OnWaitTick()
    {
        var beacon = FindBeaconComponent();
        if (beacon != null)
        {
            EditorApplication.update -= _waitCallback;
            _waitCallback = null;
            WriteKey(beacon, _pendingKey, _pendingApiBase);
            return;
        }

        if (EditorApplication.timeSinceStartup - _waitStartTime > 30.0)
        {
            EditorApplication.update -= _waitCallback;
            _waitCallback = null;
            EditorUtility.DisplayDialog("VRCAnalytics",
                "Timed out waiting for UdonSharp to initialize the beacon proxy.\n\n" +
                "Try: VRChat SDK → Utilities → Re-compile All Program Sources, then enter your key again.",
                "OK");
        }
    }

    // Resets UdonSharpEditorManager._didSceneUpgrade so U#'s OnEditorUpdate will
    // re-run UpgradeSceneBehaviours after compilation completes naturally.
    static bool TryResetSceneUpgradeFlag()
    {
        System.Type managerType = null;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            try { managerType = asm.GetType("UdonSharpEditor.UdonSharpEditorManager"); }
            catch { }
            if (managerType != null) break;
        }
        if (managerType == null) return false;

        var field = managerType.GetField("_didSceneUpgrade",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null) return false;

        field.SetValue(null, false);
        return true;
    }

    static void UpgradeSceneBehavioursNow()
    {
        System.Type utilType = null;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            try { utilType = asm.GetType("UdonSharpEditor.UdonSharpEditorUtility"); }
            catch { }
            if (utilType != null) break;
        }
        if (utilType == null) { Debug.LogWarning("[VRCAnalytics] UdonSharpEditorUtility not found via reflection"); return; }

        var method = utilType.GetMethod("UpgradeSceneBehaviours",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null) { Debug.LogWarning("[VRCAnalytics] UpgradeSceneBehaviours method not found"); return; }

        var behaviours = Object.FindObjectsByType<UdonBehaviour>(FindObjectsSortMode.None);
        method.Invoke(null, new object[] { behaviours });
    }

    static void CleanupLeftovers()
    {
        if (AssetDatabase.LoadMainAssetAtPath(LeftoverPrefab) != null)
        {
            AssetDatabase.DeleteAsset(LeftoverPrefab);
            Debug.Log("[VRCAnalytics] Removed stale prefab");
        }
        if (AssetDatabase.LoadMainAssetAtPath(LeftoverAsmDef) != null)
        {
            AssetDatabase.DeleteAsset(LeftoverAsmDef);
            Debug.Log("[VRCAnalytics] Removed old assembly definition asset");
        }
    }

    // ── Spawn UdonBehaviour ────────────────────────────────────────
    static bool SpawnBeaconUdonBehaviour()
    {
        var script = LoadBeaconScript();
        if (script == null)
        {
            EditorUtility.DisplayDialog("VRCAnalytics",
                "VRCAnalyticsBeacon script not found. Try reimporting the package.", "OK");
            return false;
        }

        var programAsset = FindProgramAsset();
        if (programAsset == null)
            programAsset = CreateProgramAsset(script);

        if (programAsset == null)
        {
            EditorUtility.DisplayDialog("VRCAnalytics",
                "Could not create UdonSharp program asset.\n\n" +
                "Please run: VRChat SDK → Utilities → Re-compile All Program Sources\n" +
                "Then try entering your key again.", "OK");
            return false;
        }

        var go = new GameObject("VRCAnalytics Beacon");
        Undo.RegisterCreatedObjectUndo(go, "Add VRCAnalytics Beacon");

        var ub = Undo.AddComponent<UdonBehaviour>(go);
        var so = new SerializedObject(ub);
        so.FindProperty("programSource").objectReferenceValue = programAsset;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(go);
        return true;
    }

    // ── Create UdonSharpProgramAsset if UdonSharp skipped it ───────
    static Object CreateProgramAsset(MonoScript script) =>
        CreateProgramAssetFor(script, "VRCAnalyticsBeacon", "b7d9e2f3a5c6478801234abcd5678ef9");

    // Generic: create an UdonSharpProgramAsset next to the given U# script, with a stable
    // .meta GUID so Unity doesn't clean it up between sessions. Used for the main beacon
    // and the zone beacon (and anything else we add later).
    static Object CreateProgramAssetFor(MonoScript script, string assetName, string programAssetGuid)
    {
        try
        {
            var scriptUnityPath = AssetDatabase.GetAssetPath(script);
            var scriptGuid      = AssetDatabase.AssetPathToGUID(scriptUnityPath);
            var assetUnityPath  = Path.ChangeExtension(scriptUnityPath, ".asset");

            var upaGuids = AssetDatabase.FindAssets("UdonSharpProgramAsset t:MonoScript");
            if (upaGuids.Length == 0)
            {
                Debug.LogError("[VRCAnalytics] Cannot locate UdonSharpProgramAsset script in project.");
                return null;
            }
            var upaGuid = upaGuids[0];

            var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(scriptUnityPath);
            string physicalPath;
            if (pkgInfo != null)
            {
                var relPart = scriptUnityPath.Substring(("Packages/" + pkgInfo.name).Length).TrimStart('/');
                physicalPath = Path.Combine(pkgInfo.resolvedPath, relPart);
            }
            else
            {
                physicalPath = Path.GetFullPath(scriptUnityPath);
            }
            physicalPath = Path.ChangeExtension(physicalPath, ".asset");

            Debug.Log($"[VRCAnalytics] Writing program asset to: {physicalPath}");

            var yaml = $"%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n  m_PrefabInstance: {{fileID: 0}}\n  m_PrefabAsset: {{fileID: 0}}\n  m_GameObject: {{fileID: 0}}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {{fileID: 11500000, guid: {upaGuid}, type: 3}}\n  m_Name: {assetName}\n  m_EditorClassIdentifier:\n  sourceCsScript: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}\n";

            File.WriteAllText(physicalPath, yaml);

            // Write a stable .meta alongside so the program asset keeps its GUID across
            // editor restarts. Without this Unity generates a random .meta on import,
            // and the asset is sometimes cleaned up entirely between sessions.
            var metaPath = physicalPath + ".meta";
            if (!File.Exists(metaPath))
            {
                var metaYaml =
                    "fileFormatVersion: 2\n" +
                    $"guid: {programAssetGuid}\n" +
                    "NativeFormatImporter:\n" +
                    "  externalObjects: {}\n" +
                    "  mainObjectFileID: 11400000\n" +
                    "  userData:\n" +
                    "  assetBundleName:\n" +
                    "  assetBundleVariant:\n";
                File.WriteAllText(metaPath, metaYaml);
            }
            AssetDatabase.ImportAsset(assetUnityPath, ImportAssetOptions.ForceSynchronousImport);

            var asset = AssetDatabase.LoadMainAssetAtPath(assetUnityPath);
            if (asset != null)
                Debug.Log($"[VRCAnalytics] Program asset created ({asset.GetType().Name})");
            return asset;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[VRCAnalytics] Failed to create program asset: {ex}");
            return null;
        }
    }

    // ── Confirm beacon is set up; URLs get baked at build time, not now ──
    static void WriteKey(Component beacon, string key, string apiBase)
    {
        // Setup key now lives in EditorPrefs (already saved by the OnGUI Save handler).
        // The build hook reads it at Build & Publish time, registers the wrld_id with
        // the API, and bakes per-world URLs that contain only ?w=wrld_xxx (no key).
        EditorUtility.SetDirty(beacon);
        Selection.activeGameObject = beacon.gameObject;
        EditorUtility.DisplayDialog("VRCAnalytics",
            $"Setup complete.\nAPI: {apiBase}\n\n" +
            "Your key is stored locally — never embedded in your built world.\n\n" +
            "When you click Build & Publish in VRChat SDK:\n" +
            "  1. Your world ID gets registered with the API\n" +
            "  2. Beacon URLs are baked with only your world ID, no secrets\n\n" +
            "Make sure \"Allow Untrusted URLs\" is ticked in your VRC Scene Descriptor, then publish your world.",
            "Got it");
    }

    internal static void SetVRCUrl(Component beacon, string fieldName, string urlValue)
    {
        var field = beacon.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field == null)
        {
            Debug.LogWarning($"[VRCAnalytics] Field '{fieldName}' not found on {beacon.GetType().Name}.");
            return;
        }
        field.SetValue(beacon, new VRCUrl(urlValue));
    }

    // ── Helpers ────────────────────────────────────────────────────
    static Object FindProgramAsset()
    {
        var scriptGuids = AssetDatabase.FindAssets("VRCAnalyticsBeacon t:MonoScript");
        if (scriptGuids.Length > 0)
        {
            var assetPath = Path.ChangeExtension(AssetDatabase.GUIDToAssetPath(scriptGuids[0]), ".asset");
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset != null) return asset;
        }
        var guids = AssetDatabase.FindAssets("VRCAnalyticsBeacon t:UdonSharpProgramAsset");
        if (guids.Length > 0)
            return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0]));
        return null;
    }

    static MonoScript LoadBeaconScript()
    {
        var guids = AssetDatabase.FindAssets("VRCAnalyticsBeacon t:MonoScript");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    static MonoScript LoadZoneBeaconScript()
    {
        var guids = AssetDatabase.FindAssets("VRCAnalyticsZoneBeacon t:MonoScript");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    static Object FindZoneProgramAsset()
    {
        var scriptGuids = AssetDatabase.FindAssets("VRCAnalyticsZoneBeacon t:MonoScript");
        if (scriptGuids.Length > 0)
        {
            var assetPath = Path.ChangeExtension(AssetDatabase.GUIDToAssetPath(scriptGuids[0]), ".asset");
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset != null) return asset;
        }
        var guids = AssetDatabase.FindAssets("VRCAnalyticsZoneBeacon t:UdonSharpProgramAsset");
        if (guids.Length > 0)
            return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0]));
        return null;
    }

    internal static Component FindZoneBeaconComponent()
    {
        var script = LoadZoneBeaconScript();
        if (script == null) return null;
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (MonoScript.FromMonoBehaviour(mb) == script)
                return mb;
        return null;
    }

    static Component FindBeaconComponent()
    {
        var script = LoadBeaconScript();
        if (script == null) return null;
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (MonoScript.FromMonoBehaviour(mb) == script)
                return mb;
        return null;
    }
}
