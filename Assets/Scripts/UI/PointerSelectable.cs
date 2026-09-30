using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Faz o mouse mexer na mesma seleção do teclado/joystick, para nunca haver dois
// elementos destacados ao mesmo tempo.
[RequireComponent(typeof(Selectable))]
public class PointerSelectable : MonoBehaviour, IPointerMoveHandler, IPointerExitHandler
{
    private Selectable selectable;

    private void Awake() => selectable = GetComponent<Selectable>();

    public void OnPointerMove(PointerEventData eventData)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null || !selectable.IsInteractable())
            return;

        if (eventSystem.currentSelectedGameObject == gameObject)
            return;

        // Ao destravar o cursor no Pause o módulo de UI gera um pointerMove falso (o ponteiro
        // "pula" de -1,-1 para a posição real): só conta se o mouse se moveu de verdade.
        if (eventData is ExtendedPointerEventData extended
            && extended.device is Pointer pointer
            && pointer.delta.ReadValue() == Vector2.zero)
            return;

        eventSystem.SetSelectedGameObject(gameObject);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null || eventSystem.currentSelectedGameObject != gameObject)
            return;

        // Arrastando o slider para fora do retângulo não pode perder o foco.
        if (eventData.pointerDrag == gameObject)
            return;

        eventSystem.SetSelectedGameObject(null);
    }
}
