using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Cena de créditos: segura a máquina de escrever até o <see cref="LoadingWarmup"/> da cena terminar (senão as
/// primeiras letras sairiam atrás da tela preta) e volta ao menu com Esc/Start ou sozinha depois do fim.
/// </summary>
public class CreditsScreen : MonoBehaviour
{
    [SerializeField] private Typewriter typewriter;

    [Tooltip("Opcional. Vazio = a máquina começa na hora.")]
    [SerializeField] private LoadingWarmup warmup;

    [SerializeField] private string menuScene = "MainMenu";

    [Tooltip("Espera extra antes de voltar ao menu, contada depois que a máquina termina — e ela só termina " +
             "depois do tempo de leitura da última página (ou de alguém apertar \"voltar ao menu\"). 0 = na hora.")]
    [SerializeField, Min(0f)] private float exitDelayAfterEnd;

    [SerializeField, Min(0.01f)] private float fadeTime = 0.8f;
    [SerializeField] private Color fadeColor = Color.black;

    private float _endTimer;
    private bool _leaving;

    private void Awake()
    {
        if (typewriter != null)
            typewriter.enabled = false;
    }

    private IEnumerator Start()
    {
        // Desligado = a cena já foi aquecida nesta sessão. O Progress chega a 1 antes do fade de saída da tela,
        // e a máquina ainda tem o autoStartDelay: a primeira letra cai com a cena já visível.
        while (warmup != null && warmup.enabled && warmup.Progress < 1f)
            yield return null;

        if (typewriter != null)
            typewriter.enabled = true;
    }

    private void Update()
    {
        if (_leaving)
            return;

        bool back = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
                    (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

        if (typewriter != null && typewriter.IsFinished)
            _endTimer += Time.deltaTime;

        if (back || _endTimer >= exitDelayAfterEnd)
            StartCoroutine(Leave());
    }

    private IEnumerator Leave()
    {
        _leaving = true;

        if (typewriter != null)
            typewriter.enabled = false;

        AsyncOperation load = SceneManager.LoadSceneAsync(menuScene);
        load.allowSceneActivation = false;

        Image fade = CreateFadeOverlay();

        for (float t = 0f; t < fadeTime; t += Time.unscaledDeltaTime)
        {
            SetAlpha(fade, t / fadeTime);
            yield return null;
        }

        SetAlpha(fade, 1f);
        load.allowSceneActivation = true;
    }

    private Image CreateFadeOverlay()
    {
        var go = new GameObject("CreditsFade", typeof(Canvas), typeof(Image));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        var image = go.GetComponent<Image>();
        image.raycastTarget = false;
        image.rectTransform.anchorMin = Vector2.zero;
        image.rectTransform.anchorMax = Vector2.one;
        image.rectTransform.sizeDelta = Vector2.zero;
        SetAlpha(image, 0f);
        return image;
    }

    private void SetAlpha(Image image, float alpha)
    {
        Color c = fadeColor;
        c.a = alpha;
        image.color = c;
    }
}
