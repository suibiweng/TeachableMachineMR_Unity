using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.InferenceEngine; // for ModelAsset (Sentis)

[DisallowMultipleComponent]
public class DynamicClassTrainer : MonoBehaviour
{
    [Header("Pipeline")]
    public SentisEmbedder embedder;         // assign in Inspector
    public Texture sourceTexture;           // 224x224 RenderTexture (webcam/passthrough)
    public LiveClassifier liveClassifier;   // optional: apply trained head live

    [Header("Models (Sentis)")]
    public List<ModelAsset> availableModels = new List<ModelAsset>();
    public int currentModelIndex = 0;

    [Header("Session & Files")]
    public string sessionName = "session1";
    public string saveHeadName = "head_runtime.json";
    public string loadHeadName = "head_runtime.json";

    [Header("Classes")]
    [SerializeField] private List<string> classNames = new List<string> { "class_A", "class_B" };
    private int currentClass = 0;

    // Running sums of normalized embeddings, and counts, per class
    private List<float[]> sums = new List<float[]>();
    private List<int> counts = new List<int>();

    // Embedding dimension (set once we get the first embedding)
    private int embeddingDim = -1;

    // 🔔 Event: many of your scripts subscribe to this exact signature
    public event Action<string, HeadData> OnHeadTrained;

    // Properties used by TrainerUIController and others
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
    }

    // ---------------- Model switching (optional) ----------------
    public void SelectModelByIndex(int index)
    {
        if (embedder == null || availableModels == null || availableModels.Count == 0) return;
        index = Mathf.Clamp(index, 0, availableModels.Count - 1);
        currentModelIndex = index;
        embedder.LoadModel(availableModels[index]); // your SentisEmbedder should handle this
        // Clear accumulators (old samples incompatible with new model dims)
        embeddingDim = -1;
        sums.Clear(); counts.Clear();
        EnsureAccumulatorsSized();
        Debug.Log($"[Trainer] Model switched -> {availableModels[index].name}");
    }

    // ---------------- Class management ----------------
    public void AddClass(string name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        classNames.Add(name);
        sums.Add(embeddingDim > 0 ? new float[embeddingDim] : null);
        counts.Add(0);
        currentClass = classNames.Count - 1;
        Debug.Log($"[Trainer] Added class '{name}'");
    }

    public void SelectClass(int index)
    {
        if (index < 0 || index >= classNames.Count) return;
        currentClass = index;
    }

    // ✅ Overload expected by HeadAutoLoader / HeadSwitcherDropdown (string[])
    public void ResetAll(string[] newClasses)
    {
        ResetAll(newClasses == null ? new List<string>() : new List<string>(newClasses));
    }

    // ✅ Overload used elsewhere (List<string>)
    public void ResetAll(List<string> newClasses)
    {
        classNames = newClasses ?? new List<string>();
        currentClass = classNames.Count > 0 ? 0 : -1;

        sums.Clear(); counts.Clear();
        for (int i = 0; i < classNames.Count; i++)
        {
            sums.Add(embeddingDim > 0 ? new float[embeddingDim] : null);
            counts.Add(0);
        }
        Debug.Log($"[Trainer] ResetAll -> {classNames.Count} classes");
    }

    // ---------------- Data collection ----------------
    public void AddSampleFromCurrent()
    {
        if (currentClass < 0 || currentClass >= classNames.Count)
        {
            Debug.LogWarning("[Trainer] No class selected.");
            return;
        }
        var z = GetEmbedding();
        if (z == null) return;

        var zn = (float[])z.Clone();
        TinyHeads.L2Normalize(zn);

        var acc = sums[currentClass];
        if (acc == null || acc.Length != embeddingDim)
        {
            acc = new float[embeddingDim];
            sums[currentClass] = acc;
        }
        for (int i = 0; i < embeddingDim; i++) acc[i] += zn[i];
        counts[currentClass]++;

        AppendEmbeddingCSV(z, currentClass, sessionName);
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
                for (int i = 0; i < embeddingDim; i++) head.centroids[c][i] = sums[c][i] / counts[c];
                TinyHeads.L2Normalize(head.centroids[c]);
            }
        }

        // Save JSON under .../heads/
        var dir = Path.Combine(PersistentPath, "heads");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, saveHeadName);
        File.WriteAllText(full, JsonUtility.ToJson(head, true));
        Debug.Log($"[Trainer] Head saved -> {full}");

        // Apply to Live
        if (liveClassifier) liveClassifier.SetHead(head);

        // Notify listeners (filename + head)
        OnHeadTrained?.Invoke(saveHeadName, head);
    }

    public void LoadHeadAndApply()
    {
        var full = Path.Combine(PersistentPath, "heads", loadHeadName);
        if (!File.Exists(full)) { Debug.LogError("[Trainer] Head not found: " + full); return; }
        var json = File.ReadAllText(full);
        var head = JsonUtility.FromJson<HeadData>(json);

        // Sync classes and zero accumulators
        ResetAll(head.classes);

        if (liveClassifier) liveClassifier.SetHead(head);

        // Notify listeners so dropdowns/autoloaders update
        OnHeadTrained?.Invoke(loadHeadName, head);

        Debug.Log($"[Trainer] Head loaded & applied -> {full}");
    }

    // ---------------- Utility ----------------
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
            for (int i = 0; i < counts.Count; i++) counts[i] = 0;
            Debug.Log($"[Trainer] Set embeddingDim={embeddingDim}");
        }
        return z;
    }

    void EnsureAccumulatorsSized()
    {
        int C = classNames.Count;
        while (sums.Count < C) sums.Add(embeddingDim > 0 ? new float[embeddingDim] : null);
        while (counts.Count < C) counts.Add(0);
    }

    void AppendEmbeddingCSV(float[] z, int cls, string baseName)
    {
        var dir = Path.Combine(PersistentPath, "records");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, baseName + ".csv");
        using (var sw = new StreamWriter(full, append: true))
        {
            sw.Write(cls);
            for (int i = 0; i < z.Length; i++)
                sw.Write("," + z[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            sw.WriteLine();
        }
    }

    public void SaveSnapshotPNG(string prefix = "cap")
    {
        var tex = Capture(sourceTexture);
        if (tex == null) return;
        var bytes = tex.EncodeToPNG();
        Destroy(tex);
        var dir = Path.Combine(PersistentPath, "snaps");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{prefix}_{(currentClass >= 0 && currentClass < classNames.Count ? classNames[currentClass] : "_")}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        File.WriteAllBytes(file, bytes);
        Debug.Log("[Trainer] PNG -> " + file);
    }

    Texture2D Capture(Texture src)
    {
        var rt = src as RenderTexture;
        if (rt == null) { Debug.LogError("[Trainer] sourceTexture must be a RenderTexture"); return null; }
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        return tex;
    }
    

    // --- Debug helper (safe to call from UI) ---
public void DebugDump()
{
    // classNames: your class list
    // counts: per-class sample counts (if you keep one; else print "n/a")
    // embeddingDim: feature dimension set after first sample
    int classCount = (classNames != null) ? classNames.Count : 0;
    Debug.Log($"[Trainer] Dump: classes={classCount}, dim={embeddingDim}, currentClass={CurrentClassIndex}");

    // If you maintain 'counts' parallel to classNames, print them; otherwise comment this loop.
    if (counts != null && counts.Count == classCount)
    {
        for (int i = 0; i < classCount; i++)
        {
            string lbl = classNames[i];
            int cnt = counts[i];
            Debug.Log($"  - [{i}] {lbl} : count={cnt}");
        }
    }
    else
    {
        for (int i = 0; i < classCount; i++)
        {
            string lbl = classNames[i];
            Debug.Log($"  - [{i}] {lbl} : count=n/a");
        }
    }
}

}
