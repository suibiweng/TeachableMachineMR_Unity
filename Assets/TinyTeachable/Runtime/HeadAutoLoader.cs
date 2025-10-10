// Assets/TinyTeachable/Runtime/HeadAutoLoader.cs
using System.IO;
using UnityEngine;

/// <summary>
/// Loads a saved head once on startup, but will NOT override a head chosen by the user (Live UI).
/// It reads/writes PlayerPrefs with the same key the Live UI uses, so both stay in sync.
/// </summary>
[DisallowMultipleComponent]
public class HeadAutoLoader : MonoBehaviour
{
    [Header("Links")]
    public LiveClassifier classifier;

    [Header("Behavior")]
    [Tooltip("If true, load a head automatically on Start() (honoring user choice from PlayerPrefs).")]
    public bool loadOnStart = true;

    [Tooltip("Do NOT override a user-chosen head (from Live UI). If true, once the user picks, autoloader stops re-applying.")]
    public bool doNotOverrideUserChoice = true;

    [Tooltip("If no PlayerPrefs value is found, try to load this head name on first run.")]
    public string defaultHeadName = ""; // e.g., "Test" (no extension)

    [Tooltip("PlayerPrefs key shared with LiveClassifierUI to remember the last chosen head.")]
    public string lastHeadPlayerPrefKey = "TTM_LastHead";

    // internal guard: once we see any head set (either by us or UI), we won't auto-apply again if doNotOverrideUserChoice is true.
    private bool _hasLockedToUserChoice = false;
    private bool _didApplyOnce = false;

    void Reset()
    {
        if (!classifier) classifier = GetComponent<LiveClassifier>();
    }

    void OnEnable()
    {
        if (classifier != null)
            classifier.OnHeadChanged += HandleHeadChanged;
    }

    void OnDisable()
    {
        if (classifier != null)
            classifier.OnHeadChanged -= HandleHeadChanged;
    }

    void Start()
    {
        if (!loadOnStart) return;
        if (classifier == null)
        {
            Debug.LogWarning("[AutoLoader] No LiveClassifier assigned.");
            return;
        }

        // 1) honor last saved choice from UI (if any)
        string chosen = PlayerPrefs.GetString(lastHeadPlayerPrefKey, string.Empty);

        // 2) fallback to default if none saved yet
        string target = !string.IsNullOrEmpty(chosen) ? chosen : defaultHeadName;

        if (string.IsNullOrEmpty(target))
        {
            Debug.Log("[AutoLoader] No last head and no default set. Skipping.");
            return;
        }

        // Don't re-apply if already active
        if (!string.IsNullOrEmpty(classifier.CurrentHeadName) &&
            string.Equals(classifier.CurrentHeadName, target, System.StringComparison.Ordinal))
        {
            Debug.Log($"[AutoLoader] Head '{target}' already active. Skipping.");
            _didApplyOnce = true;
            if (doNotOverrideUserChoice) _hasLockedToUserChoice = true;
            return;
        }

        // Apply once from file
        if (TryLoadHeadByName(target))
        {
            _didApplyOnce = true;
            if (doNotOverrideUserChoice) _hasLockedToUserChoice = true;
            Debug.Log($"[AutoLoader] Applied head on start: '{target}'");
        }
        else
        {
            Debug.LogWarning($"[AutoLoader] Could not load head '{target}'.");
        }
    }

    // Called whenever the classifier's head changes (by UI or by us)
    private void HandleHeadChanged(HeadData _)
    {
        var name = classifier != null ? classifier.CurrentHeadName : "";
        if (string.IsNullOrEmpty(name)) return;

        // Persist the new choice so we'll honor it next launch
        PlayerPrefs.SetString(lastHeadPlayerPrefKey, name);

        // If we shouldn't override user choice, lock after the first successful change
        if (doNotOverrideUserChoice)
        {
            _hasLockedToUserChoice = true;
        }

        // From now on the autoloader will not force another head unless you disable doNotOverrideUserChoice
        Debug.Log($"[AutoLoader] Observed head change -> '{name}'. Locked={_hasLockedToUserChoice}");
    }

    /// <summary>
    /// Try to load a head JSON by name (no extension) from persistent 'heads' folder.
    /// </summary>
    private bool TryLoadHeadByName(string headName)
    {
        if (classifier == null || string.IsNullOrEmpty(headName)) return false;
        if (doNotOverrideUserChoice && _hasLockedToUserChoice) return false;

        var path = Path.Combine(Application.persistentDataPath, "heads", headName + ".json");
        if (!File.Exists(path)) return false;

        classifier.LoadHeadFromPath(path); // will set CurrentHeadName from filename
        return true;
    }

    // Optional public API if another script wants to request a load through the autoloader
    public bool RequestLoad(string headName, bool force = false)
    {
        if (classifier == null) return false;
        if (!force && doNotOverrideUserChoice && _hasLockedToUserChoice) return false;

        return TryLoadHeadByName(headName);
    }
}
