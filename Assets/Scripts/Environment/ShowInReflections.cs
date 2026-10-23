using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Mostra malhas escondidas em primeira pessoa (cabeça, pescoço, cabelo: ShadowsOnly) só para as câmeras da
/// <see cref="PlanarReflection"/> (e da <see cref="ExtraCamera"/>). A câmera do player continua sem cabeça; o espelho vê o corpo inteiro.
///
/// Por câmera e não por frame: o modo vira On no begin da câmera do reflexo (antes do culling dela) e volta no end,
/// antes de a câmera do player renderizar. Vai na raiz do PlayerDummy.
/// </summary>
public class ShowInReflections : MonoBehaviour
{
    [Tooltip("As mesmas malhas do 'Hidden In First Person' do DialogueCamera.")]
    [SerializeField] private Renderer[] renderers;

    private ShadowCastingMode[] saved;
    private bool shown;

    /// <summary>Câmera de fora que também vê a cabeça enquanto estiver atribuída (o plano do Seeker no final).</summary>
    public Camera ExtraCamera { get; set; }

    private bool ShowsFor(Camera camera) => PlanarReflection.IsReflectionCamera(camera) || (ExtraCamera != null && camera == ExtraCamera);

    private void Awake() => saved = new ShadowCastingMode[renderers.Length];

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        Restore();
    }

    private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        if (shown || !ShowsFor(camera))
            return;

        // Guarda o modo de agora, não o do Awake: o DialogueCamera também liga a cabeça durante a conversa, e a
        // volta tem que devolver o que ele deixou.
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
                continue;

            saved[i] = renderers[i].shadowCastingMode;
            renderers[i].shadowCastingMode = ShadowCastingMode.On;
        }

        shown = true;
    }

    private void OnEndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (ShowsFor(camera))
            Restore();
    }

    private void Restore()
    {
        if (!shown)
            return;

        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].shadowCastingMode = saved[i];

        shown = false;
    }
}
