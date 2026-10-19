using UnityEngine;

/// <summary>
/// Som do início da partida: ao entrar em <see cref="GameState.Playing"/> toca uma entrada da AudioLibrary
/// acompanhando o AudioListener (a câmera do jogador), como o grito que ficava no <see cref="CameraShake"/>. Fica
/// fora dele de propósito: o som tem duração diferente da do tremor e não deve depender dele.
/// </summary>
public class RumbleSoundEffect : MonoBehaviour
{
    [Tooltip("Entrada da AudioLibrary. Vazio = sem som.")]
    [SerializeField] private string soundId = "rumble";

    private AudioHandle soundHandle;

    private void OnEnable() => GameManager.StateChanged += HandleGameState;

    private void OnDisable() => GameManager.StateChanged -= HandleGameState;

    // O AudioPool sobrevive ao reload da cena (DontDestroyOnLoad): sem isto o fim do som vazaria para a partida
    // nova. Handle de slot já roubado é no-op no Stop.
    private void OnDestroy() => AudioProvider.Stop(soundHandle);

    private void HandleGameState(GameState state)
    {
        // Abertura da caça: reinício já em Playing (tutorial concluído) não a repete.
        if (state != GameState.Playing || string.IsNullOrEmpty(soundId)
            || (GameManager.Current != null && GameManager.Current.SkippedTutorial))
            return;

        AudioListener listener = FindFirstObjectByType<AudioListener>();
        Transform target = listener != null ? listener.transform : transform;
        soundHandle = AudioProvider.PlayFollowing(soundId, target);
    }
}
