using UnityEngine;

/// <summary>
/// Facade estática do áudio, no molde do <see cref="PlayerInputProvider"/>: nenhum objeto
/// na cena, nenhuma ordem de execução a respeitar. No primeiro Play carrega a
/// <see cref="AudioLibrary"/> de Resources e instancia um <see cref="AudioPool"/> que
/// sobrevive à troca de cena.
///
///     AudioProvider.Play("footstep", x, z);
///     AudioProvider.PlayAt("door", posicao);
///     AudioHandle radio = AudioProvider.PlayLoop("radio", radioTransform);  // guarde para Stop
///
/// Mudo é estado do provider, não do pool: SetMuted(true) antes de qualquer som (treino
/// headless) nem chega a criar o pool.
/// </summary>
public static class AudioProvider
{
    private static AudioPool _pool;
    private static bool _muted;
    private static bool _libraryMissing;

    private static AudioPool Pool
    {
        get
        {
            // == e não ??: o pool é MonoBehaviour, e o "fake null" de objeto destruído
            // (Stop no editor, cena descarregada sem DontDestroyOnLoad) só o == enxerga.
            if (_pool == null && !_libraryMissing && Application.isPlaying)
                Create();

            return _pool;
        }
    }

    private static void Create()
    {
        var library = Resources.Load<AudioLibrary>(AudioLibrary.ResourcePath);

        if (library == null)
        {
            // Uma vez só: a partir daqui todo Play vira no-op silencioso — mas avisado.
            _libraryMissing = true;
            Debug.LogError($"[AudioProvider] Resources/{AudioLibrary.ResourcePath}.asset não encontrado. " +
                           "Crie via Assets → Create → Audio → Library.");
            return;
        }

        var host = new GameObject("AudioPool");
        Object.DontDestroyOnLoad(host);

        _pool = host.AddComponent<AudioPool>();
        _pool.Initialize(library);
        _pool.Muted = _muted;
    }

    // ------------------------------------------------------------------ API

    public static AudioHandle Play(string id, float x, float z, float volumeScale = 1f)
    {
        if (_muted)
            return AudioHandle.None;

        AudioPool pool = Pool;
        if (pool == null)
            return AudioHandle.None;

        Vector3 position = new Vector3(x, pool.Library.DefaultHeight, z);
        return pool.Play(id, position, null, false, volumeScale);
    }

    public static AudioHandle PlayAt(string id, Vector3 position, float volumeScale = 1f)
    {
        if (_muted)
            return AudioHandle.None;

        AudioPool pool = Pool;
        return pool == null ? AudioHandle.None : pool.Play(id, position, null, false, volumeScale);
    }

    /// <summary>Som que segue um objeto enquanto toca (passos, motor, alguém arrastando caixa).</summary>
    public static AudioHandle PlayFollowing(string id, Transform target, float volumeScale = 1f)
    {
        if (_muted || target == null)
            return AudioHandle.None;

        AudioPool pool = Pool;
        return pool == null ? AudioHandle.None : pool.Play(id, target.position, target, false, volumeScale);
    }

    /// <summary>Loop ocupa o slot até alguém chamar Stop — guarde o handle.</summary>
    public static AudioHandle PlayLoop(string id, Transform target, float volumeScale = 1f)
    {
        if (_muted || target == null)
            return AudioHandle.None;

        AudioPool pool = Pool;
        return pool == null ? AudioHandle.None : pool.Play(id, target.position, target, true, volumeScale);
    }

    public static bool IsPlaying(AudioHandle handle)
    {
        return _pool != null && _pool.IsPlaying(handle);
    }

    public static void Stop(AudioHandle handle)
    {
        // Acesso direto ao campo: parar um som não é motivo para criar o pool.
        if (_pool != null)
            _pool.Stop(handle);
    }

    public static void StopAll()
    {
        if (_pool != null)
            _pool.StopAll();
    }

    /// <summary>Treino headless com N arenas clonadas não tem por que gastar voz nenhuma.</summary>
    public static void SetMuted(bool muted)
    {
        _muted = muted;

        if (_pool == null)
            return;

        _pool.Muted = muted;

        if (muted)
            _pool.StopAll();
    }

    // Estáticos sobrevivem ao Stop quando o domain reload está desligado nas opções de
    // Play Mode: sem isso, o segundo Play começaria com _libraryMissing do primeiro.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _pool = null;
        _muted = false;
        _libraryMissing = false;
    }
}
