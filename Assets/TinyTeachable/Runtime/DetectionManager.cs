// Assets/TinyTeachable/Runtime/DetectionManager.cs
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[DisallowMultipleComponent]
public class DetectionManager : MonoBehaviour
{
    [Header("Links")]
    public DynamicClassTrainer trainer;   // assign
    public LiveClassifier classifier;     // optional

    public string HeadsDir => Path.Combine(Application.persistentDataPath, "heads");
    const string kPrefsLastHeadFile = "TinyTeach_LastHead";      // e.g., "CupTouch.json"
    const string kPrefsLastHeadName = "TinyTeach_LastHeadName";  // e.g., "CupTouch"

    void Awake()
    {
        Directory.CreateDirectory(HeadsDir);
    }

    public void CreateDetection(string detectionName, IEnumerable<string> seedClasses = null)
    {
        var name = Sanitize(detectionName);
        if (string.IsNullOrEmpty(name)) { Debug.LogError("[DetectionMgr] Empty detection name."); return; }

        trainer.sessionName  = name;
        trainer.saveHeadName = name + ".json";
        trainer.loadHeadName = name + ".json";

        if (seedClasses != null)
            trainer.ResetAll(new List<string>(seedClasses).ToArray());
        else
            trainer.ResetAll(Array.Empty<string>());

        Debug.Log($"[DetectionMgr] New detection '{name}' initialized.");
    }

    public void TrainDetection()
    {
        trainer.TrainAndSaveAndApply();

        // Remember last head (file + name)
        if (!string.IsNullOrEmpty(trainer.saveHeadName))
        {
            PlayerPrefs.SetString(kPrefsLastHeadFile, trainer.saveHeadName);
            PlayerPrefs.SetString(kPrefsLastHeadName, Path.GetFileNameWithoutExtension(trainer.saveHeadName));
            PlayerPrefs.Save();
        }
    }

    public void SetDetection(string detectionName)
    {
        var file = Path.Combine(HeadsDir, Sanitize(detectionName) + ".json");
        if (!File.Exists(file)) { Debug.LogError("[DetectionMgr] Head missing: " + file); return; }

        // Single source of truth: trainer loads & applies
        trainer.loadHeadName = Path.GetFileName(file);
        trainer.LoadHeadAndApply();

        // Remember last head (file + name)
        PlayerPrefs.SetString(kPrefsLastHeadFile, Path.GetFileName(file));
        PlayerPrefs.SetString(kPrefsLastHeadName, detectionName);
        PlayerPrefs.Save();

        Debug.Log("[DetectionMgr] Switched to detection: " + detectionName);
    }

    public void LoadDetectionForEditing(string detectionName, bool resetSamplesToHeadClasses = true)
    {
        var name = Sanitize(detectionName);
        var file = Path.Combine(HeadsDir, name + ".json");
        if (!File.Exists(file)) { Debug.LogError("[DetectionMgr] Head missing: " + file); return; }

        string[] classes = null;
        try
        {
            var json = File.ReadAllText(file);
            var proxy = JsonUtility.FromJson<HeadClassesProxy>(json);
            classes = proxy?.classes;
        }
        catch (Exception e)
        {
            Debug.LogError("[DetectionMgr] Failed parsing classes from '" + file + "': " + e.Message);
        }

        trainer.sessionName  = name;
        trainer.saveHeadName = name + ".json";
        trainer.loadHeadName = name + ".json";

        if (resetSamplesToHeadClasses && classes != null)
            trainer.ResetAll(classes);
        else
            trainer.ResetAll(trainer.Classes != null ? trainer.Classes.ToArray() : Array.Empty<string>());

        trainer.LoadHeadAndApply();
        Debug.Log($"[DetectionMgr] Loaded '{name}' for retrain.");
    }

    public bool RemoveDetection(string detectionName)
    {
        var file = Path.Combine(HeadsDir, Sanitize(detectionName) + ".json");
        if (!File.Exists(file)) { Debug.LogWarning("[DetectionMgr] Remove: head not found " + file); return false; }

        try
        {
            File.Delete(file);
            Debug.Log($"[DetectionMgr] Removed detection '{detectionName}'.");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[DetectionMgr] Remove failed: " + e.Message);
            return false;
        }
    }

    public List<DetectionInfo> ListDetections()
    {
        var list = new List<DetectionInfo>();
        foreach (var f in Directory.GetFiles(HeadsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(f);
                var proxy = JsonUtility.FromJson<HeadClassesProxy>(json);
                list.Add(new DetectionInfo
                {
                    name = Path.GetFileNameWithoutExtension(f),
                    classes = proxy?.classes ?? Array.Empty<string>(),
                    path = f
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DetectionMgr] Bad head '" + f + "': " + e.Message);
            }
        }
        return list;
    }

    public string ExportAllAsJson()
    {
        var infos = ListDetections();
        return JsonUtility.ToJson(new DetectionCatalog { detections = infos.ToArray() }, true);
    }

    static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    [Serializable] public class DetectionInfo { public string name; public string[] classes; public string path; }
    [Serializable] public class DetectionCatalog { public DetectionInfo[] detections; }
    [Serializable] class HeadClassesProxy { public string[] classes; }
}
