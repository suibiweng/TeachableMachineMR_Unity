// Assets/TinyTeachable/Runtime/HeadAutoLoader.cs
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public class HeadAutoLoader : MonoBehaviour
{
    [Header("Links")]
    public LiveClassifier classifier;   // assign in Inspector (required)

    [Header("Settings")]
    public string headsFolder = "heads";                        // subfolder under persistentDataPath
    public string prefsKeyFile = "TinyTeach_LastHead";          // must match DetectionManager
    public string prefsKeyName = "TinyTeach_LastHeadName";      // optional, for logging/UI

    void Awake()
    {
        if (classifier == null)
        {
            classifier = FindObjectOfType<LiveClassifier>();
        }
    }

    void Start()
    {
        var file = PlayerPrefs.GetString(prefsKeyFile, "");
        if (string.IsNullOrEmpty(file))
        {
            Debug.Log("[HeadAutoLoader] No last head saved.");
            return;
        }

        var fullPath = Path.Combine(Application.persistentDataPath, headsFolder, file);
        if (!File.Exists(fullPath))
        {
            Debug.LogWarning("[HeadAutoLoader] Last head file missing: " + fullPath);
            return;
        }

        classifier.LoadHeadFromPath(fullPath);

        var name = PlayerPrefs.GetString(prefsKeyName, Path.GetFileNameWithoutExtension(file));
        Debug.Log("[HeadAutoLoader] Loaded last head on boot: " + name);
    }
}
