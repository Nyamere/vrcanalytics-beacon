using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.StringLoading;
using VRC.Udon.Common.Interfaces;

// ── VRCAnalytics Zone Beacon ───────────────────────────────────────
// Polls each zone collider ~12x/sec against the local player position.
// Fires enter/exit beacons with pre-baked per-zone URLs (Udon can't build
// URLs at runtime, so the build hook bakes one pair per zone).
//
// Workflow:
//   1. VRCAnalytics → Add Zone Heatmap creates the "VRCAnalytics Zones" GameObject
//   2. Create child empty GameObjects with BoxColliders (axis-aligned), name them
//   3. Drag each child into the Zones array below
//   4. Build & upload — our build hook bakes URLs and validates colliders
// ──────────────────────────────────────────────────────────────────

[AddComponentMenu("VRCAnalytics/Zone Beacon")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VRCAnalyticsZoneBeacon : UdonSharpBehaviour
{
    [Header("━━ VRCAnalytics Zones ━━━━━━━━━━━━━━━━━")]
    [Tooltip("Drop zone GameObjects (each with a BoxCollider) here. GameObject names become zone IDs.")]
    public GameObject[] zones;

    // Parallel arrays populated by the build hook. Hidden because users should not edit them.
    [HideInInspector] public string[] _zoneNames;
    [HideInInspector] public VRCUrl[] _zoneEnterUrls;
    [HideInInspector] public VRCUrl[] _zoneExitUrls;

    // Runtime state — local player's most recent inside/outside status per zone.
    private bool[] _inside;
    private int    _tickCounter;

    void Start()
    {
        if (zones == null) return;
        _inside = new bool[zones.Length];
    }

    void Update()
    {
        // Poll at ~12 Hz — plenty fast for zone transitions, negligible cost.
        _tickCounter++;
        if (_tickCounter < 5) return;
        _tickCounter = 0;

        if (zones == null || _inside == null) return;

        VRCPlayerApi lp = Networking.LocalPlayer;
        if (lp == null) return;
        Vector3 pos = lp.GetPosition();

        for (int i = 0; i < zones.Length; i++)
        {
            if (zones[i] == null) continue;
            Collider c = zones[i].GetComponent<Collider>();
            if (c == null) continue;

            bool nowInside = c.bounds.Contains(pos);

            if (nowInside && !_inside[i])
            {
                _inside[i] = true;
                if (_zoneEnterUrls != null && i < _zoneEnterUrls.Length && _zoneEnterUrls[i] != null)
                    VRCStringDownloader.LoadUrl(_zoneEnterUrls[i], (IUdonEventReceiver)this);
            }
            else if (!nowInside && _inside[i])
            {
                _inside[i] = false;
                if (_zoneExitUrls != null && i < _zoneExitUrls.Length && _zoneExitUrls[i] != null)
                    VRCStringDownloader.LoadUrl(_zoneExitUrls[i], (IUdonEventReceiver)this);
            }
        }
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result) { }
    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogWarning("[VRCAnalytics] Zone beacon request failed (" + result.ErrorCode + ").");
    }
}
