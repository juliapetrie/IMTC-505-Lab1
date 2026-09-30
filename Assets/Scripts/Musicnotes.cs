using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Put on the same object as PianoController. While a key is held, colourful music notes
/// float up from it, fading out as they rise. The note images are drawn in code, so no
/// sprites or other assets are needed.
/// </summary>
[RequireComponent(typeof(PianoController))]
public class MusicNotes : MonoBehaviour
{
    [Header("Spawning")]
    [Tooltip("Seconds between notes while a key is held. The first note appears immediately.")]
    public float spawnInterval = 0.18f;
    [Tooltip("Seconds each note lives before it disappears.")]
    public float lifetime = 1.6f;

    [Header("Look (sizes are measured in key widths, so they fit any piano scale)")]
    public float size = 1.3f;
    [Tooltip("How far notes float up over their lifetime.")]
    public float riseHeight = 7f;
    [Tooltip("How far notes drift side to side.")]
    public float swayAmount = 0.6f;
    [Tooltip("On: each key always uses the same colour. Off: every note gets a random colour.")]
    public bool oneColourPerKey = true;
    public Color[] colours =
    {
        new Color(1.00f, 0.36f, 0.45f),   // pink
        new Color(1.00f, 0.66f, 0.20f),   // orange
        new Color(1.00f, 0.88f, 0.25f),   // yellow
        new Color(0.35f, 0.85f, 0.45f),   // green
        new Color(0.30f, 0.72f, 1.00f),   // blue
        new Color(0.62f, 0.45f, 1.00f),   // purple
    };

    [Header("Camera the notes face (defaults to Main Camera)")]
    public Camera faceCamera;

    class FloatingNote
    {
        public Transform t;
        public SpriteRenderer r;
        public Vector3 origin, up;
        public float age, scale, swayPhase, swaySpeed, spin;
        public Color colour;
    }

    PianoController piano;
    int lastColour = -1;
    Sprite[] sprites;
    readonly List<FloatingNote> active = new List<FloatingNote>();
    readonly Stack<FloatingNote> pool = new Stack<FloatingNote>();
    readonly List<int> held = new List<int>();
    readonly Dictionary<int, float> nextSpawn = new Dictionary<int, float>();
    readonly List<int> released = new List<int>();

    void Awake()
    {
        piano = GetComponent<PianoController>();
        sprites = new[] { MakeNoteSprite(false), MakeNoteSprite(true) };
    }

    void Update()
    {
        SpawnForHeldKeys();
        AnimateNotes();
    }

    void SpawnForHeldKeys()
    {
        piano.GetHeldNotes(held);

        foreach (int note in held)
        {
            if (!nextSpawn.TryGetValue(note, out float due) || Time.time >= due)
            {
                Spawn(note);
                nextSpawn[note] = Time.time + spawnInterval;
            }
        }

        // Forget keys that were let go, so the next press spawns a note right away.
        released.Clear();
        foreach (int note in nextSpawn.Keys) if (!held.Contains(note)) released.Add(note);
        foreach (int note in released) nextSpawn.Remove(note);
    }

    void Spawn(int midiNote)
    {
        if (!piano.TryGetKeyTop(midiNote, out Vector3 top, out float keyWidth, out Vector3 up)) return;

        FloatingNote n = pool.Count > 0 ? pool.Pop() : CreateNote();
        n.t.gameObject.SetActive(true);
        n.r.sprite = sprites[Random.Range(0, sprites.Length)];
        n.colour = oneColourPerKey ? ColourForKey(midiNote) : NextColour();
        n.up = up;
        n.scale = keyWidth * size * Random.Range(0.85f, 1.15f);
        n.origin = top + up * n.scale * 0.6f;
        n.age = 0f;
        n.swayPhase = Random.Range(0f, Mathf.PI * 2f);
        n.swaySpeed = Random.Range(2.5f, 4f);
        n.spin = Random.Range(-15f, 15f);
        n.t.localScale = Vector3.zero;
        active.Add(n);
    }

    // Cycles through the list by note, so a key always gets the same colour
    // and neighbouring keys always get different ones.
    Color ColourForKey(int midiNote)
    {
        if (colours == null || colours.Length == 0) return Color.white;
        return colours[((midiNote % colours.Length) + colours.Length) % colours.Length];
    }

    // Random colour from the list, never the same one twice in a row.
    Color NextColour()
    {
        if (colours == null || colours.Length == 0) return Color.white;
        if (colours.Length == 1) return colours[0];
        int i = Random.Range(0, colours.Length - 1);
        if (i >= lastColour && lastColour >= 0) i++;
        lastColour = i;
        return colours[i];
    }

    FloatingNote CreateNote()
    {
        // Not parented to the piano, so the piano's own scale doesn't change the note size.
        var go = new GameObject("MusicNote") { hideFlags = HideFlags.DontSave };
        var r = go.AddComponent<SpriteRenderer>();
        r.sortingOrder = 100;
        return new FloatingNote { t = go.transform, r = r };
    }

    void AnimateNotes()
    {
        Camera cam = faceCamera != null ? faceCamera : Camera.main;

        for (int i = active.Count - 1; i >= 0; i--)
        {
            FloatingNote n = active[i];
            n.age += Time.deltaTime;
            float t = n.age / lifetime;

            if (t >= 1f)
            {
                n.t.gameObject.SetActive(false);
                active.RemoveAt(i);
                pool.Push(n);
                continue;
            }

            float keyWidth = n.scale / size;
            float rise = (1f - (1f - t) * (1f - t)) * riseHeight * keyWidth;    // fast start, gentle finish
            Vector3 side = cam != null ? cam.transform.right : Vector3.right;
            float sway = Mathf.Sin(n.swayPhase + n.age * n.swaySpeed) * swayAmount * keyWidth * t;

            n.t.position = n.origin + n.up * rise + side * sway;

            // Face the camera with a little wobble.
            Quaternion facing = cam != null ? cam.transform.rotation : Quaternion.identity;
            n.t.rotation = facing * Quaternion.Euler(0f, 0f, n.spin + Mathf.Sin(n.age * n.swaySpeed) * 12f);

            // Pop in quickly, then fade out over the last 40% of the lifetime.
            float pop = Mathf.Clamp01(n.age / 0.12f);
            float popScale = pop < 1f ? Mathf.Sin(pop * Mathf.PI * 0.5f) * 1.15f : 1f;
            n.t.localScale = Vector3.one * n.scale * popScale;

            Color c = n.colour;
            c.a = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
            n.r.color = c;
        }
    }

    void OnDisable()
    {
        foreach (FloatingNote n in active) { n.t.gameObject.SetActive(false); pool.Push(n); }
        active.Clear();
        nextSpawn.Clear();
    }

    void OnDestroy()
    {
        foreach (FloatingNote n in active) if (n.t != null) Destroy(n.t.gameObject);
        foreach (FloatingNote n in pool) if (n.t != null) Destroy(n.t.gameObject);
    }

    // ---------- Drawing the note images ----------

    static Sprite MakeNoteSprite(bool beamed)
    {
        const int S = 128;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = beamed ? "BeamedNotes" : "EighthNote"
        };
        var px = new Color32[S * S];

        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float d = beamed ? BeamedShape(p) : EighthShape(p);
            byte a = (byte)(Mathf.Clamp01(0.5f - d) * 255f);   // soft 1px edge
            px[y * S + x] = new Color32(255, 255, 255, a);
        }

        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
    }

    static float EighthShape(Vector2 p)
    {
        float d = Ellipse(p, new Vector2(42, 30), 21, 14, 25);
        d = Mathf.Min(d, Box(p, new Vector2(58, 32), new Vector2(66, 112)));

        // Curved flag along a quadratic curve.
        Vector2 p0 = new Vector2(64, 112), p1 = new Vector2(102, 96), p2 = new Vector2(90, 56);
        Vector2 prev = p0;
        for (int i = 1; i <= 10; i++)
        {
            float t = i / 10f;
            Vector2 pt = (1 - t) * (1 - t) * p0 + 2 * (1 - t) * t * p1 + t * t * p2;
            d = Mathf.Min(d, Capsule(p, prev, pt, 6f - 3f * t));
            prev = pt;
        }
        return d;
    }

    static float BeamedShape(Vector2 p)
    {
        float d = Ellipse(p, new Vector2(30, 28), 18, 12, 25);
        d = Mathf.Min(d, Ellipse(p, new Vector2(92, 42), 18, 12, 25));
        d = Mathf.Min(d, Box(p, new Vector2(42, 30), new Vector2(48, 104)));
        d = Mathf.Min(d, Box(p, new Vector2(104, 44), new Vector2(110, 114)));
        d = Mathf.Min(d, Capsule(p, new Vector2(45, 103), new Vector2(107, 113), 7f));
        return d;
    }

    // Approximate signed distances in pixels (negative inside).
    static float Ellipse(Vector2 p, Vector2 c, float rx, float ry, float angleDeg)
    {
        float a = -angleDeg * Mathf.Deg2Rad;
        Vector2 q = p - c;
        float qx = q.x * Mathf.Cos(a) - q.y * Mathf.Sin(a);
        float qy = q.x * Mathf.Sin(a) + q.y * Mathf.Cos(a);
        float k = Mathf.Sqrt((qx / rx) * (qx / rx) + (qy / ry) * (qy / ry));
        return (k - 1f) * Mathf.Min(rx, ry);
    }

    static float Box(Vector2 p, Vector2 lo, Vector2 hi)
    {
        Vector2 d = Vector2.Max(lo - p, p - hi);
        return Vector2.Max(d, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(d.x, d.y), 0f);
    }

    static float Capsule(Vector2 p, Vector2 a, Vector2 b, float r)
    {
        Vector2 pa = p - a, ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
        return (pa - ba * h).magnitude - r;
    }
}