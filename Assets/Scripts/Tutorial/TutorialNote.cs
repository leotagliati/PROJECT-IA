using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Conteúdo de um bilhete de tutorial: o texto e o vídeo de exemplo. A aparência é toda da
/// <see cref="TutorialNoteView"/>; aqui só mora o que muda de um tutorial para outro.
/// </summary>
[CreateAssetMenu(fileName = "TutorialNote", menuName = "Tutorial/Note")]
public class TutorialNote : ScriptableObject
{
    [Tooltip("Marcação do HintController: {Action} vira o ícone da tecla no esquema ativo, *destaque*.")]
    [TextArea(4, 10)]
    [SerializeField] private string body;

    [Tooltip("Toca em loop, sem som, na janela da foto.")]
    [SerializeField] private VideoClip clip;

    public string Body => body;
    public VideoClip Clip => clip;
}
