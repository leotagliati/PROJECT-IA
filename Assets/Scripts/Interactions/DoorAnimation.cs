using UnityEngine;

/// <summary>
/// "Animação" atual da porta: some com o modelo ao abrir e o devolve ao fechar. Só se inscreve
/// nos eventos do <see cref="DoorController"/>. Para animar de verdade, troque este componente
/// (ou some outro) com a mesma inscrição — o controller e os triggers não mudam.
/// Vai no PAI da porta: no próprio Model, desligá-lo cancelaria a inscrição e a porta nunca
/// mais fecharia.
/// </summary>
[RequireComponent(typeof(DoorController))]
public class DoorAnimation : MonoBehaviour
{
    [Tooltip("Filho com a malha e a colisão sólida. Não use o próprio objeto pai.")]
    [SerializeField] private GameObject doorModel;

    private DoorController door;

    private void Awake()
    {
        door = GetComponent<DoorController>();
    }

    private void OnEnable()
    {
        door.Opened += HandleOpened;
        door.Closed += HandleClosed;
    }

    private void OnDisable()
    {
        door.Opened -= HandleOpened;
        door.Closed -= HandleClosed;
    }

    // Estado inicial (startsOpen): o Awake do controller já rodou, mas nenhum evento foi emitido.
    private void Start()
    {
        if (doorModel != null)
            doorModel.SetActive(!door.IsOpen);
    }

    private void HandleOpened()
    {
        if (doorModel != null)
            doorModel.SetActive(false);
    }

    private void HandleClosed()
    {
        if (doorModel != null)
            doorModel.SetActive(true);
    }
}
