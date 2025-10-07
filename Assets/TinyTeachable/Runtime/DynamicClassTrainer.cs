using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.InferenceEngine;   // ModelAsset (embedder switching)

public class DynamicClassTrainer : MonoBehaviour
{
    [Header("Pipeline")]
    public SentisEmbedder embedder;         // assign in Inspector
    public Texture sourceTexture;           // RT_224 or Passthrough RT
    public LiveClassifier liveClassifier;   // optional: apply trained head live

    [Header("Models (optional switching)")]
    public List<ModelAsset> availableModels = new List<ModelAsset>(); // drag ModelAssets here if you want to switch
    public int currentModelIndex = 0;                                 // selected index in availableModels
    public string resourcesModelName = "";                            // optional: Resources path for ModelAsset

    [Header("Classes & Session")]
    public List<string> classNames = new List<string> { "class_A", "class_B" };
    public string sessionName = "session1";
    public string saveHeadName = "head_runtime.json";
    public string loadHeadName = "head_runtime.json";

    [Header("Options")]
    public bool rememberHeadAsDefault = true; // remember last trained head name (auto-load next run)

    // Runtime state
    private int currentClass = 0;
    private int embeddingDim = -1;
    private List<float[]> sums = new List<float[]>();  // cumulative (normalized) embeddings per class
    private List<int> counts = new List<int>();        // sample counts per class

    // Events
    public event Action<string, HeadData> OnHeadTrained;

    // Public info
    public int CurrentClassIndex => currentClass;
    public IReadOnlyList<string> Classes => classNames;
    public int EmbeddingDim => embeddingDim;
    public string PersistentPath => Application.persistentDataPath;
    public string CurrentModelName => embedder ? embedder.CurrentModelName : "(no model)";

    void Awake()
    {
        EnsureAccumulatorsSized();

        // If embedder has no model but we have one in the list, load it
        if (embedder && embedder.modelAsset == null && availableModels.Count > 0)
        {
            SelectModelByIndex(Mathf.Clamp(currentModelIndex, 0, availableModels.Count - 1));
        }
        else if (embedder && embedder.modelAsset != null)
        {
            Debug.Log($"[Trainer] Using existing model: {embedder.CurrentModelName}");
        }
    }

    // --------------------------- MODEL SWITCHING ----------------------------

    public void SelectModelByIndex(int index)
    {
        if (embedder == null || availableModels == null || availableModels.Count == 0) return;
        index = Mathf.Clamp(index, 0, availableModels.Count - 1);
        currentModelIndex = index;
        var asset = availableModels[index];
        embedder.LoadModel(asset); // re-create worker with same IO names/size set on embedder
        OnModelSwitched();
    }

    public void SelectModelByNameFromResources(string modelPath, string inName = null, string outName = null, int? size = null, bool? nhwc = null)
    {
        if (embedder == null || string.IsNullOrWhiteSpace(modelPath)) return;
        var asset = Resources.Load<ModelAsset>(modelPath); // e.g. "Models/mobilenetv3_small_embed"
        if (asset == null) { Debug.LogError($"[Trainer] Resources model '{modelPath}' not found."); return; }
        resourcesModelName = modelPath;
        embedder.LoadModel(asset, inName, outName, size, nhwc);
        OnModelSwitched();
    }

    void OnModelSwitched()
    {
        Debug.Log($"[Trainer] Model switched -> {CurrentModelName}");
        // Embedding dim may change; clear accumulators and start clean
        embeddingDim = -1;
        for (int i = 0; i < sums.Count; i++) sums[i] = null;
        for (int i = 0; i < counts.Count; i++) counts[i] = 0;
        // You’ll need to re-collect samples and retrain a head for the new embedder.
    }

    // --------------------------- CLASS & TRAINING ---------------------------

    public void AddClass(string name)
    {
        name = Sanitize(name);
        if (string.IsNullOrWhiteSpace(name)) return;

        classNames.Add(name);
        sums.Add(embeddingDim > 0 ? new float[embeddingDim] : null);
        counts.Add(0);

        currentClass = classNames.Count - 1;
        Debug.Log($"[Trainer] Added class '{name}' (total={classNames.Count})");
    }

    public void SelectClass(int index)
    {
        if (index < 0 || index >= classNames.Count) return;
        currentClass = index;
    }

    public void AddSampleFromCurrent()
    {
        var z = GetEmbedding();
        if (z == null) { Debug.LogWarning("[Trainer] No embedding."); return; }

        // Accumulate normalized embedding
        var zn = (float[])z.Clone();
        TinyHeads.L2Normalize(zn);

        var arr = sums[currentClass];
        if (arr == null || arr.Length != embeddingDim)
        {
            arr = new float[embeddingDim];
            sums[currentClass] = arr;
        }
        for (int i = 0; i < embeddingDim; i++) arr[i] += zn[i];
        counts[currentClass]++;

        AppendEmbeddingCSV(z, currentClass, sessionName);
        Debug.Log($"[Trainer] +1 -> '{classNames[currentClass]}' (count={counts[currentClass]})");
    }

    public void TrainAndSaveAndApply()
    {
        if (embeddingDim <= 0) { Debug.LogWarning("[Trainer] Need samples first."); return; }
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

        SaveHead(head, saveHeadName);

        if (liveClassifier) liveClassifier.SetHead(head);

        if (rememberHeadAsDefault)
        {
            PlayerPrefs.SetString("TinyTeach_LastHead", saveHeadName);
            PlayerPrefs.Save();
        }
        OnHeadTrained?.Invoke(saveHeadName, head);

        Debug.Log($"[Trainer] Head saved & applied: {Path.Combine(PersistentPath, "heads", saveHeadName)}");
    }

    public void LoadHeadAndApply()
    {
        var path = Path.Combine(PersistentPath, "heads", loadHeadName);
        if (!File.Exists(path)) { Debug.LogError("[Trainer] Missing head: " + path); return; }
        var json = File.ReadAllText(path);
        var head = JsonUtility.FromJson<HeadData>(json);
        classNames = new List<string>(head.classes ?? Array.Empty<string>());

        if (liveClassifier) liveClassifier.SetHead(head);
        Debug.Log("[Trainer] Loaded head & applied: " + path);
    }

    // --------------------------- CLEAN-UP / SESSION ------------------------

    public void SetSessionName(string newSession) {
        if (!string.IsNullOrWhiteSpace(newSession)) sessionName = newSession.Trim();
        Debug.Log("[Trainer] Session set to: " + sessionName);
    }

    public void ClearCurrentClass() {
        if (embeddingDim <= 0 || currentClass < 0 || currentClass >= classNames.Count) return;
        sums[currentClass] = new float[embeddingDim];
        counts[currentClass] = 0;
        Debug.Log($"[Trainer] Cleared class '{classNames[currentClass]}' samples.");
    }

    public void RemoveCurrentClass() {
        if (currentClass < 0 || currentClass >= classNames.Count) return;
        string removed = classNames[currentClass];
        classNames.RemoveAt(currentClass);
        if (currentClass < sums.Count)  sums.RemoveAt(currentClass);
        if (currentClass < counts.Count) counts.RemoveAt(currentClass);
        currentClass = Mathf.Clamp(currentClass, 0, classNames.Count - 1);
        Debug.Log($"[Trainer] Removed class '{removed}'.");
    }

    public void ResetAll(string[] newClasses = null) {
        classNames = newClasses != null ? new List<string>(newClasses) : new List<string>();
        sums.Clear(); counts.Clear();
        embeddingDim = -1;  // re-init on next sample
        EnsureAccumulatorsSized();
        if (liveClassifier) liveClassifier.SetHead(null); // pause until retrained/loaded
        Debug.Log("[Trainer] Reset all. Define classes and start sampling.");
    }

    // optional: wipe current session CSV from disk
    public void DeleteCurrentSessionCSV() {
        var path = Path.Combine(PersistentPath, "records", sessionName + ".csv");
        if (File.Exists(path)) { File.Delete(path); Debug.Log("[Trainer] Deleted " + path); }
    }

    // --------------------------- SNAPSHOT (restored) -----------------------

    public void SaveSnapshotPNG(string prefix = "cap")
    {
        var tex = Capture(sourceTexture);
        if (tex == null) return;

        var bytes = tex.EncodeToPNG();
        Destroy(tex);

        var dir = Path.Combine(PersistentPath, "records");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{prefix}_{classNames[Mathf.Clamp(currentClass,0,Mathf.Max(0,classNames.Count-1))]}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
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

    // --------------------------- INTERNALS ---------------------------------

    string Sanitize(string s) => s?.Trim();

    float[] GetEmbedding()
    {
        if (embedder == null || sourceTexture == null) return null;
        var z = embedder.Embed(sourceTexture);
        if (z == null) return null;

        if (embeddingDim < 0)
        {
            embeddingDim = z.Length;
            // resize accumulators to new D
            for (int i = 0; i < sums.Count; i++) sums[i] = new float[embeddingDim];
            for (int i = 0; i < counts.Count; i++) counts[i] = 0;
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

    void SaveHead(HeadData head, string filename)
    {
        var dir = Path.Combine(PersistentPath, "heads");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, filename);
        var json = JsonUtility.ToJson(head, true);
        File.WriteAllText(full, json);
    }
}
