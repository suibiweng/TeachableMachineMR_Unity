using System;
using System.Collections.Generic;
using System.IO;
using System.Text;                // <-- for UTF8 encoding
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
    [Tooltip("Number of classes. Change dynamically via AddClass/ResetAll if needed.")]
    public int numClasses = 2;

    [Tooltip("Current class index for AddSampleFromCurrent().")]
    [Range(0, 63)] public int currentClass = 0;

    [Tooltip("Labels for classes; kept in sync by ResetAll/AddClass.")]
    public List<string> Classes = new List<string> { "A", "B" };

    [Tooltip("Normalize embeddings to unit length before training (recommended).")]
    public bool l2NormalizeEmbeddings = true;

    [Header("Input scaling (optional snapshot util)")]
    public int snapshotSize = 256;

    // Internal
    private OnlineCentroidTrainer trainer;
    private int embeddingDim = -1;

    public string PersistentPath => Application.persistentDataPath;

    // === Compatibility properties expected by your UI ===
    public int CurrentClassIndex => Mathf.Clamp(currentClass, 0, Math.Max(0, Classes.Count - 1));
    public int EmbeddingDim => embeddingDim;

    // ---------------- Public API ----------------

    // UPDATED: keep exactly what you pass in; no default placeholder class
    public void ResetAll(string[] classLabels)
    {
        Classes = new List<string>(classLabels ?? Array.Empty<string>());

        // reset selection safely
        currentClass = 0;

        if (embeddingDim > 0 && Classes.Count > 0)
        {
            trainer = new OnlineCentroidTrainer(Classes.Count, embeddingDim);
        }
        else
        {
            trainer = null; // lazy-init on first AddSample
        }

        Debug.Log($"[Trainer] Reset with classes: [{string.Join(", ", Classes)}], dim={(embeddingDim>0?embeddingDim:-1)}");
    }

    public void AddClass(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) label = $"class_{Classes.Count}";
        Classes.Add(label);
        currentClass = Classes.Count - 1;

        if (embeddingDim > 0)
        {
            var tmp = new OnlineCentroidTrainer(Classes.Count, embeddingDim);
            trainer = tmp;
        }

        Debug.Log($"[Trainer] Added class '{label}', total={Classes.Count}");
    }

    public void SelectClass(int index)
    {
        currentClass = Mathf.Clamp(index, 0, Mathf.Max(0, Classes.Count - 1));
    }

    public void AddSampleFromCurrent()
    {
        var z = GetEmbedding();
        if (z == null) { Debug.LogWarning("[Trainer] No embedding available."); return; }

        if (l2NormalizeEmbeddings) TinyHeads.L2Normalize(z);

        EnsureTrainer(z.Length);
        trainer.AddSample(currentClass, z);

        Debug.Log($"[Trainer] +1 sample to '{Classes[currentClass]}' (dim={z.Length}) count={trainer.GetCount(currentClass)}");
    }

    // UPDATED: hardened saving + force-flush + precise logs + safe filename
    public void TrainAndSaveAndApply()
    {
        if (trainer == null || Classes == null || Classes.Count == 0)
        {
            Debug.LogWarning("[Trainer] Nothing to train. Add samples first.");
            return;
        }

        var head = trainer.ToHeadData(Classes.ToArray());
        // Safety: zero any null centroid slots
        if (head.centroids != null)
        {
            for (int c = 0; c < head.centroids.Length; c++)
            {
                if (head.centroids[c] == null)
                {
                    head.centroids[c] = new float[embeddingDim > 0 ? embeddingDim : 1];
                    for (int i = 0; i < head.centroids[c].Length; i++) head.centroids[c][i] = 0f;
                }
            }
        }

        EnsureDefaultFilenames();

        // 1) Ensure non-empty, safe filename (.json ensured)
        string safeFile = saveHeadName;
        if (string.IsNullOrWhiteSpace(safeFile))
            safeFile = (string.IsNullOrWhiteSpace(sessionName) ? "Session1" : sessionName) + ".json";
        if (!safeFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            safeFile += ".json";
        foreach (char c in Path.GetInvalidFileNameChars())
            safeFile = safeFile.Replace(c, '_');
        saveHeadName = safeFile; // keep state consistent

        // 2) Build full path under persistentDataPath/heads
        string dir = Path.Combine(PersistentPath, "heads");
        string full = Path.Combine(dir, safeFile);

        // 3) Serialize once
        string headJson = JsonUtility.ToJson(head, prettyPrint: true);

        // 4) Write with robust IO + logging
        try
        {
            Directory.CreateDirectory(dir);

            using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
            {
                sw.Write(headJson);
                sw.Flush();
                fs.Flush(true); // ensure data hits disk (Android/Quest-friendly)
            }

            long len = new FileInfo(full).Length;
            Debug.Log($"[Trainer] Head saved -> {full}  (bytes={len})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Trainer] SAVE FAILED -> {full}\n{e}");
            return; // bail out: nothing to apply
        }

        // 5) Apply to live (no file polling needed)
        if (liveClassifier)
        {
            var display = Path.GetFileNameWithoutExtension(safeFile);
            try
            {
                liveClassifier.SetPreferredHead(display, enforce: true);
                liveClassifier.SetHead(head, display, force: true);
            }
            catch (Exception e)
            {
                Debug.LogError("[Trainer] Applying head to LiveClassifier failed: " + e);
            }
        }

        // 6) Fire event for UIs that are listening
        OnHeadTrained?.Invoke(saveHeadName, head);
    }

    public void LoadHeadAndApply()
    {
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

        ResetAll(head.classes);

        if (liveClassifier)
        {
            var display = Path.GetFileNameWithoutExtension(loadHeadName);
            liveClassifier.SetPreferredHead(display, enforce: true);
            liveClassifier.SetHead(head, display, force: true);
        }

        OnHeadTrained?.Invoke(loadHeadName, head);
        Debug.Log($"[Trainer] Head loaded & applied -> {full}");
    }

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

        if (liveClassifier)
        {
            var display = Path.GetFileNameWithoutExtension(file);
            liveClassifier.SetPreferredHead(display, enforce: true);
            liveClassifier.SetHead(head, display, force: true);
        }

        OnHeadTrained?.Invoke(file, head);
        Debug.Log($"[Trainer] Head loaded & applied -> {full}");
    }

    public event Action<string, HeadData> OnHeadTrained;

    // === Compatibility method stub expected by your UI ===
    public void DebugDump()
    {
        string cls = Classes != null ? string.Join(", ", Classes) : "(null)";
        Debug.Log($"[Trainer DebugDump]\n classes=[{cls}]\n dim={embeddingDim}\n currentClass={CurrentClassIndex}");
    }

    // ---------------- Internals ----------------

    void EnsureDefaultFilenames()
    {
        if (string.IsNullOrWhiteSpace(saveHeadName))
            saveHeadName = (string.IsNullOrWhiteSpace(sessionName) ? "Session1" : sessionName) + ".json";
        if (string.IsNullOrWhiteSpace(loadHeadName))
            loadHeadName = (string.IsNullOrWhiteSpace(sessionName) ? "Session1" : sessionName) + ".json";
    }

    float[] GetEmbedding()
    {
        if (embedder == null)
        {
            Debug.LogWarning("[Trainer] No embedder.");
            return null;
        }

        try
        {
            var mTex = typeof(SentisEmbedder).GetMethod("Embed", new Type[] { typeof(Texture) });
            if (mTex != null && sourceTexture != null)
            {
                var z = mTex.Invoke(embedder, new object[] { sourceTexture }) as float[];
                if (z != null && z.Length > 0) return z;
            }

            var m = typeof(SentisEmbedder).GetMethod("Embed", Type.EmptyTypes);
            if (m != null)
            {
                var z2 = m.Invoke(embedder, null) as float[];
                if (z2 != null && z2.Length > 0) return z2;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[Trainer] Embed threw: " + e.Message);
        }
        return null;
    }

    void EnsureTrainer(int dim)
    {
        if (dim <= 0) { Debug.LogWarning("[Trainer] Invalid embedding dim."); return; }
        if (trainer == null)
        {
            trainer = new OnlineCentroidTrainer(Classes.Count, dim);
            embeddingDim = dim;
            return;
        }

        if (embeddingDim != dim)
        {
            var tmp = new OnlineCentroidTrainer(Classes.Count, dim);
            trainer = tmp;
            embeddingDim = dim;
        }
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
        Destroy(tex);

        Directory.CreateDirectory(Path.Combine(PersistentPath, "records"));
        var full = Path.Combine(PersistentPath, "records", fileName);
        File.WriteAllBytes(full, scaled.EncodeToPNG());
        Destroy(scaled);

        Debug.Log("[Trainer] Saved snapshot -> " + full);
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
