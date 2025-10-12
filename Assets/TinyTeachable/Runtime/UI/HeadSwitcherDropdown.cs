using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using TMPro;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class HeadSwitcherDropdown : MonoBehaviour
{
    [Header("Pipeline")]
    public LiveClassifier classifier;          // REQUIRED
    public DynamicClassTrainer trainer;        // optional (to refresh trainer UI after load)

    [Header("Heads (manual list)")]
    public List<HeadEntry> heads = new List<HeadEntry>();

    [Header("UI (assign one)")]
    public TMP_Dropdown tmpDropdown;           // TextMeshPro dropdown
    public Dropdown     uDropdown;             // Legacy UI dropdown

    [Header("Options")]
    public bool autoLoadFirstOnStart   = true;  // load index 0 at Start
    public bool scanHeadsFolderAtStart = true;  // scan /heads/*.json at Start
    public string scanPattern           = "*.json";
    public bool includeSubfolders       = false;

    [Tooltip("If true, we only call classifier.SetHead(head) and DO NOT touch trainer.\nGreat to ensure the head applies immediately and nothing else overwrites it.")]
    public bool applyToClassifierOnly   = true;

    [Tooltip("If true, when trainer saves a new head we auto-add it to the dropdown and select it.")]
    public bool listenForTrainerEvents  = true;

    [Tooltip("When handling trainer event, select the newly trained head.")]
    public bool selectTrainedHead       = true;

    [Serializable]
    public class HeadEntry { public string label; public string fileName; }

    string PD => Application.persistentDataPath;

    void OnEnable()
    {
        if (listenForTrainerEvents && trainer != null)
            trainer.OnHeadTrained += HandleHeadTrained;
    }

    void OnDisable()
    {
        if (trainer != null)
            trainer.OnHeadTrained -= HandleHeadTrained;
    }

    void Start()
    {
        if (classifier == null)
        {
            Debug.LogError("[HeadSwitcher] No LiveClassifier assigned.");
            enabled = false; return;
        }

        if (scanHeadsFolderAtStart) ScanHeadsFolder();
        BuildDropdown();
        HookDropdownEvents();

        if (autoLoadFirstOnStart && heads.Count > 0)
            LoadByIndex(0);
    }

    // ------- Trainer event: invoked after Train & Save -------
    void HandleHeadTrained(string fileName, HeadData head)
    {
        var path = Path.Combine(PD, "heads", fileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning("[HeadSwitcher] Trained head not found on disk: " + path);
            return;
        }

        string label = Path.GetFileNameWithoutExtension(fileName);
        int idx = FindIndexByFile(fileName);
        if (idx < 0) heads.Add(new HeadEntry { label = label, fileName = fileName });
        else heads[idx].label = label;

        BuildDropdown();

        if (selectTrainedHead)
        {
            int i = FindIndexByFile(fileName);
            if (i >= 0) LoadByIndex(i);
        }
    }

    // --------------------- Public API ---------------------
    public void RefreshListAndUI()
    {
        ScanHeadsFolder();
        BuildDropdown();
    }

    public void LoadByLabel(string label)
    {
        int i = heads.FindIndex(h => string.Equals(h.label, label, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) LoadByIndex(i);
        else Debug.LogError($"[HeadSwitcher] Label not found: {label}");
    }

    public void LoadByIndex(int i)
    {
        if (i < 0 || i >= heads.Count) { Debug.LogWarning("[HeadSwitcher] Bad index " + i); return; }
        var entry = heads[i];
        LoadHead(entry.fileName);

        if (tmpDropdown) { tmpDropdown.SetValueWithoutNotify(i); tmpDropdown.RefreshShownValue(); }
        if (uDropdown)   { uDropdown.SetValueWithoutNotify(i);   uDropdown.RefreshShownValue();   }
    }

    // --------------------- Internals ----------------------
    void HookDropdownEvents()
    {
        if (tmpDropdown)
        {
            tmpDropdown.onValueChanged.RemoveAllListeners();
            tmpDropdown.onValueChanged.AddListener(i =>
            {
                Debug.Log("[HeadSwitcher] TMP onValueChanged -> " + i);
                LoadByIndex(i);
            });
        }
        else if (uDropdown)
        {
            uDropdown.onValueChanged.RemoveAllListeners();
            uDropdown.onValueChanged.AddListener(i =>
            {
                Debug.Log("[HeadSwitcher] UGUI onValueChanged -> " + i);
                LoadByIndex(i);
            });
        }
        else
        {
            Debug.LogWarning("[HeadSwitcher] No dropdown assigned (TMP or legacy).");
        }
    }

    void BuildDropdown()
    {
        var labels = new List<string>(heads.Count);
        foreach (var h in heads) labels.Add(h.label);

        if (tmpDropdown)
        {
            tmpDropdown.ClearOptions();
            tmpDropdown.AddOptions(labels);
            tmpDropdown.RefreshShownValue();
            Debug.Log("[HeadSwitcher] TMP options: " + string.Join(", ", labels));
        }
        else if (uDropdown)
        {
            uDropdown.ClearOptions();
            uDropdown.AddOptions(labels);
            uDropdown.RefreshShownValue();
            Debug.Log("[HeadSwitcher] UGUI options: " + string.Join(", ", labels));
        }
    }

    void LoadHead(string fileName)
    {
        var path = Path.Combine(PD, "heads", fileName);
        if (!File.Exists(path))
        {
            Debug.LogError("[HeadSwitcher] Head not found: " + path);
            return;
        }

        var json = File.ReadAllText(path);
        var head = JsonUtility.FromJson<HeadData>(json);

        // sanity check
        if (!HeadLooksUsable(head))
        {
            Debug.LogError("[HeadSwitcher] Head JSON invalid or empty: " + fileName);
            return;
        }

        if (applyToClassifierOnly || trainer == null)
        {
            classifier.SetHead(head);
            Debug.Log($"[HeadSwitcher] Applied to classifier ONLY -> {fileName}");
        }
        else
        {
            // sync trainer classes for UI, then apply to classifier
            // if (head.classes != null) trainer.ResetAll(new List<string>(head.classes));
            classifier.SetHead(head);
            Debug.Log($"[HeadSwitcher] Loaded (trainer+classifier) -> {fileName}");
        }

        // Confirm live head set
        if (!classifier.IsHeadReady())
            Debug.LogWarning("[HeadSwitcher] Warning: classifier reports head not ready after SetHead.");

        // Log quick summary
        int dim = -1;
        if (head.type == "centroid" && head.centroids != null && head.centroids.Length > 0 && head.centroids[0] != null)
            dim = head.centroids[0].Length;
        else if (head.type == "linear" && head.W != null)
            dim = head.W.GetLength(0);

        Debug.Log($"[HeadSwitcher] Head summary -> type={head.type}, dim={dim}, classes={(head.classes!=null?head.classes.Length:0)}");
    }

    void ScanHeadsFolder()
    {
        var dir = Path.Combine(PD, "heads");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var files = Directory.GetFiles(
            dir, scanPattern,
            includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        heads.Clear();
        foreach (var f in files)
        {
            var nameNoExt = Path.GetFileNameWithoutExtension(f);
            var file      = Path.GetFileName(f);
            heads.Add(new HeadEntry { label = nameNoExt, fileName = file });
        }
        heads.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));

        Debug.Log($"[HeadSwitcher] Scan -> found {heads.Count} head(s) in {dir}");
    }

    int FindIndexByFile(string fileName)
    {
        for (int i = 0; i < heads.Count; i++)
            if (string.Equals(heads[i].fileName, fileName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    bool HeadLooksUsable(HeadData h)
    {
        if (h == null || h.classes == null || h.classes.Length == 0) return false;
        if (h.type == "centroid")
            return h.centroids != null && h.centroids.Length == h.classes.Length && h.centroids[0] != null && h.centroids[0].Length > 0;
        if (h.type == "linear")
            return h.W != null && h.W.GetLength(1) == h.classes.Length && h.W.GetLength(0) > 0;
        return false;
    }
}
