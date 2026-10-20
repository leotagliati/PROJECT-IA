using UnityEngine;

/// <summary>
/// Som de estrondo: toca uma entrada da AudioLibrary acompanhando o AudioListener (a câmera do jogador). Só executa:
/// quem decide quando é quem chama (abertura da caça, pelo <c>huntStarted</c> do GameManager, e cutscene final).
/// Separado do <see cref="CameraShake"/> de propósito: o som tem duração diferente da do tremor e não depende dele.
/// </summary>
public class RumbleSoundEffect : MonoBehaviour
{
    [Tooltip("Entrada da AudioLibrary. Vazio = sem som.")]
    [SerializeField] private string soundId = "rumble";

    private AudioHandle soundHandle;

    // O AudioPool sobrevive ao reload da cena (DontDestroyOnLoad): sem isto o fim do som vazaria para a partida
    // nova. Handle de slot já roubado é no-op no Stop.
    private void OnDestroy() => AudioProvider.Stop(soundHandle);

    public void Play()
    {
        if (string.IsNullOrEmpty(soundId))
            return;

        AudioListener listener = FindFirstObjectByType<AudioListener>();
        Transform target = listener != null ? listener.transform : transform;
        soundHandle = AudioProvider.PlayFollowing(soundId, target);
    }
}
