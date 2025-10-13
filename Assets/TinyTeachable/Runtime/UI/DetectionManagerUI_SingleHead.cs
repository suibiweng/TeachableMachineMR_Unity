using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class DetectionManagerUI_SingleHead : MonoBehaviour
{
    [Header("Core")]
    public DetectionManager detectionManager;   // assign
    public DynamicClassTrainer trainer;         // assign
    public LiveClassifier classifier;           // assign

    [Header("Single Head Settings")]
    [Tooltip("Current head name (no .json). Overwritten on each train.")]
    public string headName = "Active";
    [Tooltip("PlayerPrefs key used by other UIs/autoloaders to restore the last head name.")]
    public string lastHeadPlayerPrefKey = "TTM_LastHead";

    [Header("Optional Indicators (purely UI/logging)")]
    public string detectionTag = "";
    public string version = "";
    [TextArea] public string notes = "";

    [Header("Name & Meta UI")]
    public TMP_InputField headNameInput;
    public TMP_InputField tagInput;
    public TMP_InputField versionInput;
    public TMP_InputField notesInput;
    public Button applyNameAndMetaButton;   // Apply Name (stop+clear+rename)

    [Header("Classes UI")]
    public TMP_Dropdown classDropdown;
    public TMP_InputField newClassInput;
    public Button addClassButton;
    public Button addSampleButton;

    [Header("Existing Classes Quick-Select")]
    public TMP_Dropdown existingClassesDropdown;
    public Button useExistingClassButton;

    [Header("Train & Clear UI")]
    public Button trainOverwriteButton;
    public Button clearButton;
    public Toggle clearAlsoDeleteClassesToggle;

    [Header("JSON Inject/Export UI")]
    public TMP_InputField injectionJsonInput;   // paste JSON here
    public Button injectJsonButton;             // inject button
    public Button exportJsonButton;             // export button (writes .json file)

    [Header("Behavior")]
    [Tooltip("If true, classification starts automatically after a successful Train applies the new head.")]
    public bool startAfterTrain = true;

    [Header("Status UI")]
    public TMP_Text headText;
    public TMP_Text statusText;
    public TMP_Text messageText;

    // local UI counters
    private readonly Dictionary<string, int> _localSampleCounts = new();

    void Awake()
    {
        if (applyNameAndMetaButton) applyNameAndMetaButton.onClick.AddListener(OnApplyNameAndMeta);

        if (addClassButton)         addClassButton.onClick.AddListener(OnAddClass);
        if (addSampleButton)        addSampleButton.onClick.AddListener(OnAddSample);
        if (classDropdown)          classDropdown.onValueChanged.AddListener(OnSelectClassIndex);

        if (useExistingClassButton) useExistingClassButton.onClick.AddListener(OnUseExistingClass);

        if (trainOverwriteButton)   trainOverwriteButton.onClick.AddListener(OnTrainOverwriteAndApply);
        if (clearButton)            clearButton.onClick.AddListener(OnClear);

        if (injectJsonButton)       injectJsonButton.onClick.AddListener(OnInjectJson);
        if (exportJsonButton)       exportJsonButton.onClick.AddListener(OnExportJson);
    }

    void OnEnable()
    {
        if (trainer != null) trainer.OnHeadTrained += OnTrainerHeadTrained;
    }

    void OnDisable()
    {
        if (trainer != null) trainer.OnHeadTrained -= OnTrainerHeadTrained;
    }

    void Start()
    {
        if (headNameInput) headNameInput.text = headName;
        if (tagInput)      tagInput.text = detectionTag;
        if (versionInput)  versionInput.text = version;
        if (notesInput)    notesInput.text = notes;

        ApplyHeadNamesToTrainer();

        RefreshClassDropdown(false);
        RefreshExistingClassesDropdown();

        UpdateStatus("Single-head UI ready.");
        UpdateMessage($"Using head: <b>{headName}</b>" + (string.IsNullOrEmpty(detectionTag) ? "" : $"  • Tag: <i>{detectionTag}</i>"));
        if (headText) headText.text = $"Head: {headName}";
    }

    void Update()
    {
        if (headText && classifier) headText.text = $"Head: {classifier.CurrentHeadName}";
    }

    // =======================
    // Name & Metadata
    // =======================
    public void OnApplyNameAndMeta()
    {
        // stop and clear
        if (classifier != null)
        {
            classifier.StopClassifying();
            classifier.Clear();
            classifier.ClearPreferredHead();
        }

        if (trainer != null)
        {
            try { trainer.ResetAll(Array.Empty<string>()); }
            catch (Exception e) { Debug.LogWarning("[SingleHeadUI] trainer.ResetAll failed: " + e.Message); }
        }
        _localSampleCounts.Clear();
        RefreshClassDropdown(false);
        RefreshExistingClassesDropdown();

        // new meta
        string newName = SafeText(headNameInput, headName).Trim();
        string newTag  = SafeText(tagInput, detectionTag).Trim();
        string newVer  = SafeText(versionInput, version).Trim();
        string newNote = SafeText(notesInput, notes).Trim();

        headName     = Sanitize(newName);
        detectionTag = newTag;
        version      = newVer;
        notes        = newNote;

        ApplyHeadNamesToTrainer();

        PlayerPrefs.SetString(lastHeadPlayerPrefKey, headName);
        PlayerPrefs.Save();

        UpdateStatus($"Stopped & cleared. New target head → {headName}");
        UpdateMessage($"Ready to record samples for <b>{headName}</b>.");
        if (headText) headText.text = $"Head: {headName}";
    }

    void ApplyHeadNamesToTrainer()
    {
        if (trainer == null) return;
        var safe = Sanitize(headName);
        trainer.sessionName  = safe;
        trainer.saveHeadName = safe + ".json";
        trainer.loadHeadName = safe + ".json";
    }

    // =======================
    // Train & Apply (event-driven)
    // =======================
    public void OnTrainOverwriteAndApply()
    {
        if (!trainer)
        {
            UpdateMessage("Trainer missing.");
            return;
        }

        ApplyHeadNamesToTrainer();

        // Use trainer directly so we get OnHeadTrained + HeadData immediately
        trainer.TrainAndSaveAndApply();

        UpdateStatus("Training requested…");
        UpdateMessage($"Training & applying <b>{headName}</b>…");
    }

    // Event from DynamicClassTrainer after it actually saved/applied the head
    private void OnTrainerHeadTrained(string fileName, HeadData head)
    {
        // Derive display name from file (no extension)
        string name = fileName;
        if (!string.IsNullOrEmpty(name) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);

        // Persist last head for other systems
        PlayerPrefs.SetString(lastHeadPlayerPrefKey, name);
        PlayerPrefs.Save();

        // Ensure LiveClassifier uses it
        if (classifier != null)
        {
            classifier.SetPreferredHead(name, enforce: true);
            classifier.SetHead(head, name, force: true);

            if (startAfterTrain)
                classifier.StartClassifying();
        }

        if (headText) headText.text = $"Head: {name}";
        UpdateStatus($"Trained and applied → '{name}'");
        UpdateMessage($"Training complete. Live head → <b>{name}</b>");
    }

    // =======================
    // CLEAR button (optional UI)
    // =======================
    public void OnClear()
    {
        if (classifier != null)
        {
            classifier.StopClassifying();
            classifier.Clear();
            classifier.ClearPreferredHead();
        }

        if (trainer != null)
        {
            try
            {
                if (clearAlsoDeleteClassesToggle != null && clearAlsoDeleteClassesToggle.isOn)
                {
                    trainer.ResetAll(Array.Empty<string>()); // wipe classes
                }
                else
                {
                    var keep = (trainer.Classes != null) ? trainer.Classes.ToArray() : Array.Empty<string>();
                    trainer.ResetAll(keep);
                }
            }
            catch (Exception e) { Debug.LogWarning("[SingleHeadUI] ResetAll failed: " + e.Message); }
        }

        _localSampleCounts.Clear();
        RefreshClassDropdown(false);
        RefreshExistingClassesDropdown();

        UpdateMessage("Cleared." + ((clearAlsoDeleteClassesToggle != null && clearAlsoDeleteClassesToggle.isOn) ? " Removed classes." : " Kept classes."));
    }

    // =======================
    // JSON Inject / Export
    // =======================
    public void OnInjectJson()
    {
        if (!detectionManager)
        {
            UpdateMessage("DetectionManager missing.");
            return;
        }
        string json = injectionJsonInput ? injectionJsonInput.text : "";
        if (string.IsNullOrWhiteSpace(json))
        {
            UpdateMessage("Paste a JSON settings string first.");
            return;
        }

        // stop/clear live to avoid sticky state
        if (classifier != null)
        {
            classifier.StopClassifying();
            classifier.Clear();
            classifier.ClearPreferredHead();
        }

        // Use extension method (DetectionManagerJsonExtensions) to configure trainer & filenames
        bool ok = detectionManager.InjectDetectionFromJson(json, setLivePreferredName: true, clearTrainer: true);
        if (!ok)
        {
            UpdateMessage("Injection failed. Check JSON format.");
            return;
        }

        // best-effort UI sync to name
        string nameJustApplied = TryPeekName(json);
        if (!string.IsNullOrEmpty(nameJustApplied))
        {
            headName = Sanitize(nameJustApplied);
            ApplyHeadNamesToTrainer();
            if (headText) headText.text = $"Head: {headName}";
        }

        _localSampleCounts.Clear();
        RefreshClassDropdown(false);

        UpdateStatus("Detection settings injected.");
        UpdateMessage("Setup complete. Add samples, then Train.");
    }

    public void OnExportJson()
    {
        if (!detectionManager)
        {
            UpdateMessage("DetectionManager missing.");
            return;
        }

        string json = detectionManager.ExportDetectionSetupAsJson(detectionTag, version, notes);

        string fileName = $"{Sanitize(headName)}.detection.json";
        string dir = Application.persistentDataPath;
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, json);

        UpdateStatus($"Exported detection setup → {path}");
        UpdateMessage($"Exported JSON to:<br><i>{path}</i>");
        Debug.Log(json);
    }

    // =======================
    // Existing classes quick-select
    // =======================
    void RefreshExistingClassesDropdown()
    {
        if (existingClassesDropdown == null || trainer == null) return;
        var labels = new List<string>(trainer.Classes);
        existingClassesDropdown.ClearOptions();
        existingClassesDropdown.AddOptions(labels);
        existingClassesDropdown.value = Mathf.Clamp(trainer.CurrentClassIndex, 0, Mathf.Max(labels.Count - 1, 0));
        existingClassesDropdown.RefreshShownValue();
    }

    void OnUseExistingClass()
    {
        if (existingClassesDropdown == null || trainer == null) return;
        int idx = existingClassesDropdown.value;
        idx = Mathf.Clamp(idx, 0, Mathf.Max(trainer.Classes.Count - 1, 0));
        trainer.SelectClass(idx);

        if (classDropdown)
        {
            classDropdown.SetValueWithoutNotify(idx);
            classDropdown.RefreshShownValue();
        }

        UpdateMessage($"Selected existing class: <b>{GetSelectedClassLabel()}</b>");
    }

    // =======================
    // Classes & Samples (simple)
    // =======================
    void RefreshClassDropdown(bool selectLast)
    {
        if (classDropdown == null || trainer == null) return;

        var options = new List<string>(trainer.Classes);
        classDropdown.ClearOptions();
        classDropdown.AddOptions(options);
        classDropdown.value = selectLast
            ? Mathf.Max(0, options.Count - 1)
            : Mathf.Max(0, trainer.CurrentClassIndex);
        classDropdown.RefreshShownValue();

        RefreshExistingClassesDropdown();
        SyncLocalCountsWith(options);
    }

    void OnSelectClassIndex(int i) => trainer?.SelectClass(i);

    void OnAddClass()
    {
        if (!trainer) { UpdateStatus("Trainer missing."); return; }
        var label = SafeText(newClassInput, "").Trim();
        if (string.IsNullOrEmpty(label)) { UpdateMessage("Enter a class name first."); return; }
        trainer.AddClass(label);
        RefreshClassDropdown(true);
        UpdateMessage($"Added class <b>{label}</b>.");
    }

    void OnAddSample()
    {
        if (!trainer) { UpdateStatus("Trainer missing."); return; }
        trainer.AddSampleFromCurrent();
        var label = GetSelectedClassLabel();
        _localSampleCounts[label] = GetLocalCount(label) + 1;
        UpdateMessage($"Added sample to <b>{label}</b> (UI total: {GetLocalCount(label)}).");
    }

    string GetSelectedClassLabel()
    {
        if (trainer == null || trainer.Classes.Count == 0) return "(none)";
        int i = (classDropdown != null) ? classDropdown.value : Mathf.Max(0, trainer.CurrentClassIndex);
        i = Mathf.Clamp(i, 0, trainer.Classes.Count - 1);
        return trainer.Classes[i];
    }

    // =======================
    // Helpers
    // =======================
    string Sanitize(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "Active";
        foreach (char c in Path.GetInvalidFileNameChars())
            t = t.Replace(c, '_');
        return t.Trim();
    }

    static string SafeText(TMP_InputField field, string fallback)
    {
        if (!field) return fallback;
        var t = field.text;
        return string.IsNullOrWhiteSpace(t) ? fallback : t;
    }

    static string TryPeekName(string json)
    {
        try
        {
            var mini = JsonUtility.FromJson<NameOnly>(json);
            return mini != null ? mini.name : null;
        }
        catch { return null; }
    }

    [Serializable]
    class NameOnly { public string name; }

    void UpdateStatus(string msg)
    {
        if (statusText) statusText.text = msg;
        Debug.Log("[SingleHeadUI] " + msg);
    }

    void UpdateMessage(string msg)
    {
        if (messageText) messageText.text = msg;
        else Debug.Log("[SingleHeadUI][Msg] " + msg);
    }

    void SyncLocalCountsWith(List<string> labels)
    {
        foreach (var l in labels) if (!_localSampleCounts.ContainsKey(l)) _localSampleCounts[l] = 0;
        var toRemove = _localSampleCounts.Keys.Where(k => !labels.Contains(k)).ToList();
        foreach (var k in toRemove) _localSampleCounts.Remove(k);
    }

    int GetLocalCount(string label)
    {
        if (!_localSampleCounts.TryGetValue(label, out var c))
        {
            c = 0;
            _localSampleCounts[label] = 0;
        }
        return c;
    }
}
