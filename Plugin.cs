using System.Globalization;
using System.Net;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace LyricsHud
{
    public enum PadButton { None, LeftX, LeftY, RightA, RightB, LeftStick, RightStick }

    class ArtResult
    {
        public string id;
        public byte[] data;
    }

    [BepInPlugin("local.lyricshud", "Lyrics HUD", "2.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        // ---------- config ----------
        ConfigEntry<int> cfgPort, cfgLyricLines;
        ConfigEntry<bool> cfgStartVisible, cfgShowArt, cfgShowAlbum, cfgShowTime, cfgShowProgress;
        ConfigEntry<PadButton> cfgToggle, cfgPlayPause, cfgNext, cfgPrevious;
        ConfigEntry<float> cfgDistance, cfgHeight, cfgOffsetX, cfgScale;
        ConfigEntry<string> cfgBackground, cfgTextColor, cfgAccent;
        int port;

        // ---------- hud objects ----------
        GameObject root;
        TextMesh titleText, artistText, albumText, timeText, prevText, curText, nextText;
        MeshRenderer artRenderer, fillRenderer;
        Material artMat, fillMat;
        Texture2D bgTex, artTex;
        Color accent, artPlaceholder;
        float barLeft, barWidth;
        Font font;
        Shader shader;
        static Mesh quadMesh;
        bool dirty;
        bool visible = true;

        // ---------- data from the helper ----------
        volatile string raw = "";
        volatile bool polling;
        volatile ArtResult artResult;
        string parsedRaw;
        string currentArtId;
        string lastTime;
        float nextPoll;

        int mode; // 0 = helper not running, 1 = nothing playing, 2 = track loaded
        string status = "", title = "", artist = "", album = "", artId = "";
        float pos, len, posTime;

        readonly bool[] held = new bool[7];
        readonly bool[] justPressed = new bool[7];

        void Awake()
        {
            cfgPort = Config.Bind("General", "HelperPort", 8765, "port lyrics_helper.py listens on");
            cfgStartVisible = Config.Bind("General", "StartVisible", true, "show the panel when the game starts");

            cfgToggle = Config.Bind("Controls", "ToggleButton", PadButton.RightA, "hide/show the panel");
            cfgPlayPause = Config.Bind("Controls", "PlayPauseButton", PadButton.LeftX, "play/pause");
            cfgNext = Config.Bind("Controls", "NextButton", PadButton.LeftY, "next track");
            cfgPrevious = Config.Bind("Controls", "PreviousButton", PadButton.RightB, "previous track");

            cfgDistance = Config.Bind("HUD", "Distance", 0.7f,
                new ConfigDescription("how far in front of your eyes (meters)", new AcceptableValueRange<float>(0.3f, 2f)));
            cfgHeight = Config.Bind("HUD", "HeightBelowEyes", 0.12f,
                new ConfigDescription("how far the top edge of the panel is below eye level (meters)", new AcceptableValueRange<float>(-0.3f, 0.6f)));
            cfgOffsetX = Config.Bind("HUD", "OffsetX", 0f,
                new ConfigDescription("move the panel left (negative) or right (positive) (meters)", new AcceptableValueRange<float>(-0.6f, 0.6f)));
            cfgScale = Config.Bind("HUD", "Scale", 1f,
                new ConfigDescription("size multiplier for the whole panel", new AcceptableValueRange<float>(0.3f, 3f)));

            cfgBackground = Config.Bind("Look", "BackgroundColor", "#14141CDC",
                "panel color as #RRGGBB or #RRGGBBAA (the last two digits are the opacity)");
            cfgTextColor = Config.Bind("Look", "TextColor", "#FFFFFF", "main text color");
            cfgAccent = Config.Bind("Look", "AccentColor", "#B388FF", "artist name and progress bar color");

            cfgShowArt = Config.Bind("Display", "ShowAlbumArt", true, "show the track picture");
            cfgShowAlbum = Config.Bind("Display", "ShowAlbumName", true, "show the album name");
            cfgShowTime = Config.Bind("Display", "ShowTime", true, "show elapsed / total time");
            cfgShowProgress = Config.Bind("Display", "ShowProgressBar", true, "show the progress bar");
            cfgLyricLines = Config.Bind("Display", "LyricLines", 3,
                new ConfigDescription("0 hides lyrics, 1 = current line only, 2 = adds the next line, 3 = adds the previous line too",
                    new AcceptableValueRange<int>(0, 3)));

            port = cfgPort.Value;
            visible = cfgStartVisible.Value;
            Config.SettingChanged += (s, e) =>
            {
                port = cfgPort.Value;
                dirty = true;
            };
            Logger.LogInfo("Lyrics HUD loaded");
        }

        void Update()
        {
            if (dirty)
            {
                dirty = false;
                DestroyHud();
            }

            ReadButtons();
            if (Pressed(cfgToggle.Value)) visible = !visible;
            if (Pressed(cfgPlayPause.Value)) Send("playpause");
            if (Pressed(cfgNext.Value)) Send("next");
            if (Pressed(cfgPrevious.Value)) Send("previous");

            EnsureHud();
            if (root == null) return;

            root.SetActive(visible);
            if (!visible) return;

            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 0.2f;
                Poll();
            }

            string current = raw;
            if (current != parsedRaw) ApplyState(current);

            ApplyArtResult();
            UpdateProgress();
        }

        // ---------- input ----------
        void ReadButtons()
        {
            for (int i = 1; i < held.Length; i++)
            {
                bool now = IsDown((PadButton)i);
                justPressed[i] = now && !held[i];
                held[i] = now;
            }
        }

        bool Pressed(PadButton b)
        {
            return b != PadButton.None && justPressed[(int)b];
        }

        static bool IsDown(PadButton b)
        {
            XRNode node;
            InputFeatureUsage<bool> usage;
            switch (b)
            {
                case PadButton.LeftX: node = XRNode.LeftHand; usage = CommonUsages.primaryButton; break;
                case PadButton.LeftY: node = XRNode.LeftHand; usage = CommonUsages.secondaryButton; break;
                case PadButton.RightA: node = XRNode.RightHand; usage = CommonUsages.primaryButton; break;
                case PadButton.RightB: node = XRNode.RightHand; usage = CommonUsages.secondaryButton; break;
                case PadButton.LeftStick: node = XRNode.LeftHand; usage = CommonUsages.primary2DAxisClick; break;
                case PadButton.RightStick: node = XRNode.RightHand; usage = CommonUsages.primary2DAxisClick; break;
                default: return false;
            }
            bool value;
            InputDevices.GetDeviceAtXRNode(node).TryGetFeatureValue(usage, out value);
            return value;
        }

        // ---------- talking to the helper ----------
        void Poll()
        {
            if (polling) return;
            polling = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { raw = Get("/state"); }
                catch { raw = ""; }
                finally { polling = false; }
            });
        }

        void Send(string cmd)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Get("/cmd/" + cmd); }
                catch { }
            });
        }

        void RequestArt(string id)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] data = null;
                try { data = GetBytes("/art"); }
                catch { }
                artResult = new ArtResult { id = id, data = data };
            });
        }

        string Get(string path)
        {
            using (var wc = new WebClient())
            {
                wc.Proxy = null;
                wc.Encoding = System.Text.Encoding.UTF8;
                return wc.DownloadString("http://127.0.0.1:" + port + path);
            }
        }

        byte[] GetBytes(string path)
        {
            using (var wc = new WebClient())
            {
                wc.Proxy = null;
                return wc.DownloadData("http://127.0.0.1:" + port + path);
            }
        }

        // ---------- applying data ----------
        void ApplyState(string s)
        {
            parsedRaw = s;
            string[] p = s.Split('\n');

            if (string.IsNullOrEmpty(s)) mode = 0;
            else if (p.Length < 10 || p[0].Trim() == "None") mode = 1;
            else mode = 2;

            string prev = "", cur = "", next = "";
            if (mode == 2)
            {
                status = p[0].Trim();
                title = p[1];
                artist = p[2];
                album = p[3];
                pos = ParseFloat(p[4]);
                len = ParseFloat(p[5]);
                artId = p[6].Trim();
                prev = p[7];
                cur = p[8];
                next = p[9];
                posTime = Time.unscaledTime;
            }
            else
            {
                status = "None";
                title = mode == 0 ? "lyrics helper not running" : "nothing playing";
                artist = "";
                album = "";
                artId = "";
                pos = 0f;
                len = 0f;
            }

            Set(titleText, title);
            Set(artistText, artist);
            Set(albumText, album);
            Set(prevText, prev);
            Set(curText, cur);
            Set(nextText, next);

            if (fillMat != null)
                fillMat.color = status == "Playing" ? accent : new Color(0.5f, 0.5f, 0.55f, 1f);

            if (artId != currentArtId)
            {
                currentArtId = artId;
                if (artRenderer != null)
                {
                    if (artId.Length == 0) SetArt(null);
                    else RequestArt(artId);
                }
            }
        }

        void ApplyArtResult()
        {
            ArtResult r = artResult;
            if (r == null) return;
            artResult = null;
            if (artRenderer == null || r.id != currentArtId) return;

            if (r.data == null)
            {
                SetArt(null);
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!LoadImage(tex, r.data))
            {
                Destroy(tex);
                SetArt(null);
                return;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            SetArt(tex);
        }

        // ImageConversion.LoadImage has a ReadOnlySpan overload the compiler can't resolve
        // against net472, so it is looked up and called by reflection instead
        static readonly System.Reflection.MethodInfo loadImageMethod =
            typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

        static bool LoadImage(Texture2D tex, byte[] data)
        {
            if (loadImageMethod == null) return false;
            try { return (bool)loadImageMethod.Invoke(null, new object[] { tex, data }); }
            catch { return false; }
        }

        void SetArt(Texture2D tex)
        {
            if (artTex != null) Destroy(artTex);
            artTex = tex;
            if (artMat == null) return;
            artMat.mainTexture = tex;
            artMat.color = tex != null ? Color.white : artPlaceholder;
        }

        void UpdateProgress()
        {
            float p = pos;
            if (mode == 2 && status == "Playing") p += Time.unscaledTime - posTime;
            if (len > 0f && p > len) p = len;

            if (fillRenderer != null)
            {
                float frac = len > 0f ? Mathf.Clamp01(p / len) : 0f;
                float w = Mathf.Max(0.0002f, barWidth * frac);
                Transform tf = fillRenderer.transform;
                tf.localScale = new Vector3(w, 0.004f, 1f);
                tf.localPosition = new Vector3(barLeft + w / 2f, tf.localPosition.y, tf.localPosition.z);
            }

            if (timeText != null)
            {
                string label = mode == 2 ? Fmt(p) + " / " + Fmt(len) : "";
                if (label != lastTime)
                {
                    lastTime = label;
                    timeText.text = label;
                }
            }
        }

        static string Fmt(float seconds)
        {
            int t = Mathf.Max(0, (int)seconds);
            return (t / 60) + ":" + (t % 60).ToString("00");
        }

        static void Set(TextMesh tm, string s)
        {
            if (tm != null && tm.text != s) tm.text = s;
        }

        static float ParseFloat(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        static Color ParseColor(string hex, Color fallback)
        {
            Color c;
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out c)) return c;
            return fallback;
        }

        // ---------- building the panel ----------
        void EnsureHud()
        {
            if (root != null) return;

            Camera cam = FindHeadCamera();
            if (cam == null) return;

            DestroyHud();
            BuildHud(cam);
            parsedRaw = null;
            currentArtId = null;
        }

        void DestroyHud()
        {
            if (root != null) Destroy(root);
            if (bgTex != null) Destroy(bgTex);
            if (artTex != null) Destroy(artTex);
            root = null;
            bgTex = null;
            artTex = null;
            titleText = artistText = albumText = timeText = prevText = curText = nextText = null;
            artRenderer = fillRenderer = null;
            artMat = fillMat = null;
        }

        void BuildHud(Camera cam)
        {
            if (font == null) font = LoadFont();
            if (shader == null) shader = FindShader();

            Color bg = ParseColor(cfgBackground.Value, new Color(0.08f, 0.08f, 0.11f, 0.86f));
            Color fg = ParseColor(cfgTextColor.Value, Color.white);
            accent = ParseColor(cfgAccent.Value, new Color(0.7f, 0.55f, 1f, 1f));
            artPlaceholder = new Color(accent.r * 0.4f, accent.g * 0.4f, accent.b * 0.4f, 1f);
            Color dim = new Color(fg.r, fg.g, fg.b, 0.5f);

            bool showArt = cfgShowArt.Value;
            bool showProgress = cfgShowProgress.Value;
            int lines = Mathf.Clamp(cfgLyricLines.Value, 0, 3);

            // panel layout in meters, origin = top center, y goes down
            const float W = 0.38f, Pad = 0.01f, ArtSize = 0.05f, HeaderH = 0.07f, LineStep = 0.017f, Z = -0.0008f;
            float lyricsH = lines > 0 ? 0.012f + lines * LineStep : 0f;
            float H = HeaderH + lyricsH + (showProgress ? 0.018f : 0.006f);

            root = new GameObject("LyricsHudPanel");
            root.transform.SetParent(cam.transform, false);
            root.transform.localPosition = new Vector3(cfgOffsetX.Value, -cfgHeight.Value, cfgDistance.Value);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one * cfgScale.Value;
            Transform t = root.transform;

            // background with rounded corners
            bgTex = RoundedTexture(W, H, 0.012f);
            MakeQuad(t, new Vector3(0f, -H / 2f, 0f), new Vector2(W, H), bg, bgTex, 0);

            // header: album art + title / artist / album / time
            float textX = -W / 2f + Pad;
            if (showArt)
            {
                artRenderer = MakeQuad(t, new Vector3(-W / 2f + Pad + ArtSize / 2f, -Pad - ArtSize / 2f, Z),
                    new Vector2(ArtSize, ArtSize), artPlaceholder, null, 1);
                artMat = artRenderer.sharedMaterial;
                textX = -W / 2f + 2f * Pad + ArtSize;
            }

            titleText = MakeText(t, new Vector3(textX, -Pad - 0.001f, Z), 0.016f, TextAnchor.UpperLeft, fg, FontStyle.Bold, 10);
            artistText = MakeText(t, new Vector3(textX, -Pad - 0.021f, Z), 0.013f, TextAnchor.UpperLeft, accent, FontStyle.Normal, 11);
            if (cfgShowAlbum.Value)
                albumText = MakeText(t, new Vector3(textX, -Pad - 0.038f, Z), 0.011f, TextAnchor.UpperLeft, dim, FontStyle.Normal, 12);
            if (cfgShowTime.Value)
                timeText = MakeText(t, new Vector3(W / 2f - Pad, -Pad - 0.038f, Z), 0.011f, TextAnchor.UpperRight, dim, FontStyle.Normal, 12);

            // lyrics
            if (lines > 0)
            {
                float y = -HeaderH - 0.006f;
                if (lines == 3)
                {
                    prevText = MakeText(t, new Vector3(0f, y, Z), 0.012f, TextAnchor.UpperCenter, dim, FontStyle.Normal, 13);
                    y -= LineStep;
                }
                curText = MakeText(t, new Vector3(0f, y, Z), 0.014f, TextAnchor.UpperCenter, fg, FontStyle.Bold, 14);
                y -= LineStep;
                if (lines >= 2)
                    nextText = MakeText(t, new Vector3(0f, y, Z), 0.012f, TextAnchor.UpperCenter, dim, FontStyle.Normal, 15);
            }

            // progress bar
            if (showProgress)
            {
                barWidth = W - 2f * Pad;
                barLeft = -W / 2f + Pad;
                float by = -(H - 0.011f);
                MakeQuad(t, new Vector3(0f, by, Z), new Vector2(barWidth, 0.004f),
                    new Color(fg.r, fg.g, fg.b, 0.18f), null, 2);
                fillRenderer = MakeQuad(t, new Vector3(barLeft, by, Z), new Vector2(0.0002f, 0.004f), accent, null, 3);
                fillMat = fillRenderer.sharedMaterial;
            }

            lastTime = null;
        }

        MeshRenderer MakeQuad(Transform parent, Vector3 pos, Vector2 size, Color color, Texture tex, int order)
        {
            var go = new GameObject("Quad");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = QuadMesh();

            var mat = new Material(shader);
            if (mat.HasProperty("unity_GUIZTestMode"))
                mat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            mat.color = color;
            mat.mainTexture = tex;
            mat.renderQueue = 4000 + order;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return mr;
        }

        TextMesh MakeText(Transform parent, Vector3 pos, float lineHeight, TextAnchor anchor, Color color, FontStyle style, int order)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;

            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = lineHeight / 6.4f; // line height is roughly characterSize * fontSize * 0.1
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.UpperCenter ? TextAlignment.Center
                         : anchor == TextAnchor.UpperRight ? TextAlignment.Right
                         : TextAlignment.Left;
            tm.fontStyle = style;
            tm.richText = false;
            tm.color = color;

            var mat = new Material(font.material);
            mat.renderQueue = 4000 + order;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return tm;
        }

        static Mesh QuadMesh()
        {
            if (quadMesh != null) return quadMesh;
            quadMesh = new Mesh();
            quadMesh.hideFlags = HideFlags.HideAndDontSave;
            quadMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            quadMesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            var white = new Color32(255, 255, 255, 255);
            quadMesh.colors32 = new[] { white, white, white, white };
            quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            quadMesh.RecalculateBounds();
            return quadMesh;
        }

        // white rounded rectangle with soft edges, tinted by the material color
        static Texture2D RoundedTexture(float w, float h, float radiusMeters)
        {
            int tw = 512;
            int th = Mathf.Max(8, Mathf.RoundToInt(tw * h / w));
            float r = radiusMeters / w * tw;
            var px = new Color32[tw * th];
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - tw / 2f) - (tw / 2f - r), 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - th / 2f) - (th / 2f - r), 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - r;
                    byte a = (byte)(Mathf.Clamp01(0.5f - d) * 255f);
                    px[y * tw + x] = new Color32(255, 255, 255, a);
                }
            }
            var tex = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        // ---------- unity lookups ----------
        // the vr camera, not the spectator/shoulder one
        static Camera FindHeadCamera()
        {
            foreach (Camera c in Camera.allCameras)
            {
                if (c.enabled && c.stereoTargetEye == StereoTargetEyeMask.Both && c.targetTexture == null)
                    return c;
            }
            return Camera.main;
        }

        Shader FindShader()
        {
            string[] names = { "UI/Default", "Sprites/Default", "Unlit/Transparent", "GUI/Text Shader" };
            foreach (string n in names)
            {
                Shader s = Shader.Find(n);
                if (s != null) return s;
            }
            return font.material.shader;
        }

        static Font LoadFont()
        {
            try { Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); if (f != null) return f; } catch { }
            try { Font f = Resources.GetBuiltinResource<Font>("Arial.ttf"); if (f != null) return f; } catch { }
            return Font.CreateDynamicFontFromOSFont("Arial", 64);
        }
    }
}
