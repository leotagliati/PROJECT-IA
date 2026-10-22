using System.Linq;
using Assets.Scripts.Seeker;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Monta o final (<see cref="EscapeCutscene"/>) na cena aberta, a partir do que já foi posto à mão: o objeto
/// "CutsceneTrigger", o "FallenDoor", o <see cref="SeekerChasePuppet"/> e a porta de saída (o <see cref="Lockable"/>
/// com mais cadeados). Cria o que falta — câmera na cabeça do Seeker, "EscapeCheckpoint", "EscapeTrigger" e o texto de
/// "aperte qualquer botão" — e liga as referências. Pode rodar de novo: reaproveita o que já existe pelo nome.
///
/// As posições criadas são palpites pela geometria (raycasts no corredor); confira na Scene antes de salvar.
/// </summary>
public static class EscapeSceneSetup
{
    private const string CreditsScenePath = "Assets/Scenes/Credits.unity";

    // O que fica na tela durante o jogo; a cutscene esconde e devolve. As dicas se calam sozinhas (PauseLocked).
    private static readonly string[] HudNames = { "Crosshair", "StaminaBar", "KeyUI" };

    [MenuItem("PROJECT-IA/Final: montar cutscene na cena aberta")]
    public static void Setup()
    {
        Undo.SetCurrentGroupName("Montar final");
        int undoGroup = Undo.GetCurrentGroup();

        GameObject trigger = FindByName("CutsceneTrigger");
        GameObject fallenDoor = FindByName("FallenDoor");
        SeekerChasePuppet seeker = Object.FindObjectsByType<SeekerChasePuppet>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
        GameObject standingDoor = FindExitDoor();

        if (trigger == null || seeker == null || standingDoor == null)
        {
            Debug.LogError("Final: preciso de um objeto \"CutsceneTrigger\", de um SeekerChasePuppet e de uma porta com Lockable na cena aberta.");
            return;
        }

        Physics.SyncTransforms();

        // Corredor: da porta para o trigger, no plano.
        Vector3 along = trigger.transform.position - standingDoor.transform.position;
        along.y = 0f;
        along = along.sqrMagnitude > 0.01f ? along.normalized : trigger.transform.forward;

        FixSeekerBody(seeker);
        Camera seekerCamera = SetupSeekerCamera(seeker);
        Transform checkpoint = SetupCheckpoint(standingDoor, trigger.transform.parent, along);
        SetupEscapeTrigger(trigger.transform, along);
        SetupCutscene(trigger, seeker, seekerCamera, checkpoint, standingDoor, fallenDoor);
        SetupWinScreen();

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(trigger.scene);
        Selection.activeGameObject = trigger;
        Debug.Log("Final montado. Confira as posições de EscapeCheckpoint, EscapeTrigger e SeekerCamera e salve a cena.", trigger);
    }

    // Move por transform: com física de verdade, a gravidade e o empurrão do player brigam com o movimento. O
    // collider vira trigger (é por ele que pega); o Rigidbody kinematic fica para o evento de trigger chegar.
    private static void FixSeekerBody(SeekerChasePuppet seeker)
    {
        if (!seeker.TryGetComponent(out Rigidbody body))
            body = Undo.AddComponent<Rigidbody>(seeker.gameObject);

        Undo.RecordObject(body, "Montar final");
        body.isKinematic = true;
        body.useGravity = false;

        foreach (Collider collider in seeker.GetComponents<Collider>())
        {
            Undo.RecordObject(collider, "Montar final");
            collider.isTrigger = true;
        }
    }

    private static Camera SetupSeekerCamera(SeekerChasePuppet seeker)
    {
        Camera existing = seeker.GetComponentInChildren<Camera>(true);
        if (existing != null)
            return existing;

        Transform head = FindHeadBone(seeker.transform);
        var go = new GameObject("SeekerCamera");
        Undo.RegisterCreatedObjectUndo(go, "Montar final");
        go.transform.SetParent(head, false);

        Camera camera = go.AddComponent<Camera>();
        PlayerCamera playerLook = Object.FindFirstObjectByType<PlayerCamera>();
        if (playerLook != null && playerLook.playerCamera != null)
            camera.CopyFrom(playerLook.playerCamera);

        // CopyFrom leva junto a transform e a RenderTexture do player; a textura o EscapeCutscene põe na hora do plano.
        camera.targetTexture = null;
        // A câmera do player pode esconder o próprio corpo por layer; daqui de fora o corpo tem que aparecer.
        camera.cullingMask = ~0;
        camera.enabled = false;

        go.transform.position = head.position + seeker.transform.forward * 0.25f;
        go.transform.rotation = Quaternion.LookRotation(seeker.transform.forward, Vector3.up);
        return camera;
    }

    private static Transform FindHeadBone(Transform root)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        foreach (string name in new[] { "spine.006", "spine.005", "spine.004", "head", "Head" })
        {
            Transform bone = all.FirstOrDefault(t => t.name == name);
            if (bone != null)
                return bone;
        }

        Debug.LogWarning("Final: osso da cabeça não achado; a SeekerCamera ficou na raiz do Seeker.", root);
        return root;
    }

    // Antes da porta de saída, olhando para ela: o player renasce aqui e passa pela cutscene de novo.
    private static Transform SetupCheckpoint(GameObject standingDoor, Transform parent, Vector3 along)
    {
        GameObject go = FindByName("EscapeCheckpoint");
        if (go != null)
            return go.transform;

        go = new GameObject("EscapeCheckpoint");
        Undo.RegisterCreatedObjectUndo(go, "Montar final");
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(Ground(standingDoor.transform.position - along * 2.5f),
            Quaternion.LookRotation(along, Vector3.up));
        return go.transform;
    }

    // No chão: o pivô do player é o pé.
    private static Vector3 Ground(Vector3 point) =>
        Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit ground, 10f, ~0, QueryTriggerInteraction.Ignore)
            ? ground.point
            : point;

    private static void SetupEscapeTrigger(Transform trigger, Vector3 along)
    {
        if (Object.FindFirstObjectByType<EscapeTrigger>(FindObjectsInactive.Include) != null)
            return;

        Vector3 eye = Ground(trigger.position) + Vector3.up * 1.2f;
        float distance = 30f;
        if (Physics.Raycast(eye, along, out RaycastHit end, 500f, ~0, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(2f, end.distance - 2f);
        else
            Debug.LogWarning("Final: não achei o fim do corredor; o EscapeTrigger ficou a 30 m do CutsceneTrigger.");

        Vector3 center = eye + along * distance;
        Vector3 side = Vector3.Cross(Vector3.up, along);
        float width = Probe(center, side) + Probe(center, -side);

        var go = new GameObject("EscapeTrigger");
        Undo.RegisterCreatedObjectUndo(go, "Montar final");
        go.transform.SetParent(trigger.parent, false);
        go.transform.SetPositionAndRotation(center, Quaternion.LookRotation(along, Vector3.up));

        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(Mathf.Max(width, 2f), 4f, 1f);
        go.AddComponent<EscapeTrigger>();

        Debug.Log($"Final: EscapeTrigger a {distance:0.0} m do CutsceneTrigger.", go);
    }

    private static float Probe(Vector3 from, Vector3 direction) =>
        Physics.Raycast(from, direction, out RaycastHit hit, 20f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 3f;

    private static void SetupCutscene(GameObject trigger, SeekerChasePuppet seeker, Camera seekerCamera,
        Transform checkpoint, GameObject standingDoor, GameObject fallenDoor)
    {
        if (trigger.TryGetComponent(out Collider collider) && !collider.isTrigger)
        {
            Undo.RecordObject(collider, "Montar final");
            collider.isTrigger = true;
        }

        EscapeCutscene cutscene = trigger.GetComponent<EscapeCutscene>();
        if (cutscene == null)
            cutscene = Undo.AddComponent<EscapeCutscene>(trigger);

        var so = new SerializedObject(cutscene);
        so.FindProperty("seeker").objectReferenceValue = seeker;
        so.FindProperty("seekerCamera").objectReferenceValue = seekerCamera;
        so.FindProperty("checkpoint").objectReferenceValue = checkpoint;

        // O alvo da virada na morte: o mesmo objeto que o monstro de IA tem, copiado junto com o modelo.
        Transform deathLook = seeker.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "DeathLookTarget");
        so.FindProperty("deathLookTarget").objectReferenceValue = deathLook != null ? deathLook : seekerCamera.transform;
        so.FindProperty("standingDoor").objectReferenceValue = standingDoor;
        so.FindProperty("fallenDoor").objectReferenceValue = fallenDoor;

        SerializedProperty hud = so.FindProperty("hideDuringCutscene");
        if (hud.arraySize == 0)
        {
            GameObject[] found = HudNames.Select(FindByName).Where(go => go != null).ToArray();
            hud.arraySize = found.Length;
            for (int i = 0; i < found.Length; i++)
                hud.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

            if (found.Length < HudNames.Length)
                Debug.LogWarning($"Final: só achei {found.Length} de {HudNames.Length} itens de HUD ({string.Join(", ", HudNames)}).", trigger);
        }

        so.ApplyModifiedProperties();

        if (fallenDoor == null)
            Debug.LogWarning("Final: nenhum \"FallenDoor\" na cena; a porta só some no estrondo.", trigger);
    }

    private static void SetupWinScreen()
    {
        WinScreenUI win = Object.FindFirstObjectByType<WinScreenUI>(FindObjectsInactive.Include);
        if (win == null)
        {
            Debug.LogWarning("Final: nenhuma WinScreenUI na cena.");
            return;
        }

        var so = new SerializedObject(win);

        SerializedProperty credits = so.FindProperty("creditsScene.sceneAsset");
        if (credits.objectReferenceValue == null)
            credits.objectReferenceValue = AssetDatabase.LoadAssetAtPath<SceneAsset>(CreditsScenePath);

        // "Aperte qualquer botão": cópia do texto da vitória, menor e mais abaixo.
        SerializedProperty continueLabel = so.FindProperty("continueLabel");
        var message = so.FindProperty("messageLabel").objectReferenceValue as TMP_Text;
        if (continueLabel.objectReferenceValue == null && message != null)
        {
            GameObject copy = Object.Instantiate(message.gameObject, message.transform.parent);
            Undo.RegisterCreatedObjectUndo(copy, "Montar final");
            copy.name = "Continue (TMP)";

            TMP_Text label = copy.GetComponent<TMP_Text>();
            label.text = string.Empty;
            label.fontSize = message.fontSize * 0.5f;

            var rect = (RectTransform)copy.transform;
            rect.anchoredPosition = ((RectTransform)message.transform).anchoredPosition + Vector2.down * 120f;

            continueLabel.objectReferenceValue = label;
        }

        so.ApplyModifiedProperties();
    }

    // A porta de saída é a que pede mais chaves.
    private static GameObject FindExitDoor()
    {
        return Object.FindObjectsByType<Lockable>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .OrderByDescending(l => new SerializedObject(l).FindProperty("locks").arraySize)
            .Select(l => l.gameObject)
            .FirstOrDefault();
    }

    private static GameObject FindByName(string name) =>
        Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(t => t.name == name && t.gameObject.scene.IsValid())
            .Select(t => t.gameObject)
            .FirstOrDefault();
}
