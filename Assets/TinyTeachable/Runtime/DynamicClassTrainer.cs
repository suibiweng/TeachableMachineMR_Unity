using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.InferenceEngine; // Sentis ModelAsset

[DisallowMultipleComponent]
public class DynamicClassTrainer : MonoBehaviour
{
    [Header("Pipeline")]
    public SentisEmbedder embedder;         // assign in Inspector
    public Texture sourceTexture;           // 224x224 RenderTexture (webcam/passthrough)
    public LiveClassifier liveClassifier;   // optional: apply trained head live

    [Header("Models (Sentis)")]
    public List<ModelAsset> availableModels = new List<ModelAsset>();
    [Tooltip("Index into availableModels; -1 = keep current embedder.modelAsset")]
    public int currentModelIndex = -1;

    [Header("Session / Filenames")]
    [Tooltip("Logical name for this training session / detection. Other scripts reference this.")]
    public string sessionName = "Session1";

    [Tooltip("Filename used when saving a head (.json). If empty, falls back to sessionName + .json")]
    public string saveHeadName = "";

    [Tooltip("Filename used when loading a head (.json). If empty, falls back to sessionName + .json")]
    public string loadHeadName = "";

    [Header("Capture / Training")]
    [Tooltip("Normalize each embedding before accumulation (recommended for centroid).")]
    public bool l2NormalizeEmbeddings = true;

    [Header("Classes")]
    [SerializeField] private List<string> classNames = new List<string> { "class_A", "class_B" };
    private int currentClass = 0;

    // Running sums of normalized embeddings, and counts, per class
    private List<float[]> sums = new List<float[]>();
    private List<int> counts = new List<int>();

    // Embedding dimension (set once we get the first embedding)
    private int embeddingDim = -1;

    // Buffer used when reading embeddings
    private float[] lastEmbedding;

    // Events
    public event Action<string, HeadData> OnHeadTrained; // (fileName, head)

    // Public read-only props
    public IReadOnlyList<string> Classes => classNames;
    public int CurrentClassIndex => currentClass;
    public int EmbeddingDim => embeddingDim;
    public string PersistentPath => Application.persistentDataPath;

    void Awake()
    {
        EnsureAccumulatorsSized();

        // Optional: auto-select model if provided
        if (embedder && embedder.modelAsset == null && availableModels.Count > 0)
            SelectModelByIndex(Mathf.Clamp(currentModelIndex, 0, availableModels.Count - 1));

        // Ensure filenames have sensible defaults
        EnsureDefaultFilenames();
    }

    // ---------------- Session name helper ----------------
    public void SetSessionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        sessionName = name.Trim();

        // If user hasn’t explicitly set filenames, update defaults to match the session
        if (string.IsNullOrWhiteSpace(saveHeadName))
            saveHeadName = sessionName + ".json";
        if (string.IsNullOrWhiteSpace(loadHeadName))
            loadHeadName = sessionName + ".json";
    }

    void EnsureDefaultFilenames()
    {
        // Only set if empty; don’t override explicit user choices
        if (string.IsNullOrWhiteSpace(saveHeadName))
            saveHeadName = (string.IsNullOrWhiteSpace(sessionName) ? "head_runtime" : sessionName) + ".json";
        if (string.IsNullOrWhiteSpace(loadHeadName))
            loadHeadName = (string.IsNullOrWhiteSpace(sessionName) ? "head_runtime" : sessionName) + ".json";

        if (!saveHeadName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) saveHeadName += ".json";
        if (!loadHeadName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) loadHeadName += ".json";
    }

    // ---------------- Model switching (optional) ----------------
    public void SelectModelByIndex(int idx)
    {
        if (embedder == null) { Debug.LogWarning("[Trainer] No embedder."); return; }
        if (idx < 0 || idx >= availableModels.Count) { Debug.LogWarning("[Trainer] Model index OOB."); return; }
        embedder.modelAsset = availableModels[idx];
        currentModelIndex = idx;
        Debug.Log($"[Trainer] Model set: {availableModels[idx]?.name}");
    }

    // ---------------- Class management ----------------
    public void AddClass(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        classNames.Add(name);
        EnsureAccumulatorsSized();
        currentClass = classNames.Count - 1;
        Debug.Log($"[Trainer] Added class '{name}'");
    }

    public void SelectClass(int index)
    {
        if (classNames.Count == 0) { currentClass = 0; return; }
        currentClass = Mathf.Clamp(index, 0, classNames.Count - 1);
        Debug.Log($"[Trainer] Selected class '{classNames[currentClass]}' (#{currentClass})");
    }

    public void ResetAll(params string[] newClasses) => ResetAll(new List<string>(newClasses));

    public void ResetAll(List<string> newClasses)
    {
        classNames = new List<string>(newClasses);
        if (classNames.Count == 0)
        {
            classNames.Add("class_A");
            classNames.Add("class_B");
        }
        embeddingDim = Mathf.Max(embeddingDim, 0); // leave as-is until first embedding if unknown
        sums = new List<float[]>();
        counts = new List<int>();
        EnsureAccumulatorsSized();
        currentClass = Mathf.Clamp(currentClass, 0, classNames.Count - 1);
        Debug.Log($"[Trainer] ResetAll -> {classNames.Count} classes");
    }

    void EnsureAccumulatorsSized()
    {
        int C = Mathf.Max(1, classNames.Count);
        while (sums.Count < C) sums.Add(embeddingDim > 0 ? new float[embeddingDim] : null);
        while (counts.Count < C) counts.Add(0);
        while (sums.Count > C) sums.RemoveAt(sums.Count - 1);
        while (counts.Count > C) counts.RemoveAt(counts.Count - 1);
    }

    // ---------------- Sampling ----------------
    public void AddSampleFromCurrent()
    {
        var z = GetEmbedding();
        if (z == null || z.Length == 0) { Debug.LogWarning("[Trainer] No embedding to sample."); return; }

        if (l2NormalizeEmbeddings) L2NormalizeInPlace(z);

        EnsureAccumulatorsSized();
        if (sums[currentClass] == null || sums[currentClass].Length != z.Length)
        {
            sums[currentClass] = new float[z.Length];
            if (embeddingDim != z.Length)
            {
                // Resize existing sums if dimension changed
                for (int i = 0; i < sums.Count; i++)
                {
                    if (sums[i] == null) sums[i] = new float[z.Length];
                    else if (sums[i].Length != z.Length)
                    {
                        var tmp = new float[z.Length];
                        int n = Mathf.Min(tmp.Length, sums[i].Length);
                        for (int k = 0; k < n; k++) tmp[k] = sums[i][k];
                        sums[i] = tmp;
                    }
                }
                embeddingDim = z.Length;
            }
        }

        // Accumulate
        for (int i = 0; i < z.Length; i++)
            sums[currentClass][i] += z[i];

        counts[currentClass] += 1;

        // Keep default names in sync if user hasn’t set them
        EnsureDefaultFilenames();

        Debug.Log($"[Trainer] +1 to '{classNames[currentClass]}' (count={counts[currentClass]})");
    }

    // ---------------- Train / Save / Apply ----------------
    public void TrainAndSaveAndApply()
    {
        if (embeddingDim <= 0)
        {
            Debug.LogWarning("[Trainer] No embeddings yet. Add samples first.");
            return;
        }

        EnsureAccumulatorsSized();
        int C = classNames.Count;
        var head = new HeadData { type = "centroid", classes = classNames.ToArray(), centroids = new float[C][] };

        for (int c = 0; c < C; c++)
        {
            head.centroids[c] = new float[embeddingDim];
            if (counts[c] > 0 && sums[c] != null)
            {
                // mean
                float inv = 1f / Mathf.Max(1, counts[c]);
                for (int i = 0; i < embeddingDim; i++)
                    head.centroids[c][i] = sums[c][i] * inv;

                // normalize centroid for cosine scoring
                L2NormalizeInPlace(head.centroids[c]);
            }
            else
            {
                // No samples -> zero centroid (won't win)
                for (int i = 0; i < embeddingDim; i++)
                    head.centroids[c][i] = 0f;
            }
        }

        // Filenames: prefer explicit saveHeadName; else fall back to sessionName
        EnsureDefaultFilenames();

        var dir = Path.Combine(PersistentPath, "heads");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, saveHeadName);
        File.WriteAllText(full, JsonUtility.ToJson(head, true));
        Debug.Log($"[Trainer] Head saved -> {full}");

        // Apply to Live  ✅ pass display name (without extension) so CurrentHeadName is set
        if (liveClassifier) liveClassifier.SetHead(head, Path.GetFileNameWithoutExtension(saveHeadName));

        // Notify listeners (filename + head)
        OnHeadTrained?.Invoke(saveHeadName, head);
    }

    public void LoadHeadAndApply()
    {
        // Filenames: prefer explicit; else fall back to sessionName
        EnsureDefaultFilenames();

        var full = Path.Combine(PersistentPath, "heads", loadHeadName);
        if (!File.Exists(full)) { Debug.LogError("[Trainer] Head not found: " + full); return; }
        var json = File.ReadAllText(full);
        var head = JsonUtility.FromJson<HeadData>(json);
        if (head == null || head.classes == null || head.classes.Length == 0)
        {
            Debug.LogWarning("[Trainer] Parsed head is invalid: " + full);
            return;
        }

        // Sync classes and zero accumulators
        ResetAll(head.classes);

        // Apply to Live  ✅ pass display name (without extension) so CurrentHeadName is set
        if (liveClassifier) liveClassifier.SetHead(head, Path.GetFileNameWithoutExtension(loadHeadName));

        // Notify listeners so dropdowns/autoloaders update
        OnHeadTrained?.Invoke(loadHeadName, head);

        Debug.Log($"[Trainer] Head loaded & applied -> {full}");
    }

    // Optional overload if caller wants to specify a particular head by name
    public void LoadHeadAndApply(string headName)
    {
        if (string.IsNullOrWhiteSpace(headName))
        {
            Debug.LogWarning("[Trainer] LoadHeadAndApply(name): name empty. Using loadHeadName/sessionName.");
            LoadHeadAndApply();
            return;
        }

        string file = headName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? headName : (headName + ".json");
        var full = Path.Combine(PersistentPath, "heads", file);
        if (!File.Exists(full)) { Debug.LogError("[Trainer] Head not found: " + full); return; }

        var json = File.ReadAllText(full);
        var head = JsonUtility.FromJson<HeadData>(json);
        if (head == null || head.classes == null || head.classes.Length == 0)
        {
            Debug.LogWarning("[Trainer] Parsed head is invalid: " + full);
            return;
        }

        ResetAll(head.classes);

        if (liveClassifier) liveClassifier.SetHead(head, Path.GetFileNameWithoutExtension(file));
        OnHeadTrained?.Invoke(file, head);
        Debug.Log($"[Trainer] Head loaded & applied -> {full}");
    }

    // ---------------- Utility ----------------
    public void SaveSnapshotPNG(string fileName = "snapshot.png", int size = 256)
    {
        if (!(sourceTexture is RenderTexture rt))
        {
            Debug.LogWarning("[Trainer] SaveSnapshot: sourceTexture is not a RenderTexture.");
            return;
        }
        var prev = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        var scaled = ScaleTexture(tex, size, size);
        var bytes = scaled.EncodeToPNG();
        var path = Path.Combine(PersistentPath, fileName);
        File.WriteAllBytes(path, bytes);
        Destroy(tex);
        Destroy(scaled);
        Debug.Log($"[Trainer] Snapshot saved -> {path}");
    }

    public void DebugDump()
    {
        Debug.Log($"[Trainer] Classes: [{string.Join(", ", classNames)}], dim={embeddingDim}, session='{sessionName}'");
        for (int c = 0; c < classNames.Count; c++)
            Debug.Log($"   - {classNames[c]}: count={counts[c]}");
    }

    // ---------------- Internals ----------------
    float[] GetEmbedding()
    {
        if (embedder == null || sourceTexture == null)
        {
            Debug.LogError("[Trainer] Missing embedder or sourceTexture");
            return null;
        }
        var z = embedder.Embed(sourceTexture);
        if (z == null) { Debug.LogError("[Trainer] Embed returned null"); return null; }

        if (embeddingDim < 0)
        {
            embeddingDim = z.Length;
            // resize accumulators
            for (int i = 0; i < sums.Count; i++) sums[i] = new float[embeddingDim];
        }
        lastEmbedding = z;
        return z;
    }

    static void L2NormalizeInPlace(float[] v)
    {
        if (v == null || v.Length == 0) return;
        double s = 0.0; for (int i = 0; i < v.Length; i++) s += (double)v[i] * v[i];
        double inv = s > 1e-12 ? 1.0 / Math.Sqrt(s) : 0.0;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] * inv);
    }

    static Texture2D ScaleTexture(Texture2D src, int w, int h)
    {
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0);
        Graphics.Blit(src, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, false);
        result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        result.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }
}
