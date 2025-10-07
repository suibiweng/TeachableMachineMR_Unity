using UnityEngine;
using System;
using System.IO;

public class LiveClassifier : MonoBehaviour
{
    [Header("Pipeline")]
    public SentisEmbedder embedder;       // set in Inspector
    public Texture sourceTexture;         // RT_224 or Passthrough RT
    public TextAsset defaultHeadJson;     // optional starting head

    [Header("Timing")]
    public int classifyEvery = 2;         // classify every N frames
    public int smooth = 5;                // majority vote window

    // Events to drive UI or logic
    public event Action<string, float> OnPrediction;  // (label, score)

    // Public read-only props for UI binders
    public string LastLabel { get; private set; } = "";
    public float LastScore  { get; private set; } = 0f;

    private HeadData head;
    private int frameIdx;
    private System.Collections.Generic.Queue<int> q = new System.Collections.Generic.Queue<int>();
    private bool warnedOnce = false;

    void Start()
    {
        if (defaultHeadJson != null)
            SetHead(JsonUtility.FromJson<HeadData>(defaultHeadJson.text));
    }

    public void SetHead(HeadData h)
    {
        head = h;
        if (head != null && head.type == "centroid" && head.centroids != null)
            for (int c = 0; c < head.centroids.Length; c++) TinyHeads.L2Normalize(head.centroids[c]);
    }

    public void LoadHeadFromPath(string path)
    {
        if (!File.Exists(path)) { Debug.LogError("Head not found: " + path); return; }
        SetHead(JsonUtility.FromJson<HeadData>(File.ReadAllText(path)));
        Debug.Log("Loaded head: " + path + " type=" + head.type);
    }

    bool IsHeadReady()
    {
        return head != null
            && head.type != null
            && head.classes != null && head.classes.Length > 0
            && ((head.type == "centroid" && head.centroids != null && head.centroids.Length == head.classes.Length)
                || (head.type == "linear" && head.W != null));
    }

    void Update()
    {
        if (embedder == null || sourceTexture == null) return;
        if (!IsHeadReady())
        {
            if (!warnedOnce && Time.frameCount % 60 == 0)
            {
                warnedOnce = true;
                Debug.LogWarning("LiveClassifier: head not set yet. Train & Save via your trainer UI or assign defaultHeadJson.");
            }
            return;
        }

        frameIdx++;
        if (frameIdx % Mathf.Max(1, classifyEvery) != 0) return;

        var z = embedder.Embed(sourceTexture);
        int idx; float score;
        if (head.type == "linear" && head.W != null) (idx, score) = TinyHeads.PredictLinear(z, head);
        else (idx, score) = TinyHeads.PredictCentroid(z, head);

        if (smooth > 0)
        {
            q.Enqueue(idx); while (q.Count > smooth) q.Dequeue();
            int best = idx, bestCnt = 0;
            var counts = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var k in q) { if (!counts.ContainsKey(k)) counts[k] = 0; counts[k]++; if (counts[k] > bestCnt) { bestCnt = counts[k]; best = k; } }
            idx = best;
        }

        string label = (head.classes != null && idx >= 0 && idx < head.classes.Length) ? head.classes[idx] : idx.ToString();
        LastLabel = label;
        LastScore = score;

        OnPrediction?.Invoke(label, score);
    }
}
