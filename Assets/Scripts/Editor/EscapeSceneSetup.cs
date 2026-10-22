using System.Linq;
using Assets.Scripts.Seeker;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

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
    private const string WinFontPath = "Assets/TextMesh Pro/Fonts/determination SDF.asset";
    private const string SkullPath = "Assets/Images/HUD/skull.png";

    // Paleta da tela de vitória: o clarão assenta num branco quente, texto quase preto.
    private static readonly Color WinBackground = new Color32(0xF3, 0xF0, 0xE8, 0xFF);
    private static readonly Color WinInk = new Color32(0x2A, 0x2A, 0x28, 0xFF);
    private static readonly Color WinMuted = new Color32(0x7C, 0x7A, 0x73, 0xFF);
    private static readonly Color WinFaint = new Color32(0x9C, 0x99, 0x8F, 0xFF);

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

    [MenuItem("PROJECT-IA/Final: montar tela de vitória")]
    public static void SetupWinScreenMenu()
    {
        SetupWinScreen();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    // A tela "J": fundo off-white, bloco de texto à esquerda, "aperte qualquer botão" no canto inferior direito.
    // Medidas no Canvas da cena (referência 800 de largura); refaz a arrumação inteira a cada vez que roda.
    private static void SetupWinScreen()
    {
        WinScreenUI win = Object.FindFirstObjectByType<WinScreenUI>(FindObjectsInactive.Include);
        if (win == null)
        {
            Debug.LogWarning("Final: nenhuma WinScreenUI na cena.");
            return;
        }

        var root = (RectTransform)win.transform;
        Undo.RecordObject(root, "Montar tela de vitória");
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        if (win.TryGetComponent(out Image background))
        {
            Undo.RecordObject(background, "Montar tela de vitória");
            background.color = WinBackground;
            background.raycastTarget = false;
        }
        else
            Debug.LogWarning("Final: a WinScreen não tem Image de fundo; adicione uma para o off-white.", win);

        var so = new SerializedObject(win);
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(WinFontPath);

        var leftBlock = new Vector2(0.09f, 0.5f);
        TMP_Text title = Label(root, so.FindProperty("messageLabel").objectReferenceValue as TMP_Text, "Title", font,
            leftBlock, new Vector2(0f, 1f), new Vector2(0f, 96f), new Vector2(700f, 72f), 68f, WinInk, "Você escapou.");
        RectTransform divider = Line(root, leftBlock, new Vector2(0f, -2f), new Vector2(76f, 4f));
        TMP_Text caption = Label(root, so.FindProperty("timeCaption").objectReferenceValue as TMP_Text, "TimeCaption", font,
            leftBlock, new Vector2(0f, 1f), new Vector2(0f, -27f), new Vector2(400f, 28f), 21f, WinMuted, "Tempo de jogo");
        TMP_Text time = Label(root, so.FindProperty("timeLabel").objectReferenceValue as TMP_Text, "TimeValue", font,
            leftBlock, new Vector2(0f, 1f), new Vector2(0f, -55f), new Vector2(400f, 42f), 35f, WinInk, "12:47");
        TMP_Text prompt = Label(root, so.FindProperty("continueLabel").objectReferenceValue as TMP_Text, "Continue", font,
            new Vector2(0.93f, 0.09f), new Vector2(1f, 0f), Vector2.zero, new Vector2(400f, 24f), 17f, WinFaint,
            "Aperte qualquer botão");
        prompt.alignment = TextAlignmentOptions.BottomRight;

        // Segunda coluna, ao lado do tempo: "Mortes", caveira e o número.
        TMP_Text deathsCaption = Label(root, so.FindProperty("deathsCaption").objectReferenceValue as TMP_Text,
            "DeathsCaption", font, leftBlock, new Vector2(0f, 1f), new Vector2(170f, -27f), new Vector2(200f, 28f), 21f,
            WinMuted, "Mortes");
        RectTransform skull = Icon(root, "DeathsIcon", SkullSprite(), leftBlock, new Vector2(170f, -62f), new Vector2(24f, 24f));
        TMP_Text deaths = Label(root, so.FindProperty("deathsLabel").objectReferenceValue as TMP_Text, "DeathsValue", font,
            leftBlock, new Vector2(0f, 1f), new Vector2(202f, -55f), new Vector2(200f, 42f), 35f, WinInk, "3");
        so.FindProperty("deathsCaption").objectReferenceValue = deathsCaption;
        so.FindProperty("deathsIcon").objectReferenceValue = skull.GetComponent<Image>();
        so.FindProperty("deathsLabel").objectReferenceValue = deaths;

        so.FindProperty("messageLabel").objectReferenceValue = title;
        so.FindProperty("message").stringValue = "Você escapou.";
        so.FindProperty("divider").objectReferenceValue = divider;
        so.FindProperty("timeCaption").objectReferenceValue = caption;
        so.FindProperty("timeLabel").objectReferenceValue = time;
        so.FindProperty("continueLabel").objectReferenceValue = prompt;

        SerializedProperty credits = so.FindProperty("creditsScene.sceneAsset");
        if (credits.objectReferenceValue == null)
            credits.objectReferenceValue = AssetDatabase.LoadAssetAtPath<SceneAsset>(CreditsScenePath);

        so.ApplyModifiedProperties();

        // O clarão é o único fade de entrada; na mesma cor do fundo, a tela entra por baixo dele sem troca visível.
        PlayerEscapedSequence escaped = Object.FindFirstObjectByType<PlayerEscapedSequence>(FindObjectsInactive.Include);
        var flash = escaped != null
            ? new SerializedObject(escaped).FindProperty("whiteFade").objectReferenceValue as CanvasGroup
            : null;
        // Só a imagem do próprio objeto, nunca os filhos: na cena o clarão é o CanvasGroup da própria WinScreen, e
        // pintar os filhos deixaria os textos da cor do fundo.
        if (flash != null)
        {
            if (flash.gameObject != win.gameObject && flash.TryGetComponent(out Graphic graphic))
            {
                Undo.RecordObject(graphic, "Montar tela de vitória");
                graphic.color = WinBackground;
            }
        }
        else
            Debug.LogWarning("Final: clarão (whiteFade da PlayerEscapedSequence) não achado; pinte a imagem dele de #F3F0E8.", win);

        if (font == null)
            Debug.LogWarning($"Final: fonte não achada em {WinFontPath}; os textos ficaram na fonte que já tinham.", win);
        Debug.Log("Tela de vitória montada.", win);
    }

    private static TMP_Text Label(RectTransform root, TMP_Text existing, string name, TMP_FontAsset font, Vector2 anchor,
        Vector2 pivot, Vector2 position, Vector2 size, float fontSize, Color color, string preview)
    {
        TMP_Text label = existing;
        if (label == null)
        {
            Transform child = root.Find(name);
            label = child != null ? child.GetComponent<TMP_Text>() : null;
        }

        if (label == null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            Undo.RegisterCreatedObjectUndo(go, "Montar tela de vitória");
            go.transform.SetParent(root, false);
            label = go.GetComponent<TMP_Text>();
        }

        Undo.RecordObject(label.gameObject, "Montar tela de vitória");
        Undo.RecordObject(label.rectTransform, "Montar tela de vitória");
        Undo.RecordObject(label, "Montar tela de vitória");

        label.gameObject.name = name;
        Place(label.rectTransform, anchor, pivot, position, size);
        if (font != null)
            label.font = font;
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = pivot.x > 0.5f ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        // Prévia no Editor; em jogo a WinScreenUI apaga e escreve na hora certa.
        label.text = preview;
        return label;
    }

    private static RectTransform Line(RectTransform root, Vector2 anchor, Vector2 position, Vector2 size)
    {
        Transform child = root.Find("Divider");
        Image line = child != null ? child.GetComponent<Image>() : null;
        if (line == null)
        {
            var go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            Undo.RegisterCreatedObjectUndo(go, "Montar tela de vitória");
            go.transform.SetParent(root, false);
            line = go.GetComponent<Image>();
        }

        Undo.RecordObject(line, "Montar tela de vitória");
        Undo.RecordObject(line.rectTransform, "Montar tela de vitória");
        line.color = WinInk;
        line.raycastTarget = false;
        Place(line.rectTransform, anchor, new Vector2(0f, 1f), position, size);
        return line.rectTransform;
    }

    private static RectTransform Icon(RectTransform root, string name, Sprite sprite, Vector2 anchor, Vector2 position,
        Vector2 size)
    {
        Transform child = root.Find(name);
        Image icon = child != null ? child.GetComponent<Image>() : null;
        if (icon == null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            Undo.RegisterCreatedObjectUndo(go, "Montar tela de vitória");
            go.transform.SetParent(root, false);
            icon = go.GetComponent<Image>();
        }

        Undo.RecordObject(icon, "Montar tela de vitória");
        Undo.RecordObject(icon.rectTransform, "Montar tela de vitória");
        icon.sprite = sprite;
        icon.color = WinInk;
        icon.raycastTarget = false;
        icon.preserveAspect = true;
        Place(icon.rectTransform, anchor, new Vector2(0f, 1f), position, size);
        return icon.rectTransform;
    }

    // Pixel art: sem filtro e sem compressão, senão os 12x12 viram borrão.
    private static Sprite SkullSprite()
    {
        if (AssetImporter.GetAtPath(SkullPath) is TextureImporter importer
            && (importer.textureType != TextureImporterType.Sprite || importer.filterMode != FilterMode.Point))
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(SkullPath);
        if (sprite == null)
            Debug.LogWarning($"Final: sprite da caveira não achado em {SkullPath}.");
        return sprite;
    }

    private static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
    {
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
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
