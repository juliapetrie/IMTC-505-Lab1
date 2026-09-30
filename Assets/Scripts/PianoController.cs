using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Drop on the piano root. Splits the keyboard mesh into separate keys if needed, sorts them
/// low to high, assigns MIDI notes, animates keys when pressed, and plays a pitched sample or
/// a generated piano-like tone. Play with the mouse or the computer keyboard.
/// </summary>
public class PianoController : MonoBehaviour
{
    [Header("Keys")]
    [Tooltip("Object whose children are the keys. Defaults to this object.")]
    public Transform keysParent;
    [Tooltip("MIDI note of the leftmost key. -1 = automatic (centered on middle C). 48 = C3, 60 = middle C.")]
    public int firstNote = -1;

    [Header("Piano axes (in keysParent local space)")]
    [Tooltip("Direction from low notes to high notes.")]
    public Vector3 lowToHighAxis = Vector3.right;
    [Tooltip("Direction from the player toward the back of the piano.")]
    public Vector3 towardBackAxis = Vector3.forward;
    public Vector3 upAxis = Vector3.up;

    [Header("Key motion")]
    [Tooltip("Degrees the key tips down. Make it negative if keys rotate the wrong way.")]
    public float pressAngle = 4f;
    public float pressTime = 0.04f;
    public float releaseTime = 0.08f;

    [Header("Sound")]
    [Tooltip("Optional recorded piano note. If empty, a piano-like tone is generated for each note.")]
    public AudioClip sample;
    [Tooltip("MIDI note of the sample above (60 = middle C).")]
    public int sampleNote = 60;
    [Range(0f, 1f)] public float volume = 0.8f;
    [Tooltip("0 = 2D sound, 1 = fully 3D from the key.")]
    [Range(0f, 1f)] public float spatialBlend = 0f;
    [Tooltip("How fast a note fades after the key is released (no sustain).")]
    public float noteReleaseFade = 0.25f;

    [Header("Input")]
    public bool mouseInput = true;
    public bool computerKeyboardInput = true;
    [Tooltip("MIDI note of the first white key in whiteKeyRow. 60 = middle C. The lower rows start one octave below.")]
    public int keyboardBaseNote = 60;
    [Tooltip("Computer keys for the white piano keys of the upper octave, left to right.")]
    public string whiteKeyRow = "qwertyuiop";
    [Tooltip("Computer keys for the upper black keys. Each character sits above the gap to the right of the white key " +
             "at the same position in whiteKeyRow. Use a space where there is no black key (E-F and B-C).")]
    public string blackKeyRow = "23 567 90";
    [Tooltip("Computer keys for the white piano keys of the lower octave. Leave empty to skip.")]
    public string lowerWhiteKeyRow = "zxcvbnm";
    [Tooltip("Computer keys for the lower black keys, laid out the same way as blackKeyRow.")]
    public string lowerBlackKeyRow = "sd ghj";
    [Tooltip("Hide piano keys that have no computer key assigned.")]
    public bool hideUnmappedKeys = true;
    [Tooltip("Hold Space to sustain.")]
    public bool spaceIsSustainPedal = true;

    class Key
    {
        public Transform t;
        public int note;
        public bool black;
        public Vector3 restPos;
        public Quaternion restRot;
        public Vector3 pivotLocal;
        public Vector3 topLocal;
        public float length, width;
        public float press;
        public bool applied;
        public int holdCount;
        public bool sounding;
        public AudioSource src;
        public Coroutine fade;
    }

    readonly List<Key> keys = new List<Key>();
    readonly Dictionary<Transform, Key> byTransform = new Dictionary<Transform, Key>();
    readonly Dictionary<int, Key> byNote = new Dictionary<int, Key>();
    static readonly Dictionary<int, AudioClip> toneCache = new Dictionary<int, AudioClip>();
    static readonly bool[] blackPitchClass = { false, true, false, true, false, false, true, false, true, false, true, false };

    readonly List<char> mappedChars = new List<char>();
    readonly List<int> mappedNotes = new List<int>();
    bool[] computerKeyHeld;
    Key mouseKey;
    bool sustain;

    void Start()
    {
        if (keysParent == null) keysParent = transform;
        TrySplitSingleMesh();
        Vector3 lh = lowToHighAxis.normalized, back = towardBackAxis.normalized, up = upAxis.normalized;

        // Collect keys and measure them in keysParent space.
        var tops = new Dictionary<Key, float>();
        var order = new Dictionary<Key, float>();
        foreach (Transform child in keysParent)
        {
            if (!TryLocalBounds(child, out Bounds b)) continue;
            var k = new Key
            {
                t = child,
                restPos = child.localPosition,
                restRot = child.localRotation,
                pivotLocal = b.center + back * Extent(b, back),
                topLocal = b.center + up * Extent(b, up),
                length = 2f * Extent(b, back),
                width = 2f * Extent(b, lh)
            };
            keys.Add(k);
            tops[k] = Vector3.Dot(b.center, up) + Extent(b, up);
            order[k] = Vector3.Dot(b.center, lh);
        }

        if (keys.Count == 0)
        {
            Debug.LogWarning("PianoController: no keys found. Each key needs to be its own child object with a mesh.");
            enabled = false;
            return;
        }

        keys.Sort((a, c) => order[a].CompareTo(order[c]));
        ClassifyBlackKeys(tops);
        if (firstNote < 0) firstNote = 60 - keys.Count / 2;
        DetectFirstNote();

        for (int i = 0; i < keys.Count; i++)
        {
            Key k = keys[i];
            k.note = firstNote + i;
            byTransform[k.t] = k;
            byNote[k.note] = k;

            if (k.t.GetComponentInChildren<Collider>() == null) k.t.gameObject.AddComponent<BoxCollider>();
            k.src = k.t.gameObject.AddComponent<AudioSource>();
            k.src.playOnAwake = false;
            k.src.spatialBlend = spatialBlend;
        }

        BuildComputerKeyMap();
        if (hideUnmappedKeys && computerKeyboardInput) HideUnmappedKeys();
    }

    void Update()
    {
        if (mouseInput) HandleMouse();
        if (computerKeyboardInput) HandleComputerKeyboard();
        AnimateKeys();
    }

    // ---------- Public API (used by MusicNotes, PianoTricks, and anything else that plays the piano) ----------

    public void PressNote(int midiNote) { if (byNote.TryGetValue(midiNote, out Key k)) Press(k); }
    public void ReleaseNote(int midiNote) { if (byNote.TryGetValue(midiNote, out Key k)) Release(k); }

    /// <summary>Fills results with the MIDI notes of all keys currently held down.</summary>
    public void GetHeldNotes(List<int> results)
    {
        results.Clear();
        foreach (Key k in keys) if (k.holdCount > 0) results.Add(k.note);
    }

    /// <summary>Fills results with the MIDI notes of all visible keys, low to high.</summary>
    public void GetPlayableNotes(List<int> results, bool whiteKeysOnly)
    {
        results.Clear();
        foreach (Key k in keys) if (!whiteKeysOnly || !k.black) results.Add(k.note);
    }

    /// <summary>World position of the top of a key at rest, plus the key's width and the piano's up direction.</summary>
    public bool TryGetKeyTop(int midiNote, out Vector3 worldTop, out float worldWidth, out Vector3 worldUp)
    {
        worldTop = Vector3.zero; worldWidth = 0f; worldUp = Vector3.up;
        if (!byNote.TryGetValue(midiNote, out Key k)) return false;
        worldTop = keysParent.TransformPoint(k.topLocal);
        worldWidth = keysParent.TransformVector(lowToHighAxis.normalized * k.width).magnitude;
        worldUp = WorldAxis(upAxis);
        return true;
    }

    /// <summary>Converts one of the piano axes above into a world-space direction.</summary>
    public Vector3 WorldAxis(Vector3 localAxis)
    {
        Transform root = keysParent != null ? keysParent : transform;
        return root.TransformDirection(localAxis).normalized;
    }

    // ---------- Input ----------

    void HandleMouse()
    {
        bool held;
        Vector2 pos;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null) return;
        held = Mouse.current.leftButton.isPressed;
        pos = Mouse.current.position.ReadValue();
#else
        held = Input.GetMouseButton(0);
        pos = Input.mousePosition;
#endif
        // Dragging across keys plays each one (glissando).
        Key hovered = held ? RaycastKey(pos) : null;
        if (hovered == mouseKey) return;
        if (mouseKey != null) Release(mouseKey);
        if (hovered != null) Press(hovered);
        mouseKey = hovered;
    }

    Key RaycastKey(Vector2 screenPos)
    {
        Camera cam = Camera.main;
        if (cam == null || !Physics.Raycast(cam.ScreenPointToRay(screenPos), out RaycastHit hit)) return null;
        for (Transform t = hit.collider.transform; t != null; t = t.parent)
            if (byTransform.TryGetValue(t, out Key k)) return k;
        return null;
    }

    void HandleComputerKeyboard()
    {
        for (int i = 0; i < mappedChars.Count; i++)
        {
            bool down = IsComputerKeyDown(mappedChars[i]);
            if (down == computerKeyHeld[i]) continue;
            computerKeyHeld[i] = down;
            if (down) PressNote(mappedNotes[i]);
            else ReleaseNote(mappedNotes[i]);
        }

        if (!spaceIsSustainPedal) return;
#if ENABLE_INPUT_SYSTEM
        bool space = Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
#else
        bool space = Input.GetKey(KeyCode.Space);
#endif
        if (space != sustain) SetSustain(space);
    }

    bool IsComputerKeyDown(char c)
    {
#if ENABLE_INPUT_SYSTEM
        var control = Keyboard.current?.FindKeyOnCurrentKeyboardLayout(c.ToString());
        return control != null && control.isPressed;
#else
        return Input.GetKey((KeyCode)c);
#endif
    }

    void BuildComputerKeyMap()
    {
        mappedChars.Clear();
        mappedNotes.Clear();
        AddKeyRows(lowerWhiteKeyRow, lowerBlackKeyRow, keyboardBaseNote - 12);
        AddKeyRows(whiteKeyRow, blackKeyRow, keyboardBaseNote);
        computerKeyHeld = new bool[mappedChars.Count];
    }

    void AddKeyRows(string whiteRow, string blackRow, int note)
    {
        if (string.IsNullOrEmpty(whiteRow)) return;
        blackRow = blackRow ?? "";

        for (int i = 0; i < whiteRow.Length; i++)
        {
            AddMapping(whiteRow[i], note);
            bool hasBlack = blackPitchClass[(note + 1) % 12];
            char blackChar = i < blackRow.Length ? blackRow[i] : ' ';
            if (hasBlack && blackChar != ' ') AddMapping(blackChar, note + 1);
            note += hasBlack ? 2 : 1;   // step to the next white key
        }
    }

    void AddMapping(char c, int note)
    {
        c = char.ToLower(c);
        if (mappedChars.Contains(c))
        {
            Debug.LogWarning($"PianoController: '{c}' is used in more than one key row; only its first use counts.");
            return;
        }
        mappedChars.Add(c);
        mappedNotes.Add(note);
    }

    void HideUnmappedKeys()
    {
        var playable = new HashSet<int>(mappedNotes);
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            Key k = keys[i];
            if (playable.Contains(k.note)) continue;
            k.t.gameObject.SetActive(false);
            byTransform.Remove(k.t);
            byNote.Remove(k.note);
            keys.RemoveAt(i);
        }
    }

    // ---------- Press / release ----------

    void Press(Key k)
    {
        if (++k.holdCount == 1) PlaySound(k);
    }

    void Release(Key k)
    {
        if (k.holdCount == 0) return;
        if (--k.holdCount == 0 && !sustain) StopSound(k);
    }

    void SetSustain(bool on)
    {
        sustain = on;
        if (on) return;
        foreach (Key k in keys)
            if (k.holdCount == 0 && k.sounding) StopSound(k);
    }

    // ---------- Animation ----------

    void AnimateKeys()
    {
        Vector3 axis = keysParent.TransformDirection(Vector3.Cross(towardBackAxis.normalized, upAxis.normalized));

        foreach (Key k in keys)
        {
            float target = k.holdCount > 0 ? 1f : 0f;
            float speed = 1f / Mathf.Max(0.001f, target > k.press ? pressTime : releaseTime);
            k.press = Mathf.MoveTowards(k.press, target, speed * Time.deltaTime);
            if (k.press <= 0f && !k.applied) continue;

            // Hinge the key down around its back edge.
            k.t.localPosition = k.restPos;
            k.t.localRotation = k.restRot;
            if (k.press > 0f) k.t.RotateAround(keysParent.TransformPoint(k.pivotLocal), axis, pressAngle * k.press);
            k.applied = k.press > 0f;
        }
    }

    // ---------- Sound ----------

    void PlaySound(Key k)
    {
        if (k.fade != null) { StopCoroutine(k.fade); k.fade = null; }
        k.src.clip = sample != null ? sample : GetTone(k.note);
        k.src.pitch = sample != null ? Mathf.Pow(2f, (k.note - sampleNote) / 12f) : 1f;
        k.src.volume = volume;
        k.src.Play();
        k.sounding = true;
    }

    void StopSound(Key k)
    {
        if (!k.sounding) return;
        if (k.fade != null) StopCoroutine(k.fade);
        k.fade = StartCoroutine(FadeOut(k));
    }

    IEnumerator FadeOut(Key k)
    {
        float start = k.src.volume;
        for (float t = 0f; t < noteReleaseFade; t += Time.deltaTime)
        {
            k.src.volume = Mathf.Lerp(start, 0f, t / noteReleaseFade);
            yield return null;
        }
        k.src.Stop();
        k.sounding = false;
        k.fade = null;
    }

    static AudioClip GetTone(int note)
    {
        // Null check: cached clips are destroyed between Play sessions when domain reload is off.
        if (toneCache.TryGetValue(note, out AudioClip clip) && clip != null) return clip;

        const int sr = 44100;
        int n = sr * 3;
        float f = 440f * Mathf.Pow(2f, (note - 69) / 12f);
        float decay = 1.2f + f / 500f;           // higher notes die away faster
        var data = new float[n];
        float peak = 0.0001f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / sr;
            float s = 0f;
            for (int h = 1; h <= 8; h++)
            {
                float fh = f * h * (1f + 0.0004f * h * h);   // slight string inharmonicity
                if (fh > sr * 0.45f) break;
                s += Mathf.Exp(-t * decay * (1f + 0.35f * h)) * Mathf.Sin(2f * Mathf.PI * fh * t) / Mathf.Pow(h, 1.4f);
            }
            data[i] = s * Mathf.Clamp01(t / 0.004f);     // short hammer attack
            peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.8f / peak;

        clip = AudioClip.Create("PianoTone_" + note, n, 1, sr, false);
        clip.SetData(data, 0);
        toneCache[note] = clip;
        return clip;
    }

    // ---------- Mesh splitting (the model's keys are all one mesh) ----------

    void TrySplitSingleMesh()
    {
        MeshFilter[] filters = keysParent.GetComponentsInChildren<MeshFilter>();
        if (filters.Length != 1) return;               // keys are already separate objects

        MeshFilter source = filters[0];
        Mesh mesh = source.sharedMesh;
        if (mesh == null) return;
        if (!mesh.isReadable)
        {
            Debug.LogWarning("PianoController: turn on Read/Write in the model's import settings so the keys can be split.");
            return;
        }

        var sourceRenderer = source.GetComponent<MeshRenderer>();
        Material[] mats = sourceRenderer != null ? sourceRenderer.sharedMaterials : new Material[0];
        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        int subCount = mesh.subMeshCount;

        // Group triangles into connected pieces. Vertices at the same position are welded across
        // materials, because hard edges duplicate vertices and one key can use both materials.
        int[] parent = new int[verts.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        float cell = Mathf.Max(1e-7f, mesh.bounds.size.magnitude * 1e-5f);
        var firstAt = new Dictionary<Vector3Int, int>();

        for (int s = 0; s < subCount; s++)
        {
            int[] tris = mesh.GetTriangles(s);
            foreach (int v in tris)
            {
                Vector3Int q = Vector3Int.RoundToInt(verts[v] / cell);
                if (firstAt.TryGetValue(q, out int other)) Union(parent, v, other);
                else firstAt[q] = v;
            }
            for (int i = 0; i < tris.Length; i += 3)
            {
                Union(parent, tris[i], tris[i + 1]);
                Union(parent, tris[i], tris[i + 2]);
            }
        }

        // Each piece's triangles, per material.
        var parts = new Dictionary<int, List<int>[]>();
        for (int s = 0; s < subCount; s++)
        {
            int[] tris = mesh.GetTriangles(s);
            for (int i = 0; i < tris.Length; i += 3)
            {
                int root = Find(parent, tris[i]);
                if (!parts.TryGetValue(root, out List<int>[] lists))
                {
                    lists = new List<int>[subCount];
                    for (int j = 0; j < subCount; j++) lists[j] = new List<int>();
                    parts[root] = lists;
                }
                lists[s].AddRange(new[] { tris[i], tris[i + 1], tris[i + 2] });
            }
        }
        if (parts.Count < 2) return;

        int n = 0;
        foreach (List<int>[] lists in parts.Values)
        {
            var remap = new Dictionary<int, int>();
            var pv = new List<Vector3>();
            var pn = new List<Vector3>();
            var usedTris = new List<List<int>>();
            var usedMats = new List<Material>();

            for (int s = 0; s < subCount; s++)
            {
                if (lists[s].Count == 0) continue;
                var local = new List<int>(lists[s].Count);
                foreach (int v in lists[s])
                {
                    if (!remap.TryGetValue(v, out int nv))
                    {
                        nv = pv.Count;
                        remap[v] = nv;
                        pv.Add(verts[v]);
                        if (normals.Length == verts.Length) pn.Add(normals[v]);
                    }
                    local.Add(nv);
                }
                usedTris.Add(local);
                usedMats.Add(s < mats.Length ? mats[s] : null);
            }

            // Recenter so each key's pivot sits at its own center.
            var b = new Bounds(pv[0], Vector3.zero);
            foreach (Vector3 p in pv) b.Encapsulate(p);
            for (int i = 0; i < pv.Count; i++) pv[i] -= b.center;

            var m = new Mesh { name = mesh.name + "_key" + n };
            m.SetVertices(pv);
            m.subMeshCount = usedTris.Count;
            for (int j = 0; j < usedTris.Count; j++) m.SetTriangles(usedTris[j], j);
            if (pn.Count == pv.Count) m.SetNormals(pn); else m.RecalculateNormals();
            m.RecalculateBounds();

            var go = new GameObject("Key_" + n++);
            go.transform.SetParent(source.transform, false);
            go.transform.localPosition = b.center;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterials = usedMats.ToArray();
        }

        if (sourceRenderer != null) sourceRenderer.enabled = false;
        foreach (Collider c in source.GetComponents<Collider>()) c.enabled = false;   // don't block clicks on the keys
        keysParent = source.transform;
    }

    static int Find(int[] parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb) parent[ra] = rb;
    }

    // ---------- Setup helpers ----------

    // Black keys stand taller than white keys.
    void ClassifyBlackKeys(Dictionary<Key, float> tops)
    {
        float min = float.MaxValue, max = float.MinValue, avgLen = 0f;
        foreach (Key k in keys) { min = Mathf.Min(min, tops[k]); max = Mathf.Max(max, tops[k]); avgLen += k.length; }
        avgLen /= keys.Count;
        bool heightsDiffer = (max - min) > avgLen * 0.02f;
        float mid = (min + max) * 0.5f;
        foreach (Key k in keys) k.black = heightsDiffer && tops[k] > mid;
    }

    // Shifts firstNote so the black/white pattern of the keys matches a real keyboard.
    void DetectFirstNote()
    {
        if (!keys.Exists(k => k.black)) return;

        int bestPc = 0, bestScore = -1;
        for (int pc = 0; pc < 12; pc++)
        {
            int score = 0;
            for (int i = 0; i < keys.Count; i++)
                if (keys[i].black == blackPitchClass[(pc + i) % 12]) score++;
            if (score > bestScore) { bestScore = score; bestPc = pc; }
        }

        int candidate = firstNote + ((bestPc - firstNote % 12) + 12) % 12;
        if (candidate - firstNote > 6) candidate -= 12;
        firstNote = candidate;
    }

    bool TryLocalBounds(Transform root, out Bounds bounds)
    {
        bounds = new Bounds();
        bool found = false;
        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            Bounds mb = mf.sharedMesh.bounds;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                Vector3 p = keysParent.InverseTransformPoint(mf.transform.TransformPoint(corner));
                if (!found) { bounds = new Bounds(p, Vector3.zero); found = true; }
                else bounds.Encapsulate(p);
            }
        }
        return found;
    }

    static float Extent(Bounds b, Vector3 dir)
    {
        return Mathf.Abs(dir.x) * b.extents.x + Mathf.Abs(dir.y) * b.extents.y + Mathf.Abs(dir.z) * b.extents.z;
    }
}g