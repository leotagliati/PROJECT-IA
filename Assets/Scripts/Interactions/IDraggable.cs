using UnityEngine;

/// <summary>
/// Interagível que se manipula segurando o botão (porta, gaveta, alavanca). O
/// <see cref="InteractionController"/> traduz o mouse em deslocamento da "mão" em mundo e
/// entrega aqui; cada objeto projeta isso no seu próprio grau de liberdade (giro na
/// dobradiça, deslize no trilho). Assim o controller não sabe o que é porta.
/// </summary>
public interface IDraggable
{
    /// <summary>Ponto agarrado, em mundo. Usado para soltar quando o jogador se afasta.</summary>
    Vector3 GrabPoint { get; }

    InteractionResult BeginDrag(InteractionController interactor, Vector3 grabPoint);

    /// <summary>Deslocamento da mão neste frame, em metros, em mundo.</summary>
    void Drag(Vector3 handDelta);

    void EndDrag();
}
