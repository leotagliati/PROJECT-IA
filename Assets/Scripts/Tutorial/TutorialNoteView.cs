using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Parte visível do bilhete: escreve o texto, toca o vídeo de exemplo na janela da foto e lê o input de fechar,
/// que só repassa em <see cref="AdvancePressed"/>. Quando fechar e o que a pausa faz são do
/// <see cref="TutorialNoteController"/>.
/// </summary>
public class TutorialNoteView : MonoBehaviour
{
    // A mesma ação do diálogo: teclas concretas, com ícone (o Submit do mapa UI é por usage).
    private const string AdvanceAction = "UI/DialogueAdvance";

    [Tooltip("O que liga e desliga com o bilhete. Vazio = este objeto.")]
    [SerializeField] private GameObject root;

    [SerializeField] private TMP_Text bodyText;

    [Tooltip("Um TMP só dele, não o do texto: o prompt troca o próprio texto e apagaria o bilhete.")]
    [SerializeField] private TMP_Text promptText;

    [Tooltip("{0} = ícone da tecla de fechar no esquema ativo.")]
    [SerializeField] private string prompt = "Aperte {0} para continuar";

    [Header("Vídeo")]
    [Tooltip("Atrás da janela transparente da moldura.")]
    [SerializeField] private RawImage videoImage;

    [Tooltip("Vazio = criado no objeto do Video Image.")]
    [SerializeField] private VideoPlayer videoPlayer;

    [Tooltip("Vazio = procura na cena. Formata {Action} e *destaque* igual às dicas; sem ele o texto sai cru.")]
    [SerializeField] private HintController markup;

    [Header("Transição")]
    [Tooltip("Fade de tudo que está no root, fundo desfocado incluso. Vazio = pega (ou cria) no root.")]
    [SerializeField] private CanvasGroup group;

    [Tooltip("O que sobe ao abrir e desce ao fechar (papel + foto). Vazio = só fade.")]
    [SerializeField] private RectTransform panel;

    [SerializeField, Min(0.01f)] private float fadeDuration = 0.3f;

    [Tooltip("Quanto o painel começa abaixo da posição final, em unidades do Canvas.")]
    [SerializeField] private float slideDistance = 60f;

    private const float MaxStep = 1f / 30f;

    private InputAction advance;
    private TutorialNote note;
    private RenderTexture target;
    private Vector2 panelRest;
    private float visibility;
    private bool promptVisible;
    private bool shown;

    public event Action AdvancePressed;

    public GameObject Root => root != null ? root : gameObject;

    private void Awake()
    {
        // Com o root salvo desligado, o Awake só roda dentro do SetActive(true) do primeiro Show.
        if (!shown)
            Root.SetActive(false);

        if (markup == null)
            markup = FindFirstObjectByType<HintController>();

        // Sem prompt o jogador não tem como saber que o bilhete fecha.
        if (promptText == null || promptText == bodyText)
            Debug.LogError("[TutorialNoteView] Prompt Text precisa ser um TMP próprio, separado do Body Text.", this);

        if (group == null && !Root.TryGetComponent(out group))
            group = Root.AddComponent<CanvasGroup>();

        if (panel != null)
            panelRest = panel.anchoredPosition;

        ApplyVisibility();
    }

    /// <summary>Abre com fade e subida. Chamado no meio do fechar, volta de onde parou.</summary>
    public void Show(TutorialNote content)
    {
        if (shown)
            return;

        shown = true;
        note = content;
        Root.SetActive(true);
        // Aplica já: sem isto o primeiro frame sai com o alpha do fim do último fechar (ou do Inspector).
        ApplyVisibility();

        advance = PlayerInputProvider.Actions.asset.FindAction(AdvanceAction, throwIfNotFound: true);
        advance.performed += OnAdvance;
        InputIcons.SchemeChanged += RefreshPrompt;

        // O do HintController e não o do InputIcons: ele só avisa depois de limpar o cache formatado.
        if (markup != null)
            markup.SchemeChanged += RefreshText;

        promptVisible = false;
        RefreshText();
        RefreshPrompt();
        // Reabrir no meio do fechar troca o vídeo, que ainda estava tocando o do bilhete anterior.
        StopClip();
        PlayClip(content.Clip);
    }

    /// <summary>Para de ler input na hora; o root só desliga quando o fade de saída termina.</summary>
    public void Hide()
    {
        if (!shown)
            return;

        Unsubscribe();
    }

    private void Update()
    {
        float goal = shown ? 1f : 0f;
        if (Mathf.Approximately(visibility, goal))
            return;

        // Não escalado (o jogo está com timeScale 0) e limitado: a primeira abertura trava no shader/atlas do TMP,
        // e o frame seguinte chegaria com ~1 s, pulando a animação inteira.
        visibility = Mathf.MoveTowards(visibility, goal, Mathf.Min(Time.unscaledDeltaTime, MaxStep) / fadeDuration);
        ApplyVisibility();

        if (!shown && visibility <= 0f)
        {
            StopClip();
            note = null;
            Root.SetActive(false);
        }
    }

    private void ApplyVisibility()
    {
        float eased = 1f - Mathf.Pow(1f - visibility, 3f);

        if (group != null)
            group.alpha = eased;

        if (panel != null)
            panel.anchoredPosition = panelRest + Vector2.down * (slideDistance * (1f - eased));
    }

    /// <summary>Esconde o prompt enquanto fechar ainda não vale, em vez de prometer o que não acontece.</summary>
    public void SetPromptVisible(bool visible)
    {
        promptVisible = visible;
        RefreshPrompt();
    }

    private void Unsubscribe()
    {
        shown = false;
        advance.performed -= OnAdvance;
        InputIcons.SchemeChanged -= RefreshPrompt;

        if (markup != null)
            markup.SchemeChanged -= RefreshText;
    }

    private void OnAdvance(InputAction.CallbackContext context) => AdvancePressed?.Invoke();

    private void RefreshText()
    {
        if (note == null || bodyText == null)
            return;

        bodyText.spriteAsset = InputIcons.Icons;
        bodyText.text = markup != null ? markup.Format(note.Body) : note.Body;
    }

    private void RefreshPrompt()
    {
        if (promptText == null || promptText == bodyText || !shown)
            return;

        string icon = InputIcons.GetActionIconName(AdvanceAction);
        string key = icon != null
            ? $"<sprite name=\"{icon}\">"
            : $"[{advance.GetBindingDisplayString(InputBinding.MaskByGroup(InputIcons.ActiveBindingGroup))}]";

        promptText.spriteAsset = InputIcons.Icons;
        // Replace e não string.Format: uma chave solta no texto do Inspector derrubaria o Format com exceção.
        promptText.text = promptVisible ? prompt.Replace("{0}", key) : string.Empty;
    }

    private void PlayClip(VideoClip clip)
    {
        if (clip == null || videoImage == null)
            return;

        if (videoPlayer == null)
            videoPlayer = videoImage.gameObject.AddComponent<VideoPlayer>();

        target = new RenderTexture((int)clip.width, (int)clip.height, 0) { name = $"TutorialNote_{clip.name}" };

        videoPlayer.playOnAwake = false;
        videoPlayer.source = VideoSource.VideoClip;
        videoPlayer.clip = clip;
        videoPlayer.isLooping = true;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.targetTexture = target;
        // O jogo fica com timeScale 0 enquanto o bilhete está aberto; no tempo de jogo o vídeo ficaria parado.
        videoPlayer.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;

        // Até o primeiro frame decodificado a textura é lixo: a janela fica vazia em vez de piscar.
        videoImage.texture = target;
        videoImage.enabled = false;
        videoPlayer.prepareCompleted -= OnPrepared;
        videoPlayer.prepareCompleted += OnPrepared;
        videoPlayer.Prepare();
    }

    private void OnPrepared(VideoPlayer player)
    {
        if (!shown)
            return;

        videoImage.enabled = true;
        player.Play();
    }

    private void StopClip()
    {
        if (videoPlayer != null)
        {
            videoPlayer.prepareCompleted -= OnPrepared;
            videoPlayer.Stop();
            videoPlayer.targetTexture = null;
        }

        if (videoImage != null)
            videoImage.texture = null;

        if (target != null)
        {
            target.Release();
            Destroy(target);
            target = null;
        }
    }

    // Só desassina: o asset de input e o InputIcons sobrevivem à cena e segurariam a view destruída.
    private void OnDestroy()
    {
        if (shown)
            Unsubscribe();

        StopClip();
    }
}
