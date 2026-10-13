using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Uma conversa: falas em ordem, cada uma com quem fala. Texto fora da cena, reaproveitável.</summary>
[CreateAssetMenu(fileName = "Dialogue", menuName = "Dialogue/Dialogue")]
public class DialogueAsset : ScriptableObject
{
    [Serializable]
    public struct Line
    {
        [Tooltip("Vazio = sem nome na caixa.")]
        public string speaker;

        [TextArea(2, 5)]
        public string text;
    }

    [SerializeField] private Line[] lines;

    public IReadOnlyList<Line> Lines => lines ?? Array.Empty<Line>();
}
