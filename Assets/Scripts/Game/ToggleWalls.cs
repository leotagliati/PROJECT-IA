using UnityEngine;

/// <summary>
/// Troca as paredes temporárias que confinam o jogador pelos objetos que liberam a passagem. Não sabe quando roda:
/// quem decide é o UnityEvent que chama <see cref="Apply"/> (ex.: o <c>started</c> do DialogueTrigger da porta, com
/// a câmera presa no enquadramento da conversa).
///
/// O estado inicial — paredes ligadas, substitutos desligados — é o da cena; este componente não o força.
/// </summary>
public class ToggleWalls : MonoBehaviour
{
    [Tooltip("Começam ativas na cena; Apply desliga.")]
    [SerializeField] private GameObject[] temporaryWalls;

    [Tooltip("Ficam no lugar das paredes temporárias; começam desativadas na cena e Apply liga.")]
    [SerializeField] private GameObject[] replacements;

    public void Apply()
    {
        SetAllActive(temporaryWalls, false);
        SetAllActive(replacements, true);
    }

    private static void SetAllActive(GameObject[] targets, bool active)
    {
        foreach (GameObject target in targets)
            if (target != null)
                target.SetActive(active);
    }
}
