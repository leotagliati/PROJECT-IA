using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Troca de cena com a mesma tela de loading do menu → jogo, para destinos sem <see cref="LoadingWarmup"/> (o menu)
/// ou já aquecidos nesta sessão (o reinício).
/// Vive em DontDestroyOnLoad: a tela atravessa a troca e sai com fade já na cena nova — sem isso o load congela o
/// pause na tela e a cena nova entra em corte seco.
/// </summary>
public class LoadingTransition : MonoBehaviour
{
    public static bool IsRunning { get; private set; }

    /// <summary>False se já há uma transição ou a cena não carrega; quem chama não faz mais nada.</summary>
    public static bool Begin(SceneReference scene, GameObject screenPrefab, float fadeTime = 0.6f) =>
        Begin(scene.LoadAsync, screenPrefab, fadeTime);

    // Recebe o load por função para não começar um segundo carregamento quando já há transição rodando.
    public static bool Begin(Func<AsyncOperation> startLoad, GameObject screenPrefab, float fadeTime = 0.6f)
    {
        if (IsRunning)
            return false;

        AsyncOperation load = startLoad();
        if (load == null)
            return false;

        load.allowSceneActivation = false;
        IsRunning = true;

        var host = new GameObject("LoadingTransition").AddComponent<LoadingTransition>();
        DontDestroyOnLoad(host.gameObject);
        host.StartCoroutine(host.Run(load, screenPrefab, fadeTime));
        return true;
    }

    // Tudo em tempo não escalado: sai do pause e do game over, com timeScale 0.
    private IEnumerator Run(AsyncOperation load, GameObject screenPrefab, float fadeTime)
    {
        // Hover/clique no menu de pause continuariam respondendo atrás do preto.
        if (EventSystem.current != null)
            EventSystem.current.enabled = false;

        Image fade = CreateFade();

        for (float t = 0f; t < fadeTime; t += Time.unscaledDeltaTime)
        {
            SetAlpha(fade, t / fadeTime);
            yield return null;
        }

        SetAlpha(fade, 1f);

        CanvasGroup screenGroup = null;
        if (screenPrefab != null)
        {
            GameObject screen = Instantiate(screenPrefab, transform);
            LoadingWarmup.PutOnTop(screen);
            if (!screen.TryGetComponent(out screenGroup))
                screenGroup = screen.AddComponent<CanvasGroup>();

            // Mesmo sortingOrder daria ordem indefinida; o preto fica logo abaixo, como fundo.
            fade.canvas.sortingOrder = short.MaxValue - 1;
        }

        while (load.progress < 0.9f)
            yield return null;

        // Um frame com a tela já desenhada: a ativação trava o main thread e o que estiver na tela fica congelado.
        yield return null;
        load.allowSceneActivation = true;

        while (!load.isDone)
            yield return null;

        // Os Start da cena nova e o primeiro render engasgam; a tela só sai depois deles.
        yield return null;
        yield return null;

        for (float t = 0f; t < fadeTime; t += Time.unscaledDeltaTime)
        {
            float a = 1f - t / fadeTime;
            SetAlpha(fade, a);
            if (screenGroup != null)
                screenGroup.alpha = a;
            yield return null;
        }

        Destroy(gameObject);
    }

    private void OnDestroy() => IsRunning = false;

    private Image CreateFade()
    {
        var go = new GameObject("Fade", typeof(Canvas), typeof(Image));
        go.transform.SetParent(transform, false);

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

    private static void SetAlpha(Image image, float alpha) => image.color = new Color(0f, 0f, 0f, alpha);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => IsRunning = false;
}
