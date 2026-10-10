using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Fica na própria câmera do menu: é ela que voa até o monitor no Play.
public class mainMenu : MonoBehaviour
{
    [Header("Transição do Play")]
    [Tooltip("Para onde a câmera voa. Se tiver Renderer, mira no centro do bounds, não no pivot " +
             "(pivot de mesh importado costuma estar no pé do objeto).")]
    [SerializeField] private Transform _focusTarget;
    [Tooltip("Distância do centro do alvo onde a câmera para. Pequena o bastante para a tela " +
             "ocupar o quadro quando o fade fecha.")]
    [SerializeField] private float _stopDistance = 0.35f;
    [SerializeField] private float _duration = 1.6f;
    [Tooltip("Ease-in: começa devagar e acelera, dá a sensação de ser puxado para dentro.")]
    [SerializeField] private AnimationCurve _moveCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0f), new Keyframe(1f, 1f, 2.5f, 0f));

    [Header("Fade")]
    [Tooltip("Fração da duração em que o fade começa.")]
    [Range(0f, 1f)] [SerializeField] private float _fadeStart = 0.55f;
    [SerializeField] private Color _fadeColor = Color.black;

    [Header("Áudio")]
    [Tooltip("Id na AudioLibrary (Assets/Resources/AudioLibrary.asset).")]
    [SerializeField] private string _playSfxId = "jumpscare";
    [SerializeField] private float _playSfxVolume = 1f;

    [Header("Menu")]
    [Tooltip("Opcional: CanvasGroup do menu, some junto com a aproximação.")]
    [SerializeField] private CanvasGroup _menuGroup;
    [SerializeField] private int _gameSceneIndex = 1;

    private bool _transitioning;

    public void PlayGame()
    {
        // Clique duplo no Play dispararia dois loads e duas corrotinas brigando pela câmera.
        if (_transitioning)
            return;

        _transitioning = true;
        StartCoroutine(PlayTransition());
    }

    public void QuitGame()
    {
        Application.Quit();
    }

    private IEnumerator PlayTransition()
    {
        // Trava o input da UI na hora: hover/seleção continuariam tocando som durante o voo.
        if (EventSystem.current != null)
            EventSystem.current.enabled = false;

        AudioProvider.PlayFollowing(_playSfxId, transform, _playSfxVolume);

        // Carrega em paralelo com a animação e só ativa no fim — senão o hitch do load cai
        // no meio do movimento ou a cena troca antes do fade fechar.
        AsyncOperation load = SceneManager.LoadSceneAsync(_gameSceneIndex);
        load.allowSceneActivation = false;

        Image fade = CreateFadeOverlay();

        Vector3 startPos = transform.position;
        Quaternion startRot = transform.rotation;
        Vector3 focus = FocusPoint();
        Vector3 endPos = focus - (focus - startPos).normalized * _stopDistance;
        Quaternion endRot = Quaternion.LookRotation(focus - endPos, Vector3.up);

        float menuStartAlpha = _menuGroup != null ? _menuGroup.alpha : 1f;

        // Tempo não escalado: se o menu for aberto com timeScale 0 (volta do pause), a
        // transição não pode congelar.
        for (float t = 0f; t < _duration; t += Time.unscaledDeltaTime)
        {
            float n = t / _duration;
            float k = _moveCurve.Evaluate(n);

            transform.SetPositionAndRotation(
                Vector3.LerpUnclamped(startPos, endPos, k),
                Quaternion.SlerpUnclamped(startRot, endRot, k));

            if (_menuGroup != null)
                _menuGroup.alpha = Mathf.Lerp(menuStartAlpha, 0f, n * 4f);

            SetAlpha(fade, Mathf.InverseLerp(_fadeStart, 1f, n));
            yield return null;
        }

        transform.SetPositionAndRotation(endPos, endRot);
        SetAlpha(fade, 1f);

        load.allowSceneActivation = true;
    }

    private Vector3 FocusPoint()
    {
        if (_focusTarget == null)
            return transform.position + transform.forward * (_stopDistance + 2f);

        Renderer r = _focusTarget.GetComponentInChildren<Renderer>();
        return r != null ? r.bounds.center : _focusTarget.position;
    }

    // Criado em runtime para não depender de wiring na cena; o sortingOrder alto garante
    // que fica por cima do Canvas do menu.
    private Image CreateFadeOverlay()
    {
        var go = new GameObject("PlayFade", typeof(Canvas), typeof(Image));
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
        Color c = _fadeColor;
        c.a = alpha;
        image.color = c;
    }
}
