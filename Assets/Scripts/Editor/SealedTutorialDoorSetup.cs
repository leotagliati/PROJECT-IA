using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Liga a porta do tutorial ao <see cref="ToggleWalls"/>: o Apply fecha e lacra a porta, e quem tenta abrir depois
/// ouve o diálogo <c>PortaTutorialLacrada</c>. A porta é a selecionada (um <see cref="SwingDoor"/>) ou, sem seleção,
/// a porta com <see cref="Lockable"/> de menos cadeados que não seja a de saída. Pode rodar de novo.
/// </summary>
public static class SealedTutorialDoorSetup
{
    private const string DialoguePath = "Assets/Resources/Dialogues/PortaTutorialLacrada.asset";
    private const string RepeatDialoguePath = "Assets/Resources/Dialogues/PortaTutorialLacradaRepetida.asset";
    private const string TriggerName = "TutorialDoorSealedDialogue";

    [MenuItem("PROJECT-IA/Final: lacrar porta do tutorial na cena aberta")]
    public static void Setup()
    {
        ToggleWalls walls = Object.FindObjectsByType<ToggleWalls>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
        SwingDoor door = FindTutorialDoor();
        DialogueAsset dialogue = AssetDatabase.LoadAssetAtPath<DialogueAsset>(DialoguePath);

        if (walls == null || door == null || dialogue == null)
        {
            Debug.LogError($"Porta do tutorial: preciso de um ToggleWalls, de uma SwingDoor com Lockable e de {DialoguePath}.");
            return;
        }

        Undo.SetCurrentGroupName("Lacrar porta do tutorial");
        int undoGroup = Undo.GetCurrentGroup();

        AddSealedDoor(walls, door);
        DialogueTrigger trigger = SetupDialogueTrigger(walls.transform.parent, dialogue);
        WireRefused(door.GetComponent<Lockable>(), trigger);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(walls.gameObject.scene);
        Selection.activeGameObject = door.gameObject;
        Debug.Log($"Porta do tutorial: '{door.name}' lacrada pelo '{walls.name}'. Confira e salve a cena.", door);
    }

    private static SwingDoor FindTutorialDoor()
    {
        if (Selection.activeGameObject != null)
        {
            SwingDoor selected = Selection.activeGameObject.GetComponentInParent<SwingDoor>();
            if (selected != null && selected.GetComponent<Lockable>() != null)
                return selected;
        }

        Lockable[] lockables = Object.FindObjectsByType<Lockable>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .OrderBy(LockCount)
            .ToArray();

        // A de saída é a de mais cadeados (mesma regra do EscapeSceneSetup); a do tutorial, a de menos que ainda tranca.
        Lockable exit = lockables.LastOrDefault();
        return lockables
            .Where(l => l != exit && LockCount(l) > 0)
            .Select(l => l.GetComponent<SwingDoor>())
            .FirstOrDefault(d => d != null);
    }

    private static int LockCount(Lockable lockable) => new SerializedObject(lockable).FindProperty("locks").arraySize;

    private static void AddSealedDoor(ToggleWalls walls, SwingDoor door)
    {
        var so = new SerializedObject(walls);
        SerializedProperty doors = so.FindProperty("sealedDoors");

        for (int i = 0; i < doors.arraySize; i++)
            if (doors.GetArrayElementAtIndex(i).objectReferenceValue == door)
                return;

        doors.InsertArrayElementAtIndex(doors.arraySize);
        doors.GetArrayElementAtIndex(doors.arraySize - 1).objectReferenceValue = door;
        so.ApplyModifiedProperties();
    }

    // Sem collider e fora da porta: o DialogueTrigger também é IInteractable e, na porta, tomaria o raycast dela.
    private static DialogueTrigger SetupDialogueTrigger(Transform parent, DialogueAsset dialogue)
    {
        GameObject go = GameObject.Find(TriggerName);
        if (go == null)
        {
            go = new GameObject(TriggerName);
            Undo.RegisterCreatedObjectUndo(go, "Lacrar porta do tutorial");
            Undo.SetTransformParent(go.transform, parent, "Lacrar porta do tutorial");
        }

        if (!go.TryGetComponent(out DialogueTrigger trigger))
            trigger = Undo.AddComponent<DialogueTrigger>(go);

        var so = new SerializedObject(trigger);
        so.FindProperty("dialogue").objectReferenceValue = dialogue;
        so.FindProperty("repeatDialogue").objectReferenceValue = AssetDatabase.LoadAssetAtPath<DialogueAsset>(RepeatDialoguePath);
        so.FindProperty("once").boolValue = false;
        so.FindProperty("rememberAs").stringValue = "";
        so.ApplyModifiedProperties();
        return trigger;
    }

    private static void WireRefused(Lockable lockable, DialogueTrigger trigger)
    {
        FieldInfo field = typeof(Lockable).GetField("sealedRefused", BindingFlags.Instance | BindingFlags.NonPublic);
        var refused = (UnityEvent)field.GetValue(lockable);

        for (int i = 0; i < refused.GetPersistentEventCount(); i++)
            if (refused.GetPersistentTarget(i) == trigger && refused.GetPersistentMethodName(i) == nameof(DialogueTrigger.Play))
                return;

        Undo.RecordObject(lockable, "Lacrar porta do tutorial");
        UnityEventTools.AddVoidPersistentListener(refused, trigger.Play);
        EditorUtility.SetDirty(lockable);
    }
}
