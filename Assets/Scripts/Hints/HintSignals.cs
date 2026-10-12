using System;
using UnityEngine;

/// <summary>
/// Sinais nomeados que o roteiro do <see cref="HintDirector"/> espera ("chave_coletada",
/// "porta_principal"). Facade estática no molde do <see cref="AudioProvider"/>: quem dispara
/// não precisa achar o director, e o director não conhece chave, porta nem câmera.
///
/// Não guarda o que já tocou: isso é estado da partida, e mora no director, que nasce de novo
/// a cada reload de cena. Aqui sobreviveria ao reload e pularia passos de outra tentativa.
/// </summary>
public static class HintSignals
{
    public static event Action<string> Raised;

    public static void Raise(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            Raised?.Invoke(id.Trim());
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Raised = null;
}
