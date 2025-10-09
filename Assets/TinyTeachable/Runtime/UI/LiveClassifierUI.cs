using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class LiveClassifierUI : MonoBehaviour
{
    [Header("Links")]
    public LiveClassifier classifier;           // drag your LiveClassifier
    public SentisEmbedder embedder;             // optional; shows model name
    public DetectionManager detectionManager;   // drag your DetectionManager

    [Header("UI (TMP)")]
    public TMP_Text labelText;
    public TMP_Text scoreText;
    public TMP_Text modelText;
    public TMP_Text headText;
    public TMP_Text fpsText;

    [Header("Detections (this dropdown controls switching)")]
    public TMP_Dropdown detectionDropdown;   // THIS controls switching heads
    public Button refreshDetectionsButton;   // optional

    [Header("Playback Controls (optional)")]
    public Button playButton;                // optional
    public Button stopButton;                // optional
    public TMP_Text runStateText;            // optional "Running / Stopped"

    [Header("Behavior")]
    [Tooltip("Switch immediately when dropdown changes.")]
    public bool applyOnDropdownChange = true;
    [Tooltip("Auto-start the classifier after switching heads.")]
    public bool autoRunAfterSwitch = true;
    public string scoreFormat = "0.00";

    // cache
    private string _lastShownLabel = "";
    private float  _lastShownScore = -1f;
    private float  _fpsSmoothed = 0f;

    private List<DetectionManager.DetectionInfo> _cachedDetections = new();
    private string _requestedHeadName = "";     // what the user picked
    private bool   _awaitingFirstPred = false;  // after switch, until we see a new pred

    void Awake()
    {
        if (!classifier) classifier = GetComponent<LiveClassifier>();
    }

    void OnEnable()
    {
        if (classifier != null)
        {
            classifier.OnPrediction  += HandlePrediction;
            classifier.OnHeadChanged += HandleHeadChanged;
        }

        if (detectionDropdown) detectionDropdown.onValueChanged.AddListener(OnSelectDetectionIndex);
        if (refreshDetectionsButton) refreshDetectionsButton.onClick.AddListener(OnRefreshDetectionsClicked);

        if (playButton) playButton.onClick.AddListener(OnPlayClicked);
        if (stopButton) stopButton.onClick.AddListener(OnStopClicked);

        // initial UI
        PushTexts(classifier ? classifier.LastLabel : "", classifier ? classifier.LastScore : 0f, force:true);
        if (modelText && embedder) modelText.text = $"Model: {SafeModelName(embedder)}";
        WriteHeadText(SafeHeadName());
        RefreshDetectionsList(selectCurrent:true);

        UpdateRunUI();
    }

    void OnDisable()
    {
        if (classifier != null)
        {
            classifier.OnPrediction  -= HandlePrediction;
            classifier.OnHeadChanged -= HandleHeadChanged;
        }

        if (detectionDropdown) detectionDropdown.onValueChanged.RemoveListener(OnSelectDetectionIndex);
        if (refreshDetectionsButton) refreshDetectionsButton.onClick.RemoveListener(OnRefreshDetectionsClicked);
        if (playButton) playButton.onClick.RemoveListener(OnPlayClicked);
        if (stopButton) stopButton.onClick.RemoveListener(OnStopClicked);
    }

    void Update()
    {
        if (fpsText)
        {
            float fps = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            _fpsSmoothed = (_fpsSmoothed <= 0f) ? fps : Mathf.Lerp(_fpsSmoothed, fps, 0.15f);
            fpsText.text = $"FPS: {Mathf.RoundToInt(_fpsSmoothed)}";
        }

        if (modelText && embedder) modelText.text = $"Model: {SafeModelName(embedder)}";

        // IMPORTANT: do NOT overwrite head text every frame from classifier if it's empty.
        // We only write head text in explicit places (switch/event).
    }

    // ---------- Populate + switch ----------

    public void RefreshDetectionsList(bool selectCurrent = false)
    {
        if (detectionDropdown == null || detectionManager == null) return;

        _cachedDetections = detectionManager.ListDetections();
        var labels = _cachedDetections.Select(d => d.name).ToList();

        detectionDropdown.ClearOptions();
        detectionDropdown.AddOptions(labels);
        detectionDropdown.RefreshShownValue();

        if (selectCurrent)
        {
            var current = SafeHeadName();
            int idx = labels.FindIndex(s => s == current);
            if (idx >= 0)
            {
                detectionDropdown.SetValueWithoutNotify(idx);
                detectionDropdown.RefreshShownValue();
            }
        }
    }

    private void OnRefreshDetectionsClicked()
    {
        RefreshDetectionsList(selectCurrent: true);
    }

    private void OnSelectDetectionIndex(int idx)
    {
        if (!applyOnDropdownChange) return;
        if (detectionManager == null || _cachedDetections == null) return;
        if (idx < 0 || idx >= _cachedDetections.Count) return;

        var name = _cachedDetections[idx].name;
        _requestedHeadName = name;

        // 1) Manager/trainer path (keeps session in sync)
        detectionManager.SetDetection(name);

        // 2) Guaranteed live swap: load head directly
        ApplyHeadDirect(name);

        // 3) Try to force-set the head name on the classifier if it didn't set it
        TryForceSetClassifierHeadName(name);

        // 4) Clear stale UI label/score & show the intended head name immediately
        ClearPredictionUI();
        WriteHeadText(name);
        _awaitingFirstPred = true;

        // 5) Ensure the classifier is running so we get fresh predictions
        if (autoRunAfterSwitch) TrySetRunning(true);
        UpdateRunUI();

        Debug.Log($"[LiveClassifierUI] Switch requested → '{name}'.");
    }

    // ---------- Play / Stop ----------

    private void OnPlayClicked()
    {
        TrySetRunning(true);
        UpdateRunUI();
    }

    private void OnStopClicked()
    {
        TrySetRunning(false);
        UpdateRunUI();
    }

    private void UpdateRunUI()
    {
        bool isRunning = TryGetIsRunning();
        if (runStateText) runStateText.text = isRunning ? "Running" : "Stopped";
        if (playButton) playButton.interactable = !isRunning;
        if (stopButton) stopButton.interactable = isRunning;
    }

    private void TrySetRunning(bool run)
    {
        if (!classifier) return;
        var t = classifier.GetType();

        var setRunning = t.GetMethod("SetRunning", new[] { typeof(bool) });
        if (setRunning != null) { setRunning.Invoke(classifier, new object[] { run }); return; }

        if (run)
        {
            var startM = t.GetMethod("StartClassifying", System.Type.EmptyTypes);
            if (startM != null) { startM.Invoke(classifier, null); return; }
        }
        else
        {
            var stopM = t.GetMethod("StopClassifying", System.Type.EmptyTypes);
            if (stopM != null) { stopM.Invoke(classifier, null); return; }
        }

        classifier.enabled = run; // fallback
    }

    private bool TryGetIsRunning()
    {
        if (!classifier) return false;
        var t = classifier.GetType();

        var prop = t.GetProperty("IsRunning");
        if (prop != null && prop.PropertyType == typeof(bool))
            return (bool)prop.GetValue(classifier);

        var field = t.GetField("IsRunning");
        if (field != null && field.FieldType == typeof(bool))
            return (bool)field.GetValue(classifier);

        return classifier.enabled;
    }

    // ---------- Classifier events ----------

    private void HandlePrediction(string label, float score)
    {
        // first fresh prediction after switch—now we can trust classifier state
        if (_awaitingFirstPred)
        {
            // If classifier exposes a non-empty name now, use it; otherwise keep requested
            var resolved = SafeHeadName();
            if (!string.IsNullOrEmpty(resolved)) WriteHeadText(resolved);
            _awaitingFirstPred = false;
        }

        PushTexts(label, score);
    }

    private void HandleHeadChanged(HeadData head)
    {
        // Prefer classifier.CurrentHeadName if non-empty, otherwise keep our requested name
        var resolved = SafeHeadName();
        if (string.IsNullOrEmpty(resolved)) resolved = _requestedHeadName;
        WriteHeadText(resolved);

        // keep dropdown selection in sync if the head changed elsewhere
        if (detectionDropdown && !string.IsNullOrEmpty(resolved))
        {
            var options = detectionDropdown.options.Select(o => o.text).ToList();
            int idx = options.FindIndex(s => s == resolved);
            if (idx >= 0)
            {
                detectionDropdown.SetValueWithoutNotify(idx);
                detectionDropdown.RefreshShownValue();
            }
        }

        // reset UI until first new prediction
        ClearPredictionUI();
        _awaitingFirstPred = true;
    }

    // ---------- Helpers ----------

    private void PushTexts(string label, float score, bool force = false)
    {
        if (labelText && (force || label != _lastShownLabel))
            labelText.text = string.IsNullOrEmpty(label) ? "Label: —" : $"Label: {label}";

        if (scoreText && (force || !Mathf.Approximately(score, _lastShownScore)))
            scoreText.text = $"Score: {score.ToString(scoreFormat)}";

        _lastShownLabel = label;
        _lastShownScore = score;
    }

    private void ClearPredictionUI()
    {
        _lastShownLabel = "";
        _lastShownScore = -1f;
        if (labelText) labelText.text = "Label: —";
        if (scoreText) scoreText.text = $"Score: {0f.ToString(scoreFormat)}";
    }

    private static string SafeModelName(SentisEmbedder e)
    {
        try { return (e != null && e.modelAsset != null) ? e.modelAsset.name : "(no model)"; }
        catch { return "(no model)"; }
    }

    private string SafeHeadName()
    {
        if (!classifier) return "";
        // Try property
        var t = classifier.GetType();
        var prop = t.GetProperty("CurrentHeadName");
        if (prop != null && prop.PropertyType == typeof(string))
        {
            string v = (string)prop.GetValue(classifier);
            return v ?? "";
        }
        // Try field
        var field = t.GetField("CurrentHeadName");
        if (field != null && field.FieldType == typeof(string))
        {
            string v = (string)field.GetValue(classifier);
            return v ?? "";
        }
        return "";
    }

    private void TryForceSetClassifierHeadName(string name)
    {
        if (!classifier || string.IsNullOrEmpty(name)) return;
        var t = classifier.GetType();

        // Only set if classifier currently reports empty; we don’t want to fight its own state.
        var current = SafeHeadName();
        if (!string.IsNullOrEmpty(current)) return;

        var prop = t.GetProperty("CurrentHeadName");
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            prop.SetValue(classifier, name);
            return;
        }
        var field = t.GetField("CurrentHeadName");
        if (field != null && field.FieldType == typeof(string))
        {
            field.SetValue(classifier, name);
        }
    }

    private void WriteHeadText(string nameOrEmpty)
    {
        if (!headText) return;
        headText.text = string.IsNullOrEmpty(nameOrEmpty) ? "Head: —" : $"Head: {nameOrEmpty}";
    }

    private void ApplyHeadDirect(string name)
    {
        if (!classifier || string.IsNullOrEmpty(name)) return;
        var path = Path.Combine(Application.persistentDataPath, "heads", name + ".json");
        if (File.Exists(path))
        {
            // Call LoadHeadFromPath if it exists
            var t = classifier.GetType();
            var m = t.GetMethod("LoadHeadFromPath", new[] { typeof(string) });
            if (m != null) m.Invoke(classifier, new object[] { path });
        }
        else
        {
            Debug.LogWarning($"[LiveClassifierUI] Head file not found: {path}");
        }
    }
}
