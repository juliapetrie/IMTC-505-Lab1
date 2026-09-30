using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Drop on the piano root. Finds each key under keysParent, sorts them low to high,
/// assigns MIDI notes, adds colliders and AudioSources, animates keys down/up,
/// and plays either a pitched sample or a generated piano-like tone.
/// Works with the old Input Manager and the new Input System.
/// </summary>
public class PianoController : MonoBehaviour
{
    public enum PressStyle { Rotate, Translate }

    [Header("Keys")]
    [Tooltip("Object whose direct children are the individual keys. Defaults to this object.")]
    public Transform keysParent;
    [Tooltip("Only children whose name contains this text count as keys (leave empty for all children).")]
    public string keyNameFilter = "";
    [Tooltip("If all the keys are one mesh, split it into one object per key at startup (mesh needs Read/Write enabled).")]
    public bool splitSingleMeshIntoKeys = true;
    [Tooltip("MIDI note of the leftmost key. -1 = automatic (A0 for 88 keys, otherwise centered on middle C). 21 = A0, 48 = C3, 60 = middle C.")]
    public int firstNote = -1;
    [Tooltip("Adjust firstNote using the black/white key pattern (keeps the octave closest to firstNote).")]
    public bool autoDetectFirstNote = true;

    [Header("Piano axes (in keysParent local space)")]
    [Tooltip("Direction from low notes to high notes.")]
    public Vector3 lowToHighAxis = Vector3.right;
    [Tooltip("Direction from the player toward the back of the piano.")]
    public Vector3 towardBackAxis = Vector3.forward;
    public Vector3 upAxis = Vector3.up;

    [Header("Key motion")]
    public PressStyle pressStyle = PressStyle.Rotate;
    [Tooltip("Degrees the key tips down. Make it negative if keys rotate the wrong way.")]
    public float pressAngle = 4f;
    [Tooltip("Translate mode only. 0 = automatic (6% of key length).")]
    public float pressDepth = 0f;
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
    public Camera inputCamera;
    public bool mouseInput = true;
    public bool computerKeyboardInput = true;
    [Tooltip("MIDI note of the first white key in whiteKeyRow. 60 = middle C. Must be a white note. " +
             "The lower rows start one octave below this.")]
    public int keyboardBaseNote = 60;
    [Tooltip("Computer keys for the white piano keys of the upper octave, left to right, starting at keyboardBaseNote.")]
    public string whiteKeyRow = "qwertyuiop";
    [Tooltip("Computer keys for the black piano keys of the upper octave. Each character sits above the gap to the right " +
             "of the white key at the same position in whiteKeyRow. Use a space where there is no black key (E-F and B-C).")]
    public string blackKeyRow = "23 567 90";
    [Tooltip("Computer keys for the white piano keys of the lower octave, starting one octave below keyboardBaseNote. Leave empty to skip.")]
    public string lowerWhiteKeyRow = "zxcvbnm";
    [Tooltip("Computer keys for the black piano keys of the lower octave, laid out the same way as blackKeyRow.")]
    public string lowerBlackKeyRow = "sd ghj";
    [Tooltip("Hide piano keys that have no computer key assigned, so only playable keys are shown.")]
    public bool hideUnmappedKeys = true;
    [Tooltip("Hold Space to sustain.")]
    public bool spaceIsSustainPedal = true;

    [Header("Debug")]
    [Tooltip("Log each key press and anything the mouse hits that isn't a key.")]
    public bool logPresses = true;

    [Header("Events (MIDI note number)")]
    public UnityEvent<int> onNoteDown;
    public UnityEvent<int> onNoteUp;

    class Key
    {
        public Transform t;
        public int note;
        public bool black;
        public Vector3 restPos;
        public Quaternion restRot;
        public Vector3 pivotLocal;
        public float length;
        public Vector3 topLocal;
        public float width;
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

    Key mouseKey;
    Collider lastNonKeyHit;
    readonly List<char> mappedChars = new List<char>();
    readonly List<int> mappedNotes = new List<int>();
    bool[] computerKeyHeld;

    bool sustain;

    void Start()
    {
        if (keysParent == null) keysParent = transform;
        if (splitSingleMeshIntoKeys) TrySplitSingleMesh();
        Vector3 lh = lowToHighAxis.normalized, back = towardBackAxis.normalized, up = upAxis.normalized;

        // Collect keys and measure them in keysParent space.
        var tops = new List<float>();
        var order = new List<float>();
        foreach (Transform child in keysParent)
        {
            if (!string.IsNullOrEmpty(keyNameFilter) &&
                !child.name.ToLower().Contains(keyNameFilter.ToLower())) continue;
            if (!TryLocalBounds(child, out Bounds b)) continue;

            var k = new Key
            {
                t = child,
                restPos = child.localPosition,
                restRot = child.localRotation,
                pivotLocal = b.center + back * Extent(b, back),
                length = 2f * Extent(b, back),
                topLocal = b.center + up * Extent(b, up),
                width = 2f * Extent(b, lh)
            };
            keys.Add(k);
            tops.Add(Vector3.Dot(b.center, up) + Extent(b, up));
            order.Add(Vector3.Dot(b.center, lh));
        }

        if (keys.Count == 0)
        {
            Debug.LogWarning("PianoController: no keys found under " + keysParent.name +
                             ". Each key needs to be its own child object with a mesh.");
            enabled = false;
            return;
        }

        // Sort low to high.
        var idx = new List<int>();
        for (int i = 0; i < keys.Count; i++) idx.Add(i);
        idx.Sort((a, c) => order[a].CompareTo(order[c]));
        var sortedKeys = new List<Key>();
        var sortedTops = new List<float>();
        foreach (int i in idx) { sortedKeys.Add(keys[i]); sortedTops.Add(tops[i]); }
        keys.Clear();
        keys.AddRange(sortedKeys);

        if (firstNote < 0) firstNote = keys.Count >= 88 ? 21 : 60 - keys.Count / 2;
        ClassifyBlackKeys(sortedTops);
        if (autoDetectFirstNote) DetectFirstNote();

        for (int i = 0; i < keys.Count; i++)
        {
            Key k = keys[i];
            k.note = firstNote + i;
            byTransform[k.t] = k;
            byNote[k.note] = k;

            if (k.t.GetComponentInChildren<Collider>() == null)
            {
                if (k.t.GetComponent<MeshFilter>() != null) k.t.gameObject.AddComponent<BoxCollider>();
                else foreach (var mf in k.t.GetComponentsInChildren<MeshFilter>()) mf.gameObject.AddComponent<BoxCollider>();
            }

            k.src = k.t.gameObject.AddComponent<AudioSource>();
            k.src.playOnAwake = false;
            k.src.spatialBlend = spatialBlend;
        }

        BuildComputerKeyMap();
        if (hideUnmappedKeys && computerKeyboardInput) HideUnmappedKeys();
        int blackCount = 0;
        foreach (Key k in keys) if (k.black) blackCount++;
        Debug.Log($"PianoController: {keys.Count} keys ({blackCount} black), lowest note MIDI {firstNote}.");

        if (FindAnyObjectByType<AudioListener>() == null)
            Debug.LogWarning("PianoController: no Audio Listener in the scene, so nothing will be heard. Add one to your camera.");
        if (AudioListener.volume <= 0f)
            Debug.LogWarning("PianoController: AudioListener volume is 0.");
    }

    void Update()
    {
        if (mouseInput) HandleMouse();
        if (computerKeyboardInput) HandleComputerKeyboard();
        AnimateKeys();
    }

    // ---------- Public API (VR hands, MIDI input, scripted playback) ----------

    public void PressNote(int midiNote) { if (byNote.TryGetValue(midiNote, out Key k)) Press(k); }
    public void ReleaseNote(int midiNote) { if (byNote.TryGetValue(midiNote, out Key k)) Release(k); }

    /// <summary>Fills results with the MIDI notes of all keys currently held down.</summary>
    public void GetHeldNotes(List<int> results)
    {
        results.Clear();
        foreach (Key k in keys) if (k.holdCount > 0) results.Add(k.note);
    }

    /// <summary>World position of the top of a key at rest, plus the key's width and the piano's up direction.</summary>
    public bool TryGetKeyTop(int midiNote, out Vector3 worldTop, out float worldWidth, out Vector3 worldUp)
    {
        worldTop = Vector3.zero; worldWidth = 0f; worldUp = Vector3.up;
        if (keysParent == null || !byNote.TryGetValue(midiNote, out Key k)) return false;
        worldTop = keysParent.TransformPoint(k.topLocal);
        worldWidth = keysParent.TransformVector(lowToHighAxis.normalized * k.width).magnitude;
        worldUp = keysParent.TransformDirection(upAxis).normalized;
        return true;
    }

    /// <summary>Converts one of the piano axes above into a world-space direction.</summary>
    public Vector3 WorldAxis(Vector3 localAxis)
    {
        Transform root = keysParent != null ? keysParent : transform;
        return root.TransformDirection(localAxis).normalized;
    }

    /// <summary>Fills results with the MIDI notes of all visible keys, low to high.</summary>
    public void GetPlayableNotes(List<int> results, bool whiteKeysOnly)
    {
        results.Clear();
        foreach (Key k in keys) if (!whiteKeysOnly || !k.black) results.Add(k.note);
    }

    public void SetSustain(bool on)
    {
        sustain = on;
        if (!on)
            foreach (var k in keys)
                if (k.holdCount == 0 && k.sounding) StopSound(k);
    }

    // ---------- Input ----------

    void HandleMouse()
    {
        bool held = false;
        Vector2 pos = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            held = Mouse.current.leftButton.isPressed;
            pos = Mouse.current.position.ReadValue();
        }
#else
        held = Input.GetMouseButton(0);
        pos = Input.mousePosition;
#endif
        // Dragging across keys plays each one (glissando).
        Key hovered = held ? RaycastKey(pos) : null;
        if (hovered != mouseKey)
        {
            if (mouseKey != null) Release(mouseKey);
            if (hovered != null) Press(hovered);
            mouseKey = hovered;
        }
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

        if (spaceIsSustainPedal)
        {
            bool space;
#if ENABLE_INPUT_SYSTEM
            space = Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
#else
            space = Input.GetKey(KeyCode.Space);
#endif
            if (space != sustain) SetSustain(space);
        }
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
        if (blackRow == null) blackRow = "";
        if (blackPitchClass[((note % 12) + 12) % 12])
        {
            Debug.LogWarning("PianoController: keyboardBaseNote is a black key, moving it up to the next white key.");
            note++;
        }

        for (int i = 0; i < whiteRow.Length; i++)
        {
            AddMapping(whiteRow[i], note);

            bool hasBlack = blackPitchClass[(note + 1) % 12];
            char blackChar = i < blackRow.Length ? blackRow[i] : ' ';
            if (blackChar != ' ')
            {
                if (hasBlack) AddMapping(blackChar, note + 1);
                else Debug.LogWarning($"PianoController: '{blackChar}' sits where there is no black key; it was skipped.");
            }

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
        Debug.Log($"PianoController: showing {keys.Count} playable keys, the rest are hidden.");
    }

    bool IsComputerKeyDown(char c)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null) return false;
        var control = Keyboard.current.FindKeyOnCurrentKeyboardLayout(c.ToString());
        return control != null && control.isPressed;
#else
        return Input.GetKey((KeyCode)char.ToLower(c));
#endif
    }

    Key RaycastKey(Vector2 screenPos)
    {
        Camera cam = inputCamera != null ? inputCamera : Camera.main;
        if (cam == null) return null;
        if (Physics.Raycast(cam.ScreenPointToRay(screenPos), out RaycastHit hit, Mathf.Infinity, ~0, QueryTriggerInteraction.Collide))
        {
            for (Transform t = hit.collider.transform; t != null; t = t.parent)
                if (byTransform.TryGetValue(t, out Key k)) { lastNonKeyHit = null; return k; }

            if (logPresses && hit.collider != lastNonKeyHit)
                Debug.Log($"PianoController: mouse hit '{hit.collider.name}', which isn't a key.");
            lastNonKeyHit = hit.collider;
        }
        return null;
    }

    // ---------- Press / release ----------

    void Press(Key k)
    {
        k.holdCount++;
        if (k.holdCount > 1) return;
        PlaySound(k);
        if (logPresses)
            Debug.Log($"PianoController: {k.t.name} down, MIDI {k.note} ({(k.black ? "black" : "white")}), " +
                      $"clip {(k.src.clip != null ? k.src.clip.name : "NONE")}, playing {k.src.isPlaying}");
        onNoteDown?.Invoke(k.note);
    }

    void Release(Key k)
    {
        if (k.holdCount == 0) return;
        k.holdCount--;
        if (k.holdCount > 0) return;
        if (!sustain) StopSound(k);
        onNoteUp?.Invoke(k.note);
    }

    // ---------- Animation ----------

    void AnimateKeys()
    {
        Vector3 up = upAxis.normalized;
        Vector3 axis = Vector3.Cross(towardBackAxis.normalized, up);

        foreach (Key k in keys)
        {
            float target = k.holdCount > 0 ? 1f : 0f;
            float speed = 1f / Mathf.Max(0.001f, target > k.press ? pressTime : releaseTime);
            k.press = Mathf.MoveTowards(k.press, target, speed * Time.deltaTime);

            if (k.press <= 0f && !k.applied) continue;

            k.t.localPosition = k.restPos;
            k.t.localRotation = k.restRot;

            if (k.press > 0f)
            {
                if (pressStyle == PressStyle.Rotate)
                {
                    k.t.RotateAround(keysParent.TransformPoint(k.pivotLocal),
                                     keysParent.TransformDirection(axis),
                                     pressAngle * k.press);
                }
                else
                {
                    float depth = pressDepth > 0f ? pressDepth : k.length * 0.06f;
                    k.t.position += keysParent.TransformDirection(-up * depth * k.press);
                }
            }
            k.applied = k.press > 0f;
        }
    }

    // ---------- Sound ----------

    void PlaySound(Key k)
    {
        if (k.fade != null) { StopCoroutine(k.fade); k.fade = null; }

        if (sample != null)
        {
            k.src.clip = sample;
            k.src.pitch = Mathf.Pow(2f, (k.note - sampleNote) / 12f);
        }
        else
        {
            k.src.clip = GetTone(k.note);
            k.src.pitch = 1f;
        }
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
        k.src.volume = volume;
        k.sounding = false;
        k.fade = null;
    }

    static AudioClip GetTone(int note)
    {
        // The null check matters when domain reload is off: cached clips from the last Play session are destroyed.
        if (toneCache.TryGetValue(note, out AudioClip clip) && clip != null) return clip;

        const int sr = 44100;
        const float length = 3f;
        int n = (int)(sr * length);
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
                float amp = 1f / Mathf.Pow(h, 1.4f);
                s += amp * Mathf.Exp(-t * decay * (1f + 0.35f * h)) * Mathf.Sin(2f * Mathf.PI * fh * t);
            }
            s *= Mathf.Clamp01(t / 0.004f);             // short hammer attack
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.8f / peak;

        clip = AudioClip.Create("PianoTone_" + note, n, 1, sr, false);
        clip.SetData(data, 0);
        toneCache[note] = clip;
        return clip;
    }

    // ---------- Mesh splitting (for pianos where every key is one combined mesh) ----------

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
        Vector2[] uvs = mesh.uv;
        int subCount = mesh.subMeshCount;

        // Group vertices into connected pieces. Vertices at the same position are welded
        // across all materials, since hard edges duplicate vertices and some models give
        // one key faces in more than one material.
        int[] parent = new int[verts.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        float cell = Mathf.Max(1e-7f, mesh.bounds.size.magnitude * 1e-5f);
        var firstAt = new Dictionary<Vector3Int, int>();

        for (int s = 0; s < subCount; s++)
        {
            int[] tris = mesh.GetTriangles(s);
            foreach (int v in tris)
            {
                Vector3 p = verts[v] / cell;
                var q = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (firstAt.TryGetValue(q, out int other)) Union(parent, v, other);
                else firstAt[q] = v;
            }
            for (int i = 0; i < tris.Length; i += 3)
            {
                Union(parent, tris[i], tris[i + 1]);
                Union(parent, tris[i], tris[i + 2]);
            }
        }

        // Collect each piece's triangles, per material.
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
                lists[s].Add(tris[i]); lists[s].Add(tris[i + 1]); lists[s].Add(tris[i + 2]);
            }
        }
        if (parts.Count < 2) return;

        int n = 0;
        foreach (List<int>[] lists in parts.Values)
        {
            var remap = new Dictionary<int, int>();
            var pv = new List<Vector3>();
            var pn = new List<Vector3>();
            var puv = new List<Vector2>();
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
                        if (uvs.Length == verts.Length) puv.Add(uvs[v]);
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
            if (pv.Count > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(pv);
            if (pn.Count == pv.Count) m.SetNormals(pn);
            if (puv.Count == pv.Count) m.SetUVs(0, puv);
            m.subMeshCount = usedTris.Count;
            for (int j = 0; j < usedTris.Count; j++) m.SetTriangles(usedTris[j], j);
            if (pn.Count != pv.Count) m.RecalculateNormals();
            m.RecalculateBounds();

            var go = new GameObject("Key_" + n);
            go.transform.SetParent(source.transform, false);
            go.transform.localPosition = b.center;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterials = usedMats.ToArray();
            n++;
        }

        if (sourceRenderer != null) sourceRenderer.enabled = false;
        foreach (Collider c in source.GetComponents<Collider>()) c.enabled = false;
        keysParent = source.transform;
        Debug.Log($"PianoController: split {mesh.name} into {parts.Count} keys.");
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

    void ClassifyBlackKeys(List<float> tops)
    {
        float min = float.MaxValue, max = float.MinValue, avgLen = 0f;
        foreach (float y in tops) { min = Mathf.Min(min, y); max = Mathf.Max(max, y); }
        foreach (Key k in keys) avgLen += k.length;
        avgLen /= keys.Count;
        bool heightsDiffer = (max - min) > avgLen * 0.02f;
        float mid = (min + max) * 0.5f;

        for (int i = 0; i < keys.Count; i++)
        {
            string name = keys[i].t.name.ToLower();
            if (name.Contains("black") || name.Contains("sharp") || name.Contains("#")) keys[i].black = true;
            else if (name.Contains("white")) keys[i].black = false;
            else keys[i].black = heightsDiffer && tops[i] > mid;
        }
    }

    void DetectFirstNote()
    {
        bool anyBlack = false;
        foreach (Key k in keys) anyBlack |= k.black;
        if (!anyBlack) return;

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
}