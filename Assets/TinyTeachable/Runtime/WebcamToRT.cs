using UnityEngine;
using PassthroughCameraSamples; // for WebCamTextureManager

[DisallowMultipleComponent]
public class WebcamToRT : MonoBehaviour
{
    [Header("Output")]
    public RenderTexture target;            // assign if you want; will auto-create if null
    public int outputWidth  = 224;          // fallback size if target is null
    public int outputHeight = 224;

    [Header("Mode")]
    public bool IsPassthrough = false;

    [Header("WebCam (Editor/Desktop)")]
    public int webcamWidth  = 640;
    public int webcamHeight = 480;
    public int webcamFPS    = 30;

    [Header("Passthrough (Quest)")]
    public WebCamTextureManager webCamTextureManager; // assign when using passthrough

    private WebCamTexture webcamTex;
    private Texture passthroughTex;         // cached reference to manager’s texture
    private bool warnedNoManager = false;
    private bool warnedNoPTex    = false;

    void Start()
    {
        ApplyMode(IsPassthrough);
        EnsureTargetRT();
    }

    void OnValidate()
    {
        // Keep sane sizes
        outputWidth  = Mathf.Max(8, outputWidth);
        outputHeight = Mathf.Max(8, outputHeight);
    }

    void OnDisable()
    {
        StopWebcamIfRunning();
    }

    void OnDestroy()
    {
        StopWebcamIfRunning();
    }

    /// <summary>Switch at runtime (wire to a toggle if you want)</summary>
    public void ApplyMode(bool usePassthrough)
    {
        IsPassthrough = usePassthrough;

        if (IsPassthrough)
        {
            // Moving to passthrough: stop regular webcam to avoid conflicts
            StopWebcamIfRunning();
            warnedNoManager = warnedNoPTex = false; // reset warnings
        }
        else
        {
            // Moving to webcam: spin it up if needed
            if (webcamTex == null)
            {
                webcamTex = new WebCamTexture(webcamWidth, webcamHeight, webcamFPS);
                webcamTex.filterMode = FilterMode.Bilinear;
                webcamTex.wrapMode = TextureWrapMode.Clamp;
            }
            if (!webcamTex.isPlaying) webcamTex.Play();
        }
    }

    void Update()
    {
        EnsureTargetRT();

        if (IsPassthrough)
        {
            // 1) Check manager
            if (webCamTextureManager == null)
            {
                if (!warnedNoManager)
                {
                    Debug.LogWarning("[WebcamToRT] Passthrough mode is ON but WebCamTextureManager is NULL. Assign it in the inspector.");
                    warnedNoManager = true;
                }
                return;
            }

            // 2) Get texture (may be null for a few frames until initialized)
            passthroughTex = webCamTextureManager.WebCamTexture;
            if (passthroughTex == null)
            {
                if (!warnedNoPTex)
                {
                    Debug.Log("[WebcamToRT] Waiting for passthrough texture to become ready…");
                    warnedNoPTex = true;
                }
                return;
            }

            // 3) Blit passthrough -> target
            SafeBlit(passthroughTex, target);
        }
        else
        {
            // Regular webcam path
            if (webcamTex == null)
            {
                webcamTex = new WebCamTexture(webcamWidth, webcamHeight, webcamFPS);
                webcamTex.filterMode = FilterMode.Bilinear;
                webcamTex.wrapMode = TextureWrapMode.Clamp;
                webcamTex.Play();
            }

            if (webcamTex.didUpdateThisFrame)
                SafeBlit(webcamTex, target);
        }
    }

    // --- Helpers -------------------------------------------------------------

    void EnsureTargetRT()
    {
        if (target != null) return;

        target = new RenderTexture(outputWidth, outputHeight, 0, RenderTextureFormat.ARGB32);
        target.useMipMap = false;
        target.autoGenerateMips = false;
        target.wrapMode = TextureWrapMode.Clamp;
        target.filterMode = FilterMode.Bilinear;
        target.Create();

        Debug.Log($"[WebcamToRT] Auto-created target RT {outputWidth}x{outputHeight}");
    }

    void SafeBlit(Texture src, RenderTexture dst)
    {
        if (src == null || dst == null) return;

        // Handle dynamic source sizes by setting viewport from the destination RT only.
        // Graphics.Blit will scale to fit the RT.
        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(src, dst);
        RenderTexture.active = prev;
    }

    void StopWebcamIfRunning()
    {
        if (webcamTex != null && webcamTex.isPlaying)
        {
            webcamTex.Stop();
            // do not Destroy: user might switch back
        }
    }
}
