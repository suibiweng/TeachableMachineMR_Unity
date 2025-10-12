// Assets/TinyTeachable/Runtime/UI/DetectionManagerUI.cs
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class DetectionManagerUI : MonoBehaviour
{
    [Header("Core")]
    public DetectionManager detectionManager;   // assign
    public DynamicClassTrainer trainer;         // assign
    public LiveClassifier classifier;           // optional (used for direct-apply fallback)

    // =======================
    // Panels
    // =======================
    [Header("Panels")]
    public GameObject panelManager;   // Manager interface (dropdown + retrain/remove + create row)
    public GameObject panelClasses;   // Classes (list/add/remove/rename + Add Sample)
    public GameObject panelTrain;     // Train & Apply
    public GameObject panelExport;    // Export & Status (optional)

    public enum PanelSet { ManagerOnly, ClassesAndTrain, All }

    [Header("Behavior")]
    [Tooltip("Selecting in the Manager dropdown applies immediately.")]
    public bool autoApplyOnDetectionSelect = true;

    [Tooltip("Auto-refresh the list after training completes.")]
    public bool autoRefreshAfterTrain = true;

    [Tooltip("Max seconds to wait for the newly saved head file to appear.")]
    public float trainRefreshTimeoutSec = 3f;

    [Tooltip("If disabled, the Export panel is hidden and its controls are ignored.")]
    public bool enableExportPanel = false;

    // ---------- Manager panel widgets ----------
    [Header("Manager Panel")]
    public TMP_Dropdown detectionSwitchDropdown;   // top row
    public Button retrainButton;
    public Button removeDetectionButton;
    public TMP_InputField detectionNameInput;      // create row
    public Button createDetectionButton;

    // ---------- Classes panel ----------
    [Header("Classes Panel")]
    public TMP_Dropdown classDropdown;
    public TMP_InputField newClassInput;
    public Button addClassButton;
    public Button removeSelectedClassButton;
    public TMP_InputField renameClassInput;
    public Button renameClassButton;
    public Button addSampleButton;

    // ---------- Train panel ----------
    [Header("Train Panel")]
    public Button trainButton;
    public Button setLiveButton;

    // ---------- Export/Status (optional) ----------
    [Header("Export / Status (Optional)")]
    public GameObject messageSection;     // container GameObject for messages (optional)
    public TMP_Text messageText;          // shows status + sample counts
    public Button exportJsonButton;
    public TMP_Text statusText;           // (kept for backwards-compat; used if messageText is null)

    // ---------- Navigation ----------
    [Header("Navigation")]
    public Button backToManagerButton;    // goes back to first page (Manager)

    // cache
    private List<DetectionManager.DetectionInfo> _cachedDetections = new();
    // local shadow counters; UI shows max(trainer, local)
    private readonly Dictionary<string, int> _localSampleCounts = new();

    void Awake()
    {
        // Manager
        if (createDetectionButton)    createDetectionButton.onClick.AddListener(OnCreateDetection);
        if (retrainButton)            retrainButton.onClick.AddListener(OnRetrainSelected);
        if (removeDetectionButton)    removeDetectionButton.onClick.AddListener(OnRemoveSelected);
        if (detectionSwitchDropdown)  detectionSwitchDropdown.onValueChanged.AddListener(OnSwitchDetectionIndex);

        // Classes
        if (addClassButton)            addClassButton.onClick.AddListener(OnAddClass);
        if (removeSelectedClassButton) removeSelectedClassButton.onClick.AddListener(OnRemoveSelectedClass);
        if (renameClassButton)         renameClassButton.onClick.AddListener(OnRenameSelectedClass);
        if (addSampleButton)           addSampleButton.onClick.AddListener(OnAddSample);
        if (classDropdown)             classDropdown.onValueChanged.AddListener(OnSelectClassIndex);

        // Train
        if (trainButton)              trainButton.onClick.AddListener(OnTrain);
        if (setLiveButton)            setLiveButton.onClick.AddListener(OnSetLive);

        // Export
        if (enableExportPanel && exportJsonButton)
            exportJsonButton.onClick.AddListener(OnExportJson);

        // Navigation
        if (backToManagerButton)      backToManagerButton.onClick.AddListener(() => ShowPanels(PanelSet.ManagerOnly));
    }

    void Start()
    {
        ShowPanels(PanelSet.ManagerOnly);
        RefreshDetectionList();
        RefreshClassDropdown();
        UpdateStatus("Manager ready.");
        UpdateMessage("Welcome! Select a detection to retrain or create a new one.");
    }

    void Update()
    {
#if OVR_SUPPORTED
        // Optional: OVR shortcut demo
        if (panelClasses != null && panelClasses.activeInHierarchy)
        {
            if (OVRInput.GetDown(OVRInput.Button.Two)) // B on Touch/Touch Pro
            {
                OnAddSample();
            }
        }
#endif
    }

    // =======================
    // Panel visibility
    // =======================
    public void ShowPanels(PanelSet set)
    {
        if (panelManager) panelManager.SetActive(set == PanelSet.ManagerOnly || set == PanelSet.All);
        if (panelClasses) panelClasses.SetActive(set == PanelSet.ClassesAndTrain || set == PanelSet.All);
        if (panelTrain)   panelTrain.SetActive(set == PanelSet.ClassesAndTrain || set == PanelSet.All);
        if (panelExport)  panelExport.SetActive(enableExportPanel && set == PanelSet.All);
    }

    // =======================
    // Manager actions
    // =======================
    public void RefreshDetectionList()
    {
        if (!detectionManager || detectionSwitchDropdown == null) return;

        _cachedDetections = detectionManager.ListDetections();
        var labels = _cachedDetections.Select(d => d.name).ToList();

        detectionSwitchDropdown.ClearOptions();
        detectionSwitchDropdown.AddOptions(labels);
        detectionSwitchDropdown.RefreshShownValue();

        UpdateStatus($"Detections: {labels.Count}");
    }

    public void OnSwitchDetectionIndex(int idx)
    {
        if (!detectionManager) return;
        if (idx < 0 || idx >= _cachedDetections.Count) return;

        var name = _cachedDetections[idx].name;
        if (autoApplyOnDetectionSelect)
        {
            detectionManager.SetDetection(name);   // trainer path
            ApplyHeadDirect(name);                 // fallback to guarantee LiveClassifier swap
            UpdateStatus($"Switched live → '{name}'.");
            UpdateMessage($"Live head: <b>{name}</b>");
        }
        else
        {
            UpdateMessage($"Selected '{name}' (not applied yet).");
        }
    }

    public void OnRetrainSelected()
    {
        if (!detectionManager) { UpdateStatus("Manager missing."); return; }
        int idx = GetSelectedDetectionIndex();
        if (idx < 0 || idx >= _cachedDetections.Count) { UpdateMessage("Select a detection first."); return; }

        var name = _cachedDetections[idx].name;
        detectionManager.LoadDetectionForEditing(name, resetSamplesToHeadClasses: true);

        // reset local counts since samples were cleared
        ResetLocalCountsFromTrainer();

        RefreshClassDropdown(selectLast: false);
        ShowPanels(PanelSet.ClassesAndTrain);
        UpdateStatus($"Loaded '{name}' for retrain.");
        UpdateMessage($"Retraining <b>{name}</b>. Add/rename/remove classes, collect samples, then Train.");
    }

    public void OnRemoveSelected()
    {
        if (!detectionManager) { UpdateStatus("Manager missing."); return; }
        int idx = GetSelectedDetectionIndex();
        if (idx < 0 || idx >= _cachedDetections.Count) { UpdateMessage("Select a detection to remove."); return; }

        var name = _cachedDetections[idx].name;
        var ok = detectionManager.RemoveDetection(name);
        if (ok)
        {
            RefreshDetectionList();
            UpdateStatus($"Removed detection '{name}'.");
            UpdateMessage($"Removed <b>{name}</b>.");
        }
        else
        {
            UpdateMessage($"Failed to remove '{name}'.");
        }
    }

    public void OnCreateDetection()
    {
        if (!detectionManager || !trainer) { UpdateStatus("Manager/trainer missing."); return; }

        var name = SafeText(detectionNameInput, "NewDetection");
        detectionManager.CreateDetection(name, new string[0]);

        ResetLocalCountsFromTrainer();
        RefreshDetectionList();
        RefreshClassDropdown(selectLast: false);
        ShowPanels(PanelSet.ClassesAndTrain);
        UpdateStatus($"Created '{name}'.");
        UpdateMessage($"Created <b>{name}</b>. Add classes, then collect samples, then Train.");
    }

    // =======================
    // Train actions
    // =======================
    public void OnTrain()
    {
        if (!detectionManager || !trainer) { UpdateMessage("Manager/trainer missing."); return; }

        string expectedFile = Path.Combine(
            Application.persistentDataPath, "heads",
            (trainer.saveHeadName ?? "").Trim());

        detectionManager.TrainDetection(); // applies via trainer

        if (autoRefreshAfterTrain)
            StartCoroutine(CoRefreshAfterTrain(expectedFile));
        else
            RefreshDetectionList();

        UpdateStatus("Training requested...");
        UpdateMessage("Training… we’ll refresh your list shortly.");
    }

    public void OnSetLive()
    {
        if (!detectionManager) { UpdateMessage("Manager missing."); return; }
        int idx = GetSelectedDetectionIndex();
        if (idx < 0 || idx >= _cachedDetections.Count) { UpdateMessage("Select a detection first."); return; }

        var name = _cachedDetections[idx].name;
        detectionManager.SetDetection(name);
        ApplyHeadDirect(name); // ensure LiveClassifier switches now
        UpdateStatus($"Live head set to '{name}'.");
        UpdateMessage($"Live head: <b>{name}</b>");
    }

    // =======================
    // Export actions
    // =======================
    public void OnExportJson()
    {
        if (!enableExportPanel) { UpdateMessage("Export panel disabled."); return; }
        if (!detectionManager) { UpdateMessage("Manager missing."); return; }

        var json = detectionManager.ExportAllAsJson();
        var path = Path.Combine(Application.persistentDataPath, "detection_catalog.json");
        File.WriteAllText(path, json);
        UpdateStatus($"Exported catalog → {path}");
        UpdateMessage($"Exported detection catalog to:<br><i>{path}</i>");
        Debug.Log(json);
    }

    // =======================
    // Helpers
    // =======================
    void RefreshClassDropdown(bool selectLast = false)
    {
        if (classDropdown == null || trainer == null) return;

        var options = new List<string>(trainer.Classes);
        classDropdown.ClearOptions();
        classDropdown.AddOptions(options);
        classDropdown.value = selectLast
            ? Mathf.Max(0, options.Count - 1)
            : Mathf.Max(0, trainer.CurrentClassIndex);
        classDropdown.RefreshShownValue();

        // align local counters with current classes (ensure keys exist & >= trainer)
        SyncLocalCountsWith(options);
    }

    string GetSelectedClassLabel()
    {
        if (trainer == null || trainer.Classes.Count == 0) return "(none)";
        int i = GetSelectedClassIndex();
        i = Mathf.Clamp(i, 0, trainer.Classes.Count - 1);
        return trainer.Classes[i];
    }

    int GetSelectedClassIndex()
    {
        if (classDropdown == null) return Mathf.Max(0, trainer?.CurrentClassIndex ?? 0);
        return classDropdown.value;
    }

    int GetSelectedDetectionIndex()
    {
        if (detectionSwitchDropdown == null) return -1;
        return detectionSwitchDropdown.value;
    }

    static string SafeText(TMP_InputField field, string fallback)
    {
        if (!field) return fallback;
        var t = field.text;
        return string.IsNullOrWhiteSpace(t) ? fallback : t;
    }

    void UpdateStatus(string msg)
    {
        // keep old statusText support; prefer messageText if present
        if (statusText) statusText.text = msg;
        Debug.Log("[DetectionUI] " + msg);
    }

    void UpdateMessage(string msg)
    {
        if (messageText)
        {
            messageText.text = msg;
            if (messageSection) messageSection.SetActive(true);
        }
        else
        {
            // fallback to console/log only
            Debug.Log("[DetectionUI][Msg] " + msg);
        }
    }

    void ResetLocalCountsFromTrainer()
    {
        _localSampleCounts.Clear();
        if (trainer != null && trainer.Classes != null)
            foreach (var c in trainer.Classes) _localSampleCounts[c] = 0;
    }

    void SyncLocalCountsWith(List<string> labels)
    {
        foreach (var l in labels) if (!_localSampleCounts.ContainsKey(l)) _localSampleCounts[l] = 0;
        // prune removed
        var toRemove = _localSampleCounts.Keys.Where(k => !labels.Contains(k)).ToList();
        foreach (var k in toRemove) _localSampleCounts.Remove(k);
    }

    // ---- Direct apply fallback (now authoritative + forced) ----
    void ApplyHeadDirect(string name)
    {
        if (classifier == null || string.IsNullOrEmpty(name)) return;

        var path = Path.Combine(Application.persistentDataPath, "heads", name + ".json");
        if (File.Exists(path))
        {
            // Lock preference so the apply is accepted even if enforcement is on
            classifier.SetPreferredHead(name, enforce: true);

            // >>> FORCE the swap so nothing can block it
            classifier.LoadHeadFromPath(path, force:true);

            // Persist for autoloader harmony
            PlayerPrefs.SetString("TTM_LastHead", name);
            PlayerPrefs.Save();

            UpdateStatus($"[DirectApply] Head applied: {name}");
        }
        else
        {
            UpdateStatus($"[DirectApply] Missing head file: {path}");
        }
    }

    // =======================
    // Auto-refresh after training
    // =======================
    private System.Collections.IEnumerator CoRefreshAfterTrain(string expectedFilePath)
    {
        float t = 0f;
        bool hasTarget = !string.IsNullOrEmpty(expectedFilePath);

        // a few frames to let IO/GPU settle
        for (int i = 0; i < 3; i++) yield return null;

        while (t < trainRefreshTimeoutSec)
        {
            if (!hasTarget) break;
            if (File.Exists(expectedFilePath)) break;
            t += Time.unscaledDeltaTime;
            yield return null;
        }

        RefreshDetectionList();

        if (hasTarget && File.Exists(expectedFilePath) && detectionSwitchDropdown != null)
        {
            var justName = Path.GetFileNameWithoutExtension(expectedFilePath);
            int idx = _cachedDetections.FindIndex(d => d.name == justName);
            if (idx >= 0)
            {
                detectionSwitchDropdown.SetValueWithoutNotify(idx);
                detectionSwitchDropdown.RefreshShownValue();
                if (autoApplyOnDetectionSelect)
                {
                    detectionManager.SetDetection(justName);
                    ApplyHeadDirect(justName); // ensure live swap
                    UpdateStatus($"Trained and switched live → '{justName}'.");
                    UpdateMessage($"Training complete. Live head → <b>{justName}</b>");
                    yield break;
                }
                else
                {
                    UpdateMessage($"Training complete. Selected <b>{justName}</b>.");
                    yield break;
                }
            }
        }

        UpdateMessage("Training complete. List refreshed.");
    }

    // ======= Simple class editing & sampling stubs (optional) =======
    void OnAddClass()
    {
        if (!trainer) { UpdateStatus("Trainer missing."); return; }
        var label = SafeText(newClassInput, "").Trim();
        if (string.IsNullOrEmpty(label)) { UpdateMessage("Enter a class name first."); return; }
        trainer.AddClass(label);
        RefreshClassDropdown(selectLast:true);
        UpdateMessage($"Added class <b>{label}</b>.");
    }

    void OnRemoveSelectedClass()
    {
        if (!trainer) { UpdateStatus("Trainer missing."); return; }
        int i = GetSelectedClassIndex();
        // trainer should expose a remove—if not, skip here or implement
        UpdateMessage("Remove class not implemented in this minimal UI.");
    }

    void OnRenameSelectedClass()
    {
        if (!trainer) { UpdateStatus("Trainer missing."); return; }
        UpdateMessage("Rename class not implemented in this minimal UI.");
    }

    void OnSelectClassIndex(int i)
    {
        trainer?.SelectClass(i);
    }

    void OnAddSample()
    {
        if (!trainer) { UpdateStatus("Trainer missing."); return; }
        trainer.AddSampleFromCurrent();
        var label = GetSelectedClassLabel();
        _localSampleCounts[label] = GetLocalCount(label) + 1;
        UpdateMessage($"Added sample to <b>{label}</b> (UI total: {GetLocalCount(label)}).");
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
