using UnityEngine;
using UnityEngine.Rendering;
using System;

using Unity.InferenceEngine;   // Worker, Tensor<T>, ModelAsset, TensorShape, BackendType

public class SentisEmbedder : MonoBehaviour, IDisposable
{
    [Header("Model")]
    public ModelAsset modelAsset;
    public BackendType backend = BackendType.GPUCompute;
    public string inputName  = "input";
    public string outputName = "embedding";
    public int    inputSize  = 224;
    public bool   useNHWC    = false; // PyTorch ONNX => false

    [Header("Debug")]
    public bool verbose = true;

    [Header("State (read-only)")]
    public int OutputDim { get; private set; } = -1;

    private Model  model;
    private Worker worker;
    private RenderTexture rt;

    public string CurrentModelName => modelAsset ? modelAsset.name : "(no model)";

    void Awake() { CreateWorker(); }

    void CreateWorker()
    {
        DisposeWorker();
        if (modelAsset == null) { if (verbose) Debug.Log("[Embedder] No modelAsset."); return; }

        model  = ModelLoader.Load(modelAsset);
        worker = new Worker(model, backend);

        if (rt == null)
        {
            rt = new RenderTexture(inputSize, inputSize, 0, RenderTextureFormat.ARGB32);
            rt.Create();
        }
        else if (rt.width != inputSize || rt.height != inputSize)
        {
            rt.Release();
            rt.width = inputSize; rt.height = inputSize; rt.Create();
        }

        if (verbose) Debug.Log($"[Embedder] Worker ready. model={CurrentModelName}, backend={backend}, in={inputName}, out={outputName}, size={inputSize}, NHWC={useNHWC}");
    }

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
        if (newInputSize.HasValue) inputSize  = newInputSize.Value;
        if (newUseNHWC.HasValue)   useNHWC    = newUseNHWC.Value;
        if (newBackend.HasValue)   backend    = newBackend.Value;

        OutputDim = -1;
        CreateWorker();
    }

    public void SetBackend(BackendType b)
    {
        if (backend == b) return;
        backend = b;
        CreateWorker();
    }

    public float[] Embed(Texture src)
    {
        if (worker == null || modelAsset == null) { if (verbose) Debug.LogWarning("[Embedder] No worker/model."); return null; }
        if (src == null) { if (verbose) Debug.LogWarning("[Embedder] Source texture is null."); return null; }

        // downscale source → rt (GPU)
        Graphics.Blit(src, rt);

        // readback pixels
        var req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
        req.WaitForCompletion();
        if (!req.done || req.hasError) { Debug.LogWarning("[Embedder] GPU readback failed."); return null; }
        var data = req.GetData<byte>();

        // build input tensor
        var shape = useNHWC
            ? new TensorShape(1, inputSize, inputSize, 3)  // NHWC
            : new TensorShape(1, 3, inputSize, inputSize); // NCHW

        using Tensor<float> input = new Tensor<float>(shape);

        int idx = 0;
        if (useNHWC)
        {
            for (int y = 0; y < inputSize; y++)
                for (int x = 0; x < inputSize; x++)
                {
                    byte r = data[idx]; byte g = data[idx + 1]; byte b = data[idx + 2];
                    idx += 4; // skip A
                    input[0, y, x, 0] = ((r / 255f) - 0.485f) / 0.229f;
                    input[0, y, x, 1] = ((g / 255f) - 0.456f) / 0.224f;
                    input[0, y, x, 2] = ((b / 255f) - 0.406f) / 0.225f;
                }
        }
        else
        {
            for (int y = 0; y < inputSize; y++)
                for (int x = 0; x < inputSize; x++)
                {
                    byte r = data[idx]; byte g = data[idx + 1]; byte b = data[idx + 2];
                    idx += 4;
                    input[0, 0, y, x] = ((r / 255f) - 0.485f) / 0.229f;
                    input[0, 1, y, x] = ((g / 255f) - 0.456f) / 0.224f;
                    input[0, 2, y, x] = ((b / 255f) - 0.406f) / 0.225f;
                }
        }

        worker.SetInput(inputName, input);
        worker.Schedule();

        var outTensor = worker.PeekOutput(outputName) as Tensor<float>;
        if (outTensor == null) { Debug.LogError($"[Embedder] Output '{outputName}' not found or wrong type."); return null; }

        float[] z = outTensor.DownloadToArray(); // CPU array
        OutputDim = z.Length;

        if (verbose) Debug.Log($"[Embedder] Embed OK. zdim={OutputDim}");
        return z;
    }

    void DisposeWorker() { worker?.Dispose(); worker = null; }

    public void Dispose()
    {
        DisposeWorker();
        if (rt != null) rt.Release();
    }
}
