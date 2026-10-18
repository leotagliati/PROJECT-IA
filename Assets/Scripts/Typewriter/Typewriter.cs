using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Utilities;

/// <summary>
/// Máquina de escrever: bate, letra a letra, o texto escrito no <see cref="paperText"/>. O texto é lido no Awake,
/// o componente é zerado e cada batida acrescenta um caractere. Com <see cref="autoType"/> ela digita sozinha e
/// qualquer tecla/botão acelera; sem, só digita quando apertam.
///
/// Uma linha contendo só "---" separa páginas. Página completa vem para o ponto de leitura, fica pelo tempo de
/// leitura (qualquer tecla pula) e voa até o ponto de saída; a nova reaparece limpa no spawn e sobe até o rolo.
///
/// Lê o dispositivo direto (onAnyButtonPress), não o PlayerInputActions: aqui vale qualquer botão, inclusive
/// os que não estão mapeados. Desligue o componente para parar de aceitar digitação.
/// </summary>
public class Typewriter : MonoBehaviour
{
    [Header("Texto")]
    [Tooltip("Filho da Sheet, para subir junto com ela.")]
    [SerializeField] private TMP_Text paperText;

    [Header("Folha")]
    [SerializeField] private Transform sheet;

    [SerializeField, Min(0.01f)] private float feedTime = 0.2f;

    [Tooltip("Multiplica a subida por linha. 1 = exatamente uma altura de linha do texto.")]
    [SerializeField, Min(0f)] private float feedScale = 1f;

    [Header("Leitura da página")]
    [Tooltip("Pausa com a página recém-terminada ainda no rolo, antes de ela vir para a câmera.")]
    [SerializeField, Min(0f)] private float pageLiftDelay = 0.4f;

    [Tooltip("Opcional. Onde a página pronta fica para ser lida (posição e rotação: de frente para a câmera). " +
             "Vazio = é lida no próprio rolo.")]
    [SerializeField] private Transform pageReadPoint;

    [SerializeField, Min(0f)] private float pageShowTime = 0.6f;

    [Tooltip("Tempo de leitura = base + por caractere: página com três nomes passa rápido, agradecimento longo " +
             "fica mais. Qualquer tecla pula a espera.")]
    [SerializeField, Min(0f)] private float readTimeBase = 1.5f;

    [SerializeField, Min(0f)] private float readTimePerChar = 0.05f;

    [Tooltip("Tempo mínimo de leitura antes de uma tecla poder pular: quem está martelando para digitar mais " +
             "rápido pularia a página no mesmo instante em que ela chega.")]
    [SerializeField, Min(0f)] private float readSkipDelay = 0.6f;

    [Header("Troca de página")]
    [Tooltip("Para onde a folha pronta voa. Posição E rotação são usadas: crie o ponto como cópia da Sheet " +
             "e gire se quiser que ela saia girando.")]
    [SerializeField] private Transform pageExitPoint;

    [SerializeField, Min(0f)] private float pageEjectTime = 0.7f;

    [Tooltip("Onde a folha nova aparece (teleportada, já limpa) antes de subir até a posição inicial. " +
             "Posição e rotação, como o de saída.")]
    [SerializeField] private Transform pageSpawnPoint;

    [SerializeField, Min(0f)] private float pageInsertTime = 0.6f;

    [Header("Prompt")]
    [Tooltip("Opcional. Texto na tela que diz o que a tecla faz agora. Sem ícone de propósito: qualquer tecla " +
             "ou botão vale, e um ícone específico mentiria.")]
    [SerializeField] private TMP_Text promptText;

    [SerializeField] private string typingPrompt = "Aperte qualquer tecla para digitar\nAperte espaço para pular para o final";
    [SerializeField] private string nextPagePrompt = "Aperte qualquer tecla para a próxima página";
    [SerializeField] private string lastPagePrompt = "Aperte qualquer tecla para voltar ao menu";

    [Header("Automático")]
    [SerializeField] private bool autoType = true;

    [Tooltip("Espera antes da primeira letra.")]
    [SerializeField, Min(0f)] private float autoStartDelay = 0.8f;

    [Tooltip("Intervalo médio entre letras. O jogador apertando soma batidas por cima desse ritmo.")]
    [SerializeField, Min(0.01f)] private float autoInterval = 0.12f;

    [Tooltip("Variação do intervalo (fração), para soar humano em vez de metrônomo.")]
    [SerializeField, Range(0f, 1f)] private float autoJitter = 0.4f;

    [Tooltip("Pausa extra depois de pontuação.")]
    [SerializeField, Min(0f)] private float punctuationPause = 0.25f;

    [Header("Injetor")]
    [Tooltip("Cabeça que acompanha a letra. Posicione no editor onde a PRIMEIRA letra da linha é batida; " +
             "ela anda só na direção da linha (X do texto) e volta para cá a cada linha nova.")]
    [SerializeField] private Transform injector;

    [SerializeField, Min(0.001f)] private float injectorStepTime = 0.04f;

    [Tooltip("Tempo de suavização da volta ao início da linha.")]
    [SerializeField, Min(0.001f)] private float injectorReturnTime = 0.15f;

    [Header("Teclas")]
    [Tooltip("Teclas de letra. Cada caractere cai sempre na mesma tecla.")]
    [SerializeField] private Transform[] keys;

    [SerializeField] private Transform spaceKey;

    [Tooltip("Quanto a tecla afunda, no espaço local do PAI dela (as teclas do FBX podem estar inclinadas).")]
    [SerializeField] private Vector3 keyPressOffset = new Vector3(0f, -0.004f, 0f);

    [SerializeField, Min(0.01f)] private float keyPressTime = 0.12f;

    [Header("Ritmo")]
    [Tooltip("Intervalo mínimo entre batidas — o limite mecânico da máquina. 0.07 ≈ 14 letras/s.")]
    [SerializeField, Min(0f)] private float minStrikeInterval = 0.07f;

    [Tooltip("Toques guardados enquanto a máquina está no limite; o excesso é descartado.")]
    [SerializeField, Min(0)] private int maxQueued = 3;

    [Tooltip("Pausa extra depois de trocar de linha, para o retorno respirar.")]
    [SerializeField, Min(0f)] private float returnPause = 0.2f;

    [Header("Volume por velocidade")]
    [Tooltip("Volume das teclas com a digitação no máximo (1 = não abaixa).")]
    [SerializeField, Range(0f, 1f)] private float fastVolume = 0.55f;

    [Tooltip("Batidas seguidas, bem rápidas, até chegar no fastVolume.")]
    [SerializeField, Min(1)] private int rampStrikes = 8;

    [Tooltip("Segundos sem digitar para voltar do fastVolume ao volume cheio. Também define o que conta " +
             "como rápido: abaixo de rampStrikes/recoverTime letras por segundo o volume nem cai.")]
    [SerializeField, Min(0.01f)] private float recoverTime = 1.2f;

    [Header("Áudio (ids da AudioLibrary; vazio = sem som)")]
    [SerializeField] private string keySound = "typewriter_key";
    [Tooltip("Sino + alavanca de retorno num clipe só: na troca de linha os dois sempre tocam juntos.")]
    [SerializeField] private string returnSound = "typewriter_return";
    [Tooltip("Folha saindo / entrando na troca de página.")]
    [SerializeField] private string pageSound = "typewriter_page";

    [Header("Eventos")]
    [SerializeField] private UnityEvent finished;

    private readonly List<Transform> _allKeys = new List<Transform>();
    private Vector3[] _keyRest;
    private float[] _keyPressAge;

    private Vector3 _sheetRestPosition;
    private Quaternion _sheetRestRotation;
    private float _sheetRestY;
    private float _sheetTargetY;
    private float _sheetVelocity;

    private Vector3 _injectorRest;
    private Vector3 _injectorTarget;
    private Vector3 _injectorVelocity;
    private float _injectorTime;

    private IDisposable _anyButton;
    private string[] _pages;
    private int _page;
    private string _script;
    private int _cursor;
    private int _line;
    private char _lastChar;
    private bool _changingPage;
    private bool _reading;
    private bool _skipRead;
    private float _readStart;

    private float _cooldown;
    private int _queued;
    private float _heat;
    private float _autoTimer;

    public bool IsFinished { get; private set; }

    private void Reset()
    {
        paperText = GetComponentInChildren<TMP_Text>();

        var found = new List<Transform>();
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "SpaceKey")
                spaceKey = t;
            else if (t.name.StartsWith("Key"))
                found.Add(t);
            else if (t.name == "Sheet")
                sheet = t;
        }

        keys = found.ToArray();
    }

    private void Awake()
    {
        if (paperText == null)
        {
            Debug.LogError("[Typewriter] Sem paperText.", this);
            enabled = false;
            return;
        }

        // No Awake: o texto inteiro não pode aparecer nem no primeiro frame.
        _pages = SplitPages(paperText.text ?? string.Empty);
        _script = _pages[0];
        paperText.text = string.Empty;
        SetPrompt(null);

        foreach (Transform key in keys)
            if (key != null)
                _allKeys.Add(key);

        if (spaceKey != null)
            _allKeys.Add(spaceKey);

        _keyRest = new Vector3[_allKeys.Count];
        _keyPressAge = new float[_allKeys.Count];

        for (int i = 0; i < _allKeys.Count; i++)
        {
            _keyRest[i] = _allKeys[i].localPosition;
            _keyPressAge[i] = -1f;
        }

        if (sheet != null)
        {
            _sheetRestPosition = sheet.localPosition;
            _sheetRestRotation = sheet.localRotation;
            _sheetRestY = _sheetRestPosition.y;
            _sheetTargetY = _sheetRestY;
        }

        if (injector != null)
        {
            _injectorRest = injector.localPosition;
            _injectorTarget = _injectorRest;
            _injectorTime = injectorStepTime;
        }
    }

    private void Start()
    {
        WarnMissingSounds();
        _autoTimer = autoStartDelay;
        SetPrompt(typingPrompt);
    }

    private void OnEnable()
    {
        _anyButton = InputSystem.onAnyButtonPress.Call(OnAnyButton);
    }

    private void OnDisable()
    {
        _anyButton?.Dispose();
        _anyButton = null;
    }

    private void OnAnyButton(InputControl control)
    {
        // Esc e Start continuam reservados para voltar ao menu.
        if (control is KeyControl key && key.keyCode == Key.Escape)
            return;

        if (control.device is Gamepad pad && control == pad.startButton)
            return;

        // Durante a leitura, qualquer botão continua podendo pular a espera.
        if (_reading)
        {
            if (Time.time - _readStart >= readSkipDelay)
                _skipRead = true;

            return;
        }

        if (_changingPage || IsFinished)
            return;

        // Espaço completa instantaneamente a folha atual.
        if (control is KeyControl spaceKey && spaceKey.keyCode == Key.Space)
        {
            CompleteCurrentPage();
            return;
        }

        // Os demais botões mantêm o comportamento original.
        if (_cooldown <= 0f && _queued == 0)
            Fire(true);
        else if (_queued < maxQueued)
            _queued++;
    }

    private void CompleteCurrentPage()
    {
        if (IsFinished || _changingPage || _reading ||
            _script == null || _cursor >= _script.Length)
            return;

        // Cancela as batidas que ainda estavam na fila.
        _queued = 0;
        _cooldown = 0f;

        // Revela todo o texto restante de uma vez.
        _cursor = _script.Length;
        paperText.text = _script;
        paperText.ForceMeshUpdate();

        TMP_TextInfo info = paperText.textInfo;

        // Atualiza a posição final da folha e do injetor sem reproduzir
        // uma batida de tecla para cada caractere pulado.
        if (info.characterCount > 0)
        {
            TMP_CharacterInfo last = info.characterInfo[info.characterCount - 1];
            _line = last.lineNumber;

            if (sheet != null && info.lineCount > 0)
            {
                float rise = info.lineInfo[0].baseline - info.lineInfo[_line].baseline;
                Vector3 worldRise = paperText.transform.TransformVector(0f, rise, 0f);
                Transform parent = sheet.parent;
                float riseY = parent != null
                    ? parent.InverseTransformVector(worldRise).y
                    : worldRise.y;

                _sheetTargetY = _sheetRestY + riseY * feedScale;
            }

            if (!IsLineBreak(last.character))
                MoveInjector(info, last);
        }

        // Reutiliza a sequência normal de conclusão, leitura e troca de página.
        StartCoroutine(PageDone());
    }

    private void Fire(bool fromPlayer)
    {
        // Só o jogador esquenta: o ritmo automático é calmo por definição e tem que soar sempre cheio.
        if (fromPlayer)
            _heat = Mathf.Min(1f, _heat + 1f / rampStrikes);

        _cooldown = minStrikeInterval;
        TypeNext();
        _autoTimer = NextAutoDelay();
    }

    private float NextAutoDelay()
    {
        float delay = autoInterval * (1f + UnityEngine.Random.Range(-autoJitter, autoJitter));

        if (".,;:!?".IndexOf(_lastChar) >= 0)
            delay += punctuationPause;

        return delay;
    }

    public void TypeNext()
    {
        if (IsFinished || _changingPage || _script == null)
            return;

        int start = _cursor;

        // Quebra explícita não é batida de tecla: vai junto com a próxima letra, que é quando a folha sobe.
        while (_cursor < _script.Length && IsLineBreak(_script[_cursor]))
            _cursor++;

        if (_cursor < _script.Length)
            _cursor = NextCharEnd(_cursor);

        paperText.text = _script.Substring(0, _cursor);

        // Layout já, para saber em que linha a letra nova caiu (inclusive por quebra automática).
        paperText.ForceMeshUpdate();
        TMP_TextInfo info = paperText.textInfo;

        if (_cursor > start && info.characterCount > 0)
        {
            TMP_CharacterInfo ch = info.characterInfo[info.characterCount - 1];

            if (ch.lineNumber != _line)
            {
                _line = ch.lineNumber;
                FeedToLine(info);
            }

            if (!IsLineBreak(ch.character))
            {
                Strike(ch.character);
                MoveInjector(info, ch);
            }
        }

        if (_cursor < _script.Length)
            return;

        StartCoroutine(PageDone());
    }

    // Página completa: vem para a câmera, fica pelo tempo de leitura (ou até alguém apertar) e, se houver próxima,
    // sai voando enquanto a nova entra. A última fica parada na leitura até a cena sair.
    private IEnumerator PageDone()
    {
        _changingPage = true;
        _queued = 0;
        SetPrompt(null);

        yield return new WaitForSeconds(pageLiftDelay);

        if (sheet != null && pageReadPoint != null)
        {
            Play(pageSound, transform.position);
            yield return MoveSheet(sheet.position, sheet.rotation, pageReadPoint.position, pageReadPoint.rotation,
                                   pageShowTime, t => t * t * (3f - 2f * t));
        }

        bool last = _page + 1 >= _pages.Length;
        _reading = true;
        _skipRead = false;
        _readStart = Time.time;
        SetPrompt(last ? lastPagePrompt : nextPagePrompt);

        for (float t = readTimeBase + readTimePerChar * paperText.textInfo.characterCount; t > 0f && !_skipRead;
             t -= Time.deltaTime)
            yield return null;

        _reading = false;

        if (last)
        {
            IsFinished = true;
            finished.Invoke();
            yield break;
        }

        SetPrompt(null);
        Play(pageSound, transform.position);

        // Ease-in na saída (arranca e acelera, voando), ease-out na entrada (assenta no rolo).
        if (sheet != null && pageExitPoint != null)
            yield return MoveSheet(sheet.position, sheet.rotation, pageExitPoint.position, pageExitPoint.rotation,
                                   pageEjectTime, t => t * t);

        _page++;
        _script = _pages[_page];
        _cursor = 0;
        _line = 0;
        _lastChar = default;
        paperText.text = string.Empty;

        _injectorTarget = _injectorRest;
        _injectorTime = injectorReturnTime;

        if (sheet != null)
        {
            Transform parent = sheet.parent;
            Vector3 restPosition = parent != null ? parent.TransformPoint(_sheetRestPosition) : _sheetRestPosition;
            Quaternion restRotation = parent != null ? parent.rotation * _sheetRestRotation : _sheetRestRotation;

            Play(pageSound, transform.position);

            if (pageSpawnPoint != null)
                yield return MoveSheet(pageSpawnPoint.position, pageSpawnPoint.rotation, restPosition, restRotation,
                                       pageInsertTime, t => 1f - (1f - t) * (1f - t));

            // Pose exata no fim (sem resíduo da interpolação) e a subida por linha recomeça do zero.
            sheet.localPosition = _sheetRestPosition;
            sheet.localRotation = _sheetRestRotation;
            _sheetTargetY = _sheetRestY;
            _sheetVelocity = 0f;
        }

        _changingPage = false;
        _autoTimer = autoStartDelay;
        SetPrompt(typingPrompt);
    }

    // Vazio esconde: na transição entre páginas a tecla não faz nada, e um prompt ali mentiria.
    private void SetPrompt(string text)
    {
        if (promptText != null)
            promptText.text = text ?? string.Empty;
    }

    private IEnumerator MoveSheet(Vector3 fromPosition, Quaternion fromRotation, Vector3 toPosition,
                                  Quaternion toRotation, float duration, Func<float, float> ease)
    {
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = ease(t / duration);
            sheet.SetPositionAndRotation(Vector3.LerpUnclamped(fromPosition, toPosition, k),
                                         Quaternion.SlerpUnclamped(fromRotation, toRotation, k));
            yield return null;
        }

        sheet.SetPositionAndRotation(toPosition, toRotation);
    }

    private static string[] SplitPages(string text)
    {
        var pages = new List<string>();

        foreach (string page in Regex.Split(text, @"\r?\n[ \t]*---[ \t]*(?=\r?\n|$)"))
        {
            string trimmed = page.Trim('\r', '\n');
            if (trimmed.Length > 0)
                pages.Add(trimmed);
        }

        if (pages.Count == 0)
            pages.Add(string.Empty);

        return pages.ToArray();
    }

    // Tag de rich text entra inteira numa tecla só; senão "<b>" apareceria digitado letra a letra.
    private int NextCharEnd(int index)
    {
        if (paperText.richText && _script[index] == '<')
        {
            int close = _script.IndexOf('>', index);
            if (close >= 0)
            {
                index = close + 1;
                return index < _script.Length && !IsLineBreak(_script[index]) ? NextCharEnd(index) : index;
            }
        }

        return index + 1;
    }

    private void Strike(char c)
    {
        bool isSpace = char.IsWhiteSpace(c);
        int keyIndex = PickKey(c, isSpace);

        if (keyIndex >= 0)
            _keyPressAge[keyIndex] = 0f;

        _lastChar = c;

        // Mashing vira chiado de fundo em vez de metralhadora; digitação calma nunca esquenta.
        Vector3 soundAt = keyIndex >= 0 ? _allKeys[keyIndex].position : transform.position;
        Play(keySound, soundAt, Mathf.Lerp(1f, fastVolume, _heat));
    }

    private int PickKey(char c, bool isSpace)
    {
        if (isSpace)
            return spaceKey != null ? _allKeys.Count - 1 : -1;

        int letterKeys = spaceKey != null ? _allKeys.Count - 1 : _allKeys.Count;
        if (letterKeys == 0)
            return -1;

        // Determinístico: a mesma letra afunda sempre a mesma tecla, que é o que o olho espera.
        return char.ToLowerInvariant(c) % letterKeys;
    }

    private void FeedToLine(TMP_TextInfo info)
    {
        // Baseline em espaço local do texto (desce a cada linha); passa pelo mundo até o espaço do pai da
        // Sheet porque os dois podem ter escala/rotação diferentes. Só o Y é usado.
        float rise = info.lineInfo[0].baseline - info.lineInfo[_line].baseline;
        Vector3 worldRise = paperText.transform.TransformVector(0f, rise, 0f);

        if (sheet != null)
        {
            Transform parent = sheet.parent;
            float riseY = parent != null ? parent.InverseTransformVector(worldRise).y : worldRise.y;
            _sheetTargetY = _sheetRestY + riseY * feedScale;
        }

        _injectorTime = injectorReturnTime;
        _cooldown += returnPause;
        Play(returnSound, transform.position);
    }

    private void MoveInjector(TMP_TextInfo info, TMP_CharacterInfo last)
    {
        if (injector == null)
            return;

        // Para a próxima casa: distância (no X do texto) do começo da linha até o fim da letra batida. Medido a
        // partir da primeira letra da linha, não da margem, para não depender de alinhamento/margens do TMP.
        float lineStart = info.characterInfo[info.lineInfo[last.lineNumber].firstCharacterIndex].origin;
        Vector3 worldOffset = paperText.transform.TransformVector(last.xAdvance - lineStart, 0f, 0f);

        Transform parent = injector.parent;
        _injectorTarget = _injectorRest + (parent != null ? parent.InverseTransformVector(worldOffset) : worldOffset);
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        _heat = Mathf.Max(0f, _heat - dt / recoverTime);
        _cooldown = Mathf.Max(0f, _cooldown - dt);

        if (_cooldown <= 0f && _queued > 0)
        {
            _queued--;
            Fire(true);
        }
        else if (autoType && !_changingPage && !IsFinished)
        {
            _autoTimer -= dt;

            if (_autoTimer <= 0f && _cooldown <= 0f)
                Fire(false);
        }

        for (int i = 0; i < _allKeys.Count; i++)
        {
            float age = _keyPressAge[i];
            if (age < 0f)
                continue;

            age += dt;
            float t = age / keyPressTime;

            if (t >= 1f)
            {
                _allKeys[i].localPosition = _keyRest[i];
                _keyPressAge[i] = -1f;
                continue;
            }

            // Desce rápido (primeiros 30%) e volta devagar, como a mola de uma tecla.
            float depth = t < 0.3f ? t / 0.3f : 1f - (t - 0.3f) / 0.7f;
            _allKeys[i].localPosition = _keyRest[i] + keyPressOffset * depth;
            _keyPressAge[i] = age;
        }

        // Na troca de página quem move a folha é o ChangePage; o SmoothDamp brigaria com ele.
        if (sheet != null && !_changingPage)
        {
            Vector3 p = sheet.localPosition;
            p.y = Mathf.SmoothDamp(p.y, _sheetTargetY, ref _sheetVelocity, feedTime);
            sheet.localPosition = p;
        }

        if (injector != null)
        {
            injector.localPosition = Vector3.SmoothDamp(injector.localPosition, _injectorTarget,
                                                        ref _injectorVelocity, _injectorTime);

            // A volta usa o tempo longo até chegar; trocar no meio por causa da próxima letra daria um tranco.
            if ((injector.localPosition - _injectorTarget).sqrMagnitude < 1e-8f)
                _injectorTime = injectorStepTime;
        }
    }

    private void Play(string id, Vector3 position, float volumeScale = 1f)
    {
        if (!string.IsNullOrEmpty(id))
            AudioProvider.PlayAt(id, position, volumeScale);
    }

    private void WarnMissingSounds()
    {
        AudioLibrary library = AudioProvider.Library;
        if (library == null)
            return;

        // Os ids já vêm preenchidos para quando os samples entrarem; até lá, apaga os que faltam (um aviso só)
        // em vez de o AudioPool reclamar a cada tecla.
        var missing = new List<string>();
        keySound = Validate(library, keySound, missing);
        returnSound = Validate(library, returnSound, missing);
        pageSound = Validate(library, pageSound, missing);

        if (missing.Count > 0)
            Debug.LogWarning("[Typewriter] Sem clipe na AudioLibrary para: " + string.Join(", ", missing), this);
    }

    private static string Validate(AudioLibrary library, string id, List<string> missing)
    {
        if (string.IsNullOrEmpty(id) || library.TryGet(id, out _))
            return id;

        missing.Add(id);
        return null;
    }

    private static bool IsLineBreak(char c) => c == '\n' || c == '\r';
}
