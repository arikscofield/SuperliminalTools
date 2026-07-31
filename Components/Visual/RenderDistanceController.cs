using UnityEngine;
using UnityEngine.SceneManagement;

namespace SuperliminalTools.Components.Visual;

class RenderDistanceController : MonoBehaviour
{
#if LEGACY
    public RenderDistanceController(System.IntPtr ptr) : base(ptr) { }
#endif
    public static RenderDistanceController Instance { get; private set; }

    public float RenderDistance { get; private set; }
    public bool DisableRendering {  get; private set; }
    
    // Display-only settings to change during high-speed speedups
    private ShadowQuality _savedShadows;
    private float _savedShadowDistance;
    private int _savedPixelLights;
    private int _savedAntiAliasing;
    private bool _saved;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            Debug.LogError("Duplicate RenderDistanceController");
            return;
        }

        Instance = this;
        RenderDistance = 1000f;
        DisableRendering = false;

#if LEGACY
        SceneManager.sceneLoaded += (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)OnSceneLoad;
#else
        SceneManager.sceneLoaded += OnSceneLoad;
#endif  
    }

    private void OnSceneLoad(Scene scene, LoadSceneMode loadMode)
    {
        SetRendering(!DisableRendering);
    }

    public void SetRenderDistance(float distance)
    {
        RenderDistance = distance;
        ApplyRenderDistance(RenderDistance);
    }

    public void SetRendering(bool render)
    {
        if (render)
        {
            DisableRendering = false;
            ApplyRenderDistance(RenderDistance);
        }
        else
        {
            DisableRendering = true;
            ApplyRenderDistance(.1f);
        }
        
        ApplyFastForwardRendering(!render);
    }

    private void ApplyRenderDistance(float distance)
    {
        var playerCamera = GameManager.GM.playerCamera;
        if (playerCamera == null) return;

        playerCamera.GetComponent<CameraSettingsLayer>().enabled = false;

        playerCamera.farClipPlane = distance;

        if (distance > 1000)
        {
            playerCamera.clearFlags = CameraClearFlags.SolidColor;
            playerCamera.backgroundColor = new Color(.15f, .15f, .15f, 1f);

            playerCamera.cullingMatrix = new(Vector4.positiveInfinity,
                Vector4.positiveInfinity,
                Vector4.positiveInfinity,
                Vector4.positiveInfinity);
        }
        else if (distance < 1)
        {
            playerCamera.clearFlags = CameraClearFlags.SolidColor;
            playerCamera.backgroundColor = new Color(.15f, .15f, .15f, 1f);

            playerCamera.cullingMatrix = new(Vector4.zero,
                Vector4.zero,
                Vector4.zero,
                Vector4.zero);
        }
        else
        {
            playerCamera.clearFlags = CameraClearFlags.Skybox;
            playerCamera.ResetCullingMatrix();
        }
    }
    
    
    // Screen-space-overlay canvases draw straight to the backbuffer, after and
    // independent of every camera. With both game cameras disabled nothing clears
    // that buffer, so the crosshair, subtitles, save icon and our own HUD composite
    // on top of whatever the last rendered frame left behind and smear -- and
    // alternate between two stale buffers, which is the flicker. This camera draws
    // nothing and exists purely to issue the clear.
    private Camera _clearCam;

    private void EnsureClearCamera()
    {
        if (_clearCam != null) return;

        var go = new GameObject("SuperliminalTools_FastForwardClear");
        go.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(go);

        _clearCam = go.AddComponent<Camera>();
        _clearCam.cullingMask = 0;                 // render nothing
        _clearCam.clearFlags = CameraClearFlags.SolidColor;
        _clearCam.backgroundColor = new Color(.15f, .15f, .15f, 1f);
        _clearCam.depth = -100;                    // clear before anything else
        _clearCam.farClipPlane = 1f;
        _clearCam.useOcclusionCulling = false;
        _clearCam.allowHDR = false;
        _clearCam.allowMSAA = false;
        _clearCam.enabled = false;

        // Left untagged on purpose: Camera.main resolves by the MainCamera tag, so
        // anything in the game reaching for it is unaffected.
    }

    private void ApplyFastForwardRendering(bool ff)
    {
        var cam = GameManager.GM != null ? GameManager.GM.playerCamera : null;
        if (cam != null) cam.enabled = !ff;

        var guiCam = GameManager.GM != null ? GameManager.GM.guiCamera : null;
        if (guiCam != null) guiCam.enabled = !ff;

        EnsureClearCamera();
        if (_clearCam != null) _clearCam.enabled = ff;

        if (ff)
        {
            if (!_saved)
            {
                _savedShadows        = QualitySettings.shadows;
                _savedShadowDistance = QualitySettings.shadowDistance;
                _savedPixelLights    = QualitySettings.pixelLightCount;
                _savedAntiAliasing   = QualitySettings.antiAliasing;
                _saved = true;
            }
            QualitySettings.shadows        = ShadowQuality.Disable;
            QualitySettings.shadowDistance = 0f;
            QualitySettings.pixelLightCount = 0;
            QualitySettings.antiAliasing   = 0;
        }
        else if (_saved)
        {
            QualitySettings.shadows        = _savedShadows;
            QualitySettings.shadowDistance = _savedShadowDistance;
            QualitySettings.pixelLightCount = _savedPixelLights;
            QualitySettings.antiAliasing   = _savedAntiAliasing;
            _saved = false;
        }
        
    }
}
