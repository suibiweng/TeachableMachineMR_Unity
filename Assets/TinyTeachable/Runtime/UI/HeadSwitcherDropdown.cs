// Assets/TinyTeachable/Runtime/HeadSwitcherDropdown.cs
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class HeadSwitcherDropdown : MonoBehaviour
{
    [Header("Pipeline")]
    public LiveClassifier classifier;          // assign your LiveClassifier
    public DynamicClassTrainer trainer;        // assign to listen for OnHeadTrained

    [Header("Heads (manual list)")]
    public List<HeadEntry> heads = new List<HeadEntry>
    {
        // Filenames must exist under Application.persistentDataPath/heads/
        new HeadEntry { label = "Eyes",     fileName = "eyes_v1.json" },
        new HeadEntry { label = "CupTouch", fileName = "cup_touch_v1.json" }
    };

    [Header("UI (assign one)")]
    public TMP_Dropdown tmpDropdown;           // TextMeshPro dropdown
    public Dropdown     uDropdown;             // Legacy UI dropdown

    [Header("Options")]
    public bool autoLoadFirstOnStart   = true;  // on Start, load first item
    public bool scanHeadsFolderAtStart = false; // scan /heads/*.json on Start
    public string scanPattern           = "*.json";
    public bool includeSubfolders       = false;
    public bool showModelNameInLabel    = false; // append current embedder name
    public bool listenForTrainerEvents  = true;  // auto-refresh when trainer saves
    public bool selectTrainedHead       = true;  // select the newly-trained head

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
        if (scanHeadsFolderAtStart) ScanHeadsFolder();
        BuildDropdown();
        HookDropdownEvents();

        if (autoLoadFirstOnStart && heads.Count > 0)
            LoadByIndex(0);
    }

    // -------- Trainer event: called after Train & Save --------
    void HandleHeadTrained(string fileName, HeadData head)
    {
        // Ensure it exists in /heads/
        var path = Path.Combine(PD, "heads", fileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning("[HeadSwitcher] Trained head file not found at: " + path);
            return;
        }

        // Add/update entry in list
        string label = Path.GetFileNameWithoutExtension(fileName);
        int idx = FindIndexByFile(fileName);
        if (idx < 0) heads.Add(new HeadEntry { label = label, fileName = fileName });
        else heads[idx].label = label; // keep label in sync

        // Rebuild UI
        BuildDropdown();

        // Select & load the freshly trained head
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
        if (i < 0 || i >= heads.Count) return;
        var entry = heads[i];
        LoadHead(entry.fileName);

        // keep UI selection in sync if changed programmatically
        if (tmpDropdown) { tmpDropdown.SetValueWithoutNotify(i); tmpDropdown.RefreshShownValue(); }
        if (uDropdown)   { uDropdown.SetValueWithoutNotify(i);   uDropdown.RefreshShownValue();   }
    }

    // --------------------- Internals ----------------------
    void HookDropdownEvents()
    {
        if (tmpDropdown)
        {
            tmpDropdown.onValueChanged.RemoveAllListeners();
            tmpDropdown.onValueChanged.AddListener(LoadByIndex);
        }
        else if (uDropdown)
        {
            uDropdown.onValueChanged.RemoveAllListeners();
            uDropdown.onValueChanged.AddListener(LoadByIndex);
        }
        else
        {
            Debug.LogWarning("[HeadSwitcher] No dropdown assigned (TMP or legacy).");
        }
    }

    void BuildDropdown()
    {
        var labels = new List<string>(heads.Count);
        string modelSuffix = (showModelNameInLabel && classifier && classifier.embedder && classifier.embedder.modelAsset)
            ? $"  (Model: {classifier.embedder.modelAsset.name})"
            : "";

        foreach (var h in heads) labels.Add(h.label + modelSuffix);

        if (tmpDropdown)
        {
            tmpDropdown.ClearOptions();
            tmpDropdown.AddOptions(labels);
            tmpDropdown.RefreshShownValue();
        }
        else if (uDropdown)
        {
            uDropdown.ClearOptions();
            uDropdown.AddOptions(labels);
            uDropdown.RefreshShownValue();
        }
    }

    void LoadHead(string fileName)
    {
        if (classifier == null) { Debug.LogError("[HeadSwitcher] No classifier assigned."); return; }
        var path = Path.Combine(PD, "heads", fileName);
        if (!File.Exists(path))
        {
            Debug.LogError("[HeadSwitcher] Head not found: " + path);
            return;
        }

        var json = File.ReadAllText(path);
        var head = JsonUtility.FromJson<HeadData>(json);
        classifier.SetHead(head);

        if (trainer != null && head.classes != null)
            trainer.ResetAll(head.classes); // sync trainer class list

        Debug.Log("[HeadSwitcher] Loaded -> " + path);
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
    }

    int FindIndexByFile(string fileName)
    {
        for (int i = 0; i < heads.Count; i++)
            if (string.Equals(heads[i].fileName, fileName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
