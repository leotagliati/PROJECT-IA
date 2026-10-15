using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Textura e material das baforadas pixeladas (<see cref="StepDust"/>, <see cref="BreathVapor"/>).
/// Quem chama é dono do que recebe e destrói os dois no OnDestroy.
/// </summary>
public static class PixelPuff
{
    /// <summary>
    /// Blob de pixels em Point: o "pixelado" vem da textura, não depende da resolução da
    /// RenderTexture da câmera. Borda irregular para não ler como círculo perfeito.
    /// </summary>
    public static Texture2D BuildTexture(int pixels, string name)
    {
        var tex = new Texture2D(pixels, pixels, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        float center = (pixels - 1) * 0.5f;
        var data = new Color32[pixels * pixels];

        for (int y = 0; y < pixels; y++)
        for (int x = 0; x < pixels; x++)
        {
            float d = new Vector2(x - center, y - center).magnitude / (pixels * 0.5f);
            float edge = 0.75f + Random.value * 0.25f;
            // Dois níveis de alpha em vez de gradiente: degrau duro é o que dá cara de pixel art.
            byte a = d < edge * 0.6f ? (byte)255 : d < edge ? (byte)120 : (byte)0;
            data[y * pixels + x] = new Color32(255, 255, 255, a);
        }

        tex.SetPixels32(data);
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>
    /// Mesmas configurações do AtmosphericDust.BuildMaterial: material por código pula o
    /// ShaderGUI do URP, então blend e keywords vão na mão.
    /// </summary>
    /// <param name="cameraFade">
    /// (perto, longe) em metros: abaixo de "perto" some, acima de "longe" opaco. Zero desliga.
    /// Para partícula que pode passar pela lente, senão vira um borrão de tela cheia.
    /// </param>
    public static Material BuildMaterial(Texture2D texture, bool lit, string name, Vector2 cameraFade = default)
    {
        Shader shader = Shader.Find(lit
            ? "Universal Render Pipeline/Particles/Lit"
            : "Universal Render Pipeline/Particles/Unlit");

        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

        if (shader == null)
        {
            Debug.LogError($"{nameof(PixelPuff)}: shaders de partícula do URP não encontrados.");
            return null;
        }

        var material = new Material(shader) { name = name };
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.Transparent;

        // _CameraFadeParams é HideInInspector e espera (near, 1/(far - near)) já calculado.
        if (cameraFade.y > cameraFade.x)
        {
            material.SetFloat("_CameraFadingEnabled", 1f);
            material.SetFloat("_CameraNearFadeDistance", cameraFade.x);
            material.SetFloat("_CameraFarFadeDistance", cameraFade.y);
            material.SetVector("_CameraFadeParams", new Vector4(cameraFade.x, 1f / (cameraFade.y - cameraFade.x), 0f, 0f));
            material.EnableKeyword("_FADING_ON");
        }

        return material;
    }
}
