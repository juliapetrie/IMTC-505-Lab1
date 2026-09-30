using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Hold the mouse button and drag: a sparkle particle trail follows the cursor.
/// Put it on any object (the piano is fine). The particle system, its material
/// and its texture are all built in code, so no setup is needed.
/// </summary>
public class MouseSparkleTrail : MonoBehaviour
{
    public enum MouseButton { Left, Right, Middle }

    [Header("Input")]
    public MouseButton button = MouseButton.Left;
    [Tooltip("Defaults to Main Camera.")]
    public Camera viewCamera;
    [Tooltip("The trail is drawn just in front of this object. Defaults to the piano.")]
    public Transform depthReference;

    [Header("Particles")]
    [Tooltip("How many sparkles are left behind when dragging across the whole screen width.")]
    public float particlesPerScreenWidth = 160f;
    [Tooltip("Sparkles per second while the mouse is held still.")]
    public float particlesPerSecond = 25f;
    [Tooltip("Sparkles that pop out when you first click.")]
    public int burstOnClick = 25;
    [Tooltip("Sparkle size as a fraction of the screen height.")]
    public float size = 0.018f;
    public float lifetime = 0.8f;

    [Header("Colour")]
    [Tooltip("Cycle through the rainbow while dragging. Off: use the colour below.")]
    public bool rainbow = true;
    public float rainbowSpeed = 0.5f;
    public Color colour = new Color(1f, 0.9f, 0.5f);

    [Tooltip("Optional. Leave empty to use a generated sparkle material.")]
    public Material particleMaterial;

    ParticleSystem ps;
    bool wasHeld;
    bool emitFromNextFrame;

    void Awake()
    {
        var go = new GameObject("MouseSparkleTrail") { hideFlags = HideFlags.DontSave };
        ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;   // sparkles stay where they were dropped
        main.maxParticles = 2000;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.5f, lifetime);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

        var emission = ps.emission;
        emission.enabled = false;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
        fade.color = g;

        var shrink = ps.sizeOverLifetime;
        shrink.enabled = true;
        shrink.size = new ParticleSystem.MinMaxCurve(1f,
            new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.1f, 1f), new Keyframe(1f, 0f)));

        var spin = ps.rotationOverLifetime;
        spin.enabled = true;
        spin.z = new ParticleSystem.MinMaxCurve(-3f, 3f);

        var noise = ps.noise;
        noise.enabled = true;
        noise.frequency = 1.2f;
        noise.scrollSpeed = 0.6f;

        var rend = go.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Billboard;
        rend.sharedMaterial = particleMaterial != null ? particleMaterial : MakeMaterial();

        ps.Play();
    }

    void Update()
    {
        Camera cam = viewCamera != null ? viewCamera : Camera.main;
        if (cam == null) return;

        ReadMouse(out bool held, out Vector2 mousePos);

        // World size of the screen at the trail's depth, so the trail looks the same at any scale.
        float depth = TrailDepth(cam);
        float screenHeight = cam.orthographic
            ? cam.orthographicSize * 2f
            : 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float screenWidth = screenHeight * cam.aspect;

        var main = ps.main;
        main.startSize = new ParticleSystem.MinMaxCurve(size * screenHeight * 0.6f, size * screenHeight * 1.4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(screenHeight * 0.01f, screenHeight * 0.06f);
        main.gravityModifier = screenHeight * 0.01f;
        main.startColor = rainbow ? Color.HSVToRGB((Time.time * rainbowSpeed) % 1f, 0.55f, 1f) : colour;

        var shape = ps.shape;
        shape.radius = screenHeight * 0.004f;
        var noise = ps.noise;
        noise.strength = screenHeight * 0.03f;

        var emission = ps.emission;
        emission.rateOverDistance = particlesPerScreenWidth / Mathf.Max(0.0001f, screenWidth);
        emission.rateOverTime = particlesPerSecond;

        if (held)
        {
            Ray ray = cam.ScreenPointToRay(mousePos);
            float along = depth / Mathf.Max(0.0001f, Vector3.Dot(ray.direction, cam.transform.forward));
            ps.transform.position = ray.GetPoint(along);

            if (!wasHeld)
            {
                // Jump to the cursor without drawing a line from the last spot, then burst.
                emission.enabled = false;
                ps.Emit(burstOnClick);
                emitFromNextFrame = true;
            }
            else if (emitFromNextFrame)
            {
                emission.enabled = true;
                emitFromNextFrame = false;
            }
        }
        else
        {
            emission.enabled = false;
            emitFromNextFrame = false;
        }
        wasHeld = held;
    }

    void ReadMouse(out bool held, out Vector2 pos)
    {
        held = false;
        pos = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        Mouse m = Mouse.current;
        if (m == null) return;
        pos = m.position.ReadValue();
        held = button == MouseButton.Left ? m.leftButton.isPressed
             : button == MouseButton.Right ? m.rightButton.isPressed
             : m.middleButton.isPressed;
#else
        pos = Input.mousePosition;
        held = Input.GetMouseButton((int)button);
#endif
    }

    float TrailDepth(Camera cam)
    {
        if (depthReference == null)
        {
            var piano = FindAnyObjectByType<PianoController>();
            depthReference = piano != null ? piano.transform : transform;
        }
        float d = Vector3.Dot(depthReference.position - cam.transform.position, cam.transform.forward);
        return Mathf.Max(cam.nearClipPlane * 2f, d * 0.92f);   // slightly in front so it isn't hidden by the keys
    }

    void OnDestroy()
    {
        if (ps != null) Destroy(ps.gameObject);
    }

    static Material MakeMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");

        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "SparkleTrail"
        };
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float u = (x + 0.5f) / S * 2f - 1f, v = (y + 0.5f) / S * 2f - 1f;
            float q = Mathf.Pow(Mathf.Abs(u), 2f / 3f) + Mathf.Pow(Mathf.Abs(v), 2f / 3f);   // 4-point star
            float star = Mathf.Clamp01((1f - q) * 6f);
            float glow = 0.35f * Mathf.Exp(-12f * (u * u + v * v));
            px[y * S + x] = new Color32(255, 255, 255, (byte)(Mathf.Max(star, glow) * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();

        var mat = new Material(shader) { name = "SparkleTrail" };
        mat.mainTexture = tex;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        return mat;
    }
}