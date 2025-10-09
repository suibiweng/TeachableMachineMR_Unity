using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class LiveClassifier : MonoBehaviour
{
    [Header("Links")]
    public SentisEmbedder embedder;     // assign in Inspector
    public Texture sourceTexture;       // 224x224 RT (webcam/passthrough)
    public TextAsset defaultHeadJson;   // optional: start with a head

    [Header("Frequency & Smoothing")]
    [Tooltip("Run inference every N frames (>=1).")]
    public int classifyEvery = 2;       // run every N frames
    [Tooltip("Majority window (0 = off). Keeps last K predicted labels and votes the most common).")]
    public int smooth = 5;              // majority window (0 = off)

    // UI / other systems can subscribe to this
    public event Action<string, float> OnPrediction;
    public event Action<HeadData> OnHeadChanged;

    [Header("Read-only")]
    [SerializeField] private string currentHeadName = "(none)";
    public string CurrentHeadName => currentHeadName;

    public string LastLabel { get; private set; } = "";
    public float  LastScore { get; private set; } = 0f;

    // State
    private HeadData head;
    private int frameGate = 0;
    private Queue<string> recentLabels;   // for smoothing

    void Awake()
    {
        if (smooth > 0) recentLabels = new Queue<string>(Mathf.Max(1, smooth));
        if (classifyEvery < 1) classifyEvery = 1;
    }

    void Start()
    {
        // Optional default head (only if nothing else loaded one first)
        if (!IsHeadReady() && defaultHeadJson != null && !string.IsNullOrEmpty(defaultHeadJson.text))
        {
            try
            {
                var h = JsonUtility.FromJson<HeadData>(defaultHeadJson.text);
                if (HeadLooksUsable(h))
                {
                    SetHead(h, "(default)");
                    Debug.Log("[LiveClassifier] Loaded default head.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LiveClassifier] Failed to parse default head: " + e.Message);
            }
        }
    }

    void Update()
    {
        if (embedder == null || sourceTexture == null || !IsHeadReady()) return;
        if ((frameGate++ % Mathf.Max(1, classifyEvery)) != 0) return;

        var z = embedder.Embed(sourceTexture);
        if (z == null || z.Length == 0) return;

        // Score using TinyHeads helpers that exist: PredictCentroid/PredictLinear
        int idx = -1; float score = 0f;
        if (head.type == "centroid")
        {
            (idx, score) = TinyHeads.PredictCentroid(z, head);
        }
        else if (head.type == "linear")
        {
            (idx, score) = TinyHeads.PredictLinear(z, head);
        }
        else
        {
            Debug.LogWarning("[LiveClassifier] Unknown head.type: " + head.type);
            return;
        }

        if (head.classes == null || head.classes.Length == 0 || idx < 0) return;
        idx = Mathf.Clamp(idx, 0, head.classes.Length - 1);
        string label = head.classes[idx];

        // Optional smoothing (majority voting)
        if (smooth > 0)
        {
            if (recentLabels == null) recentLabels = new Queue<string>(smooth);
            recentLabels.Enqueue(label);
            while (recentLabels.Count > smooth) recentLabels.Dequeue();
            label = Majority(recentLabels);
        }

        LastLabel = label;
        LastScore = score;
        OnPrediction?.Invoke(label, score);
    }

    // ------------------- Public API -------------------

    public void SetHead(HeadData newHead, string nameOverride = null)
    {
        if (!HeadLooksUsable(newHead))
        {
            Debug.LogWarning("[LiveClassifier] SetHead rejected: invalid head.");
            return;
        }

        head = newHead;
        if (!string.IsNullOrEmpty(nameOverride)) currentHeadName = nameOverride;

        // Reset smoothing window so we don't mix old labels with new head
        if (recentLabels != null) recentLabels.Clear();

        OnHeadChanged?.Invoke(head);
        Debug.Log($"[LiveClassifier] Head set: type={head.type}, classes={head.classes?.Length ?? 0}");
    }

    /// Load head JSON text (already read) and apply.
    public void LoadHeadFromJson(string json, string nameHint = null)
    {
        try
        {
            var h = JsonUtility.FromJson<HeadData>(json);
            if (!HeadLooksUsable(h)) { Debug.LogWarning("[LiveClassifier] LoadHeadFromJson invalid."); return; }
            currentHeadName = string.IsNullOrEmpty(nameHint) ? currentHeadName : nameHint;
            SetHead(h, currentHeadName);
        }
        catch (Exception e)
        {
            Debug.LogError("[LiveClassifier] LoadHeadFromJson failed: " + e.Message);
        }
    }

    /// Load head from a file path and apply.
    public void LoadHeadFromPath(string fullPath)
    {
        try
        {
            if (!System.IO.File.Exists(fullPath)) { Debug.LogError("[LiveClassifier] Head path missing: " + fullPath); return; }
            var json = System.IO.File.ReadAllText(fullPath);
            var nameHint = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            LoadHeadFromJson(json, nameHint);
        }
        catch (Exception e)
        {
            Debug.LogError("[LiveClassifier] LoadHeadFromPath failed: " + e.Message);
        }
    }

    /// Used by other scripts (e.g., HeadSwitcherDropdown) to check readiness.
    public bool IsHeadReady()
    {
        return HeadLooksUsable(head);
    }

    // ------------------- Helpers -------------------

    private bool HeadLooksUsable(HeadData h)
    {
        if (h == null || h.classes == null || h.classes.Length == 0) return false;
        if (h.type == "centroid")
            return h.centroids != null && h.centroids.Length == h.classes.Length && h.centroids[0] != null && h.centroids[0].Length > 0;
        if (h.type == "linear")
            return h.W != null && h.W.GetLength(1) == h.classes.Length && h.W.GetLength(0) > 0;
        return false;
    }

    private static string Majority(IEnumerable<string> labels)
    {
        string best = null; int bestCount = -1;
        var map = new Dictionary<string, int>();
        foreach (var l in labels)
        {
            if (string.IsNullOrEmpty(l)) continue;
            map.TryGetValue(l, out var c); c++; map[l] = c;
            if (c > bestCount) { bestCount = c; best = l; }
        }
        return best ?? "";
    }
}
