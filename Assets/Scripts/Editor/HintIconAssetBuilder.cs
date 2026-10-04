using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;

/// <summary>
/// Monta o TMP Sprite Asset de ícones de input que o <see cref="HintController"/> usa, a
/// partir dos PNGs soltos do pacote do Xelu em Assets/Images/InputSystem.
///
/// Cada sprite recebe o nome do CONTROLE no Input System ("buttonSouth", "leftStick"), não o
/// nome do arquivo: é o que o controller tem na mão depois de ler o binding. Por isso a
/// tabela abaixo é o único lugar que sabe que buttonSouth de PlayStation é o X.
///
/// Gerar de novo sobrescreve atlas e asset no mesmo caminho, mantendo o GUID: quem já
/// referencia o asset no Inspector não perde a referência.
/// </summary>
public static class HintIconAssetBuilder
{
    private const string SourceRoot = "Assets/Images/InputSystem";
    private const string OutputFolder = "Assets/Images/HintIcons";

    // Altura do ícone em relação ao ascender da fonte (o TMP escala sprite para o ascender).
    // Um pouco maior que o texto lê como botão; igual some no meio das letras.
    private const float IconScale = 1.35f;

    // Fração do ícone acima da baseline. Sem isso o TMP apoia o ícone na baseline e ele
    // parece afundar abaixo das letras minúsculas.
    private const float AboveBaseline = 0.78f;

    // Folga entre um ícone e o próximo caractere, em fração da largura.
    private const float AdvancePadding = 1.08f;

    private static readonly (string control, string file)[] PlayStation4 =
    {
        ("buttonSouth", "PS4_Cross"),
        ("buttonEast", "PS4_Circle"),
        ("buttonWest", "PS4_Square"),
        ("buttonNorth", "PS4_Triangle"),
        ("leftShoulder", "PS4_L1"),
        ("rightShoulder", "PS4_R1"),
        ("leftTrigger", "PS4_L2"),
        ("rightTrigger", "PS4_R2"),
        ("leftStick", "PS4_Left_Stick"),
        ("rightStick", "PS4_Right_Stick"),
        ("leftStickPress", "PS4_Left_Stick_Click"),
        ("rightStickPress", "PS4_Right_Stick_Click"),
        ("dpad", "PS4_Dpad"),
        ("dpad_up", "PS4_Dpad_Up"),
        ("dpad_down", "PS4_Dpad_Down"),
        ("dpad_left", "PS4_Dpad_Left"),
        ("dpad_right", "PS4_Dpad_Right"),
        ("start", "PS4_Options"),
        ("select", "PS4_Share"),
        ("touchpadButton", "PS4_Touch_Pad"),
    };

    // Controles nomeados de teclado e mouse. Letras e dígitos não estão aqui: o nome do
    // controle é o próprio caractere ("w", "1") e o arquivo é ele em maiúscula.
    private static readonly (string control, string file)[] KeyboardMouse =
    {
        ("leftShift", "Shift"), ("rightShift", "Shift"),
        ("leftCtrl", "Ctrl"), ("rightCtrl", "Ctrl"),
        ("leftAlt", "Alt"), ("rightAlt", "Alt"),
        ("leftMeta", "Win"), ("rightMeta", "Win"),
        ("space", "Space"), ("tab", "Tab"), ("escape", "Esc"), ("enter", "Enter"),
        ("backspace", "Backspace"), ("capsLock", "Caps_Lock"), ("delete", "Del"),
        ("insert", "Insert"), ("home", "Home"), ("end", "End"),
        ("pageUp", "Page_Up"), ("pageDown", "Page_Down"),
        ("upArrow", "Arrow_Up"), ("downArrow", "Arrow_Down"),
        ("leftArrow", "Arrow_Left"), ("rightArrow", "Arrow_Right"),
        ("minus", "Minus"), ("equals", "Plus"), ("semicolon", "Semicolon"), ("quote", "Quote"),
        ("slash", "Slash"), ("backquote", "Tilda"), ("comma", "Mark_Left"), ("period", "Mark_Right"),
        ("leftBracket", "Bracket_Left"), ("rightBracket", "Bracket_Right"),
        ("printScreen", "Print_Screen"), ("numLock", "Num_Lock"), ("numpadMultiply", "Asterisk"),
        ("f1", "F1"), ("f2", "F2"), ("f3", "F3"), ("f4", "F4"), ("f5", "F5"), ("f6", "F6"),
        ("f7", "F7"), ("f8", "F8"), ("f9", "F9"), ("f10", "F10"), ("f11", "F11"), ("f12", "F12"),

        // <Mouse>/delta e <Pointer>/delta caem os dois em "delta" (ver HintController.IconName).
        ("delta", "Mouse_Simple"), ("scroll", "Mouse_Middle"),
        ("leftButton", "Mouse_Left"), ("rightButton", "Mouse_Right"), ("middleButton", "Mouse_Middle"),

        // Para [Mouse] escrito à mão no texto da dica.
        ("Mouse", "Mouse_Simple"),
    };

    [MenuItem("Tools/Hints/Gerar ícones PS4")]
    private static void BuildPlayStation4() => Build("HintIcons_PS4", "Others/PS4", PlayStation4);

    [MenuItem("Tools/Hints/Gerar ícones PC (escuro)")]
    private static void BuildKeyboardDark() => Build("HintIcons_PC_Dark", "Keyboard & Mouse/Dark", KeyboardMap("Dark"));

    [MenuItem("Tools/Hints/Gerar ícones PC (claro)")]
    private static void BuildKeyboardLight() => Build("HintIcons_PC_Light", "Keyboard & Mouse/Light", KeyboardMap("Light"));

    private static (string control, string file)[] KeyboardMap(string variant)
    {
        var map = new List<(string control, string file)>();

        for (char c = 'a'; c <= 'z'; c++)
            map.Add((c.ToString(), $"{char.ToUpperInvariant(c)}_Key_{variant}"));

        for (char c = '0'; c <= '9'; c++)
            map.Add((c.ToString(), $"{c}_Key_{variant}"));

        foreach ((string control, string file) in KeyboardMouse)
            map.Add((control, $"{file}_Key_{variant}"));

        return map.ToArray();
    }

    private static void Build(string assetName, string sourceFolder, (string control, string file)[] map)
    {
        var textures = new List<Texture2D>();
        var textureByFile = new Dictionary<string, int>();
        var characters = new List<(string name, int texture)>();

        foreach ((string control, string file) in map)
        {
            // Esquerdo e direito (leftShift/rightShift) usam a mesma imagem: um glifo só no
            // atlas, dois nomes apontando para ele.
            if (!textureByFile.TryGetValue(file, out int index))
            {
                string path = $"{SourceRoot}/{sourceFolder}/{file}.png";
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"{nameof(HintIconAssetBuilder)}: '{path}' não existe — '{control}' fica sem ícone.");
                    continue;
                }

                // Lido do disco, e não pelo AssetDatabase: assim não depende do Read/Write do
                // import settings de cada um dos PNGs do pacote.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.LoadImage(File.ReadAllBytes(path));

                index = textures.Count;
                textures.Add(texture);
                textureByFile.Add(file, index);
            }

            characters.Add((control, index));
        }

        if (textures.Count == 0)
        {
            Debug.LogError($"{nameof(HintIconAssetBuilder)}: nenhum ícone encontrado em {SourceRoot}/{sourceFolder}.");
            return;
        }

        var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Rect[] uvs = atlas.PackTextures(textures.ToArray(), 4, 2048);

        Directory.CreateDirectory(OutputFolder);
        string atlasPath = $"{OutputFolder}/{assetName}.png";
        File.WriteAllBytes(atlasPath, atlas.EncodeToPNG());
        AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceUpdate);

        // Mipmap porque o ícone de 100px aparece a ~40px na tela; sem ele, serrilha.
        var importer = (TextureImporter)AssetImporter.GetAtPath(atlasPath);
        importer.textureType = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = true;
        importer.filterMode = FilterMode.Trilinear;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.SaveAndReimport();

        Texture2D sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);

        string assetPath = $"{OutputFolder}/{assetName}.asset";
        var spriteAsset = AssetDatabase.LoadAssetAtPath<TMP_SpriteAsset>(assetPath);
        if (spriteAsset == null)
        {
            spriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            AssetDatabase.CreateAsset(spriteAsset, assetPath);
        }

        // Sem a versão, o TMP acha que é um asset do formato antigo e "atualiza" a partir da
        // lista legada (vazia), apagando as tabelas montadas abaixo. O setter é internal.
        var serialized = new SerializedObject(spriteAsset);
        serialized.FindProperty("m_Version").stringValue = "1.1.0";
        serialized.ApplyModifiedPropertiesWithoutUndo();

        spriteAsset.spriteSheet = sheet;
        spriteAsset.hashCode = TMP_TextUtilities.GetSimpleHashCode(spriteAsset.name);

        // Os setters das tabelas também são internal; a lista devolvida pelo getter é a própria.
        spriteAsset.spriteGlyphTable.Clear();
        spriteAsset.spriteCharacterTable.Clear();

        var glyphs = new TMP_SpriteGlyph[textures.Count];

        for (int i = 0; i < textures.Count; i++)
        {
            Rect uv = uvs[i];
            int width = textures[i].width;
            int height = textures[i].height;

            var rect = new GlyphRect(
                Mathf.RoundToInt(uv.x * atlas.width), Mathf.RoundToInt(uv.y * atlas.height), width, height);
            var metrics = new GlyphMetrics(width, height, 0f, height * AboveBaseline, width * AdvancePadding);
            glyphs[i] = new TMP_SpriteGlyph((uint)i, metrics, rect, 1f, 0);

            spriteAsset.spriteGlyphTable.Add(glyphs[i]);
        }

        foreach ((string characterName, int texture) in characters)
        {
            spriteAsset.spriteCharacterTable.Add(new TMP_SpriteCharacter(0xFFFE, glyphs[texture])
            {
                name = characterName,
                scale = IconScale,
            });
        }

        if (spriteAsset.material == null)
        {
            var material = new Material(Shader.Find("TextMeshPro/Sprite")) { name = assetName + " Material" };
            AssetDatabase.AddObjectToAsset(material, spriteAsset);
            spriteAsset.material = material;
        }

        spriteAsset.material.SetTexture(ShaderUtilities.ID_MainTex, sheet);
        spriteAsset.UpdateLookupTables();

        EditorUtility.SetDirty(spriteAsset.material);
        EditorUtility.SetDirty(spriteAsset);
        AssetDatabase.SaveAssets();

        foreach (Texture2D texture in textures)
            Object.DestroyImmediate(texture);
        Object.DestroyImmediate(atlas);

        Selection.activeObject = spriteAsset;
        EditorGUIUtility.PingObject(spriteAsset);
        Debug.Log($"{nameof(HintIconAssetBuilder)}: {assetPath} com {characters.Count} ícones ({textures.Count} imagens).", spriteAsset);
    }
}
