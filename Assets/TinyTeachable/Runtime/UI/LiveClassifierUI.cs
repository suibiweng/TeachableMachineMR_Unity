using UnityEngine;
using TMPro;                 // TextMeshPro
using UnityEngine.UI;       // UI Image

public class LiveClassifierUI : MonoBehaviour
{
    [Header("Wiring")]
    public LiveClassifier classifier;         // drag your LiveClassifier here

    [Header("UI Targets")]
    public TMP_Text modelText;                // NEW: shows ModelAsset name
    public TMP_Text labelText;                // shows predicted label
    public TMP_Text scoreText;                // shows confidence
    public Image    statusLight;              // optional: colored by confidence

    [Header("Display")]
    public string modelPrefix = "Model: ";
    public string labelPrefix = "Label: ";
    public string scorePrefix = "Score: ";
    [Range(0f,1f)] public float confGood = 0.80f;
    [Range(0f,1f)] public float confWarn = 0.50f;

    // cache to avoid re-writing the same model name every frame
    string _lastModelName = null;

    void OnEnable()
    {
        if (classifier != null)
            classifier.OnPrediction += HandlePrediction;
        RefreshModelName(); // try once now
    }

    void OnDisable()
    {
        if (classifier != null)
            classifier.OnPrediction -= HandlePrediction;
    }

    void Start()
    {
        // If classifier already predicted before UI enabled, populate immediately
        if (classifier != null && !string.IsNullOrEmpty(classifier.LastLabel))
            HandlePrediction(classifier.LastLabel, classifier.LastScore);
        RefreshModelName();
    }

    void Update()
    {
        // In case you hot-swap the ModelAsset at runtime, keep the name fresh (cheap check)
        RefreshModelName();
    }

    void RefreshModelName()
    {
        if (modelText == null || classifier == null) return;

        // Pull name from SentisEmbedder's ModelAsset (UnityEngine.Object has .name)
        var embedder = classifier.embedder;
        string name = (embedder != null && embedder.modelAsset != null) ? embedder.modelAsset.name : "(no model)";

        if (!string.Equals(name, _lastModelName))
        {
            _lastModelName = name;
            modelText.text = $"{modelPrefix}{name}";
        }
    }

    void HandlePrediction(string label, float score)
    {
        if (labelText) labelText.text = $"{labelPrefix}{label}";
        if (scoreText) scoreText.text = $"{scorePrefix}{score:F2}";

        if (statusLight)
        {
            // simple traffic light based on confidence
            if (score >= confGood)      statusLight.color = new Color(0.25f, 0.85f, 0.35f); // green
            else if (score >= confWarn) statusLight.color = new Color(0.95f, 0.75f, 0.25f); // yellow
            else                        statusLight.color = new Color(0.9f, 0.35f, 0.35f);  // red
        }
    }
}
