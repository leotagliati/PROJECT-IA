using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Luz fraca em volta do player para dar alguma visibilidade sem a lanterna. São duas point
/// lights, montadas por código e filhas do player:
/// - Cenário: bate em tudo MENOS no corpo. É a que dá o alcance; se batesse no corpo, a
///   claridade que basta para enxergar a parede estoura braço e perna na frente da câmera.
/// - Corpo: bate no corpo e no cenário, bem mais fraca, só para o corpo não virar silhueta preta.
///
/// O filtro é por Rendering Layer, não por Culling Mask: o PC_Renderer está em Forward+, que
/// ignora o Culling Mask das luzes. Uma luz só ilumina um renderer se as máscaras dos dois
/// tiverem algum bit em comum, e o cenário inteiro está só no "Default". Para a luz do cenário
/// poder pular o corpo, o corpo sai do Default e fica só no layer <see cref="bodyRenderingLayer"/>.
/// Em troca, toda luz da cena ganha esse bit no Start, senão lanterna e lâmpadas também
/// deixariam de iluminar o corpo. Luz instanciada depois do Start não ganha: o corpo fica sem ela.
/// </summary>
public class PlayerAmbientLight : MonoBehaviour
{
    [Tooltip("Rendering Layer só do corpo (Project Settings > Tags and Layers > Rendering Layers). " +
             "Precisa existir com esse nome; sem ele, o componente desliga.")]
    [SerializeField] private string bodyRenderingLayer = "Player";

    [Header("-----Luz do cenário (não bate no corpo)-----")]
    [SerializeField] private Vector3 worldOffset = new Vector3(0f, 1.6f, 0f);
    [SerializeField] private Color worldColor = new Color(0.55f, 0.62f, 0.75f);
    [SerializeField, Min(0f)] private float worldIntensity = 1.5f;
    [SerializeField, Min(0.1f)] private float worldRange = 7f;

    [Header("-----Luz do corpo (bate no corpo e no cenário)-----")]
    // Fora do corpo de propósito: uma point light dentro da malha não acende a superfície
    // de fora (a normal aponta para longe da luz). Acima e à frente da cabeça, ela pega o
    // braço e as pernas que a câmera vê.
    [SerializeField] private Vector3 bodyOffset = new Vector3(0f, 2.2f, 0.4f);
    [SerializeField] private Color bodyColor = new Color(0.55f, 0.62f, 0.75f);
    [SerializeField, Min(0f)] private float bodyIntensity = 0.4f;
    [SerializeField, Min(0.1f)] private float bodyRange = 3f;

    private Light worldLight;
    private Light bodyLight;
    private uint bodyMask;

    private void Awake()
    {
        int layer = RenderingLayerMask.NameToRenderingLayer(bodyRenderingLayer);
        if (layer < 0)
        {
            Debug.LogWarning($"{nameof(PlayerAmbientLight)}: Rendering Layer '{bodyRenderingLayer}' não existe " +
                             "(renomeie um 'Light Layer N' em Tags and Layers) — luz desligada.", this);
            enabled = false;
            return;
        }
        bodyMask = 1u << layer;

        // Só malha: as partículas (StepDust, BreathVapor) ficam no Default para a luz do cenário
        // continuar pegando a poeira no chão.
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is SkinnedMeshRenderer || r is MeshRenderer)
                r.renderingLayerMask = bodyMask;
        }

        worldLight = CreateLight("Ambient Light (cenário)");
        bodyLight = CreateLight("Ambient Light (corpo)");
        Apply();
    }

    private void Start()
    {
        // No Start, depois do Awake de todo mundo: lanterna e lâmpadas já existem.
        foreach (Light light in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (light == worldLight)
                continue;
            UniversalAdditionalLightData data = light.GetUniversalAdditionalLightData();
            data.renderingLayers |= bodyMask;
            // A sombra também é filtrada por Rendering Layer (o URP testa a máscara de sombra
            // da luz contra o renderer): com Custom Shadow Layers, só a de cima não basta e o
            // corpo pararia de projetar sombra dessa luz.
            if (data.customShadowLayers)
                data.shadowRenderingLayers |= bodyMask;
        }
    }

    // Em Play, mexer no Inspector já reflete na luz: dá para ajustar olhando a cena.
    private void OnValidate()
    {
        if (worldLight != null && bodyLight != null)
            Apply();
    }

    private Light CreateLight(string lightName)
    {
        var holder = new GameObject(lightName);
        holder.transform.SetParent(transform, false);

        Light light = holder.AddComponent<Light>();
        light.type = LightType.Point;
        light.shadows = LightShadows.None;
        return light;
    }

    private void Apply()
    {
        worldLight.transform.localPosition = worldOffset;
        worldLight.color = worldColor;
        worldLight.intensity = worldIntensity;
        worldLight.range = worldRange;
        // Todos os layers menos o do corpo.
        worldLight.GetUniversalAdditionalLightData().renderingLayers = ~bodyMask;

        bodyLight.transform.localPosition = bodyOffset;
        bodyLight.color = bodyColor;
        bodyLight.intensity = bodyIntensity;
        bodyLight.range = bodyRange;
        // Bit 0 = "Default", onde está o cenário.
        bodyLight.GetUniversalAdditionalLightData().renderingLayers = 1u | bodyMask;
    }
}
