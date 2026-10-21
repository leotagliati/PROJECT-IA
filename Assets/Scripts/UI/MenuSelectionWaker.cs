using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Acorda a seleção do EventSystem quando o jogador navega por teclado/controle sem nada
// selecionado (o mouse zera a seleção ao sair do botão, e sem seleção o módulo de UI ignora
// o input de navegação). Fica no root do menu: ativo = menu aberto.
//
// Roda depois do EventSystem.Update: o módulo de UI já "gastou" o primeiro toque de navegação
// (sem seleção ele só arma o repeat delay), então aqui o toque apenas acorda a seleção em vez
// de pular direto para o segundo botão.
public class MenuSelectionWaker : MonoBehaviour
{
    private GameObject lastSelected;

    private void OnDisable() => lastSelected = null;

    private void LateUpdate()
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null)
            return;

        var current = eventSystem.currentSelectedGameObject;
        if (current != null)
        {
            lastSelected = current;
            return;
        }

        // No controle, menu aberto sempre tem algo selecionado: sem seleção o Submit (A) não faz nada, e quem abre
        // um menu pelo controle não sabe que precisa mexer o analógico antes. Com mouse fica sem seleção, para não
        // haver dois destaques (ver PointerSelectable). O Submit também acorda, para o primeiro A não se perder.
        if (!NavigateHeld(eventSystem) && !SubmitPressed(eventSystem) && !GamepadActive)
            return;

        var target = lastSelected != null && lastSelected.activeInHierarchy
            ? lastSelected
            : FirstSelectableIn(gameObject, false);

        if (target != null)
            eventSystem.SetSelectedGameObject(target);
    }

    /// <summary>O último input veio do controle (o mesmo esquema que troca os ícones de tecla).</summary>
    public static bool GamepadActive => InputIcons.ActiveBindingGroup == "Gamepad";

    // Quem troca de tela define o alvo que a primeira navegação vai acordar.
    public void Remember(GameObject focus) => lastSelected = focus;

    // Ordem da hierarquia = ordem visual do menu; botão desabilitado não pode receber o foco.
    // includeInactive: a tela de destino ainda está desligada quando o foco é escolhido.
    public static GameObject FirstSelectableIn(GameObject root, bool includeInactive)
    {
        foreach (var selectable in root.GetComponentsInChildren<Selectable>(includeInactive))
        {
            if (selectable.IsInteractable())
                return selectable.gameObject;
        }

        return null;
    }

    // Lê a mesma action que o módulo usa para mover a seleção: assets diferentes com
    // bindings diferentes fariam o menu acordar com uma tecla e navegar com outra.
    public static bool NavigateHeld(EventSystem eventSystem)
    {
        if (eventSystem.currentInputModule is not InputSystemUIInputModule module)
            return false;

        var action = module.move != null ? module.move.action : null;
        return action != null && action.ReadValue<Vector2>() != Vector2.zero;
    }

    public static bool SubmitPressed(EventSystem eventSystem)
    {
        if (eventSystem.currentInputModule is not InputSystemUIInputModule module)
            return false;

        var action = module.submit != null ? module.submit.action : null;
        return action != null && action.WasPressedThisFrame();
    }
}
