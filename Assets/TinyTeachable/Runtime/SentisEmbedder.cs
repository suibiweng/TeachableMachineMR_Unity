using UnityEngine;
using UnityEngine.Rendering;
using Unity.InferenceEngine;
using System;
using System.Collections.Generic;

public class SentisEmbedder : MonoBehaviour, IDisposable
{
    [Header("Model")]
    public ModelAsset modelAsset;                 // drag your Sentis ModelAsset
    public BackendType backend = BackendType.GPUCompute;
    public string inputName = "input";            // match your ONNX
    public string outputName = "embedding";       // match your ONNX
    public int inputSize = 224;
    public bool useNHWC = false;                  // PyTorch ONNX => NCHW -> false

    [Header("State (read-only)")]
    public int OutputDim { get; private set; } = -1;

    private Model model;
    private Worker worker;
    private RenderTexture rt;

    public string CurrentModelName => modelAsset ? modelAsset.name : "(no model)";

    void Awake()
    {
        CreateWorker();
    }

    void CreateWorker()
    {
        DisposeWorker();
        if (modelAsset == null) return;
        model = ModelLoader.Load(modelAsset);
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
    }

    public void LoadModel(
        ModelAsset asset,
        string newInputName = null,
        string newOutputName = null,
        int? newInputSize = null,
        bool? newUseNHWC = null,
        BackendType? newBackend = null)
    {
        modelAsset = asset;
        if (newInputName != null)  inputName  = newInputName;
        if (newOutputName != null) outputName = newOutputName;
        if (newInputSize.HasValue) inputSize  = newInputSize.Value;
        if (newUseNHWC.HasValue)   useNHWC    = newUseNHWC.Value;
        if (newBackend.HasValue)   backend    = newBackend.Value;

        OutputDim = -1;
        CreateWorker();
        Debug.Log($"[SentisEmbedder] Loaded model: {CurrentModelName} (input={inputName}, output={outputName}, size={inputSize}, NHWC={useNHWC})");
    }

    public void SetBackend(BackendType b)
    {
        if (backend == b) return;
        backend = b;
        CreateWorker();
    }

    public float[] Embed(Texture src)
    {
        if (worker == null || modelAsset == null) return null;

        Graphics.Blit(src, rt);
        var req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
        req.WaitForCompletion();
        var data = req.GetData<byte>();

        var shape = useNHWC
            ? new TensorShape(1, inputSize, inputSize, 3)
            : new TensorShape(1, 3, inputSize, inputSize);

        using Tensor<float> input = new Tensor<float>(shape);

        int idx = 0;
        if (useNHWC)
        {
            for (int y = 0; y < inputSize; y++)
                for (int x = 0; x < inputSize; x++)
                {
                    byte r = data[idx]; byte g = data[idx + 1]; byte b = data[idx + 2];
                    idx += 4;
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
        float[] z = outTensor.DownloadToArray();
        OutputDim = z.Length;
        return z;
    }

    void DisposeWorker()
    {
        worker?.Dispose(); worker = null;
    }

    public void Dispose()
    {
        DisposeWorker();
        if (rt != null) rt.Release();
    }
}
