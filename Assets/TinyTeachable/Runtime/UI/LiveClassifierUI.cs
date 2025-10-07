using UnityEngine;
using TMPro;  // or swap to UnityEngine.UI.Text if you prefer

[DisallowMultipleComponent]
public class LiveClassifierUI : MonoBehaviour
{
    [Header("Source")]
    public LiveClassifier classifier;     // drag your existing LiveClassifier here

    [Header("UI (assign any you want)")]
    public TMP_Text labelText;
    public TMP_Text scoreText;
    public TMP_Text modelText;
    public TMP_Text headText;
    public TMP_Text fpsText;

    [Header("Formatting")]
    public string scoreFormat = "0.00";
    public string fpsFormat   = "0.0";

    [Header("Behavior")]
    public bool pollEachFrame = true;     // fallback: poll classifier state every frame

    float _lastFpsTime;
    int   _framesSince;
    string _lastShownLabel = "";
    float  _lastShownScore = float.NaN;

    void OnEnable()
    {
        if (classifier != null)
            classifier.OnPrediction += HandlePrediction;

        _lastFpsTime   = Time.realtimeSinceStartup;
        _framesSince   = 0;

        // initial UI state
        var initLabel = classifier != null ? classifier.LastLabel : "";
        var initScore = classifier != null ? classifier.LastScore : 0f;
        PushTexts(initLabel, initScore, force:true);

        if (modelText) modelText.text = classifier && classifier.embedder && classifier.embedder.modelAsset
            ? $"Model: {classifier.embedder.modelAsset.name}"
            : "Model: (none)";

        // head name from prefs or current head class count
        if (headText)
        {
            var last = PlayerPrefs.GetString("TinyTeach_LastHead", "");
            if (!string.IsNullOrEmpty(last)) headText.text = $"Head: {last}";
            else if (classifier != null && classifier.CurrentHead != null && classifier.CurrentHead.classes != null)
                headText.text = $"Head: ({classifier.CurrentHead.classes.Length} classes)";
            else headText.text = "Head: (unset)";
        }
    }

    void OnDisable()
    {
        if (classifier != null)
            classifier.OnPrediction -= HandlePrediction;
    }

    void Update()
    {
        // FPS (optional)
        _framesSince++;
        float dt = Time.realtimeSinceStartup - _lastFpsTime;
        if (dt >= 0.5f)
        {
            if (fpsText)
            {
                float fps = _framesSince / dt;
                fpsText.text = $"FPS: {fps.ToString(fpsFormat)}";
            }
            _framesSince = 0; _lastFpsTime = Time.realtimeSinceStartup;
        }

        // Keep model name up to date if you hot-swap models
        if (modelText && classifier && classifier.embedder)
        {
            var ma = classifier.embedder.modelAsset;
            modelText.text = $"Model: {(ma ? ma.name : "(none)")}";
        }

        // Fallback polling: if events didn’t fire (or missed), show the latest state
        if (pollEachFrame && classifier != null)
        {
            var lbl = classifier.LastLabel;
            var scr = classifier.LastScore;
            // Only push if changed to avoid string churn
            if (lbl != _lastShownLabel || scr != _lastShownScore)
                PushTexts(lbl, scr);
        }
    }

    void HandlePrediction(string label, float score)
    {
        // Event-driven update (preferred when it fires)
        PushTexts(label, score);
    }

    void PushTexts(string label, float score, bool force=false)
    {
        if (labelText && (force || label != _lastShownLabel))
            labelText.text = string.IsNullOrEmpty(label) ? "Label: —" : $"Label: {label}";

        if (scoreText && (force || score != _lastShownScore))
            scoreText.text = $"Score: {score.ToString(scoreFormat)}";

        _lastShownLabel = label;
        _lastShownScore = score;
    }
}
