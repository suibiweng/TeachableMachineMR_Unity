using UnityEngine;
using System.IO;

[DisallowMultipleComponent]
public class HeadAutoLoader : MonoBehaviour
{
    public LiveClassifier classifier;   // assign
    public DynamicClassTrainer trainer; // optional: keeps trainer UI in sync

    string PD => Application.persistentDataPath;

    void Awake()
    {
        if (trainer) trainer.OnHeadTrained += HandleHeadTrained;
    }

    void OnDestroy()
    {
        if (trainer) trainer.OnHeadTrained -= HandleHeadTrained;
    }

    void Start()
    {
        var last = PlayerPrefs.GetString("TinyTeach_LastHead", "");
        if (!string.IsNullOrEmpty(last)) LoadHead(last);
    }

    void HandleHeadTrained(string fileName, HeadData head)
    {
        // remember and auto-load next run
        PlayerPrefs.SetString("TinyTeach_LastHead", fileName);
        PlayerPrefs.Save();
        Debug.Log("[HeadAutoLoader] Remembered head: " + fileName);
    }

    public void LoadHead(string fileName)
    {
        if (classifier == null) { Debug.LogError("[HeadAutoLoader] No classifier."); return; }
        var path = Path.Combine(PD, "heads", fileName);
        if (!File.Exists(path)) { Debug.LogWarning("[HeadAutoLoader] Head not found: " + path); return; }

        var json = File.ReadAllText(path);
        var head = JsonUtility.FromJson<HeadData>(json);
        classifier.SetHead(head);
        if (trainer != null && head.classes != null) trainer.ResetAll(new System.Collections.Generic.List<string>(head.classes));

        Debug.Log("[HeadAutoLoader] Loaded head -> " + path);
    }
}
