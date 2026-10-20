using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Seta que aponta o botão selecionado. Lê só o EventSystem: mouse (PointerSelectable), teclado e controle já
/// mexem na mesma seleção, então a seta não precisa saber de onde veio a troca. Pula direto para o botão novo e
/// gira no lugar, em vez de deslizar entre eles.
/// </summary>
public class MenuSelectionArrow : MonoBehaviour
{
    [Tooltip("Só segue botões dentro deste objeto. Vazio = o pai da seta.")]
    [SerializeField] private Transform menuRoot;

    [Tooltip("Some com um Slider selecionado: o SegmentedSliderView já acende os blocos.")]
    [SerializeField] private bool ignoreSliders = true;

    [Tooltip("Distância entre a ponta da seta e o começo do texto, em unidades do Canvas.")]
    [SerializeField] private float gap = 24f;

    [Header("Giro ao trocar de botão")]
    [SerializeField, Min(0.01f)] private float spinDuration = 0.35f;

    [Tooltip("(0,1,0) gira como moeda; (0,0,1) gira no plano da tela.")]
    [SerializeField] private Vector3 spinAxis = Vector3.up;

    [SerializeField, Min(1)] private int turns = 1;

    private RectTransform rect;
    private Graphic graphic;
    private GameObject target;
    private TMP_Text targetText;
    private float spinTime = float.PositiveInfinity;

    private void Awake()
    {
        rect = (RectTransform)transform;
        graphic = GetComponent<Graphic>();

        if (menuRoot == null)
            menuRoot = transform.parent;

        Show(false);
    }

    // LateUpdate: o HoverEffect desliza o texto no Update, e a seta acompanha a posição já deste frame.
    private void LateUpdate()
    {
        GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        if (selected == null || !selected.transform.IsChildOf(menuRoot)
            || (ignoreSliders && selected.TryGetComponent(out Slider _)))
        {
            target = null;
            Show(false);
            return;
        }

        if (selected != target)
        {
            target = selected;
            targetText = selected.GetComponentInChildren<TMP_Text>();
            spinTime = 0f;
        }

        Show(true);
        Place();
        Spin();
    }

    private void Place()
    {
        // Pelo texto e não pelo RectTransform do botão: os botões são alinhados à direita, e a caixa é maior que a
        // palavra.
        Vector3 anchor;
        if (targetText != null)
        {
            Bounds bounds = targetText.textBounds;
            anchor = targetText.transform.TransformPoint(new Vector3(bounds.min.x, bounds.center.y, 0f));
        }
        else
        {
            var targetRect = (RectTransform)target.transform;
            anchor = targetRect.TransformPoint(new Vector3(targetRect.rect.xMin, targetRect.rect.center.y, 0f));
        }

        Vector3 local = rect.parent.InverseTransformPoint(anchor);
        rect.localPosition = new Vector3(local.x - gap - rect.rect.width * (1f - rect.pivot.x), local.y, 0f);
    }

    private void Spin()
    {
        if (spinTime >= spinDuration)
        {
            rect.localRotation = Quaternion.identity;
            return;
        }

        // Tempo não escalado: o mesmo menu abre no Pause, com timeScale 0.
        spinTime += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(spinTime / spinDuration);
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
        rect.localRotation = Quaternion.AngleAxis(eased * 360f * turns, spinAxis);
    }

    private void Show(bool visible)
    {
        if (graphic != null && graphic.enabled != visible)
            graphic.enabled = visible;
    }
}
