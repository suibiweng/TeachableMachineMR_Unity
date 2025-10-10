// Assets/TinyTeachable/Runtime/LiveClassifier.cs
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[DisallowMultipleComponent]
public class LiveClassifier : MonoBehaviour
{
    [Header("Links")]
    public SentisEmbedder embedder;
    public Texture sourceTexture;
    public TextAsset defaultHeadJson;

    [Header("Runtime Controls")]
    public int classifyEvery = 2;
    public int smooth = 5;

    [Header("Console Debug")]
    public bool consoleDebug = true;
    public bool logPredictions = true;

    [Header("State (read-only)")]
    public string LastLabel { get; private set; } = "";
    public float  LastScore { get; private set; } = 0f;
    public string CurrentHeadName { get; private set; } = "";
    public bool   IsRunning { get; private set; } = true;

    // === Head enforcement ===
    [Header("Head Enforcement")]
    [Tooltip("If true, SetHead() calls with a different name will be ignored unless forced.")]
    public bool EnforcePreferredHead = false;

    [Tooltip("When EnforcePreferredHead is true, only this head name will be accepted.")]
    public string PreferredHeadName = "";

    public event Action<string, float> OnPrediction;
    public event Action<HeadData> OnHeadChanged;

    private HeadData head;
    private int frameIdx;
    private readonly Queue<int> smoothQ = new();
    private string _lastSkipPrinted = "";
    private int _ticks = 0;

    void Start()
    {
        if (defaultHeadJson != null && !string.IsNullOrEmpty(defaultHeadJson.text) && !IsHeadReady())
        {
            try
            {
                var h = JsonUtility.FromJson<HeadData>(defaultHeadJson.text);
                if (HeadLooksUsable(h)) { SetHead(h, "(default TextAsset)", force:false); DLog("[LC] Loaded default head TextAsset."); }
                else DWarn("[LC] defaultHeadJson invalid.");
            }
            catch (Exception e) { DErr("[LC] defaultHeadJson parse error: " + e.Message); }
        }
    }

    void Update()
    {
        if (!IsRunning) { Skip("IsRunning=false"); return; }
        if (classifyEvery < 1) classifyEvery = 1;
        frameIdx++;
        if ((frameIdx % classifyEvery) != 0) { Skip("throttled"); return; }
        if (!IsHeadReady()) { Skip("no head"); return; }
        if (embedder == null) { Skip("embedder not assigned"); return; }

        var z = GetEmbedding();
        int zLen = z != null ? z.Length : 0;
        if (zLen == 0) { Skip("no embedding"); return; }

        var (idx, score) = Score(z);
        if (idx < 0) { Skip("score idx < 0"); return; }

        if (smooth > 0)
        {
            smoothQ.Enqueue(idx);
            while (smoothQ.Count > smooth) smoothQ.Dequeue();
            idx = MajorityIndex(smoothQ);
        }

        string label = (head.classes != null && idx >= 0 && idx < head.classes.Length) ? head.classes[idx] : idx.ToString();

        LastLabel = label;
        LastScore = score;
        _ticks++;

        if (logPredictions)
            DLog($"[LC] ✓ Pred #{_ticks}  head='{CurrentHeadName}'  idx={idx}  label='{label}'  score={score:0.000}  embLen={zLen}  classes=[{ClassesPreview(head)}]");

        OnPrediction?.Invoke(label, score);
        _lastSkipPrinted = "";
    }

    // ------------- Embedding -------------
    float[] GetEmbedding()
    {
        try
        {
            if (sourceTexture != null)
            {
                var m = typeof(SentisEmbedder).GetMethod("Embed", new[] { typeof(Texture) });
                if (m != null)
                {
                    var res = m.Invoke(embedder, new object[] { sourceTexture }) as float[];
                    if (res != null && res.Length > 0) return res;
                    Skip("Embed(Texture) returned null/empty.");
                }
            }
            else Skip("sourceTexture not assigned.");
        }
        catch (Exception e) { DErr("Embed(Texture) threw: " + e.Message); }

        try
        {
            var m = typeof(SentisEmbedder).GetMethod("Embed", Type.EmptyTypes);
            if (m != null)
            {
                var res = m.Invoke(embedder, null) as float[];
                if (res != null && res.Length > 0) return res;
                Skip("Embed() returned null/empty.");
            }
        }
        catch (Exception e) { DErr("Embed() threw: " + e.Message); }

        try
        {
            var m = typeof(SentisEmbedder).GetMethod("GetLastEmbedding", Type.EmptyTypes);
            if (m != null)
            {
                var res = m.Invoke(embedder, null) as float[];
                if (res != null && res.Length > 0) return res;
                Skip("GetLastEmbedding() returned null/empty.");
            }
        }
        catch (Exception e) { DErr("GetLastEmbedding() threw: " + e.Message); }

        foreach (var pname in new[] { "LastEmbedding", "LatestEmbedding", "lastEmbedding", "Embedding", "embedding" })
        {
            try
            {
                var p = typeof(SentisEmbedder).GetProperty(pname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null)
                {
                    var arr = p.GetValue(embedder) as float[];
                    if (arr != null && arr.Length > 0) return arr;
                }
                var f = typeof(SentisEmbedder).GetField(pname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var arr = f.GetValue(embedder) as float[];
                    if (arr != null && arr.Length > 0) return arr;
                }
            }
            catch (Exception e) { DErr($"Read {pname} threw: " + e.Message); }
        }
        return null;
    }

    // ------------- Scoring -------------
    (int, float) Score(float[] z)
    {
        if (head == null) return (-1, float.NegativeInfinity);
        if (head.type == "centroid" && head.centroids != null) { L2NormalizeInPlace(z); return ScoreCentroidLocal(head, z); }
        if (head.type == "linear" && head.W != null) return ScoreLinearLocal(head, z);
        return (-1, float.NegativeInfinity);
    }

    static (int, float) ScoreCentroidLocal(HeadData h, float[] zNorm)
    {
        int best = -1; float bestScore = float.NegativeInfinity;
        var cents = h.centroids; if (cents == null) return (best, bestScore);
        for (int c = 0; c < cents.Length; c++)
        {
            var cvec = cents[c]; if (cvec == null) continue;
            float s = Dot(zNorm, cvec);
            if (s > bestScore) { bestScore = s; best = c; }
        }
        return (best, bestScore);
    }

    static (int, float) ScoreLinearLocal(HeadData h, float[] z)
    {
        var W = h.W; if (W == null || z == null) return (-1, float.NegativeInfinity);
        int D = W.GetLength(0), C = W.GetLength(1), dim = Math.Min(D, z.Length);
        int best = -1; float bestScore = float.NegativeInfinity;
        for (int c = 0; c < C; c++)
        {
            double s = 0.0;
            for (int i = 0; i < dim; i++) s += (double)W[i, c] * z[i];
            float sf = (float)s;
            if (sf > bestScore) { bestScore = sf; best = c; }
        }
        return (best, bestScore);
    }

    static void L2NormalizeInPlace(float[] v)
    {
        if (v == null || v.Length == 0) return;
        double sumSq = 0.0; for (int i = 0; i < v.Length; i++) sumSq += (double)v[i] * v[i];
        double inv = sumSq > 1e-12 ? 1.0 / Math.Sqrt(sumSq) : 0.0;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] * inv);
    }
    static float Dot(float[] a, float[] b) { int n = Math.Min(a?.Length ?? 0, b?.Length ?? 0); double s = 0.0; for (int i = 0; i < n; i++) s += (double)a[i] * (double)b[i]; return (float)s; }
    static int MajorityIndex(Queue<int> q) { var m = new Dictionary<int,int>(); foreach (var v in q) { if (!m.ContainsKey(v)) m[v]=0; m[v]++; } int best=-1,bc=-1; foreach (var kv in m) if (kv.Value>bc){bc=kv.Value;best=kv.Key;} return best; }

    // ------------- Head management & enforcement -------------

    public bool IsHeadReady() => HeadLooksUsable(head);
    static bool HeadLooksUsable(HeadData h)
    {
        if (h == null) return false;
        if (h.classes == null || h.classes.Length == 0) return false;
        if (h.type == "centroid") return h.centroids != null && h.centroids.Length == h.classes.Length;
        if (h.type == "linear")   return h.W != null && h.W.GetLength(1) == h.classes.Length;
        return false;
    }

    // Lock/unlock desired head
    public void SetPreferredHead(string name, bool enforce = true)
    {
        PreferredHeadName = name ?? "";
        EnforcePreferredHead = enforce;
        DLog($"[LC] PreferredHead set to '{PreferredHeadName}', enforce={EnforcePreferredHead}");
    }
    public void ClearPreferredHead() => SetPreferredHead("", false);

    public void SetHead(HeadData h) => SetHead(h, CurrentHeadName, force:false);

    public void SetHead(HeadData h, string displayName, bool force = false)
    {
        // Enforce preferred head name unless forced
        if (EnforcePreferredHead && !force)
        {
            var nameToApply = displayName ?? "";
            if (!string.IsNullOrEmpty(PreferredHeadName) &&
                !string.Equals(nameToApply, PreferredHeadName, StringComparison.Ordinal))
            {
                DWarn($"[LC] SetHead ignored (enforced='{PreferredHeadName}', attempted='{nameToApply}')");
                return;
            }
        }

        if (!HeadLooksUsable(h)) { DWarn("[LC] SetHead skipped: head null/invalid."); return; }

        head = h;
        CurrentHeadName = displayName ?? CurrentHeadName;

        if (head.type == "centroid" && head.centroids != null)
            for (int c = 0; c < head.centroids.Length; c++)
                if (head.centroids[c] != null) L2NormalizeInPlace(head.centroids[c]);

        LastLabel = ""; LastScore = 0f; smoothQ.Clear();
        OnPrediction?.Invoke("", 0f);
        OnHeadChanged?.Invoke(head);

        int clsCount = (head.classes != null) ? head.classes.Length : 0;
        int dim = -1;
        if (head.type == "centroid") dim = (head.centroids != null && head.centroids.Length > 0 && head.centroids[0] != null) ? head.centroids[0].Length : -1;
        else if (head.type == "linear" && head.W != null) dim = head.W.GetLength(0);

        DLog($"[LC] Head set: name='{CurrentHeadName}', type={head.type}, dim={dim}, classes={clsCount}, classes=[{ClassesPreview(head)}]");
    }

    public void LoadHeadFromPath(string path, bool force = false)
    {
        if (!File.Exists(path)) { DErr("[LC] Head file missing: " + path); return; }
        try
        {
            var json = File.ReadAllText(path);
            var h = JsonUtility.FromJson<HeadData>(json);
            var display = Path.GetFileNameWithoutExtension(path);
            SetHead(h, display, force);
            DLog($"[LC] Loaded head from path: '{path}', name='{CurrentHeadName}' (force={force})");
        }
        catch (Exception e) { DErr("[LC] LoadHeadFromPath error: " + e.Message); }
    }

    public void Clear()
    {
        head = null; CurrentHeadName = ""; LastLabel = ""; LastScore = 0f; smoothQ.Clear();
        OnPrediction?.Invoke("", 0f); OnHeadChanged?.Invoke(null);
        DLog("[LC] Cleared head and predictions.");
    }

    public void StartClassifying() => SetRunning(true);
    public void StopClassifying()  => SetRunning(false);
    public void SetRunning(bool run) { IsRunning = run; DLog($"[LC] SetRunning({run})"); }

    public void ResetPrediction() { LastLabel = ""; LastScore = 0f; OnPrediction?.Invoke("", 0f); }

    // ------------- Logging helpers -------------
    void Skip(string reason)
    {
        if (consoleDebug && reason != _lastSkipPrinted)
        {
            Debug.Log($"[LC] Skip: {reason} (headReady={IsHeadReady()}, embedder={(embedder!=null)}, srcTex={(sourceTexture!=null)})");
            _lastSkipPrinted = reason;
        }
    }
    void DLog(string msg)  { if (consoleDebug) Debug.Log(msg); }
    void DWarn(string msg) { if (consoleDebug) Debug.LogWarning(msg); }
    void DErr(string msg)  { Debug.LogError(msg); }

    static string ClassesPreview(HeadData h) => (h == null || h.classes == null) ? "(null)" : string.Join(", ", h.classes);
}
