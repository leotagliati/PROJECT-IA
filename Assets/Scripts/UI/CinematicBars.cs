using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Barras pretas de cinema (letterbox) que deslizam para dentro da imagem quando o jogo assume o player e saem quando
/// o controle volta. Quem decide é a cutscene (<see cref="Show"/> / <see cref="Hide"/>); a barra não escuta o estado do
/// jogo.
///
/// A pose "mostrada" é a que está na cena, e a "escondida" é essa mesma empurrada para fora pela altura da barra:
/// mexer no layout no Editor não obriga a mexer aqui. Anima a posição (e não a escala) para a borda interna da barra
/// entrar na imagem, que é o letterbox clássico; escalar a partir do pivô central moveria a borda de fora.
/// </summary>
public class CinematicBars : MonoBehaviour
{
    [SerializeField] private RectTransform top;
    [SerializeField] private RectTransform bottom;
    [SerializeField, Min(0.01f)] private float duration = 0.6f;

    private float topShownY;
    private float bottomShownY;
    private float topHiddenY;
    private float bottomHiddenY;

    // 0 = escondida, 1 = mostrada. Um progresso só: interromper no meio inverte de onde está, sem salto.
    private float progress;
    private float target;

    private void Awake()
    {
        if (top == null || bottom == null)
        {
            Debug.LogError($"{name}: Top/Bottom não atribuídos — as barras não animam.", this);
            enabled = false;
            return;
        }

        topShownY = top.anchoredPosition.y;
        bottomShownY = bottom.anchoredPosition.y;
        topHiddenY = topShownY + top.rect.height;
        bottomHiddenY = bottomShownY - bottom.rect.height;

        // Barra não pode engolir clique.
        top.GetComponent<Graphic>().raycastTarget = false;
        bottom.GetComponent<Graphic>().raycastTarget = false;

        Apply();
        enabled = false;
    }

    public void Show() => SetTarget(1f);

    public void Hide() => SetTarget(0f);

    private void SetTarget(float value)
    {
        if (top == null || bottom == null)
            return;

        target = value;
        enabled = true;
        SetBarsActive(true);
    }

    private void Update()
    {
        // Tempo não escalado: pause e diálogo mexem no timeScale e a barra não pode congelar no meio.
        progress = Mathf.MoveTowards(progress, target, Time.unscaledDeltaTime / duration);
        Apply();

        if (Mathf.Approximately(progress, target))
        {
            // Escondida de vez: desativa, porque em telas mais altas que 16:9 a pose escondida ainda cai dentro do
            // Canvas. Parado, o componente também deixa de rodar.
            if (target <= 0f)
                SetBarsActive(false);

            enabled = false;
        }
    }

    private void Apply()
    {
        float k = Mathf.SmoothStep(0f, 1f, progress);
        SetY(top, Mathf.Lerp(topHiddenY, topShownY, k));
        SetY(bottom, Mathf.Lerp(bottomHiddenY, bottomShownY, k));

        if (progress <= 0f && target <= 0f)
            SetBarsActive(false);
    }

    private static void SetY(RectTransform bar, float y)
    {
        Vector2 position = bar.anchoredPosition;
        position.y = y;
        bar.anchoredPosition = position;
    }

    private void SetBarsActive(bool active)
    {
        if (top.gameObject.activeSelf != active)
            top.gameObject.SetActive(active);
        if (bottom.gameObject.activeSelf != active)
            bottom.gameObject.SetActive(active);
    }
}
