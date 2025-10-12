// Assets/TinyTeachable/Runtime/UI/LiveClassifierUI.cs
using System;
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
    public LiveClassifier classifier;          // assign
    public DetectionManager detectionManager;  // optional (not used to switch here)
    public SentisEmbedder embedder;            // optional

    [Header("UI (TMP)")]
    public TMP_Text labelText;
    public TMP_Text scoreText;
    public TMP_Text headText;
    public TMP_Text fpsText;

    [Header("Saved Detections UI")]
    public TMP_Dropdown detectionDropdown;
    public Button refreshDetectionsButton;

    [Header("Prefs")]
    public string lastHeadPlayerPrefKey = "TTM_LastHead";

    public string scoreFormat = "0.000";

    private string _lastLabel = "";
    private float  _lastScore = -1f;
    private float  _fpsSmoothed = 0f;

    void OnEnable()
    {
        if (classifier != null)
        {
            classifier.OnPrediction  += HandlePrediction;
            classifier.OnHeadChanged += HandleHeadChanged;
        }
        if (detectionDropdown) detectionDropdown.onValueChanged.AddListener(OnSelectDetectionIndex);
        if (refreshDetectionsButton) refreshDetectionsButton.onClick.AddListener(OnRefreshDetectionsClicked);

        PushTexts(classifier ? classifier.LastLabel : "", classifier ? classifier.LastScore : 0f, force:true);
        if (headText) headText.text = $"Head: {(classifier ? classifier.CurrentHeadName : "")}";

        RefreshDetectionsList(selectCurrent:true);
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
    }

    void Update()
    {
        if (fpsText)
        {
            float fps = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            _fpsSmoothed = (_fpsSmoothed <= 0f) ? fps : Mathf.Lerp(_fpsSmoothed, fps, 0.15f);
            fpsText.text = $"FPS: {Mathf.RoundToInt(_fpsSmoothed)}";
        }
        if (headText && classifier) headText.text = $"Head: {classifier.CurrentHeadName}";
    }

    // ------------ Populate dropdown ------------
    public void RefreshDetectionsList(bool selectCurrent = false)
    {
        if (detectionDropdown == null) return;

        var labels = new List<string>();
        var dir = Path.Combine(Application.persistentDataPath, "heads");
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.json"))
                labels.Add(Path.GetFileNameWithoutExtension(f));
        }

        detectionDropdown.ClearOptions();
        detectionDropdown.AddOptions(labels);
        detectionDropdown.RefreshShownValue();

        if (selectCurrent && classifier != null && !string.IsNullOrEmpty(classifier.CurrentHeadName))
        {
            int idx = labels.FindIndex(s => s == classifier.CurrentHeadName);
            if (idx >= 0) { detectionDropdown.SetValueWithoutNotify(idx); detectionDropdown.RefreshShownValue(); }
        }
        Debug.Log($"[LiveClassifierUI] Dropdown list: [{string.Join(", ", labels)}]");
    }

    private void OnRefreshDetectionsClicked()
    {
        RefreshDetectionsList(selectCurrent: true);
        Debug.Log("[LiveClassifierUI] Refreshed detection list.");
    }

    // ------------ Switch (authoritative) ------------
    private void OnSelectDetectionIndex(int idx)
    {
        if (classifier == null || detectionDropdown == null) return;

        var options = detectionDropdown.options.Select(o => o.text).ToList();
        if (idx < 0 || idx >= options.Count) return;

        var name = options[idx];
        var path = Path.Combine(Application.persistentDataPath, "heads", name + ".json");

        Debug.Log($"[LiveClassifierUI] Authoritative switch -> '{name}'");

        // Clear UI immediately
        ForceClearLabelAndScore();
        classifier.ResetPrediction();

        // Lock to this head name so any SetHead with another name is ignored
        classifier.SetPreferredHead(name, enforce: true);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[LiveClassifierUI] Missing head file: {path}");
            return;
        }

        // >>> FORCE the swap so enforcement can't block it
        classifier.LoadHeadFromPath(path, force:true);

        // Persist user choice for autoloaders
        PlayerPrefs.SetString(lastHeadPlayerPrefKey, name);
        PlayerPrefs.Save();

        if (headText) headText.text = $"Head: {name}";
    }

    // ------------ Events ------------
    private void HandlePrediction(string label, float score)
    {
        PushTexts(label, score);
        int embLen = TryGetEmbeddingLength(embedder);
        Debug.Log($"[Pred] head='{(classifier? classifier.CurrentHeadName : "")}' label='{label}' score={score.ToString(scoreFormat)} embLen={embLen}");
    }

    private void HandleHeadChanged(HeadData head)
    {
        if (headText && classifier) headText.text = $"Head: {classifier.CurrentHeadName}";

        // Align the dropdown to the actual head
        if (detectionDropdown && classifier != null && !string.IsNullOrEmpty(classifier.CurrentHeadName))
        {
            var opts = detectionDropdown.options.Select(o => o.text).ToList();
            int idx = opts.FindIndex(s => s == classifier.CurrentHeadName);
            if (idx >= 0) detectionDropdown.SetValueWithoutNotify(idx);
        }
    }

    // ------------ UI helpers ------------
    void PushTexts(string label, float score, bool force = false)
    {
        if (!force && label == _lastLabel && Mathf.Approximately(score, _lastScore)) return;
        _lastLabel = label; _lastScore = score;
        if (labelText) labelText.text = label ?? "";
        if (scoreText) scoreText.text = score.ToString(scoreFormat);
    }

    void ForceClearLabelAndScore()
    {
        _lastLabel = ""; _lastScore = -1f;
        if (labelText) labelText.text = "";
        if (scoreText) scoreText.text = "";
    }

    // FIXED: use OutputDim (your SentisEmbedder exposes this) instead of LastEmbedding
    static int TryGetEmbeddingLength(SentisEmbedder emb)
    {
        try { return emb ? emb.OutputDim : -1; }
        catch { return -1; }
    }
}
