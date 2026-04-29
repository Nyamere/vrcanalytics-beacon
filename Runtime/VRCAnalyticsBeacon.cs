using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.StringLoading;
using VRC.Udon.Common.Interfaces;

// ── VRCAnalytics Beacon ────────────────────────────────────────────
// Fires pre-baked beacon URLs to the VRCAnalytics API. Udon's VM can't construct
// VRCUrls at runtime, so every URL a world sends has to be baked at build time.
// We pre-bake arrays covering every platform × (instance-size | fps-bucket) combo,
// plus one URL per supported system language.
//
// Index layout:
//   _joinUrls[platformIdx * 5 + nBucket]    — 15 URLs (3 plat × 5 size buckets)
//   _leaveUrls[platformIdx]                 —  3 URLs
//   _fpsUrls[platformIdx * 8 + fpsBucket]   — 24 URLs (3 plat × 8 fps buckets)
// ──────────────────────────────────────────────────────────────────

[AddComponentMenu("VRCAnalytics/Beacon")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VRCAnalyticsBeacon : UdonSharpBehaviour
{
    [Header("━━ VRCAnalytics Setup ━━━━━━━━━━━━━━━━━")]
    [Tooltip("Legacy field — kept for scene-file back-compat. The dashboard key lives in EditorPrefs now.")]
    public string worldKey = "";

    // Arrays baked by VRCAnalyticsBuildHook. Users should not edit these directly.
    [HideInInspector] public VRCUrl[] _joinUrls;
    [HideInInspector] public VRCUrl[] _leaveUrls;
    [HideInInspector] public VRCUrl[] _fpsUrls;

    // ── runtime state ──────────────────────────────────────────────
    private int   _platformIdx;
    private bool  _leaveSent;
    private bool  _ready;

    private float _fpsAccum;
    private int   _fpsFrames;
    private float _fpsWindowStart;
    private const float FpsWindowSec = 60f;

    // ── lifecycle ──────────────────────────────────────────────────
    void Start()
    {
        // Sanity check: if the build hook didn't run, there's nothing to do.
        if (_joinUrls == null || _joinUrls.Length == 0)
        {
            Debug.LogWarning("[VRCAnalytics] Beacon URLs not baked — Build & Publish via VRChat SDK to set them up.");
            return;
        }

        VRCPlayerApi local = Networking.LocalPlayer;
        if (local == null) return;

        _platformIdx = GetPlatformIndex(local);

        // Fire join with instance-size bucket based on current concurrent player count.
        int nBucket = GetNBucket(VRCPlayerApi.GetPlayerCount());
        int joinIdx = _platformIdx * 5 + nBucket;
        if (joinIdx >= 0 && joinIdx < _joinUrls.Length && _joinUrls[joinIdx] != null)
            VRCStringDownloader.LoadUrl(_joinUrls[joinIdx], (IUdonEventReceiver)this);

        _fpsWindowStart = Time.unscaledTime;
        _ready = true;
    }

    void Update()
    {
        if (!_ready) return;

        // Accumulate unscaled frame time so rendering pauses (e.g. menu) don't skew FPS.
        _fpsAccum += Time.unscaledDeltaTime;
        _fpsFrames++;

        float elapsed = Time.unscaledTime - _fpsWindowStart;
        if (elapsed >= FpsWindowSec && _fpsFrames > 0 && _fpsAccum > 0f)
        {
            float avgFps = _fpsFrames / _fpsAccum;
            int fpsBucket = GetFpsBucket(avgFps);
            int fpsIdx = _platformIdx * 8 + fpsBucket;

            if (_fpsUrls != null && fpsIdx >= 0 && fpsIdx < _fpsUrls.Length && _fpsUrls[fpsIdx] != null)
                VRCStringDownloader.LoadUrl(_fpsUrls[fpsIdx], (IUdonEventReceiver)this);

            _fpsWindowStart = Time.unscaledTime;
            _fpsAccum = 0f;
            _fpsFrames = 0;
        }
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (!_ready || !player.isLocal || _leaveSent) return;
        _leaveSent = true;
        if (_leaveUrls != null && _platformIdx < _leaveUrls.Length && _leaveUrls[_platformIdx] != null)
            VRCStringDownloader.LoadUrl(_leaveUrls[_platformIdx], (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result) { }
    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogWarning("[VRCAnalytics] Beacon request failed (" + result.ErrorCode + ")");
    }

    // ── bucket helpers (kept as if/else — UdonSharp's switch support is finicky) ──
    private int GetPlatformIndex(VRCPlayerApi p)
    {
        if (p.IsUserInVR()) return 0; // pcvr
#if UNITY_ANDROID
        return 1; // android (Quest)
#else
        return 2; // desktop
#endif
    }

    private int GetNBucket(int playerCount)
    {
        if (playerCount <= 4)  return 0;
        if (playerCount <= 8)  return 1;
        if (playerCount <= 16) return 2;
        if (playerCount <= 32) return 3;
        return 4;
    }

    private int GetFpsBucket(float fps)
    {
        if (fps < 20f)  return 0;
        if (fps < 30f)  return 1;
        if (fps < 45f)  return 2;
        if (fps < 60f)  return 3;
        if (fps < 75f)  return 4;
        if (fps < 90f)  return 5;
        if (fps < 120f) return 6;
        return 7;
    }
}
