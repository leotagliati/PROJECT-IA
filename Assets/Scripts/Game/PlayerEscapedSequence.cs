using UnityEngine;

/// <summary>
/// Reação do player à vitória: quando a tela de vitória fica toda branca (<see cref="WinScreenUI.CutToWhite"/>),
/// desliga o que é dele — movimento, câmera, input. Só nessa hora: o jogador corre o clarão inteiro no controle dele
/// (ou no da cutscene final). O clarão, o som e o texto são da <see cref="WinScreenUI"/>.
/// </summary>
public class PlayerEscapedSequence : MonoBehaviour
{
    [Tooltip("Desligados quando a tela fica branca. Vazio: movimento, câmera, SpineLook, CameraJuice, ShoulderPeek e InteractionController deste objeto.")]
    [SerializeField] private Behaviour[] disableAfterFade;

    [SerializeField] private Flashlight flashlight;

    private void Awake()
    {
        if (flashlight == null)
            flashlight = GetComponentInChildren<Flashlight>();

        // Tudo que lê input do Player entra aqui, não só quem move a câmera: o
        // InteractionController continuaria aceitando E numa porta por baixo da tela branca.
        if (disableAfterFade == null || disableAfterFade.Length == 0)
        {
            disableAfterFade = new Behaviour[]
            {
                GetComponentInChildren<PlayerMovement>(),
                GetComponentInChildren<PlayerCamera>(),
                GetComponentInChildren<SpineLook>(),
                GetComponentInChildren<CameraJuice>(),
                GetComponentInChildren<ShoulderPeek>(),
                GetComponentInChildren<InteractionController>(),
            };
        }
    }

    private void OnEnable() => WinScreenUI.CutToWhite += HandleCutToWhite;

    private void OnDisable() => WinScreenUI.CutToWhite -= HandleCutToWhite;

    private void HandleCutToWhite()
    {
        foreach (Behaviour behaviour in disableAfterFade)
        {
            if (behaviour != null)
                behaviour.enabled = false;
        }

        if (flashlight != null)
            flashlight.InputLocked = true;
    }
}
