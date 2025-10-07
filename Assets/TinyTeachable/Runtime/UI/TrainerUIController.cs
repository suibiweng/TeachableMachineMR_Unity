using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class TrainerUIController : MonoBehaviour
{
    [Header("Wiring")]
    public DynamicClassTrainer trainer;   // MUST be assigned in Inspector

    [Header("UI")]
    public TMP_InputField newClassInput;
    public TMP_Dropdown classDropdown;
    public TMP_InputField sessionInput;
    public TMP_InputField saveHeadInput;
    public TMP_InputField loadHeadInput;
    public Button addClassBtn;
    public Button addSampleBtn;
    public Button trainSaveBtn;
    public Button loadHeadBtn;
    public Button snapshotBtn;
    public TextMeshProUGUI statusText;

    void Start()
    {
        if (trainer == null)
        {
            Debug.LogError("[TrainerUI] trainer is NULL. Assign DynamicClassTrainer in the Inspector.");
            enabled = false;
            return;
        }

        Debug.Log("[TrainerUI] Start() OK. Wiring UI…");

        // seed fields
        if (sessionInput)  sessionInput.text  = trainer.sessionName;
        if (saveHeadInput) saveHeadInput.text = trainer.saveHeadName;
        if (loadHeadInput) loadHeadInput.text = trainer.loadHeadName;

        RefreshClassDropdown();

        // wire events
        if (classDropdown)
        {
            classDropdown.onValueChanged.AddListener(i =>
            {
                trainer.SelectClass(i);
                Debug.Log($"[TrainerUI] SelectClass({i}) -> {trainer.Classes[i]}");
            });
        }

        if (addClassBtn) addClassBtn.onClick.AddListener(() =>
        {
            var name = (newClassInput && !string.IsNullOrWhiteSpace(newClassInput.text))
                ? newClassInput.text.Trim()
                : "class_" + (trainer.Classes.Count + 1);

            trainer.AddClass(name);
            RefreshClassDropdown(selectLast: true);

            if (statusText) statusText.text = $"Added class '{name}'";
            Debug.Log($"[TrainerUI] AddClass('{name}')");
        });

        if (addSampleBtn) addSampleBtn.onClick.AddListener(() =>
        {
            trainer.AddSampleFromCurrent();
            if (statusText)
            {
                var label = (trainer.CurrentClassIndex >= 0 && trainer.CurrentClassIndex < trainer.Classes.Count)
                    ? trainer.Classes[trainer.CurrentClassIndex] : "(none)";
                statusText.text = $"Sample -> {label}  (D={trainer.EmbeddingDim})";
            }
            Debug.Log("[TrainerUI] AddSampleFromCurrent()");
        });

        if (trainSaveBtn) trainSaveBtn.onClick.AddListener(() =>
        {
            Debug.Log("[TrainerUI] TRAIN clicked");
            PullTextFields();
            // Dump state so we can see counts/dim BEFORE train
            trainer.DebugDump();                 // <-- requires the helper in trainer (see below)
            trainer.TrainAndSaveAndApply();      // <-- this IS the call
            if (statusText) statusText.text = $"Head saved/applied: {trainer.saveHeadName}";
            Debug.Log("[TrainerUI] TrainAndSaveAndApply() invoked");
        });

        if (loadHeadBtn) loadHeadBtn.onClick.AddListener(() =>
        {
            Debug.Log("[TrainerUI] LOAD clicked");
            PullTextFields();
            trainer.LoadHeadAndApply();
            RefreshClassDropdown(); // reload class list from head
            if (statusText) statusText.text = $"Head loaded: {trainer.loadHeadName}";
        });

        if (snapshotBtn) snapshotBtn.onClick.AddListener(() =>
        {
            Debug.Log("[TrainerUI] SNAPSHOT clicked");
            trainer.SaveSnapshotPNG("cap");
            if (statusText) statusText.text = "Snapshot saved.";
        });

        // Final sanity
        if (!UnityEngine.EventSystems.EventSystem.current)
            Debug.LogWarning("[TrainerUI] No EventSystem found in scene. Buttons won’t receive clicks.");
        if (trainSaveBtn && !trainSaveBtn.interactable)
            Debug.LogWarning("[TrainerUI] trainSaveBtn is not interactable.");
    }

    void PullTextFields()
    {
        if (sessionInput)  trainer.sessionName  = sessionInput.text;
        if (saveHeadInput) trainer.saveHeadName = saveHeadInput.text;
        if (loadHeadInput) trainer.loadHeadName = loadHeadInput.text;

        Debug.Log($"[TrainerUI] Fields -> session='{trainer.sessionName}', save='{trainer.saveHeadName}', load='{trainer.loadHeadName}'");
    }

    void RefreshClassDropdown(bool selectLast = false)
    {
        if (classDropdown == null) return;
        classDropdown.ClearOptions();
        classDropdown.AddOptions(new System.Collections.Generic.List<string>(trainer.Classes));
        classDropdown.value = selectLast ? Mathf.Max(0, trainer.Classes.Count - 1) : Mathf.Max(0, trainer.CurrentClassIndex);
        classDropdown.RefreshShownValue();
        Debug.Log($"[TrainerUI] RefreshClassDropdown -> count={trainer.Classes.Count}, current={classDropdown.value}");
    }
}
