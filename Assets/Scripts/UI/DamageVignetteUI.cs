using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Vinheta vermelha de dano. Vai no próprio objeto da vinheta (Image/RawImage de UI) e controla
/// a opacidade pelo CanvasGroup, então serve para qualquer imagem nele ou nos filhos.
///
/// O pico é quase instantâneo e a descida é lenta, o contrário de um fade simétrico: dano se lê
/// como um golpe seguido de dor, e um fade-in suave soaria como transição de cena. Na morte a
/// vinheta não some — assenta em <see cref="deathHoldAlpha"/> e fica até o reload.
///
/// O objeto precisa estar ATIVO na cena (o componente zera o alpha sozinho): desativado, ele
/// não assina o evento de fim de jogo e nunca aparece.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class DamageVignetteUI : MonoBehaviour
{
    [Tooltip("Segundos até o pico. Curto: é o impacto.")]
    [SerializeField, Min(0.01f)] private float attack = 0.08f;

    [Tooltip("Segundos do pico até o nível de repouso.")]
    [SerializeField, Min(0.01f)] private float decay = 0.6f;

    [SerializeField, Range(0f, 1f)] private float peakAlpha = 1f;

    [Tooltip("Onde a vinheta assenta depois de morrer. 0 = some por completo.")]
    [SerializeField, Range(0f, 1f)] private float deathHoldAlpha = 0.75f;

    private CanvasGroup group;
    private float flashStart = float.NegativeInfinity;
    private float restAlpha;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        group.alpha = 0f;
        // Decorativa: não pode engolir clique nem raycast de UI de ninguém.
        group.interactable = false;
        group.blocksRaycasts = false;

        if (GetComponentInChildren<Graphic>() == null)
            Debug.LogWarning($"{name}: nenhuma Image/RawImage aqui — o CanvasGroup não tem o que apagar.", this);
    }

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable() => GameManager.StateChanged -= HandleGameState;

    private void HandleGameState(GameState state)
    {
        if (state == GameState.Lost)
            Flash(deathHoldAlpha);
    }

    /// <summary>
    /// Golpe de dano: sobe até o pico e desce até <paramref name="settleAlpha"/>. Fica público para
    /// um dano que não mata (quando existir) reaproveitar o mesmo efeito com settle 0.
    /// </summary>
    public void Flash(float settleAlpha = 0f)
    {
        flashStart = Time.time;
        restAlpha = Mathf.Clamp01(settleAlpha);
    }

    private void Update()
    {
        float t = Time.time - flashStart;

        float alpha;
        if (t < attack)
        {
            // Sobe do valor atual: um segundo golpe no meio da descida não pisca para zero antes.
            alpha = Mathf.Lerp(group.alpha, peakAlpha, t / attack);
        }
        else
        {
            // Ease-out: cai rápido logo depois do pico e demora a chegar no repouso.
            float k = Mathf.Clamp01((t - attack) / decay);
            alpha = Mathf.Lerp(peakAlpha, restAlpha, 1f - (1f - k) * (1f - k));
        }

        group.alpha = alpha;
    }
}
