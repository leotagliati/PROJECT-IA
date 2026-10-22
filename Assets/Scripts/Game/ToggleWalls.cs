using UnityEngine;

/// <summary>
/// Troca as paredes temporárias que confinam o jogador pelos objetos que liberam a passagem. Não sabe quando roda:
/// quem decide é o UnityEvent que chama <see cref="Apply"/> (ex.: o <c>started</c> do DialogueTrigger da porta, com
/// a câmera presa no enquadramento da conversa).
///
/// Partida nova começa com as paredes ligadas e os substitutos desligados, seja qual for o estado salvo na cena;
/// reinício com o tutorial já concluído começa direto no estado do Apply.
/// </summary>
public class ToggleWalls : MonoBehaviour
{
    [Tooltip("Ativas no começo da partida; Apply desliga.")]
    [SerializeField] private GameObject[] temporaryWalls;

    [Tooltip("Ficam no lugar das paredes temporárias; desativadas no começo da partida e Apply liga.")]
    [SerializeField] private GameObject[] replacements;

    // No Awake, antes de qualquer Start: o tutorialSkipped do GameManager (Start) que chama o Apply não é desfeito.
    private void Awake()
    {
        SetAllActive(temporaryWalls, true);
        SetAllActive(replacements, false);
    }

    private void Start()
    {
        if (GameManager.Current != null && GameManager.Current.SkippedTutorial)
            Apply();
    }

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
