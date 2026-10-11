using System;
using TMPro;
using UnityEngine;

[CreateAssetMenu(menuName = "Input/Icon Library", fileName = "InputIconLibrary")]
public class InputIconLibrary : ScriptableObject
{
    public const string ResourcePath = "InputIconLibrary";

    [Serializable]
    public struct Scheme
    {
        [Tooltip("Nome do control scheme no .inputactions.")]
        public string bindingGroup;

        [Tooltip("Vazio = as teclas deste esquema ficam sem ícone.")]
        public TMP_SpriteAsset icons;
    }

    [Tooltip("O primeiro é o inicial.")]
    public Scheme[] schemes =
    {
        new() { bindingGroup = "Keyboard&Mouse" },
        new() { bindingGroup = "Gamepad" },
    };
}
