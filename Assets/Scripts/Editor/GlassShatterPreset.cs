using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Botão direito no componente <see cref="GlassShatter"/> → "Aplicar preset de cacos". Configura o ParticleSystem
/// do zero (o default da Unity emite por 5 s, vive 5 s e sai com material que o URP pinta de roxo) e cria, uma
/// vez só, o material e a malha do caco em <see cref="OutputFolder"/>. Depois disso é asset normal: ajuste no
/// Inspector à vontade.
/// </summary>
public static class GlassShatterPreset
{
    private const string OutputFolder = "Assets/Materials";
    private const string MaterialPath = OutputFolder + "/GlassShard.mat";
    private const string MeshPath = OutputFolder + "/GlassShard.asset";

    [MenuItem("CONTEXT/GlassShatter/Aplicar preset de cacos")]
    private static void Apply(MenuCommand command)
    {
        var shatter = (GlassShatter)command.context;
        var system = shatter.GetComponent<ParticleSystem>();
        var renderer = shatter.GetComponent<ParticleSystemRenderer>();

        Material material = LoadOrCreateMaterial();
        if (material == null)
            return;

        Undo.RecordObjects(new Object[] { system, renderer, shatter.transform }, "Preset de cacos");

        shatter.transform.localScale = Vector3.one;

        var main = system.main;
        main.duration = 0.1f;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.05f);
        main.startRotation3D = true;
        main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new Color(0.75f, 0.9f, 0.8f, 0.85f);
        main.gravityModifier = 1f;
        main.maxParticles = 28;
        main.stopAction = ParticleSystemStopAction.Destroy;

        var emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        emission.rateOverDistance = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)28) });

        // Cone emite em +Z, que o GlassShatter vira para a normal da batida.
        var shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 70f;
        shape.radius = 0.04f;
        shape.position = Vector3.zero;
        shape.rotation = Vector3.zero;

        var rotation = system.rotationOverLifetime;
        rotation.enabled = true;
        rotation.separateAxes = true;
        rotation.x = new ParticleSystem.MinMaxCurve(-12f, 12f);
        rotation.y = new ParticleSystem.MinMaxCurve(-12f, 12f);
        rotation.z = new ParticleSystem.MinMaxCurve(-12f, 12f);

        var collision = system.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.mode = ParticleSystemCollisionMode.Collision3D;
        collision.quality = ParticleSystemCollisionQuality.Medium;
        collision.bounce = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
        collision.dampen = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
        collision.radiusScale = 0.5f;

        var fade = system.colorOverLifetime;
        fade.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
        fade.color = new ParticleSystem.MinMaxGradient(gradient);

        // Triângulo plano girando em 3D: pisca ao virar contra a lanterna, que é o brilho do vidro.
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.mesh = LoadOrCreateMesh();
        renderer.alignment = ParticleSystemRenderSpace.World;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;

        EditorUtility.SetDirty(system);
        EditorUtility.SetDirty(renderer);
    }

    // Particles/Lit: no escuro, caco unlit brilharia sozinho. Material criado por código pula o ShaderGUI do URP,
    // então blend, keywords e fila vão na mão (ver AtmosphericDust).
    private static Material LoadOrCreateMaterial()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null)
            return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Lit");
        if (shader == null)
        {
            Debug.LogError("[GlassShatterPreset] Shader Universal Render Pipeline/Particles/Lit não encontrado.");
            return null;
        }

        var material = new Material(shader) { name = "GlassShard" };
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Smoothness", 0.9f);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;

        AssetDatabase.CreateAsset(material, MaterialPath);
        return material;
    }

    private static Mesh LoadOrCreateMesh()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (existing != null)
            return existing;

        var mesh = new Mesh { name = "GlassShard" };
        mesh.vertices = new[] { new Vector3(-0.5f, -0.4f, 0f), new Vector3(0.6f, -0.2f, 0f), new Vector3(-0.1f, 0.6f, 0f) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.RecalculateBounds();

        AssetDatabase.CreateAsset(mesh, MeshPath);
        return mesh;
    }
}
