using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Tela de loading do início da cena que compila shaders ANTES do jogador ver: sem isso, cada material ou
/// luz vista pela primeira vez compila a variante na hora e a build engasga. Continua o fade preto do
/// <see cref="mainMenu"/>.
///
/// Dois passos. (1) As <see cref="ShaderVariantCollection"/> gravadas (Graphics → Save to asset). (2) Desenha
/// cada material da cena na frente da câmera por alguns frames, uma vez como a cena está e outra com TODAS
/// as luzes e a lanterna acesas. No Forward+ do URP as variantes de luz são globais por frame
/// (_ADDITIONAL_LIGHT_SHADOWS, cookies), então o que engasgava era justamente a primeira luz com sombra
/// aparecendo; o passo 2 pega o que a coleção não tiver.
///
/// Só roda uma vez por cena por sessão: o reload da partida reaproveita o que o driver já compilou.
///
/// A tela (<see cref="screen"/>) é montada à vontade, num prefab ou direto na cena; daqui ela só é exibida,
/// recebe progresso e some com fade. Vazio = tela preta gerada por código.
/// </summary>
[DefaultExecutionOrder(-200)]
public class LoadingWarmup : MonoBehaviour
{
    [Header("Tela")]
    [Tooltip("Prefab (instanciado aqui) ou objeto da cena. Não precisa de Canvas nem CanvasGroup na raiz: o que " +
             "faltar é adicionado. Vazio = tela preta gerada.")]
    [SerializeField] private GameObject screen;

    [Tooltip("Progresso 0..1 a cada frame. Ligue em Slider.value ou Image.fillAmount (Dynamic float).")]
    [SerializeField] private UnityEvent<float> progressChanged;

    [Header("Warmup")]
    [SerializeField] private ShaderVariantCollection[] collections;

    [Tooltip("Variantes compiladas por frame: mais = loading mais curto, mas a tela de loading trava mais.")]
    [SerializeField, Min(1)] private int variantsPerFrame = 8;

    [Tooltip("Frames desenhando os materiais em cada configuração de luz. A lanterna sobe a intensidade aos poucos.")]
    [SerializeField, Min(1)] private int framesPerPass = 8;

    [SerializeField, Min(0f)] private float fadeOutTime = 0.6f;

    // Fatia do progresso que cabe às coleções; o resto é dos passes de desenho, que têm duração conhecida.
    private const float CollectionsShare = 0.3f;

    // Por cena, não global: os créditos têm materiais que a partida nunca mostrou, e vice-versa.
    private static readonly HashSet<string> warmedScenes = new HashSet<string>();

    private bool AlreadyWarmed => warmedScenes.Contains(gameObject.scene.path);

    private readonly List<(Mesh mesh, Material material, int submesh)> draws = new List<(Mesh, Material, int)>();
    private CanvasGroup group;
    private bool ownsScreen;
    private bool running;
    private float previousVolume;
    private Mesh quad;
    private int outlineLayer;
    private int drawFramesDone;

    /// <summary>0..1, para quem preferir ler por código a ligar o <see cref="progressChanged"/>.</summary>
    public float Progress { get; private set; }

    private void Awake()
    {
        bool isPrefab = screen != null && !screen.scene.IsValid();

        if (AlreadyWarmed)
        {
            if (screen != null && !isPrefab)
                screen.SetActive(false);
            enabled = false;
            return;
        }

        // Já no Awake: o primeiro frame da cena não pode aparecer antes da tela.
        if (screen == null)
        {
            screen = BuildScreen();
            ownsScreen = true;
        }
        else if (isPrefab)
        {
            screen = Instantiate(screen);
            ownsScreen = true;
        }

        screen.SetActive(true);
        PutOnTop(screen);

        if (!screen.TryGetComponent(out group))
            group = screen.AddComponent<CanvasGroup>();

        group.alpha = 1f;
        group.blocksRaycasts = true;

        running = true;
        previousVolume = AudioListener.volume;
        AudioListener.volume = 0f;
        SetProgress(0f);
    }

    private void Start()
    {
        if (!AlreadyWarmed)
            StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        PlayerInputProvider.SetMode(InputMode.UI);

        // Um frame para os Start da cena criarem o que criam em runtime (a luz da lanterna, por exemplo).
        yield return null;

        int collectionCount = collections != null ? collections.Length : 0;
        for (int i = 0; i < collectionCount; i++)
        {
            if (collections[i] == null)
                continue;

            while (!collections[i].WarmUpProgressively(variantsPerFrame))
            {
                yield return null;
            }

            SetProgress(CollectionsShare * (i + 1) / collectionCount);
        }

        SetProgress(CollectionsShare);

        CollectDraws();
        Camera cam = Camera.main;

        if (cam != null && draws.Count > 0)
        {
            yield return DrawPass(cam, framesPerPass);

            var lightStates = new List<(Light light, bool enabled)>();
            var flashlightStates = new List<(Flashlight flashlight, bool on)>();
            AllLightsOn(lightStates, flashlightStates);

            yield return DrawPass(cam, framesPerPass * 2);

            Restore(lightStates, flashlightStates);
        }

        SetProgress(1f);
        warmedScenes.Add(gameObject.scene.path);
        PlayerInputProvider.SetMode(InputMode.Player);
        group.blocksRaycasts = false;

        for (float t = 0f; t < fadeOutTime; t += Time.unscaledDeltaTime)
        {
            float k = t / fadeOutTime;
            group.alpha = 1f - k;
            AudioListener.volume = Mathf.Lerp(0f, previousVolume, k);
            yield return null;
        }

        running = false;
        AudioListener.volume = previousVolume;

        if (ownsScreen)
            Destroy(screen);
        else
            screen.SetActive(false);
    }

    private void OnDestroy()
    {
        // Cena trocada no meio do loading: não deixar o jogo mudo.
        if (running)
            AudioListener.volume = previousVolume;
    }

    private IEnumerator DrawPass(Camera cam, int frames)
    {
        int totalFrames = framesPerPass * 3;

        for (int i = 0; i < frames; i++)
        {
            DrawAll(cam);
            drawFramesDone++;
            SetProgress(Mathf.Lerp(CollectionsShare, 1f, (float)drawFramesDone / totalFrames));
            yield return null;
        }
    }

    private void SetProgress(float value)
    {
        Progress = Mathf.Clamp01(value);
        progressChanged?.Invoke(Progress);
    }

    // Tudo no mesmo ponto na frente da câmera: só precisa estar no frustum, sobreposição não importa.
    // Sombra ligada para compilar o ShadowCaster sob a lanterna.
    private void DrawAll(Camera cam)
    {
        Transform t = cam.transform;
        Vector3 center = t.position + t.forward * 1.5f;

        for (int i = 0; i < draws.Count; i++)
        {
            (Mesh mesh, Material material, int submesh) = draws[i];

            float size = Mathf.Max(mesh.bounds.size.magnitude, 1e-4f);
            Matrix4x4 matrix = Matrix4x4.TRS(center - t.rotation * (mesh.bounds.center * (0.3f / size)),
                t.rotation, Vector3.one * (0.3f / size));

            Graphics.DrawMesh(mesh, matrix, material, 0, cam, submesh, null, ShadowCastingMode.On, true);

            // A render feature de outline só entra para objetos na layer Outline.
            if (i == 0 && outlineLayer >= 0)
                Graphics.DrawMesh(mesh, matrix, material, outlineLayer, cam, submesh);
        }
    }

    // Inclui inativos: a garrafa leva os cacos num filho desligado, e é isso que engasgaria no primeiro smash.
    private void CollectDraws()
    {
        quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        outlineLayer = LayerMask.NameToLayer("Outline");

        var seen = new HashSet<(Mesh, Material)>();

        foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Mesh mesh = MeshOf(renderer);
            Material[] materials = renderer.sharedMaterials;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || !seen.Add((mesh, material)))
                    continue;

                draws.Add((mesh, material, Mathf.Clamp(i, 0, mesh.subMeshCount - 1)));
            }
        }
    }

    // A malha certa importa no DX12/Vulkan, onde o layout de vértice faz parte do estado compilado.
    private Mesh MeshOf(Renderer renderer)
    {
        Mesh mesh = null;

        if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter))
            mesh = filter.sharedMesh;
        else if (renderer is SkinnedMeshRenderer skinned)
            mesh = skinned.sharedMesh;
        else if (renderer is ParticleSystemRenderer particles && particles.renderMode == ParticleSystemRenderMode.Mesh)
            mesh = particles.mesh;

        return mesh != null ? mesh : quad;
    }

    // Só luzes em hierarquia ativa: ativar GameObject desligado dispararia OnEnable de quem estiver nele.
    private static void AllLightsOn(List<(Light, bool)> lights, List<(Flashlight, bool)> flashlights)
    {
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            lights.Add((light, light.enabled));
            light.enabled = true;
        }

        // A lanterna desliga a própria Light com intensidade 0: tem que acender pelo componente.
        foreach (Flashlight flashlight in FindObjectsByType<Flashlight>(FindObjectsSortMode.None))
        {
            flashlights.Add((flashlight, flashlight.IsOn));
            flashlight.SetOn(true);
        }
    }

    private static void Restore(List<(Light light, bool enabled)> lights, List<(Flashlight flashlight, bool on)> flashlights)
    {
        foreach ((Flashlight flashlight, bool on) in flashlights)
            if (flashlight != null)
                flashlight.SetOn(on);

        foreach ((Light light, bool enabled) in lights)
            if (light != null)
                light.enabled = enabled;
    }

    /// <summary>
    /// Garante que a tela renderiza por cima de tudo, inclusive do HUD e das dicas da cena. Público porque o
    /// menu exibe o mesmo prefab enquanto a cena carrega, e os dois precisam sair idênticos para a emenda sumir.
    /// </summary>
    public static void PutOnTop(GameObject screen)
    {
        // Sem Canvas acima, uma tela montada como prefab solto não renderizaria.
        Canvas canvas = screen.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            canvas = screen.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            screen.AddComponent<CanvasScaler>();
        }

        if (canvas.isRootCanvas)
            canvas.sortingOrder = short.MaxValue;
    }

    // Fallback sem tela configurada: fundo preto, como o fade do menu termina.
    private GameObject BuildScreen()
    {
        var root = new GameObject("LoadingOverlay", typeof(Canvas), typeof(CanvasGroup));
        root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        var background = new GameObject("Background", typeof(Image)).GetComponent<Image>();
        background.transform.SetParent(root.transform, false);
        background.color = Color.black;
        Stretch(background.rectTransform);

        return root;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        warmedScenes.Clear();
    }
}
