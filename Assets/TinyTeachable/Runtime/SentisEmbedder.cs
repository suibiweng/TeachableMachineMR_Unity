using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.InferenceEngine; // 2.2 package

/// <summary>
/// GPU-friendly image embedder for Unity Inference Engine 2.2 (ex-Sentis).
/// - Scales any Texture/RT to square inputSize
/// - Packs to NCHW by default (toggle NHWC via useNHWC)
/// - Returns embedding as float[]
/// - Prewarms on Start() to avoid first-click hiccup
/// </summary>
[DisallowMultipleComponent]
public class SentisEmbedder : MonoBehaviour, IDisposable
{
    [Header("Model")]
    public ModelAsset  modelAsset;
    public BackendType backend    = BackendType.GPUCompute;
    [Tooltip("Model input tensor name")]
    public string      inputName  = "input";
    [Tooltip("Model output tensor name (embedding)")]
    public string      outputName = "embedding";
    [Tooltip("Square input size (pixels)")]
    public int         inputSize  = 224;
    [Tooltip("If true, packs [1,H,W,C] (NHWC). Otherwise [1,C,H,W] (NCHW).")]
    public bool        useNHWC    = false;

    [Header("Normalization")]
    [Tooltip("If true, applies mean/std (ImageNet style).")]
    public bool   applyMeanStd = true;
    public Vector3 mean = new Vector3(0.485f, 0.456f, 0.406f);
    public Vector3 std  = new Vector3(0.229f, 0.224f, 0.225f);

    [Header("Warm-up")]
    public bool prewarmOnStart = true;
    [Range(1,5)] public int prewarmFrames = 2;

    [Header("Debug")]
    public bool verbose = false;

    // internals
    Model  _model;
    Worker _worker;
    RenderTexture _stagingRT;     // inputSize x inputSize RGBA32
    Texture2D     _readbackTex;   // CPU-side RGBA32
    int _outputDim = -1;

    void Awake() => EnsureWorker();
    void Start() { if (prewarmOnStart) StartCoroutine(PrewarmRoutine()); }
    void OnDestroy() => Dispose();

    void EnsureWorker()
    {
        if (_worker != null) return;
        if (modelAsset == null) { Debug.LogError("[Embedder] No ModelAsset assigned."); return; }

        // Inference Engine 2.2: load Model from ModelAsset, then create Worker
        _model  = ModelLoader.Load(modelAsset);                                   // :contentReference[oaicite:2]{index=2}
        _worker = new Worker(_model, backend);                                     // :contentReference[oaicite:3]{index=3}

        if (verbose) Debug.Log($"[Embedder] Worker created. backend={backend}, in={inputName}, out={outputName}, size={inputSize}, NHWC={useNHWC}");
    }

    RenderTexture GetRT()
    {
        if (_stagingRT == null || _stagingRT.width != inputSize || _stagingRT.height != inputSize)
        {
            if (_stagingRT != null) _stagingRT.Release();
            _stagingRT = new RenderTexture(inputSize, inputSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            _stagingRT.Create();
        }
        return _stagingRT;
    }

    Texture2D GetReadbackTex()
    {
        if (_readbackTex == null || _readbackTex.width != inputSize || _readbackTex.height != inputSize)
        {
            if (_readbackTex != null) Destroy(_readbackTex);
            _readbackTex = new Texture2D(inputSize, inputSize, TextureFormat.RGBA32, false, true);
        }
        return _readbackTex;
    }

    /// <summary>Embed any Texture</summary>
    public float[] Embed(Texture tex)
    {
        if (tex == null) throw new ArgumentNullException(nameof(tex));
        EnsureWorker();
        var rt = GetRT();
        Graphics.Blit(tex, rt);
        return Embed(rt);
    }

    /// <summary>Embed a RenderTexture (will be scaled if needed)</summary>
    public float[] Embed(RenderTexture source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        EnsureWorker();

        // scale into our staging RT if size differs
        var rt = GetRT();
        if (source.width != inputSize || source.height != inputSize)
            Graphics.Blit(source, rt);
        else
            rt = source;

        // GPU->CPU readback (blocking but small)
        var cpuTex = GetReadbackTex();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        cpuTex.ReadPixels(new Rect(0, 0, inputSize, inputSize), 0, 0, false);
        cpuTex.Apply(false, false);
        RenderTexture.active = prev;

        // Pack to tensor
        var inputTensor = PackToTensor(cpuTex, useNHWC);

        // 2.2 API: SetInput + Schedule; read with PeekOutput
        _worker.SetInput(inputName, inputTensor);                                  // :contentReference[oaicite:4]{index=4}
        _worker.Schedule();                                                        // :contentReference[oaicite:5]{index=5}

        // If you have multiple outputs, use PeekOutput(outputName)
        var output = _worker.PeekOutput(outputName) as Tensor<float>;              // :contentReference[oaicite:6]{index=6}
        if (output == null)
        {
            inputTensor.Dispose();
            throw new InvalidOperationException($"[Embedder] Output '{outputName}' not found or wrong data type.");
        }

        // 2.2 API provides DownloadToArray()
        var arr = output.DownloadToArray();                                        // :contentReference[oaicite:7]{index=7}
        _outputDim = arr.Length;

        inputTensor.Dispose();
        if (verbose) Debug.Log($"[Embedder] Embed OK. zdim={_outputDim}");
        return arr;
    }

    /// <summary>
    /// Convert RGBA32 Texture2D → Tensor<float>
    /// Layout: [1,C,H,W] (NCHW) or [1,H,W,C] (NHWC)
    /// Values: 0..1 or normalized by mean/std (if applyMeanStd).
    /// </summary>
    Tensor<float> PackToTensor(Texture2D tex, bool nhwc)
    {
        int H = tex.height, W = tex.width;
        var pixels = tex.GetPixels32();
        const int C = 3;

        if (nhwc)
        {
            var t = new Tensor<float>(new TensorShape(1, H, W, C));
            int idx = 0;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var p = pixels[y * W + x];
                float r = p.r / 255f, g = p.g / 255f, b = p.b / 255f;
                if (applyMeanStd) { r = (r - mean.x)/std.x; g = (g - mean.y)/std.y; b = (b - mean.z)/std.z; }
                t[idx++] = r; t[idx++] = g; t[idx++] = b;
            }
            return t;
        }
        else
        {
            var t = new Tensor<float>(new TensorShape(1, C, H, W));
            int plane = H * W;
            int rOff = 0, gOff = plane, bOff = 2 * plane;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int pi = y * W + x;
                var p = pixels[pi];
                float r = p.r / 255f, g = p.g / 255f, b = p.b / 255f;
                if (applyMeanStd) { r = (r - mean.x)/std.x; g = (g - mean.y)/std.y; b = (b - mean.z)/std.z; }
                t[rOff + pi] = r; t[gOff + pi] = g; t[bOff + pi] = b;
            }
            return t;
        }
    }

    System.Collections.IEnumerator PrewarmRoutine()
    {
        var temp = new RenderTexture(inputSize, inputSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        temp.Create();
        Graphics.Blit(Texture2D.blackTexture, temp);

        int frames = Mathf.Max(1, prewarmFrames);
        for (int i = 0; i < frames; i++)
        {
            try { var _ = Embed(temp); }
            catch (Exception e) { if (verbose) Debug.LogWarning("[Embedder] Prewarm: " + e.Message); }
            yield return null; // spread over a couple frames
        }

        temp.Release();
        Destroy(temp);
        if (verbose) Debug.Log("[Embedder] Prewarm complete.");
    }

    public int OutputDim => _outputDim;

    public void LoadModel(
        ModelAsset asset,
        string newInputName  = null,
        string newOutputName = null,
        int?   newInputSize  = null,
        bool?  newUseNHWC    = null,
        BackendType? newBackend = null)
    {
        modelAsset = asset;
        if (newInputName  != null) inputName  = newInputName;
        if (newOutputName != null) outputName = newOutputName;
        if (newInputSize  != null) inputSize  = newInputSize.Value;
        if (newUseNHWC    != null) useNHWC    = newUseNHWC.Value;
        if (newBackend    != null) backend    = newBackend.Value;

        DisposeWorker();
        EnsureWorker();
    }

    void DisposeWorker() { _worker?.Dispose(); _worker = null; _model = null; }

    public void Dispose()
    {
        DisposeWorker();
        if (_stagingRT != null) { _stagingRT.Release(); _stagingRT = null; }
        if (_readbackTex != null) { Destroy(_readbackTex); _readbackTex = null; }
    }
}
