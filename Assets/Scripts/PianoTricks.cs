using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Put on the same object as PianoController.
///   Enter        Twirl: spins around like a turntable
///   Up / Down    Log roll: flips end over end, away from or toward you
///   Right / Left Clock spin: spins flat in front of the camera, clockwise or counterclockwise
/// Each trick hops, trails sparkles, whooshes, plays the keys, and lands with a bounce,
/// a sparkle burst and a chime. All sounds and images are generated in code.
/// </summary>
[RequireComponent(typeof(PianoController))]
public class PianoTricks : MonoBehaviour
{
    enum Trick { Twirl, LogRoll, ClockSpin }

    [Header("Controls")]
    public bool enterTwirls = true;
    public bool arrowKeysDoTricks = true;

    [Header("Motion")]
    [Tooltip("Seconds each trick takes.")]
    public float duration = 1.1f;
    [Tooltip("How high the piano hops, relative to the height it needs to clear the floor.")]
    public float hopScale = 1.15f;
    [Tooltip("How much the piano squashes when it lands.")]
    [Range(0f, 0.4f)] public float landingSquash = 0.12f;
    [Tooltip("Camera used for the clock spin and for facing sparkles (defaults to Main Camera).")]
    public Camera viewCamera;

    [Header("Sound")]
    [Range(0f, 1f)] public float whooshVolume = 0.6f;
    [Range(0f, 1f)] public float chimeVolume = 0.5f;
    [Tooltip("Play a glissando or arpeggio on the keys during tricks, and a chord on landing.")]
    public bool playKeysDuringTricks = true;

    [Header("Sparkles")]
    public int landingSparkles = 30;
    [Tooltip("Sparkle size as a fraction of the piano's length.")]
    public float sparkleSize = 0.05f;
    public Color[] sparkleColours =
    {
        new Color(1.00f, 0.95f, 0.55f),
        new Color(1.00f, 0.55f, 0.75f),
        new Color(0.55f, 0.85f, 1.00f),
        new Color(0.75f, 0.60f, 1.00f),
        Color.white,
    };

    class Sparkle
    {
        public Transform t;
        public SpriteRenderer r;
        public Vector3 velocity;
        public float age, life, size, spin;
        public Color colour;
    }

    PianoController piano;
    AudioSource sfx;
    AudioClip whooshClip, chimeClip;
    Sprite starSprite;
    bool busy;
    float pianoLength = 1f;
    readonly List<int> notes = new List<int>();
    readonly List<Sparkle> sparkles = new List<Sparkle>();
    readonly Stack<Sparkle> sparklePool = new Stack<Sparkle>();

    void Awake()
    {
        piano = GetComponent<PianoController>();
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        whooshClip = MakeWhoosh();
        chimeClip = MakeChime();
        starSprite = MakeStarSprite();
    }

    void Update()
    {
        if (!busy) ReadInput();
        AnimateSparkles();
    }

    // ---------- Input ----------

    void ReadInput()
    {
        bool enter = false, up = false, down = false, left = false, right = false;
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        enter = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
        up = kb.upArrowKey.wasPressedThisFrame;
        down = kb.downArrowKey.wasPressedThisFrame;
        left = kb.leftArrowKey.wasPressedThisFrame;
        right = kb.rightArrowKey.wasPressedThisFrame;
#else
        enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        up = Input.GetKeyDown(KeyCode.UpArrow);
        down = Input.GetKeyDown(KeyCode.DownArrow);
        left = Input.GetKeyDown(KeyCode.LeftArrow);
        right = Input.GetKeyDown(KeyCode.RightArrow);
#endif
        if (enterTwirls && enter) StartCoroutine(DoTrick(Trick.Twirl, 1));
        else if (arrowKeysDoTricks && up) StartCoroutine(DoTrick(Trick.LogRoll, 1));
        else if (arrowKeysDoTricks && down) StartCoroutine(DoTrick(Trick.LogRoll, -1));
        else if (arrowKeysDoTricks && right) StartCoroutine(DoTrick(Trick.ClockSpin, 1));
        else if (arrowKeysDoTricks && left) StartCoroutine(DoTrick(Trick.ClockSpin, -1));
    }

    /// <summary>Call from buttons, events or other scripts. 0 = twirl, 1 = log roll, 2 = clock spin.</summary>
    public void PlayTrick(int trick)
    {
        if (!busy) StartCoroutine(DoTrick((Trick)Mathf.Clamp(trick, 0, 2), 1));
    }

    // ---------- The trick ----------

    IEnumerator DoTrick(Trick trick, int direction)
    {
        if (!TryGetPianoBounds(out Bounds b)) yield break;
        busy = true;

        Camera cam = viewCamera != null ? viewCamera : Camera.main;
        Vector3 restPos = transform.position;
        Quaternion restRot = transform.rotation;
        Vector3 center = b.center;

        Vector3 longAxis = piano.WorldAxis(piano.lowToHighAxis);
        float halfLength = Extent(b, longAxis);
        pianoLength = Mathf.Max(0.01f, halfLength * 2f);

        Vector3 axis;
        float pitch;
        switch (trick)
        {
            case Trick.Twirl: axis = Vector3.up; pitch = 1.1f; break;
            case Trick.LogRoll: axis = longAxis; pitch = 0.8f; break;
            // Axis points at the viewer, so a positive angle looks clockwise from the camera.
            default: axis = cam != null ? -cam.transform.forward : Vector3.back; pitch = 1.3f; break;
        }

        // Hop high enough that the spinning piano clears the floor.
        float along = Extent(b, axis);
        float radius = Mathf.Sqrt(Mathf.Max(0f, b.extents.sqrMagnitude - along * along));
        float below = Extent(b, Vector3.up);
        float hop = trick == Trick.Twirl
            ? radius * 0.25f
            : Mathf.Max(0f, radius - below) * hopScale + radius * 0.15f;

        // The two ends of the keyboard, for the sparkle trail.
        Vector3 endA = transform.InverseTransformPoint(center - longAxis * halfLength);
        Vector3 endB = transform.InverseTransformPoint(center + longAxis * halfLength);

        sfx.pitch = pitch * (1f / duration);           // whoosh clip is 1 second long
        sfx.PlayOneShot(whooshClip, whooshVolume);
        if (playKeysDuringTricks) StartCoroutine(PlayKeysDuring(trick, direction));

        float start = Time.time, nextTrail = 0f;
        while (true)
        {
            float t = (Time.time - start) / duration;
            if (t >= 1f) break;

            transform.SetPositionAndRotation(restPos, restRot);
            transform.RotateAround(center, axis, 360f * direction * EaseInOutBack(t));
            transform.position += Vector3.up * hop * Mathf.Sin(Mathf.PI * t);

            if (Time.time >= nextTrail)
            {
                SpawnSparkle(transform.TransformPoint(endA), Random.insideUnitSphere * pianoLength * 0.08f, 0.5f, 0.7f);
                SpawnSparkle(transform.TransformPoint(endB), Random.insideUnitSphere * pianoLength * 0.08f, 0.5f, 0.7f);
                nextTrail = Time.time + 0.035f;
            }
            yield return null;
        }

        transform.SetPositionAndRotation(restPos, restRot);
        Land(center, cam);
        yield return StartCoroutine(SquashBounce());
        busy = false;
    }

    void Land(Vector3 center, Camera cam)
    {
        sfx.pitch = 1f;
        sfx.PlayOneShot(chimeClip, chimeVolume);
        if (playKeysDuringTricks) StartCoroutine(PlayLandingChord());

        Vector3 right = cam != null ? cam.transform.right : Vector3.right;
        Vector3 up = cam != null ? cam.transform.up : Vector3.up;
        Vector3 toward = cam != null ? -cam.transform.forward : Vector3.back;
        for (int i = 0; i < landingSparkles; i++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            Vector3 dir = (right * Mathf.Cos(a) + up * Mathf.Sin(a) * 0.7f + toward * Random.Range(0f, 0.4f)).normalized;
            SpawnSparkle(center, dir * pianoLength * Random.Range(0.6f, 1.4f), Random.Range(0.6f, 1f), 1f);
        }
    }

    IEnumerator SquashBounce()
    {
        Vector3 restScale = transform.localScale;
        const float time = 0.4f;
        for (float u = 0f; u < time; u += Time.deltaTime)
        {
            float k = u / time;
            float f = 1f - landingSquash * Mathf.Exp(-5f * k) * Mathf.Cos(k * 14f);
            float side = 1f / Mathf.Sqrt(Mathf.Max(0.01f, f));   // keep the volume roughly the same
            transform.localScale = Vector3.Scale(restScale, new Vector3(side, f, side));
            yield return null;
        }
        transform.localScale = restScale;
    }

    // ---------- Keys playing themselves ----------

    IEnumerator PlayKeysDuring(Trick trick, int direction)
    {
        if (trick == Trick.Twirl) yield break;   // the twirl just lands on a chord

        if (trick == Trick.LogRoll)
        {
            // Glissando across the white keys, up for rolling away, down for rolling back.
            piano.GetPlayableNotes(notes, true);
            if (direction < 0) notes.Reverse();
        }
        else
        {
            // Major arpeggio up and back down across the keyboard.
            piano.GetPlayableNotes(notes, false);
            notes.RemoveAll(n => { int pc = n % 12; return pc != 0 && pc != 4 && pc != 7; });
            int count = notes.Count;
            for (int i = count - 2; i >= 0; i--) notes.Add(notes[i]);
            if (direction < 0) notes.Reverse();
        }
        if (notes.Count == 0) yield break;

        var sequence = new List<int>(notes);
        float step = duration * 0.85f / sequence.Count;
        foreach (int n in sequence)
        {
            StartCoroutine(TapNote(n, step * 1.5f));
            yield return new WaitForSeconds(step);
        }
    }

    IEnumerator PlayLandingChord()
    {
        piano.GetPlayableNotes(notes, false);
        var available = new HashSet<int>(notes);

        // Find the C major chord closest to middle C that fits on the visible keys.
        int best = -1;
        foreach (int n in notes)
            if (n % 12 == 0 && available.Contains(n + 4) && available.Contains(n + 7) && available.Contains(n + 12))
                if (best < 0 || Mathf.Abs(n - 60) < Mathf.Abs(best - 60)) best = n;
        if (best < 0) yield break;

        foreach (int n in new[] { best, best + 4, best + 7, best + 12 }) StartCoroutine(TapNote(n, 0.6f));
    }

    IEnumerator TapNote(int note, float hold)
    {
        piano.PressNote(note);
        yield return new WaitForSeconds(hold);
        piano.ReleaseNote(note);
    }

    // ---------- Sparkles ----------

    void SpawnSparkle(Vector3 position, Vector3 velocity, float life, float sizeScale)
    {
        Sparkle s = sparklePool.Count > 0 ? sparklePool.Pop() : CreateSparkle();
        s.t.gameObject.SetActive(true);
        s.t.position = position;
        s.velocity = velocity;
        s.age = 0f;
        s.life = life;
        s.size = pianoLength * sparkleSize * sizeScale * Random.Range(0.7f, 1.3f);
        s.spin = Random.Range(-360f, 360f);
        s.colour = sparkleColours.Length > 0 ? sparkleColours[Random.Range(0, sparkleColours.Length)] : Color.white;
        sparkles.Add(s);
    }

    Sparkle CreateSparkle()
    {
        var go = new GameObject("Sparkle") { hideFlags = HideFlags.DontSave };
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = starSprite;
        r.sortingOrder = 101;
        return new Sparkle { t = go.transform, r = r };
    }

    void AnimateSparkles()
    {
        Camera cam = viewCamera != null ? viewCamera : Camera.main;
        Quaternion facing = cam != null ? cam.transform.rotation : Quaternion.identity;
        float dt = Time.deltaTime;

        for (int i = sparkles.Count - 1; i >= 0; i--)
        {
            Sparkle s = sparkles[i];
            s.age += dt;
            float k = s.age / s.life;
            if (k >= 1f)
            {
                s.t.gameObject.SetActive(false);
                sparkles.RemoveAt(i);
                sparklePool.Push(s);
                continue;
            }

            s.velocity *= 1f - 2.5f * dt;                          // air drag
            s.velocity += Vector3.down * pianoLength * 0.6f * dt;   // gentle fall
            s.t.position += s.velocity * dt;
            s.t.rotation = facing * Quaternion.Euler(0f, 0f, s.spin * s.age);

            float twinkle = 0.8f + 0.2f * Mathf.Sin(s.age * 40f);
            s.t.localScale = Vector3.one * s.size * Mathf.Sqrt(1f - k) * twinkle;
            Color c = s.colour;
            c.a = 1f - k * k;
            s.r.color = c;
        }
    }

    void OnDestroy()
    {
        foreach (Sparkle s in sparkles) if (s.t != null) Destroy(s.t.gameObject);
        foreach (Sparkle s in sparklePool) if (s.t != null) Destroy(s.t.gameObject);
    }

    // ---------- Helpers ----------

    bool TryGetPianoBounds(out Bounds bounds)
    {
        bounds = new Bounds();
        bool found = false;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!found) { bounds = r.bounds; found = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return found;
    }

    static float Extent(Bounds b, Vector3 dir)
    {
        return Mathf.Abs(dir.x) * b.extents.x + Mathf.Abs(dir.y) * b.extents.y + Mathf.Abs(dir.z) * b.extents.z;
    }

    // Winds up slightly, then overshoots slightly before settling.
    static float EaseInOutBack(float t)
    {
        const float c2 = 1.2f * 1.525f;
        return t < 0.5f
            ? (Mathf.Pow(2f * t, 2f) * ((c2 + 1f) * 2f * t - c2)) / 2f
            : (Mathf.Pow(2f * t - 2f, 2f) * ((c2 + 1f) * (2f * t - 2f) + c2) + 2f) / 2f;
    }

    // ---------- Generated sounds and images ----------

    static AudioClip MakeWhoosh()
    {
        const int sr = 44100;
        int n = sr;   // 1 second
        var data = new float[n];
        var rng = new System.Random(7);
        float lp1 = 0f, lp2 = 0f, peak = 0.0001f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float swell = Mathf.Sin(Mathf.PI * t);
            float cutoff = Mathf.Lerp(250f, 2800f, swell);              // brightens as it speeds up
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / sr);
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lp1 += a * (noise - lp1);
            lp2 += a * (lp1 - lp2);
            data[i] = lp2 * Mathf.Pow(swell, 1.5f);
            peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.9f / peak;

        var clip = AudioClip.Create("Whoosh", n, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    static AudioClip MakeChime()
    {
        const int sr = 44100;
        int n = (int)(sr * 1.2f);
        var data = new float[n];
        float[] freqs = { 1568f, 2093f, 2637f, 3136f };   // G6, C7, E7, G7
        float peak = 0.0001f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float s = 0f;
            for (int j = 0; j < freqs.Length; j++)
            {
                float start = j * 0.06f;
                if (t < start) continue;
                float lt = t - start;
                float env = Mathf.Exp(-lt * 5f) * Mathf.Clamp01(lt / 0.003f);
                s += env * (Mathf.Sin(2f * Mathf.PI * freqs[j] * lt) + 0.3f * Mathf.Sin(2f * Mathf.PI * freqs[j] * 2.76f * lt));
            }
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.8f / peak;

        var clip = AudioClip.Create("Chime", n, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    static Sprite MakeStarSprite()
    {
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "Sparkle"
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
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
    }
}