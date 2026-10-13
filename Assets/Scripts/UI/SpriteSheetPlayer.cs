using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Toca um spritesheet em grade num RawImage andando o uvRect, sem fatiar a textura nem precisar de Animator.
/// Frames lidos da esquerda para a direita, de cima para baixo.
///
/// O frame sai do tempo decorrido, não de um contador: quando o warmup trava alguns frames, a animação pula para
/// onde deveria estar em vez de ficar em câmera lenta. Tempo não escalado, para rodar com timeScale 0.
/// </summary>
[RequireComponent(typeof(RawImage))]
public class SpriteSheetPlayer : MonoBehaviour
{
    [SerializeField, Min(1)] private int columns = 8;
    [SerializeField, Min(1)] private int rows = 6;

    [Tooltip("Frames usados. 0 = a grade inteira (última linha incompleta? informe o total aqui).")]
    [SerializeField, Min(0)] private int frameCount;

    [SerializeField, Min(0.01f)] private float framesPerSecond = 24f;

    private RawImage _image;
    private float _startTime;

    private int TotalFrames => frameCount > 0 ? Mathf.Min(frameCount, columns * rows) : columns * rows;

    private void Awake()
    {
        _image = GetComponent<RawImage>();
    }

    private void OnEnable()
    {
        _startTime = Time.unscaledTime;
        ShowFrame(0);
    }

    private void Update()
    {
        int frame = (int)((Time.unscaledTime - _startTime) * framesPerSecond) % TotalFrames;
        ShowFrame(frame);
    }

    private void ShowFrame(int frame)
    {
        if (_image == null)
            return;

        int column = frame % columns;
        int row = frame / columns;
        float w = 1f / columns;
        float h = 1f / rows;

        // UV tem origem embaixo; a linha 0 da grade é a de cima.
        _image.uvRect = new Rect(column * w, 1f - (row + 1) * h, w, h);
    }

    // Mostra o primeiro frame no editor, em vez da folha inteira espremida.
    private void OnValidate()
    {
        _image = GetComponent<RawImage>();
        ShowFrame(0);
    }
}
