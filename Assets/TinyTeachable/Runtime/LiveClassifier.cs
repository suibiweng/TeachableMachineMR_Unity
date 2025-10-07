using System;
using UnityEngine;

[DisallowMultipleComponent]
public class LiveClassifier : MonoBehaviour
{
    [Header("Links")]
    public SentisEmbedder embedder;     // assign in Inspector
    public Texture sourceTexture;       // 224x224 RT (webcam/passthrough)
    public TextAsset defaultHeadJson;   // optional: start with a head

    [Header("Frequency & Smoothing")]
    public int classifyEvery = 2;       // run every N frames
    public int smooth = 5;              // majority window (0 = off)

    // UI / other systems can subscribe to this
    public event Action<string, float> OnPrediction;

    // Latest output
    public string LastLabel { get; private set; } = "";
    public float  LastScore { get; private set; } = 0f;

    // For UI
    public HeadData CurrentHead => head;

    // internal
    private HeadData head;
    private int frameIdx;
    private System.Collections.Generic.Queue<int> smoothQ = new System.Collections.Generic.Queue<int>();

    void Start()
    {
        // Only load default if we do NOT already have a valid head (e.g. HeadSwitcher/AutoLoader may set first)
        if (defaultHeadJson != null && !string.IsNullOrEmpty(defaultHeadJson.text))
        {
            if (!IsHeadReady())
            {
                var h = JsonUtility.FromJson<HeadData>(defaultHeadJson.text);
                if (HeadLooksUsable(h))
                {
                    SetHead(h);
                    Debug.Log("[LiveClassifier] Loaded default head from TextAsset.");
                }
                else
                {
                    Debug.LogWarning("[LiveClassifier] defaultHeadJson is empty/invalid; skipping.");
                }
            }
        }
    }

    // Guard: check if head has usable data
    private bool HeadLooksUsable(HeadData h)
    {
        if (h == null || h.classes == null || h.classes.Length == 0) return false;
        if (h.type == "centroid")
            return h.centroids != null && h.centroids.Length == h.classes.Length && h.centroids[0] != null && h.centroids[0].Length > 0;
        if (h.type == "linear")
            return h.W != null && h.W.GetLength(0) > 0 && h.W.GetLength(1) == h.classes.Length;
        return false;
    }

    /// <summary>Apply a new head. For centroid heads, centroids are L2-normalized. Skips empty heads.</summary>
    public void SetHead(HeadData h)
    {
        if (!HeadLooksUsable(h))
        {
            Debug.LogWarning("[LiveClassifier] SetHead skipped: head is null/empty.");
            return;
        }

        head = h;

        if (head.type == "centroid" && head.centroids != null)
        {
            for (int c = 0; c < head.centroids.Length; c++)
                TinyHeads.L2Normalize(head.centroids[c]);
        }

        LastLabel = ""; LastScore = 0f; smoothQ.Clear();

        int clsCount = (head.classes != null) ? head.classes.Length : 0;
        int dim = -1;
        if (head.type == "centroid")
            dim = (head.centroids != null && head.centroids.Length > 0 && head.centroids[0] != null) ? head.centroids[0].Length : -1;
        else if (head.type == "linear" && head.W != null)
            dim = head.W.GetLength(0); // W[D,C]

        Debug.Log($"[LiveClassifier] Head set: type={head.type}, dim={dim}, classes={clsCount}");
    }

    /// <summary>Load head JSON from an absolute path and apply if valid.</summary>
    public void LoadHeadFromPath(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            Debug.LogError("[LiveClassifier] Head file missing: " + path);
            return;
        }
        var json = System.IO.File.ReadAllText(path);
        var h = JsonUtility.FromJson<HeadData>(json);
        SetHead(h);
        Debug.Log("[LiveClassifier] Loaded head from: " + path);
    }

    /// <summary>Returns true if a usable head is present.</summary>
    public bool IsHeadReady()
    {
        return HeadLooksUsable(head);
    }

    void Update()
    {
        if (embedder == null || sourceTexture == null) return;
        if (!IsHeadReady()) return;

        frameIdx++;
        if (frameIdx % Mathf.Max(1, classifyEvery) != 0) return;

        // 1) Embed
        var z = embedder.Embed(sourceTexture);
        if (z == null || z.Length == 0) return;

        // 2) Predict
        int idx; float score;
        if (head.type == "centroid")
        {
            TinyHeads.L2Normalize(z);
            (idx, score) = TinyHeads.PredictCentroid(z, head);  // (bestIndex, cosineScore)
        }
        else // "linear"
        {
            (idx, score) = TinyHeads.PredictLinear(z, head);    // (bestIndex, softmaxProb) with W[D,C]
        }

        // 3) Optional temporal smoothing (majority vote)
        if (smooth > 0)
        {
            smoothQ.Enqueue(idx);
            while (smoothQ.Count > smooth) smoothQ.Dequeue();

            int best = idx, bestCnt = 0;
            var counts = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var k in smoothQ)
            {
                if (!counts.ContainsKey(k)) counts[k] = 0;
                counts[k]++;
                if (counts[k] > bestCnt) { bestCnt = counts[k]; best = k; }
            }
            idx = best;
        }

        string label = (head.classes != null && idx >= 0 && idx < head.classes.Length) ? head.classes[idx] : idx.ToString();

        LastLabel = label;
        LastScore = score;

        OnPrediction?.Invoke(label, score);

        if (Time.frameCount % 15 == 0)
            Debug.Log($"[LiveClassifier] Prediction: {label} ({score:0.00})");
    }
}
