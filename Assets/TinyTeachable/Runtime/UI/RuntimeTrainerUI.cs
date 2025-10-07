using UnityEngine;
using System;
using System.IO;

public class RuntimeTrainerUI : MonoBehaviour
{
    [Header("Inputs")]
    public SentisEmbedder embedder;
    public Texture sourceTexture;
    public string[] classNames = new string[] { "class_A", "class_B" };

    [Header("Attach (optional)")]
    public LiveClassifier liveClassifier; // will receive trained head

    [Header("UI")]
    public bool showOnGUI = true;

    private OnlineCentroidTrainer trainer;
    private int currentClass = 0;
    private int embeddingDim = -1;
    private string saveHeadName = "head_runtime.json";
    private string loadHeadName = "head_runtime.json";
    private string sessionName = "session1";

    string PD => Application.persistentDataPath;

    void Start() {
        Debug.Log("PersistentDataPath: " + PD);
    }

    float[] GetEmbedding() {
        if (embedder == null || sourceTexture == null) return null;
        var z = embedder.Embed(sourceTexture);
        if (embeddingDim < 0) {
            embeddingDim = z.Length;
            trainer = new OnlineCentroidTrainer(classNames.Length, embeddingDim);
        }
        return z;
    }

    void AddSample() {
        var z = GetEmbedding();
        if (z == null) { Debug.LogWarning("No embedding available."); return; }
        trainer.AddSample(currentClass, z);
        AppendEmbeddingCSV(z, currentClass, sessionName);
        Debug.Log($"Added sample to {classNames[currentClass]} (dim={z.Length}) count={trainer.GetCount(currentClass)}");
    }

    void TrainAndSave() {
        if (trainer == null) { Debug.LogWarning("Collect some samples first."); return; }
        var head = trainer.ToHeadData(classNames);
        SaveHead(head, saveHeadName);
        if (liveClassifier != null) liveClassifier.SetHead(head);
    }

    void LoadHead() {
        var path = Path.Combine(PD, "heads", loadHeadName);
        if (!File.Exists(path)) { Debug.LogError("Missing head: " + path); return; }
        var json = File.ReadAllText(path);
        var head = JsonUtility.FromJson<HeadData>(json);
        if (liveClassifier != null) liveClassifier.SetHead(head);
        Debug.Log("Loaded head -> " + path);
    }

    void SaveSnapshotPNG() {
        var tex = Capture(sourceTexture);
        if (tex == null) return;
        var bytes = tex.EncodeToPNG();
        Destroy(tex);
        var dir = Path.Combine(PD, "records");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{classNames[currentClass]}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        File.WriteAllBytes(file, bytes);
        Debug.Log("Saved PNG -> " + file);
    }

    Texture2D Capture(Texture src) {
        var rt = src as RenderTexture;
        if (rt == null) { Debug.LogError("sourceTexture must be a RenderTexture"); return null; }
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0,0,rt.width,rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        return tex;
    }

    void SaveHead(HeadData head, string filename) {
        var dir = Path.Combine(PD, "heads");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, filename);
        var json = JsonUtility.ToJson(head, true);
        File.WriteAllText(full, json);
        Debug.Log("Saved head -> " + full);
    }

    void AppendEmbeddingCSV(float[] z, int cls, string baseName) {
        var dir = Path.Combine(PD, "records");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, baseName + ".csv");
        using (var sw = new StreamWriter(full, append:true)) {
            sw.Write(cls);
            for (int i=0;i<z.Length;i++) {
                sw.Write("," + z[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sw.WriteLine();
        }
    }

    void NextClass(int delta) {
        currentClass = (currentClass + delta + classNames.Length) % classNames.Length;
    }

    void OnGUI() {
        if (!showOnGUI) return;
        int x=10, y=40, w=340, h=28, pad=6;

        GUI.Label(new Rect(x, 10, 1000, 24), $"PersistentDataPath: {PD}");

        GUI.Label(new Rect(x, y, w, h), "Current Class:");
        if (GUI.Button(new Rect(x+110, y, 30, h), "<")) NextClass(-1);
        GUI.Label(new Rect(x+145, y, 150, h), classNames[currentClass]);
        if (GUI.Button(new Rect(x+300, y, 30, h), ">")) NextClass(+1);
        y += h + pad;

        if (GUI.Button(new Rect(x, y, w, h), "Add Sample (embedding)")) { AddSample(); }
        y += h + pad;

        if (GUI.Button(new Rect(x, y, w, h), "Save Snapshot PNG")) { SaveSnapshotPNG(); }
        y += h + pad;

        GUI.Label(new Rect(x, y, w, h), "Session name (CSV):");
        sessionName = GUI.TextField(new Rect(x+170, y, 160, h), sessionName);
        y += h + pad;

        GUI.Label(new Rect(x, y, w, h), "Save Head Name:");
        saveHeadName = GUI.TextField(new Rect(x+170, y, 160, h), saveHeadName);
        y += h + pad;
        if (GUI.Button(new Rect(x, y, w, h), "Train & Save Head (+apply)")) { TrainAndSave(); }
        y += h + pad;

        GUI.Label(new Rect(x, y, w, h), "Load Head Name:");
        loadHeadName = GUI.TextField(new Rect(x+170, y, 160, h), loadHeadName);
        y += h + pad;
        if (GUI.Button(new Rect(x, y, w, h), "Load Head from persistentDataPath")) { LoadHead(); }
        y += h + pad;

        GUI.Label(new Rect(x, y, 600, h), $"Embedding dim: {(embeddingDim>0?embeddingDim:0)}  Counts: " + string.Join(", ", GetCounts()));
    }

    string[] GetCounts() {
        if (trainer == null) return new string[classNames.Length];
        var arr = new string[classNames.Length];
        for (int i=0;i<classNames.Length;i++) arr[i] = classNames[i] + ":" + trainer.GetCount(i);
        return arr;
    }
}
